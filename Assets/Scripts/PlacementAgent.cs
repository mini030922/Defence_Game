using System.Collections;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

// 최적 배치 AI (설계상 '역할 B: 플레이어 도우미 / 최적 타워 배치').
// - 관찰: 전체 그리드(설치가능/경로/타워위치/경로진행도/경로근접도 5채널) + 상태 스칼라
// - 행동(멀티 디스크리트 3브랜치):
//     branch0: 무엇을 할지  [0=아무것도, 1..T=타워 종류 건설, T+1..2T=종류 업그레이드]
//     branch1: 타일 X (0..GRID_W-1)
//     branch2: 타일 Y (0..GRID_H-1)
//   → 유효하지 않은 선택은 액션 마스킹으로 대부분 차단
// - 보상: 라운드를 적은 피해로 넘길수록 +, 죽으면 큰 -, 스테이지 클리어 시 +보너스 (DDA와 반대 목표)
//
// 에피소드 루프는 DifficultyAgent(DDA)가 주도한다. 이 에이전트는 '정비 단계'에
// RoundManager가 BuildPhase()를 돌려 여러 번 결정을 내리게 하고, 라운드 결과로 보상만 받는다.
//
// 인스펙터(Behavior Parameters) 설정:
//   - Behavior Name = Placement
//   - Vector Observation > Space Size = GRID_W*GRID_H*5 + 12  (기본 20*16*5+12 = 1612)
//   - Actions > Discrete Branches = 3, 크기 = [1+2*타워종류수, GRID_W, GRID_H] = 기본 [11, 20, 16]
//   - Behavior Type = Default(학습) / Inference Only(고정 상대로 쓸 때)
//   - DecisionRequester는 붙이지 말 것 (RequestDecision을 코드에서 직접 호출)
public class PlacementAgent : Agent
{
    [Header("그리드 최대 크기 (실제 그리드 ≤ 이 값이어야 함)")]
    public int gridW = 20;
    public int gridH = 16;
    private const int CHANNELS = 5;   // 설치가능-빈칸 / 경로 / 타워있음 / 경로진행도 / 경로근접도
    private const int SCALARS = 12;
    private const float PROX_REF = 5f; // 경로 근접도 정규화 기준(타일). 이 거리 이상이면 0

    // 경로 진행도/근접도 맵 (매 관측마다 재계산, 재사용해서 GC 최소화)
    private float[,] pathProg;
    private float[,] pathProx;

    [Header("연결 (비우면 자동으로 찾음)")]
    public GridManager grid;
    public GameManager gm;             // 정적 접근 보조 (실제로는 GameManager.Instance 사용)
    public GameStateCollector collector;
    public RoundManager roundManager;

    [Header("건설")]
    public GameObject[] towerPrefabs;      // BuildManager와 동일한 타워 종류들 (T개)
    public int maxActionsPerPrep = 12;     // 한 정비 단계에서 내릴 최대 결정 수

    [Header("보상 - 기본/체력")]
    public float surviveBase = 0.2f;      // 라운드 생존 기본 보상 (고스테이지 가중 적용)
    public float healthLossWeight = 3f;   // 체력 손실 벌 = 손실비율 × 이 값 (조금만 닳아도 큰 타격)
    public float deathPenalty = 1.5f;     // 라운드 패배(사망) 시 큰 벌
    public float clearBonus = 1f;         // 스테이지 클리어 시 +이만큼 (고스테이지 가중)
    public float illegalPenalty = 0.01f;  // 유효하지 않은 건설 시도 시 소량 -
    public float uselessTowerPenalty = 0.05f; // 사거리 여유 안에 경로가 없는 자리에 지으려 하면 즉시 벌 (짓지 않음) — 비용에 비례해 커짐
    public float rangeMargin = 1f;            // 사거리 판정 여유: 실제 사거리 n에서 (n − 이 값) 안에 경로가 없으면 벌 (경계 배치 방지)
    public float placementRefCost = 100f;      // 배치 벌·보상의 비용 스케일 기준 (그 타워 비용 ÷ 이 값 = 배율)
    public float placementValueWeight = 0.02f; // 제대로(사거리 안) 지었을 때 보상 (비용에 비례) — 작게 유지

    [Header("한 종류 과다 배치 억제 (비용과 무관 — 종류 다양성 유도)")]
    public int sameTypeSoftCap = 3;                // 이 개수까진 벌 없음
    public float overConcentrationPenalty = 0.03f; // soft cap 초과분 1개당 누적 벌

