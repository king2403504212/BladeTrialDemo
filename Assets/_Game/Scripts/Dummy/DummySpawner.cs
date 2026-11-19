using System.Collections;
using UnityEngine;

/// <summary>
/// 挂在场景空物体 DummySpawner 上。Dummy 受击播完销毁后，在原位置再生成一只。
/// 场景里第一只 Dummy 仍手动摆；本脚本只负责死后刷新。波次以后再接到这里。
/// </summary>
public class DummySpawner : MonoBehaviour
{
    // Dummy 预制体。Inspector 拖 Assets/_Game/Prefabs/Dummy。空则死后无法刷新。
    public DummyTarget dummyPrefab;

    // 秒。消失后再过多久出现下一只。大了场上空得久，小了像没死干净。
    public float respawnDelay = 0.5f;

    // DummyTarget 血量归零、受击播完后调用。按死者的位置和朝向排队生成下一只。
    public void RespawnAt(Vector3 position, Quaternion rotation)
    {
        if (dummyPrefab == null)
        {
            Debug.LogWarning("[DummySpawner] 没有 Dummy 预制体，死后不刷新。", this);
            return;
        }

        StartCoroutine(SpawnAfter(position, rotation));
    }

    // 等 respawnDelay 再 Instantiate，避免下一只和正在消失的叠在一起。
    IEnumerator SpawnAfter(Vector3 position, Quaternion rotation)
    {
        yield return new WaitForSeconds(respawnDelay);
        DummyTarget next = Instantiate(dummyPrefab, position, rotation);
        next.name = dummyPrefab.name;
        Debug.Log($"[DummySpawner] 刷新 Dummy，位置 {position}。", this);
    }
}
