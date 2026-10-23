using UnityEngine;

/// <summary>
/// 披风布料（Verlet/PBD）：粒子 + 距离约束（横向/纵向结构 + 对角剪切）。
/// 顶行不是水平直线，而是"肩颈弧线"：两端盖住肩尖（upperarm 外侧）、中段上拱包住后颈（neck_01 上方），
/// 钉在 anchorBone（spine_02）本地坐标上跟随躯干动画（两端角点可改钉左右肩骨 upperarm：抬臂/耸肩时角点跟肩走）；
/// 行宽从肩到下摆线性放宽，静置就是梯形披风轮廓。
/// 每帧：Verlet 积分（重力 + 风） → 约束迭代（PBD，与身体碰撞推离交替） → 钉锚点 → 写回网格。
/// 风场 = 环境微风（双正弦） + 锚点运动相对风：跑动后飘、原地转身甩摆、下落上飘都由锚点速度驱动
/// （比只看刚体速度多了转向与动画起伏，且包含竖直分量）。
/// </summary>
[DefaultExecutionOrder(50)]    // 晚于 HitReaction.LateUpdate（默认 0）：受击摆动叠加完骨骼姿态，披风锚定才读到含摆动的最终姿态；早于 TornedCapeDriver(100)
public class CapeCloth : MonoBehaviour
{
    [Header("网格尺寸")]
    public int cols = 7;            // 列（宽向粒子数）
    public int rows = 10;           // 行（长向粒子数）
    public float length = 0.7f;     // 垂落长度（m）

    
    
    
    [Header("披风轮廓（相对锚骨 spine_02 的本地坐标，按本骨架实测标定）")]
    [Tooltip("肩线全宽（m）：顶行两端落在肩尖（upperarm x=±0.192）略外侧")]
    public float topWidth = 0.42f;
    [Tooltip("下摆全宽（m）：明显大于肩宽才有披风感；行宽从上到下线性渐宽，静置即梯形轮廓。\n风压会让下摆后摆收窄，实际观感约为本值的 7~8 成")]
    public float bottomWidth = 0.85f;
    [Tooltip("肩线高度（m）：肩尖相对 spine_02 的高度（实测 upperarm y=0.269）")]
    public float shoulderHeight = 0.27f;
    [Tooltip("后颈上拱（m）：顶行中段比肩线抬高多少，盖住后颈（neck_01 y=0.314 → 中段=肩线+本值）")]
    public float neckArch = 0.065f;
    [Tooltip("肩尖贴背偏移（m）：spine_02 本地 +Z 朝模型胸前，负值朝背后")]
    public float shoulderBack = -0.11f;
    [Tooltip("后颈贴背偏移（m）：顶行中段更贴近脊柱，包住后颈")]
    public float neckBack = -0.055f;
    [Tooltip("防缠绕：粒子不许越过身体冠状面跑到胸前（推离碰撞是单向的，布料可能顺体侧绕到前面卡住）")]
    public bool keepBehindBack = true;
    [Tooltip("前方界限（沿 spine.forward 的有符号距离）：粒子超过它就拉回，0=不许越过脊柱冠状面")]
    public float frontLimit = 0f;

    [Header("引用（留空自动查找 spine_02）")]
    public Transform anchorBone;

    [Header("肩部锚定")]
    [Tooltip("顶行两个角点改钉在左右肩骨（upperarm_l/r）上：抬臂/耸肩时角点跟肩走，不再悬空。\n中间肩颈弧线仍钉 spine_02；按世界距离就近绑定肩骨（不猜左右手坐标约定），找不到肩骨时自动回退为全钉 spine_02。\n绑定时按当前轮廓标定位置反算肩骨本地偏移，轮廓零变化只换挂点")]
    public bool cornersFollowShoulders = true;

    [Header("物理（Verlet/PBD）")]
    public float gravity = -9.8f;           // 重力加速度
    [Range(0.8f, 1f)] public float damping = 0.985f;    // 速度保留率（1=无阻尼）
    public int constraintIterations = 8;    // 距离约束求解迭代次数：越多布越硬挺、越不容易折叠塌陷

    [Header("风场")]
    public float windStrength = 1.5f;       // 环境微风强度（m/s²）
    public float windFrequency = 1.6f;      // 摆动频率
    [Tooltip("锚点运动 → 相对风系数：跑动后飘、原地转身甩摆、下落上飘全由它决定。\n锚点速度已含移动/转向/动画起伏，比只看刚体速度更真实")]
    public float moveWindScale = 0.9f;
    [Tooltip("锚点速度上限（m/s）：动画突变帧（受击/起身）不至于把披风瞬间抽飞")]
    public float maxAnchorWind = 12f;