    [Header("보상 - 속도/타워활동/스테이지가중")]
    public float speedWeight = 0.15f;         // 라운드를 빨리 끝낼수록 + (스폰 완료 후 잔존 시간 기준)
    public float lingerRef = 8f;              // 스폰 완료 후 이 시간(초) 안에 필드가 비면 속도 만점
    public float towerActivityWeight = 0.12f; // '많은 타워가 실제로 일할수록' + (타워수 × 가동률)
    public float activeRefCount = 8f;         // 활동 타워 정규화 기준 (이만큼이면 만점)
    public float stageRewardWeight = 0.5f;    // 고스테이지 생존·클리어 보상 가중 (0=없음, 1=최대 2배)

    [HideInInspector] public bool lastActionWasNoOp = false;

    private int T => (towerPrefabs != null) ? towerPrefabs.Length : 0;

    public override void Initialize()
    {
        if (grid == null) grid = Object.FindFirstObjectByType<GridManager>();
        if (collector == null) collector = Object.FindFirstObjectByType<GameStateCollector>();
        if (roundManager == null) roundManager = Object.FindFirstObjectByType<RoundManager>();
        pathProg = new float[gridW, gridH];
        pathProx = new float[gridW, gridH];
        Debug.Log($"[배치AI] 기대 관측 수 = {gridW * gridH * CHANNELS + SCALARS} (Behavior Parameters의 Space Size와 같아야 함)");
    }

    // DDA가 에피소드를 주도하므로 여기서는 상태만 초기화 (StartEpisode 호출하지 않음)
    public override void OnEpisodeBegin()
    {
        lastActionWasNoOp = false;
    }

    // ── 관찰: 전체 그리드 3채널 + 스칼라 ──
    public override void CollectObservations(VectorSensor sensor)
    {
        RebuildPathMaps(); // 이번 판 경로의 진행도/근접도 맵 갱신

        for (int y = 0; y < gridH; y++)
        {
            for (int x = 0; x < gridW; x++)
            {
                Tile t = (grid != null) ? grid.GetTile(new Vector2Int(x, y)) : null;
                float buildableEmpty = (t != null && t.CanBuild()) ? 1f : 0f;
                float isPath = (t != null && t.type == TileType.Path) ? 1f : 0f;
                float towerHere = (t != null && t.type == TileType.Buildable && t.occupied) ? 1f : 0f;
                float prog = (t != null) ? pathProg[x, y] : 0f;   // 경로면 입구≈작은값 → 골=1, 비경로=0
                float prox = (t != null) ? pathProx[x, y] : 0f;   // 경로에 가까울수록 1
                sensor.AddObservation(buildableEmpty);
                sensor.AddObservation(isPath);
                sensor.AddObservation(towerHere);
                sensor.AddObservation(prog);
                sensor.AddObservation(prox);
            }
        }

        // 스칼라 (총 12개)
        GameState s = (collector != null) ? collector.Collect() : default;
        int gold = (GameManager.Instance != null) ? GameManager.Instance.gold : 0;
        int round = (roundManager != null) ? roundManager.CurrentRound : 0;
        int rounds = (roundManager != null) ? Mathf.Max(1, roundManager.RoundsPerStage) : 1;

        sensor.AddObservation(s.playerHealthRatio);              // 1
        sensor.AddObservation(Mathf.Clamp01(gold / 2000f));      // 2
        sensor.AddObservation(Mathf.Clamp01(s.stage / 10f));     // 3
        sensor.AddObservation(Mathf.Clamp01(round / (float)rounds)); // 4
        sensor.AddObservation(Mathf.Clamp01(s.towerCount / 30f)); // 5
        sensor.AddObservation(s.pathCoverage);                   // 6
        sensor.AddObservation(Mathf.Clamp01(s.pathLength / 100f));// 7
        // 종류별 타워 수 (최대 5종) — 8~12
        for (int i = 0; i < 5; i++)
        {
            float c = 0f;
            if (towerPrefabs != null && i < towerPrefabs.Length)
                c = CountTowersOfType(TypeName(i)) / 15f;
            sensor.AddObservation(Mathf.Clamp01(c));
        }
    }

