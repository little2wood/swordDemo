using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 输入采集层：只负责读取硬件输入，不做任何游戏逻辑。
/// 移动/攻击的语义解释（含攻击状态过滤）由 PlayerController 完成。
/// </summary>
public class PlayerInput : MonoBehaviour
{
    /// <summary>WASD 移动意图。x: A(-1)/D(+1)，y: S(-1)/W(+1)。对角线已归一化。</summary>
    public Vector2 MoveInput { get; private set; }

    /// <summary>本帧鼠标位移（像素）。x 向右为正，y 向上为正。</summary>
    public Vector2 MouseDelta { get; private set; }

    /// <summary>
    /// 攻击请求缓冲：Update 里检测到点击就置位，保持到被 PlayerController 消费为止。
    /// 不用单帧 bool 是因为 FixedUpdate 与渲染帧不同步——点击若落在没有物理步的帧上，
    /// 单帧 bool 会在下一帧被 Update 覆盖，造成"点了没反应"。
    /// </summary>
    public bool AttackBuffered { get; private set; }

    /// <summary>右键当前是否按住（防御意图）。持续状态，不是单帧事件。</summary>
    public bool GuardHeld { get; private set; }

    /// <summary>右键按下的单帧意图：盾不在手上时用它触发召回。持续按住不会重复触发。</summary>
    bool guardBuffered;

    /// <summary>
    /// 投盾：按住空格 = 进入瞄准（拉近镜头、突出预瞄点，不投掷）；
    /// 松开空格的那一下 = 真的甩出去。
    /// ThrowHeld 是当前是否按住（持续状态），ThrowReleased 是"本帧松开"的单帧事件。
    /// </summary>
    public bool ThrowHeld { get; private set; }
    public bool ThrowReleased { get; private set; }

    bool prevThrowHeld;

    void Start()
    {
        LockCursor();
    }

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;

        // WASD
        float x = 0f;
        float y = 0f;
        if (kb != null)
        {
            if (kb.wKey.isPressed) y += 1f;
            if (kb.sKey.isPressed) y -= 1f;
            if (kb.aKey.isPressed) x -= 1f;
            if (kb.dKey.isPressed) x += 1f;
        }
        MoveInput = new Vector2(x, y);
        if (MoveInput.sqrMagnitude > 1f)
        {
            MoveInput = MoveInput.normalized;
        }

        // 鼠标位移：供相机做球面轨道旋转
        MouseDelta = mouse != null ? mouse.delta.ReadValue() : Vector2.zero;

        // 鼠标左键：按下瞬间 → 置入缓冲（由 PlayerController 消费或丢弃）
        if (mouse != null && mouse.leftButton.wasPressedThisFrame)
        {
            AttackBuffered = true;
        }

        // 鼠标右键：按住即防御，松开即收盾
        GuardHeld = mouse != null && mouse.rightButton.isPressed;
        if (mouse != null && mouse.rightButton.wasPressedThisFrame) guardBuffered = true;   // 按下瞬间：供召回用

        // 空格：按住=瞄准，松开=掷出
        bool held = kb != null && kb.spaceKey.isPressed;
        ThrowReleased = prevThrowHeld && !held;     // 本帧松开
        ThrowHeld = held;
        prevThrowHeld = held;
    }

    /// <summary>消费"松开空格"事件：返回本帧是否松开了空格，并清除标志。</summary>
    public bool TakeThrowRelease()
    {
        bool v = ThrowReleased;
        ThrowReleased = false;
        return v;
    }

    /// <summary>消费攻击请求：返回是否存在待处理的点击，并清除缓冲。</summary>
    public bool TakeAttackRequest()
    {
        bool v = AttackBuffered;
        AttackBuffered = false;
        return v;
    }

    /// <summary>消费右键按下请求：返回本帧是否按下了右键，并清除标志。</summary>
    public bool TakeGuardRequest()
    {
        bool v = guardBuffered;
        guardBuffered = false;
        return v;
    }

    /// <summary>锁定光标到屏幕中心并隐藏（第三人称操作的标准做法）。</summary>
    public void LockCursor()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }
}
