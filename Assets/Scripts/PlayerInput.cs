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

    /// <summary>本帧是否按下了鼠标左键（攻击请求）。</summary>
    public bool AttackPressed { get; private set; }

    /// <summary>本帧鼠标 X 增量（像素）。仅光标锁定时有效，Esc 解锁后归零防乱转。</summary>
    public float MouseDeltaX { get; private set; }

    /// <summary>光标当前是否锁定（编辑器按 Esc 可解除）。</summary>
    public bool CursorLocked
    {
        get { return Cursor.lockState == CursorLockMode.Locked; }
    }

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

        // 鼠标左键：按下瞬间
        AttackPressed = mouse != null && mouse.leftButton.wasPressedThisFrame;

        // 鼠标 X 增量：仅锁定状态下采集
        MouseDeltaX = (mouse != null && CursorLocked) ? mouse.delta.ReadValue().x : 0f;
    }

    /// <summary>锁定光标到屏幕中心并隐藏（第三人称操作的标准做法）。</summary>
    public void LockCursor()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }
}
