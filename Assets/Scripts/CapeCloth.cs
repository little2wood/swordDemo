using UnityEngine;

/// <summary>
/// 披风布料（Verlet/PBD）：粒子 + 距离约束（横向/纵向结构 + 对角剪切），顶行钉在 anchorBone（spine_02）
/// 的本地偏移处，转身/前倾/攻击时披风顶部自动跟随躯干动画。
/// 每帧：Verlet 积分（重力 + 风） → 约束迭代（PBD，与身体碰撞推离交替） → 钉锚点 → 写回网格。
/// 风场 = 环境微风（双正弦） + 锚点运动相对风：跑动后飘、原地转身甩摆、下落上飘都由锚点速度驱动
/// （比只看刚体速度多了转向与动画起伏，且包含竖直分量）。
/// </summary>
public class CapeCloth : MonoBehaviour
{
    [Header("网格尺寸")]
    public int cols = 7;            // 列（宽向粒子数）
    public int rows = 10;           // 行（长向粒子数）
    public float width = 0.4f;      // 挂肩宽度（m）
    public float length = 0.7f;     // 垂落长度（m）

    [Header("锚定（相对锚骨的本地偏移）")]
    public float anchorHeight = 0.25f;  // 顶行相对 spine_02 的高度（约肩线）
    public float anchorBack = -0.10f;   // 贴背偏移（实测 spine_02 local +Z 朝模型胸前，负值才落到背后）

    [Header("引用（留空自动查找 spine_02）")]
    public Transform anchorBone;

    [Header("物理（Verlet/PBD）")]
    public float gravity = -9.8f;           // 重力加速度
    [Range(0.8f, 1f)] public float damping = 0.985f;    // 速度保留率（1=无阻尼）
    public int constraintIterations = 4;    // 距离约束求解迭代次数

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
    [Tooltip("各推离球半径（m）：球挂在对应骨骼上跟着动画走，粒子被推到球面外")]
    public float torsoRadius = 0.16f;   // spine_03（上胸）
    public float hipRadius = 0.14f;     // pelvis（骨盆）
    public float headRadius = 0.11f;    // Head（低头/受击时防穿）
    public float thighRadius = 0.08f;   // thigh_l / thigh_r（大腿：跑动时防披风穿腿）

    [Header("外观（renderer 无材质时自动按此建运行时材质）")]
    public Color clothColor = new Color(0.45f, 0.12f, 0.10f);
    public float smoothness = 0.35f;

    Transform spine;
    Vector3[] pos;              // 粒子位置（世界空间）
    Vector3[] prevPos;          // 上一帧位置（Verlet 积分）
    Vector3[] anchorLocals;     // 顶行粒子相对锚骨的本地偏移
    Constraint[] cons;          // 距离约束（结构 + 对角剪切）
    struct Constraint { public int a, b; public float rest; }
    Mesh mesh;
    int halfCount;              // cols*rows
    int vertCount;              // 双面顶点 = half*2
    Vector3[] meshVerts;        // 复用的顶点缓冲：每帧写网格零 GC
    float time;                 // 风场累计时间
    Vector3 lastAnchorPos;      // 上一帧锚点世界位置：算锚点速度（相对风的来源）
    bool anchorInit;
    Transform[] colBones;       // 身体碰撞球挂的骨骼
    float[] colRadii;           // 与 colBones 一一对应的半径

    void Awake() { BuildNow(); }

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

    /// 身体碰撞球：胸口/骨盆/头/大腿，挂在骨骼上跟着动画走（缺哪根骨就跳过哪个球）
    void BuildColliders()
    {
        var root = transform.root != null ? transform.root : transform;
        var bones = new System.Collections.Generic.List<Transform>();
        var radii = new System.Collections.Generic.List<float>();

        Transform b;
        b = FindDeep(root, "spine_03"); if (b != null) { bones.Add(b); radii.Add(torsoRadius); }
        b = FindDeep(root, "pelvis");   if (b != null) { bones.Add(b); radii.Add(hipRadius); }
        b = FindDeep(root, "Head");     if (b != null) { bones.Add(b); radii.Add(headRadius); }
        b = FindDeep(root, "thigh_l");  if (b != null) { bones.Add(b); radii.Add(thighRadius); }
        b = FindDeep(root, "thigh_r");  if (b != null) { bones.Add(b); radii.Add(thighRadius); }

        colBones = bones.ToArray();
        colRadii = radii.ToArray();
    }

    /// 把非锚点粒子推出身体碰撞球（迭代内调用，与约束求解交替收敛，最后一轮保证不穿身体）
    void PushOutOfBody()
    {
        if (!bodyCollision || colBones == null) return;
        for (int s = 0; s < colBones.Length; s++)
        {
            if (colBones[s] == null) continue;
            Vector3 c = colBones[s].position;
            float r = colRadii[s];
            float rSq = r * r;
            for (int i = cols; i < halfCount; i++)      // 顶行是锚点，不参与
            {
                Vector3 d = pos[i] - c;
                float dsq = d.sqrMagnitude;
                if (dsq < rSq && dsq > 1e-8f)
                {
                    float len = Mathf.Sqrt(dsq);
                    pos[i] += d * ((r - len) / len);
                }
            }
        }
    }

    /// 粒子初始位置：顶行按锚点展开，其余行竖直下垂
    void InitParticles()
    {
        int n = cols * rows;
        pos = new Vector3[n];
        prevPos = new Vector3[n];
        anchorLocals = new Vector3[cols];
        for (int c = 0; c < cols; c++)
        {
            float x = Mathf.Lerp(-width / 2f, width / 2f, (float)c / (cols - 1));
            anchorLocals[c] = new Vector3(x, anchorHeight, anchorBack);
            Vector3 anchorW = spine.TransformPoint(anchorLocals[c]);
            for (int r = 0; r < rows; r++)
                pos[r * cols + c] = anchorW + Vector3.down * (length * r / (rows - 1));
        }
        System.Array.Copy(pos, prevPos, n);
    }

    /// 距离约束：横向 + 纵向（结构） + 每格两条对角线（剪切）——布不再能被剪成菱形/对折成纸片
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
        if (mesh == null || pos == null || cons == null) return;

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
        }

        // ---- 顶行锚定：强制对齐锚骨世界位置（最后执行；锚点位移不产生粒子速度） ----
        if (spine != null)
            for (int c = 0; c < cols; c++)
            {
                pos[c] = spine.TransformPoint(anchorLocals[c]);
                prevPos[c] = pos[c];
            }

        SyncMesh();
    }

    /// 粒子位置写回网格顶点（双面两份同步），法线按当前形态重算；顶点缓冲复用，零 GC
    void SyncMesh()
    {
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
