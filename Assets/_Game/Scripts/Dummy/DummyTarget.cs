using System.Collections;
using UnityEngine;

/// <summary>
/// 挂在 Dummy / MaleMonster 根上（胶囊判定）。被攻击盒扫中时扣血。
/// 未死：SetTrigger Hit。归零：只 SetTrigger Die（不播这次 Hit），等 Die 播完再销毁并刷新。
/// </summary>
public class DummyTarget : MonoBehaviour
{
    // 刀数。调大更耐砍。占位伤害：每次 Hit 扣 1。
    public int maxHp = 3;

    // 当前血量；Awake 从 maxHp 拷贝，Hit 里递减，归零后进死亡流程。
    int hp;
    // 模型子物体上的 Animator；未死 Trigger Hit，归零 Trigger Die。
    Animator animator;
    // 已经进入死亡流程。防止连砍再开协程，Chase 用 IsDead 停手。
    bool dying;

    // Chase 每帧问：为真则停追停砍。归零或正在播死后为真。
    public bool IsDead => dying || hp <= 0;

    void Awake()
    {
        hp = maxHp;
        dying = false;
        animator = GetComponentInChildren<Animator>();
        if (animator == null)
            Debug.LogWarning($"[DummyTarget] {name} 子物体没有 Animator，受击不会播。", this);
    }

    // PlayerAttack 扫到本物体时调用：扣 1 血；未死播 Hit，归零只播 Die 再等销毁。
    public void Hit()
    {
        if (hp <= 0 || dying)
            return;

        hp -= 1;

        if (hp <= 0)
        {
            dying = true;
            if (animator != null)
            {
                // 清掉未消耗的 Attack，避免 Upper 还在挥爪。
                animator.ResetTrigger("Attack");
                animator.ResetTrigger("Hit");
                animator.SetTrigger("Die");
            }
            else
                Debug.LogWarning($"[DummyTarget] {name} 没有 Animator，跳过死亡动画。", this);

            Debug.Log($"[DummyTarget] {name} 血量归零，播 Die。", this);
            StartCoroutine(DieAfterAnim());
            return;
        }

        if (animator != null)
            animator.SetTrigger("Hit");
        else
            Debug.LogWarning($"[DummyTarget] {name} 没有 Animator，跳过受击动画。", this);

        Debug.Log($"[DummyTarget] {name} 被击中，剩余 {hp}/{maxHp}。", this);
    }

    // 等到进入 Die 且播到 0.9，再刷新并销毁。Trigger 没接上则超时后仍销毁。
    IEnumerator DieAfterAnim()
    {
        // 秒。Die 状态没接上时不要一直等。
        float timeout = 3f;
        while (timeout > 0f && !IsInDie())
        {
            timeout -= Time.deltaTime;
            yield return null;
        }

        while (IsInDie() && DieNormalizedTime() < 0.9f)
            yield return null;

        DummySpawner spawner = FindObjectOfType<DummySpawner>();
        if (spawner != null)
            spawner.RespawnAt(transform.position, transform.rotation);
        else
            Debug.LogWarning("[DummyTarget] 没有 DummySpawner，死后不刷新。", this);

        Destroy(gameObject);
    }

    // 当前层是否在播或正在转入 Die。死亡等待认这个名字。
    bool IsInDie()
    {
        return IsInBaseState("Die");
    }

    // Die 片段播到哪，0～1；还没进 Die 为 -1。
    float DieNormalizedTime()
    {
        if (animator == null)
            return 1f;

        AnimatorStateInfo cur = animator.GetCurrentAnimatorStateInfo(0);
        if (cur.IsName("Die"))
            return cur.normalizedTime;

        if (animator.IsInTransition(0))
        {
            AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(0);
            if (next.IsName("Die"))
                return next.normalizedTime;
        }

        return -1f;
    }

    // Base 层（0）状态名是否匹配；转入中认下一状态。
    bool IsInBaseState(string stateName)
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
}
