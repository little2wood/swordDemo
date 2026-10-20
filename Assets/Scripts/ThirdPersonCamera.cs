using UnityEngine;

/// <summary>
/// 半球面轨道相机：相机始终位于以主角腰部为球心、半径 distance 的"上半球"球面上。
/// 底面（pitch=0）穿过主角腰部且与地面平行，球面向上，相机不会落到底面以下。
/// 鼠标滑动改变水平角 yaw（绕主角水平环绕）与仰角 pitch（沿球面向上/向下）。
/// 相机的水平前方即主角的前进方向（PlayerController 按相机系解释 WASD）。
/// </summary>
public class ThirdPersonCamera : MonoBehaviour
{
    [Header("跟随")]
    public Transform target;            // 跟随目标（Player）
    public float waistHeight = 1.0f;    // 半球底面高度（腰部）
    public float lookHeight = 1.4f;     // 注视点高度（胸口）

    [Header("球面")]
    public float distance = 4.5f;       // 球面半径（相机到腰部的距离）
    [Range(-45f, 89f)] public float pitch = 18f;    // 仰角：0=腰部平面，90=正上方，负=沉到腰部以下往上仰视
    public float minPitch = -45f;       // 允许负值：相机会沉到腰部以下，屏幕中心随之抬起 → 可以仰射
    public float maxPitch = 85f;        // 接近天顶留余量，避免朝向奇异
    [Tooltip("相机不低于角色脚部上方这么多米，防止负 pitch 时钻进地面")] public float minCameraHeight = 0.15f;

    [Header("鼠标")]
    public float sensitivityX = 0.06f;  // 度/像素
    public float sensitivityY = 0.06f;  // 度/像素
    public bool invertX = false;        // 水平方向取反
    public bool invertY = false;        // 垂直方向取反

    [Header("避障")]
    public LayerMask collisionLayers = ~0;  // 参与遮挡检测的层（默认全部）
    public float cameraRadius = 0.25f;      // 相机碰撞半径（约等于近裁剪面，避免边缘穿模）
    public float minDistance = 0.6f;        // 被遮挡时最近可拉到多近
    public float restoreSpeed = 4f;         // 遮挡消失后推远的速度（m/s）

    [Header("瞄准拉近（投掷瞄准用）")]
    [Range(0.1f, 1f)] public float aimDistanceScale = 0.45f;   // 瞄准时球面半径缩到原来的多少
    public float zoomSpeed = 7f;                               // 拉近/推远的速度（m/s）
    [Tooltip("瞄准时相机整体沿右侧平移的距离：靠视差把主角错到画面左边，别让模型挡住远处的预瞄点")]
    public float aimShoulderOffset = 0.6f;
    public float shoulderSpeed = 6f;                           // 越肩平移的速度（m/s）

    PlayerInput input;
    float yaw;
    float restDistance;             // 当前理想球面半径（会被瞄准拉近修改）
    float shoulder;                 // 当前越肩平移量
    float currentDistance;              // 当前"注视点 → 相机"的实际距离（沿 dir，含避障结果）
    bool distanceReady;
    bool aiming;                    // 是否处于瞄准态（由外部 SetAiming 控制）
    RaycastHit[] hits = new RaycastHit[8];

    void Start()
    {
        if (target == null) return;
        input = target.GetComponent<PlayerInput>();

        // 初始位于主角后上方：相机水平前方 = 主角朝向
        var pc = target.GetComponent<PlayerController>();
        yaw = target.eulerAngles.y + (pc != null ? pc.facingOffset : 0f);

        restDistance = distance;
        Apply();
    }

    /// <summary>切到/退出瞄准视角（球面半径在 distance 与 distance*aimDistanceScale 之间过渡）。</summary>
    public void SetAiming(bool on)
    {
        aiming = on;
    }

    /// <summary>当前理想球面半径（瞄准时更近）。</summary>
    public float CurrentRestDistance
    {
        get { return restDistance; }
    }

    void LateUpdate()
    {
        if (target == null) return;

        // ---- 鼠标 → 球面角度 ----
        if (input != null)
        {
            Vector2 md = input.MouseDelta;
            yaw += (invertX ? -1f : 1f) * md.x * sensitivityX;
            pitch += (invertY ? -1f : 1f) * md.y * sensitivityY;
            pitch = Mathf.Clamp(pitch, minPitch, maxPitch);   // 半球约束
        }

        Apply();
    }

    /// 把 (yaw, pitch) 换算成球面上的位置并摆放相机（含避障）
    void Apply()
    {
        Vector3 pivot = target.position + Vector3.up * waistHeight;   // 球心（腰部）
        Vector3 look = target.position + Vector3.up * lookHeight;     // 注视点（胸口）

        // pitch 绕 X 轴抬起，yaw 绕 Y 轴环绕；back 为"从球心指向相机"的方向
        Vector3 dir = Quaternion.Euler(pitch, yaw, 0f) * Vector3.back;

        // ---- 越肩平移：相机连同注视点整体沿"画面右方"平移 ----
        // 视差让近处的主角在画面上大幅左移、远处的预瞄点几乎不动，于是不再被挡住
        float wantShoulder = aiming ? aimShoulderOffset : 0f;
        shoulder = Mathf.MoveTowards(shoulder, wantShoulder, shoulderSpeed * Time.deltaTime);
        if (Mathf.Abs(shoulder) > 0.001f)
        {
            Vector3 camRight = Vector3.Cross(Vector3.up, -dir).normalized;
            pivot += camRight * shoulder;
            look += camRight * shoulder;
        }

        // 理想球面半径平缓过渡到"常态 / 瞄准"两档，再交给避障逻辑
        float want = aiming ? distance * aimDistanceScale : distance;
        restDistance = Mathf.MoveTowards(restDistance, want, zoomSpeed * Time.deltaTime);

        // 无遮挡时的理想距离：注视点 → 球面位置
        float desiredDist = Vector3.Distance(look, pivot + dir * restDistance);

        // ---- 避障：从注视点朝相机方向做球体投射 ----
        float allowed = desiredDist;
        int n = Physics.SphereCastNonAlloc(look, cameraRadius, dir, hits, desiredDist, collisionLayers);
        float nearest = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            Transform ht = hits[i].transform;
            if (ht == target || ht.IsChildOf(target)) continue;   // 忽略角色自身
            if (hits[i].distance < nearest) nearest = hits[i].distance;
        }
        if (nearest < float.MaxValue)
        {
            allowed = Mathf.Max(minDistance, nearest - cameraRadius);
        }

        // 拉近立即（防穿墙），推远渐进（防抖动）
        if (!distanceReady) { currentDistance = allowed; distanceReady = true; }
        else if (allowed < currentDistance) currentDistance = allowed;
        else currentDistance = Mathf.MoveTowards(currentDistance, allowed, restoreSpeed * Time.deltaTime);

        transform.position = look + dir * currentDistance;

        // 负 pitch 时相机会沉到腰部以下：把它连同注视点整体抬回地面之上（视线方向保持不变）
        if (target != null)
        {
            float floor = target.position.y + minCameraHeight;
            if (transform.position.y < floor)
            {
                float lift = floor - transform.position.y;
                transform.position += Vector3.up * lift;
                look += Vector3.up * lift;
            }
        }

        transform.LookAt(look);
    }
}
