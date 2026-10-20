using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 布娃娃（主角与敌人通用）：平时所有骨骼刚体为 kinematic（完全由动画驱动，零开销），
/// 死亡时唤醒骨骼物理并施加冲量，角色自然倒地/翻滚。
/// 骨骼部件由 Build() 自动生成（等价于 Ragdoll Wizard，但可脚本化）。
/// </summary>
[RequireComponent(typeof(Animator))]
public class Ragdoll : MonoBehaviour
{
    [Header("部件（Build 自动生成）")]
    public List<Rigidbody> parts = new List<Rigidbody>();

    [Header("死亡表现（demo 夸张化）")]
    public float deathImpulse = 10f;      // 飞出速度改变量 m/s（方向直接取命中冲量方向，整体基础，会被距离衰减）
    public float randomTorque = 9f;       // 随机角速度，让翻滚更夸张
    public float deathDrag = 1.0f;        // 布娃娃阻尼，避免滑太久
    public float deathSleepThreshold = 0.08f;   // 提高休眠阈值，让静止更快到来

    [Header("按命中部位分配（砍哪飞哪）")]
    public float localImpulse = 12f;      // 命中点局部冲量（m/s）：施加在被砍中的那个部件上
    public float falloffRadius = 1.2f;    // 距离衰减半径：离命中点这么远时速度降到 falloffMin
    [Range(0f, 1f)] public float falloffMin = 0.5f;   // 最远部件保留的速度比例（近快远慢 → 以命中点为中心旋转）

    [Header("强度随命中冲量（与受击摆动同一标尺）")]
    public float refImpulse = 28f;        // 参考冲量（N·s）：达到该值即满档飞出
    [Range(0f, 1f)] public float minScale = 0.35f;    // 最轻击的飞出强度下限（0=轻杀几乎不飞）

    [Header("稳定性")]
    public int ragdollLayer = 9;                // 布娃娃专用层（该层内部互不碰撞）
    public bool ignoreSelfCollision = true;     // 关掉肢体互撞 → 消除抽搐的主因
    public bool retargetCameraOnDeath = true;   // 死亡后把相机跟随目标切到骨盆（否则镜头留在原地）
    public bool freezeOnSettle = true;          // 静止后冻结，彻底不再抖
    public float settleDelay = 4.0f;            // 多少秒后开始强制减速
    public float freezeDelay = 5.5f;            // 多少秒后完全冻结（kinematic）
    public float settleDamping = 12f;           // 减速阶段的阻尼

    float activateTime;
    bool dampingRaised;
    bool frozen;

    Animator anim;
    Rigidbody rootBody;
    Collider rootCollider;
    MonoBehaviour[] controllers;   // 死亡时要停用的控制脚本（主角：PlayerController / 敌人：EnemyAI）
    Health health;

    bool active;

    // 部件 → 子骨骼（构成"骨骼线段"的远端；末端骨骼为 null）。Build() 时填充，
    // 供外部（如盾牌局部受力）按"点到骨骼线段的距离"定位被击中的骨头，
    // 而不必依赖碰撞体查询 —— 存活期间部件是 kinematic 且 detectCollisions=false，ClosestPoint 会退化失效。
    readonly Dictionary<Rigidbody, Transform> childOfPart = new Dictionary<Rigidbody, Transform>();
    bool boneSegmentsReady;

    // 骨骼规格：骨骼名 / 子骨骼名（量长度用）/ 质量 / 半径
    public class BoneSpec
    {
        public string bone, child;
        public float mass, radius;
        public BoneSpec(string b, string c, float m, float r) { bone = b; child = c; mass = m; radius = r; }
    }

