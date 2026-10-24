using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 帧间体积扫掠检测（纯 C# 类，由持有它的控制器逐帧驱动，避免额外 MonoBehaviour 的执行顺序问题）：
/// 调用方每帧喂判定体积的<b>本帧</b>位置（胶囊轴两端点，两端重合即按球处理），
/// 每帧双重检测并输出全部命中——
/// ① 初始重叠（cast 不报告初始重叠，上一帧体积终点的重叠由此衔接覆盖）；
/// ② 帧间扫掠（上一帧体积沿本帧位移方向扫过帧间距，帧率无关、高速不隧穿）。
/// 挥扫方向带两级 fallback（fallbackDir → Vector3.forward）；
/// 判定点线速度 = 帧间位移 / dt（dt 钳 1/240~1/20，防卡顿帧算出荒谬速度）。
/// 命中去重与结算不在这里做（见 MeleeHitResolver / 调用方）。
/// </summary>
public class SweepProbe
{
    public readonly struct Hit
    {
        public readonly Collider collider;
        public readonly Vector3 point;     // 命中点：重叠=物体上离前沿端点最近处；扫掠=cast 自带命中点
        public readonly Vector3 sweepDir;  // 本帧挥扫方向（已走 fallback；未保证归一化，Health.TakeDamage 内会归一化）
        public readonly Vector3 velocity;  // 判定点线速度（m/s），供冲量结算（J = 速度 × 有效质量）

        public Hit(Collider collider, Vector3 point, Vector3 sweepDir, Vector3 velocity)
        {
            this.collider = collider;
            this.point = point;
            this.sweepDir = sweepDir;
            this.velocity = velocity;
        }
    }

    /// 返回 true = 跳过该碰撞体（默认不过滤）
    public System.Func<Collider, bool> filter;

    /// 本帧检测所用的上一帧体积端点（调试画扫掠线用；做过检测的帧才有效）
    public Vector3 prevA { get; private set; }
    public Vector3 prevB { get; private set; }

    /// 本帧是否做了检测（区别于"首帧只采样起点"）
    public bool ProbedLast { get; private set; }

    Vector3 lastA, lastB;   // 上一帧喂入的体积端点
    bool init;              // 是否已采样扫掠起点

    readonly Collider[] overlapBuf = new Collider[64];   // NonAlloc 复用缓冲，防逐帧分配
    readonly RaycastHit[] castBuf = new RaycastHit[64];

    /// <summary>
    /// 帧间扫掠检测。
    /// a/b = 本帧判定胶囊轴两端（重合即球）；radius = 半径；
    /// fallbackDir = 位移近零时的挥扫方向兜底（剑传"剑身指向"，无则传 zero 落到 forward）；
    /// results = 调用方复用的结果列表（调用前会被清空）。
    /// 首帧只采样起点、不检测。
    /// </summary>
    public void Probe(Vector3 a, Vector3 b, float radius, Vector3 fallbackDir, List<Hit> results)
    {
        results.Clear();

        if (!init)
        {
            lastA = a;
            lastB = b;
            init = true;
            ProbedLast = false;
            return;
        }

        prevA = lastA;
        prevB = lastB;
        ProbedLast = true;

        Vector3 move = b - lastB;
        Vector3 sweepDir = move;
        if (sweepDir.sqrMagnitude < 0.0001f) sweepDir = fallbackDir;   // 本帧几乎没动：退化到调用方给的参考方向
        if (sweepDir.sqrMagnitude < 0.0001f) sweepDir = Vector3.forward;

        // 判定点真实线速度（m/s）：帧间位移 / dt（dt 钳上下限，防卡顿帧算出荒谬速度）
        float dt = Mathf.Clamp(Time.deltaTime, 1f / 240f, 1f / 20f);
        Vector3 vel = move / dt;

        bool asSphere = (a - b).sqrMagnitude < 1e-10f;   // 两端重合 → 球（不依赖退化胶囊的引擎行为）

        // ① 初始重叠：本帧体积已压在目标上（上一帧终点的重叠由此衔接覆盖）
        int n = asSphere
            ? Physics.OverlapSphereNonAlloc(a, radius, overlapBuf)
            : Physics.OverlapCapsuleNonAlloc(a, b, radius, overlapBuf);
        for (int i = 0; i < n; i++)
        {
            var c = overlapBuf[i];
            if (c == null || (filter != null && filter(c))) continue;
            results.Add(new Hit(c, c.SafeClosestPoint(b), sweepDir, vel));   // 重叠无命中点，取物体上离前沿端点最近的点（地面等不支持类型走包围盒近似）
        }

        // ② 帧间扫掠：上一帧体积沿位移方向扫过帧间距（方向显式归一化 + 显式距离，语义无歧义）
        float moveLen = move.magnitude;
        if (moveLen > 1e-4f)
        {
            Vector3 dir = move / moveLen;
            n = asSphere
                ? Physics.SphereCastNonAlloc(lastA, radius, dir, castBuf, moveLen)
                : Physics.CapsuleCastNonAlloc(lastA, lastB, radius, dir, castBuf, moveLen);
            for (int i = 0; i < n; i++)
            {
                var c = castBuf[i].collider;
                if (c == null || (filter != null && filter(c))) continue;
                results.Add(new Hit(c, castBuf[i].point, sweepDir, vel));   // cast 自带命中点
            }
        }

        lastA = a;
        lastB = b;
    }

    /// 重置采样状态：本帧起重新以当前位置为扫掠起点（不检测），用于离开激活状态/新的一次攻击
    public void Reset()
    {
        init = false;
        ProbedLast = false;
    }
}
