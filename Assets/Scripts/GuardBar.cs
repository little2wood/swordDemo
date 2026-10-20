using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 架势条 HUD：屏幕左下角，玩家血条正下方，橙色填充。
/// 只做显示 —— 数值与破防状态都来自 PlayerGuard。
/// 破防瞬间架势值会被拉满，所以这里额外把条改成"破防色"来提示正处于破防硬直。
/// </summary>
public class GuardBar : MonoBehaviour
{
    [Header("绑定")]
    public PlayerGuard guard;           // 留空则取自身子树

    [Header("屏幕空间（HUD）")]
    public Vector2 screenPos = new Vector2(24f, 24f);
    public Vector2 screenSize = new Vector2(320f, 22f);

    [Header("外观")]
    public Color fillColor = new Color(1f, 0.55f, 0.1f, 1f);      // 橙
    public Color backColor = new Color(0f, 0f, 0f, 0.65f);
    public Color breakColor = new Color(0.5f, 0.22f, 0.05f, 1f);  // 破防中：暗橙

    Canvas canvas;
    RectTransform fillRt;
    Image fillImg;
    static Sprite whiteSprite;

    void Awake()
    {
        if (guard == null) guard = GetComponentInChildren<PlayerGuard>();
        if (guard == null) guard = GetComponentInParent<PlayerGuard>();
        Build();
    }

    void LateUpdate()
    {
        if (guard == null || fillRt == null) return;

        float ratio = guard.Ratio;
        fillRt.anchorMax = new Vector2(ratio, 1f);
        fillRt.offsetMin = Vector2.zero;
        fillRt.offsetMax = Vector2.zero;

        if (fillImg != null) fillImg.color = guard.IsStunned ? breakColor : fillColor;
    }

    void Build()
    {
        var go = new GameObject("GuardBar_HUD");
        canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        canvas.transform.SetParent(null, false);
        DontDestroyOnLoad(go);

        float pxW = 200f, pxH = 20f;

        // --- 条容器：背景/前景只相对它拉伸，否则屏幕空间会铺满整屏 ---
        var rootGo = new GameObject("Bar");
        rootGo.transform.SetParent(canvas.transform, false);
        var crt = rootGo.AddComponent<RectTransform>();
        crt.anchorMin = Vector2.zero;
        crt.anchorMax = Vector2.zero;
        crt.pivot = Vector2.zero;
        crt.anchoredPosition = screenPos;
        crt.sizeDelta = screenSize;

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

        // --- 前景（架势值）---
        var fillGo = new GameObject("Fill");
        fillGo.transform.SetParent(rootGo.transform, false);
        fillImg = fillGo.AddComponent<Image>();
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
