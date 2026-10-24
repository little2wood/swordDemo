using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 近战命中结算（纯 C# 类）：按"Prop / 石头 / 血量"三分支结算一次命中，并按"一次攻击"去重。
/// 由玩家挥砍（PlayerController）、敌人爪击（EnemyAI）、盾牌（ShieldThrow）共用——
/// 三处原有的重复分支逻辑收拢于此，来源差异用 Policy 的开关与回调表达：
/// - propApplyImpulse=false：Prop 只登记不施加冲量（玩家剑的箱子击飞交给 BladeCollider 的真实物理碰撞）；
/// - onStone：命中 StoneBlock 后的额外表现（玩家剑=中断挥砍迸火星；盾/爪无此需求传 null）；
/// - onFlesh：命中血量目标后的额外表现（玩家剑=溅血）。
/// HasHitTarget 供外部读取"这一击是否结算过"（敌人爪的弹反窗口依赖它）。
/// </summary>
public class MeleeHitResolver
{
    public struct Policy
    {
        public LayerMask propLayer;
        public float mass;            // 武器有效质量（冲量 J = 速度 × 质量，N·s）
        public float maxSpeed;        // Prop 击飞的速度上限（防卡顿帧/峰值把目标弹射上天）
        public float damage;
        public Health.Team targetTeam;
        public bool propApplyImpulse;
        public System.Action<Vector3, Vector3, StoneBlock> onStone;   // (接触点, 挥扫方向, 石头)
        public System.Action<Vector3, Vector3> onFlesh;               // (接触点, 挥扫方向)
    }

    readonly HashSet<Health> healths = new HashSet<Health>();         // 本次攻击已结算的目标（对同一目标只结算一次）
    readonly HashSet<Rigidbody> props = new HashSet<Rigidbody>();     // 本次攻击已处理过的可互动物体（一次只处理一次）
    readonly HashSet<StoneBlock> stones = new HashSet<StoneBlock>();  // 本次攻击已响应过的石头（一块只响应一次）

    /// <summary>本次攻击是否已对血量目标结算过（敌人爪：命中后这一击不再可弹反）。</summary>
    public bool HasHitTarget { get; private set; }

    /// <summary>
    /// 开始新的一次攻击：清空去重集合与 HasHitTarget。
    /// "什么算一击"是业务语义，清空时机由调用方决定（新刀/新爪/盾的新一次飞行）。
    /// </summary>
    public void Begin()
    {
        healths.Clear();
        props.Clear();
        stones.Clear();
        HasHitTarget = false;
    }

    /// <summary>
    /// 结算一次命中。
    /// sweepDir 为受力方向（挥扫/飞行方向，Health.TakeDamage 内归一化）；
    /// velocity 为命中前速度（m/s，冲量标尺用）；point 为接触点（决定布娃娃部位与 Prop 偏心扭矩）。
    /// </summary>
    public void Resolve(Collider col, Vector3 sweepDir, Vector3 velocity, Vector3 point, in Policy p)
    {
        // ---- 可互动物体（Prop 层）----
        if (PropImpulse.IsProp(col.gameObject, p.propLayer))
        {
            if (p.propApplyImpulse)
            {
                PropImpulse.Apply(col.attachedRigidbody, velocity, p.mass, p.maxSpeed, point, props);
            }
            else if (col.attachedRigidbody != null)
            {
                props.Add(col.attachedRigidbody);   // 只登记，保持"一次攻击只处理一次"的语义
            }
            return;
        }

        // ---- 石头/硬质障碍：剑砍不动 → 交回调（弹刀迸火星等）；无回调时不结算 ----
        var stone = col.GetComponentInParent<StoneBlock>();
        if (stone != null)
        {
            if (!stone.bounces || p.onStone == null) return;
            if (!stones.Add(stone)) return;   // 这块石头本刀已响应过
            p.onStone(point, sweepDir, stone);
            return;
        }

        // ---- 血量目标：按阵营/已死/本刀重复过滤后扣血 ----
        var hp = col.GetComponentInParent<Health>();
        if (hp == null || hp.IsDead || hp.team != p.targetTeam) return;
        if (!healths.Add(hp)) return;    // 该目标本刀已结算过
        HasHitTarget = true;
        hp.TakeDamage(p.damage, sweepDir, PropImpulse.Magnitude(velocity, p.mass), point);   // 冲量决定晃动幅度，命中点决定布娃娃飞散姿态
        if (p.onFlesh != null) p.onFlesh(point, sweepDir);
    }
}