    [Header("身体碰撞推离（防穿模：披风穿过躯干/大腿）")]
    [Tooltip("关闭后回到纯布料（会穿模）")]
    public bool bodyCollision = true;
    [Tooltip("胸腔/骨盆胶囊段半径（m）：胶囊沿相邻骨骼布置、覆盖躯干实际轮廓，比单球更贴背，转身时侧缘不容易从球缝漏进身体")]
    public float torsoRadius = 0.12f;   // 胸腔胶囊 spine_02 → neck_01：半径贴着躯干半厚，太大披风会飘离后背
    public float hipRadius = 0.13f;     // 骨盆胶囊 spine_01 → pelvis
    [Tooltip("头部球半径（m）：低头/受击时防穿")]
    public float headRadius = 0.11f;    // Head
    [Tooltip("大腿球半径（m）：跑动时防披风穿腿")]
    public float thighRadius = 0.08f;   // thigh_l / thigh_r
    [Tooltip("肩部球半径（m）：披风两侧从肩尖外侧垂下，转身甩摆时防侧缘穿肩")]
    public float shoulderRadius = 0.07f; // upperarm_l / upperarm_r
    [Tooltip("碰撞安全间距（m）：粒子被推到碰撞面外后多留这点距离 —— 粒子不穿不代表三角面不穿（面横跨两粒子），留出皮肤间距防可见网格贴面穿透")]
    public float collisionSkin = 0.015f;

    [Header("外观（renderer 无材质时自动按此建运行时材质）")]
    [Tooltip("是否生成并渲染自带的程序化梯形网格。\n作为隐形物理骨架（撕裂披风等蒙皮代理的粒子宿主）时应关闭：\n关闭后只跑粒子模拟，不建网格、不接管 MeshFilter/MeshRenderer，避免覆盖同物体上的美术网格")]
    public bool renderGridMesh = true;
    public Color clothColor = new Color(0.45f, 0.12f, 0.10f);
    public float smoothness = 0.35f;

    Transform spine;
    Vector3[] pos;              // 粒子位置（世界空间）
    Vector3[] prevPos;          // 上一帧位置（Verlet 积分）
    Vector3[] restLocals;       // 粒子静止位置（spine 本地）：外部代理网格用它算纯形变位移
    Vector3[] anchorLocals;     // 顶行粒子相对锚骨的本地偏移
    Constraint[] cons;          // 距离约束（结构 + 对角剪切）
    struct Constraint { public int a, b; public float rest; }
    struct ColSeg { public Transform a, b; public float r; }    // 碰撞段：胶囊 a→b（b=null 退化为单球）
    Mesh mesh;
    int halfCount;              // cols*rows
    int vertCount;              // 双面顶点 = half*2
    Vector3[] meshVerts;        // 复用的顶点缓冲：每帧写网格零 GC
    float time;                 // 风场累计时间
    Vector3 lastAnchorPos;      // 上一帧锚点世界位置：算锚点速度（相对风的来源）
    bool anchorInit;
    Transform[] colA;           // 身体碰撞段：起点骨骼（球=唯一骨骼）
    Transform[] colB;           // 碰撞段终点骨骼；null = 单球
    
    float[] colRadii;           // 与 colA 一一对应的半径
    Transform[] cornerBones;    // 顶行角点锚骨（upperarm_l/r，index 0=首列 1=末列；null 项回退 spine）
    Vector3[] cornerLocals;     // 角点相对各自肩骨的本地偏移（绑定时按轮廓标定位置反算，保形）
    bool cornerAnchorsActive;   // 最近一次绑定时 cornersFollowShoulders 的取值
    bool cornerAnchorsDirty;    // Play 中切换开关后置位，LateUpdate 懒重绑

    void Awake() { BuildNow(); }

    /// 粒子当前世界位置（rows×cols 个；供 Torned 等外部代理网格驱动读取）
    public Vector3[] ParticlePositions { get { return pos; } }
    /// 粒子静止位置（spine 锚骨本地空间；代理用它算"纯形变位移"）
    public Vector3[] ParticleRestLocals { get { return restLocals; } }
    /// 锚骨（spine_02）
    public Transform AnchorBone { get { return spine; } }

