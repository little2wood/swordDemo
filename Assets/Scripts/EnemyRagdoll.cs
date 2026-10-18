using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 布娃娃：平时所有骨骼刚体为 kinematic（完全由动画驱动，零开销），
/// 死亡时唤醒骨骼物理并施加冲量，角色自然倒地/翻滚。
/// 骨骼部件由 Build() 自动生成（等价于 Ragdoll Wizard，但可脚本化）。
/// </summary>
[RequireComponent(typeof(Animator))]
public class EnemyRagdoll : MonoBehaviour
{
    [Header("部件（Build 自动生成）")]
    public List<Rigidbody> parts = new List<Rigidbody>();

    [Header("死亡表现（demo 夸张化）")]
    public float deathImpulse = 10f;      // 水平方向速度改变量 m/s
    public float upwardBias = 0.7f;       // 向上分量（相对水平速度的比例）
    public float randomTorque = 9f;       // 随机角速度，让翻滚更夸张
    public float deathDrag = 0.6f;        // 布娃娃阻尼，避免滑太久

    Animator anim;
    Rigidbody rootBody;
    Collider rootCollider;
    MonoBehaviour aiScript;
    Health health;

    bool active;

    // 骨骼规格：骨骼名 / 子骨骼名（量长度用）/ 质量 / 半径
    class BoneSpec
    {
        public string bone, child;
        public float mass, radius;
        public BoneSpec(string b, string c, float m, float r) { bone = b; child = c; mass = m; radius = r; }
    }

    static readonly BoneSpec[] Specs = new BoneSpec[]
    {
        new BoneSpec("pelvis",   "spine_01",  12f, 0.14f),
        new BoneSpec("spine_01", "spine_02",  10f, 0.15f),
        new BoneSpec("spine_02", "spine_03",  10f, 0.16f),
        new BoneSpec("spine_03", "neck_01",    8f, 0.14f),
        new BoneSpec("neck_01",  "Head",       3f, 0.08f),
        new BoneSpec("Head",     "",           4f, 0.13f),
        new BoneSpec("thigh_l",  "calf_l",     6f, 0.12f),
        new BoneSpec("calf_l",   "foot_l",     4f, 0.10f),
        new BoneSpec("foot_l",   "ball_l",     2f, 0.08f),
        new BoneSpec("thigh_r",  "calf_r",     6f, 0.12f),
        new BoneSpec("calf_r",   "foot_r",     4f, 0.10f),
        new BoneSpec("foot_r",   "ball_r",     2f, 0.08f),
        new BoneSpec("upperarm_l","lowerarm_l",3f, 0.09f),
        new BoneSpec("lowerarm_l","hand_l",    2f, 0.08f),
        new BoneSpec("hand_l",   "",           1f, 0.07f),
        new BoneSpec("upperarm_r","lowerarm_r",3f, 0.09f),
        new BoneSpec("lowerarm_r","hand_r",    2f, 0.08f),
        new BoneSpec("hand_r",   "",           1f, 0.07f)
    };

    // 父骨骼映射：谁连谁
    static string ParentOf(string bone)
    {
        switch (bone)
        {
            case "pelvis": return "";
            case "spine_01": return "pelvis";
            case "spine_02": return "spine_01";
            case "spine_03": return "spine_02";
            case "neck_01": return "spine_03";
            case "Head": return "neck_01";
            case "thigh_l": case "thigh_r": return "pelvis";
            case "calf_l": return "thigh_l";
            case "calf_r": return "thigh_r";
            case "foot_l": return "calf_l";
            case "foot_r": return "calf_r";
            case "upperarm_l": case "upperarm_r": return "spine_03";
            case "lowerarm_l": return "upperarm_l";
            case "lowerarm_r": return "upperarm_r";
            case "hand_l": return "lowerarm_l";
            case "hand_r": return "lowerarm_r";
        }
        return "";
    }

    void Awake()
    {
        anim = GetComponent<Animator>();
        rootBody = GetComponent<Rigidbody>();
        rootCollider = GetComponent<Collider>();
        aiScript = GetComponent<EnemyAI>();
        health = GetComponent<Health>();
        SetPartsKinematic(true);
    }

    void OnEnable()
    {
        if (health == null) health = GetComponent<Health>();
        if (health != null) health.OnDeath += Activate;
    }

    void OnDisable()
    {
        if (health != null) health.OnDeath -= Activate;
    }

    /// <summary>死亡回调：切到物理布娃娃并施加冲量。hitDir 为受力方向。</summary>
    public void Activate(Vector3 hitDir)
    {
        if (active) return;
        active = true;

        // 1. 关掉动画与 AI，保留最后一帧姿势
        if (aiScript != null) aiScript.enabled = false;
        if (anim != null) anim.enabled = false;

        // 2. 交出根物体控制权：主碰撞体与主刚体停用，避免两套管物理打架
        if (rootCollider != null) rootCollider.enabled = false;
        if (rootBody != null)
        {
            rootBody.isKinematic = true;
            rootBody.detectCollisions = false;
            rootBody.linearVelocity = Vector3.zero;
        }

        // 3. 唤醒骨骼物理
        SetPartsKinematic(false);

        // 3.5 修正视锥剔除：骨骼飞散后，SkinnedMeshRenderer 按 rootBone 算的包围盒会留在原地，
        //     导致"走近就看不见"。这里改用骨盆做包围盒基准 + 放大本地包围盒 + 离屏也更新。
        PrepareRenderersForRagdoll();

        // 4. 施加冲量（VelocityChange：不依赖质量，效果直观可控）
        Vector3 dir = hitDir;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) dir = -transform.forward;
        dir.Normalize();
        Vector3 v = dir * deathImpulse + Vector3.up * (deathImpulse * upwardBias);

