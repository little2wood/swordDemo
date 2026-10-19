using UnityEngine;

/// <summary>
/// 受击摆动（上半身倾摆）：受击后上半身朝攻击方向倾倒，欠阻尼弹簧回摆 1~2 次归零。
/// 只叠加到 spine/head（不碰 pelvis）——骨盆一旦旋转会带动整条腿、脚离地出戏；
/// 上半身弯/歪时腿和脚完全不受影响。
/// LateUpdate 在 Animator 写完骨骼姿态之后叠加倾斜（渲染前），
/// 下一帧 Animator 自动覆盖骨骼，无需恢复逻辑；死亡后骨骼交给布娃娃物理，立即停手。
/// </summary>
public class HitReaction : MonoBehaviour
{
    [Header("引用（留空自动取自身）")]
    public Health health;
    public Animator animator;

    [Header("受击摆动")]
    public float impulseStrength = 200f;    // 受击冲量（deg/s），决定晃多狠
    [Range(0f, 45f)] public float maxAngle = 14f;   // 最大倾倒角

    [Header("弹簧手感")]
    public float stiffness = 150f;          // 刚度：越大拉回越快
    public float damping = 12f;             // 阻尼：越小荡的次数越多（欠阻尼）

    [Header("调试")]
    public bool debugLog = false;

    // ---- 弹簧状态 ----
    float angle;      // 当前倾斜角（deg）
    float angVel;     // 角速度（deg/s）
    Vector3 axis;     // 倾倒轴（世界空间）

    Transform spine, head;   // 只晃上半身：spine/head 不在腿的父链上，脚不会离地
    Health hp;

    void Awake()
    {
        hp = health != null ? health : GetComponent<Health>();
        var anim = animator != null ? animator : GetComponent<Animator>();
        if (anim != null)
        {
            spine = anim.GetBoneTransform(HumanBodyBones.Spine);
            head = anim.GetBoneTransform(HumanBodyBones.Head);
        }
        if (spine == null) spine = FindDeep(transform, "spine");
        if (head == null) head = FindDeep(transform, "head");
        if (hp != null) hp.OnDamaged += OnHit;
    }

    /// 受击：把攻击方向转成倾倒轴，并给弹簧一个冲量
    void OnHit(Vector3 dir)
    {
        if (hp.IsDead) return;    // 死亡那一下由布娃娃接管，不做摆动

        Vector3 flat = dir; flat.y = 0f;                    // 拍平：只取水平攻击方向
        if (flat.sqrMagnitude < 0.0001f) return;

        axis = Vector3.Cross(Vector3.up, flat.normalized);  // 绕这根轴转 = 身体朝攻击方向倒
        angVel += impulseStrength;                          // 冲量进弹簧
        if (debugLog) Debug.Log("[HitReaction] 受击 dir=" + dir.ToString("F2") + " axis=" + axis.ToString("F2"));
    }

    void LateUpdate()
    {
        if (hp == null || hp.IsDead) return;    // 死亡后骨骼交给布娃娃物理，停手
        if (spine == null && head == null) return;

        // 欠阻尼弹簧积分：acc = -刚度*角度 - 阻尼*角速度
        float dt = Time.deltaTime;
        angVel += (-stiffness * angle - damping * angVel) * dt;
        angle = Mathf.Clamp(angle + angVel * dt, -maxAngle, maxAngle);

        if (Mathf.Abs(angle) < 0.05f && Mathf.Abs(angVel) < 0.5f) return;   // 静止时零开销

        // 只晃上半身：腰带得最多，头其次；骨盆/腿不动，脚不离地
        Apply(spine, angle, axis, 1.0f);
        Apply(head, angle, axis, 0.5f);
    }

    /// 世界空间轴旋转叠加到骨骼当前旋转（Animator 更新之后、渲染之前）
    static void Apply(Transform bone, float deg, Vector3 axis, float weight)
    {
        if (bone == null) return;
        float d = deg * weight;
        if (Mathf.Abs(d) < 0.02f) return;
        bone.rotation = Quaternion.AngleAxis(d, axis) * bone.rotation;
    }

    /// 递归查找子孙节点（名字包含关键词，小写比较；humanoid 映射取不到时的兜底）
    static Transform FindDeep(Transform root, string keyword)
    {
        if (root.name.ToLower().Contains(keyword)) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var r = FindDeep(root.GetChild(i), keyword);
            if (r != null) return r;
        }
        return null;
    }
}
