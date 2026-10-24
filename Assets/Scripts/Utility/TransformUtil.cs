using UnityEngine;

/// <summary>
/// Transform 层级查找工具：集中原先分散复制在 PlayerController / EnemyAI / ShieldThrow / HitReaction 四处的递归查找。
/// </summary>
public static class TransformUtil
{
    /// 递归查找子孙节点（按名字精确匹配，含根自身；找不到返回 null）
    public static Transform FindDeep(Transform root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var r = FindDeep(root.GetChild(i), name);
            if (r != null) return r;
        }
        return null;
    }

    /// 递归查找子孙节点（名字包含关键词，小写比较；humanoid 映射取不到骨骼时的兜底）
    public static Transform FindDeepContains(Transform root, string keyword)
    {
        if (root == null) return null;
        if (root.name.ToLower().Contains(keyword)) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var r = FindDeepContains(root.GetChild(i), keyword);
            if (r != null) return r;
        }
        return null;
    }
}
