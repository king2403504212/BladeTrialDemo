using UnityEngine;

/// <summary>
/// 挂在 Player 上。WASD / 方向键相对镜头水平方向移动，并转向走路方向。
/// 有移动时给子物体 Animator 写 Speed：0 停 / 1 走 / 2 跑（WASD+Left Shift）。
/// 占用锁 Locked 时停水平移动（砍、铲都走这把锁），重力仍 Move。
/// Speed 占用时也写，滑铲结束才能按着方向进 Walk / Run。
/// 触屏摇杆以后再接到这里的输入，不改「相对镜头」这套换算。
/// </summary>
public class PlayerMove : MonoBehaviour
{
    // 米/秒。走路速度。大了走得快，小了像挪步。
    public float speed = 4f;

    // 跑步相对走路的倍数。按住 Left Shift 时用。大了冲太快，1 则跑和走一样快。
    public float runMultiplier = 1.6f;

    // 角色碰撞与 Move；Awake 取一次，Update 里用。
    CharacterController controller;
    // 主相机 Transform；用来算相对镜头的前后左右。
    Transform cam;
    // 模型子物体上的 Animator；写 Speed 切 Idle / Walk / Run。
    Animator animator;
    // 全角色占用锁；Locked 时只保留重力。
    PlayerBusy busy;
    // 叠在怪身体里时推出玩家。PushingPlayer 时停水平输入，重力仍 Move。
    PlayerSeparate separate;

    // 只处理竖直方向：贴地时压住角色，离地时模拟重力下落。
    float verticalVelocity;

    void Awake()
    {
        // 开局取一次即可。Update 里每帧 GetComponent 没必要。
        controller = GetComponent<CharacterController>();
        if (controller == null)
            Debug.LogWarning("[PlayerMove] 缺少 CharacterController，无法移动。", this);
        // Animator 挂在模型子物体上，用 InChildren 找。
        animator = GetComponentInChildren<Animator>();
        if (animator == null)
            Debug.LogWarning("[PlayerMove] 子物体没有 Animator，走不会切 Walk。", this);
        busy = GetComponent<PlayerBusy>();
        if (busy == null)
            Debug.LogWarning("[PlayerMove] 缺少 PlayerBusy，砍/铲时不会锁移动。", this);
        separate = GetComponent<PlayerSeparate>();
        if (separate == null)
            Debug.LogWarning("[PlayerMove] 缺少 PlayerSeparate，叠在怪身上时不会被推出。", this);
    }

    void Start()
    {
        // Main Camera 带 MainCamera 标签。镜头脚本也挂在那台相机上。
        if (Camera.main != null)
            cam = Camera.main.transform;
        else
            Debug.LogWarning("[PlayerMove] 找不到 Main Camera，将按世界坐标走。", this);
    }

    void Update()
    {
        // Horizontal = A/D，Vertical = W/S。Raw 是 -1/0/1，没有摇杆缓入。
        // 左右输入；交给 CameraRelativeFlat 当 right 分量。
        float x = Input.GetAxisRaw("Horizontal");
        // 前后输入；交给 CameraRelativeFlat 当 forward 分量。
        float z = Input.GetAxisRaw("Vertical");

        // 有没有按方向。占用时位移会清掉，Speed 仍用这个，结束才能切 Walk / Run。
        bool hasMoveInput = Mathf.Abs(x) > 0.01f || Mathf.Abs(z) > 0.01f;
        // 按住 Shift 且有方向才算跑。GetKey 是按住，不是点一下。
        bool wantRun = hasMoveInput && Input.GetKey(KeyCode.LeftShift);

        // 砍 / 铲占用时清掉水平输入；重力仍走下面的 Move。
        if (busy != null && busy.Locked)
        {
            x = 0f;
            z = 0f;
        }

        // 正在被推出停距。按键先让路，Speed 仍用上面的 hasMoveInput。
        if (separate != null && separate.PushingPlayer)
        {
            x = 0f;
            z = 0f;
        }

        // 相对镜头、贴地的水平方向；后面叠竖直速度再 Move。
        Vector3 move = CameraRelativeFlat(x, z);

        // 有在走才转身。占用时 isMoving 为假，滑铲中不转身。
        bool isMoving = move.sqrMagnitude > 0.01f;
        if (isMoving)
            transform.rotation = Quaternion.LookRotation(move);

        // 0 停、1 走、2 跑。对应 Idle 0.1、Walk/Run 1.5 那两条阈值。
        float animSpeed = 0f;
        if (hasMoveInput)
            animSpeed = wantRun ? 2f : 1f;
        if (animator != null)
            animator.SetFloat("Speed", animSpeed);

        // isGrounded：上一帧 Move 后，脚下有没有顶到可站的碰撞。
        // 贴地给 -1 往下压，避免缝隙把判定打成离地；悬空则每帧叠重力下落。
        if (controller.isGrounded)
            verticalVelocity = -1f;
        else
            verticalVelocity += Physics.gravity.y * Time.deltaTime;

        // 只有水平乘跑步倍数。竖直仍用 -1 贴地，乘 1.6 会砸地面，镜头跟着晃。
        float moveSpeed = speed;
        if (wantRun && isMoving)
            moveSpeed = speed * runMultiplier;
        // 水平位移；y 清掉，重力单独加。
        Vector3 horizontal = move;
        horizontal.y = 0f;
        controller.Move((horizontal * moveSpeed + Vector3.up * verticalVelocity) * Time.deltaTime);
    }

    // 把按键从「世界 XYZ」换成「镜头看的前方/右方」在地面上的投影。
    Vector3 CameraRelativeFlat(float x, float z)
    {
        // 镜头前方在地面上的投影；按 W/S 沿它走。
        Vector3 forward;
        // 镜头右方在地面上的投影；按 A/D 沿它走。
        Vector3 right;

        if (cam != null)
        {
            forward = cam.forward;
            right = cam.right;
        }
        else
        {
            // 找不到相机时退回世界坐标，避免角色不能走。
            forward = Vector3.forward;
            right = Vector3.right;
        }

        forward.y = 0f;
        right.y = 0f;

        // 镜头几乎垂直往下时 forward 投影接近 0，改用相机的 up 当「画面上方」。
        if (forward.sqrMagnitude < 0.001f)
        {
            forward = cam != null ? cam.up : Vector3.forward;
            forward.y = 0f;
        }

        forward.Normalize();
        right.Normalize();

        // y 保持 0。normalized 防止斜走比直线快。
        return (forward * z + right * x).normalized;
    }
}
