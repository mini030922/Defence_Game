using System.Collections.Generic;
using UnityEngine;

// 학습용 봇 플레이어.
// 사람 대신, 경로 옆 빈 타일에 예산 안에서 타워를 자동으로 짓는다.
// 학습이 '다양한 실력의 플레이어'에 대응하도록 에피소드마다 실력(skill)을 무작위화한다.
public class AutoPlayerBot : MonoBehaviour
{
    [Header("연결")]
    public GridManager grid;            // 비우면 자동으로 찾음
    public GameObject[] towerPrefabs;    // BuildManager와 동일하게 사용할 타워들

    [Header("행동")]
    public float buildInterval = 0.5f;   // 건설 시도 간격(초)
    [Range(0f, 1f)] public float skill = 1f; // 1=예산 적극 사용, 낮을수록 소극적
    [Range(0f, 1f)] public float upgradeChance = 0.4f; // 새로 짓는 대신 기존 타워 업그레이드할 확률

    [Header("에피소드마다 실력 무작위화 (학습 다양성)")]
    public bool randomizeSkillEachEpisode = true;
    public float minSkill = 0.3f;
    public float maxSkill = 1.0f;

    private float timer;

    void Start()
    {
        if (grid == null) grid = Object.FindFirstObjectByType<GridManager>();
    }

    // RoundManager가 에피소드 리셋 때 호출 → 이번 판 봇 실력 결정
    public void OnEpisodeReset()
    {
        if (randomizeSkillEachEpisode)
            skill = Random.Range(minSkill, maxSkill);
        timer = 0f;
    }

    void Update()
    {
        timer -= Time.deltaTime;
        if (timer > 0f) return;
        timer = buildInterval;
        TryBuildOne();
    }

    void TryBuildOne()
    {
        if (grid == null || towerPrefabs == null || towerPrefabs.Length == 0) return;
        GameManager gm = GameManager.Instance;
        if (gm == null) return;

        // 실력이 낮으면 이번 시도를 확률적으로 건너뜀 (약한 플레이어 흉내)
        if (Random.value > skill) return;

        // 가끔 새로 짓는 대신 기존 타워를 업그레이드
        if (Random.value < upgradeChance && TryUpgradeRandomTower(gm)) return;

        GameObject prefab = PickAffordableTower(gm.gold);
        if (prefab == null) return;

        Tower data = prefab.GetComponent<Tower>();
        float range = (data != null) ? data.maxRange : 3f;

        Tile spot = PickBuildSpot(range);
        if (spot == null) return;

        int baseCost = (data != null) ? data.cost : 0;
        string builtName = (data != null && !string.IsNullOrEmpty(data.towerName)) ? data.towerName : prefab.name;
        int cost = gm.GetScaledCost(baseCost, builtName);
        if (!gm.SpendGold(cost)) return;

        GameObject go = Instantiate(prefab, spot.transform.position, Quaternion.identity);
        spot.occupied = true;
        gm.RegisterTowerBuilt(builtName);

        // 이미 강화된 종류면 새 타워도 즉시 그 레벨로
        if (TowerUpgradeManager.Instance != null)
            TowerUpgradeManager.Instance.ApplyCurrentLevel(go.GetComponent<Tower>());
    }

    // 존재하는 타워 종류 중 하나를 골라 그 종류 '전체'를 업그레이드 (여유되면). 성공 시 true.
    bool TryUpgradeRandomTower(GameManager gm)
    {
        var mgr = TowerUpgradeManager.Instance;
        if (mgr == null) return false;
        Tower[] towers = Object.FindObjectsByType<Tower>(FindObjectsSortMode.None);
        if (towers.Length == 0) return false;
        Tower t = towers[Random.Range(0, towers.Length)];
        return mgr.UpgradeType(t.towerName);
    }

