using UnityEngine;

/// <summary>
/// Collider.ClosestPoint 的安全包装。
///
/// Unity 的 Collider.ClosestPoint 只支持 Box/Sphere/Capsule 和凸 MeshCollider；
/// 对 TerrainCollider、非凸 MeshCollider 调用会每帧刷
/// "Physics.ClosestPoint can only be used with ..." 警告（挥砍/爪击扫到地面时高频触发）。
///
/// 不支持的碰撞体退回"包围盒(AABB)上的最近点"：无警告、任何碰撞体都可用，
/// 对地面/地形这类近似误差只有几厘米，用于命中点定位完全够看。
/// </summary>
public static class ColliderSafeClosestPoint
{
    public static Vector3 SafeClosestPoint(this Collider col, Vector3 point)
    {
        if (col is BoxCollider || col is SphereCollider || col is CapsuleCollider)
            return col.ClosestPoint(point);

        var mesh = col as MeshCollider;
        if (mesh != null && mesh.convex)
            return col.ClosestPoint(point);

        // TerrainCollider / 非凸 MeshCollider：走包围盒近似
        return col.bounds.ClosestPoint(point);
    }
}
