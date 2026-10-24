using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 角色控制器：物理驱动移动（WASD 按固定屏幕方向）+ 朝向平滑对齐移动方向 + 攻击仅在站立时可起手/续击。
/// 攻击链全程（出刀/收势/连击）锁定移动与朝向，连击输入不受影响。
/// 攻击点击经 PlayerInput 的缓冲传递到 FixedUpdate 消费，避免渲染帧与物理步不同步导致丢点击。
/// 相机为固定偏移跟随（不旋转），动画只负责表演，位移完全由 Rigidbody 决定。
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Animator))]
[RequireComponent(typeof(PlayerInput))]
public class PlayerController : MonoBehaviour
{
    [Header("移动")]
    public float moveSpeed = 6f;        // m/s

    [Header("坡地贴合")]
    [Tooltip("探地判定距离（米，从腰部的探地起点往下算）：贴地范围内行走时竖直速度会钉成坡面速度，超过它（跳跃/击飞腾空）不干预")]
    public float snapMaxDist = 0.75f;

    [Header("朝向")]
    public float turnSpeed = 540f;      // deg/s，朝向转向移动方向的角速度
    public float facingOffset = 0f;     // 模型自带朝向偏移：实测模型正面朝 +Z 故为 0；若角色背对移动方向改成 180

    [Header("挥砍判定")]
    public float attackDamage = 34f;    // 每次挥砍伤害

    [Header("扫掠检测（剑尖轨迹）")]
    public Transform bladeBase;         // 剑根 Transform（留空则自动查找 Sword）
    public Transform bladeTip;          // 剑尖 Transform（留空则自动查找 SwordTip）
    [Range(0.01f, 0.3f)] public float bladeRadius = 0.05f;   // 剑刃半宽（胶囊半径，即判定容错度）
    [Range(0f, 1f)] public float tipLength = 0.25f;          // 参与判定的剑尖段长度（从剑尖沿刃往回取；0=纯剑尖点，≈剑长=整条刃）
    public bool debugSweep = false;     // 在 Scene 视图画出扫掠检测线（黄=刃线，红=扫掠线）

    [Header("盾牌（空格甩出）")]
    public ShieldThrow shield;          // 留空自动查找自身子树里的 ShieldThrow
    [Tooltip("动画到哪个进度时盾飞出去（Shield_OneShot 里约 0.17s 手抬到最高 → 0.2 左右）")]
    [Range(0f, 1f)] public float throwReleaseTime = 0.2f;
    public float throwTurnSpeed = 900f;        // 起手转身用更快的角速度，保证在出手前转到位
    public float maxReleaseAngle = 12f;        // 朝向与目标偏差小于这么多度才允许盾脱手（deg）
    public float throwForceTime = 0.55f;       // 兜底：起手后超过这么久无论如何都扔出去（防永远转不到位）
    public float throwPendingTimeout = 0.6f;   // 触发后这么久没进入投掷状态就丢弃（防卡待命）
    [Tooltip("出手后还要锁多久的移动：等投掷动画播完再放行，否则会带着投掷姿势平移（滑步）")]
    public float throwMoveLockTime = 0.45f;

    [Header("举盾（GuardUpper 上半身叠加层）")]
    [Tooltip("GuardUpper 层权重趋近目标的速度（每秒）：决定举手/收盾的快慢")]
    public float guardWeightSpeed = 8f;

    [Header("击飞可互动物体（Prop 层：箱子等）")]
    public LayerMask propLayer;         // 可互动物体所在层（Inspector 选 Prop）；主角/敌人/平台不在该层，天然不受影响
    public float bladeMass = 2f;        // 剑的有效质量：冲量 = 剑尖速度(m/s) × 该质量(kg)，单位 N·s
    public float maxSwingSpeed = 18f;   // 剑尖速度上限（m/s）：防卡顿帧或速度峰值把物体弹射上天

    [Header("砍到石头（StoneBlock）")]
    [Tooltip("留空自动从自身/父级取 StoneBlockReaction：命中石头时由它中断挥砍、播砍不动动画并迸火星")]
    public StoneBlockReaction stoneReaction;

