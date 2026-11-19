using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 挂在 MaleMonster 根上（和 DummyTarget、NavMeshAgent 同级）。
/// Base：活着就追玩家，用 Speed 切 walk/Idle。Upper：进停距且未死就攻击，追击不打断当前 Attack；死了 ClearAttack。不扣玩家血。
/// </summary>
public class DummyChase : MonoBehaviour
{
    // 玩家 Transform。Inspector 可拖；空则 Start 里按名字找 Player。Chase 读位置，不读朝向。
    public Transform player;

    // 这只怪还能否自己滑出。每只怪各有一份；滑过一次后，等玩家重新按移动再恢复。
    public bool canSlide = true;

    // 根上的 Agent；活着时每帧 SetDestination，速度和停距用组件上的值。
    NavMeshAgent agent;
    // 模型子物体上的 Animator；Base 写 Speed，Upper 写 Attack / Empty。
    Animator animator;
    // 同物体血量；IsDead 时停追并 ClearAttack。Awake 取一次。
    DummyTarget target;
    // Upper 层下标；找不到则为 -1，出手只走 Trigger，死时只 ResetTrigger。
    int upperLayerIndex = -1;
    // 本帧正在被玩家分离逻辑往外滑。Update 不再下目的地，避免和滑出对着拉。
    bool skipChase;
    // 当前是否处于追击。到停距时关，超过 Stopping Distance + 0.2 米才重新开。
    bool chasing;
    // 当前攻击要在哪个 normalizedTime 循环边界结束；-1 表示继续循环。
    float stopAttackAtNormalizedTime = -1f;
    // 身体胶囊。玩家用来 IgnoreCollision，没有则人仍可能被顶。
    CapsuleCollider body;

    // 分离脚本问：活着且在网格上才能推、才能滑出。
    public bool CanSeparate => agent != null && agent.isOnNavMesh && (target == null || !target.IsDead);

    // 停距（米）。分离用半距判断推人，用整段判断怪自己滑出。
    public float StoppingDistance => agent != null ? agent.stoppingDistance : 0f;

    // 根上的胶囊。玩家每帧忽略和它的碰撞。
    public CapsuleCollider Body => body;

    // 这只怪开始滑出时调用，只消耗自己的本轮机会。
    public void UseSlide()
    {
        canSlide = !canSlide;
    }

    // 玩家重新按下移动时调用；只有已经滑过的怪才恢复机会。
    public void RearmSlide()
    {
        if (!canSlide)
            canSlide = !canSlide;
    }

    void OnEnable()
    {
        PlayerSeparate.Register(this);
    }

    void OnDisable()
    {
        PlayerSeparate.Unregister(this);
    }

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        if (agent == null)
            Debug.LogWarning($"[DummyChase] {name} 缺少 NavMeshAgent，无法追击。", this);

        animator = GetComponentInChildren<Animator>();
        if (animator == null)
            Debug.LogWarning($"[DummyChase] {name} 子物体没有 Animator，走不会切 walk。", this);
        else
        {
            upperLayerIndex = animator.GetLayerIndex("Upper");
            if (upperLayerIndex < 0)
                Debug.LogWarning($"[DummyChase] {name} 没有 Upper 层，死时无法切回 Empty。", this);
        }

        target = GetComponent<DummyTarget>();
        if (target == null)
            Debug.LogWarning($"[DummyChase] {name} 缺少 DummyTarget，无法根据死亡停手。", this);

