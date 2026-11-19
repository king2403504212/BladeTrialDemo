using UnityEngine;

/// <summary>
/// 挂在 Main Camera 上。绕角色转越肩镜头：按住鼠标右键拖，改 yaw/pitch。
/// 弹簧臂：机位永远是「注视点 + 朝向 × 臂长」。撞墙立刻缩短臂长；离开后按 recoverySpeed 拉回。
/// 注视点优先用骨骼上的 CameraLook（Look Point）；空着才用脚底 + lookHeight。
/// 拉回每帧重新探测，再撞就马上收。起点已在墙里时不往回收，避免钻进角色黑屏。
/// </summary>
public class CameraFollow : MonoBehaviour
{
    // 拖 Hierarchy 里的 Player。挡墙用这个判断「自己」。空着会在 Start 里按名字找。
    public Transform target;

    // 拖 Spine 下的 CameraLook。镜头看这里，滑铲等动作会跟着骨头走。不要拖到 Target。
    public Transform lookPoint;

    // 米。镜头到注视点的距离。大了看得远，小了更贴近后背。
    public float distance = 2f;

    // 米。Look Point 为空时的兜底：注视点相对脚底的高度。有 CameraLook 时不用这个。
    public float lookHeight = 0.8f;

    // 度。开局俯视角度。大了更俯视，小了更平视。
    public float pitch = 25f;

    // 度。水平朝向。0 表示从南边看向世界 +Z。
    public float yaw = 0f;

    // 度。俯仰下限/上限，防止翻到地底下或转到头顶。
    public float minPitch = 5f;
    public float maxPitch = 70f;

    // 鼠标移动到转角的倍率。大了转得快。不要乘 deltaTime，轴输入已是这一帧的位移。
    public float sensitivity = 3f;

    // 米。挡墙探测球半径。大了贴墙时镜头被顶得很近，小了会从墙缝漏到隔壁。
    public float collisionRadius = 0.25f;

    // 米。收到墙内侧再留空隙。小于相机 Near Clip 仍会裁穿墙面。
    public float collisionSkin = 0.2f;

    // 要挡镜头的层。默认 Default + Environment：地图还在 Default 时也能挡；改到 Environment 后请只勾 Environment。
    public LayerMask collisionMask = 65;

    // 调试：在镜头处画一颗与 collisionRadius 同大的球。看完关掉或删掉生成物。
    public bool showProbe = true;

    // 米。弹簧臂最短长度。再短会钻进脑袋黑屏。撞墙也不会短过它，必要时允许轻微穿模。
    public float minDistance = 0.8f;

    // 米/秒。离开障碍后臂长拉回的速度。大了弹得快，小了拖泥带水。只影响拉远，收近仍然立刻。
    public float recoverySpeed = 8f;

    // 运行时生成的调试球；showProbe 关着就是空。
    Transform probe;
    // 当前臂长。收近立刻写成探测值，拉远每帧往 distance 挪，不直接 Lerp 穿墙。
    float currentArm;

    void Start()
    {
        if (target == null)
        {
            // 按名字找到的 Player；只作 target 为空时的兜底。
            GameObject player = GameObject.Find("Player");
            if (player != null)
                target = player.transform;
        }

        if (target == null)
            Debug.LogWarning("[CameraFollow] 没有跟随目标，镜头不会动。请拖 Player 到 Target。", this);

        if (lookPoint == null)
        {
            // 只作 lookPoint 为空时的兜底。改名后请手动拖。
            GameObject look = GameObject.Find("CameraLook");
            if (look != null)
                lookPoint = look.transform;
        }

        if (lookPoint == null)
            Debug.LogWarning("[CameraFollow] 没有 Look Point，注视点用脚底+Look Height。请拖 CameraLook 到 Look Point。", this);

        if (collisionMask == 0)
            Debug.LogWarning("[CameraFollow] Collision Mask 是 Nothing，镜头不会挡墙。请勾选 Default 或 Environment。", this);

        // 开局臂长等于空旷距离，避免第一帧从 0 拉出来。
        currentArm = distance;

        if (showProbe)
        {
            // 只看不撞的探测球，半径跟 collisionRadius 走；看完关 showProbe。
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "CameraProbe";
            Object.Destroy(go.GetComponent<Collider>());
            probe = go.transform;
        }
    }

