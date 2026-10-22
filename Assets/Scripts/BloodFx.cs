using UnityEngine;

/// <summary>
/// 剑砍中敌人的溅血：在命中点沿挥砍方向甩出固定一小撮血滴，抛飞后落在静态地面上就地停住，
/// 停在地上的血点随时间氧化变暗并淡出，播完连同对象一起销毁。
/// 全程运行时程序化创建（项目里没有特效 prefab），不落任何资产。
///
/// 物理链路（刻意选"模拟"而不是"解析估算落点 + 贴片"）：
///   ① 初速：锥形喷射，轴 = 剑尖本帧的挥扫方向 swingDir —— 血是"被剑刃甩出去"的，所以沿挥砍方向飞
///   ② 飞行：重力积分 + 空气阻尼，由引擎逐帧模拟，落点是飞出来的而不是算出来的
///   ③ 落地：粒子 Collision 模块走真实物理查询，撞到静态碰撞体（地面/石头）就地停住，
///      不再弹跳、不再铺贴片。只碰静态体，所以不会挂在会走动的角色身上。
/// </summary>
public class BloodFx : MonoBehaviour
{
    [Header("溅血量（固定一小喷，不按伤害/挥砍力度缩放）")]
    public int bloodCount = 24;
    [Tooltip("血滴大小（米）：太小在第三人称视角下几乎看不见")]
    public float bloodSize = 0.15f;
    [Tooltip("喷射初速（m/s）")]
    public float bloodSpeed = 4f;
    [Tooltip("喷射锥角（度）：血沿挥砍方向甩出的张角")]
    public float bloodCone = 55f;

    [Header("飞行")]
    [Tooltip("重力缩放：血比火星重，抛飞后较快坠下、横向溅不了太远")]
    public float gravity = 1.6f;
    [Tooltip("空气阻尼：速度随寿命衰减掉的比例，越大横向飞得越近，像液体而不是砂石")]
    [Range(0f, 1f)] public float drag = 0.35f;

    [Header("落地与存留")]
    [Tooltip("血滴总寿命（秒）：约半秒落地 → 地上停留 → 末段淡出。到点自动销毁，不会越积越多")]
    public float bloodLifetime = 5f;
    [Tooltip("寿命末段这么大比例内淡出到透明")]
    [Range(0.05f, 0.9f)] public float fadeRatio = 0.35f;
    [Tooltip("与哪些层碰撞（只碰静态碰撞体：地面/石头；带 Rigidbody 的角色自动排除）")]
    public LayerMask collideWith = 1;      // Default
    [Tooltip("落地能量损失：1 = 完全停住不弹（血不弹跳）")]
    [Range(0f, 1f)] public float dampen = 1f;
    [Tooltip("血点碰撞半径缩放：决定血滴停在离地多高。太小血点会埋进地面被裁掉，看着就没了")]
    [Range(0.1f, 1f)] public float radiusScale = 0.8f;

    [Header("颜色")]
    [Tooltip("新鲜血色：偏亮的红，暗红在地面上会糊成一片看不出来")]
    public Color freshColor = new Color(0.78f, 0.06f, 0.06f);
    [Tooltip("干涸血色（落地后逐渐氧化变暗）")]
    public Color dryColor = new Color(0.38f, 0.05f, 0.05f);
    [Tooltip("着色器（URP 粒子无光照，Alpha 混合）；找不到时退到 Sprites/Default")]
    public string bloodShader = "Universal Render Pipeline/Particles/Unlit";

    Material bloodMat;
    Texture2D bloodTex;

    /// <summary>
    /// 剑砍中敌人（由 PlayerController 的命中结算调用）。
    /// point = 剑尖命中点；swingDir = 剑尖这一帧的挥扫方向。
    /// </summary>
    public void Spray(Vector3 point, Vector3 swingDir)
    {
        Vector3 dir = swingDir.sqrMagnitude > 1e-6f ? swingDir.normalized : transform.forward;

        // 先停用再配置：刚 Add 出来的 ParticleSystem 默认 playOnAwake=true，
        // 激活状态下改 duration 会被 Unity 拒绝（"system is still playing"）
        var go = new GameObject("BloodSpray");
        go.SetActive(false);
        go.transform.SetPositionAndRotation(point, Quaternion.LookRotation(dir));

        var ps = go.AddComponent<ParticleSystem>();
        var rend = go.GetComponent<ParticleSystemRenderer>();

        var main = ps.main;
        main.loop = false;
        main.playOnAwake = false;
        main.duration = 0.05f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = gravity;
        main.startLifetime = new ParticleSystem.MinMaxCurve(bloodLifetime * 0.7f, bloodLifetime);
        main.startSpeed = new ParticleSystem.MinMaxCurve(bloodSpeed * 0.4f, bloodSpeed);
        main.startSize = new ParticleSystem.MinMaxCurve(bloodSize * 0.65f, bloodSize);
        main.startColor = freshColor;
        main.maxParticles = bloodCount + 16;

        var emission = ps.emission;
        emission.enabled = true;
        emission.rateOverTime = 0f;
        emission.SetBursts(new ParticleSystem.Burst[] {
            new ParticleSystem.Burst(0f, (short)Mathf.Clamp(bloodCount, 1, 400))
        });

        // 锥口朝挥砍方向：血往剑扫出去的那一侧飞
        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = bloodCone;
        shape.radius = 0.03f;

        // 空气阻尼：Unity 6 的 VelocityOverLifetime 没有 drag，用 speedModifier 曲线乘性衰减速度
        // （阻力和真实情况一样同时削弱水平与竖直分量，所以落地会比纯抛物线慢半拍）
        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.speedModifier = new ParticleSystem.MinMaxCurve(1f, DragCurve());

        // 撞到静态地面就地停住：不弹跳、不减寿，停在那儿等末段淡出
        var col = ps.collision;
        col.enabled = true;
        col.type = ParticleSystemCollisionType.World;
        col.mode = ParticleSystemCollisionMode.Collision3D;
        col.collidesWith = collideWith;
        col.enableDynamicColliders = false;   // 只碰静态体：不会挂到玩家/敌人身上跟着走
        col.bounce = 0f;
        col.dampen = dampen;
        col.lifetimeLoss = 0f;
        col.radiusScale = radiusScale;
        col.quality = ParticleSystemCollisionQuality.High;

        // 氧化变暗 + 末段淡出
        var colorOverLife = ps.colorOverLifetime;
        colorOverLife.enabled = true;
        colorOverLife.color = BloodGradient();

        // 落地后略微摊开：血点在地上随时间稍微变大一点点
        var sizeOverLife = ps.sizeOverLifetime;
        sizeOverLife.enabled = true;
        sizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, SpreadCurve());

