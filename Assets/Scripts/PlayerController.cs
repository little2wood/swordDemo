using UnityEngine;

/// <summary>
/// 角色控制器：物理驱动移动（WASD 按固定屏幕方向）+ 朝向平滑对齐移动方向 + 攻击输入按状态过滤。
/// 相机为固定偏移跟随（不旋转），动画只负责表演，位移完全由 Rigidbody 决定。
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Animator))]
[RequireComponent(typeof(PlayerInput))]
public class PlayerController : MonoBehaviour
{
    [Header("移动")]
    public float moveSpeed = 6f;        // m/s

    [Header("朝向")]
    public float turnSpeed = 540f;      // deg/s，朝向转向移动方向的角速度
    public float facingOffset = 0f;     // 模型自带朝向偏移：实测模型正面朝 +Z 故为 0；若角色背对移动方向改成 180

    Rigidbody rb;
    Animator anim;
    PlayerInput input;
    Camera cam;

    // Animator 参数与状态名 hash
    static readonly int MovingId = Animator.StringToHash("Moving");
    static readonly int AttackId = Animator.StringToHash("Attack");
    static readonly int IdleH = Animator.StringToHash("Idle");
    static readonly int JogH = Animator.StringToHash("Jog");
    static readonly int AH = Animator.StringToHash("A");
    static readonly int ARecH = Animator.StringToHash("A_Rec");
    static readonly int BH = Animator.StringToHash("B");
    static readonly int BRecH = Animator.StringToHash("B_Rec");
    static readonly int CH = Animator.StringToHash("C");

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        anim = GetComponent<Animator>();
        input = GetComponent<PlayerInput>();
        cam = Camera.main;
    }

    void FixedUpdate()
    {
        Vector2 mv = input.MoveInput;
        bool hasMove = mv.sqrMagnitude > 0.01f;

        // ---- 移动方向：相机系 WASD（相机不旋转，即固定的屏幕方向） ----
        Vector3 dir = Vector3.zero;
        if (hasMove)
        {
            Vector3 fwd = cam != null ? Flat(cam.transform.forward) : Vector3.forward;
            Vector3 right = cam != null ? Flat(cam.transform.right) : Vector3.right;
            dir = (fwd * mv.y + right * mv.x).normalized;
        }

        // ---- 位移：挥砍中(A/B/C)锁定；恢复动作(A_Rec/B_Rec)与常态正常移动 ----
        if (IsAttackSwing() || !hasMove)
        {
            ZeroHorizontalVelocity();
        }
        else
        {
            Vector3 v = rb.linearVelocity;
            v.x = dir.x * moveSpeed;
            v.z = dir.z * moveSpeed;
            rb.linearVelocity = v;
        }

        // ---- 朝向：平滑转向移动方向（攻击中锁定朝向不转） ----
        if (hasMove && !IsAttackSwing())
        {
            float targetYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg + facingOffset;
            Quaternion target = Quaternion.Euler(0f, targetYaw, 0f);
            rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, target, turnSpeed * Time.fixedDeltaTime));
        }

        // ---- 动画参数 ----
        anim.SetBool(MovingId, hasMove);

        // ---- 攻击输入：按状态过滤（A/B/C 挥砍中直接忽略，不缓存）----
        if (input.AttackPressed && CanStartAttack())
        {
            anim.SetTrigger(AttackId);
        }
    }

    /// 是否处于挥砍中（A/B/C）——锁移动且不可起手/续击
    bool IsAttackSwing()
    {
        int h = anim.GetCurrentAnimatorStateInfo(0).shortNameHash;
        return h == AH || h == BH || h == CH;
    }

    /// 是否可起手/续击（Idle/Jog 起手，A_Rec/B_Rec 连击窗口）
    bool CanStartAttack()
    {
        int h = anim.GetCurrentAnimatorStateInfo(0).shortNameHash;
        return h == IdleH || h == JogH || h == ARecH || h == BRecH;
    }

    static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.normalized;
    }

    void ZeroHorizontalVelocity()
    {
        Vector3 v = rb.linearVelocity;
        if (v.x != 0f || v.z != 0f)
        {
            v.x = 0f;
            v.z = 0f;
            rb.linearVelocity = v;
        }
    }
}