    // LateUpdate 在角色 Update 移动之后再摆相机，避免镜头比人慢一帧。
    void LateUpdate()
    {
        if (target == null)
            return;

        // GetMouseButton(1) 是右键按住。滑动以后替换这一段输入即可。
        if (Input.GetMouseButton(1))
        {
            yaw += Input.GetAxis("Mouse X") * sensitivity;
            pitch -= Input.GetAxis("Mouse Y") * sensitivity;
            pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        }

        // 有 CameraLook 就看节点（跟骨骼）；否则仍是脚底往上 lookHeight。
        Vector3 focus = lookPoint != null
            ? lookPoint.position
            : target.position + Vector3.up * lookHeight;
        Quaternion orbit = Quaternion.Euler(pitch, yaw, 0f);
        // 弹簧臂方向。机位只沿这根臂伸缩，不换轨道。
        Vector3 dir = orbit * Vector3.back;
        // 这一帧弹簧臂最多能伸到哪。拉远时用它当上限，避免 Lerp 进墙。
        float allowed = ProbeArmLength(focus, dir, distance);
        currentArm = SmoothArm(currentArm, allowed);
        transform.position = focus + dir * currentArm;
        transform.LookAt(focus);

        if (showProbe)
        {
            // 黄线理想臂长，绿线实际臂长，Scene 里对照穿模。
            Debug.DrawLine(focus, focus + dir * distance, Color.yellow);
            Debug.DrawLine(focus, transform.position, Color.green);
        }

        if (probe != null)
        {
            probe.position = transform.position;
            probe.localScale = Vector3.one * (collisionRadius * 2f);
        }
    }

    /// <summary>
    /// LateUpdate 调用。沿弹簧臂探测：撞墙缩短臂长，但不短过 minDistance；起点已在墙里则不收。
    /// </summary>
    float ProbeArmLength(Vector3 focus, Vector3 dir, float desired)
    {
        float radius = Mathf.Max(0.01f, collisionRadius);
        float skin = Mathf.Max(0f, collisionSkin);
        // 贴脸下限。0.8 米大约在后脑勺后方，再近会看到头腔。
        float minArm = Mathf.Max(0.01f, minDistance);

        // 胸口已经埋进挡板时，SphereCast 会忽略这块板，再往回收只会钻进角色。
        if (Physics.CheckSphere(focus, radius, collisionMask, QueryTriggerInteraction.Ignore))
        {
            if (!FocusOnlyHitsSelf(focus, radius))
                return desired;
        }

        if (!Physics.SphereCast(
                focus, radius, dir, out RaycastHit hit, desired, collisionMask, QueryTriggerInteraction.Ignore))
            return desired;

        if (IsOwnTransform(hit.transform))
            return desired;

        return Mathf.Max(minArm, hit.distance - skin);
    }

    /// <summary>
    /// LateUpdate 调用。探测值比当前短就立刻收；比当前长才按 recoverySpeed 拉远。
    /// </summary>
    float SmoothArm(float from, float allowed)
    {
        if (allowed < from)
            return allowed;

        // 米/秒。0 会停在贴墙处拉不回去。
        float speed = Mathf.Max(0f, recoverySpeed);
        return Mathf.MoveTowards(from, allowed, speed * Time.deltaTime);
    }

    /// <summary>
    /// ProbeArmLength 调用。CheckSphere 打到的若全是角色自己，不算埋进地图。
    /// </summary>
    bool FocusOnlyHitsSelf(Vector3 focus, float radius)
    {
        Collider[] hits = Physics.OverlapSphere(focus, radius, collisionMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            if (!IsOwnTransform(hits[i].transform))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 碰撞求解调用。角色和手上的斧都不当墙。
    /// </summary>
    bool IsOwnTransform(Transform t)
    {
        if (t == null || target == null)
            return false;
        return t == target || t.IsChildOf(target);
    }
}
