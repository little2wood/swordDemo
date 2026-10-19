using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 角色控制器：物理驱动移动（WASD 按固定屏幕方向）+ 朝向平滑对齐移动方向 + 攻击仅在站立时可起手/续击。
/// 攻击链全程（出刀/收势/连击）锁定移动与朝向，连击输入不受影响。
/// 攻击点击经 PlayerInput 的缓冲传递到 FixedUpdate 消费，避免渲染帧与物理步不同步导致丢点击。
/// 相机为固定偏移跟随（不旋转），动画只负责表演，位移完全由 Rigidbody 决定。
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Animator))]
[RequireComponent(typeof(PlayerInput))]
public class PlayerController : MonoBehaviour
{
    [Header("移动")]
    public float moveSpeed = 6f;        // m/s

    [Header("朝向")]
    public float turnSpeed = 540f;      // deg/s，朝向转向移动方向的角速度
    public float facingOffset = 0f;     // 模型自带朝向偏移：实测模型正面朝 +Z 故为 0；若角色背对移动方向改成 180

    [Header("挥砍判定")]
    public float attackDamage = 34f;    // 每次挥砍伤害

    [Header("扫掠检测（剑尖轨迹）")]
    public Transform bladeBase;         // 剑根 Transform（留空则自动查找 Sword）
    public Transform bladeTip;          // 剑尖 Transform（留空则自动查找 SwordTip）
    [Range(0.01f, 0.3f)] public float bladeRadius = 0.05f;   // 剑刃半宽（胶囊半径，即判定容错度）
    [Range(0f, 1f)] public float tipLength = 0.25f;          // 参与判定的剑尖段长度（从剑尖沿刃往回取；0=纯剑尖点，≈剑长=整条刃）
    public bool debugSweep = false;     // 在 Scene 视图画出扫掠检测线（黄=刃线，红=扫掠线）

    [Header("击飞可互动物体（Prop 层：箱子等）")]
    public LayerMask propLayer;         // 可互动物体所在层（Inspector 选 Prop）；主角/敌人/平台不在该层，天然不受影响
    public float bladeMass = 2f;        // 剑的有效质量：冲量 = 剑尖速度(m/s) × 该质量(kg)，单位 N·s
    public float maxSwingSpeed = 18f;   // 剑尖速度上限（m/s）：防卡顿帧或速度峰值把物体弹射上天

    [Header("扫掠激活窗口（normalizedTime 区间；前摇/收势不判，按各刀剑尖速度曲线实测标定）")]
    public Vector2 windowA = new Vector2(0.40f, 0.75f);   // A：挥砍爆发 0.46~0.65
    public Vector2 windowB = new Vector2(0.35f, 0.65f);   // B：挥砍爆发 0.375~0.56
    public Vector2 windowC = new Vector2(0.28f, 0.45f);   // C：前摇很长(0~0.30)，爆发仅 0.31~0.33

    Rigidbody rb;
    Animator anim;
    PlayerInput input;
    Camera cam;

    int lastSwingHash;      // 上一次挥砍的状态 hash，用于识别"新的一刀"
    Vector3 prevTip;        // 上一渲染帧的剑尖位置（扫掠起点）
    Vector3 prevInner;      // 上一渲染帧剑尖段的内端点（扫掠胶囊轴的另一端）
    bool sweepInit;         // 扫掠起点是否已采样
    readonly HashSet<Health> hitThisSwing = new HashSet<Health>();  // 本刀已结算的目标（对同一目标只结算一次）
    readonly HashSet<Rigidbody> hitPropsThisSwing = new HashSet<Rigidbody>();  // 本刀已推过的可互动物体（一次挥砍只推一次）

