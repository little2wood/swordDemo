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
    [Tooltip("可弹反窗口的起点（动画进度）：前摇（抬手）结束、爪子开始挥出的那一瞬。\n同一条界线也用作\"前摇不结算命中\"的分界 —— 抬手阶段爪子压到主角也不伤人。\nZombie_Scratch 实测：手掌速度峰值在 0.30~0.34，0.26 之前是抬手")]
    [Range(0f, 1f)] public float parryWindowStart = 0.22f;
    [Tooltip("可弹反窗口的终点（动画进度）：刻意放宽到挥击段之后 —— 真正的终点是\"爪子打到主角\"那一瞬，\n由 resolver.HasHitTarget 在命中时自动切断（见 IsInParryWindow），命中之后按防御一律不算弹反。\n实测 1.2m 距离下命中在第 18.5 帧(0.343)，所以有效窗口 ≈ 第 12~18.5 帧")]
    [Range(0f, 1f)] public float parryWindowEnd = 0.45f;

    Rigidbody rb;
    Animator anim;
    EnemyStagger stagger;

    float nextAttackTime;
    bool spawnChase;    // 出生直追：生成点在 spawnDistance(16m) > detectRange(12m)，出生时在感知圈外，
                        // 没有它敌人会在原地永远待机 —— 出生时知道主角位置，追进感知圈后交还正常感知（跑远可甩开）
    // 扫掠检测与命中结算：与主角挥砍、盾牌共用 SweepProbe / MeleeHitResolver
    readonly SweepProbe probeL = new SweepProbe();   // 左手掌
    readonly SweepProbe probeR = new SweepProbe();   // 右手掌
    readonly MeleeHitResolver resolver = new MeleeHitResolver();
    readonly List<SweepProbe.Hit> sweepHits = new List<SweepProbe.Hit>();
    MeleeHitResolver.Policy clawPolicy;   // 每帧从 Inspector 字段刷新（Play 模式调参即时生效）

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
        if (handL == null) handL = TransformUtil.FindDeep(transform, "hand_l");
        if (handR == null) handR = TransformUtil.FindDeep(transform, "hand_r");
        if (stagger == null) stagger = GetComponent<EnemyStagger>();
    }

    /// <summary>
    /// 是否处于可弹反的"攻击期间"：正在播攻击动画，且落在 [parryWindowStart, parryWindowEnd] 这段
    /// 真正的挥击区间里 —— 前摇（抬手）和后摇（收势）都不算。
    /// 抬手阶段 UpdateSweep 不结算命中（爪子还没挥出来，压到人不伤人，见 PastWindup），
    /// 所以 HasHitTarget 最早也要到窗口开启之后才可能置位 —— 窗口不会一开就被命中关掉。
    /// 只有这段时间里玩家按下防御才判定弹反 —— 手掌扫掠之外的伤害源不参与。
    /// </summary>
    public bool IsInParryWindow()
    {
        if (stagger != null && stagger.IsLocked) return false;
        // 这一击已经打到主角、伤害也结算了 —— 挨完打再按防御不算弹反（弹反只能在挨打之前）。
        // HasHitTarget 由手掌扫掠的结算置位（resolver.Begin 复位），非攻击状态/被弹反/新的一击起手时清掉，下一击重新可弹反。
        if (resolver.HasHitTarget) return false;

        var st = anim.GetCurrentAnimatorStateInfo(0);
        if (st.shortNameHash != AttackStateH) return false;
        float t = st.normalizedTime;
        return t >= parryWindowStart && t <= parryWindowEnd;
    }

    /// <summary>这一击够得到目标的距离上限（弹反判定用）：超出这个距离，它的爪子打不到你，也就不该弹反。</summary>
    public float AttackRange { get { return attackRange; } }

    /// <summary>
    /// 出生警觉：由刷怪方在 Instantiate 后调用。敌人出生在感知范围外（spawnDistance 16m &gt; detectRange 12m），
    /// 不调用它敌人会在原地永远待机 —— 调用后敌人直追主角，追进感知圈后交还正常感知。
    /// </summary>
    public void Alert()
    {
        spawnChase = true;
    }

    void Update()
    {
        UpdateSweep();  // 扫掠检测跟动画帧走（Animator 在 Update 驱动），手掌采样按渲染帧稳定推进
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
                // 新的一击起手：清空"已命中玩家/已推箱子"标记，重新可弹反
                // （不单靠 UpdateSweep 的退出路径复位，防止动画直接 Attack→Attack 时把弹反锁死）
                resolver.Begin();
                nextAttackTime = Time.time + attackInterval;
            }
        }
        else if (dist <= detectRange || spawnChase)
        {
            // 发现玩家：边转向边追。（spawnChase = 出生直追，追进感知圈后交还正常感知）
            if (dist <= detectRange) spawnChase = false;
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
    /// 检测与结算由 SweepProbe / MeleeHitResolver 完成（与主角挥砍、盾牌共用）。
    /// 对主角一次挥爪只结算一次（resolver.HasHitTarget，弹反窗口读同一个标记）。
    /// </summary>
    void UpdateSweep()
    {
        // 被弹反后这一击不再结算伤害；非攻击状态重置采样与去重
        if ((stagger != null && stagger.IsLocked)
            || target == null || (!IsAttacking()) || (handL == null && handR == null))
        {
            probeL.Reset();
            probeR.Reset();
            resolver.Begin();
            return;
        }

        // 结算策略每帧从 Inspector 字段刷新（Play 模式调参即时生效）
        clawPolicy.propLayer = propLayer;
        clawPolicy.mass = clawMass;
        clawPolicy.maxSpeed = maxClawSpeed;
        clawPolicy.damage = attackDamage;
        clawPolicy.targetTeam = Health.Team.Player;
        clawPolicy.propApplyImpulse = true;   // 箱子由冲量击飞（敌人爪没有主角剑的 BladeCollider 物理碰撞）
        clawPolicy.onStone = null;            // 爪子打石头无特殊反馈
        clawPolicy.onFlesh = null;

        // 抬手（前摇）阶段不结算：爪子还没挥出来，压到人不该伤人，
        // 也更不该让"已命中"标记抢在弹反窗口开启之前生效（那一击就再也弹不了了，见 IsInParryWindow）
        bool settle = PastWindup();
        if (handL != null) ProbeHand(probeL, handL, settle); else probeL.Reset();
        if (handR != null) ProbeHand(probeR, handR, settle); else probeR.Reset();
    }

    /// 挥击是否已开始（动画进度 ≥ parryWindowStart，与弹反窗口同一条界线）：之前是抬手，爪子还没挥出来
    bool PastWindup()
    {
        var st = anim.GetCurrentAnimatorStateInfo(0);
        return st.normalizedTime >= parryWindowStart;
    }

    /// 单只手掌的带宽度扫掠检测（球体近似爪击体积）：命中玩家方 Health 结算一次，命中 Prop 层箱子施加冲量。
    /// settle=false（抬手阶段）时只推进扫掠采样、不结算 ——
    /// 采样必须照常推进，否则进入挥击段时扫掠起点会跳变，前摇到挥击之间会连出一条超长扫掠线，命中范围凭空变大。
    void ProbeHand(SweepProbe probe, Transform hand, bool settle)
    {
        Vector3 now = hand.position;
        probe.Probe(now, now, clawRadius, Vector3.zero, sweepHits);   // 球体 = 胶囊两端重合
        if (settle)
        {
            for (int i = 0; i < sweepHits.Count; i++)
            {
                var hit = sweepHits[i];
                resolver.Resolve(hit.collider, hit.sweepDir, hit.velocity, hit.point, clawPolicy);
            }
        }

        if (debugSweep && probe.ProbedLast)
            Debug.DrawLine(probe.prevB, now, resolver.HasHitTarget ? Color.green : Color.magenta,
                           resolver.HasHitTarget ? 1f : 0.1f);
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
