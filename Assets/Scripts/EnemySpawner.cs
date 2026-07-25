using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// 적을 주기적으로 만들어 경로를 따라 보내는 임시 스포너.
// (다음 단계에서 '라운드/웨이브 매니저'로 발전시킬 예정)
public class EnemySpawner : MonoBehaviour
{
    [Header("연결")]
    public GridManager grid;   // 비워두면 씬에서 자동으로 찾음

    [Header("스폰 설정")]
    public float spawnInterval = 1.5f; // 적 생성 간격(초)
    public float enemyHealth = 10f;
    public float enemySpeed = 2f;
    public float enemyHeight = 0.6f;   // 타일 위로 띄우는 높이
    public Color enemyColor = new Color(0.85f, 0.3f, 0.3f);

    void Start()
    {
        // Unity 6의 최신 API. (구버전 FindObjectOfType 대체)
        if (grid == null)
            grid = Object.FindFirstObjectByType<GridManager>();

        StartCoroutine(SpawnLoop());
    }

    IEnumerator SpawnLoop()
    {
        while (true)
        {
            SpawnOne();
            yield return new WaitForSeconds(spawnInterval);
        }
    }

    void SpawnOne()
    {
        if (grid == null) return;

        List<Vector3> path = grid.GetPathWorldPoints();
        if (path == null || path.Count == 0) return;

        // 임시 비주얼: 캡슐 (나중에 적 프리팹으로 교체)
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = "Enemy";
        go.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);

        Vector3 start = path[0];
        go.transform.position = new Vector3(start.x, enemyHeight, start.z);

        // 색 입히기 (머티리얼 누수 없이)
        Renderer rend = go.GetComponent<Renderer>();
        MaterialPropertyBlock mpb = new MaterialPropertyBlock();
        rend.GetPropertyBlock(mpb);
        mpb.SetColor("_BaseColor", enemyColor);
        mpb.SetColor("_Color", enemyColor);
        rend.SetPropertyBlock(mpb);

        Enemy enemy = go.AddComponent<Enemy>();
        enemy.Init(path, enemyHealth, enemySpeed);
        enemy.onReachedEnd = (e) => Debug.Log("적이 골에 도달!");
        enemy.onDied = (e) => Debug.Log("적 처치!");
    }
}