    /// 初始化粒子/约束/网格/碰撞球（Awake 自动调用；编辑模式下也可手动调用预览）
    public void BuildNow()
    {
        spine = anchorBone != null ? anchorBone : FindDeep(transform.root != null ? transform.root : transform, "spine_02");
        if (spine == null) { Debug.LogError("[CapeCloth] 找不到锚骨 spine_02"); return; }
        InitParticles();
        BuildConstraints();
        BuildColliders();
        BuildMesh();
        lastAnchorPos = spine.position;
        anchorInit = false;     // 首帧不算相对风，避免初始化瞬间的位置差把披风抽一下
    }

    /// 风场：环境微风（沿模型背后吹 + 侧向摆动） + 锚点运动相对风
    Vector3 ComputeWind(Vector3 anchorVel)
    {
        Vector3 back = spine != null ? -spine.forward : Vector3.back;
        back.y = 0f;
        if (back.sqrMagnitude < 0.0001f) back = Vector3.back;
        back.Normalize();
        float sway1 = Mathf.Sin(time * windFrequency) * 0.6f;
        float sway2 = Mathf.Sin(time * windFrequency * 2.7f + 1.3f) * 0.4f;
        Vector3 env = (back * (0.6f + sway1) + (spine != null ? spine.right : Vector3.right) * sway2) * windStrength;
        // 相对风：躯干怎么动就往反方向吹 —— 跑动后飘、转身甩摆、下落上飘都从这来（含竖直分量）
        return env - anchorVel * moveWindScale;
    }

    /// 身体碰撞体：胸腔/骨盆用沿相邻骨骼布置的胶囊段覆盖背部实际轮廓（单球转身时容易从球缝漏进身体），
    /// 头/大腿/肩用球；挂在骨骼上跟着动画走（缺主骨跳过该项，胶囊缺终点骨退化为单球）
    void BuildColliders()
    {
        var segs = new System.Collections.Generic.List<ColSeg>();
        AddSeg(segs, "spine_02", "neck_01", torsoRadius);       // 胸腔胶囊：沿脊柱覆盖上背到后颈下方
        AddSeg(segs, "spine_01", "pelvis", hipRadius);          // 骨盆胶囊：覆盖腰臀（缺 spine_01 退化为 pelvis 单球）
        AddSeg(segs, "Head", null, headRadius);
        AddSeg(segs, "thigh_l", null, thighRadius);
        AddSeg(segs, "thigh_r", null, thighRadius);
        AddSeg(segs, "upperarm_l", null, shoulderRadius);       // 肩部补球：防转身甩摆时披风侧缘穿肩
        AddSeg(segs, "upperarm_r", null, shoulderRadius);

        colA = new Transform[segs.Count];
        colB = new Transform[segs.Count];
        colRadii = new float[segs.Count];
        for (int i = 0; i < segs.Count; i++) { colA[i] = segs[i].a; colB[i] = segs[i].b; colRadii[i] = segs[i].r; }
    }

    /// 按骨骼名添加碰撞段；找不到主骨则跳过（同老逻辑"缺哪根骨就跳过哪个球"）
    void AddSeg(System.Collections.Generic.List<ColSeg> segs, string boneA, string boneB, float r)
    {
        var root = transform.root != null ? transform.root : transform;
        Transform a = FindDeep(root, boneA);
        if (a == null) return;
        segs.Add(new ColSeg { a = a, b = boneB != null ? FindDeep(root, boneB) : null, r = r });
    }

    /// 把非锚点粒子推出身体碰撞段（迭代内调用，与约束求解交替收敛，最后一轮保证不穿身体）。
    /// 推离是纯位置投影：必须同步修正 prevPos，否则这份位移会被 Verlet 当成速度，下一帧又把披风甩回身体里
    void PushOutOfBody()
    {
        if (!bodyCollision || colA == null) return;
        for (int s = 0; s < colA.Length; s++)
        {
            if (colA[s] == null) continue;
            Vector3 a = colA[s].position;
            Vector3 ab = colB[s] != null ? colB[s].position - a : Vector3.zero;   // 胶囊轴；单球时 ab=0
            float abSq = ab.sqrMagnitude;
            float r = colRadii[s] + collisionSkin;      // 安全间距：粒子不穿 ≠ 三角面不穿
            float rSq = r * r;
            for (int i = cols; i < halfCount; i++)      // 顶行是锚点，不参与
            {
                Vector3 d = pos[i] - a;
                if (abSq > 1e-8f)                       // 点到胶囊轴线的最近点（钳在段内）
                {
                    float t = Mathf.Clamp(Vector3.Dot(d, ab) / abSq, 0f, 1f);
                    d -= ab * t;
                }
                float dsq = d.sqrMagnitude;
                if (dsq < rSq && dsq > 1e-8f)
                {
                    float len = Mathf.Sqrt(dsq);
                    Vector3 corr = d * ((r - len) / len);
                    pos[i] += corr;
                    prevPos[i] += corr;
                }
            }
        }
    }

