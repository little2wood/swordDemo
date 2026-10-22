using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 剑尖段的攻击拖尾：自行维护一条 ribbon 网格。
/// 每渲染帧记录"剑尖 → 沿刃回退 trailLength 的内端点"这两个点，相邻帧的两个端点连成四边形，
/// 于是 ribbon 正好贴合剑尖段真实扫过的区域（和命中判定用的是同一段几何），随时间淡出。
///
/// 只在挥砍的<b>有效窗口</b>内采集样本——窗口与 PlayerController 的命中判定完全相同
/// （同一套 normalizedTime 区间），所以前摇（举刀蓄力）和收势（A_Rec/B_Rec）都不留拖尾。
/// 停止采集后不再追加新样本，已生成的部分按各自的出生时间自然消散。
///
/// 注意：网格顶点写的是世界坐标，所以本组件必须挂在不受角色位移/旋转影响的对象上
/// （独立 GameObject，position/rotation 归零），否则顶点会被父级的变换再乘一次。
/// </summary>
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class SwordTrail : MonoBehaviour
{
    [Header("引用（留空自动查找）")]
    public PlayerController controller;     // 挥砍窗口与朝向状态从它读
    public Transform bladeBase;             // 剑根
    public Transform bladeTip;              // 剑尖

    [Header("几何")]
    [Tooltip("参与拖尾的剑尖段长度（米）：从剑尖沿刃往回取，决定 ribbon 的宽度")]
    public float trailLength = 0.28f;
    [Tooltip("样本的存活时间（秒）：越长拖尾拖得越远")]
    public float lifetime = 0.16f;
    [Tooltip("最多保留多少帧样本：防止极端掉帧时顶点数失控")]
    public int maxSamples = 48;

    [Header("外观")]
    [Tooltip("刚生成时（最靠近剑的那端）的颜色与透明度")]
    public Color colorHot = new Color(1f, 0.92f, 0.60f, 0.90f);
    [Tooltip("完全老化时的颜色：alpha 为 0 时自然消失")]
    public Color colorCold = new Color(1f, 0.35f, 0.08f, 0f);
    [Tooltip("内侧（靠剑根那一边）顶点相对外侧的透明度占比：小于 1 让拖尾越靠刃越亮")]
    [Range(0f, 1f)] public float innerAlpha = 0.25f;

    Mesh mesh;
    MeshFilter filter;
    MeshRenderer rend;

    // 样本列表：三条平行记录的 [0] 是最老的一帧
    readonly List<Vector3> tipPts = new List<Vector3>(48);      // 剑尖点
    readonly List<Vector3> innerPts = new List<Vector3>(48);    // 沿刃回退后的内端点
    readonly List<float> birthTimes = new List<float>(48);      // 采样时刻（Time.time）

    // 每帧重建网格用的复用缓冲，避免持续 new
    readonly List<Vector3> verts = new List<Vector3>(96);
    readonly List<Color> cols = new List<Color>(96);
    readonly List<Vector2> uvs = new List<Vector2>(96);
    readonly List<int> tris = new List<int>(288);

    bool prevActive;
    bool initialized;

    void Awake() { EnsureInit(); }

    /// <summary>
    /// 懒初始化：编辑模式下 AddComponent 不会走 Awake，改在 LateUpdate 里按需补齐，
    /// 保证无论组件是运行时创建还是编辑器里挂上都能正常工作（幂等）。
    /// </summary>
    void EnsureInit()
    {
        if (initialized) return;
        initialized = true;

        if (controller == null) controller = FindObjectOfType<PlayerController>();

        filter = GetComponent<MeshFilter>();
        rend = GetComponent<MeshRenderer>();

        if (rend.sharedMaterial == null)
        {
            rend.sharedMaterial = CreateTrailMaterial();
        }

        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows = false;
        rend.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

        if (filter.sharedMesh == null)
        {
            mesh = new Mesh { name = "SwordTrail" };
            mesh.MarkDynamic();
            filter.sharedMesh = mesh;
        }
        else
        {
            mesh = filter.sharedMesh;
        }

        rend.enabled = false;
    }

    void LateUpdate()
    {
        EnsureInit();
        ResolveBlade();

        bool active = IsStreamingWindow();

        if (active)
        {
            // 进入新的一刀：先断开，避免把上一刀的尾巴连成一条跨刀的长带
            if (!prevActive) ClearSamples();
            AddSample();
        }

        Expire();
        RebuildMesh();

        prevActive = active;
    }

    /// <summary>
    /// 是否应该采集拖尾：与挥砍命中判定共用同一个窗口（PlayerController.IsSwingWindow），
    /// 因此前摇和收势都不会产生拖尾。
    /// </summary>
    bool IsStreamingWindow()
    {
        return controller != null && controller.IsSwingWindow();
    }

    /// <summary>自动查找剑根/剑尖：优先用 Inspector 指定的；没指定就在主角子树里按名字找。</summary>
    void ResolveBlade()
    {
        if (bladeBase != null && bladeTip != null) return;

        Transform root = controller != null ? controller.transform : transform;
        if (bladeBase == null) bladeBase = FindDeep(root, "Sword");
        if (bladeTip == null) bladeTip = FindDeep(root, "SwordTip");
    }

    void AddSample()
    {
        if (bladeTip == null) return;

        Vector3 tip = bladeTip.position;
        Vector3 root = bladeBase != null ? bladeBase.position : tip;
        Vector3 dir = tip - root;
        float len = dir.magnitude;
        // 内端点：从剑尖沿刃往回退 trailLength（不超过实际剑长）
        Vector3 inner = len > 1e-5f ? tip - dir / len * Mathf.Min(trailLength, len) : tip;

        tipPts.Add(tip);
        innerPts.Add(inner);
        birthTimes.Add(Time.time);

        if (tipPts.Count > maxSamples) RemoveOldest();
    }

    void Expire()
    {
        float now = Time.time;
        while (tipPts.Count > 0 && now - birthTimes[0] > lifetime) RemoveOldest();
    }

    void RemoveOldest()
    {
        tipPts.RemoveAt(0);
        innerPts.RemoveAt(0);
        birthTimes.RemoveAt(0);
    }

    void ClearSamples()
    {
        tipPts.Clear();
        innerPts.Clear();
        birthTimes.Clear();
    }

    void RebuildMesh()
    {
        int n = tipPts.Count;
        rend.enabled = n >= 2;
        if (!rend.enabled)
        {
            if (mesh != null && mesh.vertexCount > 0) mesh.Clear();
            return;
        }

        verts.Clear();
        cols.Clear();
        uvs.Clear();
        tris.Clear();

        float now = Time.time;
        for (int i = 0; i < n; i++)
        {
            float age01 = Mathf.Clamp01((now - birthTimes[i]) / lifetime);
            Color c = Color.Lerp(colorHot, colorCold, age01);

            verts.Add(tipPts[i]);
            verts.Add(innerPts[i]);

            cols.Add(c);
            cols.Add(new Color(c.r, c.g, c.b, c.a * innerAlpha));

            float u = n > 1 ? i / (float)(n - 1) : 0f;
            uvs.Add(new Vector2(u, 1f));
            uvs.Add(new Vector2(u, 0f));
        }

        for (int i = 0; i < n - 1; i++)
        {
            int a = i * 2;
            int b = a + 1;
            int c = a + 2;
            int d = a + 3;
            tris.Add(a); tris.Add(c); tris.Add(b);
            tris.Add(b); tris.Add(c); tris.Add(d);
        }

        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetColors(cols);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
    }

    void OnDisable()
    {
        ClearSamples();
        if (rend != null) rend.enabled = false;
    }

    /// <summary>递归查找子孙节点（按名字精确匹配）。</summary>
    static Transform FindDeep(Transform root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var r = FindDeep(root.GetChild(i), name);
            if (r != null) return r;
        }
        return null;
    }

    /// <summary>
    /// 默认材质：URP 的 Particles/Unlit（支持顶点色 alpha）+ 透明混合、关闭深度写入与背面剔除。
    /// 用 URP 专用 shader 是因为项目走 URP 管线，且该 shader 会把顶点色正确乘进最终颜色；
    /// 两个 URP shader 都找不到时退化到 Sprites/Default（Built-in 管线的兜底）。
    /// </summary>
    static Material CreateTrailMaterial()
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh == null) return null;

        var m = new Material(sh) { name = "SwordTrail" };
        m.SetFloat("_Surface", 1f);                                                 // 1 = Transparent
        m.SetFloat("_Blend", 0f);                                                   // 0 = Alpha 混合
        m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        m.SetFloat("_Cull", 0f);                                                    // 双面：从背面也能看到拖尾
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = 3000;
        return m;
    }
}