        body = GetComponent<CapsuleCollider>();
        if (body == null)
            Debug.LogWarning($"[DummyChase] {name} 缺少 CapsuleCollider，玩家无法忽略碰撞。", this);
    }

    void Start()
    {
        if (player == null)
        {
            // 场景里玩家物体名固定为 Player，和 CameraFollow 同一找法。
            GameObject found = GameObject.Find("Player");
            if (found != null)
                player = found.transform;
        }

        if (player != null)
            Debug.Log($"[DummyChase] {name} 锁定玩家 {player.name}。", this);
        else
            Debug.LogWarning($"[DummyChase] {name} 找不到 Player，不追击。", this);

        if (agent != null && !agent.isOnNavMesh)
            Debug.LogWarning($"[DummyChase] {name} 不在导航网格上，Agent 无法移动。", this);
    }

    // 没有刚体，目的地按帧交给 Agent，不用 FixedUpdate。
    void Update()
    {
        if (agent == null || !agent.isOnNavMesh)
            return;

        if (target != null && target.IsDead)
        {
            StopForDeath();
            return;
        }

        if (player == null)
            return;

        // 正在滑出停距。目的地交给分离逻辑，这一帧不追。
        if (skipChase)
        {
            skipChase = false;
            return;
        }

        // 当前水平距离；追击、停步和本轮攻击结束时都读这个值。
        float distance = FlatDistance(player.position);
        // 重新追击距离始终比停距多 0.2 米，调整 Agent 停距时自动同步。
        float chaseStartDistance = agent.stoppingDistance + 0.2f;

        // 人跑远就先追，但 Upper 会把当前这一轮播完；回到范围内则继续无缝连挥。
        if (IsInAttack())
        {
            if (distance > chaseStartDistance)
            {
                chasing = true;
                agent.SetDestination(player.position);
                agent.isStopped = false;
                agent.updateRotation = true;
                SetSpeed(1f);
                ScheduleAttackStop();
            }
            else
            {
                chasing = false;
                HoldAgent();
                SetSpeed(0f);
                stopAttackAtNormalizedTime = -1f;
            }

            TryStopAttackAtLoopEnd(distance, chaseStartDistance);
            return;
        }

        stopAttackAtNormalizedTime = -1f;

        // 追到 Stopping Distance 就停；停下后要超过停距 0.2 米才重新追，留出边界缓冲。
        if (chasing)
        {
            if (distance <= agent.stoppingDistance)
                chasing = false;
        }
        else if (distance > chaseStartDistance)
        {
            chasing = true;
        }

        if (chasing)
        {
            agent.SetDestination(player.position);
            agent.isStopped = false;
            agent.updateRotation = true;
            SetSpeed(1f);
            return;
        }

        // 停距到停距 + 0.2 米的缓冲区内保持停步和攻击，不会因边界浮动反复追击。
        HoldAgent();
        SetSpeed(0f);
        FacePlayer();
        TryAttack();
    }

    // 人在 Attack 中跑远时调用：只记录一次当前循环的结束点，避免每帧把结束时间往后推。
    void ScheduleAttackStop()
    {
        if (animator == null || stopAttackAtNormalizedTime >= 0f)
            return;

        // Attack 所在层；找不到 Upper 时沿用原有的 Base 兜底。
        int layer = upperLayerIndex >= 0 ? upperLayerIndex : 0;
        // 当前攻击播放进度；整数部分表示已经完成的循环数。
        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(layer);
        if (!state.IsName("Attack"))
            return;

        stopAttackAtNormalizedTime = Mathf.Floor(state.normalizedTime) + 1f;
    }

    // Attack 每帧调用：到当前循环边界时再看距离，仍然太远才收招。
    void TryStopAttackAtLoopEnd(float distance, float chaseStartDistance)
    {
        if (animator == null || stopAttackAtNormalizedTime < 0f)
            return;

        // 当前 Attack 进度；到记录的整数边界前都让这一轮继续播放。
        int layer = upperLayerIndex >= 0 ? upperLayerIndex : 0;
        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(layer);
        if (!state.IsName("Attack"))
        {
            stopAttackAtNormalizedTime = -1f;
            return;
        }

        if (state.normalizedTime < stopAttackAtNormalizedTime)
            return;

        if (distance > chaseStartDistance && upperLayerIndex >= 0)
        {
            animator.ResetTrigger("Attack");
            animator.Play("Empty", upperLayerIndex, 0f);
        }

        stopAttackAtNormalizedTime = -1f;
    }

    // 死亡：停步、清 Attack、Upper 回 Empty，避免边死边挥。
    void StopForDeath()
    {
        HoldAgent();
        SetSpeed(0f);
        ClearAttack();
    }

    // 清掉 Attack Trigger，并把 Upper 切回 Empty。仅死亡调用。
    void ClearAttack()
    {
        if (animator == null)
            return;

        stopAttackAtNormalizedTime = -1f;
        animator.ResetTrigger("Attack");
        if (upperLayerIndex >= 0)
            animator.Play("Empty", upperLayerIndex, 0f);
    }

    // 死亡时当场刹住，清路径和速度，避免死后还滑步。
    void HoldAgent()
    {
        agent.isStopped = true;
        agent.updateRotation = false;
        agent.ResetPath();
        agent.velocity = Vector3.zero;
    }

    // 贴身且不在 Attack 时 Trigger 一次。连挥靠片段 Loop，不要每刀再 Trigger。
    void TryAttack()
    {
        if (animator == null)
            return;
        if (IsInAttack())
            return;

        animator.SetTrigger("Attack");
        Debug.Log($"[DummyChase] {name} 出手。", this);
    }

    // 玩家分离调用：沿「人 → 怪」滑一步。还在停距内且确实挪动了才返回真。
    public bool StepAway(float pushSpeed)
    {
        skipChase = true;
        if (!CanSeparate || player == null)
            return false;

        Vector3 flat = transform.position - player.position;
        flat.y = 0f;
        float dist = flat.magnitude;
        float stop = agent.stoppingDistance;
        if (dist >= stop)
            return false;

        // 停掉寻路，避免这一帧路径把怪拉回人身边。
        agent.isStopped = true;
        agent.ResetPath();
        agent.velocity = Vector3.zero;

        Vector3 dir = dist < 0.001f ? transform.forward : flat / dist;
        float step = Mathf.Min(stop - dist, pushSpeed * Time.deltaTime);
        Vector3 before = transform.position;
        agent.Move(dir * step);

        Vector3 moved = transform.position - before;
        moved.y = 0f;
        return moved.sqrMagnitude > 0.000001f && FlatDistance(player.position) < stop;
    }

    // 水平距离是否还在半距（推人）或整段停距（怪滑出）内。
    public bool IsInside(Vector3 worldPos, bool half)
    {
        if (!CanSeparate)
            return false;
        float limit = half ? StoppingDistance * 0.5f : StoppingDistance;
        return FlatDistance(worldPos) < limit;
    }

    // 与某点的水平距离（米）。分离用来挑最近的一只。
    public float FlatDistance(Vector3 worldPos)
    {
        Vector3 delta = worldPos - transform.position;
        delta.y = 0f;
        return delta.magnitude;
    }

    // 和玩家的水平距离是否还在停距内。Upper 出手、贴身 Idle 都认这个。
    bool InStoppingRange()
    {
        // 指向玩家的水平向量；只用长度和停距比，不转头。
        Vector3 toPlayer = player.position - transform.position;
        toPlayer.y = 0f;
        return toPlayer.magnitude <= agent.stoppingDistance;
    }

    // 只转水平朝向玩家。贴身出手时脚几乎不动，补一次朝向。
    void FacePlayer()
    {
        // 指向玩家的水平向量；用来转 yaw，不抬头低头。
        Vector3 toPlayer = player.position - transform.position;
        toPlayer.y = 0f;
        if (toPlayer.sqrMagnitude < 0.001f)
            return;

        transform.rotation = Quaternion.LookRotation(toPlayer);
    }

    // 写 Animator 的 Speed：0 停 / 1 走。maleMonsterAnim 用大于 0.1 切 walk。
    void SetSpeed(float value)
    {
        if (animator != null)
            animator.SetFloat("Speed", value);
    }

    // Upper 层是否在播或转入 Attack；没有 Upper 则查 Base。
    bool IsInAttack()
    {
        // Attack 在 Upper；层缺失时退回 Base，避免旧控制器完全不认。
        int layer = upperLayerIndex >= 0 ? upperLayerIndex : 0;
        return IsInLayerState(layer, "Attack");
    }

    // 指定层状态名是否匹配；转入中认下一状态。
    bool IsInLayerState(int layer, string stateName)
    {
        if (animator == null)
            return false;

        AnimatorStateInfo cur = animator.GetCurrentAnimatorStateInfo(layer);
        if (cur.IsName(stateName))
            return true;
        if (animator.IsInTransition(layer) && animator.GetNextAnimatorStateInfo(layer).IsName(stateName))
            return true;
        return false;
    }
}