    /// 防缠绕：把越过身体前方冠状面的粒子拉回（披风只允许待在背后，绕到胸前就永远卡在前面了）
    void ClampBehindBack()
    {
        if (!keepBehindBack || spine == null) return;
        Vector3 fwd = spine.forward;
        Vector3 origin = spine.position;
        for (int i = cols; i < halfCount; i++)      // 顶行是锚点，不参与
        {
            float along = Vector3.Dot(pos[i] - origin, fwd);
            if (along > frontLimit)
            {
                Vector3 corr = fwd * (along - frontLimit);
                pos[i] -= corr;         // 同步 prevPos：拉回是位置修正，不能被 Verlet 当成速度
                prevPos[i] -= corr;
            }
        }
    }

    /// 粒子初始位置：顶行是肩颈弧线，往下逐行放宽（梯形）——约束静止长度直接取自这份轮廓
    void InitParticles()
    {
        int n = cols * rows;
        halfCount = n;      // 隐形骨架模式（renderGridMesh=false）不跑 BuildMesh，这里也必须赋值，否则碰撞推离循环不生效
        pos = new Vector3[n];
        prevPos = new Vector3[n];
        restLocals = new Vector3[n];
        anchorLocals = new Vector3[cols];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                Vector3 local = RowLocal(r, c);
                if (r == 0) anchorLocals[c] = local;
                restLocals[r * cols + c] = local;
                pos[r * cols + c] = spine.TransformPoint(local);
            }
        System.Array.Copy(pos, prevPos, n);
        SetupCornerAnchors();
    }

    /// 第 r 行第 c 列粒子的锚骨本地坐标：
    /// 横向按本行宽度展开（行宽从肩到下摆线性放宽）、高度按行下垂、
    /// 顶行（r=0）中段上拱 neckArch 盖住后颈、贴背深度从肩尖到后颈渐变
    Vector3 RowLocal(int r, int c)
    {
        float tc = (float)c / (cols - 1);
        float tr = (float)r / (rows - 1);
        float arch = Mathf.Sin(tc * Mathf.PI);                          // 0=肩尖 1=后颈
        float rw = Mathf.Lerp(topWidth, bottomWidth, tr);
        return new Vector3(
            Mathf.Lerp(-rw / 2f, rw / 2f, tc),
            shoulderHeight + neckArch * arch - length * tr,
            Mathf.Lerp(shoulderBack, neckBack, arch));
    }

    /// 角点肩部锚定：绑定时把角点世界位（来自 spine 轮廓标定）反算成 upperarm 本地偏移——
    /// 轮廓零变化，只换挂点；左右肩按"谁近用谁"自动匹配（不猜左右手坐标约定），找不到肩骨就回退 spine（老行为）。
    /// 必须在 InitParticles 填好 pos 之后调用（角点初始世界位取自轮廓标定结果）
    void SetupCornerAnchors()
    {
        cornerBones = null;
        cornerLocals = null;
        cornerAnchorsActive = cornersFollowShoulders;
        cornerAnchorsDirty = false;
        if (!cornersFollowShoulders || cols < 2) return;
        var root = transform.root != null ? transform.root : transform;
        Transform l = FindDeep(root, "upperarm_l");
        Transform r = FindDeep(root, "upperarm_r");
        if (l == null && r == null) return;

        Vector3 w0 = pos[0], w1 = pos[cols - 1];
        cornerBones = new Transform[2];
        cornerLocals = new Vector3[2];
        cornerBones[0] = PickNearest(l, r, w0);
        cornerBones[1] = PickNearest(l, r, w1);
        if (cornerBones[0] != null) cornerLocals[0] = cornerBones[0].InverseTransformPoint(w0);
        if (cornerBones[1] != null) cornerLocals[1] = cornerBones[1].InverseTransformPoint(w1);
    }

    /// 就近挑肩骨：两侧都有取距 p 近者；某侧缺失用另一侧
    static Transform PickNearest(Transform a, Transform b, Vector3 p)
    {
        if (a == null) return b;
        if (b == null) return a;
        return (a.position - p).sqrMagnitude <= (b.position - p).sqrMagnitude ? a : b;
    }

    /// 距离约束：横向 + 纵向（结构） + 每格两条对角线（剪切） + 隔粒长程（抗弯）——
    /// 布不再能被剪成菱形、对折成纸片，也不容易被风压卷成杯状
    void BuildConstraints()
    {
        var list = new System.Collections.Generic.List<Constraint>();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int i = r * cols + c;
                if (c + 1 < cols) list.Add(MakeCon(i, i + 1));          // 横向
                if (r + 1 < rows) list.Add(MakeCon(i, i + cols));       // 纵向
                if (c + 1 < cols && r + 1 < rows)
                {
                    list.Add(MakeCon(i, i + cols + 1));                 // 对角 \（抗剪切）
                    list.Add(MakeCon(i + 1, i + cols));                 // 对角 /（抗剪切）
                }
                if (c + 2 < cols) list.Add(MakeCon(i, i + 2));          // 隔粒横向（抗弯）
                if (r + 2 < rows) list.Add(MakeCon(i, i + 2 * cols));   // 隔粒纵向（抗弯）
            }
        cons = list.ToArray();
    }

    Constraint MakeCon(int a, int b)
    {
        return new Constraint { a = a, b = b, rest = (pos[b] - pos[a]).magnitude };
    }

    /// 动态网格：顶点 = 粒子位置（双面顶点：每粒子正/反两个顶点，反面绕序反转）
    void BuildMesh()
    {
        if (!renderGridMesh) return;    // 隐形骨架模式：只跑物理，不建网格、不碰 MeshFilter/MeshRenderer
        halfCount = cols * rows;
        vertCount = halfCount * 2;
        meshVerts = new Vector3[vertCount];
        mesh = new Mesh { name = "Cape" };
        var uvs = new Vector2[vertCount];
        var w2l = transform.worldToLocalMatrix;
        for (int i = 0; i < halfCount; i++)
        {
            int c = i % cols, r = i / cols;
            var uv = new Vector2((float)c / (cols - 1), 1f - (float)r / (rows - 1));
            meshVerts[i] = w2l.MultiplyPoint3x4(pos[i]);    // 粒子是世界坐标，顶点必须转 mesh 本地坐标（否则双重变换，披风飘离主角）
            meshVerts[halfCount + i] = meshVerts[i];
            uvs[i] = uv;
            uvs[halfCount + i] = uv;
        }
        var tris = new System.Collections.Generic.List<int>();
        for (int r = 0; r < rows - 1; r++)
            for (int c = 0; c < cols - 1; c++)
            {
                int i = r * cols + c;
                int a = i, b = i + 1, d = i + cols, e = i + cols + 1;
                tris.Add(a); tris.Add(b); tris.Add(e);              // 正面
                tris.Add(a); tris.Add(e); tris.Add(d);
                tris.Add(halfCount + b); tris.Add(halfCount + a); tris.Add(halfCount + e);   // 反面（绕序反转）
                tris.Add(halfCount + a); tris.Add(halfCount + d); tris.Add(halfCount + e);
            }
        mesh.SetVertices(meshVerts);
        mesh.SetUVs(0, new System.Collections.Generic.List<Vector2>(uvs));
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.MarkDynamic();

        var mf = gameObject.GetComponent<MeshFilter>();
        if (mf == null) mf = gameObject.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        var mr = gameObject.GetComponent<MeshRenderer>();
        if (mr == null) mr = gameObject.AddComponent<MeshRenderer>();
        if (mr.sharedMaterial == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var mat = new Material(shader);
            int colorId = mat.HasProperty("_BaseColor") ? Shader.PropertyToID("_BaseColor") : Shader.PropertyToID("_Color");
            mat.SetColor(colorId, clothColor);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            mr.material = mat;
        }
    }

    void LateUpdate()
    {
        // 自愈：Play 中途重编译（域重载）会把非序列化字段清空且不重跑 Awake，这里懒重建
        //（隐形骨架模式不建 mesh，自愈只查物理数组；普通模式才查 mesh）
        if (pos == null || cons == null || (renderGridMesh && mesh == null))
        {
            BuildNow();
            if (pos == null || cons == null || (renderGridMesh && mesh == null)) return;
        }

        float dt = Mathf.Min(Time.deltaTime, 1f / 30f);
        time += dt;

        // ---- 锚点速度（相对风的来源）：包含移动、原地转身、动画起伏；首帧与 dt≈0 时跳过 ----
        Vector3 anchorVel = Vector3.zero;
        if (spine != null)
        {
            Vector3 p = spine.position;
            if (anchorInit && dt > 1e-5f)
            {
                anchorVel = (p - lastAnchorPos) / dt;
                float v = anchorVel.magnitude;
                if (v > maxAnchorWind) anchorVel *= maxAnchorWind / v;
            }
            lastAnchorPos = p;
            anchorInit = true;
        }

        // ---- Verlet 积分：newPos = pos + (pos - prev)*damping + a*dt² ----
        // 顶行（r==0）是锚点，不积分
        Vector3 wind = ComputeWind(anchorVel);
        Vector3 accel = new Vector3(0f, gravity, 0f) + wind;
        float dt2 = dt * dt;
        for (int i = cols; i < pos.Length; i++)
        {
            Vector3 newPos = pos[i] + (pos[i] - prevPos[i]) * damping + accel * dt2;
            prevPos[i] = pos[i];
            pos[i] = newPos;
        }

        // ---- 距离约束求解（PBD）：迭代把相邻粒子拉回 rest 长度，每轮末尾做身体推离 ----
        for (int it = 0; it < constraintIterations; it++)
        {
            for (int ci = 0; ci < cons.Length; ci++)
            {
                var con = cons[ci];
                Vector3 delta = pos[con.b] - pos[con.a];
                float d = delta.magnitude;
                if (d < 1e-6f) continue;
                float diff = (d - con.rest) / d;
                bool aPinned = con.a < cols;    // 顶行是锚点
                bool bPinned = con.b < cols;
                if (aPinned && bPinned) continue;
                if (aPinned) pos[con.b] -= delta * diff;                 // 锚点不动，位移全给另一端
                else if (bPinned) pos[con.a] += delta * diff;
                else { pos[con.a] += delta * (diff * 0.5f); pos[con.b] -= delta * (diff * 0.5f); }
            }
            PushOutOfBody();
            ClampBehindBack();
        }

        // ---- 顶行锚定：强制对齐锚骨世界位置（最后执行；锚点位移不产生粒子速度） ----
        // 中段钉 spine_02；两端角点若绑定过肩骨则钉 upperarm（跟肩膀走，消除悬空）
        if (cornerAnchorsDirty && pos != null && spine != null)
            SetupCornerAnchors();       // Play 中切换开关：懒重绑（角点会跳到当前肩位）
        if (spine != null)
            for (int c = 0; c < cols; c++)
            {
                Vector3 p = spine.TransformPoint(anchorLocals[c]);
                if (cornerBones != null && cornerLocals != null)
                {
                    if (c == 0 && cornerBones[0] != null) p = cornerBones[0].TransformPoint(cornerLocals[0]);
                    else if (c == cols - 1 && cornerBones[1] != null) p = cornerBones[1].TransformPoint(cornerLocals[1]);
                }
                pos[c] = p;
                prevPos[c] = p;
            }

        SyncMesh();
    }

    /// 粒子位置写回网格顶点（双面两份同步），法线按当前形态重算；顶点缓冲复用，零 GC
    void SyncMesh()
    {
        if (mesh == null) return;       // 隐形骨架模式：无网格可写
        var w2l = transform.worldToLocalMatrix;
        for (int i = 0; i < halfCount; i++)
        {
            meshVerts[i] = w2l.MultiplyPoint3x4(pos[i]);    // 世界坐标 → 本地坐标（mesh 挂在子物体上，直接写世界坐标会被再次平移）
            meshVerts[halfCount + i] = meshVerts[i];
        }
        mesh.SetVertices(meshVerts);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    /// Inspector 里切换"角点跟肩"后懒生效（LateUpdate 重绑）；仅开关值变化才置位，避免其他参数改动触发重绑
    void OnValidate() { if (cornerAnchorsActive != cornersFollowShoulders) cornerAnchorsDirty = true; }

    /// 递归查找子孙节点（按名字）
    static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var r = FindDeep(root.GetChild(i), name);
            if (r != null) return r;
        }
        return null;
    }
}
