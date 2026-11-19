using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 挂在 Player 上。人和怪不加物理碰撞，避免追击把人顶走。
/// 半距内待满 1 秒，只推最近的一只，把人分帧滑到停距外。
/// 这次推完且人没重新按移动时，不再推人；别的怪待满 0.5 秒自己滑出停距。
/// </summary>
[DefaultExecutionOrder(-20)]
public class PlayerSeparate : MonoBehaviour
{
    // 秒。人在半距里连续超过这个时间才被推出。大了穿模更久，小了刚叠上就被推。
    public float insideSeconds = 1f;

    // 秒。人已被推过且没重新按移动时，怪在停距里待够才自己滑出。大了叠更久。
    public float monsterWaitSeconds = 0.5f;

    // 推出速度 = 走路速度 × 这个倍数。约好是 1.5。大了像弹开，小了拖。
    public float pushSpeedScale = 1f;

    // 角色胶囊；和怪胶囊 IgnoreCollision，推出时也用它 Move。
    CharacterController controller;
    // 读走路速度。空则按 4 米/秒算。
    PlayerMove move;
    // 正在把人推出的那只。滑完前不换。
    DummyChase locked;
    // 半距计时。离开半距或换怪就清零。
    float insideTimer;
    // 怪退开计时。只在「已经推过人且人没按移动」时走。
    float monsterTimer;
    // 人已经被推过一次。重新按下移动才清掉。
    bool playerPushUsed;
    // 这一帧正在推人。PlayerMove 看到就停水平输入。
    bool pushingPlayer;
    // 上一帧有没有移动键。用来认「重新按移动」。
    bool hadMove;
    // 这一帧移动键是否按下。按住也算在操作。
    bool hasMoveInput;
    // 人几乎和怪重合时的推出方向。有移动时在 LateUpdate 记朝向。
    Vector3 lastMoveDir = Vector3.forward;
    // 墙挡住、滑不出去的那只。人重新移动前不再反复试。
    DummyChase gaveUp;
    // 正在让怪自己滑出。滑完前不换另一只，也不再推人。
    bool monsterRetreating;
    // 场上的怪。Chase 启用时登记，禁用时摘掉。
    static readonly List<DummyChase> all = new List<DummyChase>();

    // PlayerMove 问：为真则这一帧不要加水平位移。
    public bool PushingPlayer => pushingPlayer;

    // 怪启用时登记，分离只从这张表里挑最近的一只。
    public static void Register(DummyChase chase)
    {
        if (chase != null && !all.Contains(chase))
            all.Add(chase);
    }

