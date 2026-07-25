using System.Collections.Generic;
using UnityEngine;

// 강화학습 에이전트가 "관찰"할 게임 상태를 담는 구조체.
// M3에서 ML-Agents의 관찰값(Observation)으로 거의 그대로 연결할 예정.
public struct GameState
{
    public float playerHealthRatio; // 시작 체력 대비 현재 체력 (0~1)
    public int gold;                // 보유 골드
    public int stage;               // 현재 스테이지
    public int towerCount;          // 설치된 타워 수
    public int totalTowerCost;      // 설치된 타워에 든 총 골드 (방어 투자량)
    public float totalTowerDps;     // 타워 총 화력(초당 데미지 합)
    public float pathCoverage;      // 경로 타일 중 타워 사거리에 덮인 비율 (0~1)
    public int lastRoundLeaked;     // 직전 라운드에서 골에 도달한 적 수
    public int lastRoundHealthLost; // 직전 라운드에서 잃은 체력
    public float towerUtilization;  // 직전 라운드 타워 평균 가동률 (0~1, 낮으면 놀던 타워 많음)
    public int pathLength;          // 경로 칸 수 (맵 난이도: 길수록 타워가 쏠 시간 많아 쉬움)

    // 강화학습에 넣을 순수 숫자 벡터 (순서 고정)
    public float[] ToVector()
    {
        return new float[]
        {
            playerHealthRatio,
            gold,
            stage,
            towerCount,
            totalTowerCost,
            totalTowerDps,
            pathCoverage,
            lastRoundLeaked,
            lastRoundHealthLost,
            towerUtilization,
            pathLength
        };
    }

    public string ToLogString(int round, int totalRounds)
    {
        return $"라운드 {round}/{totalRounds} | 체력비율 {playerHealthRatio:0.00} | 골드 {gold} | "
             + $"타워 {towerCount}(투자 {totalTowerCost}골드) | 총DPS {totalTowerDps:0.0} | 경로커버 {pathCoverage:0.00} | "
             + $"직전누수 {lastRoundLeaked} | 직전체력손실 {lastRoundHealthLost} | 타워가동률 {towerUtilization:0.00} | 경로칸 {pathLength}";
    }
}

// 게임 상태를 숫자로 수집하는 계층 (설계 문서의 '역할 A: 게임 분석').
// 지금은 로그로 검증하고, M3에서 이 값을 그대로 관찰값으로 사용한다.
public class GameStateCollector : MonoBehaviour
{
    public GridManager grid;   // 비우면 자동으로 찾음

    // 직전 라운드 결과 (RoundManager가 라운드 종료 시 채워준다)
    [HideInInspector] public int lastRoundLeaked = 0;
    [HideInInspector] public int lastRoundHealthLost = 0;
    [HideInInspector] public float lastRoundTowerUtilization = 0f;

    void Start()
    {
        if (grid == null)
            grid = Object.FindFirstObjectByType<GridManager>();
    }

    // 현재 게임 상태를 관찰 벡터로 조립한다
    public GameState Collect()
    {
        GameState gs = new GameState();

        GameManager gm = GameManager.Instance;
        gs.playerHealthRatio = (gm != null) ? gm.HealthRatio() : 0f;
        gs.gold = (gm != null) ? gm.gold : 0;
        gs.stage = GameManager.stage;

        Tower[] towers = Object.FindObjectsByType<Tower>(FindObjectsSortMode.None);
        gs.towerCount = towers.Length;

        float dps = 0f;
        int totalCost = 0;
        foreach (Tower t in towers)
        {
            if (t.shootingSpeed > 0f)
                dps += t.damage / t.shootingSpeed; // shootingSpeed = 공격 간격(초)
            totalCost += t.cost;                   // 각 타워 설치 비용 합
        }
        gs.totalTowerDps = dps;
        gs.totalTowerCost = totalCost;

        gs.pathCoverage = ComputeCoverage(towers);
        gs.lastRoundLeaked = lastRoundLeaked;
        gs.lastRoundHealthLost = lastRoundHealthLost;
        gs.towerUtilization = lastRoundTowerUtilization;
        gs.pathLength = (grid != null && grid.pathCoords != null) ? grid.pathCoords.Count : 0;

        return gs;
    }

    // 경로 타일 중, 어떤 타워든 사거리에 들어오는 타일의 비율
    float ComputeCoverage(Tower[] towers)
    {
        if (grid == null) return 0f;
        List<Vector3> pts = grid.GetPathWorldPoints();
        if (pts.Count == 0) return 0f;

        int covered = 0;
        foreach (Vector3 p in pts)
        {
            foreach (Tower t in towers)
            {
                Vector3 a = p; a.y = 0f;
                Vector3 b = t.transform.position; b.y = 0f;
                if (Vector3.Distance(a, b) <= t.maxRange)
                {
                    covered++;
                    break;
                }
            }
        }
        return (float)covered / pts.Count;
    }

    // 라운드 종료 시 호출 → 현재 상태를 로그로 남긴다 (M2 검증용)
    public void LogRoundEnd(int roundNumber, int totalRounds)
    {
        GameState gs = Collect();
        Debug.Log("[상태] " + gs.ToLogString(roundNumber, totalRounds));
    }
}
