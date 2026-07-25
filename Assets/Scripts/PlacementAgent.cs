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
// - 보상(v3): 라운드를 넘기면 항상 + (피해가 적을수록 더 큼), 죽으면 큰 - (일찍 포기할수록 더 큼),
//             스테이지 클리어 시 +보너스. DDA와 반대 목표.
//
// 에피소드 루프는 DifficultyAgent(DDA)가 주도한다. 이 에이전트는 '정비 단계'에
// RoundManager가 BuildPhase()를 돌려 여러 번 결정을 내리게 하고, 라운드 결과로 보상만 받는다.
//
// 인스펙터(Behavior Parameters) 설정:
//   - Behavior Name = Placement
//   - Vector Observation > Space Size = GRID_W*GRID_H*7 + 12  (기본 20*16*7+12 = 2252)
//   - Actions > Discrete Branches = 3, 크기 = [1+2*타워종류수, GRID_W, GRID_H] = 기본 [11, 20, 16]
//   - Behavior Type = Default(학습) / Inference Only(고정 상대로 쓸 때)
//   - DecisionRequester는 붙이지 말 것 (RequestDecision을 코드에서 직접 호출)
public class PlacementAgent : Agent
{
    [Header("그리드 최대 크기 (실제 그리드 ≤ 이 값이어야 함)")]
    public int gridW = 20;
    public int gridH = 16;
    // 채널: 설치가능-빈칸 / 경로 / 타워있음 / 경로진행도 / 경로근접도 / 경로밀집도 / 적밀도
    private const int CHANNELS = 7;
    private const int SCALARS = 12;
    private const float PROX_REF = 5f; // 경로 근접도 정규화 기준(타일). 이 거리 이상이면 0

    [Header("경로 밀집도 관측 (여러 경로 구간을 한 번에 덮는 자리를 알아보게)")]
    // '경로에 붙어 있으면 좋다'(근접도)만으로는 뱀형 경로가 접히는 목 좋은 자리를 구분 못 한다.
    // 이 채널은 각 칸에서 densityRefRange 안에 경로 칸이 몇 개나 들어오는지를 알려준다.
    public float densityRefRange = 3f;   // 밀집도 판정 반경(칸) — 일반 타워 사거리 기준
    public float densityRefTiles = 10f;  // 이만큼 덮이면 1.0
    public float enemyDensRefCount = 3f; // 한 칸에 적이 이만큼 겹치면 1.0

    // 그리드 맵들 (매 관측마다 재계산, 배열 재사용으로 GC 최소화)
    private float[,] pathProg;
    private float[,] pathProx;
    private float[,] pathDens;
    private float[,] enemyDens;

    [Header("연결 (비우면 자동으로 찾음)")]
    public GridManager grid;
    public GameManager gm;             // 정적 접근 보조 (실제로는 GameManager.Instance 사용)
    public GameStateCollector collector;
    public RoundManager roundManager;

    [Header("건설")]
    public GameObject[] towerPrefabs;      // BuildManager와 동일한 타워 종류들 (T개)
    public int maxActionsPerPrep = 12;     // 한 정비 단계에서 내릴 최대 결정 수

