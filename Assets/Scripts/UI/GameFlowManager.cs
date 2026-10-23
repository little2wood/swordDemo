using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// 游戏流程：开始菜单 → 同屏只刷 1 个敌人 → 杀死后在画面外补下一个 → 杀满 totalEnemies 个通关。
///
/// - 场景里的 Enemy_Imp 只当"模板"：开局隐藏，之后由本脚本 Instantiate 出场上唯一的敌人
/// - 生成点在主角周围随机方向的画面外（相机视锥外），敌人自己走进侦测范围
/// - 杀满通关 / 主角阵亡都不立刻弹结算：世界照常跑 victoryDelay 秒（看清最后的死亡表现），
///   再定格显示"您已通关"或"你已阵亡"
/// - "重新开始"重载场景后跳过菜单、直接开打（一次性静态标志跨场景传递）
/// - ESC：游戏中暂停（显示 继续游戏/退出游戏），暂停中再按 ESC 或点"继续游戏"恢复
/// - UI 全部 IMGUI 程序化绘制；所有 UI 阶段 timeScale=0 冻结世界、解锁光标、停用相机控制
/// </summary>
public class GameFlowManager : MonoBehaviour
{
    [Header("流程")]
    [Tooltip("通关需要击杀的敌人总数")]
    public int totalEnemies = 10;
    [Tooltip("一个敌人被杀死后，隔多久在画面外刷出下一个（给死亡表现留点时间）")]
    public float respawnDelay = 1.5f;
    [Tooltip("杀满通关 / 主角阵亡后，隔多久才弹出结算界面（让死亡表现播完）")]
    public float victoryDelay = 3f;
    [Tooltip("新敌人的生成距离（米）：在主角周围随机方向的画面外，且超出侦测范围，它会自己追进来")]
    public float spawnDistance = 16f;
    [Tooltip("场景里作为模板的敌人名字（开局隐藏，之后由本脚本实例化）")]
    public string enemyTemplateName = "Enemy_Imp";

    enum Phase { Menu, Playing, Paused, Victory, Defeat }
    Phase phase = Phase.Menu;

    /// 场景里唯一的流程管理器（PlayerInput 开局锁光标前会来问一句"现在是不是 UI 阶段"）
    public static GameFlowManager Instance { get; private set; }

    /// 当前是否处于 UI 阶段（菜单/暂停/通关/失败）：期间光标必须可见可点、视角锁死
    public bool IsUIPhase { get { return phase != Phase.Playing; } }

    /// "重新开始"跨场景重载的一次性标志：true = 重载后跳过菜单直接开打
    static bool startDirectly;

    int kills;
    GameObject template;      // 隐藏的模板敌人
    GameObject current;       // 场上唯一的敌人
    Transform player;
    ThirdPersonCamera camCtrl;   // 相机控制脚本：UI 期间整个禁用，视角纹丝不动
    float respawnAt;          // 下次生成时刻
    bool waitingRespawn;
    bool settlePending;       // 已触发结算（通关或阵亡），等 victoryDelay 后弹界面
    float settleAt;           // 结算界面弹出时刻
    bool settleAsVictory;     // true=通关结算 false=阵亡结算

    void Awake()
    {
        Instance = this;

        var t = GameObject.Find(enemyTemplateName);
        if (t != null)
        {
            // 模板在场景里是激活状态保存的（GameObject.Find 只找得到激活对象，不能预隐藏），
            // 此时模板身上的 HealthBar.Awake 已经跑过、建了血条 Canvas —— 必须先清掉再隐藏，
            // 否则 SpawnNext 的 Instantiate 会把旧 Canvas 一起复制，实例又建一个新的 → 敌人头顶双血条
            for (int i = t.transform.childCount - 1; i >= 0; i--)
            {
                var ch = t.transform.GetChild(i);
                if (ch.name == "HealthBar_World" || ch.name == "HealthBar_HUD") Destroy(ch.gameObject);
            }
            t.SetActive(false);
            template = t;
        }
        var p = GameObject.Find("Player");
        if (p != null) player = p.transform;
        camCtrl = FindObjectOfType<ThirdPersonCamera>();

        if (startDirectly)
        {
            // 从"重新开始"重载过来：跳过菜单直接开打
            startDirectly = false;
            StartGame();
        }
        else
        {
            // 正常进游戏：停在开始菜单，冻结世界、解锁光标、停用相机控制
            SetUIActive(true);
            Time.timeScale = 0f;
        }
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// UI 阶段与游戏阶段的光标/相机切换（幂等，重复调用无害）
    void SetUIActive(bool ui)
    {
        if (ui)
        {
            Cursor.lockState = CursorLockMode.None;     // 解锁 + 显示：按钮才点得到
            Cursor.visible = true;
            if (camCtrl != null) camCtrl.enabled = false;   // LateUpdate 不跑 → 视角纹丝不动
        }
        else
        {
            Cursor.lockState = CursorLockMode.Locked;   // 回到第三人称操作的标准光标态
            Cursor.visible = false;
            if (camCtrl != null) camCtrl.enabled = true;
        }
    }

    void Update()
    {
        // ESC：游戏中 → 暂停；暂停中 → 继续（不用点按钮也能快速回到战斗）
        var kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame)
        {
            if (phase == Phase.Playing) Pause();
            else if (phase == Phase.Paused) Resume();
        }

        if (phase != Phase.Playing) return;

        // 结算过渡中（已杀满或已阵亡）：世界继续跑，倒计时到了再定格弹界面
        if (settlePending)
        {
            if (Time.time >= settleAt)
            {
                settlePending = false;
                phase = settleAsVictory ? Phase.Victory : Phase.Defeat;
                SetUIActive(true);
                Time.timeScale = 0f;
            }
            return;
        }

        // 主角先倒下 → 进入阵亡倒计时（等 3 秒看清死亡表现，再弹"你已阵亡"）
        var php = player != null ? player.GetComponentInChildren<Health>() : null;
        if (php != null && php.IsDead)
        {
            settlePending = true;
            settleAsVictory = false;
            settleAt = Time.time + victoryDelay;
            return;
        }

        // 场上敌人已死：计入击杀，安排补刷或进入通关倒计时
        if (current != null)
        {
            var hp = current.GetComponentInChildren<Health>();
            if (hp != null && hp.IsDead)
            {
                kills++;
                current = null;                     // 尸体（Ragdoll）留在原地，成为游离对象
                if (kills >= totalEnemies)
                {
                    settlePending = true;
                    settleAsVictory = true;
                    settleAt = Time.time + victoryDelay;
                    return;
                }
                waitingRespawn = true;
                respawnAt = Time.time + respawnDelay;
            }
        }
        else if (waitingRespawn && Time.time >= respawnAt)
        {
            waitingRespawn = false;
            SpawnNext();
        }
    }

