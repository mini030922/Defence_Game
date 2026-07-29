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

    // ── 아래는 DDA 관측 보강용 (2026-07-26 추가) ──────────────────────────
    public float lastRoundLinger;      // 스폰 완료 후 소탕까지 걸린 시간(초). '여유' 지표 —
                                       // 체력을 안 잃어도 간신히 잡았는지 압살했는지를 구분한다.
    public float lastRoundSlowRatio;   // 감속을 건 적 / 스폰된 적 (0~1). 지원 타워의 간접 기여.
    public int   lastRoundMaxAlive;    // 라운드 중 최대 동시 생존 적 수 (필드가 얼마나 밀렸나)
    public float lastRoundMaxProgress; // 적이 도달한 최대 경로 진행도 (0~1). 안 샜어도 골 앞까지 갔는지.
    public float pathFoldRatio;        // 경로 접힘 정도 (일직선=낮음, 뱀형=높음). 같은 칸 수라도 체감 난이도가 다르다.
    public int[] towerCountByType;     // 종류별 타워 수 (5). 플레이어 '구성'을 알아야 반례 적을 보낼 수 있다.
    public int[] upgradeLevelByType;   // 종류별 업그레이드 레벨 (5). 같은 DPS라도 적은 수+고레벨과 많은 수+0레벨은 다른 상대.

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
    public GameObject[] towerPrefabs; // 종류 순서 기준. 비우면 BuildManager에서 자동으로 가져옴

    [Header("경로 접힘 정도 계산")]
    public float foldRadius = 3f;       // 각 경로 칸 주변 이 반경 안의 다른 경로 칸을 센다
    public float foldRefNeighbors = 12f;// 평균 이웃 수가 이만큼이면 1.0 (일직선은 대략 6)

    // 직전 라운드 결과 (RoundManager가 라운드 종료 시 채워준다)
    [HideInInspector] public int lastRoundLeaked = 0;
    [HideInInspector] public int lastRoundHealthLost = 0;
    [HideInInspector] public float lastRoundTowerUtilization = 0f;
    [HideInInspector] public float lastRoundLinger = 0f;
    [HideInInspector] public float lastRoundSlowRatio = 0f;
    [HideInInspector] public int lastRoundMaxAlive = 0;
    [HideInInspector] public float lastRoundMaxProgress = 0f;

    void Start()
    {
        if (grid == null)
            grid = Object.FindFirstObjectByType<GridManager>();
        EnsureTowerTypes();
    }

    // 타워 종류 순서를 BuildManager에서 가져온다 (배치 AI와 같은 순서를 쓰기 위함)
    void EnsureTowerTypes()
    {
        if (towerPrefabs != null && towerPrefabs.Length > 0) return;
        BuildManager bm = Object.FindFirstObjectByType<BuildManager>();
        if (bm != null && bm.towerPrefabs != null) towerPrefabs = bm.towerPrefabs;
    }

    string TypeName(int idx)
    {
        if (towerPrefabs == null || idx < 0 || idx >= towerPrefabs.Length || towerPrefabs[idx] == null) return "";
        Tower t = towerPrefabs[idx].GetComponent<Tower>();
        return (t != null && !string.IsNullOrEmpty(t.towerName)) ? t.towerName : towerPrefabs[idx].name;
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

        // ── DDA 관측 보강분 ─────────────────────────────
        gs.lastRoundLinger = lastRoundLinger;
        gs.lastRoundSlowRatio = lastRoundSlowRatio;
        gs.lastRoundMaxAlive = lastRoundMaxAlive;
        gs.lastRoundMaxProgress = lastRoundMaxProgress;
        gs.pathFoldRatio = ComputeFoldRatio();

        EnsureTowerTypes();
        gs.towerCountByType = new int[5];
        gs.upgradeLevelByType = new int[5];
        var upm = TowerUpgradeManager.Instance;
        for (int i = 0; i < 5; i++)
        {
            string nm = TypeName(i);
            if (string.IsNullOrEmpty(nm)) continue;
            int c = 0;
            foreach (Tower t in towers) if (t != null && t.towerName == nm) c++;
            gs.towerCountByType[i] = c;
            gs.upgradeLevelByType[i] = (upm != null) ? upm.GetLevel(nm) : 0;
        }

        return gs;
    }

    // 경로가 얼마나 '접혀' 있는가 — 각 경로 칸의 foldRadius 안에 있는 다른 경로 칸 수의 평균.
    // 일직선이면 대략 6, 뱀형으로 접혀 있으면 인접 레인이 더해져 10~14가 나온다.
    // 같은 경로 길이라도 일직선 맵은 타워 하나가 덮는 구간이 적어 체감 난이도가 훨씬 높다.
    float ComputeFoldRatio()
    {
        var pl = (grid != null) ? grid.pathCoords : null;
        int n = (pl != null) ? pl.Count : 0;
        if (n < 2) return 0f;

        float r2 = foldRadius * foldRadius;
        long total = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                float dx = pl[i].x - pl[j].x, dy = pl[i].y - pl[j].y;
                if (dx * dx + dy * dy <= r2) total++;
            }
        float avg = total / (float)n;
        return Mathf.Clamp01(avg / Mathf.Max(1f, foldRefNeighbors));
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
                // 조준 사거리가 아니라 커버리지용 상한(미사일 999 방지)을 쓴다
                if (Vector3.Distance(a, b) <= t.CoverageRange)
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

    // 에피소드(스테이지 한 판) 종료 시 딱 한 줄 — 타워 '구성'을 남긴다. (2026-07-29 추가)
    //
    // 왜 필요한가: 지금까지 로그에는 타워 '총 개수'와 '총 DPS'만 있어서,
    //   "빠른 적이 늘면 속사 타워 비율이 오르는가", "지원 타워는 여전히 죽은 카드인가" 같은
    //   구성 질문에 답을 할 수가 없었다. 강화 분포는 [강화] 로그로 간접 추정했지만
    //   그건 '올린 횟수'지 '판이 끝났을 때 실제로 보유한 레벨'이 아니다.
    //   에피소드당 한 줄이라 로그 용량 부담도 거의 없다.
    public void LogComposition(bool cleared, int reachedRound, int totalRounds)
    {
        GameState gs = Collect();
        var sb = new System.Text.StringBuilder();
        sb.Append($"[구성] S{gs.stage} 결과={(cleared ? "클리어" : "실패")} R{reachedRound}/{totalRounds} 타워[");
        for (int i = 0; i < 5; i++)
        {
            string nm = TypeName(i);
            if (string.IsNullOrEmpty(nm)) continue;
            if (i > 0) sb.Append(' ');
            sb.Append($"{nm}:{gs.towerCountByType[i]}");
        }
        sb.Append("] 강화[");
        for (int i = 0; i < 5; i++)
        {
            if (i > 0) sb.Append('/');
            sb.Append(gs.upgradeLevelByType[i]);
        }
        sb.Append($"] 총DPS {gs.totalTowerDps:0.0} 골드 {gs.gold} 경로칸 {gs.pathLength}");
        Debug.Log(sb.ToString());
    }
}