    // Animator 参数与状态名 hash
    static readonly int MovingId = Animator.StringToHash("Moving");
    static readonly int GuardId = Animator.StringToHash("Guard");
    static readonly int AttackId = Animator.StringToHash("Attack");
    static readonly int IdleH = Animator.StringToHash("Idle");
    static readonly int JogH = Animator.StringToHash("Jog");
    static readonly int RaiseH = Animator.StringToHash("Shield_Raise");
    static readonly int LowerH = Animator.StringToHash("Shield_Lower");
    static readonly int AH = Animator.StringToHash("A");
    static readonly int ARecH = Animator.StringToHash("A_Rec");
    static readonly int BH = Animator.StringToHash("B");
    static readonly int BRecH = Animator.StringToHash("B_Rec");
    static readonly int CH = Animator.StringToHash("C");

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        anim = GetComponent<Animator>();
        input = GetComponent<PlayerInput>();
        cam = Camera.main;

        // 自动查找剑的判定点（Inspector 未指定时）
        if (bladeTip == null || bladeBase == null)
        {
            var sword = FindDeep(transform, "Sword");
            if (sword != null)
            {
                if (bladeBase == null) bladeBase = sword;
                var tip = FindDeep(sword, "SwordTip");
                if (bladeTip == null) bladeTip = tip != null ? tip : sword;
            }
        }
    }

    void Update()
    {
        SweepHit();     // 扫掠检测跟动画帧走（Animator 在 Update 驱动），剑尖采样按渲染帧稳定推进
    }

    void FixedUpdate()
    {
        Vector2 mv = input.MoveInput;
        bool hasMove = mv.sqrMagnitude > 0.01f;

        // ---- 移动方向：相机系 WASD（相机不旋转，即固定的屏幕方向） ----
        Vector3 dir = Vector3.zero;
        if (hasMove)
        {
            Vector3 fwd = cam != null ? Flat(cam.transform.forward) : Vector3.forward;
            Vector3 right = cam != null ? Flat(cam.transform.right) : Vector3.right;
            dir = (fwd * mv.y + right * mv.x).normalized;
        }

        // ---- 位移：攻击链全程(A/A_Rec/B/B_Rec/C)与防御(Raise/Lower)锁定；常态正常移动 ----
        if (IsAttackChain() || IsGuarding() || !hasMove)
        {
            ZeroHorizontalVelocity();
        }
        else
        {
            Vector3 v = rb.linearVelocity;
            v.x = dir.x * moveSpeed;
            v.z = dir.z * moveSpeed;
            rb.linearVelocity = v;
        }

        // ---- 朝向：平滑转向移动方向（攻击链与防御全程锁定朝向不转） ----
        if (hasMove && !IsAttackChain() && !IsGuarding())
        {
            float targetYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg + facingOffset;
            Quaternion target = Quaternion.Euler(0f, targetYaw, 0f);
            rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, target, turnSpeed * Time.fixedDeltaTime));
        }

        // ---- 动画参数 ----
        anim.SetBool(MovingId, hasMove);

        // ---- 防御：按住右键举盾；攻击链期间不响应（链结束仍按着会自动举起） ----
        anim.SetBool(GuardId, input.GuardHeld && !IsAttackChain());

        // ---- 攻击输入：从缓冲消费点击；只有站立时才起手/续击，移动中直接丢弃（不缓存、不排队）----
        if (input.TakeAttackRequest())
        {
            if (CanStartAttack(hasMove))
            {
                anim.SetTrigger(AttackId);
            }
            else
            {
                anim.ResetTrigger(AttackId);    // 清掉残留的 pending trigger，避免停下后自动补一刀
            }
        }
    }

    /// 是否处于攻击链（A/A_Rec/B/B_Rec/C）——全程锁定移动与朝向；A_Rec/B_Rec 仅放行连击输入
    bool IsAttackChain()
    {
        int h = anim.GetCurrentAnimatorStateInfo(0).shortNameHash;
        return h == AH || h == ARecH || h == BH || h == BRecH || h == CH;
    }

    /// <summary>
    /// 扫掠命中：出刀状态的激活窗口内（normalizedTime 区间，跳过前摇/收势）每帧检测剑尖段胶囊（轴=剑尖段内端→剑尖，半径=bladeRadius）——
    /// ① 刃线（当前帧剑尖段是否压在敌人身上）；② 扫掠线（上一帧剑尖段沿挥扫方向扫过帧间距，防高速穿透漏检）。
    /// 只算剑尖这一段：判定体积贴近剑尖真实轨迹，命中时机与旧版零宽线一致，受击方向不会被"挥砍起势的前推分量"带偏。
    /// 命中方向取剑尖本帧的挥扫速度方向，攻击因此"有方向"：横扫往侧向、下劈带向下分量。
    /// 对同一敌人一次挥砍只结算一次。
    /// </summary>
    void SweepHit()
    {
        var st = anim.GetCurrentAnimatorStateInfo(0);
        int h = st.shortNameHash;
        bool swinging = (h == AH || h == BH || h == CH);

        if (!swinging || bladeTip == null)
        {
            lastSwingHash = 0;
            sweepInit = false;
            hitThisSwing.Clear();
            hitPropsThisSwing.Clear();
            return;
        }

        if (h != lastSwingHash)     // 新的一刀：清空已结算列表，重新采样扫掠起点
        {
            lastSwingHash = h;
            sweepInit = false;
            hitThisSwing.Clear();
            hitPropsThisSwing.Clear();
        }

        // 进度窗口：前摇/收势阶段剑尖轨迹不算攻击，窗口外不检测。
        // 回到窗口内的第一帧会因 sweepInit=false 重新以当前剑尖为扫掠起点，前摇轨迹不会被连成扫掠线。
        Vector2 w = h == AH ? windowA : h == BH ? windowB : windowC;
        if (st.normalizedTime < w.x || st.normalizedTime > w.y)
        {
            sweepInit = false;
            return;
        }

        Vector3 tipNow = bladeTip.position;
        Vector3 baseNow = bladeBase != null ? bladeBase.position : prevTip;
        Vector3 innerNow = TipInner(tipNow, baseNow);       // 剑尖段内端点（只取剑尖 tipLength 这一段）
        if (!sweepInit)
        {
            prevTip = tipNow;       // 首帧只记录起点，不做检测
            prevInner = innerNow;
            sweepInit = true;
            return;
        }

        Vector3 swingDir = tipNow - prevTip;
        if (swingDir.sqrMagnitude < 0.0001f)    // 本帧剑尖几乎没动时，退化为剑身指向
            swingDir = tipNow - (bladeBase != null ? bladeBase.position : prevTip);
        if (swingDir.sqrMagnitude < 0.0001f)
            swingDir = transform.forward;

        // 剑尖真实线速度（m/s）：帧间位移 / dt（dt 钳上下限，防卡顿帧算出荒谬速度）
        float dt = Mathf.Clamp(Time.deltaTime, 1f / 240f, 1f / 20f);
        Vector3 tipVel = (tipNow - prevTip) / dt;

        BladeOverlap(innerNow, tipNow, swingDir, tipVel);           // ① 刃线：当前帧剑尖段胶囊与敌人重叠
        BladeSweep(prevInner, prevTip, tipNow, swingDir, tipVel);   // ② 扫掠线：上一帧剑尖段沿挥扫方向扫过帧间距

        if (debugSweep)
        {
            Debug.DrawLine(innerNow, tipNow, Color.yellow, 0.1f);
            Debug.DrawLine(prevTip, tipNow, Color.red, 0.1f);
            DebugRadius(innerNow, bladeRadius, Color.yellow);
            DebugRadius(tipNow, bladeRadius, Color.red);
        }

        prevTip = tipNow;
        prevInner = innerNow;
    }

    /// 剑尖段内端点：从剑尖沿刃方向往剑根退 tipLength（不超过剑长，tipLength=0 时退化为剑尖点）
    Vector3 TipInner(Vector3 tipPos, Vector3 basePos)
    {
        Vector3 bladeDir = tipPos - basePos;
        float len = bladeDir.magnitude;
        if (len < 1e-5f) return tipPos;
        return tipPos - bladeDir / len * Mathf.Min(tipLength, len);
    }

    /// 用三轴十字线标出刃宽范围（Scene 视图调 bladeRadius 时的宽度可视化）
    void DebugRadius(Vector3 center, float r, Color c)
    {
        Debug.DrawLine(center + Vector3.right * r, center - Vector3.right * r, c, 0.1f);
        Debug.DrawLine(center + Vector3.up * r, center - Vector3.up * r, c, 0.1f);
        Debug.DrawLine(center + Vector3.forward * r, center - Vector3.forward * r, c, 0.1f);
    }

    /// 刃线检测：当前帧剑尖段以胶囊（轴=剑尖段内端→剑尖，半径=bladeRadius）与敌人重叠即命中
    void BladeOverlap(Vector3 p1, Vector3 p2, Vector3 swingDir, Vector3 tipVel)
    {
        var cols = Physics.OverlapCapsule(p1, p2, bladeRadius);
        for (int i = 0; i < cols.Length; i++)
            SettleHit(cols[i], swingDir, tipVel, cols[i].ClosestPoint(bladeTip.position));   // 重叠无命中点，取物体上离剑尖最近的点
    }

    /// 扫掠检测：上一帧剑尖段胶囊沿挥扫方向扫过帧间距（旋转扫掠以平移近似，帧间误差厘米级）。
    /// CapsuleCast 不报告初始重叠，起始帧的重叠由上一帧的刃线检测负责，帧间衔接无缝。
    void BladeSweep(Vector3 prevInner, Vector3 prevTip, Vector3 tipNow, Vector3 swingDir, Vector3 tipVel)
    {
        Vector3 move = tipNow - prevTip;
        if (move.sqrMagnitude < 1e-8f) return;
        var hits = Physics.CapsuleCastAll(prevInner, prevTip, bladeRadius, move, move.magnitude);
        for (int i = 0; i < hits.Length; i++)
            SettleHit(hits[i].collider, swingDir, tipVel, hits[i].point);   // cast 自带命中点
    }

    /// 命中结算：可互动物体（Prop 层）在接触点施加冲量；敌人按阵营/已死/本刀重复过滤后扣血。
    /// 主角/敌人/平台不在 Prop 层，走不到冲量分支，因此不受推力影响。
    void SettleHit(Collider col, Vector3 swingDir, Vector3 tipVel, Vector3 point)
    {
        if (PropImpulse.IsProp(col.gameObject, propLayer))
        {
            PropImpulse.Apply(col.attachedRigidbody, tipVel, bladeMass, maxSwingSpeed, point, hitPropsThisSwing);
            return;
        }

        var hp = col.GetComponentInParent<Health>();
        if (hp == null || hp.IsDead || hp.team != Health.Team.Enemy) return;
        if (!hitThisSwing.Add(hp)) return;    // 这个目标本刀已结算过
        hp.TakeDamage(attackDamage, swingDir);
    }

    /// 是否处于防御状态（举盾/收盾）——锁定移动与朝向；过渡期间按来源状态判定，仍然算防御中
    bool IsGuarding()
    {
        int h = anim.GetCurrentAnimatorStateInfo(0).shortNameHash;
        return h == RaiseH || h == LowerH;
    }

    /// 是否可起手/续击：由"是否有移动输入"定义站立，而不是状态名——
    /// 松开移动键即视为站立意图，Jog→Idle 过渡建立前后都放行，停步后立即可攻击。
    /// Idle 起手，A_Rec/B_Rec 连击窗口；跑动中（有移动输入）一律不响应。
    bool CanStartAttack(bool hasMove)
    {
        if (hasMove) return false;
        int h = anim.GetCurrentAnimatorStateInfo(0).shortNameHash;
        return h == IdleH || h == JogH || h == ARecH || h == BRecH;
    }

    static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.normalized;
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

    void ZeroHorizontalVelocity()
    {
        Vector3 v = rb.linearVelocity;
        if (v.x != 0f || v.z != 0f)
        {
            v.x = 0f;
            v.z = 0f;
            rb.linearVelocity = v;
        }
    }
}
