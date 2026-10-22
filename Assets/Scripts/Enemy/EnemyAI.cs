using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 敌人 AI：待机 → 发现玩家后朝玩家走 → 进入攻击距离停步并周期性攻击。
/// 位移由 Rigidbody 驱动，动画只负责表演（与主角一致：不使用 root motion）。
/// 攻击动画播放期间锁定移动，避免"边挥边滑"。
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Animator))]
public class EnemyAI : MonoBehaviour
{
    [Header("目标")]
    public Transform target;            // 留空则在 Awake 里按名字 Player 自动查找

    [Header("感知")]
    public float detectRange = 12f;     // 进入该距离开始追击
    public float attackRange = 2.2f;    // 进入该距离停下攻击（剑/锤够得到的距离）

    [Header("战斗")]
    public float attackInterval = 3f;   // 两次攻击的间隔（秒），从起手时刻开始计时
    public float attackDamage = 20f;    // 每次挥爪伤害
    public float moveSpeed = 2.6f;      // 追击速度（略慢于主角，给玩家走位空间）
    public float turnSpeed = 360f;      // deg/s，转向玩家的角速度

    [Header("扫掠检测（手掌轨迹）")]
    public Transform handL;             // 左手掌骨（留空则自动查找 hand_l）
    public Transform handR;             // 右手掌骨（留空则自动查找 hand_r）
    [Range(0.01f, 0.3f)] public float clawRadius = 0.08f;   // 爪击扫掠半径（球体，即判定容错度）
    public bool debugSweep = false;     // 在 Scene 视图画出手掌扫掠检测线

    [Header("击飞可互动物体（Prop 层：箱子等）")]
    public LayerMask propLayer;         // 可互动物体所在层（Inspector 选 Prop）；主角/敌人/平台不在该层，天然不受影响
    public float clawMass = 2.5f;       // 爪的有效质量：冲量 = 手掌速度(m/s) × 该质量(kg)
    public float maxClawSpeed = 12f;    // 手掌速度上限（m/s）

    [Header("朝向")]
    public float facingOffset = 0f;     // 模型正面与 transform.forward 的夹角；若角色倒着走改成 180

    [Header("弹反")]
    [Tooltip("可弹反窗口的起点（动画进度）：前摇（抬手）结束、爪子开始挥出的那一瞬。\nZombie_Scratch 实测：手掌速度峰值在 0.30~0.34，0.26 之前是抬手")]
    [Range(0f, 1f)] public float parryWindowStart = 0.22f;
    [Tooltip("可弹反窗口的终点（动画进度）：刻意放宽到挥击段之后 —— 真正的终点是\"爪子打到主角\"那一瞬，\n由 playerHitDone 在命中时自动切断（见 IsInParryWindow），命中之后按防御一律不算弹反。\n实测 1.2m 距离下命中在第 18.5 帧(0.343)，所以有效窗口 ≈ 第 12~18.5 帧")]
    [Range(0f, 1f)] public float parryWindowEnd = 0.45f;

    Rigidbody rb;
    Animator anim;
    EnemyStagger stagger;

    float nextAttackTime;
    Vector3 prevHandL, prevHandR;   // 上一渲染帧两手掌位置（扫掠起点）
    bool sweepInit;                 // 扫掠起点是否已采样
    bool playerHitDone;             // 本次挥爪是否已结算（对玩家只结算一次）
    readonly HashSet<Rigidbody> hitPropsThisSwipe = new HashSet<Rigidbody>();  // 本次挥爪已推过的可互动物体（一次挥爪只推一次）

