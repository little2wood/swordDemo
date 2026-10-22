using UnityEngine;

/// <summary>
/// 场景空气墙：根据所有激活 Terrain 的总包围盒，运行时生成四面侧墙 + 上方盖板 + 底部兜底，
/// 使场景完全密封，玩家/敌人无法走出地图边界。
/// 纯静态 BoxCollider，无渲染、无每帧开销；场景地形改动后自动适配，无需手工摆放。
/// 挂在场景任意空物体上（建议放在世界原点，便于 Gizmos 观察）。
/// </summary>
public class SceneSeal : MonoBehaviour
{
    [Header("密封范围")]
    public float topClearance = 20f;    // 上盖距地形最高点的余量（米）
    public float thickness = 2f;        // 空气墙厚度（米）
    public float underhang = 12f;       // 地形最低点向下延伸的密封深度（米）

    [Header("底部")]
    public bool bottomSeal = true;      // 底部兜底板，防止极端情况下从地形下方坠出

    private void Start()
    {
        Build();
    }

    /// <summary>依据激活 Terrain 的总包围盒重建全部空气墙（幂等，重复调用会先清空旧墙）。</summary>
    public void Build()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Object child = transform.GetChild(i).gameObject;
#if UNITY_EDITOR
            if (!Application.isPlaying) Object.DestroyImmediate(child);
            else Object.Destroy(child);
#else
            Object.Destroy(child);
#endif
        }

        Bounds b;
        Terrain[] terrains = Terrain.activeTerrains;
        if (terrains != null && terrains.Length > 0)
        {
            b = WorldBounds(terrains[0]);
            for (int i = 1; i < terrains.Length; i++) b.Encapsulate(WorldBounds(terrains[i]));
        }
        else
        {
            // 场景没有 Terrain 时退化为以自身为中心的 200x200 区域
            b = new Bounds(transform.position, new Vector3(200f, 30f, 200f));
        }

        float t = thickness;
        float floorTopY = b.min.y - underhang;          // 底部兜底板的顶面高度（侧墙由此向下无缝衔接）
        float wallTopY = b.max.y + topClearance;        // 侧墙顶 = 上盖所在高度
        float wallBottomY = floorTopY;
        float wallH = wallTopY - wallBottomY;
        float wallCy = (wallTopY + wallBottomY) * 0.5f;

        MakeWall("Seal_West",  new Vector3(b.min.x - t * 0.5f, wallCy, b.center.z), new Vector3(t, wallH, b.size.z + 2f * t));
        MakeWall("Seal_East",  new Vector3(b.max.x + t * 0.5f, wallCy, b.center.z), new Vector3(t, wallH, b.size.z + 2f * t));
        MakeWall("Seal_South", new Vector3(b.center.x, wallCy, b.min.z - t * 0.5f), new Vector3(b.size.x + 2f * t, wallH, t));
        MakeWall("Seal_North", new Vector3(b.center.x, wallCy, b.max.z + t * 0.5f), new Vector3(b.size.x + 2f * t, wallH, t));
        MakeWall("Seal_Top",   new Vector3(b.center.x, wallTopY + t * 0.5f, b.center.z), new Vector3(b.size.x + 2f * t, t, b.size.z + 2f * t));

        if (bottomSeal)
        {
            MakeWall("Seal_Bottom", new Vector3(b.center.x, floorTopY - t * 0.5f, b.center.z), new Vector3(b.size.x + 2f * t, t, b.size.z + 2f * t));
        }
    }

    private static Bounds WorldBounds(Terrain terrain)
    {
        Bounds local = terrain.terrainData.bounds;
        Vector3 center = terrain.transform.TransformPoint(local.center);
        Vector3 size = Vector3.Scale(local.size, terrain.transform.lossyScale);
        return new Bounds(center, size);
    }

    private void MakeWall(string name, Vector3 center, Vector3 size)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = center;
        BoxCollider col = go.AddComponent<BoxCollider>();
        col.size = size;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.5f);
        foreach (Transform child in transform)
        {
            if (child == null) continue;
            BoxCollider col = child.GetComponent<BoxCollider>();
            if (col == null) continue;
            Gizmos.DrawWireCube(child.position, col.size);
        }
    }
}
