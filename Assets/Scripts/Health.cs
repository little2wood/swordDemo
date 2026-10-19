using System;
using UnityEngine;

/// <summary>
/// 血量组件：主角与敌人共用。
/// 受伤/死亡以事件广播，布娃娃、UI、特效等自行订阅，不在这里耦合。
/// </summary>
public class Health : MonoBehaviour
{
    public enum Team { Player = 0, Enemy = 1 }

    [Header("阵营")]
    public Team team = Team.Enemy;

    [Header("血量")]
    public float maxHp = 100f;
    [SerializeField] float hp = 100f;

    [Header("调试")]
    public bool invincible = false;   // 锁血：只广播受击事件，不扣血（主角测试用）

    /// <summary>
    /// 受击时触发。参数1=受力方向（世界坐标，单位向量）；
    /// 参数2=命中冲量（N·s，武器速度 × 武器有效质量），受击反馈按它决定晃动幅度，0 表示未知/无物理量。
    /// </summary>
    public event Action<Vector3, float> OnDamaged;
    /// <summary>
    /// 死亡时触发。参数1=致死那一下的受力方向（单位向量）；
    /// 参数2=致死那一下的命中冲量（N·s），布娃娃据此决定飞多远（0 表示未知，按满档处理）；
    /// 参数3=命中点（世界坐标），布娃娃据此决定"被砍中的是哪个部位"（未传入时为 Vector3.zero）。
    /// </summary>
    public event Action<Vector3, float, Vector3> OnDeath;

    public float CurrentHp
    {
        get { return hp; }
    }

    public bool IsDead { get; private set; }

    void Awake()
    {
        hp = maxHp;     // 始终以 maxHp 作为初始血量，避免改了 maxHp 却不生效
    }

    /// <summary>
    /// 造成伤害。hitDir 为受力方向（世界坐标，会被归一化）；
    /// impulse 为命中冲量（N·s = 武器速度 × 武器有效质量），仅用于受击反馈的强度标尺，不影响扣血；
    /// point 为命中点（世界坐标），用于布娃娃判断被击中的部位（不传则退化为整体冲量）。
    /// </summary>
    public void TakeDamage(float amount, Vector3 hitDir, float impulse = 0f, Vector3 point = default(Vector3))
    {
        if (IsDead) return;
        if (hitDir.sqrMagnitude > 0.0001f) hitDir.Normalize();

        if (!invincible)
        {
            hp -= amount;
            if (hp <= 0f)
            {
                hp = 0f;
                IsDead = true;
                if (OnDamaged != null) OnDamaged(hitDir, impulse);
                if (OnDeath != null) OnDeath(hitDir, impulse, point);
                return;
            }
        }

        if (OnDamaged != null) OnDamaged(hitDir, impulse);
    }

    /// <summary>回满血并复活（测试用）</summary>
    public void Reset()
    {
        hp = maxHp;
        IsDead = false;
    }
}