    // 지금 살 수 있는 타워 중 하나를 무작위로 (플레이 다양성)
    GameObject PickAffordableTower(int gold)
    {
        // 경로 옆 빈자리가 남아있으면 단거리 주력부터, 원거리(지원/미사일)는 뒤로 미룸
        bool preferShort = CountFreeNearPath() > 0;

        List<GameObject> affordable = new List<GameObject>();
        List<GameObject> affordableAny = new List<GameObject>();
        foreach (GameObject p in towerPrefabs)
        {
            if (p == null) continue;
            Tower t = p.GetComponent<Tower>();
            if (t == null) continue;
            int c = (GameManager.Instance != null) ? GameManager.Instance.GetScaledCost(t.cost, t.towerName) : t.cost;
            if (c > gold) continue;

            affordableAny.Add(p);
            bool longRange = t.maxRange >= 5f;
            if (preferShort && longRange) continue; // 경로 옆 자리 있을 땐 원거리 제외
            affordable.Add(p);
        }

        if (affordable.Count == 0) affordable = affordableAny; // 단거리가 없으면(다 비쌈) 아무거나
        if (affordable.Count == 0) return null;
        return affordable[Random.Range(0, affordable.Count)];
    }

    // 경로 바로 옆(거리 1.5 이내)의 설치 가능한 빈 타일 수
    int CountFreeNearPath()
    {
        int count = 0;
        for (int x = 0; x < grid.width; x++)
            for (int y = 0; y < grid.height; y++)
            {
                Tile t = grid.GetTile(new Vector2Int(x, y));
                if (t == null || !t.CanBuild()) continue;
                if (DistToPath(x, y) <= 1.5f) count++;
            }
        return count;
    }

    // 타워 사거리를 고려해 설치 자리를 고른다.
    // - 원거리(사거리 ≥ 5): 경로에서 떨어진 안쪽 우선(경로 옆 자리는 단거리 타워용으로 아낌)
    // - 단거리: 경로 인접 코너 우선
    Tile PickBuildSpot(float range)
    {
        List<Tile> nearPath = new List<Tile>();  // 경로 바로 옆
        List<Tile> interior = new List<Tile>();  // 경로에서 떨어졌지만 사거리로 닿는 곳

        for (int x = 0; x < grid.width; x++)
        {
            for (int y = 0; y < grid.height; y++)
            {
                Tile t = grid.GetTile(new Vector2Int(x, y));
                if (t == null || !t.CanBuild()) continue;
                float d = DistToPath(x, y);
                if (d > range) continue;            // 경로를 못 때리는 자리는 제외
                if (d <= 1.5f) nearPath.Add(t);
                else interior.Add(t);
            }
        }

        bool longRange = range >= 5f;

        if (longRange)
        {
            // 원거리: 안쪽 우선, 없으면 경로 옆
            List<Tile> pool = (interior.Count > 0) ? interior : nearPath;
            if (pool.Count == 0) return null;
            return pool[Random.Range(0, pool.Count)];
        }

        // 단거리: 경로 인접 코너 우선
        if (nearPath.Count == 0)
            return (interior.Count > 0) ? interior[Random.Range(0, interior.Count)] : null;

        if (Random.value < 0.25f)  // 가끔 무작위(다양성)
            return nearPath[Random.Range(0, nearPath.Count)];

        int bestScore = -1;
        List<Tile> best = new List<Tile>();
        foreach (Tile c in nearPath)
        {
            int score = PathTilesNear(c.coord);
            if (score > bestScore) { bestScore = score; best.Clear(); best.Add(c); }
            else if (score == bestScore) best.Add(c);
        }
        return best[Random.Range(0, best.Count)];
    }

    // 이 좌표에서 가장 가까운 경로 타일까지의 거리(유클리드)
    float DistToPath(int x, int y)
    {
        float best = float.MaxValue;
        foreach (Vector2Int p in grid.pathCoords)
        {
            float dx = p.x - x, dy = p.y - y;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            if (d < best) best = d;
        }
        return best;
    }

    // 이 좌표 주변(맨해튼 거리 2 이내)의 경로 타일 수 — 코너·교차 자리일수록 크다
    int PathTilesNear(Vector2Int coord)
    {
        int count = 0;
        foreach (Vector2Int p in grid.pathCoords)
            if (Mathf.Abs(p.x - coord.x) + Mathf.Abs(p.y - coord.y) <= 2)
                count++;
        return count;
    }
}
