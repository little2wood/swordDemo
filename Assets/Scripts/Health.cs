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

    /// <summary>受击时触发，参数为受力方向（世界坐标，单位向量）</summary>
    public event Action<Vector3> OnDamaged;
    /// <summary>死亡时触发，参数为致死那一下的受力方向</summary>
    public event Action<Vector3> OnDeath;

    public float CurrentHp
    {
        get { return hp; }
    }

    public bool IsDead { get; private set; }

    void Awake()
    {
        hp = maxHp;     // 始终以 maxHp 作为初始血量，避免改了 maxHp 却不生效
    }

    /// <summary>造成伤害。hitDir 为受力方向（世界坐标，会被归一化）。</summary>
    public void TakeDamage(float amount, Vector3 hitDir)
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
                if (OnDamaged != null) OnDamaged(hitDir);
                if (OnDeath != null) OnDeath(hitDir);
                return;
            }
        }

        if (OnDamaged != null) OnDamaged(hitDir);
    }

    /// <summary>回满血并复活（测试用）</summary>
    public void Reset()
    {
        hp = maxHp;
        IsDead = false;
    }
}
