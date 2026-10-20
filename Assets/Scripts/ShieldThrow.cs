using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 盾牌投掷 —— 步骤①：出手与飞行。
/// 空格按屏幕中心甩出，出手后完全交给物理：重力按 gravityScale 缩放 + 微阻尼 + 绕盾面法线的自旋，
/// 没有任何追踪/制导，轨迹全由出手方向决定；停下来后冻结在原地。
/// 本步只做出手与飞行：不含回收、不含命中结算、不含预测线（后续步骤补）。
/// 状态：InHand → Flying → Loose
/// </summary>
public class ShieldThrow : MonoBehaviour
{
    public enum State { InHand, Flying, Loose }

    [Header("引用（留空自动查找）")]
    public Transform owner;                 // 主角（含 PlayerController 的那个根物体）
    public Camera aimCamera;                // 瞄准参考相机（留空取 Camera.main）
    public ThrowReticle reticle;            // 瞄准指示器（瞄准时由它放大突出预瞄点）
    public string socketName = "Shield_L";  // 场景里盾牌挂点名（脚本挂盾自身时可留空）

    [Header("出手")]
    [Tooltip("false=全方位投掷：相机的俯仰角直接当成出手仰角，可以往天上抛、也可以居高临下往下砸；\ntrue=只保留左右瞄准（强制水平甩出，最好瞄）")]
    public bool flattenAim = false;         // 默认全方位；想改回"只能水平甩"勾上它
    public float throwSpeed = 22f;          // 出手速度（m/s）
    public float throwForward = 0.6f;       // 出手点沿瞄准方向前移，避免一出手就擦到自身
    [Tooltip("甩出去的起点沿\"画面右方\"侧移多少米：让整条弹道连同预瞄点一起偏右，\n近距离时才不会被主角自己的模型挡住。0=完全从身前飞出。实际飞行与预测共用这个起点，所以预瞄点仍然精确。")]
    public float aimSideOffset = 0.45f;

    [Header("自旋（飞盘式：绕盾面法线）")]
    [Tooltip("盾面法线（本地轴）。(0,0,0) 时按网格包围盒最薄的一轴自动判定")]
    public Vector3 spinLocalAxis = Vector3.zero;
    public float spinRate = 12f;            // 角速度 rad/s（只在出手瞬间赋一次，之后守恒）
    [Tooltip("出手瞬间盾面放平、边缘朝前：法线竖直（水平弹道时正朝上），像美队盾牌那样绕竖直轴旋着飞出")]
    public bool alignFaceToThrow = true;

    [Header("命中结算")]
    [Tooltip("可互动物体所在层（Inspector 选 Prop）：箱子等；主角/敌人/平台不在该层，天然不受影响")]
    public LayerMask propLayer;
    public float shieldMass = 4f;       // 盾的有效质量：冲量 = 盾速度(m/s) × 该质量(kg)，单位 N·s
    public float maxThrowSpeed = 22f;   // 用于冲量计算的速度上限（防卡顿帧/峰值把目标弹射上天）
    public float throwDamage = 5f;      // 一发盾牌的伤害

    [Header("飞行物理")]
    [Range(0f, 1f)] public float gravityScale = 0.4f;   // 重力缩放（0=直线，1=完整抛物线）
    public float flightDrag = 0.02f;                    // 线性阻尼（飞行衰减）
    public float maxSpeed = 40f;                        // 飞行速度上限
    public float restSpeed = 1.0f;                      // 低于该速度视为"停下来"
    public float restTime = 0.3f;                       // 持续这么久才冻结

    /// <summary>当前状态（后续步骤用它来判定能不能再投 / 能不能回收）。</summary>
    public State Current { get; private set; } = State.InHand;
    /// <summary>盾是否还在手上。</summary>
    public bool InHand { get { return Current == State.InHand; } }

    Rigidbody rb;
    Collider col;

    // 握持位姿（socket）：出手时脱离骨骼层级，以后回手时按这套本地变换贴回去
    Transform socketParent;
    Vector3 socketLocalPos;
    Quaternion socketLocalRot;
    Vector3 socketLocalScale;

    float restTimer;
    bool firstHitFired;     // 本次飞行是否已报告过第一次碰撞

    // 一次飞行内的命中去重：同一个箱子/同一个敌人只结算一次，避免弹跳反复触发
    readonly HashSet<Rigidbody> hitThisThrow = new HashSet<Rigidbody>();
    readonly HashSet<Health> hitThisThrowHealth = new HashSet<Health>();
    Vector3 lastVel;        // 最近一次 FixedUpdate 的盾速度（碰撞时物理已改过 rb，用它当碰撞前速度）

    /// 飞行途中第一次撞到外部物体时触发（供相机提前结束"投掷观察期"等外部逻辑使用）
    public event System.Action FlightFirstHit;

