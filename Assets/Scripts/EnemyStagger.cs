using UnityEngine;

/// <summary>
/// 敌人的弹反表现（配合 PlayerParry 使用）：
/// ① Parried —— 被弹反：Animator 时间缩放到 slowScale（慢 10 倍），播破防动画（复用主角的 Idle_Shield_Break）；
///    以现实时间等待玩家 parryHoldTime 秒，玩家点左键就进 ②，没点就直接恢复、无事发生。
/// ② Launched —— 被反击撞飞：时间恢复正常，沿玩家面朝方向抛出 launchDistance 米，播 Hit_Knockback；
///    动画播完还没落地就定格在最后一帧。
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

    [Header("撞飞")]
    [Tooltip("撞飞的水平距离（米）")]
    public float launchDistance = 3f;
    [Tooltip("撞飞抛物线的最高点比起飞点高多少（米）")]
    public float launchArcHeight = 0.8f;
    [Tooltip("落地检测：脚底离地这么多米以内就算落地（米）。调大=提前落地、飞得近")]
    public float groundProbe = 0.12f;
    public LayerMask groundLayer = ~0;
    [Tooltip("兜底：起飞后超过这么久无论如何算落地（防射线探空）")]
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
    float startY;           // 起飞高度：落地判定用
    bool savedGravity;      // 原本是否受重力（撞飞期间强制开，恢复时还原）

    public Phase Current { get { return phase; } }
    /// <summary>被弹反/撞飞/起身期间为 true：EnemyAI 据此完全停手。</summary>
    public bool IsLocked { get { return phase != Phase.None; } }

    void Awake()
    {
        if (animator == null) animator = GetComponent<Animator>();
        if (body == null) body = GetComponent<Rigidbody>();
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

    /// <summary>玩家反击：时间恢复正常，沿 dir（玩家面朝方向）被撞飞。</summary>
    public void Launch(Vector3 dir)
    {
        if (phase != Phase.Parried) return;   // 只有在弹反硬直中才吃这一下

        phase = Phase.Launched;
        phaseStart = Time.unscaledTime;
        startY = transform.position.y;

        if (animator != null)
        {
            animator.speed = 1f;              // 时间恢复正常
            animator.SetTrigger(KnockId);
        }
        if (body != null)
        {
            savedGravity = body.useGravity;
            body.useGravity = true;           // 抛物线交给物理，落地由射线判定
            body.angularVelocity = Vector3.zero;

            Vector3 flat = dir;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f) flat = transform.forward;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            flat.Normalize();

            // 同高抛物线：弧高定 vUp → 飞行时间 T → 水平速度 = 距离 / T（落点即 launchDistance）
            float g = Mathf.Max(0.01f, Physics.gravity.magnitude);
            float vUp = Mathf.Sqrt(2f * g * Mathf.Max(0.01f, launchArcHeight));
            float t = 2f * vUp / g;
            body.linearVelocity = flat * (launchDistance / t) + Vector3.up * vUp;
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
                // 撞飞动画播完还没落地 → 定格在最后一帧（把 Animator 时间停掉）
                if (animator != null)
                {
                    var st = animator.GetCurrentAnimatorStateInfo(0);
                    if (st.shortNameHash == KnockH && st.normalizedTime >= 1f) animator.speed = 0f;
                }
                if (ReachedGround() || Time.unscaledTime - phaseStart >= launchTimeout) Land();
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
            animator.speed = 1f;              // 解除可能的定格
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

    /// 落地判定：正在下落 +（探到地面 或 已回到起飞高度）
    bool ReachedGround()
    {
        if (body == null) return true;
        if (body.linearVelocity.y > 0f) return false;      // 还在上升

        Vector3 origin = transform.position + Vector3.up * 0.15f;
        var hits = Physics.RaycastAll(origin, Vector3.down, groundProbe + 0.15f, groundLayer);
        for (int i = 0; i < hits.Length; i++)
        {
            if (IsSelf(hits[i].transform)) continue;
            return true;
        }
        return transform.position.y <= startY + 0.02f;     // 射线探空时按起飞高度兜底
    }

    bool IsSelf(Transform t)
    {
        return t == transform || t.IsChildOf(transform);
    }

    void Stop()
    {
        if (body == null) return;
        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
    }
}
