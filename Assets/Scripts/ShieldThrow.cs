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
    public enum State { InHand, Flying, Loose, Returning }

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

    [Header("回收")]
    [Tooltip("盾牌飞回手上的速度（m/s）：按当前距离算飞行时长")]
    public float recoverSpeed = 18f;
    [Tooltip("回收弧线的拱起高度（m）：0=直线吸回，越大抛得越高")]
    public float recoverArc = 0.8f;
    [Tooltip("回收时把盾挂在 socket 之前，先跟到离 socket 这么近")]
    public float recoverSnapDist = 0.12f;

    [Header("破防掉盾")]
    [Tooltip("破防动画播到第几帧时盾脱手（按 breakClipFps 换算成时间）")]
    public float breakReleaseFrame = 9f;
    public float breakClipFps = 30f;
    [Tooltip("破防动画 clip 长度（秒）：把\"第几帧\"换算成状态内进度用（Idle_Shield_Break = 1.0667s）")]
    public float breakClipLength = 1.067f;
    [Tooltip("兜底：破防触发后超过这么久无论如何都把盾甩出去（防动画状态没接上）")]
    public float breakForceTime = 1.5f;
    [Tooltip("按动画采样速度换算的出手速度再乘这个系数（1=完全按动画的手速飞出去）")]
    public float breakSpeedScale = 1f;
    [Tooltip("采样到的手速过小（动画那几帧手几乎没动）时的兜底速度")]
    public float breakMinSpeed = 3f;
    [Tooltip("按动画采样角速度换算的自旋再乘这个系数")]
    public float breakSpinScale = 1f;

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

    bool colRuntime;     // 碰撞体是否为出手时由脚本创建（回收时要销毁）

    // 回收插值状态：从 returnFrom 沿一条拱起的弧线飞回 socket
    Vector3 returnFrom;
    Quaternion returnFromRot;
    float returnT;
    float returnDuration;

    // 破防掉盾：动画出手帧之前盾仍挂在 socket 上跟着动画走，逐帧采样它的世界速度/角速度，
    // 到出手帧时把这些"当时盾牌的物理量"原样交给刚体 —— 飞出去的方向和速度就是动画甩出来的样子
    Animator breakAnimator;
    static readonly int BreakStateH = Animator.StringToHash("Idle_Shield_Break");
    bool breakPending;
    float breakT;
    Vector3 samplePrevPos;
    Quaternion samplePrevRot;
    Vector3 sampleVel;
    Vector3 sampleAngVel;
    bool sampleInit;
    bool fromBreak;      // 本次飞行是"被打掉"而不是主动投掷：不触发投掷观察期

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
            colRuntime = true;      // 脚本创建的：回手时销毁，别留在手上
        }
        else colRuntime = false;
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

    // ---------- 回收 ----------

    /// <summary>
    /// 召回盾牌：从当前位置沿一条拱起的弧线飞回握持 socket。
    /// 回程切成 kinematic —— 撞到 Prop 层箱子仍会把它推开（真实碰撞），
    /// 但盾自己不受反作用力、不减速、不偏转，稳稳飞回手上。
    /// </summary>
    public void StartReturn()
    {
        if (Current != State.Flying && Current != State.Loose) return;
        if (socketParent == null) { SetIdle(); return; }

        returnFrom = transform.position;
        returnFromRot = transform.rotation;
        returnT = 0f;
        float dist = Vector3.Distance(returnFrom, socketParent.TransformPoint(socketLocalPos));
        returnDuration = Mathf.Max(0.05f, dist / Mathf.Max(0.01f, recoverSpeed));

        if (rb != null) rb.isKinematic = true;   // detectCollisions 保持 true：照样撞飞箱子
        Current = State.Returning;
    }

    /// 回收落位：贴回握持 socket 并回到"在手"静止态
    void FinishReturn()
    {
        if (socketParent != null)
        {
            transform.SetParent(socketParent, false);
            transform.localPosition = socketLocalPos;
            transform.localRotation = socketLocalRot;
            transform.localScale = socketLocalScale;
        }
        SetIdle();
    }

    // ---------- 破防掉盾 ----------

    /// <summary>
    /// 破防：盾不立刻飞走，先跟着破防动画（Idle_Shield_Break）走到出手帧，
    /// 在那一帧按"当时盾牌的物理量"（逐帧采样出来的线速度 + 角速度）原样交给物理。
    /// 方向/速度完全由动画决定 —— 动画怎么甩，盾就怎么飞。
    /// 只在盾还在手上时生效（盾已被投出去的情况下破防就不掉盾）。
    /// </summary>
    public bool BeginBreakThrow()
    {
        if (Current != State.InHand) return false;

        if (breakAnimator == null)
            breakAnimator = owner != null ? owner.GetComponent<Animator>() : GetComponentInParent<Animator>();

        breakPending = true;
        breakT = 0f;
        sampleInit = false;
        sampleVel = Vector3.zero;
        sampleAngVel = Vector3.zero;
        return true;
    }

    /// 出手时间（秒）：第 breakReleaseFrame 帧
    float BreakReleaseTime()
    {
        return breakReleaseFrame / Mathf.Max(1f, breakClipFps);
    }

    /// 是否已经播到破防动画的出手帧（用状态内进度换算成秒，避免过渡时长把时机带偏）
    bool InBreakState(out float stateTime)
    {
        stateTime = 0f;
        if (breakAnimator == null) return false;
        var st = breakAnimator.GetCurrentAnimatorStateInfo(0);
        if (st.shortNameHash != BreakStateH) return false;
        stateTime = st.normalizedTime * breakClipLength;
        return true;
    }

    /// <summary>
    /// 逐帧采样盾的世界速度/角速度。放在 LateUpdate：Animator 在 Update 之后才写骨骼，
    /// 此时读到的才是本帧动画实际摆到的姿态。
    /// </summary>
    void SampleBreakMotion(float dt)
    {
        Vector3 p = transform.position;
        Quaternion r = transform.rotation;

        if (sampleInit && dt > 1e-5f)
        {
            Vector3 v = (p - samplePrevPos) / dt;

            Quaternion dq = r * Quaternion.Inverse(samplePrevRot);
            float ang; Vector3 axis;
            dq.ToAngleAxis(out ang, out axis);
            if (ang > 180f) ang -= 360f;                        // ToAngleAxis 只给 0~360，取短弧
            Vector3 av = axis.sqrMagnitude > 1e-8f
                ? axis.normalized * (ang * Mathf.Deg2Rad / dt)
                : Vector3.zero;

            // 帧间差分抖动大，做一次平滑，免得某一帧抽风把盾弹射出去
            sampleVel = Vector3.Lerp(sampleVel, v, 0.5f);
            sampleAngVel = Vector3.Lerp(sampleAngVel, av, 0.5f);
        }

        samplePrevPos = p;
        samplePrevRot = r;
        sampleInit = true;
    }

    void LateUpdate()
    {
        if (!breakPending) return;

        float dt = Time.deltaTime;
        SampleBreakMotion(dt);
        breakT += dt;

        float releaseT = BreakReleaseTime();
        float stateTime;
        if ((InBreakState(out stateTime) && stateTime >= releaseT) || breakT >= breakForceTime)
            ReleaseBreak();
    }

    /// 出手帧：把采样到的速度/角速度交给刚体，之后完全交给物理
    void ReleaseBreak()
    {
        breakPending = false;

        Vector3 pos = transform.position;
        Quaternion rot = transform.rotation;

        // 先脱离骨骼层级再挂刚体：挂在动画骨骼下的刚体会被物理接管、不再跟随动画
        transform.SetParent(null, true);
        transform.SetPositionAndRotation(pos, rot);

        EnsurePhysics();
        IgnoreSelf(true);       // 脱手瞬间别踫到主角自己的碰撞体

        rb.isKinematic = false;
        rb.detectCollisions = true;
        rb.WakeUp();

        Vector3 v = sampleVel * breakSpeedScale;
        if (v.magnitude < breakMinSpeed)     // 采样速度过小（那几帧手几乎没动）时的兜底，保证确实被打飞
        {
            Vector3 d = v.sqrMagnitude > 1e-6f ? v.normalized : (owner != null ? owner.forward : transform.forward);
            v = d * breakMinSpeed;
        }
        rb.linearVelocity = v;
        rb.angularVelocity = sampleAngVel * breakSpinScale;

        Current = State.Flying;
        restTimer = 0f;
        firstHitFired = false;
        fromBreak = true;
        hitThisThrow.Clear();
        hitThisThrowHealth.Clear();
        lastVel = v;        // 碰撞结算当作"碰撞前速度"
    }

    void Update()
    {
        if (Current != State.Returning) return;
        if (socketParent == null) { FinishReturn(); return; }

        returnT += Time.deltaTime;
        float k = returnDuration > 0.0001f ? Mathf.Clamp01(returnT / returnDuration) : 1f;

        Vector3 targetPos = socketParent.TransformPoint(socketLocalPos);
        Quaternion targetRot = socketParent.rotation * socketLocalRot;

        // 位置：直线插值 + 正弦拱起（两端为 0、中点最高）
        Vector3 flat = Vector3.Lerp(returnFrom, targetPos, k);
        float arc = recoverArc * Mathf.Sin(k * Mathf.PI);
        transform.position = flat + Vector3.up * arc;

        // 朝向：回程姿态转回握持姿态，同时叠加自旋
        Quaternion baseRot = Quaternion.Slerp(returnFromRot, targetRot, k);
        Quaternion spin = Quaternion.AngleAxis(spinRate * returnT * Mathf.Rad2Deg, DetectSpinAxisLocal());
        transform.rotation = baseRot * spin;

        if (k >= 1f) FinishReturn();
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

        // 相机的"投掷观察期"只关心第一次碰撞，之后继续撞不再重复通知。
        // 破防掉盾不是主动投掷，不拉观察期。
        if (!firstHitFired && !fromBreak)
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

        // ---- 敌人：和挥砍/爪击走同一套受击反馈（HitReaction 统一上半身摆动） ----
        var hp = col.GetComponentInParent<Health>();
        if (hp == null || hp.IsDead || hp.team != Health.Team.Enemy) return;
        if (!hitThisThrowHealth.Add(hp)) return;    // 这次飞行已结算过该目标

        Vector3 dir = lastVel.sqrMagnitude > 1e-6f ? lastVel.normalized : transform.forward;
        float impulse = PropImpulse.Magnitude(lastVel, shieldMass);

        hp.TakeDamage(throwDamage, dir, impulse, point);   // 致死时自动交给原 Ragdoll 方案
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
        fromBreak = false;
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

    /// 回到"在手"的静止态（Awake 初始化用；回收落位也走这里）
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

            // 出手时创建的刚体必须在回手时销毁：挂在骨骼层级下带刚体会被物理接管，
            // 不再跟随动画 —— 表现为"盾悬空留在原地、主角走远"
            DestroyImmediate(rb);
            rb = null;
        }

        if (col != null && colRuntime)
        {
            DestroyImmediate(col);
            col = null;
            colRuntime = false;
        }

        breakPending = false;   // 破防掉盾 pending 期间被强制复位时清掉，避免残留到下一次
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
