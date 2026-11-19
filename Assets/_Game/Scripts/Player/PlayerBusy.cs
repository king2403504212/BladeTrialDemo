using UnityEngine;

/// <summary>
/// 角色当前占用。同一时刻只允许一个动作占坑，走路 / 砍 / 铲都问这把锁。
/// </summary>
public enum PlayerOccupy
{
    // 空闲。走路、砍、铲都可以开始。
    None,
    // 挥砍占着。走路停，铲不能开始。
    Attack,
    // 滑铲占着。走路停，砍不能开始。
    Slide
}

/// <summary>
/// 挂在 Player 上。全角色共用的占用锁：空闲才能上锁，只有占有者能解开。
/// PlayerMove 读 Locked 停水平走；PlayerAttack / PlayerDodge 用 TryLock 互斥。
/// </summary>
public class PlayerBusy : MonoBehaviour
{
    // 谁正占着身体。TryLock 写入，Unlock 清掉；外人只读。
    public PlayerOccupy Current { get; private set; }

    // 走路每帧问：为真就清 WASD。不区分砍还是铲。
    public bool Locked => Current != PlayerOccupy.None;

    // 攻击 / 滑铲出手前调用。空闲则占上并返回 true；已被占则 false，调用方应忽略按键。
    public bool TryLock(PlayerOccupy who)
    {
        if (who == PlayerOccupy.None)
            return false;
        if (Current != PlayerOccupy.None)
            return false;
        Current = who;
        return true;
    }

    // 占有者在动画结束或超时时调用。名字对不上就不动，避免砍的超时把铲的锁清掉。
    public void Unlock(PlayerOccupy who)
    {
        if (Current != who)
            return;
        Current = PlayerOccupy.None;
    }
}