    [Header("보상 - 기본/체력")]
    // ⚠️ 핵심 원칙 (v3, 2026-07-25): 라운드를 살아서 넘기는 것은 어떤 상황에서도 죽는 것보다 나아야 한다.
    //    v2에서는 손실 15%만 넘으면 라운드 보상이 음수가 됐고, 죽음은 -1 '한 번'에 에피소드가 끝나서
    //    "어차피 못 깰 판이면 투자 안 하고 빨리 지는 것"이 최적 정책이 되어버렸다.
    public float surviveBase = 0.5f;      // 라운드 생존 기본 보상 (고스테이지 가중 적용)
    public float healthLossWeight = 1.5f; // 체력 손실 벌 = 손실비율 × 이 값
    public float minRoundReward = 0.05f;  // 라운드 생존 보상의 하한 — 아무리 두들겨 맞아도 넘기면 이득
    public float deathPenalty = 1.5f;     // 라운드 패배(사망) 시 기본 벌
    public float giveUpWeight = 1f;       // 남은 라운드가 많을수록 사망 벌 가중 (1라운드 포기 = -3.0, 마지막 라운드 = -1.5)
    public float clearBonus = 2f;         // 스테이지 클리어 시 +이만큼 (고스테이지 가중)
    public float illegalPenalty = 0.003f; // 유효하지 않은 건설 시도 시 소량 - (액션 마스킹이 이미 거르므로 아주 작게)
    public float uselessTowerPenalty = 0.02f; // 사거리 여유 안에 경로가 없는 자리에 지으려 하면 즉시 벌 (짓지 않음) — 비용에 비례해 커짐
    public float rangeMargin = 0.5f;          // 판정 여유. 실제 사거리 = maxRange+0.5 이므로 0.5면 '중심이 n칸 안'
                                              // = 그 칸을 온전히 때릴 수 있는 칸만 인정하게 된다.
    public float placementRefCost = 100f;      // 배치 벌·보상의 비용 스케일 기준 (그 타워 비용 ÷ 이 값 = 배율)
    public float placementValueWeight = 0.06f; // 제대로 지었을 때 보상 (비용 × 새로 덮은 경로 칸 수에 비례)
    public float coverageRefTiles = 5f;        // 새로 덮은 경로 칸이 이만큼이면 배치 보상 1배
    public float coverageMaxMul = 2f;          // 배치 보상 배율 상한 (여러 경로가 겹치는 목 좋은 자리 = 최대 2배)

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
        pathDens = new float[gridW, gridH];
        enemyDens = new float[gridW, gridH];
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
                float dens = (t != null) ? pathDens[x, y] : 0f;   // 반경 안에 경로 칸이 많을수록 1 (목 좋은 자리)
                float edns = (t != null) ? enemyDens[x, y] : 0f;  // 지금 이 근처에 적이 많을수록 1
                sensor.AddObservation(buildableEmpty);
                sensor.AddObservation(isPath);
                sensor.AddObservation(towerHere);
                sensor.AddObservation(prog);
                sensor.AddObservation(prox);
                sensor.AddObservation(dens);
                sensor.AddObservation(edns);
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
        float range = (data != null) ? data.AttackRange : 3.5f;   // 실제 타격 사거리(= maxRange + 0.5)

        // 판정 여유(rangeMargin)를 빼서, '애매하게 걸치는 칸'이 아니라 '온전히 때릴 수 있는 칸'만 인정한다.
        // 그런 칸이 하나도 없으면 = 사실상 쓸모없는 자리 → 짓지 않고 즉시 벌 (비용에 비례).
        float effectiveRange = Mathf.Max(0.1f, range - rangeMargin);
        if (!CoversAnyPath(x, y, effectiveRange))
        {
            AddReward(-uselessTowerPenalty * costNorm);
            return;
        }

        int cost = g.GetScaledCost(PrefabCost(typeIdx));
        if (!g.CanAfford(cost)) return;

        // 짓기 전에: '아무도 못 덮던 경로 칸'을 몇 개나 새로 덮는지 + 이미 같은 종류가 몇 개인지
        int newCovered = CountNewPathCoverage(tile, effectiveRange);
        int sameTypeCount = CountTowersOfType(TypeName(typeIdx));

        g.SpendGold(cost);
        GameObject go = Instantiate(prefab, tile.transform.position, Quaternion.identity);
        tile.occupied = true;
        g.RegisterTowerBuilt();
        if (TowerUpgradeManager.Instance != null)
            TowerUpgradeManager.Instance.ApplyCurrentLevel(go.GetComponent<Tower>());

        // 배치 보상 = 비용배율 × (새로 덮은 경로 칸 수 / 기준). 이미 덮인 곳에 도배하면 0.
        // → 경로가 접히거나 여러 구간이 몰린 '목 좋은 자리'가 한 번에 여러 칸을 덮어 더 큰 보상을 받는다.
        //   (단순히 경로에 붙어 있기만 한 자리는 1~2칸이라 보상이 작다)
        if (newCovered > 0)
        {
            float covMul = Mathf.Min(coverageMaxMul, newCovered / Mathf.Max(1f, coverageRefTiles));
            AddReward(placementValueWeight * costNorm * covMul);
        }

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

        // (4) 하한 보장 — 라운드를 넘겼다는 사실 자체는 항상 +.
        //     이게 없으면 후반(DDA 목표손실 14%) 라운드가 전부 마이너스가 되어
        //     '빨리 죽어서 에피소드를 끝내기'가 최적 정책이 된다.
        r = Mathf.Max(minRoundReward, r);

