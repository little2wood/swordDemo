using UnityEngine;

/// <summary>
/// 砍到石头（StoneBlock）的反馈：中断挥砍 → 播"砍不动"动画 → 期间锁住玩家操作 → 在接触点迸火星。
///
/// 为什么不靠物理阻挡"不许穿透"：剑尖碰撞体是 kinematic 胶囊、碰撞矩阵里只与 Prop 层碰，
/// 石头则是静态 MeshCollider —— 两者物理上互不干涉（kinematic 胶囊不会被静态体挡住）。
/// 真正让剑尖停住的是"命中即刻中断动画"：动画一断，剑就停在击中那一帧的姿态，
/// 接着接上"砍不动"动画的起始姿势，视觉上就是剑抵在石头上被顶回来。
///
/// 火星在运行时程序化创建（项目里没有特效 prefab），播完自行销毁，不落任何资产。
/// </summary>
public class StoneBlockReaction : MonoBehaviour
{
    [Header("引用（留空自动查找）")]
    public PlayerController controller;
    public Animator anim;

    [Header("砍不动动画（Animator Base Layer 里的状态）")]
    public string stateName = "Tree_Chop";
    [Tooltip("从第几帧开始播：clip 前段是抬手，砍到石头的部分从这里才开始")]
    public float startFrame = 13f;
    [Tooltip("clip 采样帧率")]
    public float clipFps = 30f;
    [Tooltip("clip 总时长（秒）：Armature|TreeChopping_Loop 实测 0.967s")]
    public float clipLength = 0.967f;
    [Tooltip("动画播完后淡回 Idle 的过渡时长（秒）")]
    public float recoverFade = 0.12f;

    [Header("火星")]
    public int sparkCount = 70;
    [Tooltip("火星颗粒大小（米）")]
    public float sparkSize = 0.05f;
    [Tooltip("火星初速（m/s）")]
    public float sparkSpeed = 12f;
    [Tooltip("火星寿命（秒）：短促才像迸溅而不是烟花")]
    public float sparkLifetime = 0.28f;
    [Tooltip("扩散锥角度（度）：火星沿撞击方向喷出的张角")]
    public float sparkCone = 40f;
    [Tooltip("火星着色器（URP 粒子无光照）；找不到时退到 Sprites/Default")]
    public string sparkShader = "Universal Render Pipeline/Particles/Unlit";

    int stateHash;
    int idleHash = Animator.StringToHash("Idle");
    float startTimeNorm;
    float blockTimer;
    Material sparkMat;

    /// <summary>是否处于"砍到石头"的锁定中：期间玩家不能移动/攻击/举盾/投掷/召回（镜头照常）。</summary>
    public bool IsBlocking { get { return blockTimer > 0f; } }

    void Awake()
    {
        if (controller == null) controller = GetComponent<PlayerController>();
        if (anim == null)
        {
            anim = GetComponent<Animator>();
            if (anim == null && controller != null) anim = controller.GetComponent<Animator>();
        }
        stateHash = Animator.StringToHash(stateName);
        startTimeNorm = Mathf.Clamp01((startFrame / Mathf.Max(1f, clipFps)) / Mathf.Max(0.01f, clipLength));
    }

    void Update()
    {
        if (blockTimer <= 0f) return;
        blockTimer -= Time.deltaTime;
        if (blockTimer <= 0f && anim != null)
            anim.CrossFade(idleHash, Mathf.Max(0.01f, recoverFade), 0);     // 播完淡回 Idle
    }

    /// <summary>
    /// 剑砍到石头（由 PlayerController 的命中结算调用）。
    /// point = 剑尖与石头的接触点；swingDir = 剑尖这一帧的挥扫方向。
    /// </summary>
    public void HitStone(Vector3 point, Vector3 swingDir, StoneBlock stone)
    {
        if (anim == null || IsBlocking) return;      // 已在锁定中就忽略（本刀同一块石头由调用方去重）

        // 直接 Play 到指定进度：不用 trigger，避免走状态机里那些连招/归位过渡
        anim.Play(stateHash, 0, startTimeNorm);
        blockTimer = clipLength * (1f - startTimeNorm);

        SpawnSparks(point, swingDir, stone);
    }

    /// 在接触点生成一撮火星：沿"撞击方向"喷出的锥形短命颗粒，带重力，播完连对象一起销毁
    void SpawnSparks(Vector3 point, Vector3 swingDir, StoneBlock stone)
    {
        Color color = stone != null ? stone.sparkColor : Color.yellow;
        float scale = stone != null ? Mathf.Max(0.1f, stone.sparkScale) : 1f;
        Vector3 back = swingDir.sqrMagnitude > 1e-6f ? -swingDir.normalized : Vector3.up;

        // 先停用再配置：刚 Add 出来的 ParticleSystem 默认 playOnAwake=true，
        // 激活状态下改 duration 会被 Unity 拒绝（"system is still playing"）
        var go = new GameObject("SparkBurst");
        go.SetActive(false);
        go.transform.SetPositionAndRotation(point, Quaternion.LookRotation(back));

        var ps = go.AddComponent<ParticleSystem>();
        var rend = go.GetComponent<ParticleSystemRenderer>();

        var main = ps.main;
        main.loop = false;
        main.playOnAwake = false;
        main.duration = 0.05f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = 1.8f;                // 火星很快坠下，不像烟花悬停
        main.startLifetime = new ParticleSystem.MinMaxCurve(sparkLifetime * 0.45f, sparkLifetime);
        main.startSpeed = new ParticleSystem.MinMaxCurve(sparkSpeed * 0.35f, sparkSpeed * scale);
        main.startSize = new ParticleSystem.MinMaxCurve(sparkSize * 0.5f, sparkSize);
        main.startColor = color;
        main.maxParticles = Mathf.CeilToInt(sparkCount * scale) + 24;

        var emission = ps.emission;
        emission.enabled = true;
        emission.rateOverTime = 0f;
        emission.SetBursts(new ParticleSystem.Burst[] {
            new ParticleSystem.Burst(0f, (short)Mathf.Clamp(sparkCount * scale, 1f, 400f))
        });

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = sparkCone;
        shape.radius = 0.02f;

        if (rend != null) rend.sharedMaterial = SparkMaterial();

        go.SetActive(true);
        ps.Play();
        Destroy(go, sparkLifetime + 1f);      // 兜底清理（stopAction 之外再保一层）
    }

    /// 火星材质：基底留白，颜色走粒子顶点色（startColor），这样同一材质能被不同石头复用
    Material SparkMaterial()
    {
        if (sparkMat != null) return sparkMat;

        var sh = Shader.Find(sparkShader);
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh == null) return null;

        sparkMat = new Material(sh);
        sparkMat.color = Color.white;
        return sparkMat;
    }
}