    // ── 유효하지 않은 행동 마스킹 ──
    public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
    {
        GameManager g = GameManager.Instance;
        int gold = (g != null) ? g.gold : 0;

        // branch0: 0=NoOp 항상 허용
        // 건설(1..T): 자금 여유 + 설치 가능한 빈 타일 존재
        bool anyBuildable = AnyBuildableTile();
        for (int i = 0; i < T; i++)
        {
            int baseCost = towerPrefabs[i] != null ? PrefabCost(i) : int.MaxValue;
            int cost = (g != null) ? g.GetScaledCost(baseCost) : baseCost;
            bool ok = anyBuildable && cost <= gold;
            actionMask.SetActionEnabled(0, 1 + i, ok);
        }
        // 업그레이드(T+1..2T): 그 종류 타워 존재 + 최대레벨 아님 + 자금 여유
        var mgr = TowerUpgradeManager.Instance;
        for (int i = 0; i < T; i++)
        {
            string nm = TypeName(i);
            bool has = CountTowersOfType(nm) > 0;
            bool notMax = (mgr == null) || !mgr.IsMaxed(nm);
            bool afford = (mgr == null) || (g != null && gold >= mgr.NextCost(nm));
            actionMask.SetActionEnabled(0, 1 + T + i, has && notMax && afford);
        }

        // branch1(x): 실제 그리드 범위 밖 + 설치가능 타일 없는 열 차단
        int w = (grid != null) ? grid.width : gridW;
        int h = (grid != null) ? grid.height : gridH;
        bool anyX = false;
        for (int x = 0; x < gridW; x++)
        {
            bool ok = (x < w) && ColumnHasBuildable(x, h);
            actionMask.SetActionEnabled(1, x, ok);
            anyX |= ok;
        }
        if (!anyX) actionMask.SetActionEnabled(1, 0, true); // 브랜치당 최소 1개는 열어둬야 함

        bool anyY = false;
        for (int y = 0; y < gridH; y++)
        {
            bool ok = (y < h) && RowHasBuildable(y, w);
            actionMask.SetActionEnabled(2, y, ok);
            anyY |= ok;
        }
        if (!anyY) actionMask.SetActionEnabled(2, 0, true);
    }

    // ── 행동 실행 ──
    public override void OnActionReceived(ActionBuffers actions)
    {
        var d = actions.DiscreteActions;
        int what = d[0];
        int x = d[1];
        int y = d[2];

        lastActionWasNoOp = false;

        if (what == 0) { lastActionWasNoOp = true; return; } // 아무것도 안 함 → 정비 종료 신호

        if (what >= 1 && what <= T)               // 건설
        {
            TryBuild(what - 1, x, y);
        }
        else if (what >= T + 1 && what <= 2 * T)   // 업그레이드
        {
            string nm = TypeName(what - 1 - T);
            var mgr = TowerUpgradeManager.Instance;
            if (mgr != null) mgr.UpgradeType(nm);   // 실패해도 조용히 무시
        }
    }

    void TryBuild(int typeIdx, int x, int y)
    {
        GameManager g = GameManager.Instance;
        if (g == null || grid == null || towerPrefabs == null || typeIdx >= towerPrefabs.Length) return;

        Tile tile = grid.GetTile(new Vector2Int(x, y));

        // 비용 스케일: 비싼 타워일수록 벌·보상이 크다 (지원=싼 것은 둘 다 작음)
        float costNorm = Mathf.Max(0.1f, PrefabCost(typeIdx) / Mathf.Max(1f, placementRefCost));

        if (tile == null || !tile.CanBuild())
        {
            AddReward(-illegalPenalty);   // 경로/점유 등 물리적으로 불가한 자리 → 소량 벌(비용 무관)
            return;
        }

        GameObject prefab = towerPrefabs[typeIdx];
        Tower data = prefab.GetComponent<Tower>();
        float range = (data != null) ? data.maxRange : 3f;

        // 경계(딱 사거리)에 걸치면 실제론 발사가 잘 안 됨 → 여유(rangeMargin)를 빼서 판정한다.
        // (사거리 n) − (여유) 안에 경로가 없으면 = 사실상 못 때리는 자리 → 짓지 않고 즉시 벌 (비용에 비례).
        float effectiveRange = Mathf.Max(0.1f, range - rangeMargin);
        if (!CoversAnyPath(x, y, effectiveRange))
        {
            AddReward(-uselessTowerPenalty * costNorm);
            return;
        }

        int cost = g.GetScaledCost(PrefabCost(typeIdx));
        if (!g.CanAfford(cost)) return;

        // 짓기 전에: 새 경로를 덮는 자리인지 + 이미 같은 종류가 몇 개인지
        bool addsNew = AddsNewPathCoverage(tile, effectiveRange);
        int sameTypeCount = CountTowersOfType(TypeName(typeIdx));

        g.SpendGold(cost);
        GameObject go = Instantiate(prefab, tile.transform.position, Quaternion.identity);
        tile.occupied = true;
        g.RegisterTowerBuilt();
        if (TowerUpgradeManager.Instance != null)
            TowerUpgradeManager.Instance.ApplyCurrentLevel(go.GetComponent<Tower>());

        // 사거리 안 + '새 경로'를 덮을 때만 배치 보상 (이미 덮인 곳에 도배 시 0)
        if (addsNew)
            AddReward(placementValueWeight * costNorm);

        // 한 종류를 soft cap 넘게 쌓으면 초과분에 누적 벌 (비용과 무관, 종류 다양성 유도)
        if (sameTypeCount >= sameTypeSoftCap)
            AddReward(-overConcentrationPenalty * (sameTypeCount - sameTypeSoftCap + 1));
    }

