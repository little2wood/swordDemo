using UnityEngine;

/// <summary>
/// 玩家侧弹反：敌人处于"攻击期间"（Attack 动画前摇之后）时按下右键进入防御 → 弹反成功。
/// 弹反后玩家被定住 parryHoldTime 秒（现实时间，只有敌人的时间被放慢，玩家时间照常流逝），
/// 期间不能移动，按左键即触发反击：播 Shield_Dash，同时把敌人沿玩家面朝方向撞飞并扣血。
/// 定住期间不点左键 → 时间到自动结束，敌人恢复正常，无事发生。
/// 只对敌人的手掌攻击生效（判定看 EnemyAI.IsInParryWindow），其它伤害源不参与。
/// </summary>
public class PlayerParry : MonoBehaviour
{
    [Header("引用（留空自动查找）")]
    public Animator animator;

    [Header("弹反")]
    [Tooltip("粗筛：超过这个距离直接不考虑（米）")]
    public float parryRange = 3.5f;
    [Tooltip("真正的判定距离 = 敌人的攻击范围（EnemyAI.attackRange）+ 这点边界余量。\n站在这个范围外它的爪子根本够不到你，不允许弹反")]
    public float rangeTolerance = 0.15f;
    [Tooltip("弹反有效的朝向范围（半角，deg）：攻击者必须落在角色正面这个扇区内才允许弹反，\n与格挡（PlayerGuard.blockArcDeg）同一口径 —— 默认 120 = 正面 ±120°，只有背后 ±60° 的攻击弹不了")]
    public float parryArcDeg = 120f;
    [Tooltip("弹反成功后玩家被定住的现实时间（秒）：期间不能移动，只能按左键反击")]
    public float holdTime = 3f;
    [Tooltip("反击动画时长（Shield_Dash ≈ 1.1s）：期间同样锁住移动")]
    public float riposteTime = 1.1f;

    [Header("反击前冲（撞上去的视觉）")]
    [Tooltip("前冲距离上限（米）：只是防止极端距离下一帧滑过去，正常由\"吸到贴身\"决定")]
    public float dashDistance = 3.5f;
    [Tooltip("前冲速度（m/s）：冲多远由贴身距离算，耗时 = 距离 / 速度")]
    public float dashSpeed = 6f;
    [Tooltip("前冲最长持续（秒）：兜底，防止远距离时冲太久")]
    public float maxDashTime = 0.6f;
    [Tooltip("贴身判定：吸到\"双方碰撞体半径之和 − 这个重叠量\"就停（米）。\n0=两个人的身体表面刚好相贴；调大=再往里挤一点（物理会把双方推开一点，撞击更实）")]
    public float contactOverlap = 0.08f;
    [Tooltip("反击瞬间把角色转向敌人（撞击方向才对得上）")]
    public bool faceTargetOnRiposte = true;
    [Tooltip("反击瞬间相机拉近多少米：相机平时是硬跟随，只靠角色前移在画面上看不出冲劲，靠这一下把撞击演出来")]
    public float punchZoom = 1.2f;

    [Header("反击伤害")]
    [Tooltip("反击命中时对敌人造成的伤害：默认与普攻一刀相同（34）。60 血的小怪弹两下收走。\n打死了就不走撞飞硬直，改为布娃娃按致死冲量飞出去")]
    public float riposteDamage = 34f;

    static readonly int ParryId = Animator.StringToHash("Parry");

    bool holding;
    bool riposted;
    float holdTimer;
    EnemyStagger target;
    Rigidbody body;
    ThirdPersonCamera cam;

    // 前冲状态：由 Riposte 起算，速度直接写进刚体（PlayerController 在这段时间内不接管水平速度）
    bool dashing;
    float dashTimer;
    float dashDuration;
    Vector3 dashDir;
    float dashVelocity;
    bool pendingLaunch;     // 前冲撞上去之后才把敌人打飞（先接触、后击飞）

