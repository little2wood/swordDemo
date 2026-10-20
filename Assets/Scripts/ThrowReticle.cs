using UnityEngine;

/// <summary>
/// 盾牌瞄准指示：用与盾飞行完全相同的积分（同重力档、同阻尼、同步长）逐段做射线，
/// 取第一个撞到的点，在那儿画一个贴着表面的圆环 —— 看到的就是真会打中的地方。
/// 只在盾还在手上时显示；标记是运行时生成，不往工程里写任何资产。
/// </summary>
public class ThrowReticle : MonoBehaviour
{
    [Header("引用（留空自动查找）")]
    public ShieldThrow shield;
    public Camera aimCamera;

    [Header("命中标记")]
    public bool showImpactMarker = true;
    public float ringRadius = 0.14f;    // 圆环半径（m）
    public Color hitColor = new Color(0.4f, 0.9f, 1f, 0.9f);     // 有命中：青色
    public Color missColor = new Color(0.6f, 0.6f, 0.6f, 0.5f);  // 没打到东西（打到最远处）：灰色
    public float maxRange = 60f;        // 最长算多远
    public LayerMask castLayers = Physics.DefaultRaycastLayers;

    LineRenderer ring;
    GameObject ringObj;
    const int RingSegments = 28;

    void Awake()
    {
        if (shield == null) shield = GetComponent<ShieldThrow>();
        if (shield == null) shield = GetComponentInParent<ShieldThrow>();
        if (aimCamera == null) aimCamera = Camera.main;

        ringObj = new GameObject("ShieldImpactRing");
        ringObj.hideFlags = HideFlags.HideAndDontSave;       // 不在 Hierarchy 里留残骸，也不参与保存
        ring = ringObj.AddComponent<LineRenderer>();
        ring.useWorldSpace = true;
        ring.loop = true;
        ring.positionCount = RingSegments;
        ring.startWidth = ring.endWidth = 0.02f;
        ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        ring.receiveShadows = false;
        var mat = new Material(Shader.Find("Sprites/Default"));
        mat.color = Color.white;
        ring.material = mat;
        ring.enabled = false;
    }

    void OnDestroy()
    {
        if (ringObj != null) Destroy(ringObj);
    }

    void LateUpdate()
    {
        UpdateVisual();
    }

    /// <summary>更新命中标记（LateUpdate 自动调用；也可手动调用来预览）</summary>
    public void UpdateVisual()
    {
        ShowRing(false);
        if (!showImpactMarker) return;
        if (shield == null || !shield.InHand) return;

        Vector3 aim = shield.AimDirection();
        Vector3 pos = shield.transform.position + aim * shield.throwForward;
        Vector3 v = aim * shield.throwSpeed;
        float dt = 1f / 60f;                        // 与 ShieldThrow.FixedUpdate 的积分保持一致
        float travelled = 0f;
        bool hitted = false;
        Vector3 hitPoint = Vector3.zero, hitNormal = Vector3.up;

        while (travelled < maxRange)
        {
            Vector3 next = pos + v * dt;
            float segLen = Vector3.Distance(pos, next);

            RaycastHit h;
            if (CastSegment(pos, next, out h))
            {
                hitPoint = h.point;
                hitNormal = h.normal;
                hitted = true;
                break;
            }

            travelled += segLen;
            pos = next;

            // 与飞行完全相同的力学：自定义重力 + 线性阻尼 + 限速
            v += Physics.gravity * shield.gravityScale * dt;
            v *= 1f / (1f + shield.flightDrag * dt);
            v = Vector3.ClampMagnitude(v, shield.maxSpeed);
        }

        if (!hitted)
        {
            // 没打到东西：把标记放在轨迹最远端，用灰色表示"这里没有目标"
            DrawRing(pos, Vector3.up, missColor);
            return;
        }
        DrawRing(hitPoint, hitNormal, hitColor);
    }

    /// 一小段射线path Several：命中的是主角自身就跳过继续往前找
    bool CastSegment(Vector3 a, Vector3 b, out RaycastHit result)
    {
        Vector3 dir = b - a;
        float len = dir.magnitude;
        if (len < 1e-5f) { result = default(RaycastHit); return false; }
        Vector3 n = dir / len;

        while (len > 0.01f)
        {
            RaycastHit h;
            if (Physics.Raycast(a, n, out h, len, castLayers))
            {
                if (!IsSelf(h.collider)) { result = h; return true; }
                float skip = h.distance + 0.08f;     // 跳过主角自己，从碰撞点之后继续
                a += n * skip;
                len -= skip;
                continue;
            }
            break;
        }
        result = default(RaycastHit);
        return false;
    }

    bool IsSelf(Collider c)
    {
        if (c == null) return false;
        Transform owner = shield != null ? shield.owner : null;
        return owner != null && (c.transform == owner || c.transform.IsChildOf(owner));
    }

    void DrawRing(Vector3 center, Vector3 normal, Color c)
    {
        // 在被瞄准的表面上构造一对切向基，圆环贴着表面
        Vector3 up = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up;
        Vector3 t1 = Vector3.Cross(normal, up).normalized;
        Vector3 t2 = Vector3.Cross(normal, t1).normalized;

        for (int i = 0; i < RingSegments; i++)
        {
            float a = (float)i / RingSegments * Mathf.PI * 2f;
            Vector3 p = center + (t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a)) * ringRadius;
            ring.SetPosition(i, p + normal * 0.01f);   // 抬一点，避免陷进表面里
        }

        ring.startColor = c;
        ring.endColor = c;
        ShowRing(true);
    }

    void ShowRing(bool on)
    {
        if (ring == null) return;
        ring.enabled = on;
    }

}