    static readonly int MovingId = Animator.StringToHash("Moving");
    static readonly int AttackId = Animator.StringToHash("Attack");
    static readonly int AttackStateH = Animator.StringToHash("Attack");

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        anim = GetComponent<Animator>();
        if (target == null)
        {
            var p = GameObject.Find("Player");
            if (p != null) target = p.transform;
        }
        if (handL == null) handL = FindDeep(transform, "hand_l");
        if (handR == null) handR = FindDeep(transform, "hand_r");
        if (stagger == null) stagger = GetComponent<EnemyStagger>();
    }

    /// <summary>
    /// 是否处于可弹反的"攻击期间"：正在播攻击动画，且落在 [parryWindowStart, parryWindowEnd] 这段
    /// 真正的挥击区间里 —— 前摇（抬手）和后摇（收势）都不算。
    /// 只有这段时间里玩家按下防御才判定弹反 —— 手掌扫掠之外的伤害源不参与。
    /// </summary>
    public bool IsInParryWindow()
    {
        if (stagger != null && stagger.IsLocked) return false;
        // 这一击已经打到主角、伤害也结算了 —— 挨完打再按防御不算弹反（弹反只能在挨打之前）。
        // playerHitDone 由 SweepHands 的结算置位，非攻击状态/被弹反时自动复位，下一击重新可弹反。
        if (playerHitDone) return false;

        var st = anim.GetCurrentAnimatorStateInfo(0);
        if (st.shortNameHash != AttackStateH) return false;
        float t = st.normalizedTime;
        return t >= parryWindowStart && t <= parryWindowEnd;
    }

    /// <summary>这一击够得到目标的距离上限（弹反判定用）：超出这个距离，它的爪子打不到你，也就不该弹反。</summary>
    public float AttackRange { get { return attackRange; } }

    void Update()
    {
        SweepHands();   // 扫掠检测跟动画帧走（Animator 在 Update 驱动），手掌采样按渲染帧稳定推进
    }

    void FixedUpdate()
    {
        if (target == null) return;

        // 被弹反/撞飞/起身期间：完全停手（不走、不转身、不攻击）。
        // 这里不能 Stop()：撞飞阶段位移来自 EnemyStagger 给的初速度，每帧清零会把人钉在原地。
        // 但角速度要清：盾牌这类高速钝器砸中根刚体会留下角速度，硬直期间没人 FaceTo，
        // 而根刚体 angularDrag=0 不衰减，不清理就会原地自转。
        if (stagger != null && stagger.IsLocked)
        {
            rb.angularVelocity = Vector3.zero;
            anim.SetBool(MovingId, false);
            return;
        }

        Vector3 toTarget = target.position - transform.position;
        toTarget.y = 0f;
        float dist = toTarget.magnitude;

        // 攻击动画播放中：锁住移动，只保持面向玩家
        if (IsAttacking())
        {
            Stop();
            FaceTo(toTarget);
            anim.SetBool(MovingId, false);
            return;
        }

        if (dist <= attackRange)
        {
            // 进入攻击距离：停步，按间隔攻击
            Stop();
            FaceTo(toTarget);
            anim.SetBool(MovingId, false);
            if (Time.time >= nextAttackTime)
            {
                anim.SetTrigger(AttackId);
                playerHitDone = false;      // 新的一击起手：重新可弹反（不单靠 SweepHands 复位，防止动画直接 Attack→Attack 时把弹反锁死）
                nextAttackTime = Time.time + attackInterval;
            }
        }
        else if (dist <= detectRange)
        {
            // 发现玩家：边转向边追
            FaceTo(toTarget);
            Move(toTarget.normalized);
            anim.SetBool(MovingId, true);
        }
        else
        {
            // 超出感知：原地待机。
            // 角速度必须清：玩家走远后扔盾砸中根刚体会留下角速度，这里没人 FaceTo（感知范围外不转向），
            // 不清理就会一直原地自转。只清角速度不动朝向 —— 待机时保持被打之前面向的方向。
            Stop();
            rb.angularVelocity = Vector3.zero;
            anim.SetBool(MovingId, false);
        }
    }

    /// <summary>
    /// 手掌扫掠命中：攻击状态下每帧检测两手掌"上一帧→当前帧"的带宽度扫掠（球体半径 clawRadius），
    /// 手掌轨迹真正扫到主角才结算（抬手阶段天然不判定），方向取手掌挥扫速度方向。
    /// 对主角一次挥爪只结算一次。
    /// </summary>
    void SweepHands()
    {
        if (stagger != null && stagger.IsLocked)   // 被弹反后这一击不再结算伤害
        {
            sweepInit = false;
            playerHitDone = false;
            hitPropsThisSwipe.Clear();
            return;
        }

        if (target == null || (!IsAttacking()) || (handL == null && handR == null))
        {
            sweepInit = false;
            playerHitDone = false;
            hitPropsThisSwipe.Clear();
            return;
        }

        if (!sweepInit)
        {
            if (handL != null) prevHandL = handL.position;
            if (handR != null) prevHandR = handR.position;
            sweepInit = true;
            return;
        }

        SweepHand(handL, ref prevHandL);
        SweepHand(handR, ref prevHandR);
    }

    /// 单只手掌的带宽度扫掠检测（球体近似爪击体积）：命中玩家方 Health 结算一次，命中 Prop 层箱子施加冲量
    void SweepHand(Transform hand, ref Vector3 prev)
    {
        if (hand == null) return;

        Vector3 now = hand.position;
        Vector3 swingDir = now - prev;

        if (swingDir.sqrMagnitude > 0.0001f)
        {
            // 手掌真实线速度（m/s）：帧间位移 / dt（dt 钳上下限，防卡顿帧算出荒谬速度）
            float dt = Mathf.Clamp(Time.deltaTime, 1f / 240f, 1f / 20f);
            Vector3 handVel = swingDir / dt;

            // ① 起点球重叠：挥爪瞬间爪子已贴在目标身上（SphereCast 不报告初始重叠，需单独补；
            //    上一帧的终点重叠也由此处衔接覆盖）
            var cols = Physics.OverlapSphere(prev, clawRadius);
            for (int i = 0; i < cols.Length; i++)
                SettleHit(cols[i], swingDir, handVel, cols[i].SafeClosestPoint(now));

            // ② 扫掠球：上一帧→当前帧爪子扫过的路径（cast 自带命中点）
            var hits = Physics.SphereCastAll(prev, clawRadius, swingDir, swingDir.magnitude);
            for (int i = 0; i < hits.Length; i++)
                SettleHit(hits[i].collider, swingDir, handVel, hits[i].point);

            if (playerHitDone && debugSweep) Debug.DrawLine(prev, now, Color.green, 1f);
        }

        if (debugSweep && swingDir.sqrMagnitude > 0.0001f)
            Debug.DrawLine(prev, now, Color.magenta, 0.1f);

        prev = now;
    }

    /// 命中结算：可互动物体（Prop 层）在接触点施加冲量（不受 playerHitDone 影响，箱子照样被推）；
    /// 玩家方 Health 按阵营/已死/本次挥爪重复过滤后扣血。主角/敌人/平台不在 Prop 层，走不到冲量分支。
    void SettleHit(Collider col, Vector3 swingDir, Vector3 handVel, Vector3 point)
    {
        if (PropImpulse.IsProp(col.gameObject, propLayer))
        {
            PropImpulse.Apply(col.attachedRigidbody, handVel, clawMass, maxClawSpeed, point, hitPropsThisSwipe);
            return;
        }

        if (playerHitDone) return;
        var hp = col.GetComponentInParent<Health>();
        if (hp == null || hp.IsDead || hp.team != Health.Team.Player) return;
        playerHitDone = true;
        hp.TakeDamage(attackDamage, swingDir, PropImpulse.Magnitude(handVel, clawMass), point);   // 冲量决定晃动幅度，命中点决定布娃娃飞散姿态
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

    /// 是否正在播放攻击动画（含 Attack→Idle/Walk 的过渡，仍按源状态判定）
    bool IsAttacking()
    {
        return anim.GetCurrentAnimatorStateInfo(0).shortNameHash == AttackStateH;
    }

    void FaceTo(Vector3 toTarget)
    {
        if (toTarget.sqrMagnitude < 0.0001f) return;
        float yaw = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg + facingOffset;
        Quaternion want = Quaternion.Euler(0f, yaw, 0f);
        rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, want, turnSpeed * Time.fixedDeltaTime));
        rb.angularVelocity = Vector3.zero;
    }

    void Move(Vector3 dir)
    {
        Vector3 v = rb.linearVelocity;
        v.x = dir.x * moveSpeed;
        v.z = dir.z * moveSpeed;
        rb.linearVelocity = v;
    }

    void Stop()
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
