using UnityEngine;

/// <summary>
/// 挂在 Player 上。Q / 左键打出最多三连击：第一刀 SetTrigger("Attack")，续刀只 SetBool("Combo")。
/// 每一刀播到 hitNormalizedTime 再 OverlapBox。TryLock(Attack) 覆盖三刀，结束才 Unlock。
/// 续刀绝不再 Trigger Attack，否则 Any State 会跳回第一刀。
/// </summary>
[DefaultExecutionOrder(-20)]
public class PlayerAttack : MonoBehaviour
{
    // 秒。攻击盒亮起多久。大了挥砍拖得长，小了容易看不清。
    public float attackDuration = 0.15f;

    // 秒。第一刀的出手间隔（CD）。续刀不走 CD。大了起手慢，小了接近连点。
    public float cooldown = 0.4f;

    // 0～1。当前这一刀播到这个比例再结算。调大更晚，对准下劈最低点。
    public float hitNormalizedTime = 0.42f;

    // 相对角色本地坐标：前方约 1.1 米、腰高 0.9 米。Z 加大挥得更远。
    public Vector3 hitboxOffset = new Vector3(0f, 0.9f, 1.1f);

    // 米。攻击盒长宽高。Z 加大判定更厚。
    public Vector3 hitboxSize = new Vector3(1.2f, 0.8f, 1.2f);

    // 是否在 Scene / Game 里画出攻击盒。关了只看不见，判定还在。对刀时再勾上。
    public bool showHitbox = false;

    // 模型子物体上的 Animator；第一刀 Trigger Attack，续刀写 Bool Combo。
    Animator animator;
    // 挥砍占位子物体；出手时亮起，倒计时结束关掉。
    GameObject hitbox;
    // 还剩多久能起第一刀；>0 时空闲按键被挡住。续刀不读这个。
    float cooldownLeft;
    // 攻击盒还要亮几秒；到 0 就 SetActive(false)。
    float visibleLeft;
    // 全角色占用锁；第一刀 TryLock(Attack)，三刀结束 Unlock。
    PlayerBusy busy;
    // 这一帧刚出手；LateUpdate 先不解锁，等 Animator 切进 Attack。
    bool lockStartedThisFrame;
    // 已经进过任意一刀；离开后再 Unlock。
    bool seenAttackState;
    // 秒。Trigger 没接上时的兜底，避免永远锁死。
    float lockTimeout;
    // 这一刀还没扫盒子；到命中点后关掉。进下一刀时重新打开。
    bool hitPending;
    // 当前这一刀的状态名（Attack / Attack2 / Attack3）。变了说明接到下一刀。
    string attackStateName;
    // 这一刀 hitPending 对应的状态名。避免过渡时拿上一刀的时间立刻结算。
    string hitForState;

    void Awake()
    {
        // Animator 挂在模型子物体上，用 InChildren 找。
        animator = GetComponentInChildren<Animator>();
        if (animator == null)
            Debug.LogWarning("[PlayerAttack] 子物体没有 Animator，砍不会播动画。", this);
        busy = GetComponent<PlayerBusy>();
        if (busy == null)
            Debug.LogWarning("[PlayerAttack] 缺少 PlayerBusy，无法上锁。", this);

        // 运行时造子物体当刀光占位，省得在 Hierarchy 里手动摆盒子。
        hitbox = GameObject.CreatePrimitive(PrimitiveType.Cube);
        hitbox.name = "AttackHitbox";
        hitbox.transform.SetParent(transform, false);
        hitbox.transform.localPosition = hitboxOffset;
        hitbox.transform.localScale = hitboxSize;

        // 只要看见范围，不要把角色或地面顶开。
        // 盒子自带的碰撞体；改成 Trigger 只做范围显示与扫判定。
        Collider col = hitbox.GetComponent<Collider>();
        col.isTrigger = true;

        // 复用胶囊已有的 URP 材质，避免默认材质在 URP 下变洋红。
        // 角色身上的 Renderer，用来拷贝 URP 材质。
        Renderer playerRenderer = GetComponent<Renderer>();
        // 攻击盒的 Renderer，用来上橙红色。
        Renderer boxRenderer = hitbox.GetComponent<Renderer>();
        if (playerRenderer != null && playerRenderer.sharedMaterial != null)
        {
            // 从角色材质复制一份，避免改到共享材质。
            Material mat = new Material(playerRenderer.sharedMaterial);
            // 橙红，和胶囊白色区分开，Play 里一眼能认出这是挥砍。
            mat.color = new Color(1f, 0.35f, 0.15f, 1f);
            boxRenderer.material = mat;
        }
        else
        {
            Debug.LogWarning("[PlayerAttack] 角色没有可用材质，攻击盒可能洋红。", this);
        }

        hitbox.SetActive(false);
    }

