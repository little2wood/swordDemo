using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 可互动物体（Prop 层）的物理击飞：在接触点施加冲量，偏心命中自动产生扭矩 → 物体一边飞一边转。
/// 只对 dynamic Rigidbody 生效：主角/敌人（Default 层）、平台（静态 collider 没有刚体）、
/// 敌人布娃娃部件（Ragdoll 层且 kinematic）都天然不受影响。
/// 冲量模型：J = 武器速度(m/s) × 武器有效质量(kg)，单位 N·s，直接用 ForceMode.Impulse 施加。
/// </summary>
public static class PropImpulse
{
    /// 该对象是否位于可互动物体层（Prop）
    public static bool IsProp(GameObject go, LayerMask propLayer)
    {
        if (go == null) return false;
        return (propLayer.value & (1 << go.layer)) != 0;
    }

    /// <summary>
    /// 在接触点施加冲量。速度为 0、刚体缺失/kinematic、或本次攻击已推过该刚体时跳过（返回 false）。
    /// 一次攻击只对同一物体推一次，避免逐帧叠加把物体弹射上天。
    /// </summary>
    public static bool Apply(Rigidbody rb, Vector3 weaponVel, float weaponMass, float maxSpeed, Vector3 point, HashSet<Rigidbody> hitThis)
    {
        if (rb == null || rb.isKinematic) return false;          // 无刚体（平台）或 kinematic（布娃娃部件）：不动
        if (hitThis != null && !hitThis.Add(rb)) return false;   // 本次攻击已推过

        Vector3 v = Vector3.ClampMagnitude(weaponVel, maxSpeed); // 速度上限，防卡顿帧/速度峰值
        if (v.sqrMagnitude < 1e-6f) return false;

        rb.AddForceAtPosition(v * weaponMass, point, ForceMode.Impulse);   // 接触点 → 偏心自动产生旋转
        return true;
    }
}
