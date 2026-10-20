using UnityEngine;

/// <summary>
/// 敌人的弹反表现（配合 PlayerParry 使用）：
/// ① Parried —— 被弹反：Animator 时间缩放到 slowScale（慢 10 倍），播破防动画（复用主角的 Idle_Shield_Break）；
///    以现实时间等待玩家 parryHoldTime 秒，玩家点左键就进 ②，没点就直接恢复、无事发生。
/// ② Launched —— 被反击平推：时间恢复正常，沿玩家面朝方向贴地滑出 launchDistance 米
///    （速度只有水平分量、没有垂直分量，不再抛物线弹飞），播 Hit_Knockback；
///    动画速度自适应，滑停那一刻刚好播完。
/// ③ Landing —— 落地：播 LayToIdle，播完回到正常状态，AI 解锁。
/// 全程 IsLocked=true，EnemyAI 借此停手：不再移动、不再攻击、手掌不再结算伤害。
/// </summary>
[RequireComponent(typeof(Animator))]
public class EnemyStagger : MonoBehaviour
{
    public enum Phase { None, Parried, Launched, Landing }

    [Header("引用（留空自动查找）")]
    public Animator animator;
    public Rigidbody body;

    [Header("弹反硬直")]
    [Tooltip("弹反成功后敌人 Animator 的时间缩放：0.1 = 慢 10 倍（只影响它自己的动画，玩家时间照常）")]
    public float slowScale = 0.1f;
    [Tooltip("等待玩家反击的现实时间（秒，用 unscaledTime 计时，不受慢动作影响）")]
    public float parryHoldTime = 3f;

    [Header("平推")]
    [Tooltip("平推滑行的水平距离（米）")]
    public float launchDistance = 3f;
    [Tooltip("滑出这段距离所用的时长（秒）：平推速度 = 距离 / 时长")]
    public float launchTime = 0.8f;
    [Tooltip("撞飞动画 clip 的长度（秒，Hit_Knockback=0.833）：用来把动画速度对齐到滑行时长")]
    public float knockClipLength = 0.833f;
    [Tooltip("动画速度的自适应范围：滑停前把动画播完，别提前定格僵住")]
    public Vector2 knockSpeedRange = new Vector2(0.25f, 3f);
    [Tooltip("兜底：平推开始超过这么久仍未结束就强制进落地")]
    public float launchTimeout = 2.5f;
    [Tooltip("兜底：LayToIdle 播这么久还没播完就强行恢复")]
    public float landingTimeout = 3f;

    static readonly int ParriedId = Animator.StringToHash("Parried");
    static readonly int KnockId = Animator.StringToHash("Knock");
    static readonly int GroundedId = Animator.StringToHash("Grounded");
    static readonly int RecoverId = Animator.StringToHash("Recover");
    static readonly int KnockH = Animator.StringToHash("Knockback");
    static readonly int LayH = Animator.StringToHash("LayToIdle");

    Phase phase = Phase.None;
    float phaseStart;       // unscaled 计时起点
    Vector3 launchVelocity; // 平推的水平速度（滑行期间全程锁定，摩擦/穿透修正都偷不走）
    float slideTimer;       // 滑行剩余时长（按物理步扣，扣完即落地）
    bool savedGravity;      // 原本是否受重力（平推期间强制开，恢复时还原）

    public Phase Current { get { return phase; } }
    /// <summary>被弹反/撞飞/起身期间为 true：EnemyAI 据此完全停手。</summary>
    public bool IsLocked { get { return phase != Phase.None; } }

    void Awake()
    {
        if (animator == null) animator = GetComponent<Animator>();
        if (body == null) body = GetComponent<Rigidbody>();

        // 必须在这里记下原始重力状态：Recover 在"弹反后玩家没反击"这条路上也会被调用，
        // 而那时 Launch 还没跑过。若留默认 false，一恢复就把敌人重力关掉 —— 表现为敌人飘在空中不落地。
        if (body != null) savedGravity = body.useGravity;
    }

