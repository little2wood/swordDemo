using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 灌木交互摆动：玩家/敌人走到灌木附近时，灌木朝角色移动方向倾倒并左右回弹，离开后自动回正。
/// 挂在植被父物体上（默认收集所有直接子物体），不依赖物理碰撞，纯距离检测，开销极低。
/// 摆动只作用于 Transform 旋转（绕根部），因此不改动材质、不影响批处理之外的渲染。
/// </summary>
public class BushSwayField : MonoBehaviour
{
    [Header("检测")]
    public float radius = 2.4f;                 // 触发半径（米）
    public float refreshSourceInterval = 2f;    // 重新扫描触发源（玩家/敌人）的间隔（秒）
    [Range(0.1f, 3f)] public float yTolerance = 1.5f;   // 触发源与灌木的高度差容差，避免山坡上误触发

    [Header("摆动")]
    public float maxAmplitude = 16f;            // 最大摆角（度）
    public float frequency = 8f;                // 摆动频率（Hz 级，越大抖得越快）
    public float riseSpeed = 12f;               // 接触时振幅上升速度
    public float dampSpeed = 3.2f;              // 离开后回正速度
    public float fullSpeedReference = 3f;       // 达到满振幅所需的移动速度（m/s）；静止时仍有 35% 基础摆动

    [Header("触发源")]
    public string playerName = "Player";        // 玩家对象名
    public string enemyKeyword = "Enemy";       // 敌人对象名关键字

    private class Bush
    {
        public Transform t;
        public Quaternion baseRot;
        public Vector3 pos;
        public float phase;
        public float amp;
        public float targetAmp;
        public Vector3 axis;
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
                axis = Vector3.right
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

            float target = 0f;
            Vector3 pushDir = Vector3.zero;

            for (int k = 0; k < sources.Count; k++)
            {
                Source s = sources[k];
                if (s.t == null) continue;

                Vector3 d = b.pos - s.t.position;
                if (Mathf.Abs(d.y) > yTolerance) continue;
                d.y = 0f;
                float dist2 = d.sqrMagnitude;
                if (dist2 > r2) continue;

                float dist = Mathf.Sqrt(dist2);
                float falloff = 1f - Mathf.Clamp01(dist / radius);
                float speedFactor = Mathf.Clamp01(s.velocity.magnitude / Mathf.Max(0.01f, fullSpeedReference));
                float amp = maxAmplitude * falloff * (0.35f + 0.65f * speedFactor);
                if (amp <= target) continue;

                target = amp;
                Vector3 moveDir = s.velocity;
                moveDir.y = 0f;
                // 角色在动：沿移动方向倒；角色静止：被挤向远离角色的一侧
                pushDir = moveDir.sqrMagnitude > 0.25f ? moveDir.normalized : -d.normalized;
            }

            if (target > 0.01f)
            {
                b.targetAmp = target;
                Vector3 axis = Vector3.Cross(Vector3.up, pushDir);
                if (axis.sqrMagnitude > 0.0001f) b.axis = axis.normalized;
            }
            else
            {
                b.targetAmp = 0f;
            }

            float k2 = b.targetAmp > b.amp ? riseSpeed : dampSpeed;
            b.amp = Mathf.Lerp(b.amp, b.targetAmp, 1f - Mathf.Exp(-k2 * dt));
            b.phase += dt * frequency;

            if (b.amp > 0.03f)
            {
                float angle = b.amp * Mathf.Sin(b.phase);
                b.t.rotation = b.baseRot * Quaternion.AngleAxis(angle, b.axis);
            }
            else if (b.amp > 0f)
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
