using UnityEngine;

/// <summary>
/// Torned 破损披风代理驱动（套法二：蒙在 CapeCloth 粒子上"跟着动"）：
/// CapeCloth 继续跑 9×13 粒子 Verlet/PBD 物理，本组件把 Torned 网格每个顶点
/// 在初始姿态下绑定到最近的 bindCount 个粒子（反距离权重），运行时做位移插值：
///   顶点新位置 = 顶点初始位置经当前 spine_02 变换 + Σ 权重 × 粒子位移
/// 粒子位移 = 粒子当前位置 - 粒子静止位置经当前骨骼变换（纯形变量）：
/// 角色移动/转身/弯腰由基点项整体跟随，粒子位移只提供布料飘动。
/// 网格在绑定时实例化一份再每帧写顶点，不改 FBX 资产。
/// </summary>
[DefaultExecutionOrder(100)]    // 晚于 CapeCloth.LateUpdate，用本帧粒子位置
public class TornedCapeDriver : MonoBehaviour
{
    [Header("引用（留空自动查找）")]
    public CapeCloth cape;
    [Tooltip("Torned 网格所在 MeshFilter（默认取本物体上的）")]
    public MeshFilter tornFilter;

    [Header("绑定")]
    [Range(1, 6)]
    public int bindCount = 4;           // 每顶点绑定的粒子数（3~4 平滑）
    public float weightPower = 2f;      // 反距离权重指数：越大越偏向最近粒子

    Mesh instanceMesh;                  // 实例化网格（可写）
    Vector3[] v0Spine;                  // 顶点初始位置（spine 本地空间）
    int[] bIdx;                         // V × bindCount 粒子索引
    float[] bW;                         // V × bindCount 权重（每顶点和为 1）
    Vector3[] disp;                     // 粒子位移缓冲
    Vector3[] outVerts;                 // 顶点写回缓冲
    bool bound;

    void Start() { TryBind(); }

    void LateUpdate()
    {
        if (!bound) TryBind();
        if (!bound || cape == null || instanceMesh == null) return;
        Transform spine = cape.AnchorBone;
        if (spine == null) return;

        Vector3[] p = cape.ParticlePositions;
        Vector3[] rest = cape.ParticleRestLocals;
        if (p == null || rest == null || p.Length != rest.Length) return;

        var s2w = spine.localToWorldMatrix;
        for (int i = 0; i < p.Length; i++)
            disp[i] = p[i] - s2w.MultiplyPoint3x4(rest[i]);     // 纯形变位移

        var w2l = tornFilter.transform.worldToLocalMatrix;
        int v = v0Spine.Length;
        for (int i = 0; i < v; i++)
        {
            Vector3 pos = s2w.MultiplyPoint3x4(v0Spine[i]);     // 基点：跟骨骼走
            int o = i * bindCount;
            for (int k = 0; k < bindCount; k++)
                pos += bW[o + k] * disp[bIdx[o + k]];           // 位移插值
            outVerts[i] = w2l.MultiplyPoint3x4(pos);
        }
        instanceMesh.SetVertices(outVerts);
        instanceMesh.RecalculateNormals();
        instanceMesh.RecalculateBounds();
    }

    /// 初始姿态绑定：顶点 → spine 本地 → 找最近 N 个粒子 → 反距离权重
    void TryBind()
    {
        if (cape == null) cape = GetComponent<CapeCloth>();
        if (cape == null) cape = GetComponentInParent<CapeCloth>();
        if (tornFilter == null) tornFilter = GetComponent<MeshFilter>();
        if (cape == null || tornFilter == null) return;
        Transform spine = cape.AnchorBone;
        if (spine == null || tornFilter.sharedMesh == null) return;
        Vector3[] restLocals = cape.ParticleRestLocals;
        if (restLocals == null || restLocals.Length == 0) return;

        // FBX 资产不能直接写顶点，实例化一份
        if (instanceMesh == null)
        {
            instanceMesh = Instantiate(tornFilter.sharedMesh);
            instanceMesh.name = tornFilter.sharedMesh.name + "_Proxy";
            tornFilter.sharedMesh = instanceMesh;
        }

        Vector3[] verts = instanceMesh.vertices;
        int vc = verts.Length;
        var meshToSpine = spine.worldToLocalMatrix * tornFilter.transform.localToWorldMatrix;
        v0Spine = new Vector3[vc];
        for (int i = 0; i < vc; i++)
            v0Spine[i] = meshToSpine.MultiplyPoint3x4(verts[i]);

        int pn = restLocals.Length;
        int bc = Mathf.Clamp(bindCount, 1, 6);
        bIdx = new int[vc * bc];
        bW = new float[vc * bc];
        var bestD = new float[bc];
        for (int i = 0; i < vc; i++)
        {
            Vector3 vp = v0Spine[i];
            for (int k = 0; k < bc; k++) { bIdx[i * bc + k] = -1; bestD[k] = float.MaxValue; }
            // 暴力扫全部粒子，维护最近的 bc 个（插入法，粒子才几十个）
            for (int j = 0; j < pn; j++)
            {
                float d = (restLocals[j] - vp).sqrMagnitude;
                if (d >= bestD[bc - 1]) continue;
                int k = bc - 1;
                while (k > 0 && bestD[k - 1] > d) { bestD[k] = bestD[k - 1]; bIdx[i * bc + k] = bIdx[i * bc + k - 1]; k--; }
                bestD[k] = d; bIdx[i * bc + k] = j;
            }
            // 反距离权重归一化
            float sum = 0f;
            for (int k = 0; k < bc; k++)
            {
                if (bIdx[i * bc + k] < 0) { bestD[k] = float.MaxValue; continue; }
                float w = 1f / (Mathf.Pow(Mathf.Sqrt(bestD[k]) + 1e-4f, weightPower));
                bW[i * bc + k] = w; sum += w;
            }
            for (int k = 0; k < bc; k++) bW[i * bc + k] = bIdx[i * bc + k] < 0 ? 0f : bW[i * bc + k] / sum;
        }

        disp = new Vector3[pn];
        outVerts = new Vector3[vc];
        bound = true;
    }
}