    // 학습 없이 테스트할 때: 아무것도 안 함(NoOp)
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var d = actionsOut.DiscreteActions;
        d[0] = 0; d[1] = 0; d[2] = 0;
    }

    // ── RoundManager가 정비 단계에 호출: 여러 번 결정 → 타워 배치/업그레이드 ──
    public IEnumerator BuildPhase()
    {
        for (int i = 0; i < maxActionsPerPrep; i++)
        {
            lastActionWasNoOp = false;
            RequestDecision();
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            if (lastActionWasNoOp) yield break;   // 그만두겠다고 하면 정비 종료
        }
    }

    // ── 보상 (RoundManager가 호출) ──
    // lingerTime = 스폰 완료 후 소탕까지 걸린 시간(작을수록 빨리 끝냄). towerCount/utilization = 이번 라운드 타워 수·평균 가동률.
    public void GradeRoundSurvived(float lossFraction, float lingerTime, int towerCount, float utilization)
    {
        int stage = GameManager.stage;
        float stageNorm = Mathf.Clamp01((stage - 1) / 9f);
        float stageMul = 1f + stageRewardWeight * stageNorm; // 고스테이지일수록 살짝 큰 보상

        // (1) 생존 기본 (고스테이지 가중) − 체력 손실 큰 벌(비례, 조금만 닳아도 타격)
        float r = surviveBase * stageMul - healthLossWeight * lossFraction;

        // (2) 빠른 클리어: 스폰 완료 후 필드가 빨리 비면 +
        float speedScore = Mathf.Clamp01(1f - lingerTime / Mathf.Max(0.1f, lingerRef));
        r += speedWeight * speedScore;

        // (3) 많은 타워가 실제로 일할수록 + (타워수 × 가동률) → '적은 타워 + 잦은 업글' 습관 교정
        float active = towerCount * Mathf.Clamp01(utilization);
        r += towerActivityWeight * Mathf.Clamp01(active / Mathf.Max(1f, activeRefCount));

        AddReward(r);
    }

    public void GradeDied()
    {
        AddReward(-deathPenalty); // 라운드 패배 = 큰 벌
    }

    // 스테이지 클리어: 기본 보너스 + 고스테이지 가중 (조금 더 큰 보상)
    public void StageClearBonus()
    {
        int stage = GameManager.stage;
        float stageNorm = Mathf.Clamp01((stage - 1) / 9f);
        AddReward(clearBonus * (1f + stageRewardWeight * stageNorm));
    }

    // ── 헬퍼 ──
    string TypeName(int idx)
    {
        if (towerPrefabs == null || idx < 0 || idx >= towerPrefabs.Length || towerPrefabs[idx] == null) return "";
        Tower t = towerPrefabs[idx].GetComponent<Tower>();
        return (t != null && !string.IsNullOrEmpty(t.towerName)) ? t.towerName : towerPrefabs[idx].name;
    }

    int PrefabCost(int idx)
    {
        Tower t = towerPrefabs[idx].GetComponent<Tower>();
        return (t != null) ? t.cost : 0;
    }

    int CountTowersOfType(string nm)
    {
        int c = 0;
        foreach (Tower t in Object.FindObjectsByType<Tower>(FindObjectsSortMode.None))
            if (t != null && t.towerName == nm) c++;
        return c;
    }

    bool AnyBuildableTile()
    {
        if (grid == null) return false;
        for (int x = 0; x < grid.width; x++)
            for (int y = 0; y < grid.height; y++)
            {
                Tile t = grid.GetTile(new Vector2Int(x, y));
                if (t != null && t.CanBuild()) return true;
            }
        return false;
    }

    bool ColumnHasBuildable(int x, int h)
    {
        if (grid == null) return false;
        for (int y = 0; y < h; y++)
        {
            Tile t = grid.GetTile(new Vector2Int(x, y));
            if (t != null && t.CanBuild()) return true;
        }
        return false;
    }

    bool RowHasBuildable(int y, int w)
    {
        if (grid == null) return false;
        for (int x = 0; x < w; x++)
        {
            Tile t = grid.GetTile(new Vector2Int(x, y));
            if (t != null && t.CanBuild()) return true;
        }
        return false;
    }

    // (x,y)에서 사거리 range 안에 경로 타일이 하나라도 있는가? (그 자리에 지으면 경로를 때릴 수 있는가)
    bool CoversAnyPath(int x, int y, float range)
    {
        if (grid == null || grid.pathCoords == null) return false;
        float r2 = range * range;
        foreach (Vector2Int p in grid.pathCoords)
        {
            float dx = p.x - x, dy = p.y - y;
            if (dx * dx + dy * dy <= r2) return true;
        }
        return false;
    }

    // 이 자리에 지으면 '아직 어떤 타워도 못 덮던 경로 칸'을 새로 덮는가? (월드 좌표 기준)
    // 이미 덮인 경로에 겹쳐 지으면 false → 배치 보상 없음 (도배 파밍 방지)
    bool AddsNewPathCoverage(Tile tile, float myRange)
    {
        if (grid == null || tile == null) return false;
        var pathWorld = grid.GetPathWorldPoints();
        if (pathWorld == null || pathWorld.Count == 0) return false;

        Tower[] towers = Object.FindObjectsByType<Tower>(FindObjectsSortMode.None);
        Vector3 myPos = tile.transform.position; myPos.y = 0f;

        foreach (Vector3 pw in pathWorld)
        {
            Vector3 pp = pw; pp.y = 0f;
            if (Vector3.Distance(myPos, pp) > myRange) continue; // 내가 못 덮는 칸은 패스

            bool already = false;
            foreach (Tower t in towers)
            {
                if (t == null) continue;
                float te = Mathf.Max(0.1f, t.maxRange - rangeMargin); // 기존 타워의 유효 사거리
                Vector3 tp = t.transform.position; tp.y = 0f;
                if (Vector3.Distance(tp, pp) <= te) { already = true; break; }
            }
            if (!already) return true; // 아무도 못 덮던 칸을 새로 덮음
        }
        return false;
    }

    // 이번 판 경로 기준으로 진행도/근접도 맵을 다시 계산
    void RebuildPathMaps()
    {
        if (pathProg == null) pathProg = new float[gridW, gridH];
        if (pathProx == null) pathProx = new float[gridW, gridH];

        for (int x = 0; x < gridW; x++)
            for (int y = 0; y < gridH; y++) { pathProg[x, y] = 0f; pathProx[x, y] = 0f; }

        var pl = (grid != null) ? grid.pathCoords : null;
        int n = (pl != null) ? pl.Count : 0;
        if (n == 0) return;

        // 진행도: 경로 순서대로 (i+1)/n → 입구≈작은 양수, 골=1 (비경로는 0)
        for (int i = 0; i < n; i++)
        {
            Vector2Int c = pl[i];
            if (c.x >= 0 && c.x < gridW && c.y >= 0 && c.y < gridH)
                pathProg[c.x, c.y] = (i + 1) / (float)n;
        }

        // 근접도: 가장 가까운 경로 타일까지 거리 → 가까울수록 1 (PROX_REF 이상이면 0)
        for (int x = 0; x < gridW; x++)
            for (int y = 0; y < gridH; y++)
            {
                float best = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    float dx = pl[i].x - x, dy = pl[i].y - y;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d < best) best = d;
                }
                pathProx[x, y] = Mathf.Clamp01(1f - best / PROX_REF);
            }
    }
}
