using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 血条：运行时自建 UI，无需预制体。
/// 主角用 ScreenSpace（左下角 HUD），敌人用 WorldSpace（头顶，始终面向相机）。
/// </summary>
public class HealthBar : MonoBehaviour
{
    public enum Mode { ScreenSpace, WorldSpace }

    [Header("模式")]
    public Mode mode = Mode.WorldSpace;

    [Header("绑定")]
    public Health health;               // 留空则取自身

    [Header("世界空间（头顶）")]
    public float worldWidth = 1.6f;     // 米
    public float worldHeight = 0.16f;   // 米
    public Vector3 worldOffset = new Vector3(0f, 2.2f, 0f);
    public bool billboard = true;       // 始终面向相机

    [Header("屏幕空间（HUD）")]
    public Vector2 screenPos = new Vector2(24f, 24f);
    public Vector2 screenSize = new Vector2(320f, 26f);

    [Header("外观")]
    public Color fillColor = new Color(0.85f, 0.14f, 0.14f, 1f);
    public Color backColor = new Color(0f, 0f, 0f, 0.65f);
    public bool hideOnDeath = false;    // 敌人尸体上不显示血条

    Canvas canvas;
    RectTransform fillRt;
    static Sprite whiteSprite;

    void Awake()
    {
        if (health == null) health = GetComponent<Health>();
        Build();
    }

    void LateUpdate()
    {
        if (health == null || fillRt == null) return;

        if (hideOnDeath && health.IsDead)
        {
            if (canvas != null && canvas.gameObject.activeSelf) canvas.gameObject.SetActive(false);
            return;
        }

        float ratio = health.maxHp > 0f ? Mathf.Clamp01(health.CurrentHp / health.maxHp) : 0f;
        fillRt.anchorMax = new Vector2(ratio, 1f);
        fillRt.offsetMin = Vector2.zero;
        fillRt.offsetMax = Vector2.zero;

        if (mode == Mode.WorldSpace && billboard && Camera.main != null)
        {
            canvas.transform.rotation = Camera.main.transform.rotation;
        }
    }

    void Build()
    {
        // --- 防御：清掉同名的历史残留 Canvas（如被复制模板带来的旧血条），避免双血条 ---
        var stale = transform.Find(mode == Mode.WorldSpace ? "HealthBar_World" : "HealthBar_HUD");
        if (stale != null) Destroy(stale.gameObject);

        // --- Canvas 根节点 ---
        var go = new GameObject(mode == Mode.WorldSpace ? "HealthBar_World" : "HealthBar_HUD");
        canvas = go.AddComponent<Canvas>();
        canvas.renderMode = (mode == Mode.WorldSpace) ? RenderMode.WorldSpace : RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        float pxW = 200f, pxH = 20f;

        if (mode == Mode.WorldSpace)
        {
            canvas.transform.SetParent(transform, false);
            canvas.transform.localPosition = worldOffset;
            // 用缩放把像素尺寸换算成世界尺寸；父物体若有缩放（如放大过的敌人）要除掉，否则血条会被一起放大
            float ps = Mathf.Max(0.0001f, transform.lossyScale.y);
            canvas.transform.localScale = new Vector3(worldWidth / pxW / ps, worldHeight / pxH / ps, 1f / ps);
        }
        else
        {
            canvas.transform.SetParent(null, false);
            DontDestroyOnLoad(go);
        }

        // --- 血条容器：所有模式共用，背景/前景只相对它拉伸（否则屏幕空间会铺满整屏）---
        var rootGo = new GameObject("Bar");
        rootGo.transform.SetParent(canvas.transform, false);
        var crt = rootGo.AddComponent<RectTransform>();
        if (mode == Mode.WorldSpace)
        {
            crt.anchorMin = new Vector2(0.5f, 0.5f);
            crt.anchorMax = new Vector2(0.5f, 0.5f);
            crt.pivot = new Vector2(0.5f, 0.5f);
            crt.sizeDelta = new Vector2(pxW, pxH);
            crt.anchoredPosition = Vector2.zero;
        }
        else
        {
            crt.anchorMin = Vector2.zero;
            crt.anchorMax = Vector2.zero;
            crt.pivot = Vector2.zero;
            crt.anchoredPosition = screenPos;
            crt.sizeDelta = screenSize;
        }

        // --- 背景 ---
        var backGo = new GameObject("Back");
        backGo.transform.SetParent(rootGo.transform, false);
        var backImg = backGo.AddComponent<Image>();
        backImg.sprite = White();
        backImg.color = backColor;
        backImg.raycastTarget = false;
        var backRt = backGo.GetComponent<RectTransform>();
        backRt.anchorMin = Vector2.zero;
        backRt.anchorMax = Vector2.one;
        backRt.offsetMin = Vector2.zero;
        backRt.offsetMax = Vector2.zero;

        // --- 前景（血量）---
        var fillGo = new GameObject("Fill");
        fillGo.transform.SetParent(rootGo.transform, false);
        var fillImg = fillGo.AddComponent<Image>();
        fillImg.sprite = White();
        fillImg.color = fillColor;
        fillImg.raycastTarget = false;
        fillRt = fillGo.GetComponent<RectTransform>();
        fillRt.anchorMin = Vector2.zero;
        fillRt.anchorMax = Vector2.one;
        fillRt.offsetMin = Vector2.zero;
        fillRt.offsetMax = Vector2.zero;
    }

    static Sprite White()
    {
        if (whiteSprite == null)
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var cols = new Color32[16];
            for (int i = 0; i < 16; i++) cols[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(cols);
            tex.Apply();
            whiteSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 100f);
        }
        return whiteSprite;
    }
}