    [Header("砍到敌人（溅血）")]
    [Tooltip("留空自动从自身/父级取 BloodFx：命中敌人时由它在接触点沿挥砍方向溅血")]
    public BloodFx bloodFx;

    [Header("扫掠激活窗口（normalizedTime 区间；前摇/收势不判，按各刀剑尖速度曲线实测标定）")]
    public Vector2 windowA = new Vector2(0.40f, 0.75f);   // A：挥砍爆发 0.46~0.65
    public Vector2 windowB = new Vector2(0.35f, 0.65f);   // B：挥砍爆发 0.375~0.56
    public Vector2 windowC = new Vector2(0.28f, 0.45f);   // C：前摇很长(0~0.30)，爆发仅 0.31~0.33

    Rigidbody rb;
    Animator anim;
    PlayerInput input;
    Camera cam;
    bool parryHoldPrev;         // 上一物理帧的弹反定身状态：用于识别"解锁帧"（快切跑步用）

    bool throwPending;      // 已起手投掷动画，等动画走到出手点
    float throwPendingTime;
    bool aimMode;           // 按住空格中：镜头拉近 + 身体对准这一击的方向
    float throwLockTimer;   // 出手后的移动锁定剩余时间：等投掷动画播完，避免平移不播走路动画
    ThirdPersonCamera thirdPersonCamera;
    PlayerGuard guard;      // 防御/架势/破防：破防硬直要并进下面的各种锁定
    PlayerParry parry;      // 弹反：定身期间锁移动，左键走反击而不是普通挥砍
    bool throwAiming;       // 松开空格后的起手阶段：把身体转向投掷方向（瞄准按住期间不转，交给正常移动）
    float throwAimYaw;      // 目标朝向（Y 轴欧拉角，已含 facingOffset）
    int guardLayerIdx = -1; // GuardUpper 叠加层索引：上半身举盾姿势（腿在 Base 层照常跑动）

    int lastSwingHash;      // 上一次挥砍的状态 hash，用于识别"新的一刀"

    // 扫掠检测与命中结算：与敌人爪击、盾牌共用 SweepProbe / MeleeHitResolver
    readonly SweepProbe bladeProbe = new SweepProbe();
    readonly MeleeHitResolver resolver = new MeleeHitResolver();
    readonly List<SweepProbe.Hit> sweepHits = new List<SweepProbe.Hit>();
    MeleeHitResolver.Policy bladePolicy;    // 每帧从 Inspector 字段刷新（Play 模式调参即时生效）
    System.Action<Vector3, Vector3> fleshCb;                 // 缓存委托：溅血（避免每帧方法组转换分配）
    System.Action<Vector3, Vector3, StoneBlock> stoneCb;     // 缓存委托：弹刀

    // Animator 参数与状态名 hash
    static readonly int MovingId = Animator.StringToHash("Moving");
    static readonly int GuardId = Animator.StringToHash("Guard");
    static readonly int AttackId = Animator.StringToHash("Attack");
    static readonly int ThrowId = Animator.StringToHash("Throw");
    static readonly int ThrowStateH = Animator.StringToHash("Shield_Throw");
    static readonly int IdleH = Animator.StringToHash("Idle");
    static readonly int JogH = Animator.StringToHash("Jog");
    static readonly int DashH = Animator.StringToHash("Shield_Dash");
    static readonly int AH = Animator.StringToHash("A");
    static readonly int ARecH = Animator.StringToHash("A_Rec");
    static readonly int BH = Animator.StringToHash("B");
    static readonly int BRecH = Animator.StringToHash("B_Rec");
    static readonly int CH = Animator.StringToHash("C");

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        anim = GetComponent<Animator>();
        input = GetComponent<PlayerInput>();
        cam = Camera.main;
        guardLayerIdx = anim.GetLayerIndex("GuardUpper");