    /// <summary>是否处于弹反定身（不能移动，按左键触发反击）。</summary>
    public bool IsHolding { get { return holding; } }
    /// <summary>本次反击是否已打出（定身期间只放一次）。</summary>
    public bool Riposted { get { return riposted; } }
    /// <summary>反击前冲进行中：这段时间内水平速度归 PlayerParry 管。</summary>
    public bool IsDashing { get { return dashing; } }

    void Awake()
    {
        if (animator == null) animator = GetComponentInParent<Animator>();
        if (body == null) body = GetComponent<Rigidbody>();
        if (body == null) body = GetComponentInParent<Rigidbody>();
        if (cam == null && Camera.main != null) cam = Camera.main.GetComponent<ThirdPersonCamera>();
    }

    /// <summary>
    /// 右键按下的那一帧调用：找正在攻击（且在弹反窗口内）的最近敌人 → 弹反。
    /// 返回是否弹反成功。
    /// </summary>
    public bool TryParry()
    {
        if (holding) return false;

        EnemyStagger best = null;
        float bestD = float.MaxValue;

        var list = Object.FindObjectsByType<EnemyAI>(FindObjectsSortMode.None);
        for (int i = 0; i < list.Length; i++)
        {
            var ai = list[i];
            if (ai == null || !ai.IsInParryWindow()) continue;

            var st = ai.GetComponent<EnemyStagger>();
            if (st == null || st.IsLocked) continue;

            // 水平距离：与 EnemyAI 的追击/攻击判定保持同一套口径
            Vector3 flat = ai.transform.position - transform.position;
            flat.y = 0f;
            float d = flat.magnitude;
            if (d > parryRange || d >= bestD) continue;

            // 朝向：攻击者必须在正面扇区内（与格挡同口径）—— 背后袭来的爪击不配被弹
            if (!FacingEnemy(flat)) continue;

            // 必须在它这一击够得到的范围内：站得太远它打不到你，也就不该弹反
            if (d > ai.AttackRange + rangeTolerance) continue;

            bestD = d;
            best = st;
        }

        if (best == null) return false;

        target = best;
        best.EnterParry();

        holding = true;
        riposted = false;
        holdTimer = holdTime;
        return true;
    }

    /// <summary>弹反定身期间的左键：打出反击（一局只放一次）。</summary>
    public void Riposte()
    {
        if (!holding || riposted) return;

        riposted = true;
        holdTimer = riposteTime;        // 反击动画期间继续锁住移动

        // 撞击前先把身体转向敌人：这一下是"撞过去"，朝向和前冲方向得一致
        if (faceTargetOnRiposte && target != null && body != null)
        {
            Vector3 to = target.transform.position - transform.position;
            to.y = 0f;
            if (to.sqrMagnitude > 1e-6f)
                body.MoveRotation(Quaternion.Euler(0f, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg, 0f));
        }

        pendingLaunch = true;
        StartDash();
        if (!dashing) LaunchTarget();   // 已经贴身就不用冲，直接打飞

        // 撞上去那一下的画面冲击（相机 Awake 阶段可能还没就绪，这里用到时再取一次）
        if (cam == null && Camera.main != null) cam = Camera.main.GetComponent<ThirdPersonCamera>();
        if (cam != null) cam.PunchZoom(punchZoom);

        if (animator != null)
        {
            // 强制切过去：此时 Guard 仍是 true，走 trigger 的话 Idle→Shield_Raise 会抢在
            // AnyState→Shield_Dash 前面（具体状态过渡优先级更高），反击动画就播不出来
            animator.ResetTrigger(ParryId);
            animator.CrossFade("Shield_Dash", 0.08f, 0, 0f);
        }
    }