    public static readonly BoneSpec[] Specs = new BoneSpec[]
    {
        // 质量比尽量控制在 3:1 以内（悬殊的质量比会让关节解算器发抖）
        new BoneSpec("pelvis",   "spine_01",  10f, 0.13f),
        new BoneSpec("spine_01", "spine_02",   8f, 0.14f),
        new BoneSpec("spine_02", "spine_03",   8f, 0.15f),
        new BoneSpec("spine_03", "neck_01",    7f, 0.13f),
        new BoneSpec("neck_01",  "Head",       2f, 0.07f),
        new BoneSpec("Head",     "",           3f, 0.12f),
        new BoneSpec("thigh_l",  "calf_l",     5f, 0.11f),
        new BoneSpec("calf_l",   "foot_l",     3.5f, 0.09f),
        new BoneSpec("foot_l",   "ball_l",     1.5f, 0.07f),
        new BoneSpec("thigh_r",  "calf_r",     5f, 0.11f),
        new BoneSpec("calf_r",   "foot_r",     3.5f, 0.09f),
        new BoneSpec("foot_r",   "ball_r",     1.5f, 0.07f),
        new BoneSpec("upperarm_l","lowerarm_l",2.5f, 0.08f),
        new BoneSpec("lowerarm_l","hand_l",    1.8f, 0.07f),
        new BoneSpec("hand_l",   "",           1.2f, 0.06f),
        new BoneSpec("upperarm_r","lowerarm_r",2.5f, 0.08f),
        new BoneSpec("lowerarm_r","hand_r",    1.8f, 0.07f),
        new BoneSpec("hand_r",   "",           1.2f, 0.06f)
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
        health = GetComponent<Health>();

        // 死亡时要停用的控制脚本：主角是 PlayerController，敌人是 EnemyAI
        var list = new List<MonoBehaviour>();
        var pc = GetComponent<PlayerController>();
        if (pc != null) list.Add(pc);
        var ai = GetComponent<EnemyAI>();
        if (ai != null) list.Add(ai);
        controllers = list.ToArray();

        SetPartsKinematic(true);

        // 布娃娃层内部互不碰撞：肢体胶囊在骨盆/胸部附近必然重叠，
        // 若互相碰撞，解算器会不停把它们推开 —— 这是"地上一直抽搐"的主因
        if (ignoreSelfCollision) Physics.IgnoreLayerCollision(ragdollLayer, ragdollLayer, true);
    }