    void Awake()
    {
        // ---- 自动查找引用 ----
        if (owner == null)
        {
            var pc = GetComponentInParent<PlayerController>();
            owner = pc != null ? pc.transform : transform.root;
        }
        if (aimCamera == null) aimCamera = Camera.main;
        if (reticle == null) reticle = GetComponent<ThrowReticle>();
        if (reticle == null && owner != null) reticle = owner.GetComponentInChildren<ThrowReticle>(true);

        // 挂点可以是盾自身（脚本挂在盾上时），也可以是 owner 下的同名骨骼空物体
        Transform self = transform;
        if (!string.IsNullOrEmpty(socketName))
        {
            Transform found = FindDeep(owner != null ? owner : transform, socketName);
            if (found != null) self = found;
        }
        socketParent = self.parent;
        socketLocalPos = self.localPosition;
        socketLocalRot = self.localRotation;
        socketLocalScale = self.localScale;

        // 在手期间绝不能带 Rigidbody：挂在 Animator 骨骼层级下的刚体会被物理接管，
        // 不再跟随骨骼动画 —— 表现为"盾悬浮在空中、主角走远它留在原地"。
        // 刚体/碰撞体推迟到出手瞬间才创建（见 EnsurePhysics）。
        var leftover = self.GetComponent<Rigidbody>();
        if (leftover != null) DestroyImmediate(leftover);   // 立即移除，避免本帧 Throw 时被当成已有刚体复用

        SetIdle();
    }

    /// 出手瞬间才补齐物理部件（不改动磁盘资产，也不影响在手时的骨骼动画）
    void EnsurePhysics()
    {
        rb = GetComponent<Rigidbody>();
        if (rb == null) rb = gameObject.AddComponent<Rigidbody>();
        rb.useGravity = false;              // 重力全程自己施加，便于按 gravityScale 调档
        rb.linearDamping = 0f;
        rb.angularDamping = 0f;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;  // 薄盾高速防穿透
        rb.interpolation = RigidbodyInterpolation.Interpolate;

        col = GetComponent<Collider>();
        if (col == null)
        {
            // 只有 Box/Capsule/Sphere 支持 CCD，所以用 Box 按网格包围盒自动贴合
            var box = gameObject.AddComponent<BoxCollider>();
            FitBoxToMesh(box);
            col = box;
        }
    }

    /// 碰撞体按网格包围盒自动贴合（略大于本 mesh），省掉手工调数值
    void FitBoxToMesh(BoxCollider box)
    {
        Mesh mesh = null;
        var mf = GetComponent<MeshFilter>();
        if (mf != null) mesh = mf.sharedMesh;
        if (mesh == null) { box.size = new Vector3(0.6f, 0.8f, 0.1f); return; }

        Vector3 s = mesh.bounds.size;
        Vector3 ls = transform.lossyScale;
        Vector3 size = new Vector3(
            s.x / Mathf.Max(1e-5f, Mathf.Abs(ls.x)),
            s.y / Mathf.Max(1e-5f, Mathf.Abs(ls.y)),
            s.z / Mathf.Max(1e-5f, Mathf.Abs(ls.z)));
        box.size = size * 1.02f;                        // 留一点余量，避免面擦不到
        box.center = mesh.bounds.center;
    }

    void FixedUpdate()
    {
        if (Current != State.Flying) return;

        float dt = Time.fixedDeltaTime;
        Vector3 v = rb.linearVelocity;

        // 自定义重力（gravityScale 档位） + 线性阻尼
        v += Physics.gravity * gravityScale * dt;
        v *= 1f / (1f + flightDrag * dt);
        v = Vector3.ClampMagnitude(v, maxSpeed);
        rb.linearVelocity = v;
        lastVel = v;        // 供碰撞结算当作"碰撞前速度"（物理步之后 rb 的速度已被改变）

        // 停下来 → 冻结在原地
        if (v.sqrMagnitude < restSpeed * restSpeed)
        {
            restTimer += dt;
            if (restTimer >= restTime) Freeze();
        }
        else restTimer = 0f;
    }

    // ---------- 首次碰撞 ----------

    void OnCollisionEnter(Collision collision)
    {
        if (Current != State.Flying) return;
        if (IsSelfCollider(collision.collider)) return;   // 保险：IgnoreCollision 已挡住自己人，这里再筛一次

        Vector3 point = collision.contactCount > 0 ? collision.GetContact(0).point : transform.position;
        SettleHit(collision.collider, point);

        // 相机的"投掷观察期"只关心第一次碰撞，之后继续撞不再重复通知
        if (!firstHitFired)
        {
            firstHitFired = true;
            var handler = FlightFirstHit; if (handler != null) handler();
        }
    }