        // 自动查找剑的判定点（Inspector 未指定时）
        if (bladeTip == null || bladeBase == null)
        {
            var sword = TransformUtil.FindDeep(transform, "Sword");
            if (sword != null)
            {
                if (bladeBase == null) bladeBase = sword;
                var tip = TransformUtil.FindDeep(sword, "SwordTip");
                if (bladeTip == null) bladeTip = tip != null ? tip : sword;
            }
        }
        // 盾：留空则自动取子树里的 ShieldThrow（脚本挂在盾牌对象上）
        if (shield == null) shield = GetComponentInChildren<ShieldThrow>(true);
        if (thirdPersonCamera == null) thirdPersonCamera = Camera.main != null ? Camera.main.GetComponent<ThirdPersonCamera>() : null;
        if (guard == null) guard = GetComponentInParent<PlayerGuard>();
        if (parry == null) parry = GetComponentInParent<PlayerParry>();
        if (stoneReaction == null) stoneReaction = GetComponentInParent<StoneBlockReaction>();
        if (bloodFx == null) bloodFx = GetComponentInParent<BloodFx>();

        // 盾飞行中第一次撞到东西 → 相机提前结束投掷观察期
        if (shield != null) shield.FlightFirstHit += OnShieldFirstHit;

        fleshCb = SprayBlood;
        stoneCb = StoneBounced;
    }

    void OnDestroy()
    {
        if (shield != null) shield.FlightFirstHit -= OnShieldFirstHit;
    }

    /// 盾飞行途中第一次撞到东西 → 相机再保持观察期一小段（看清落点）后回位
    void OnShieldFirstHit()
    {
        if (thirdPersonCamera != null) thirdPersonCamera.OnThrowFlightHit();
    }

    void Update()
    {
        UpdateSweep();  // 扫掠检测跟动画帧走（Animator 在 Update 驱动），剑尖采样按渲染帧稳定推进

        // ---- 砍到石头的锁定：吞掉全部待处理输入（松开空格/攻击缓冲/右键）----
        //      不吞的话，解锁那一帧会立刻执行排队的那一下，玩家会觉得"我没按它自己动了"
        bool stoneBlocking = stoneReaction != null && stoneReaction.IsBlocking;
        if (stoneBlocking)
        {
            input.TakeThrowRelease();
            input.TakeAttackRequest();
            input.TakeGuardRequest();
        }

        bool released = input.TakeThrowRelease();   // 每帧都消费一次"松开空格"，避免事件堆积到下一次瞄准
        // ---- 投盾：松开空格的那一帧调用（TakeThrowRelease 每帧都要消费一次，避免事件堆积） ----
        if (released && aimMode && !stoneBlocking) ReleaseThrow();

        bool stunnedUp = IsStunned();
        bool parryHoldUp = parry != null && parry.IsHolding;

        // ---- 投盾：按住空格 = 瞄准（拉近镜头、突出预瞄点），松开 = 掷出 ----
        bool canThrow = shield != null && shield.InHand && CanStartThrow() && !stunnedUp && !parryHoldUp && !stoneBlocking;

        // ---- 右键按下的那一帧：盾在手=尝试弹反（进入防御的瞬间）；盾不在手（飞行中/已落地）=召回 ----
        //      破防硬直/弹反定身期间照常消费掉这次按下但不执行
        bool guardPressed = input.TakeGuardRequest();
        if (guardPressed && !stunnedUp && !parryHoldUp && !stoneBlocking)
        {
            if (shield != null && !shield.InHand)
            {
                shield.StartReturn();
                // 召回开始立刻置 Guard=true：GuardUpper 层权重由 FixedUpdate 平滑推上去，左手即举盾，
                // 直到 FinishReturn（盾回到手）后 shieldReturning 解除才回落
                anim.SetBool(GuardId, true);
            }
            else if (parry != null) parry.TryParry();
        }

        if (input.ThrowHeld && canThrow)
        {
            if (!aimMode) BeginAim();
            // 按住期间只记录这一击的方向（预瞄点跟着动），身体不强制转向 —— 松开起手那一刻才转过去
            Vector3 aim = shield != null ? shield.AimDirection() : transform.forward;
            throwAimYaw = Mathf.Atan2(aim.x, aim.z) * Mathf.Rad2Deg + facingOffset;
        }
        else if (aimMode && (!input.ThrowHeld || !canThrow))
        {
            // 中途松开失败/盾没了/进了攻击链：退出瞄准，但不当作投掷
            EndAim();
        }

        // ---- 已起手投掷动画：等动画走到出手点时才真的把盾甩出去 ----
        if (throwPending)
        {
            throwPendingTime += Time.deltaTime;
            var st = anim.GetCurrentAnimatorStateInfo(0);
            if (st.shortNameHash == ThrowStateH)
            {
                if (st.normalizedTime >= throwReleaseTime && (FacingAligned() || throwPendingTime >= throwForceTime))
                {
                    shield.Throw(shield.AimDirection());   // 盾内部按 flattenAim 决定是否用完整三维前向
                    throwPending = false;
                    throwAiming = false;                  // 出手了，交还给正常的移动转向
                    throwLockTimer = throwMoveLockTime;   // 锁住移动，等投掷动画播完再放行
                }
            }
            else if (throwPendingTime > throwPendingTimeout)
            {
                throwPending = false;               // 没能进入投掷状态（被打断/状态机没接上）：丢弃这次请求
                if (thirdPersonCamera != null) thirdPersonCamera.EndThrowHold();   // 没扔出去：结束观察期，相机回位
            }
        }
    }

    /// 能否起手投掷：站/走时都行，攻击链与防御中不行
    bool CanStartThrow()
    {
        int h = anim.GetCurrentAnimatorStateInfo(0).shortNameHash;
        return h == IdleH || h == JogH;
    }

    // ---- 按住空格瞄准 → 松开空格掷出 ----

    /// 进入瞄准：镜头拉近、预瞄点放大高亮（身体朝向交给正常移动逻辑，不强制对准）
    void BeginAim()
    {
        aimMode = true;
        if (thirdPersonCamera != null) thirdPersonCamera.SetAiming(true);
        if (shield != null && shield.reticle != null) shield.reticle.SetAiming(true);
    }

    /// 退出瞄准：镜头推回原处、预瞄点恢复常态
    void EndAim()
    {
        aimMode = false;
        if (shield != null && shield.reticle != null) shield.reticle.SetAiming(false);
        // 相机不立即回位：进入投掷观察期，保持瞄准位（拉近 + 越肩）直到盾第一次碰撞
        // 或最长 3 秒，方便玩家看清盾的飞行轨迹
        if (thirdPersonCamera != null) thirdPersonCamera.BeginThrowHold();
    }

    /// 松开空格 → 起手投掷动画：这一刻才把身体转向投掷方向（盾在动画走到出手点时才真的飞出去）
    void ReleaseThrow()
    {
        anim.SetTrigger(ThrowId);
        throwPending = true;
        throwPendingTime = 0f;
        throwAiming = true;     // 起手转身开始（出手后结束）
        EndAim();       // 掷出的同时镜头推回正常距离
    }

    /// 身体是否已经转到这一击的方向（还不够对准就再等两帧，别歪着把盾甩出去）
    bool FacingAligned()
    {
        float err = Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.y, throwAimYaw));
        return err <= maxReleaseAngle;
    }

    void FixedUpdate()
    {
        if (throwLockTimer > 0f) throwLockTimer -= Time.fixedDeltaTime;

        bool stunned = IsStunned();     // 破防硬直：禁止一切操作（移动/转向/攻击/投掷/举盾/召回）
        bool parryHold = parry != null && parry.IsHolding;   // 弹反定身：不能移动/转向/投掷/召回，但左键可以打反击
        bool parryJustReleased = parryHoldPrev && !parryHold;   // 定身解锁帧（本帧状态写入 parryHoldPrev 供下帧比较）
        parryHoldPrev = parryHold;
        bool stoneBlocking = stoneReaction != null && stoneReaction.IsBlocking;   // 砍到石头：同上禁一切，唯独镜头不归它管

        Vector2 mv = input.MoveInput;
        bool hasMove = mv.sqrMagnitude > 0.01f;

        // ---- 移动方向：相机系 WASD（相机不旋转，即固定的屏幕方向） ----
        Vector3 dir = Vector3.zero;
        if (hasMove)
        {
            Vector3 fwd = cam != null ? Flat(cam.transform.forward) : Vector3.forward;
            Vector3 right = cam != null ? Flat(cam.transform.right) : Vector3.right;
            dir = (fwd * mv.y + right * mv.x).normalized;
        }

        // ---- 位移：攻击链全程(A/A_Rec/B/B_Rec/C)、投掷起手与出手后的收势才锁定。
        //      举盾(Raise/Lower)与召回期间可以移动：动画停在举盾姿势、位移与转向照常（举盾滑步）；
        //      瞄准（按住空格）期间照常移动，身体朝向也交给正常移动逻辑 ----
        // 弹反反击的前冲：这段时间水平速度由 PlayerParry 写，这里既不接管也不清零
        bool dashing = parry != null && parry.IsDashing;
        bool shieldReturning = shield != null && shield.IsReturning;   // 召回飞行中：左手强制保持举盾

        if (dashing)
        {
            // 交给 PlayerParry
        }
        else if (IsAttackChain() || stunned || parryHold || stoneBlocking || throwPending || throwLockTimer > 0f || !hasMove)
        {
            ZeroHorizontalVelocity();
        }
        else
        {
            Vector3 v = rb.linearVelocity;
            v.x = dir.x * moveSpeed;
            v.z = dir.z * moveSpeed;

            // ---- 坡面贴合：把竖直速度钉成"期望速度在坡面上的竖直分量" ----
            // 没有它，坡上行走是"水平匀速 + 自由落体"的小抛物线（飞出坡面→撞回→再飞出）。
            // 钉住之后四个方向全贴：下坡自动吸附、横切等高线跟随地形缓降、上坡沿坡爬升
            // （上坡的正值由坡面碰撞接管——角色贴坡时切向速度不会脱离坡面）。
            var ground = ProbeGround();
            if (ground.hit && ground.dist <= snapMaxDist)
            {
                float slope = 1f - ground.normal.y;     // 0=平地，越大越陡
                if (slope > 0.001f)
                {
                    var vh = new Vector3(dir.x, 0f, dir.z) * moveSpeed;                        // 期望水平速度
                    v.y = vh.y - ground.normal.y * Vector3.Dot(vh, ground.normal);             // 投影到坡面后的竖直分量
                }
                else if (v.y > 0f)
                {
                    v.y = 0f;   // 走回平地：掐掉坡上带来的残留爬升，避免翻过坡顶时小跳一下
                }
            }

            rb.linearVelocity = v;
        }

        // ---- 朝向：只在投掷起手阶段转去投掷方向；其余时候平滑转向移动方向（攻击链全程锁定不转，举盾移动照常转向） ----
        if (throwAiming && !IsAttackChain() && !stunned && !parryHold && !stoneBlocking)
        {
            Quaternion aimTarget = Quaternion.Euler(0f, throwAimYaw, 0f);
            rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, aimTarget, throwTurnSpeed * Time.fixedDeltaTime));
        }
        else if (hasMove && !IsAttackChain() && !stunned && !parryHold && !stoneBlocking)
        {
            float targetYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg + facingOffset;
            Quaternion target = Quaternion.Euler(0f, targetYaw, 0f);
            rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, target, turnSpeed * Time.fixedDeltaTime));
        }

        // ---- 动画参数：投掷起手/收势期间不报"在移动"，否则会原地踩跑步动画 ----
        //      举盾照常报 Moving：腿在 Base 层跑动（Idle/Jog），上半身由 GuardUpper 层叠举盾姿势
        anim.SetBool(MovingId, hasMove && throwLockTimer <= 0f && !throwPending && !stunned && !parryHold && !stoneBlocking);

        // ---- 防御：按住右键举盾；召回飞行中强制举盾（左手保持举盾姿势直到盾回到手上）----
        //      举盾输入在攻击链期间不响应（链结束仍按着会自动举起），召回强制项不受此限。
        // 破防硬直期间冻结 Guard：破防动画必须完整播出。
        // 弹反定身则反过来：必须保持 Guard=true，主角才一直举着盾（反击动画由 PlayerParry 强制切，不吃过渡竞争）。
        bool guardHeld = (parryHold || shieldReturning || (input.GuardHeld && !IsAttackChain())) && !stoneBlocking;
        if (!stunned) anim.SetBool(GuardId, guardHeld);

        // ---- GuardUpper 层权重：举盾姿势叠加在上半身（腿照常 Idle/Jog）；
        //      攻击链/投掷/弹反 Dash 等需要占用手臂的全身动作期间权重归零让位，
        //      其余时候向目标平滑趋近（举手/收盾的过渡效果由权重的淡入淡出完成）----
        if (guardLayerIdx >= 0)
        {
            float guardTarget = (!stunned && guardHeld && !IsFullBodyAction() && !stoneBlocking) ? 1f : 0f;
            float w = anim.GetLayerWeight(guardLayerIdx);
            if (!Mathf.Approximately(w, guardTarget))
                anim.SetLayerWeight(guardLayerIdx, Mathf.MoveTowards(w, guardTarget, Time.fixedDeltaTime * guardWeightSpeed));
        }

        // ---- 弹反定身解锁帧：方向键已按死时直接硬切入跑步（Play，无混合）----
        //      走常规 Idle→Jog 要 0.15s 过渡，位移已恢复、腿还混在站立姿势 —— 纯滑步段；
        //      必须用 Play 而非 CrossFade：同帧 SetBool(Moving) 会先触发 Idle→Jog 的常规过渡
        //      （0.15s 且不可中断），CrossFade 会被它盖过；Play 直接改当前状态，无竞争。
        //      举盾权重也立即归零（正常是平滑回落），上半身残影一并消掉
        if (parryJustReleased && hasMove)
        {
            anim.Play(JogH, 0, 0f);
            if (guardLayerIdx >= 0) anim.SetLayerWeight(guardLayerIdx, 0f);
        }

        // ---- 攻击输入：从缓冲消费点击；只有站立时才起手/续击，移动中直接丢弃（不缓存、不排队）----
        if (input.TakeAttackRequest())
        {
            if (parryHold)
            {
                // 弹反定身期间：这一下是反击（播 Shield_Dash 并把敌人撞飞），不是普通挥砍
                if (parry != null && !parry.Riposted) parry.Riposte();
                else anim.ResetTrigger(AttackId);
            }
            else if (!stunned && !stoneBlocking && CanStartAttack(hasMove))
            {
                anim.SetTrigger(AttackId);
            }
            else
            {
                anim.ResetTrigger(AttackId);    // 清掉残留的 pending trigger，避免停下后自动补一刀
            }
        }
    }

    /// 是否处于攻击链（A/A_Rec/B/B_Rec/C）——全程锁定移动与朝向；A_Rec/B_Rec 仅放行连击输入
    bool IsAttackChain()
    {
        int h = anim.GetCurrentAnimatorStateInfo(0).shortNameHash;
        return h == AH || h == ARecH || h == BH || h == BRecH || h == CH;
    }

    /// <summary>
    /// 是否处在挥砍的<b>有效窗口</b>内：只认 A/B/C，且动画进度落在各自的 windowX 区间内。
    /// 与下面 UpdateSweep 的命中判定共用同一套窗口定义，因此表现层（剑拖尾）
    /// 与命中判定的开关时机完全一致——前摇和收势都不算在"挥砍中"。
    /// </summary>
    public bool IsSwingWindow()
    {
        var st = anim.GetCurrentAnimatorStateInfo(0);
        int h = st.shortNameHash;
        if (h != AH && h != BH && h != CH) return false;
        Vector2 w = WindowOf(h);
        return st.normalizedTime >= w.x && st.normalizedTime <= w.y;
    }

    /// A/B/C 各刀的扫掠激活窗口（normalizedTime 区间）
    Vector2 WindowOf(int h)
    {
        return h == AH ? windowA : h == BH ? windowB : windowC;
    }

    /// <summary>
    /// 扫掠命中：出刀状态的激活窗口内（normalizedTime 区间，跳过前摇/收势）每帧检测剑尖段胶囊（轴=剑尖段内端→剑尖，半径=bladeRadius）——
    /// ① 刃线（当前帧剑尖段是否压在敌人身上）；② 扫掠线（上一帧剑尖段沿挥扫方向扫过帧间距，防高速穿透漏检）。
    /// 只算剑尖这一段：判定体积贴近剑尖真实轨迹，命中时机与旧版零宽线一致，受击方向不会被"挥砍起势的前推分量"带偏。
    /// 检测与结算分别由 SweepProbe / MeleeHitResolver 完成（与敌人爪击、盾牌共用），对同一敌人一次挥砍只结算一次。
    /// </summary>
    void UpdateSweep()
    {
        var st = anim.GetCurrentAnimatorStateInfo(0);
        int h = st.shortNameHash;
        bool swinging = (h == AH || h == BH || h == CH);

        if (!swinging || bladeTip == null)
        {
            lastSwingHash = 0;
            bladeProbe.Reset();
            resolver.Begin();
            return;
        }

        if (h != lastSwingHash)     // 新的一刀：清空已结算列表，重新采样扫掠起点
        {
            lastSwingHash = h;
            bladeProbe.Reset();
            resolver.Begin();
        }

        // 进度窗口：前摇/收势阶段剑尖轨迹不算攻击，窗口外不检测。
        // 回到窗口内的第一帧会重新以当前剑尖为扫掠起点，前摇轨迹不会被连成扫掠线；
        // 去重集合不清——同一刀窗口内已结算的目标不重复结算。
        Vector2 w = WindowOf(h);
        if (st.normalizedTime < w.x || st.normalizedTime > w.y)
        {
            bladeProbe.Reset();
            return;
        }

        // 结算策略每帧从 Inspector 字段刷新（Play 模式调参即时生效）
        bladePolicy.propLayer = propLayer;
        bladePolicy.mass = bladeMass;
        bladePolicy.maxSpeed = maxSwingSpeed;
        bladePolicy.damage = attackDamage;
        bladePolicy.targetTeam = Health.Team.Enemy;
        bladePolicy.propApplyImpulse = false;   // 箱子击飞交给 BladeCollider 的真实物理碰撞（碰撞矩阵里 Blade 层只与 Prop 碰），这里只登记
        bladePolicy.onStone = stoneCb;
        bladePolicy.onFlesh = fleshCb;

        Vector3 tipNow = bladeTip.position;
        Vector3 baseNow = bladeBase != null ? bladeBase.position : tipNow;
        Vector3 innerNow = TipInner(tipNow, baseNow);       // 剑尖段内端点（只取剑尖 tipLength 这一段）

        // 挥扫方向：本帧位移 → 位移近零时退化为剑身指向（SweepProbe 内再退化为 forward）；速度按帧间位移/dt 算
        bladeProbe.Probe(innerNow, tipNow, bladeRadius, tipNow - baseNow, sweepHits);
        for (int i = 0; i < sweepHits.Count; i++)
        {
            var hit = sweepHits[i];
            resolver.Resolve(hit.collider, hit.sweepDir, hit.velocity, hit.point, bladePolicy);
        }

        if (debugSweep && bladeProbe.ProbedLast)
        {
            Debug.DrawLine(innerNow, tipNow, Color.yellow, 0.1f);
            Debug.DrawLine(bladeProbe.prevB, tipNow, Color.red, 0.1f);
            DebugRadius(innerNow, bladeRadius, Color.yellow);
            DebugRadius(tipNow, bladeRadius, Color.red);
        }
    }

    /// 溅血：沿挥扫方向甩出去（resolver 在去重之后回调，所以同一刀对同一敌人只喷一次）
    void SprayBlood(Vector3 point, Vector3 swingDir)
    {
        if (bloodFx != null) bloodFx.Spray(point, swingDir);
    }

    /// 弹刀：中断挥砍 + 播砍不动动画 + 迸火星（接触点当火星的出生地）
    void StoneBounced(Vector3 point, Vector3 swingDir, StoneBlock stone)
    {
        if (stoneReaction != null) stoneReaction.HitStone(point, swingDir, stone);
    }

    /// 剑尖段内端点：从剑尖沿刃方向往剑根退 tipLength（不超过剑长，tipLength=0 时退化为剑尖点）
    Vector3 TipInner(Vector3 tipPos, Vector3 basePos)
    {
        Vector3 bladeDir = tipPos - basePos;
        float len = bladeDir.magnitude;
        if (len < 1e-5f) return tipPos;
        return tipPos - bladeDir / len * Mathf.Min(tipLength, len);
    }

    /// 用三轴十字线标出刃宽范围（Scene 视图调 bladeRadius 时的宽度可视化）
    void DebugRadius(Vector3 center, float r, Color c)
    {
        Debug.DrawLine(center + Vector3.right * r, center - Vector3.right * r, c, 0.1f);
        Debug.DrawLine(center + Vector3.up * r, center - Vector3.up * r, c, 0.1f);
        Debug.DrawLine(center + Vector3.forward * r, center - Vector3.forward * r, c, 0.1f);
    }

    /// 是否处于举盾姿态：GuardUpper 层权重过半（举手姿势已明显叠加）。
    /// 供格挡判定（PlayerGuard）使用；权重平滑趋近目标，天然覆盖举手/收盾的过渡段
    bool IsGuarding()
    {
        return guardLayerIdx >= 0 && anim.GetLayerWeight(guardLayerIdx) > 0.4f;
    }

    /// 当前是否在播需要占用手臂的全身动作（攻击链/投掷/弹反 Dash）：GuardUpper 层必须让位
    bool IsFullBodyAction()
    {
        int h = anim.GetCurrentAnimatorStateInfo(0).shortNameHash;
        return IsAttackChain() || throwPending || h == ThrowStateH || h == DashH;
    }

    /// <summary>是否处于举盾姿态（供 PlayerGuard 判定能否格挡）。</summary>
    public bool IsGuardPose()
    {
        return IsGuarding();
    }

    /// <summary>是否处于破防硬直（PlayerGuard 触发）：期间禁止玩家一切操作。</summary>
    bool IsStunned()
    {
        return guard != null && guard.IsStunned;
    }

    /// 是否可起手/续击：由"是否有移动输入"定义站立，而不是状态名——
    /// 松开移动键即视为站立意图，Jog→Idle 过渡建立前后都放行，停步后立即可攻击。
    /// Idle 起手，A_Rec/B_Rec 连击窗口；跑动中（有移动输入）一律不响应。
    bool CanStartAttack(bool hasMove)
    {
        if (hasMove) return false;
        int h = anim.GetCurrentAnimatorStateInfo(0).shortNameHash;
        return h == IdleH || h == JogH || h == ARecH || h == BRecH;
    }

    static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.normalized;
    }

    void ZeroHorizontalVelocity()
    {
        Vector3 v = rb.linearVelocity;
        if (v.x != 0f || v.z != 0f || v.y > 0f)
        {
            v.x = 0f;
            v.z = 0f;
            v.y = Mathf.Min(v.y, 0f);   // 掐掉上坡残留的爬升速度：停下不再"升一小段再落下"（向下的下落分量保留，交给重力）
            rb.linearVelocity = v;
        }
    }

    readonly RaycastHit[] groundHits = new RaycastHit[8];

    /// <summary>
    /// 向下探地：返回脚下地面的（是否命中, 从腰部探地起点到地面的距离, 地面法线）。
    /// 起点放在身体内部（Unity 射线不命中起点所在的碰撞体），但腿部的部件胶囊挡在路径上，
    /// 所以用 NonAlloc 全量收再过滤掉自己的部件，取最近的外部命中。
    /// </summary>
    (bool hit, float dist, Vector3 normal) ProbeGround()
    {
        var origin = transform.position + Vector3.up * 0.5f;
        int n = Physics.RaycastNonAlloc(origin, Vector3.down, groundHits, snapMaxDist, ~0, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        Vector3 normal = Vector3.up;
        bool found = false;
        for (int i = 0; i < n; i++)
        {
            var h = groundHits[i];
            if (h.collider == null) continue;
            if (h.collider.transform.root == transform) continue;   // 自己的部件不算"地面"
            if (h.distance < best) { best = h.distance; normal = h.normal; found = true; }
        }
        return (found, best, normal);
    }
}
