using UnityEngine;

/// <summary>
/// 披风布料（Verlet/PBD）—— 步骤①：粒子与距离约束初始化、网格渲染、顶行锚定。
/// 粒子在世界空间模拟（5×7，0.4m 宽 × 0.7m 长）；顶行钉在 anchorBone（spine_02）
/// 的本地偏移处，转身/前倾/攻击时披风顶部自动跟随躯干动画。
/// 后续步骤：② Verlet 积分（重力+风） ③ 约束求解迭代（PBD） ④ 可选身体碰撞推离。
/// </summary>
public class CapeCloth : MonoBehaviour
{
    [Header("网格尺寸")]
    public int cols = 5;            // 列（宽向粒子数）
    public int rows = 7;            // 行（长向粒子数）
    public float width = 0.4f;      // 挂肩宽度（m）
    public float length = 0.7f;     // 垂落长度（m）

    [Header("锚定（相对锚骨的本地偏移）")]
    public float anchorHeight = 0.25f;  // 顶行相对 spine_02 的高度（约肩线）
    public float anchorBack = 0.05f;    // 贴背偏移（spine_02 local +Z = 模型背后）

    [Header("引用（留空自动查找 spine_02）")]
    public Transform anchorBone;

    [Header("外观（renderer 无材质时自动按此建运行时材质）")]
    public Color clothColor = new Color(0.45f, 0.12f, 0.10f);
    public float smoothness = 0.35f;

    Transform spine;
    Vector3[] pos;              // 粒子位置（世界空间）
    Vector3[] prevPos;          // 上一帧位置（步骤② Verlet 积分用）
    Vector3[] anchorLocals;     // 顶行粒子相对锚骨的本地偏移
    Constraint[] cons;          // 距离约束（步骤③ 求解用）
    struct Constraint { public int a, b; public float rest; }
    Mesh mesh;
    int halfCount;              // cols*rows
    int vertCount;              // 双面顶点 = half*2

    void Awake() { BuildNow(); }

    /// 初始化粒子/约束/网格（Awake 自动调用；编辑模式下也可手动调用预览）
    public void BuildNow()
    {
        spine = anchorBone != null ? anchorBone : FindDeep(transform.root != null ? transform.root : transform, "spine_02");
        if (spine == null) { Debug.LogError("[CapeCloth] 找不到锚骨 spine_02"); return; }
        InitParticles();
        BuildConstraints();
        BuildMesh();
    }

    /// 粒子初始位置：顶行按锚点展开，其余行竖直下垂
    void InitParticles()
    {
        int n = cols * rows;
        pos = new Vector3[n];
        prevPos = new Vector3[n];
        anchorLocals = new Vector3[cols];
        for (int c = 0; c < cols; c++)
        {
            float x = Mathf.Lerp(-width / 2f, width / 2f, (float)c / (cols - 1));
            anchorLocals[c] = new Vector3(x, anchorHeight, anchorBack);
            Vector3 anchorW = spine.TransformPoint(anchorLocals[c]);
            for (int r = 0; r < rows; r++)
                pos[r * cols + c] = anchorW + Vector3.down * (length * r / (rows - 1));
        }
        System.Array.Copy(pos, prevPos, n);
    }

    /// 距离约束：横向 + 竖向相邻粒子（对角约束以后可加，用于抗剪切）
    void BuildConstraints()
    {
        var list = new System.Collections.Generic.List<Constraint>();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int i = r * cols + c;
                if (c + 1 < cols) list.Add(MakeCon(i, i + 1));      // 横向
                if (r + 1 < rows) list.Add(MakeCon(i, i + cols));   // 纵向
            }
        cons = list.ToArray();
    }

    Constraint MakeCon(int a, int b)
    {
        return new Constraint { a = a, b = b, rest = (pos[b] - pos[a]).magnitude };
    }

    /// 动态网格：顶点 = 粒子位置（双面顶点：每粒子正/反两个顶点，反面绕序反转）
    void BuildMesh()
    {
        halfCount = cols * rows;
        vertCount = halfCount * 2;
        mesh = new Mesh { name = "Cape" };
        var verts = new Vector3[vertCount];
        var uvs = new Vector2[vertCount];
        for (int i = 0; i < halfCount; i++)
        {
            int c = i % cols, r = i / cols;
            var uv = new Vector2((float)c / (cols - 1), 1f - (float)r / (rows - 1));
            verts[i] = pos[i];
            verts[halfCount + i] = pos[i];
            uvs[i] = uv;
            uvs[halfCount + i] = uv;
        }
        var tris = new System.Collections.Generic.List<int>();
        for (int r = 0; r < rows - 1; r++)
            for (int c = 0; c < cols - 1; c++)
            {
                int i = r * cols + c;
                int a = i, b = i + 1, d = i + cols, e = i + cols + 1;
                tris.Add(a); tris.Add(b); tris.Add(e);              // 正面
                tris.Add(a); tris.Add(e); tris.Add(d);
                tris.Add(halfCount + b); tris.Add(halfCount + a); tris.Add(halfCount + e);   // 反面（绕序反转）
                tris.Add(halfCount + a); tris.Add(halfCount + d); tris.Add(halfCount + e);
            }
        mesh.SetVertices(verts);
        mesh.SetUVs(0, new System.Collections.Generic.List<Vector2>(uvs));
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.MarkDynamic();

        var mf = gameObject.GetComponent<MeshFilter>();
        if (mf == null) mf = gameObject.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        var mr = gameObject.GetComponent<MeshRenderer>();
        if (mr == null) mr = gameObject.AddComponent<MeshRenderer>();
        if (mr.sharedMaterial == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var mat = new Material(shader);
            int colorId = mat.HasProperty("_BaseColor") ? Shader.PropertyToID("_BaseColor") : Shader.PropertyToID("_Color");
            mat.SetColor(colorId, clothColor);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            mr.material = mat;
        }
    }

    void LateUpdate()
    {
        if (mesh == null || pos == null) return;

        // 顶行锚定：强制对齐锚骨世界位置（物理更新后再 pin；锚点位移不产生粒子速度）
        if (spine != null)
            for (int c = 0; c < cols; c++)
            {
                pos[c] = spine.TransformPoint(anchorLocals[c]);
                prevPos[c] = pos[c];
            }

        SyncMesh();
    }

    /// 粒子位置写回网格顶点（双面两份同步），法线按当前形态重算
    void SyncMesh()
    {
        var verts = new Vector3[vertCount];
        for (int i = 0; i < halfCount; i++)
        {
            verts[i] = pos[i];
            verts[halfCount + i] = pos[i];
        }
        mesh.SetVertices(verts);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    /// 递归查找子孙节点（按名字）
    static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var r = FindDeep(root.GetChild(i), name);
            if (r != null) return r;
        }
        return null;
    }
}