    /// <summary>
    /// 命中结算：箱子走 PropImpulse 冲量击飞；敌人走 ShieldHitReaction 按骨骼局部受力 + 扣血。
    /// （这里刻意不走 HitReaction —— 投掷盾牌是独立方案，不叠加统一的上半身摆动。）
    /// </summary>
    void SettleHit(Collider col, Vector3 point)
    {
        // ---- 可互动物体（Prop 层）：接触点施加冲量，偏心命中自动产生扭矩 ----
        if (PropImpulse.IsProp(col.gameObject, propLayer))
        {
            PropImpulse.Apply(col.attachedRigidbody, lastVel, shieldMass, maxThrowSpeed, point, hitThisThrow);
            return;
        }

        // ---- 敌人：按被击中的骨骼做局部受力反馈 ----
        var hp = col.GetComponentInParent<Health>();
        if (hp == null || hp.IsDead || hp.team != Health.Team.Enemy) return;
        if (!hitThisThrowHealth.Add(hp)) return;    // 这次飞行已结算过该目标

        Vector3 dir = lastVel.sqrMagnitude > 1e-6f ? lastVel.normalized : transform.forward;
        float impulse = PropImpulse.Magnitude(lastVel, shieldMass);

        var reaction = col.GetComponentInParent<ShieldHitReaction>();
        if (reaction != null) reaction.ReactAt(point, dir, impulse);

        hp.TakeDamage(throwDamage, dir, impulse, point, true);   // 致死时自动交给原 Ragdoll 方案
    }

    // ---------- 出手 ----------

    /// <summary>
    /// 屏幕中心瞄准方向（ThirdPersonCamera 始终 LookAt 胸口，屏幕中心即准心）。
    /// 默认拍平：相机默认俯角 18°，直接拿它的 forward 会往下砸地，所以只保留左右瞄准。
    /// flattenAim=false 时允许完整三维前向（可往下砸、往上抬）。
    /// </summary>
    public Vector3 AimDirection()
    {
        Vector3 d = aimCamera != null ? aimCamera.transform.forward
                                      : (owner != null ? owner.forward : transform.forward);
        if (flattenAim)
        {
            d.y = 0f;
            if (d.sqrMagnitude < 0.0001f) d = owner != null ? owner.forward : Vector3.forward;   // 垂直俯视时的兜底
            d.y = 0f;
        }
        return d.normalized;
    }

    /// <summary>
    /// 甩出去的起点：这是盾本身就是"甩"出去的，起点可以侧移到身体右前方。
    /// 飞行与预测线共用这个起点，所以预瞄点标的位置仍然就是盾真会落到的地方 —— 只是整体偏右，
    /// 近距离时不会被自己的模型挡住。dir 为瞄准方向时按它的右手侧横移。
    /// </summary>
    public Vector3 AimStartPoint()
    {
        Vector3 aim = AimDirection();
        Vector3 right = Vector3.Cross(Vector3.up, aim);
        if (right.sqrMagnitude < 1e-6f) right = Vector3.right;      // 垂直上下瞄准时的兜底
        right.Normalize();
        return PushOutOfSolids(transform.position + aim * throwForward + right * aimSideOffset, aim);
    }

    /// <summary>
    /// 敌人/箱子贴脸时，出手点会陷进它的碰撞体内部 —— 而 Physics.Raycast 从碰撞体内部出发不会命中它，
    /// 射线会直接穿过去，预瞄点就跑到敌人身后去了（实弹同理会穿过去）。
    /// 这里把起点沿"射手这一侧"退到目标表面之外一点点：既让预瞄立刻命中，也让实弹确实会在那里撞上。
    /// </summary>
    Vector3 PushOutOfSolids(Vector3 p, Vector3 aim)
    {
        for (int iter = 0; iter < 3; iter++)
        {
            var overlaps = Physics.OverlapSphere(p, 0.02f);
            bool buried = false;
            for (int i = 0; i < overlaps.Length; i++)
            {
                var c = overlaps[i];
                if (c == null || c.isTrigger) continue;
                if (IsSelfCollider(c)) continue;
                if (Vector3.Distance(c.ClosestPoint(p), p) < 0.001f) { buried = true; break; }   // 点就在它体内
            }
            if (!buried) break;

            RaycastHit h;
            if (!CastIgnoreSelf(p - aim * 2f, aim, 3f, out h)) break;   // 从身后往前打，命中面就是朝我这侧的表面
            p = h.point - aim * 0.015f;                                 // 退到表面外一点点
        }
        return p;
    }