    void Update()
    {
        if (!active || !freezeOnSettle || frozen) return;

        float t = Time.time - activateTime;

        if (!dampingRaised && t >= settleDelay)
        {
            dampingRaised = true;
            foreach (var p in parts)
            {
                if (p == null) continue;
                p.linearDamping = settleDamping;
                p.angularDamping = settleDamping;
            }
        }

        if (t >= freezeDelay)
        {
            frozen = true;
            foreach (var p in parts)
            {
                if (p == null) continue;
                p.linearVelocity = Vector3.zero;
                p.angularVelocity = Vector3.zero;
                p.isKinematic = true;   // 冻结：彻底静止，也省 CPU
            }
        }
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

    /// <summary>
    /// 死亡回调：切到物理布娃娃并施加冲量。
    /// hitDir=受力方向；impulse=致死那一下的命中冲量（N·s，决定飞多远）；point=命中点（决定飞散姿态）。
    /// 有命中点时按部位分配：整体速度按到命中点的距离衰减（近快远慢 → 自然旋转），
    /// 并在被砍中的那个部件上按接触点施加局部冲量（力臂自动产生扭矩）→ 砍腿腿飞、砍头上半身后仰。
    /// 没有命中点时退化为整体固定冲量 + 随机扭矩（旧行为）；impulse=0（未知）时按满档处理。
    /// </summary>
    public void Activate(Vector3 hitDir, float impulse, Vector3 point)
    {
        if (active) return;
        active = true;
        activateTime = Time.time;

        // 1. 关掉控制脚本与动画，保留最后一帧姿势
        if (controllers != null)
        {
            for (int i = 0; i < controllers.Length; i++)
                if (controllers[i] != null) controllers[i].enabled = false;
        }
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
        Transform pelvis = FindPelvis();
        PrepareRenderersForRagdoll(pelvis);

        // 3.6 相机改跟骨盆（根物体已停用不动，否则镜头会停在原地看空气）
        if (retargetCameraOnDeath && pelvis != null)
        {
            var camFollow = Camera.main != null ? Camera.main.GetComponent<ThirdPersonCamera>() : null;
            if (camFollow != null && camFollow.target == transform) camFollow.target = pelvis;
        }

        // 4. 施加冲量（VelocityChange：不依赖质量，效果直观可控）
        // 方向直接取命中冲量的方向（hitDir = 武器速度方向归一化，与冲量矢量同源），不再压平、不再叠加向上分量：
        // 上挑真的把尸体挑起、下劈真的把它压向地面，飞出方向完全由这一击怎么打决定
        Vector3 dir = hitDir;
        if (dir.sqrMagnitude < 0.0001f) dir = -transform.forward;   // 没有方向时的兜底
        dir.Normalize();

        // 飞出强度由致死那一下的命中冲量决定：轻刀软倒、重劈飞远（impulse=0 表示未知，按满档）
        float scale = impulse > 0.0001f
            ? Mathf.Lerp(minScale, 1f, Mathf.Clamp01(impulse / refImpulse))
            : 1f;

        Vector3 v = dir * deathImpulse * scale;

        bool hasPoint = point.sqrMagnitude > 0.0001f;
        Rigidbody hitPart = hasPoint ? NearestPart(point) : null;   // 被砍中的部位

        foreach (var p in parts)
        {
            if (p == null) continue;

            // 整体速度按到命中点的距离衰减：近的部件快、远的慢，身体因此绕命中点旋转
            float w = 1f;
            if (hasPoint)
                w = Mathf.Lerp(1f, falloffMin, Mathf.Clamp01(DistanceToPart(p, point) / falloffRadius));

            p.AddForce(v * w, ForceMode.VelocityChange);
            p.AddTorque(Random.insideUnitSphere * randomTorque, ForceMode.VelocityChange);
        }

        // 被砍中的部位：在接触点施加局部冲量，力臂自动产生扭矩（砍腿踢飞腿、砍头把上半身掀翻）
        if (hitPart != null)
        {
            Vector3 localV = dir * localImpulse * scale;
            hitPart.AddForceAtPosition(localV, point, ForceMode.VelocityChange);
        }
    }

    /// 找出离命中点最近的骨骼部件（即被砍中的部位）：按碰撞体表面最近点判定，
    /// 比用质心准确（胶囊质心偏离骨骼原点，容易把"砍左脚"判成右脚）
    Rigidbody NearestPart(Vector3 point)
    {
        Rigidbody best = null;
        float bestD = float.MaxValue;
        foreach (var p in parts)
        {
            if (p == null) continue;
            float d = DistanceToPart(p, point);
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    /// 部件到命中点的距离：取碰撞体表面最近点（无碰撞体时退回质心）
    float DistanceToPart(Rigidbody p, Vector3 point)
    {
        var col = p.GetComponent<Collider>();
        Vector3 closest = col != null ? col.ClosestPoint(point) : p.worldCenterOfMass;
        return Vector3.Distance(closest, point);
    }

    /// <summary>
    /// 按"点到骨骼线段的距离"找出离命中点最近的部件（即被击中的部位），供外部局部受力使用。
    /// 存活期间部件的 collider 被禁用，ClosestPoint 会退化失效，所以这里走纯几何的线段距离。
    /// </summary>
    public Rigidbody NearestPartByBone(Vector3 point, out float distance)
    {
        BuildBoneSegments();
        Rigidbody best = null;
        float bestD = float.MaxValue;
        foreach (var kv in childOfPart)
        {
            if (kv.Key == null) continue;
            float d = DistanceToBoneSegment(kv.Key, point);
            if (d < bestD) { bestD = d; best = kv.Key; }
        }
        distance = bestD;
        return best;
    }

    /// 按需重建"部件 → 子骨骼"映射：场景里的布娃娃往往是预生成并保存好的，
    /// 不会重新走 Build()，所以这里在首次查询时才建立映射（不依赖 Build 被调用）。
    void BuildBoneSegments()
    {
        if (boneSegmentsReady) return;
        childOfPart.Clear();

        foreach (var rb in parts)
        {
            if (rb == null) continue;
            string childName = ChildNameOf(rb.transform.name);
            Transform child = null;
            if (!string.IsNullOrEmpty(childName))
            {
                // 骨架里子骨骼就是该骨骼的直接子节点（子骨骼恒在局部 +Y，见 Build）
                for (int i = 0; i < rb.transform.childCount; i++)
                {
                    var c = rb.transform.GetChild(i);
                    if (c != null && c.name == childName) { child = c; break; }
                }
            }
            childOfPart[rb] = child;
        }

        boneSegmentsReady = true;
    }

    /// 查骨骼规格表里某根骨骼对应的子骨骼名（末端骨骼返回空串）
    public static string ChildNameOf(string bone)
    {
        for (int i = 0; i < Specs.Length; i++)
        {
            var s = Specs[i];
            if (s != null && s.bone == bone) return s.child;
        }
        return "";
    }

    /// 点到骨骼线段的距离：线段 = 骨骼原点 → 子骨骼原点；末端骨骼退化为到原点的距离
    public float DistanceToBoneSegment(Rigidbody part, Vector3 point)
    {
        if (part == null) return float.MaxValue;
        Transform a = part.transform;
        Transform b;
        childOfPart.TryGetValue(part, out b);
        if (b == null) return Vector3.Distance(a.position, point);

        Vector3 ab = b.position - a.position;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-8f) return Vector3.Distance(a.position, point);
        float t = Mathf.Clamp01(Vector3.Dot(point - a.position, ab) / len2);
        return Vector3.Distance(point, a.position + ab * t);
    }

    Transform FindPelvis()
    {
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "pelvis") return t;
        }
        return null;
    }

    /// 布娃娃状态下让所有蒙皮网格始终可见（否则会被视锥剔除掉）
    void PrepareRenderersForRagdoll(Transform pelvis)
    {
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
                p.sleepThreshold = deathSleepThreshold;
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
            bone.gameObject.layer = ragdollLayer;   // 归入布娃娃层（层内互不碰撞）

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
            var th = new SoftJointLimit(); th.limit = 40f; th.bounciness = 0f; joint.highTwistLimit = th;
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