    void Update()
    {
        if (cooldownLeft > 0f)
            cooldownLeft -= Time.deltaTime;

        if (visibleLeft > 0f)
        {
            visibleLeft -= Time.deltaTime;
            if (visibleLeft <= 0f)
                hitbox.SetActive(false);
        }

        // 触屏按钮以后再接到同一处 TryAttack，不改出手逻辑。
        if (Input.GetKeyDown(KeyCode.Q) || Input.GetMouseButtonDown(0))
            TryAttack();
    }

    // 按键入口。已在三连击里则只点 Combo；空闲才 TryLock 并 Trigger 第一刀。
    void TryAttack()
    {
        // 占用已是挥砍：续刀只写 Combo。再 Trigger 会被 Any State 拉回第一刀。
        if (busy != null && busy.Current == PlayerOccupy.Attack)
        {
            if (IsInNamedAttack("Attack3"))
            {
                Debug.Log("[PlayerAttack] 第三刀，忽略续刀。", this);
                return;
            }

            if (animator != null)
            {
                animator.SetBool("Combo", true);
                Debug.Log("[PlayerAttack] 续刀，等当前刀 0.65 处接下一刀。", this);
            }
            else
            {
                Debug.LogWarning("[PlayerAttack] 没有 Animator，无法续刀。", this);
            }

            return;
        }

        if (cooldownLeft > 0f)
        {
            Debug.Log($"[PlayerAttack] 还不能砍（CD {cooldownLeft:F2}），忽略这次按键。", this);
            return;
        }

        if (busy == null || !busy.TryLock(PlayerOccupy.Attack))
        {
            Debug.Log($"[PlayerAttack] 占用中（{busy?.Current}），忽略这次按键。", this);
            return;
        }

        cooldownLeft = cooldown;
        attackStateName = null;
        hitForState = "Attack";
        if (animator != null)
        {
            // 上一轮若 Combo 还是 true，第一刀播到 0.65 会自动接第二刀。
            animator.SetBool("Combo", false);
            animator.SetTrigger("Attack");
        }
        else
        {
            Debug.LogWarning("[PlayerAttack] 没有 Animator，改为立刻结算。", this);
        }

        // 立刻占锁。不靠 CD：CD 比动画短，否则挥刀后半段仍会滑。
        lockStartedThisFrame = true;
        seenAttackState = false;
        lockTimeout = 2f;
        hitPending = true;
        Debug.Log($"[PlayerAttack] 出手，等动画 {hitNormalizedTime:F2} 处再结算。", this);

        // 没动画就没法对最低点，退回按下立刻扫。
        if (animator == null)
            ResolveHit();
    }

    // 跟 Animator 同一帧的状态：先解锁判定，再认下一刀，再结算。
    void LateUpdate()
    {
        UpdateAttackMoveLock();
        AdvanceComboState();
        TryResolvePendingHit();
    }

    // LateUpdate 里跟动画：进过任意一刀再离开，或超时，才 Unlock(Attack)。
    void UpdateAttackMoveLock()
    {
        if (busy == null || busy.Current != PlayerOccupy.Attack)
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
                EndAttackLock();
            return;
        }

        // 当前层正在播或正在转入三刀之一；为真则继续占锁。
        bool inAttack = IsInAttackAnim();
        if (inAttack)
        {
            seenAttackState = true;
            return;
        }

