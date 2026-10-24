using UnityEngine;

/// <summary>
/// 盾牌瞄准指示：用与盾飞行完全相同的积分（同重力档、同阻尼、同步长）逐段做射线，
/// 取第一个撞到的点，在那儿画一个贴着表面的圆环 —— 看到的就是真会打中的地方。
/// 只在按住空格瞄准时显示，平时不画；标记是运行时生成，不往工程里写任何资产。
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

    [Header("瞄准时突出显示")]
    public float aimScale = 1.6f;                               // 按住空格瞄准时圆盘放大倍数
    public Color aimColor = new Color(0.35f, 1f, 0.55f, 0.95f); // 瞄准时的高亮色

    GameObject discObj;
    MeshRenderer discR;
    Transform discT;
    bool aiming;
    const int DiscSegments = 32;

    void Awake()
    {
        if (shield == null) shield = GetComponent<ShieldThrow>();
        if (shield == null) shield = GetComponentInParent<ShieldThrow>();
        if (aimCamera == null) aimCamera = Camera.main;

        // 实心圆盘：单位半径的扇形 mesh，用 position/rotation/scale 摆放，不用每帧重建网格
        discObj = new GameObject("ShieldImpactDisc");
        discObj.hideFlags = HideFlags.HideAndDontSave;       // 不在 Hierarchy 里留残骸，也不参与保存
        var mf = discObj.AddComponent<MeshFilter>();
        mf.sharedMesh = MakeDisc(DiscSegments);
        discR = discObj.AddComponent<MeshRenderer>();
        var mat = new Material(Shader.Find("Sprites/Default"));
        mat.color = new Color(1f, 1f, 1f, 0.85f);
        mat.renderQueue = 3000;                              // transparent，避免和不透明物体抢排序
        discR.sharedMaterial = mat;
        discR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        discR.receiveShadows = false;
        discT = discObj.transform;
        discObj.SetActive(false);
    }

    void OnDestroy()
    {
        if (discObj != null) Destroy(discObj);
    }

    /// 生成一个朝向 +Z 的实心圆盘（半径 1），用位移/旋转/缩放摆到命中处
    static Mesh MakeDisc(int segments)
    {
        var mesh = new Mesh { name = "ReticleDisc" };
        var verts = new Vector3[segments + 1];
        var uvs = new Vector2[segments + 1];
        verts[0] = Vector3.zero;
        uvs[0] = new Vector2(0.5f, 0.5f);
        for (int i = 0; i < segments; i++)
        {
            float a = (float)i / segments * Mathf.PI * 2f;
            float x = Mathf.Cos(a), y = Mathf.Sin(a);
            verts[i + 1] = new Vector3(x, y, 0f);
            uvs[i + 1] = new Vector2(x * 0.5f + 0.5f, y * 0.5f + 0.5f);
        }
        var tris = new int[segments * 3];
        for (int i = 0; i < segments; i++)
        {
            int n = (i + 1 == segments) ? 1 : i + 2;
            tris[i * 3] = 0;
            tris[i * 3 + 1] = i + 1;
            tris[i * 3 + 2] = n;
        }
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    void LateUpdate()
    {
        UpdateVisual();
    }

    /// <summary>按住空格瞄准时调用：圆盘放大并变高亮色，突出预瞄点</summary>
    public void SetAiming(bool on)
    {
        aiming = on;
    }

    /// <summary>更新命中标记（LateUpdate 自动调用；也可手动调用来预览）</summary>
    public void UpdateVisual()
    {
        ShowRing(false);
        if (!showImpactMarker) return;
        if (shield == null || !shield.InHand) return;
        if (!aiming) return;   // 预瞄点只在按住空格瞄准时显示，其他时候不画

        // 起点与真实甩出点完全一致（含 AimSideOffset），预瞄点才会等于真实落点
        Vector3 aim = shield.AimDirection();
        Vector3 pos = shield.AimStartPoint();
        Vector3 v = aim * shield.throwSpeed;
        float dt = Time.fixedDeltaTime;             // 与 ShieldThrow.FixedUpdate 用同一个物理步长（同源，项目改步长也自动一致）
        float travelled = 0f;
        bool hitted = false;
        Vector3 hitPoint = Vector3.zero, hitNormal = Vector3.up;

        while (travelled < maxRange)
        {
            // 与真实飞行逐步相同：每个物理步先跑 FixedUpdate（加重力/阻尼/限速），
            // 物理引擎随后用改好的速度积分位移 —— 所以预测必须"先加速、后位移"（半隐式欧拉），
            // 顺序反了会让预测轨迹系统性偏高（重力每步晚一步生效）
            v += Physics.gravity * shield.gravityScale * dt;
            v *= 1f / (1f + shield.flightDrag * dt);
            v = Vector3.ClampMagnitude(v, shield.maxSpeed);

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
        }

        if (!hitted)
        {
            // 没打到东西：把标记放在轨迹最远端，用灰色表示"这里没有目标"
            DrawRing(pos, Vector3.up, missColor);
            return;
        }
        DrawRing(hitPoint, hitNormal, hitColor);
    }

    /// 一小段射线：命中的是主角自身就跳过继续往前找
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
        if (discT == null) return;

        // +Z 对准被瞄准表面的法线 → 圆盘贴着表面；抬一点点避免陷进去打 z-fighting
        discT.position = center + normal * 0.012f;
        discT.rotation = Quaternion.LookRotation(normal);
        discT.localScale = Vector3.one * ringRadius * (aiming ? aimScale : 1f);
        discR.sharedMaterial.color = aiming ? aimColor : c;
        discObj.SetActive(true);
    }

    void ShowRing(bool on)
    {
        if (discObj == null) return;
        discObj.SetActive(on);
    }

}
