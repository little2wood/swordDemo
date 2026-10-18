using UnityEngine;

/// <summary>
/// 敌人 AI：待机 → 发现玩家后朝玩家走 → 进入攻击距离停步并周期性攻击。
/// 位移由 Rigidbody 驱动，动画只负责表演（与主角一致：不使用 root motion）。
/// 攻击动画播放期间锁定移动，避免"边挥边滑"。
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Animator))]
public class EnemyAI : MonoBehaviour
{
    [Header("目标")]
    public Transform target;            // 留空则在 Awake 里按名字 Player 自动查找

    [Header("感知")]
    public float detectRange = 12f;     // 进入该距离开始追击
    public float attackRange = 2.2f;    // 进入该距离停下攻击（剑/锤够得到的距离）

    [Header("战斗")]
    public float attackInterval = 3f;   // 两次攻击的间隔（秒），从起手时刻开始计时
    public float attackDamage = 20f;    // 每次挥爪伤害（主角锁血时不会真扣）
    public float hitDelay = 0.45f;      // 起手到判定的延迟（对准挥爪挥出去的瞬间）
    public float moveSpeed = 2.6f;      // 追击速度（略慢于主角，给玩家走位空间）
    public float turnSpeed = 360f;      // deg/s，转向玩家的角速度

    [Header("朝向")]
    public float facingOffset = 0f;     // 模型正面与 transform.forward 的夹角；若角色倒着走改成 180

    Rigidbody rb;
    Animator anim;

    float nextAttackTime;
    float pendingHitAt = -1f;   // 到点执行挥爪判定的时间；<0 表示无待处理判定

    static readonly int MovingId = Animator.StringToHash("Moving");
    static readonly int AttackId = Animator.StringToHash("Attack");
    static readonly int AttackStateH = Animator.StringToHash("Attack");

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        anim = GetComponent<Animator>();
        if (target == null)
        {
            var p = GameObject.Find("Player");
            if (p != null) target = p.transform;
        }
    }

    void FixedUpdate()
    {
        if (target == null) return;

        // 挥爪判定：起手后延迟 hitDelay 判一次
        if (pendingHitAt > 0f && Time.time >= pendingHitAt)
        {
            pendingHitAt = -1f;
            TryClawHit();
        }

        Vector3 toTarget = target.position - transform.position;
        toTarget.y = 0f;
        float dist = toTarget.magnitude;

        // 攻击动画播放中：锁住移动，只保持面向玩家
        if (IsAttacking())
        {
            Stop();
            FaceTo(toTarget);
            anim.SetBool(MovingId, false);
            return;
        }

        if (dist <= attackRange)
        {
            // 进入攻击距离：停步，按间隔攻击
            Stop();
            FaceTo(toTarget);
            anim.SetBool(MovingId, false);
            if (Time.time >= nextAttackTime)
            {
                anim.SetTrigger(AttackId);
                nextAttackTime = Time.time + attackInterval;
                pendingHitAt = Time.time + hitDelay;
            }
        }
        else if (dist <= detectRange)
        {
            // 发现玩家：边转向边追
            FaceTo(toTarget);
            Move(toTarget.normalized);
            anim.SetBool(MovingId, true);
        }
        else
        {
            // 超出感知：原地待机
            Stop();
            anim.SetBool(MovingId, false);
        }
    }

    /// 挥爪命中：距离与朝向双条件，命中主角（主角锁血时只广播事件不掉血）
    void TryClawHit()
    {
        if (target == null) return;
        var hp = target.GetComponent<Health>();
        if (hp == null || hp.IsDead || hp.team != Health.Team.Player) return;

        Vector3 to = target.position - transform.position;
        to.y = 0f;
        if (to.magnitude > attackRange * 1.35f) return;     // 攻击过程中玩家可能走开一点

        hp.TakeDamage(attackDamage, to.normalized);
    }

    /// 是否正在播放攻击动画（含 Attack→Idle/Walk 的过渡，仍按源状态判定）
    bool IsAttacking()
    {
        return anim.GetCurrentAnimatorStateInfo(0).shortNameHash == AttackStateH;
    }

    void FaceTo(Vector3 toTarget)
    {
        if (toTarget.sqrMagnitude < 0.0001f) return;
        float yaw = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg + facingOffset;
        Quaternion want = Quaternion.Euler(0f, yaw, 0f);
        rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, want, turnSpeed * Time.fixedDeltaTime));
        rb.angularVelocity = Vector3.zero;
    }

    void Move(Vector3 dir)
    {
        Vector3 v = rb.linearVelocity;
        v.x = dir.x * moveSpeed;
        v.z = dir.z * moveSpeed;
        rb.linearVelocity = v;
    }

    void Stop()
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
