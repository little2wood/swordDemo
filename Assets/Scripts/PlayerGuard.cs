using System;
using UnityEngine;

/// <summary>
/// 主角防御 / 架势 / 破防。
///
/// 举盾（Shield_Raise / Shield_Lower 期间）且正面朝向攻击来向时，来袭伤害被吸收 —— 不扣血，
/// 但受击反馈照常（Health 拦截后仍广播 OnDamaged），代价是消耗架势值。
/// 架势值归零即破防：不扣血，改为把盾打飞出去，并硬直一段与破防动画等长的时间，期间禁止一切操作。
/// 破防瞬间架势条立刻回满；盾要等硬直结束后按右键召回。
/// </summary>
public class PlayerGuard : MonoBehaviour
{
    [Header("引用（留空自动查找）")]
    public Health health;
    public ShieldThrow shield;
    public Animator animator;

    [Header("架势条")]
    public float maxGuard = 100f;
    [Tooltip("非防御状态下，架势从 0 回到满所需的时间（秒）")]
    public float guardRegenTime = 3f;

    [Header("格挡")]
    [Tooltip("可格挡的正面扇区总角度（deg）：攻击来向与角色正面的夹角在一半以内才算挡住")]
    public float blockArcDeg = 120f;
    [Tooltip("每 1 点伤害消耗多少架势值")]
    public float guardCostPerDamage = 1f;
    [Tooltip("单次格挡至少消耗多少架势值（防止极小额攻击被无限格挡）")]
    public float minGuardCost = 1f;
    [Tooltip("盾不在手时不算防御：举盾动画照播，但不吸收伤害")]
    public bool requireShieldInHand = true;

    [Header("破防")]
    [Tooltip("破防硬直时长（秒）：期间禁止一切操作，默认与 Idle_Shield_Break 动画等长")]
    public float breakStunTime = 1.067f;

    static readonly int BreakId = Animator.StringToHash("ShieldBreak");

    float guard;
    bool stunned;
    float stunTimer;
    PlayerController controller;

    /// <summary>架势值 0~1（UI 用）。</summary>
    public float Ratio { get { return maxGuard > 0f ? Mathf.Clamp01(guard / maxGuard) : 0f; } }
    /// <summary>当前架势值。</summary>
    public float CurrentGuard { get { return guard; } }
    /// <summary>是否处于破防硬直（禁止一切操作）。</summary>
    public bool IsStunned { get { return stunned; } }
    /// <summary>硬直剩余时间（秒），0 表示未硬直。</summary>
    public float StunRemain { get { return stunned ? stunTimer : 0f; } }

    /// <summary>破防瞬间触发（UI / 相机等外部表现用）。</summary>
    public event Action OnGuardBroken;

    void Awake()
    {
        if (health == null) health = GetComponent<Health>();
        if (shield == null) shield = GetComponentInChildren<ShieldThrow>(true);
        if (animator == null) animator = GetComponentInParent<Animator>();
        if (controller == null) controller = GetComponentInParent<PlayerController>();

        guard = maxGuard;
        if (health != null) health.OnDamageIntercept += TryBlock;
    }

    void OnDestroy()
    {
        if (health != null) health.OnDamageIntercept -= TryBlock;
    }

    void Update()
    {
        // 破防硬直：架势条已在破防瞬间回满，硬直期间不做任何恢复/消耗
        if (stunned)
        {
            stunTimer -= Time.deltaTime;
            if (stunTimer <= 0f)
            {
                stunned = false;
                stunTimer = 0f;
            }
            return;
        }

        if (InGuardPose()) return;      // 防御状态：架势不恢复

        if (guard < maxGuard)
        {
            guard += (maxGuard / Mathf.Max(0.0001f, guardRegenTime)) * Time.deltaTime;
            if (guard > maxGuard) guard = maxGuard;
        }
    }

    /// <summary>是否处于举盾姿态（含收盾过渡：过渡期间按来源状态判定，仍算防御中）。</summary>
    bool InGuardPose()
    {
        return controller != null && controller.IsGuardPose();
    }

    /// <summary>
    /// 伤害拦截（订阅 Health.OnDamageIntercept）：
    /// 举盾 + 盾在手 + 正面朝向攻击 → 吸收这次伤害（不扣血），扣架势值；架势归零则破防。
    /// 破防那一下同样被吸收 —— 破防的代价是掉盾与硬直，不是掉血。
    /// </summary>
    bool TryBlock(Vector3 hitDir, float amount)
    {
        if (health != null && health.IsDead) return false;
        if (stunned) return false;
        if (!InGuardPose()) return false;
        if (requireShieldInHand && shield != null && !shield.InHand) return false;
        if (!FacingAttack(hitDir)) return false;      // 背后/侧面来的攻击挡不住

        float cost = Mathf.Max(minGuardCost, amount * guardCostPerDamage);
        guard -= cost;
        if (guard <= 0f)
        {
            guard = 0f;
            Break();
        }
        return true;
    }

    /// <summary>
    /// 攻击是否来自正面扇区：hitDir 是受力方向（攻击者 → 我），
    /// 所以"攻击者在哪个方向"是 -hitDir，与角色正面做夹角判定。
    /// </summary>
    bool FacingAttack(Vector3 hitDir)
    {
        Vector3 f = transform.forward; f.y = 0f;
        Vector3 toAttacker = -hitDir; toAttacker.y = 0f;
        if (f.sqrMagnitude < 1e-6f || toAttacker.sqrMagnitude < 1e-6f) return true;   // 退化（垂直攻击）时放行
        f.Normalize();
        toAttacker.Normalize();
        return Vector3.Dot(f, toAttacker) >= Mathf.Cos(blockArcDeg * 0.5f * Mathf.Deg2Rad);
    }

    void Break()
    {
        guard = maxGuard;               // 破防后架势条马上回满
        stunned = true;
        stunTimer = breakStunTime;

        if (animator != null) animator.SetTrigger(BreakId);
        if (shield != null) shield.BeginBreakThrow();   // 盾在破防动画的出手帧被打飞（内部只在盾在手时生效）

        var handler = OnGuardBroken;
        if (handler != null) handler();
    }

    /// <summary>测试/复位用：架势回满、清除硬直。</summary>
    public void ResetGuard()
    {
        guard = maxGuard;
        stunned = false;
        stunTimer = 0f;
    }
}
