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
    [Range(0f, 89f)] public float pitch = 18f;  // 仰角：0=腰部平面，90=正上方
    public float minPitch = 0f;         // 半球底面：不允许低于腰部平面
    public float maxPitch = 85f;        // 接近天顶留余量，避免朝向奇异

    [Header("鼠标")]
    public float sensitivityX = 0.12f;  // 度/像素
    public float sensitivityY = 0.12f;  // 度/像素
    public bool invertX = false;        // 水平方向取反
    public bool invertY = false;        // 垂直方向取反

    PlayerInput input;
    float yaw;

    void Start()
    {
        if (target == null) return;
        input = target.GetComponent<PlayerInput>();

        // 初始位于主角后上方：相机水平前方 = 主角朝向
        var pc = target.GetComponent<PlayerController>();
        yaw = target.eulerAngles.y + (pc != null ? pc.facingOffset : 0f);

        Apply();
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

    /// 把 (yaw, pitch) 换算成球面上的位置并摆放相机
    void Apply()
    {
        Vector3 pivot = target.position + Vector3.up * waistHeight;   // 球心（腰部）

        // pitch 绕 X 轴抬起，yaw 绕 Y 轴环绕；back 为"从球心指向相机"的方向
        Vector3 dir = Quaternion.Euler(pitch, yaw, 0f) * Vector3.back;

        transform.position = pivot + dir * distance;
        transform.LookAt(target.position + Vector3.up * lookHeight);
    }
}
