using UnityEngine;

/// <summary>
/// 剑尖段的物理碰撞体：让剑能"真的挡到"可互动物体（Prop 层），而不是插进去。
///
/// 做法：一个 kinematic 的胶囊 Rigidbody，几何与命中判定用的是同一段剑尖
/// （轴 = 剑尖→沿刃回退 tipLength 的内端点，半径 = bladeRadius），
/// 只在挥砍窗口内启用（复用 PlayerController.IsSwingWindow，与命中判定/拖尾同一个开关）。
///
/// 三个关键点：
/// ① 物体必须挂在独立 GameObject 上（不挂到骨骼下），否则父级动画变换会再叠一次；
/// ② 必须用 MovePosition/MoveRotation 推进，直接写 transform 的话 CCD 拿不到速度，高速必穿；
/// ③ 启用瞬间要直接落位（rb.position），否则会从上一刀的残留位置扫出一条横跨全屏的扫掠。
///
/// 碰撞矩阵里 Blade 层只开 Prop，所以剑不会撞到敌人、地面、自己身体，也不参与任何其他物理。
/// 击飞力度由物理求解器决定（kinematic = 无限质量），箱子会被加速到接近剑尖速度。
/// </summary>
public class BladeCollider : MonoBehaviour
{
    [Header("引用（留空自动从 PlayerController 取）")]
    public PlayerController controller;
    public Transform bladeBase;     // 剑根
    public Transform bladeTip;      // 剑尖

    [Header("几何（与 PlayerController 的命中判定保持一致）")]
    [Tooltip("参与碰撞的剑尖段长度（米）：与 PlayerController.tipLength 一致")]
    public float tipLength = 0.25f;
    [Tooltip("剑刃半宽，即胶囊半径：与 PlayerController.bladeRadius 一致")]
    public float bladeRadius = 0.05f;

    [Header("碰撞体所在层（碰撞矩阵里该层只与 Prop 碰撞）")]
    public string layerName = "Blade";

    [Header("连续碰撞检测（剑尖可达 40 m/s 以上，必须开）")]
    public CollisionDetectionMode ccdMode = CollisionDetectionMode.Continuous;

    GameObject go;
    Rigidbody rb;
    CapsuleCollider cap;

    void Awake()
    {
        if (controller == null) controller = Object.FindFirstObjectByType<PlayerController>();
        Build();
    }

    void OnDestroy()
    {
        if (go != null) Destroy(go);
    }

    void Build()
    {
        int layer = LayerMask.NameToLayer(layerName);
        if (layer < 0)
        {
            Debug.LogWarning("[BladeCollider] 找不到层 " + layerName + "，碰撞体放在 Default 层（会撞到别的东西）", this);
            layer = 0;
        }

        // 独立对象、世界空间驱动：不能挂在骨骼下，否则父级动画变换会再叠一次
        go = new GameObject("BladeCollider");
        go.transform.SetParent(null, false);
        go.layer = layer;

        cap = go.AddComponent<CapsuleCollider>();
        cap.direction = 2;                                  // 胶囊轴沿本地 +Z（与 LookRotation 的朝向一致）
        cap.radius = bladeRadius;
        cap.height = tipLength + bladeRadius * 2f;          // Unity 的 height 含两端半球
        cap.isTrigger = false;

        rb = go.AddComponent<Rigidbody>();
        rb.isKinematic = true;                              // 动画驱动：kinematic 才不会被反作用力推歪
        rb.useGravity = false;
        rb.collisionDetectionMode = ccdMode;

        go.SetActive(false);
    }

    void FixedUpdate()
    {
        if (controller == null) return;

        // 剑的引用在 FixedUpdate 里补齐：此时所有 Awake 都已跑完，不受脚本执行顺序影响
        if (bladeBase == null) bladeBase = controller.bladeBase;
        if (bladeTip == null) bladeTip = controller.bladeTip;
        if (bladeTip == null) return;

        bool active = controller.IsSwingWindow();
        if (!active)
        {
            if (go != null && go.activeSelf) go.SetActive(false);
            return;
        }

        // 剑尖段：与命中判定完全同一段几何
        Vector3 tip = bladeTip.position;
        Vector3 root = bladeBase != null ? bladeBase.position : tip;
        Vector3 dir = tip - root;
        float len = dir.magnitude;
        if (len > 1e-5f) dir /= len; else dir = transform.forward;
        Vector3 inner = tip - dir * Mathf.Min(tipLength, len);
        Vector3 center = (tip + inner) * 0.5f;
        Quaternion rot = Quaternion.LookRotation(dir);

        if (!go.activeSelf)
        {
            go.SetActive(true);
            // 启用瞬间直接落位：否则会从上一刀残留的位置扫出一条巨大的扫掠，把沿途箱子全崩飞
            rb.position = center;
            rb.rotation = rot;
            return;
        }

        rb.MovePosition(center);
        rb.MoveRotation(rot);
    }
}