    /// <summary>被玩家弹反：进入慢动作硬直，开始用现实时间等玩家反击。</summary>
    public void EnterParry()
    {
        if (phase != Phase.None) return;

        phase = Phase.Parried;
        phaseStart = Time.unscaledTime;
        Stop();
        if (animator != null)
        {
            animator.speed = slowScale;
            animator.SetTrigger(ParriedId);
        }
    }

    /// <summary>玩家反击：时间恢复正常，沿 dir（玩家面朝方向）被平推出去。</summary>
    public void Launch(Vector3 dir)
    {
        if (phase != Phase.Parried) return;   // 只有在弹反硬直中才吃这一下

        phase = Phase.Launched;
        phaseStart = Time.unscaledTime;

        if (animator != null)
        {
            animator.speed = 1f;              // 时间恢复正常
            animator.SetTrigger(KnockId);
        }
        if (body != null)
        {
            savedGravity = body.useGravity;
            body.useGravity = true;           // 重力保留：贴着地面滑行
            body.angularVelocity = Vector3.zero;

            Vector3 flat = dir;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f) flat = transform.forward;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            flat.Normalize();

            // 平推：去掉速度的垂直分量，水平恒速滑出 launchDistance 米。
            // 弹反是贴身触发的，先沿滑行方向挪开一点，摆脱与玩家的初始重叠
            float slideTime = Mathf.Max(0.1f, launchTime);
            launchVelocity = flat * (launchDistance / slideTime);
            body.position += flat * 0.12f;
            slideTimer = slideTime;
            body.linearVelocity = new Vector3(launchVelocity.x, 0f, launchVelocity.z);
        }
    }

    void Update()
    {
        switch (phase)
        {
            case Phase.Parried:
                // 玩家没在 3 秒（现实时间）内反击 → 时间恢复正常，无事发生
                if (Time.unscaledTime - phaseStart >= parryHoldTime) Recover();
                break;

            case Phase.Launched:
                // 贴地平推：滑满时长（= 滑满 launchDistance）即落地，不再做抛物线落地判定
                // 动画速度跟着剩余滑行时间走：滑停那一刻刚好播完，不提前定格僵住
                if (animator != null)
                {
                    var st = animator.GetCurrentAnimatorStateInfo(0);
                    if (st.shortNameHash == KnockH)
                    {
                        float remainAnim = (1f - Mathf.Clamp01(st.normalizedTime)) * knockClipLength;
                        animator.speed = Mathf.Clamp(remainAnim / Mathf.Max(0.06f, slideTimer), knockSpeedRange.x, knockSpeedRange.y);
                    }
                }
                if (slideTimer <= 0f || Time.unscaledTime - phaseStart >= launchTimeout) Land();
                break;

            case Phase.Landing:
                bool done = false;
                if (animator != null)
                {
                    var st = animator.GetCurrentAnimatorStateInfo(0);
                    done = st.shortNameHash == LayH && st.normalizedTime >= 1f;
                }
                if (done || Time.unscaledTime - phaseStart >= landingTimeout) Recover();
                break;
        }
    }

    /// 落地：停住位移，播 LayToIdle
    void Land()
    {
        phase = Phase.Landing;
        phaseStart = Time.unscaledTime;
        Stop();
        if (animator != null)
        {
            animator.speed = 1f;              // 解除自适应速度
            animator.SetTrigger(GroundedId);
        }
    }

    /// 回到正常状态：时间恢复、AI 解锁（LayToIdle 有自己的 exitTime 过渡回 Idle）
    void Recover()
    {
        phase = Phase.None;
        if (animator != null)
        {
            animator.speed = 1f;
            animator.SetTrigger(RecoverId);   // 仍在 Break 状态时用它回 Idle
        }
        if (body != null)
        {
            body.useGravity = savedGravity;
            Stop();
        }
    }

    void FixedUpdate()
    {
        if (phase != Phase.Launched || body == null) return;

        // 全程锁定水平速度：摩擦、穿透修正都改变不了平推轨迹；竖直方向留给重力和地面
        body.linearVelocity = new Vector3(launchVelocity.x, body.linearVelocity.y, launchVelocity.z);
        slideTimer -= Time.fixedDeltaTime;
    }

    void Stop()
    {
        if (body == null) return;
        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
    }
}
