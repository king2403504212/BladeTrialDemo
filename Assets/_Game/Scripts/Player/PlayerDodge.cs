using UnityEngine;

/// <summary>
/// 挂在 Player 上。按 F 滑铲：Trigger Slide，脚本按固定方向与速度推 CharacterController。
/// Slide 片段需 Bake XZ（动画只播姿势，水平位移由脚本负责），避免根运动与脚本双重位移导致变形。
/// 占用锁 PlayerOccupy.Slide，砍和走都问同一把锁。
/// </summary>
[DefaultExecutionOrder(-10)]
public class PlayerDodge : MonoBehaviour
{
    // 秒。两次滑铲间隔。大了不能连铲，小了接近连点。
    public float cooldown = 0.4f;

    // 米/秒。滑铲水平速度。大了冲太远，小了像挪步。
    public float slideSpeed = 5f;

    // 模型子物体上的 Animator；出手时写 Trigger Slide，位移不走根运动。
    Animator animator;
    // 模型相对 Player 的默认本地坐标；结束时扳回，防止异常残留偏移。
    Vector3 animLocalPos;
    // 模型相对 Player 的默认本地旋转；不把滑铲的侧倒写进胶囊朝向。
    Quaternion animLocalRot;
    // 滑铲位移走碰撞；不用 transform 传送。
    CharacterController controller;
    // 全角色占用锁；出手 TryLock(Slide)，结束 Unlock。
    PlayerBusy busy;
    // 这一次滑铲的水平方向；出手时定死，铲中不再转向。
    Vector3 slideDir;
    // 还剩多久能再铲；>0 时 TrySlide 直接 return。
    float cooldownLeft;
    // 这一帧刚出手；LateUpdate 先不解锁，等 Animator 切进 Slide。
    bool lockStartedThisFrame;
    // 已经进过 Slide 状态；离开后再 Unlock。
    bool seenSlideState;
    // 秒。Trigger 没接上时的兜底，避免永远锁死。
    float lockTimeout;

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        if (controller == null)
            Debug.LogWarning("[PlayerDodge] 缺少 CharacterController，滑铲不能位移。", this);
        animator = GetComponentInChildren<Animator>();
        if (animator == null)
        {
            Debug.LogWarning("[PlayerDodge] 子物体没有 Animator，铲不会播动画。", this);
        }
        else
        {
            animLocalPos = animator.transform.localPosition;
            animLocalRot = animator.transform.localRotation;
            // 走路与滑铲都用脚本位移，根运动全程关闭。
            animator.applyRootMotion = false;
        }
        busy = GetComponent<PlayerBusy>();
        if (busy == null)
            Debug.LogWarning("[PlayerDodge] 缺少 PlayerBusy，无法上锁。", this);
    }

    void OnDisable()
    {
        RestoreModelPose();
    }

    void Update()
    {
        if (cooldownLeft > 0f)
            cooldownLeft -= Time.deltaTime;

        if (Input.GetKeyDown(KeyCode.F))
            TrySlide();

        ApplySlideMotion();
    }

    // 跟动画解锁放 LateUpdate，等本帧状态切完再判断离开 Slide。
    void LateUpdate()
    {
        UpdateSlideLock();
    }

    // 按键入口。CD 或锁已被占则 return；否则定方向、Trigger 并 TryLock(Slide)。
    void TrySlide()
    {
        if (cooldownLeft > 0f)
        {
            Debug.Log($"[PlayerDodge] 还不能铲（CD {cooldownLeft:F2}），忽略这次按键。", this);
            return;
        }

        if (busy == null || !busy.TryLock(PlayerOccupy.Slide))
        {
            Debug.Log($"[PlayerDodge] 占用中（{busy?.Current}），忽略这次按键。", this);
            return;
        }

        cooldownLeft = cooldown;
        lockStartedThisFrame = true;
        seenSlideState = false;
        lockTimeout = 2f;

        // 出手瞬间朝向；铲中不跟镜头改方向。
        slideDir = transform.forward;
        slideDir.y = 0f;
        if (slideDir.sqrMagnitude < 0.001f)
            slideDir = Vector3.forward;
        else
            slideDir.Normalize();

        if (animator != null)
        {
            animator.applyRootMotion = false;
            animator.SetTrigger("Slide");
        }
        else
            Debug.LogWarning("[PlayerDodge] 没有 Animator，无法滑铲。", this);

        Debug.Log($"[PlayerDodge] 滑铲，脚本位移 speed={slideSpeed}。", this);
    }

    // 滑铲中按固定方向推胶囊。y 丢掉，高度仍由 PlayerMove 的重力管。
    void ApplySlideMotion()
    {
        if (controller == null)
            return;
        if (busy == null || busy.Current != PlayerOccupy.Slide)
            return;
        if (!IsInSlideAnim())
            return;

        controller.Move(slideDir * slideSpeed * Time.deltaTime);
    }

    // 跟动画解锁。占有者必须是 Slide，才允许 Unlock。
    void UpdateSlideLock()
    {
        if (busy == null || busy.Current != PlayerOccupy.Slide)
            return;

        if (lockTimeout > 0f)
            lockTimeout -= Time.deltaTime;

        if (lockStartedThisFrame)
        {
            lockStartedThisFrame = false;
            return;
        }

        if (animator == null)
        {
            if (lockTimeout <= 0f)
                StopSlide();
            return;
        }

        if (IsInSlideAnim())
        {
            seenSlideState = true;
            return;
        }

        if (seenSlideState || lockTimeout <= 0f)
            StopSlide();
    }

    // 滑铲结束：解开占用，模型回到相对 Player 的默认位置。
    void StopSlide()
    {
        if (busy != null)
            busy.Unlock(PlayerOccupy.Slide);
        RestoreModelPose();
        Debug.Log("[PlayerDodge] 滑铲结束。", this);
    }

    // 确保根运动关闭，模型 local 无残留偏移。
    void RestoreModelPose()
    {
        if (animator == null)
            return;
        animator.applyRootMotion = false;
        animator.transform.localPosition = animLocalPos;
        animator.transform.localRotation = animLocalRot;
    }

    // 当前层是否在播 / 正在转入名为 Slide 的状态（和 Animator 状态名一致，区分大小写）。
    bool IsInSlideAnim()
    {
        if (animator == null)
            return false;

        // 当前状态；名字是 Slide 说明还在铲。
        AnimatorStateInfo cur = animator.GetCurrentAnimatorStateInfo(0);
        if (cur.IsName("Slide"))
            return true;
        if (animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).IsName("Slide"))
            return true;
        return false;
    }
}