    /// <summary>
    /// 起算前冲：不是冲固定距离，而是"吸到贴身" —— 目标间距按双方碰撞体半径之和算，
    /// 冲完刚好身体表面相贴（盾牌够得到敌人），已经贴身则一点都不冲。
    /// dashDistance 只是上限，防止从很远的地方一帧滑过去。
    /// </summary>
    void StartDash()
    {
        if (target == null) { dashing = false; return; }

        Vector3 to = target.transform.position - transform.position;
        to.y = 0f;
        float d = to.magnitude;
        if (d > 1e-4f) dashDir = to / d;
        else dashDir = transform.forward;

        dashDir.y = 0f;
        if (dashDir.sqrMagnitude > 1e-6f) dashDir.Normalize();

        float gap = RadiusOf(GetComponent<Collider>()) + RadiusOf(target.GetComponent<Collider>()) - contactOverlap;
        float want = d - gap;
        if (want <= 0.03f) { dashing = false; return; }     // 已经贴身，没必要再动

        float dist = Mathf.Min(want, dashDistance);
        dashDuration = Mathf.Min(dist / Mathf.Max(0.1f, dashSpeed), maxDashTime);
        dashVelocity = dist / dashDuration;
        dashTimer = 0f;
        dashing = true;
    }

    /// 攻击者是否在弹反扇区内：与格挡（PlayerGuard.FacingAttack）同一口径 ——
    /// 攻击者方位（我 → 它）与角色正面的水平夹角 ≤ parryArcDeg；退化（垂直方向）时放行，同格挡
    bool FacingEnemy(Vector3 toEnemy)
    {
        Vector3 f = transform.forward; f.y = 0f;
        toEnemy.y = 0f;
        if (f.sqrMagnitude < 1e-6f || toEnemy.sqrMagnitude < 1e-6f) return true;
        return Vector3.Dot(f.normalized, toEnemy.normalized) >= Mathf.Cos(Mathf.Clamp(parryArcDeg, 0f, 180f) * Mathf.Deg2Rad);
    }

    /// 碰撞体的水平半径（胶囊/球/盒），没有碰撞体时按成年人身材兜底
    static float RadiusOf(Collider c)
    {
        if (c == null) return 0.4f;
        float s = Mathf.Abs(c.transform.lossyScale.x);
        if (s < 0.0001f) s = 0.0001f;

        var cap = c as CapsuleCollider;
        if (cap != null) return cap.radius * s;
        var sph = c as SphereCollider;
        if (sph != null) return sph.radius * s;
        var box = c as BoxCollider;
        if (box != null) return Mathf.Min(box.size.x, box.size.z) * 0.5f * s;
        return 0.4f;
    }

    void FixedUpdate()
    {
        if (!dashing || body == null) return;

        dashTimer += Time.fixedDeltaTime;
        if (dashTimer >= dashDuration)
        {
            dashing = false;        // 冲完停住（后续由 PlayerController 的定身锁定接管）
            var v0 = body.linearVelocity;
            v0.x = 0f;
            v0.z = 0f;
            body.linearVelocity = v0;
            if (pendingLaunch) LaunchTarget();   // 撞到了才打飞
            return;
        }

        var v = body.linearVelocity;    // 只接管水平速度，竖直交给重力
        v.x = dashDir.x * dashVelocity;
        v.z = dashDir.z * dashVelocity;
        body.linearVelocity = v;
    }

    /// 把敌人打飞（前冲撞上去之后才调用）：命中瞬间结算反击伤害
    void LaunchTarget()
    {
        pendingLaunch = false;
        if (target == null) return;

        // 反击这一下是真伤害（与普攻同档，Inspector 可调）。
        // 打死了就不再走撞飞硬直 —— 布娃娃按致死冲量（0 = 满档）沿击飞方向飞出去
        var hp = target.GetComponent<Health>();
        if (hp != null && hp.team == Health.Team.Enemy && !hp.IsDead)
        {
            hp.TakeDamage(riposteDamage, transform.forward, 0f, Vector3.zero);
            if (hp.IsDead) return;
        }

        target.Launch(transform.forward);   // 沿玩家面朝方向
    }

    void Update()
    {
        if (!holding) return;

        // 现实时间计时：不受敌人慢动作影响
        holdTimer -= Time.unscaledDeltaTime;
        if (holdTimer <= 0f)
        {
            holding = false;
            riposted = false;
            target = null;
        }
    }
}
