using UnityEngine;

/// <summary>
/// 固定偏移跟随相机：启动时定位到主角后上方并对准胸口，
/// 之后保持与主角的相对偏移恒定（只随主角平移，不随主角旋转、不随视角旋转）。
/// </summary>
public class ThirdPersonCamera : MonoBehaviour
{
    public Transform target;            // 跟随目标（Player）
    public float distance = 4.5f;       // 与主角的水平距离
    public float height = 2.2f;         // 相机高度
    public float lookHeight = 1.4f;     // 注视点高度（胸口）
    public float facingOffset = 180f;   // 模型自带朝向偏移，与 PlayerController 一致

    Vector3 offset;

    void Start()
    {
        if (target == null) return;
        // 以主角初始面朝方向的反向为"后方"，摆位一次
        float yaw = target.eulerAngles.y + facingOffset;
        Vector3 back = Quaternion.Euler(0f, yaw, 0f) * Vector3.back;
        transform.position = target.position + back * distance + Vector3.up * height;
        transform.rotation = Quaternion.LookRotation(
            (target.position + Vector3.up * lookHeight) - transform.position);
        offset = transform.position - target.position;
    }

    void LateUpdate()
    {
        if (target == null) return;
        // 硬锁定相对偏移：不插值、不旋转，保证"零相对位移"
        transform.position = target.position + offset;
    }
}