        foreach (var p in parts)
        {
            if (p == null) continue;
            p.AddForce(v, ForceMode.VelocityChange);
            p.AddTorque(Random.insideUnitSphere * randomTorque, ForceMode.VelocityChange);
        }
    }

    /// 布娃娃状态下让所有蒙皮网格始终可见（否则会被视锥剔除掉）
    void PrepareRenderersForRagdoll()
    {
        Transform pelvis = null;
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "pelvis") { pelvis = t; break; }
        }

        foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            smr.updateWhenOffscreen = true;                 // 离屏也继续蒙皮并刷新包围盒
            if (pelvis != null) smr.rootBone = pelvis;      // 包围盒基准跟随真正会飞的骨盆
            var b = smr.localBounds;
            b.center = new Vector3(0f, 0f, 0f);
            b.size = Vector3.one * 8f;                      // 放大到足够覆盖飞散后的肢体
            smr.localBounds = b;
        }
    }

    void SetPartsKinematic(bool kinematic)
    {
        foreach (var p in parts)
        {
            if (p == null) continue;
            p.isKinematic = kinematic;
            p.detectCollisions = !kinematic;
            if (!kinematic)
            {
                p.linearDamping = deathDrag;
                p.angularDamping = deathDrag;
                p.WakeUp();
            }
        }
    }

    /// <summary>按骨骼名自动生成刚体/碰撞体/关节。编辑器或脚本调用均可。</summary>
    public void Build()
    {
        Clear();

        var boneMap = new Dictionary<string, Transform>();
        foreach (var t in GetComponentsInChildren<Transform>(true)) boneMap[t.name] = t;

        // 先建所有刚体与碰撞体
        var rbMap = new Dictionary<string, Rigidbody>();
        foreach (var spec in Specs)
        {
            if (!boneMap.ContainsKey(spec.bone)) { Debug.LogWarning("[Ragdoll] 缺少骨骼 " + spec.bone); continue; }
            Transform bone = boneMap[spec.bone];

            float len = 0.12f;
            if (spec.child != "" && boneMap.ContainsKey(spec.child))
                len = Vector3.Distance(bone.position, boneMap[spec.child].position) / Mathf.Max(0.0001f, transform.lossyScale.y);

            var rb = bone.GetComponent<Rigidbody>();
            if (rb == null) rb = bone.gameObject.AddComponent<Rigidbody>();
            rb.mass = spec.mass;
            rb.isKinematic = true;
            rb.detectCollisions = false;
            rb.useGravity = true;

            var col = bone.GetComponent<CapsuleCollider>();
            if (col == null) col = bone.gameObject.AddComponent<CapsuleCollider>();
            col.direction = 1;                       // 该骨架子骨骼恒在局部 +Y
            col.radius = spec.radius;
            col.height = len + spec.radius * 2f;
            col.center = new Vector3(0f, len * 0.5f, 0f);

            rbMap[spec.bone] = rb;
            parts.Add(rb);
        }

        // 再连关节（父骨骼必须已建好）
        foreach (var spec in Specs)
        {
            string parentName = ParentOf(spec.bone);
            if (parentName == "" || !rbMap.ContainsKey(spec.bone) || !rbMap.ContainsKey(parentName)) continue;

            Transform bone = boneMap[spec.bone];
            var joint = bone.GetComponent<CharacterJoint>();
            if (joint == null) joint = bone.gameObject.AddComponent<CharacterJoint>();
            joint.connectedBody = rbMap[parentName];
            joint.anchor = Vector3.zero;                 // 关节在本骨骼原点
            joint.connectedAnchor = bone.localPosition;  // 父骨骼本地空间中的位置
            joint.axis = new Vector3(0f, 1f, 0f);        // 骨骼朝向 = 局部 +Y
            joint.swingAxis = new Vector3(1f, 0f, 0f);

            var sl = new SoftJointLimit(); sl.limit = 60f; sl.bounciness = 0f; joint.swing1Limit = sl;
            var s2 = new SoftJointLimit(); s2.limit = 60f; s2.bounciness = 0f; joint.swing2Limit = s2;
            var tl = new SoftJointLimit(); tl.limit = -40f; tl.bounciness = 0f; joint.lowTwistLimit = tl;
            var th = joint.highTwistLimit = new SoftJointLimit(); th.limit = 40f; joint.highTwistLimit = th;
            joint.enableProjection = true;               // 减少关节被拉散
        }

        Debug.Log("[Ragdoll] 生成完成，部件数=" + parts.Count);
    }

    /// <summary>移除布娃娃部件（重建前调用）</summary>
    public void Clear()
    {
        foreach (var p in parts)
        {
            if (p == null) continue;
            var go = p.gameObject;
            var j = go.GetComponent<CharacterJoint>();
            if (j != null) DestroyImmediate(j);
            var c = go.GetComponent<CapsuleCollider>();
            if (c != null) DestroyImmediate(c);
            DestroyImmediate(p);
        }
        parts.Clear();
        active = false;
    }
}
