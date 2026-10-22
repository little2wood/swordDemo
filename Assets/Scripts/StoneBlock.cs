using UnityEngine;

/// <summary>
/// 石头 / 硬质障碍标记：剑砍到这里会被"弹刀" —— 中断挥砍、播砍不动动画、在接触点迸火星，
/// 而不是像 Prop 层箱子那样被击飞（石头没有 Rigidbody，本来也推不动）。
///
/// 为什么需要显式标记：场景里的石头在 Default 层、只有静态 MeshCollider，
/// 和地面/平台在层与物理属性上完全一样，靠层、刚体、.tag 都分不出来。
/// </summary>
public class StoneBlock : MonoBehaviour
{
    [Header("弹刀")]
    [Tooltip("关掉就退化成普通静态障碍：剑照常砍过去，不弹刀也不迸火星")]
    public bool bounces = true;

    [Header("火星")]
    [Tooltip("火星颜色（默认高温橙，像从石头上迸出的碎屑）")]
    public Color sparkColor = new Color(1f, 0.70f, 0.22f);
    [Tooltip("火星强度倍率：影响数量与初速")]
    public float sparkScale = 1f;
}