    /// 射线：命中的是主角自身就跳过继续往前找
    bool CastIgnoreSelf(Vector3 origin, Vector3 dir, float maxDist, out RaycastHit result)
    {
        result = default(RaycastHit);
        float remain = maxDist;
        Vector3 p = origin;
        while (remain > 0.01f)
        {
            RaycastHit h;
            if (Physics.Raycast(p, dir, out h, remain))
            {
                if (!IsSelfCollider(h.collider)) { result = h; return true; }
                float skip = Mathf.Max(h.distance, 0f) + 0.05f;
                p += dir * skip;
                remain -= skip;
                continue;
            }
            break;
        }
        return false;
    }

    bool IsSelfCollider(Collider c)
    {
        if (c == null || owner == null) return false;
        return c.transform == owner || c.transform.IsChildOf(owner);
    }

    /// <summary>沿 dir 甩出（由 PlayerController 消费空格输入后调用）。</summary>
    public void Throw(Vector3 dir)
    {
        if (Current != State.InHand) return;

        Vector3 start = AimStartPoint();

        // 先脱离骨骼层级，再挂刚体：飞行的刚体留在动画骨骼下会和骨骼姿态互相打架
        transform.SetParent(null, true);
        transform.SetPositionAndRotation(start, transform.rotation);

        // 盾面平行于弹道、且"躺"在弹道所在的竖直平面关系里：法线取 up 去掉弹道分量
        // （n ⊥ 弹道，且 n、弹道、竖直三者共面 → 法线与弹道张成的平面垂直于水平面）。
        // 水平弹道时法线正好竖直朝上 → 盾面水平，像美队盾牌那样绕竖直轴旋着平飞
        if (alignFaceToThrow)
        {
            Vector3 spinAxis = Vector3.up - Vector3.Project(Vector3.up, dir);
            if (spinAxis.sqrMagnitude < 1e-6f)
                spinAxis = transform.TransformDirection(DetectSpinAxisLocal());   // 近乎垂直上抛时兜底
            Vector3 faceWorld = transform.TransformDirection(DetectSpinAxisLocal());
            Quaternion align = Quaternion.FromToRotation(faceWorld, spinAxis.normalized);
            transform.rotation = align * transform.rotation;
        }

        EnsurePhysics();
        IgnoreSelf(true);       // 出手瞬间别踫到主角自己的根碰撞体/布娃娃部件

        rb.isKinematic = false;
        rb.detectCollisions = true;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.WakeUp();

        Current = State.Flying;
        restTimer = 0f;
        firstHitFired = false;
        hitThisThrow.Clear();
        hitThisThrowHealth.Clear();
        lastVel = dir * throwSpeed;     // 出手瞬间速度：碰撞结算用（物理步之后 rb 的速度已被改写）

        rb.linearVelocity = dir * throwSpeed;

        // 飞盘式自旋：绕盾面法线，赋值一次后不再干预（角动量守恒，盘面朝向在惯性空间保持稳定）
        Vector3 axis = transform.TransformDirection(DetectSpinAxisLocal());
        rb.angularVelocity = (axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.up) * spinRate;
    }

    /// 盾面法线（本地）：取网格包围盒最薄的一根轴；没有网格时退回 Vector3.up
    Vector3 DetectSpinAxisLocal()
    {
        if (spinLocalAxis.sqrMagnitude > 1e-6f) return spinLocalAxis.normalized;

        Mesh mesh = null;
        var mf = GetComponent<MeshFilter>();
        if (mf != null) mesh = mf.sharedMesh;

        Vector3 s = mesh != null ? mesh.bounds.size : new Vector3(0.6f, 0.05f, 0.8f);
        Vector3 axis = Vector3.up;
        float min = s.y;
        if (s.x < min) { min = s.x; axis = Vector3.right; }
        if (s.z < min) { axis = Vector3.forward; }
        return axis;
    }

    void Freeze()
    {
        Current = State.Loose;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.isKinematic = true;      // 冻结：彻底静止，也省 CPU
    }

    /// 出手期间忽略主角的碰撞体（根碰撞体 + 布娃娃部件），否则一出手就会被自己弹飞
    void IgnoreSelf(bool ignore)
    {
        if (col == null || owner == null) return;
        foreach (var c in owner.GetComponentsInChildren<Collider>(true))
        {
            if (c == null || c == col || !c.enabled) continue;
            Physics.IgnoreCollision(col, c, ignore);
        }
    }

    /// 回到"在手"的静止态（Awake 初始化用；后续回收步骤会复用）
    void SetIdle()
    {
        if (rb != null)
        {
            if (!rb.isKinematic)    // kinematic 状态下赋值速度会刷 Unity 警告
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            rb.isKinematic = true;
            rb.detectCollisions = false;
        }
        Current = State.InHand;
        restTimer = 0f;
    }

    static Transform FindDeep(Transform root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var r = FindDeep(root.GetChild(i), name);
            if (r != null) return r;
        }
        return null;
    }
}
