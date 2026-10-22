using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 灌木交互摆动：玩家/敌人"进入"灌木范围时触发一次冲量式摆动，
/// 灌木朝角色移动方向倒下并正弦振荡衰减回正；停留在其中或离开时不再重复触发。
/// 挂在植被父物体上（默认收集所有直接子物体），纯距离检测，不加碰撞体，开销极低。
/// 摆动轴在触发瞬间确定，且以世界轴左乘应用到 Transform，因此不受灌木自身随机 Y 旋转影响。
/// </summary>
public class BushSwayField : MonoBehaviour
{
    [Header("检测")]
    public float radius = 3.2f;                 // 触发半径（米，水平距离）
    public float refreshSourceInterval = 2f;    // 重新扫描触发源（玩家/敌人）的间隔（秒）

    [Header("摆动")]
    public float maxAmplitude = 20f;            // 触发时的初始摆角（度）
    public float frequency = 4.5f;              // 摆动频率（越大抖得越快）
    public float dampSpeed = 1.2f;              // 摆动衰减速度（越小晃得越久）
    public float fullSpeedReference = 3f;       // 达到满幅所需的移动速度（m/s）；静止时仍有 35% 基础摆动

    [Header("触发源")]
    public string playerName = "Player";        // 玩家对象名
    public string enemyKeyword = "Enemy";       // 敌人对象名关键字

    [Header("调试")]
    public int triggerCount;                    // 累计触发次数（只读参考，确认是否命中）

    private class Bush
    {
        public Transform t;
        public Quaternion baseRot;
        public Vector3 pos;
        public float phase;
        public float amp;
        public Vector3 axis;
        public float freqMul;       // 每丛频率微扰，避免整片同步摆动
        public bool wasInside;      // 上一帧是否已有触发源在范围内（用于只在"进入"时触发）
    }

    private class Source
    {
        public Transform t;
        public Vector3 lastPos;
        public Vector3 velocity;
    }

    private readonly List<Bush> bushes = new List<Bush>();
    private readonly List<Source> sources = new List<Source>();
    private float refreshTimer;

    private void Start()
    {
        CollectBushes();
        RefreshSources();
        refreshTimer = refreshSourceInterval;
    }

    /// <summary>收集自身所有直接子物体作为可摆动植被。</summary>
    public void CollectBushes()
    {
        bushes.Clear();
        foreach (Transform child in transform)
        {
            if (child == null) continue;
            bushes.Add(new Bush
            {
                t = child,
                baseRot = child.rotation,
                pos = child.position,
                phase = Random.Range(0f, Mathf.PI * 2f),
                axis = Vector3.right,
                freqMul = Random.Range(0.85f, 1.15f),
                wasInside = false
            });
        }
    }

    /// <summary>按名字扫描玩家与敌人作为触发源。</summary>
    public void RefreshSources()
    {
        sources.Clear();

        GameObject player = GameObject.Find(playerName);
        if (player != null) AddSource(player.transform);

        Transform[] all = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null || t == transform) continue;
            if (t.name.IndexOf(enemyKeyword, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (t.GetComponentInParent<Rigidbody>() == null) continue;  // 只认带刚体的实体（排除部件名命中）
            AddSource(t);
        }
    }

    private void AddSource(Transform t)
    {
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i].t == t) return;
        }
        sources.Add(new Source { t = t, lastPos = t.position, velocity = Vector3.zero });
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        refreshTimer -= dt;
        if (refreshTimer <= 0f)
        {
            RefreshSources();
            refreshTimer = refreshSourceInterval;
        }

        for (int i = 0; i < sources.Count; i++)
        {
            Source s = sources[i];
            if (s.t == null) continue;
            Vector3 p = s.t.position;
            s.velocity = (p - s.lastPos) / dt;
            s.lastPos = p;
        }

        float r2 = radius * radius;
        for (int i = 0; i < bushes.Count; i++)
        {
            Bush b = bushes[i];
            if (b.t == null) continue;

            bool inside = false;
            float peak = 0f;
            Vector3 pushDir = Vector3.zero;

            for (int k = 0; k < sources.Count; k++)
            {
                Source s = sources[k];
                if (s.t == null) continue;

                Vector3 d = b.pos - s.t.position;
                d.y = 0f;   // 只按水平距离判定，坡上坡下同样触发
                float dist2 = d.sqrMagnitude;
                if (dist2 > r2) continue;

                inside = true;
                float speedFactor = Mathf.Clamp01(s.velocity.magnitude / Mathf.Max(0.01f, fullSpeedReference));
                // 摆幅与进入位置无关：触发点在跨进边界的瞬间，若按距离衰减会得到接近 0° 的不可见摆动
                float amp = maxAmplitude * (0.45f + 0.55f * speedFactor);
                if (amp <= peak) continue;

                peak = amp;
                Vector3 moveDir = s.velocity;
                moveDir.y = 0f;
                // 角色在动：沿移动方向倒；角色静止：被挤向远离角色的一侧
                pushDir = moveDir.sqrMagnitude > 0.25f ? moveDir.normalized : -d.normalized;
            }

            // 只在"进入"的那一帧给一次冲量
            if (inside && !b.wasInside && peak > 0.01f)
            {
                b.amp = peak;
                b.phase = 0f;                   // 相位从 0 起步：摆角从原位平滑增长，第一下自然摆向移动方向（原 π/2 会让首帧瞬移到满幅）
                Vector3 axis = Vector3.Cross(Vector3.up, pushDir);
                if (axis.sqrMagnitude > 0.0001f) b.axis = axis.normalized;
                triggerCount++;
            }
            b.wasInside = inside;

            if (b.amp <= 0f) continue;

            b.amp = Mathf.Lerp(b.amp, 0f, 1f - Mathf.Exp(-dampSpeed * dt));
            b.phase += dt * frequency * b.freqMul;

            if (b.amp > 0.03f)
            {
                float angle = b.amp * Mathf.Sin(b.phase);
                // 世界轴左乘：摆动方向不受灌木自身随机 Y 旋转影响
                b.t.rotation = Quaternion.AngleAxis(angle, b.axis) * b.baseRot;
            }
            else
            {
                b.amp = 0f;
                b.t.rotation = b.baseRot;
            }
        }
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.3f, 0.9f, 0.4f, 0.25f);
        foreach (Transform child in transform)
        {
            if (child != null) Gizmos.DrawWireSphere(child.position, radius);
        }
    }
}
