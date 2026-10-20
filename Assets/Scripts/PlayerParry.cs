using UnityEngine;

/// <summary>
/// 玩家侧弹反：敌人处于"攻击期间"（Attack 动画前摇之后）时按下右键进入防御 → 弹反成功。
/// 弹反后玩家被定住 parryHoldTime 秒（现实时间，只有敌人的时间被放慢，玩家时间照常流逝），
/// 期间不能移动，按左键即触发反击：播 Shield_Dash，同时把敌人沿玩家面朝方向撞飞。
/// 定住期间不点左键 → 时间到自动结束，敌人恢复正常，无事发生。
/// 只对敌人的手掌攻击生效（判定看 EnemyAI.IsInParryWindow），其它伤害源不参与。
/// </summary>
public class PlayerParry : MonoBehaviour
{
    [Header("引用（留空自动查找）")]
    public Animator animator;

    [Header("弹反")]
    [Tooltip("只对这么多米以内、正在攻击的敌人弹反")]
    public float parryRange = 3.5f;
    [Tooltip("弹反成功后玩家被定住的现实时间（秒）：期间不能移动，只能按左键反击")]
    public float holdTime = 3f;
    [Tooltip("反击动画时长（Shield_Dash ≈ 1.1s）：期间同样锁住移动")]
    public float riposteTime = 1.1f;

    static readonly int ParryId = Animator.StringToHash("Parry");

    bool holding;
    bool riposted;
    float holdTimer;
    EnemyStagger target;

    /// <summary>是否处于弹反定身（不能移动，按左键触发反击）。</summary>
    public bool IsHolding { get { return holding; } }
    /// <summary>本次反击是否已打出（定身期间只放一次）。</summary>
    public bool Riposted { get { return riposted; } }

    void Awake()
    {
        if (animator == null) animator = GetComponentInParent<Animator>();
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

        var list = Object.FindObjectsOfType<EnemyAI>();
        for (int i = 0; i < list.Length; i++)
        {
            var ai = list[i];
            if (ai == null || !ai.IsInParryWindow()) continue;

            var st = ai.GetComponent<EnemyStagger>();
            if (st == null || st.IsLocked) continue;

            float d = Vector3.Distance(transform.position, ai.transform.position);
            if (d > parryRange || d >= bestD) continue;

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

        if (animator != null)
        {
            // 强制切过去：此时 Guard 仍是 true，走 trigger 的话 Idle→Shield_Raise 会抢在
            // AnyState→Shield_Dash 前面（具体状态过渡优先级更高），反击动画就播不出来
            animator.ResetTrigger(ParryId);
            animator.CrossFade("Shield_Dash", 0.08f, 0, 0f);
        }
        if (target != null) target.Launch(transform.forward);   // 沿玩家面朝方向撞飞
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
