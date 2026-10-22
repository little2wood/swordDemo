using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 投掷盾牌专用的按骨骼局部受力反馈 —— 与其他攻击的受击反应完全独立。
///
/// 区别：HitReaction（挥砍/爪击）是"统一晃上半身"——不管打哪都摆 spine/head；
/// 本组件是"砸中哪根骨头、哪根骨头按受力转动"：用接触点相对骨骼的力臂 × 命中冲量
/// 算出扭矩，注入该骨骼的独立弹簧，再沿子链按权重衰减传导（上层转了，下层惯性甩出）。
///
/// 只在 Flying 中的盾发生碰撞时被调用（见 ShieldThrow.OnCollisionEnter）。
/// 存活期间布娃娃部件是 kinematic，不能靠施加力，改为 LateUpdate 叠加骨骼旋转：
/// Animator 写完姿态之后、渲染之前叠加，下一帧 Animator 自动覆盖，无需恢复逻辑。
/// 对手死亡后骨骼交给 Ragdoll 物理，本组件立即停手（也放开 pelvis 约束 —— 砸中骨盆照常摆）。
/// </summary>
public class ShieldHitReaction : MonoBehaviour
{
    [Header("引用（留空自动取自身）")]
    public Health health;
    public Ragdoll ragdoll;

    [Header("幅度随命中冲量缩放（与挥砍同一标尺）")]
    public float refImpulse = 80f;                 // 参考冲量（N·s）：达到即满档
    [Range(0f, 1f)] public float minScale = 0.35f; // 最轻击的幅度下限

    [Header("弹簧手感")]
    [Tooltip("满档倾倒角速度（deg/s）。盾是重型钝器，冲量量级远大于挥砍，这里比 HitReaction 的 200 高一档")]
    public float impulseStrength = 420f;           // 满档倾倒角速度（deg/s）
    [Range(0f, 60f)] public float maxAngle = 30f; // 单根骨骼最大倾倒角（放开 pelvis，可比挥砍的 14° 大）
    public float stiffness = 150f;                  // 刚度：越大拉回越快
    public float damping = 12f;                      // 阻尼：越小荡的次数越多（欠阻尼）

    [Header("力臂缩放")]
    [Tooltip("参考力臂长度（m）：偏心命中超过它按满档算。砸四肢末端比砸关节根转得更狠")]
    public float refLeverArm = 0.35f;
    [Range(0f, 1f)] public float minLeverScale = 0.7f;   // 正中骨骼原点时保留的比例

    [Header("子链传导")]
    [Range(0f, 1f)] public float childWeight = 0.5f;        // 子骨
    [Range(0f, 1f)] public float grandChildWeight = 0.25f;  // 孙骨

    [Header("调试")]
    public bool debugLog = false;

    class Spring
    {
        public float angle;     // 当前倾倒角（deg）
        public float angVel;    // 角速度（deg/s）
        public Vector3 axis;    // 倾倒轴（世界空间）
    }

    readonly Dictionary<Transform, Spring> springs = new Dictionary<Transform, Spring>();
    Health hp;

    void Awake()
    {
        hp = health != null ? health : GetComponent<Health>();
    }

    /// <summary>
    /// 盾命中回调：point=接触点（世界坐标），dir=受力方向（会被归一化），impulse=命中冲量（N·s）。
    /// 找到被击中的骨骼并给它与子链注入旋转脉冲。
    /// </summary>
    public void ReactAt(Vector3 point, Vector3 dir, float impulse)
    {
        if (hp == null || hp.IsDead) return;    // 死亡那一下交给布娃娃
        if (dir.sqrMagnitude > 0.0001f) dir.Normalize();
        if (ragdoll == null) return;

        var part = ragdoll.NearestPartByBone(point, out float dist);
        if (part == null) return;

        Transform hitBone = part.transform;

        // 力臂：接触点相对被击中骨骼的旋转中心；力矩 τ = r × J 决定"绕哪根轴、转多快"
        Vector3 r = point - hitBone.position;
        Vector3 impulseVec = dir * impulse;
        Vector3 torque = Vector3.Cross(r, impulseVec);

        // 倾倒轴：优先用力矩方向（真实受力）；正中骨骼原点时力矩退化，回退到"攻击方向的水平分量"
        Vector3 flat = dir; flat.y = 0f;
        Vector3 axis = torque.sqrMagnitude > 1e-6f
            ? torque.normalized
            : (flat.sqrMagnitude > 1e-6f ? Vector3.Cross(Vector3.up, flat.normalized) : Vector3.right);

        // 强度：冲量水平分量（与 HitReaction 同款）× 力臂系数（偏心越大转得越狠）
        float horizontal = impulse * Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z);
        float scale = refImpulse > 0.0001f
            ? Mathf.Lerp(minScale, 1f, Mathf.Clamp01(horizontal / refImpulse))
            : 1f;

        float lever = refLeverArm > 0.0001f
            ? Mathf.Lerp(minLeverScale, 1f, Mathf.Clamp01(r.magnitude / refLeverArm))
            : 1f;

        Inject(hitBone, axis, impulseStrength * scale * lever);

        // 子链传导：上层转了，下层按 0.5 / 0.25 惯性跟甩
        var child = FirstChildBone(hitBone);
        if (child != null)
        {
            Inject(child, axis, impulseStrength * scale * lever * childWeight);
            var grand = FirstChildBone(child);
            if (grand != null)
                Inject(grand, axis, impulseStrength * scale * lever * grandChildWeight);
        }

        if (debugLog)
            Debug.Log("[ShieldHit] bone=" + hitBone.name + " dist=" + dist.ToString("F2")
                      + " impulse=" + impulse.ToString("F1") + "N·s scale=" + scale.ToString("F2")
                      + " lever=" + lever.ToString("F2"));
    }

    /// 给某根骨骼的弹簧注入角速度（没有就新建一个弹簧实例）
    void Inject(Transform bone, Vector3 axis, float angVel)
    {
        if (bone == null) return;
        Spring s;
        if (!springs.TryGetValue(bone, out s))
        {
            s = new Spring();
            springs[bone] = s;
        }
        s.axis = axis;      // 后来的一次覆盖轴向：以最新受力为准
        s.angVel += angVel;
    }

    /// 骨架里"骨骼线段"的远端：取第一个子骨骼（子骨骼恒在局部 +Y，见 Ragdoll.Build）
    static Transform FirstChildBone(Transform bone)
    {
        if (bone == null || bone.childCount == 0) return null;
        return bone.GetChild(0);
    }

    void LateUpdate()
    {
        if (hp == null || hp.IsDead) { springs.Clear(); return; }    // 死亡后交给布娃娃，停手并清空
        if (springs.Count == 0) return;

        float dt = Time.deltaTime;
        var dead = new List<Transform>();

        foreach (var kv in springs)
        {
            var bone = kv.Key;
            var s = kv.Value;
            if (bone == null) { dead.Add(bone); continue; }

            // 欠阻尼弹簧积分：acc = -刚度*角度 - 阻尼*角速度
            s.angVel += (-stiffness * s.angle - damping * s.angVel) * dt;
            s.angle = Mathf.Clamp(s.angle + s.angVel * dt, -maxAngle, maxAngle);

            if (Mathf.Abs(s.angle) < 0.05f && Mathf.Abs(s.angVel) < 0.5f) { dead.Add(bone); continue; }

            bone.rotation = Quaternion.AngleAxis(s.angle, s.axis) * bone.rotation;
        }

        for (int i = 0; i < dead.Count; i++) springs.Remove(dead[i]);
    }
}