    /// 在主角周围随机方向挑一个相机视锥外的点，贴合地形高度后实例化下一个敌人
    void SpawnNext()
    {
        if (template == null || player == null) return;

        Vector3 pos = player.position;
        var cam = Camera.main;
        Plane[] planes = cam != null ? GeometryUtility.CalculateFrustumPlanes(cam) : null;
        for (int i = 0; i < 12; i++)
        {
            float ang = Random.Range(0f, Mathf.PI * 2f);
            pos = player.position + new Vector3(Mathf.Cos(ang) * spawnDistance, 0f, Mathf.Sin(ang) * spawnDistance);
            var bounds = new Bounds(pos + Vector3.up, Vector3.one * 2f);
            if (planes == null || !GeometryUtility.TestPlanesAABB(planes, bounds)) break;   // 视锥外 → 就用它
        }

        var terr = Terrain.activeTerrain;
        if (terr != null) pos.y = terr.transform.position.y + terr.SampleHeight(pos) + 0.05f;

        current = Instantiate(template, pos, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
        current.name = enemyTemplateName;
        current.SetActive(true);

        // 出生点在感知范围外（16m > 12m），必须显式警觉，敌人起步直追而不是原地待机
        var ai = current.GetComponent<EnemyAI>();
        if (ai != null) ai.Alert();
    }

    void StartGame()
    {
        kills = 0;
        waitingRespawn = false;
        settlePending = false;
        Time.timeScale = 1f;
        phase = Phase.Playing;
        SetUIActive(false);     // 锁回光标、恢复相机控制，正式开打
        SpawnNext();
    }

    void Pause()
    {
        phase = Phase.Paused;
        SetUIActive(true);      // 光标交给按钮、视角锁死
        Time.timeScale = 0f;    // 世界冻结（恢复时 Time.time 原地续走，补刷/通关计时不受影响）
    }

    void Resume()
    {
        phase = Phase.Playing;
        SetUIActive(false);
        Time.timeScale = 1f;
    }

    /// 重开 = 整个场景重来，但跳过菜单直接开打
    void Restart()
    {
        startDirectly = true;   // 一次性标志，重载后的 Awake 读走
        Time.timeScale = 1f;
        SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }

    void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;    // 编辑器里结束 Play 模式
#else
        Application.Quit();                                  // 打包后退出程序
#endif
    }

    void OnGUI()
    {
        if (phase == Phase.Playing)
        {
            GUI.Label(new Rect(14f, 10f, 260f, 28f), "击杀  " + kills + " / " + totalEnemies);
            return;
        }

        // 全屏压暗底
        GUI.Box(new Rect(0f, 0f, Screen.width, Screen.height), "");

        if (phase == Phase.Paused)
        {
            var box = new Rect((Screen.width - 320f) / 2f, (Screen.height - 170f) / 2f, 320f, 170f);
            GUI.Box(box, "已暂停");
            if (GUI.Button(new Rect(box.x + 80f, box.y + 48f, 160f, 40f), "继续demo"))
                Resume();
            if (GUI.Button(new Rect(box.x + 80f, box.y + 100f, 160f, 40f), "退出demo"))
                QuitGame();
            return;
        }

        // 菜单 / 通关 / 失败：标题 + 一个按钮
        string title = phase == Phase.Menu ? "开始demo" : phase == Phase.Victory ? "您已通关！" : "你已阵亡";
        string btn = phase == Phase.Menu ? "开始demo" : "重新开始";

        var b = new Rect((Screen.width - 320f) / 2f, (Screen.height - 140f) / 2f, 320f, 140f);
        GUI.Box(b, title);
        if (GUI.Button(new Rect(b.x + 80f, b.y + 72f, 160f, 40f), btn))
        {
            if (phase == Phase.Menu) StartGame();
            else Restart();
        }
    }
}
