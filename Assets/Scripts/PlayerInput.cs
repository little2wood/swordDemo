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

    /// <summary>
    /// 投盾请求缓冲：空格按下瞬间置位，保持到被消费为止（与攻击缓冲同理，避免落在无物理步的帧上丢输入）。
    /// </summary>
    public bool ThrowBuffered { get; private set; }

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

        // 空格：按下瞬间 → 置入投盾缓冲（由 PlayerController 消费）
        if (kb != null && kb.spaceKey.wasPressedThisFrame) ThrowBuffered = true;
    }

    /// <summary>消费投盾请求：返回是否存在待处理的按键，并清除缓冲。</summary>
    public bool TakeThrowRequest()
    {
        bool v = ThrowBuffered;
        ThrowBuffered = false;
        return v;
    }

    /// <summary>消费攻击请求：返回是否存在待处理的点击，并清除缓冲。</summary>
    public bool TakeAttackRequest()
    {
        bool v = AttackBuffered;
        AttackBuffered = false;
        return v;
    }

    /// <summary>锁定光标到屏幕中心并隐藏（第三人称操作的标准做法）。</summary>
    public void LockCursor()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }
}