        if (rend != null) rend.sharedMaterial = BloodMaterial();

        go.SetActive(true);
        ps.Play();
        Destroy(go, bloodLifetime + 1f);      // 兜底清理
    }

    /// 颜色随寿命变化：新鲜 → 干涸变暗 → 末段透明
    Gradient BloodGradient()
    {
        float fadeStart = 1f - Mathf.Clamp01(fadeRatio);
        var g = new Gradient();
        g.SetKeys(
            new GradientColorKey[] {
                new GradientColorKey(freshColor, 0f),
                new GradientColorKey(Color.Lerp(freshColor, dryColor, 0.6f), fadeStart * 0.8f),
                new GradientColorKey(dryColor, 1f)
            },
            new GradientAlphaKey[] {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, fadeStart),
                new GradientAlphaKey(0f, 1f)
            });
        return g;
    }

    /// 速度随寿命衰减：出手后急速减速，之后维持一个较低的末速
    AnimationCurve DragCurve()
    {
        float floor = Mathf.Clamp01(1f - drag);
        var c = new AnimationCurve();
        c.AddKey(0f, 1f);
        c.AddKey(0.4f, floor + (1f - floor) * 0.35f);
        c.AddKey(1f, floor);
        return c;
    }

    /// 尺寸随寿命变化：飞行中保持，落地后摊开成一摊（地上的血渍比飞行的血滴大得多）
    AnimationCurve SpreadCurve()
    {
        var c = new AnimationCurve();
        c.AddKey(0f, 1f);
        c.AddKey(0.15f, 1f);
        c.AddKey(0.3f, 1.6f);     // 落地即摊开
        c.AddKey(1f, 1.9f);
        return c;
    }

    /// 血材质：贴图是程序化软圆点，颜色走粒子顶点色（startColor × colorOverLifetime）
    Material BloodMaterial()
    {
        if (bloodMat != null) return bloodMat;

        var sh = Shader.Find(bloodShader);
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh == null) return null;

        bloodMat = new Material(sh);
        bloodMat.color = Color.white;
        bloodMat.SetTexture("_BaseMap", BloodTexture());   // URP 主贴图名
        bloodMat.SetTexture("_MainTex", BloodTexture());   // 内置 shader 主贴图名

        // 血要 Alpha 混合、不发光（火星用加法混合是"迸光"，血不能发光）
        bloodMat.SetFloat("_Surface", 1f);                                                  // 0=Opaque 1=Transparent
        bloodMat.SetFloat("_Blend", 0f);                                                    // 0=Alpha 1=Premultiply 2=Additive
        bloodMat.SetFloat("_ZWrite", 0f);
        bloodMat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        bloodMat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        bloodMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

        return bloodMat;
    }

    /// 程序化软圆点贴图：径向 alpha 衰减，边缘柔化，避免血点是个硬方块
    Texture2D BloodTexture()
    {
        if (bloodTex != null) return bloodTex;

        int n = 32;
        bloodTex = new Texture2D(n, n, TextureFormat.RGBA32, true);
        var px = new Color32[n * n];
        float c = (n - 1) * 0.5f;

        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float dx = x - c, dy = y - c;
                float d = Mathf.Sqrt(dx * dx + dy * dy) / c;    // 0=中心 1=边缘
                // 中心 65% 半径内接近实心，外圈再快速收边：
                // 纯线性衰减会让整颗血点都雾蒙蒙的，缩在地面上看不见
                float a = Mathf.Clamp01((1f - d) / 0.35f);
                a = a * a * (3f - 2f * a);                       // smoothstep，边缘不硬也不虚
                px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        }

        bloodTex.SetPixels32(px);
        bloodTex.Apply(true, true);
        bloodTex.wrapMode = TextureWrapMode.Clamp;
        return bloodTex;
    }

    void OnDestroy()
    {
        // 运行时创建的材质/贴图不落盘，跟着组件一起回收，避免切场景后残留
        if (bloodMat != null) Destroy(bloodMat);
        if (bloodTex != null) Destroy(bloodTex);
    }
}