        // 进过 Attack 再离开，或超时，才允许再走 / 再铲。
        if (seenAttackState || lockTimeout <= 0f)
            EndAttackLock();
    }

    // 占有者解开挥砍锁，并清掉 Combo，避免下一轮第一刀自动接第二刀。
    void EndAttackLock()
    {
        busy.Unlock(PlayerOccupy.Attack);
        attackStateName = null;
        hitForState = null;
        if (animator != null)
            animator.SetBool("Combo", false);
    }

    // 当前状态变成下一刀时：清 Combo（否则 0.65 处会自动再接），并给新的一刀重新挂判定。
    void AdvanceComboState()
    {
        if (animator == null || busy == null || busy.Current != PlayerOccupy.Attack)
            return;

        // 只用当前状态，不用 Next：过渡开始时上一刀时间已超过命中点，会立刻误结算。
        string now = CurrentAttackStateName();
        if (now == null || now == attackStateName)
            return;

        if (attackStateName != null)
        {
            animator.SetBool("Combo", false);
            hitPending = true;
            hitForState = now;
            Debug.Log($"[PlayerAttack] 接到 {now}，等动画 {hitNormalizedTime:F2} 处再结算。", this);
        }

        attackStateName = now;
    }

    // 动画到了命中比例就扫一次盒子；错过窗口则丢掉这一刀判定。
    void TryResolvePendingHit()
    {
        if (!hitPending)
            return;

        if (animator == null)
        {
            hitPending = false;
            return;
        }

        // 当前这一刀播到哪，0～1；还没进对应状态为 -1。
        float t = AttackNormalizedTime();
        if (t >= hitNormalizedTime)
        {
            ResolveHit();
            return;
        }

        // 挥砍已经结束却没到命中点（状态名对不上等），不要一直等。
        if ((busy == null || busy.Current != PlayerOccupy.Attack) && !lockStartedThisFrame)
        {
            hitPending = false;
            Debug.LogWarning("[PlayerAttack] 离开 Attack 仍未到命中点，取消这一刀判定。", this);
        }
    }

    // 亮盒子并扫木桩。TryAttack 到点、或没 Animator 时立刻走这里。
    void ResolveHit()
    {
        hitPending = false;
        hitbox.transform.localPosition = hitboxOffset;
        hitbox.transform.localScale = hitboxSize;
        if (showHitbox)
        {
            hitbox.SetActive(true);
            visibleLeft = attackDuration;
        }
        Debug.Log($"[PlayerAttack] 命中判定{(showHitbox ? $"，盒子亮 {attackDuration:F2} 秒" : "（不画盒子）")}。", this);
        HitDummiesInBox();
    }

    // 当前层正在播的那一刀名字；过渡中仍返回上一刀，避免提前切 hitForState。
    string CurrentAttackStateName()
    {
        if (animator == null)
            return null;

        AnimatorStateInfo cur = animator.GetCurrentAnimatorStateInfo(0);
        if (cur.IsName("Attack"))
            return "Attack";
        if (cur.IsName("Attack2"))
            return "Attack2";
        if (cur.IsName("Attack3"))
            return "Attack3";
        return null;
    }

    // hitForState 那一刀的归一化时间；不在该状态返回 -1。
    float AttackNormalizedTime()
    {
        if (string.IsNullOrEmpty(hitForState))
            return -1f;

        AnimatorStateInfo cur = animator.GetCurrentAnimatorStateInfo(0);
        if (cur.IsName(hitForState))
            return cur.normalizedTime;

        if (animator.IsInTransition(0))
        {
            // 正在转入这一刀时当前还是 Idle/上一刀，读下一状态的时间。
            AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(0);
            if (next.IsName(hitForState))
                return next.normalizedTime;
        }

        return -1f;
    }

    // 当前层是否在播 / 正在转入三刀之一。走路锁和续刀都问这个。
    bool IsInAttackAnim()
    {
        return IsInNamedAttack("Attack") || IsInNamedAttack("Attack2") || IsInNamedAttack("Attack3");
    }

    // 当前或下一状态是不是这一刀。第三刀用来挡住续刀；锁也复用。
    bool IsInNamedAttack(string stateName)
    {
        if (animator == null)
            return false;

        AnimatorStateInfo cur = animator.GetCurrentAnimatorStateInfo(0);
        if (cur.IsName(stateName))
            return true;
        if (animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).IsName(stateName))
            return true;
        return false;
    }

    // 按当前攻击盒 OverlapBox；扫到 DummyTarget 就 Hit。打中数写进日志对照「亮了没闪红」。
    void HitDummiesInBox()
    {
        // 按当前盒子位置扫实心碰撞。打中木桩就通知 Hit（扣血 + 闪红）。
        // 攻击盒世界中心；OverlapBox 扫这个点。
        Vector3 center = hitbox.transform.position;
        // 半边长（米）；OverlapBox 要半尺寸，不是全尺寸。
        Vector3 half = hitbox.transform.lossyScale * 0.5f;
        // 这一帧扫到的所有碰撞体；再筛 DummyTarget。
        Collider[] hits = Physics.OverlapBox(center, half, hitbox.transform.rotation);
        // 这一刀打中的木桩数；用来对照「盒子亮了但没闪红」。
        int dummyHits = 0;
        // i：当前扫到的碰撞体下标。
        for (int i = 0; i < hits.Length; i++)
        {
            // 当前碰撞体上的木桩组件；没有则跳过。
            DummyTarget dummy = hits[i].GetComponent<DummyTarget>();
            if (dummy != null)
            {
                dummyHits++;
                dummy.Hit();
            }
        }

        Debug.Log($"[PlayerAttack] 扫到碰撞 {hits.Length} 个，打中木桩 {dummyHits} 个。", this);
    }
}