        AddReward(r);
    }

    // 라운드 패배 = 큰 벌. 남은 라운드가 많을수록(=일찍 포기할수록) 더 아프게 한다.
    // 1라운드에 포기 = -deathPenalty×2, 마지막 라운드에서 사망 = -deathPenalty×1.
    public void GradeDied()
    {
        int rounds = (roundManager != null) ? Mathf.Max(1, roundManager.RoundsPerStage) : 1;
        int cur = (roundManager != null) ? roundManager.CurrentRound : 0;
        float remainFrac = (rounds > 1) ? Mathf.Clamp01((rounds - 1 - cur) / (float)(rounds - 1)) : 0f;
        float p = deathPenalty * (1f + giveUpWeight * remainFrac);
        AddReward(-p);
        Debug.Log($"[배치AI:사망] R{cur + 1}/{rounds} 남은비율 {remainFrac:0.00} → {-p:0.00}");
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

    // 이 자리에 지으면 '아직 어떤 타워도 못 덮던 경로 칸'을 몇 개나 새로 덮는가? (월드 좌표 기준)
    // 이미 덮인 경로에 겹쳐 지으면 0 → 배치 보상 없음 (도배 파밍 방지).
    // 뱀형 경로가 접히는 자리는 한 타워가 여러 구간을 동시에 덮어 이 값이 크게 나온다.
    int CountNewPathCoverage(Tile tile, float myRange)
    {
        if (grid == null || tile == null) return 0;
        var pathWorld = grid.GetPathWorldPoints();
        if (pathWorld == null || pathWorld.Count == 0) return 0;

        Tower[] towers = Object.FindObjectsByType<Tower>(FindObjectsSortMode.None);
        Vector3 myPos = tile.transform.position; myPos.y = 0f;

        int count = 0;
        foreach (Vector3 pw in pathWorld)
        {
            Vector3 pp = pw; pp.y = 0f;
            if (Vector3.Distance(myPos, pp) > myRange) continue; // 내가 못 덮는 칸은 패스

            bool already = false;
            foreach (Tower t in towers)
            {
                if (t == null) continue;
                float te = Mathf.Max(0.1f, t.AttackRange - rangeMargin); // 기존 타워의 유효 사거리
                Vector3 tp = t.transform.position; tp.y = 0f;
                if (Vector3.Distance(tp, pp) <= te) { already = true; break; }
            }
            if (!already) count++; // 아무도 못 덮던 칸을 새로 덮음
        }
        return count;
    }

    // 이번 판 경로 기준으로 진행도/근접도 맵을 다시 계산
    void RebuildPathMaps()
    {
        if (pathProg == null) pathProg = new float[gridW, gridH];
        if (pathProx == null) pathProx = new float[gridW, gridH];
        if (pathDens == null) pathDens = new float[gridW, gridH];
        if (enemyDens == null) enemyDens = new float[gridW, gridH];

        for (int x = 0; x < gridW; x++)
            for (int y = 0; y < gridH; y++)
            { pathProg[x, y] = 0f; pathProx[x, y] = 0f; pathDens[x, y] = 0f; enemyDens[x, y] = 0f; }

        RebuildEnemyDensity();

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

        // 근접도(가장 가까운 경로까지) + 밀집도(반경 안 경로 칸 수)를 한 번에 계산
        float dr2 = densityRefRange * densityRefRange;
        for (int x = 0; x < gridW; x++)
            for (int y = 0; y < gridH; y++)
            {
                float best = float.MaxValue;
                int within = 0;
                for (int i = 0; i < n; i++)
                {
                    float dx = pl[i].x - x, dy = pl[i].y - y;
                    float sq = dx * dx + dy * dy;
                    if (sq <= dr2) within++;              // 밀집도: 반경 안 경로 칸 수
                    float d = Mathf.Sqrt(sq);
                    if (d < best) best = d;
                }
                pathProx[x, y] = Mathf.Clamp01(1f - best / PROX_REF);
                pathDens[x, y] = Mathf.Clamp01(within / Mathf.Max(1f, densityRefTiles));
            }
    }

    // 지금 필드에 있는 적들을 타일 격자로 집계 (전투 중 판단용 관측)
    void RebuildEnemyDensity()
    {
        if (grid == null) return;
        Enemy[] all = Object.FindObjectsByType<Enemy>(FindObjectsSortMode.None);
        if (all.Length == 0) return;
        foreach (Enemy e in all)
        {
            if (e == null) continue;
            Vector2Int c = grid.WorldToGrid(e.transform.position);
            if (c.x < 0 || c.x >= gridW || c.y < 0 || c.y >= gridH) continue;
            enemyDens[c.x, c.y] = Mathf.Clamp01(enemyDens[c.x, c.y] + 1f / enemyDensRefCount);
        }
    }
}