    // 怪禁用或销毁时摘掉，避免还去推已经没了的对象。
    public static void Unregister(DummyChase chase)
    {
        all.Remove(chase);
    }

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        if (controller == null)
            Debug.LogWarning("[PlayerSeparate] 缺少 CharacterController，无法把人推出。", this);
        move = GetComponent<PlayerMove>();
        if (move == null)
            Debug.LogWarning("[PlayerSeparate] 缺少 PlayerMove，推出速度按 4 米/秒。", this);
    }

    // 比 PlayerMove、DummyChase 早，好让本帧的推人标记先被看见。
    void Update()
    {
        // 原始移动键。砍和铲会清掉实际位移，这里仍认玩家有没有按方向。
        bool moving = Mathf.Abs(Input.GetAxisRaw("Horizontal")) > 0.01f
            || Mathf.Abs(Input.GetAxisRaw("Vertical")) > 0.01f;
        if (moving && !hadMove)
        {
            playerPushUsed = false;
            gaveUp = null;
            // 只恢复已经滑过的怪；没滑过的保持可用。每只怪各有自己的 canSlide。
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null)
                    all[i].RearmSlide();
            }
        }
        hadMove = moving;
        hasMoveInput = moving;

        IgnoreMonsterColliders();

        if (pushingPlayer || monsterRetreating)
        {
            if (monsterRetreating)
                KeepMonsterRetreating();
            return;
        }

        if (hasMoveInput || !playerPushUsed)
            TickPlayerPush();
        else
            TickMonsterRetreat();
    }

    void LateUpdate()
    {
        if (pushingPlayer)
            StepPlayer();

        if (!hasMoveInput)
            return;

        // 本帧 PlayerMove 已经转向，记下来给脚底重合时当推出方向。
        Vector3 forward = transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude > 0.001f)
            lastMoveDir = forward.normalized;
    }

    // 人和每只怪的胶囊互相忽略。攻击盒是 Overlap，不靠这层碰撞。
    void IgnoreMonsterColliders()
    {
        if (controller == null)
            return;

        for (int i = all.Count - 1; i >= 0; i--)
        {
            DummyChase chase = all[i];
            if (chase == null)
            {
                all.RemoveAt(i);
                continue;
            }

            Collider body = chase.Body;
            if (body != null)
                Physics.IgnoreCollision(controller, body, true);
        }
    }

    // 半距内累计 1 秒后开始推人。只认当前钉住的那只。
    void TickPlayerPush()
    {
        if (locked == null || !locked.IsInside(transform.position, true))
        {
            locked = Closest(true);
            insideTimer = 0f;
        }

        if (locked == null)
            return;

        insideTimer += Time.deltaTime;
        if (insideTimer < insideSeconds)
            return;

        pushingPlayer = true;
        insideTimer = 0f;
        float speed = PushSpeed();
        Debug.Log($"[PlayerSeparate] 推出玩家，怪 {locked.name}，速度 {speed:F1}。", this);
    }

    // 人站着不再推。最近那只在停距内待满 0.5 秒，改由怪自己滑出。
    void TickMonsterRetreat()
    {
        if (locked == null || !locked.IsInside(transform.position, false)
            || locked == gaveUp || !locked.canSlide)
        {
            locked = Closest(false, gaveUp, true);
            monsterTimer = 0f;
        }

        if (locked == null)
            return;

        monsterTimer += Time.deltaTime;
        if (monsterTimer < monsterWaitSeconds)
            return;

        monsterTimer = 0f;
        // 计时满就消耗这只怪自己的机会；玩家重新按下移动前不会再滑第二次。
        locked.UseSlide();
        monsterRetreating = true;
        Debug.Log($"[PlayerSeparate] {locked.name} 滑出停距。", this);
        KeepMonsterRetreating();
    }

    // 怪沿「人 → 怪」滑一帧。滑不动就放弃这只，追击恢复。
    void KeepMonsterRetreating()
    {
        if (locked == null)
        {
            monsterRetreating = false;
            return;
        }

        bool still = locked.StepAway(PushSpeed());
        if (still)
            return;

        monsterRetreating = false;
        if (locked.IsInside(transform.position, false))
            gaveUp = locked;
        locked = null;
    }

    // 人沿「怪 → 人」滑一帧，到达停距就结束。墙挡住也结束，避免一直抢移动。
    void StepPlayer()
    {
        if (controller == null || locked == null || !locked.CanSeparate)
        {
            EndPlayerPush();
            return;
        }

        float stop = locked.StoppingDistance;
        Vector3 flat = transform.position - locked.transform.position;
        flat.y = 0f;
        float dist = flat.magnitude;
        if (dist >= stop)
        {
            EndPlayerPush();
            return;
        }

        Vector3 dir = dist < 0.001f ? lastMoveDir : flat / dist;
        if (dir.sqrMagnitude < 0.001f)
            dir = transform.forward;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.001f)
            dir = Vector3.forward;
        dir.Normalize();

        Vector3 target = locked.transform.position + dir * stop;
        Vector3 remain = target - transform.position;
        remain.y = 0f;
        Vector3 before = transform.position;
        controller.Move(Vector3.ClampMagnitude(remain, PushSpeed() * Time.deltaTime));

        Vector3 moved = transform.position - before;
        moved.y = 0f;
        if (moved.sqrMagnitude < 0.000001f)
            EndPlayerPush();
    }

    // 这一次推人用完。没松键再走进半距，要重新待满 1 秒。
    void EndPlayerPush()
    {
        pushingPlayer = false;
        playerPushUsed = true;
        insideTimer = 0f;
        locked = null;
    }

    // 米/秒。走路速度 × 倍数。没有 PlayerMove 时用脚本默认 4。
    float PushSpeed()
    {
        float walk = move != null ? move.speed : 4f;
        return walk * pushSpeedScale;
    }

    // 水平距离最近、且在半距或整段停距内的一只。needSlide 为真时跳过本轮已经滑过的怪。
    DummyChase Closest(bool half, DummyChase exclude = null, bool needSlide = false)
    {
        DummyChase best = null;
        float bestDist = float.MaxValue;
        for (int i = 0; i < all.Count; i++)
        {
            DummyChase chase = all[i];
            if (chase == null || chase == exclude || !chase.IsInside(transform.position, half))
                continue;
            if (needSlide && !chase.canSlide)
                continue;

            float dist = chase.FlatDistance(transform.position);
            if (dist < bestDist)
            {
                best = chase;
                bestDist = dist;
            }
        }
        return best;
    }
}
