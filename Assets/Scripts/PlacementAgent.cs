using System.Collections;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

// 최적 배치 AI (설계상 '역할 B: 플레이어 도우미 / 최적 타워 배치').
// - 관찰: 맵 그리드 8채널은 PlacementGridSensor(이미지 센서, CNN)가 별도로, 여기서는 스칼라 29개만
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
//   - Vector Observation > Space Size = **29** (스칼라만. 그리드는 PlacementGridSensor가 따로 냄)
//   - 같은 GameObject에 PlacementGridSensorComponent를 붙일 것
//   - placement.yaml: vis_encode_type: match3
//   - Actions > Discrete Branches = 3, 크기 = [1+2*타워종류수, GRID_W, GRID_H] = 기본 [11, 16, 12]
//   - Behavior Type = Default(학습) / Inference Only(고정 상대로 쓸 때)
//   - DecisionRequester는 붙이지 말 것 (RequestDecision을 코드에서 직접 호출)
public class PlacementAgent : Agent
{
    [Header("그리드 최대 크기 (실제 그리드 ≤ 이 값이어야 함)")]
    public int gridW = 16;
    public int gridH = 12;
    // v7: 맵 그리드 8채널은 이제 **PlacementGridSensor**(커스텀 이미지 센서)가 따로 내보낸다.
    //     여기(CollectObservations)에는 스칼라 29개만 남는다. Space Size = 29.
    // 12 → 17 (v5): 종류별 '업그레이드 레벨' 5개.  17 → 29 (v6): 직전 라운드 누수·손실 2개 +
    // 종류별 '현재 건설가' 5개 + 종류별 '업그레이드 비용' 5개.
    // 가격을 못 보면 "지금 이게 비싸다"는 판단을 할 근거가 없다(마스킹은 살 수 있냐만 막아줌).
    private const int SCALARS = 29;

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
    public float placementValueWeight = 0.06f; // 제대로 지었을 때 보상 (비용 × 새로 덮은 경로의 '가중치 합'에 비례)
    // v4: 이진 판정(사거리 안이면 전부 1칸) → **거리 가중**으로 변경.
    //     같은 칸 수를 덮어도 경로를 바짝 끼고 지은 자리가 멀찍이서 걸치기만 한 자리보다 높은 점수를 받는다.
    //     가까울수록 적이 사거리 안에 머무는 시간이 길고 투사체가 빗나갈 확률도 낮으므로 실제로 더 가치 있다.
    //     칸 가중치 = (1 − 거리/유효사거리) ^ coverageFalloffPower   (거리 0 → 1.0, 유효사거리 끝 → 0)
    public float coverageFalloffPower = 1f;    // 1=선형. 올릴수록 '바짝 붙이기'를 더 강하게 선호
    public float coverageRefWeight = 3f;       // 새로 덮은 가중치 합이 이만큼이면 배치 보상 1배 (사거리 3 기준)
    // v6: 기준을 그 타워의 사거리에 비례시킨다. 안 그러면 사거리가 길수록 무조건 유리해서
    //     직선 경로 기준 폭탄(R2) 0.022 vs 미사일(R7.5) 0.120으로 5.5배 격차가 났다.
    //     비례시키면 기준이 '얼마나 넓게 덮었나'가 아니라 '자기 사거리를 얼마나 잘 활용했나'가 되어 1.7배로 줄어든다.
    public float coverageRangeRef = 3f;        // 이 사거리를 가진 타워가 coverageRefWeight를 그대로 씀 (일반 타워 기준)
    public float coverageMaxMul = 2f;          // 배치 보상 배율 상한 (여러 경로가 겹치는 목 좋은 자리 = 최대 2배)

    [Header("업그레이드 보상")]
    public float upgradeValueWeight = 0.06f; // 업그레이드 성공 시 즉시 보상 (건설 보상과 같은 눈금)
    public float upgradeRefCount = 3f;       // 그 종류 타워가 이만큼 있으면 1배 (설계상 3개부터 업글이 이득)
    public float upgradeMaxMul = 2f;         // 배율 상한

    // ── 강화 이월 가치 (2026-07-30 신설) ─────────────────────────────────────
    // 문제: 학습은 '에피소드 = 스테이지 1개'라 에피소드가 스테이지 끝에서 잘린다.
    //   그런데 실제 게임에서 강화는 **남은 판 전체**에 남는 투자다.
    //   AI 입장에서는 "이번 스테이지 안에서 회수되지 않는 강화 = 손해"로 보이므로
    //   강화를 체계적으로 과소투자하도록 학습된다.
    //   증거(plc_v9 실측): 스테이지 종료 시 남은 골드가 S1 133 → S10 353골드로 증가.
    //   폭탄 강화가 300골드인데 353골드를 들고 판을 끝낸다. 살 이유가 없어 보이는 것이다.
    //
    // 처방: 잘린 지평의 **continuation value를 근사해서 종결 시점에 넣어준다.**
    //   편법이 아니라 truncated-horizon 문제의 표준 처방이다.
    //   남은 스테이지 수에 비례 → 초반에 투자할수록 더 오래 회수된다는 사실이 보상에 반영된다.
    //   S10에서는 남은 판이 없으므로 0 (후반 막판 강화 남발 방지).
    //
    // 보상 해킹 방어: **타워를 실제로 보유한 종류의 레벨만** 센다.
    //   그러지 않으면 시드로 꽂힌 미사일 레벨이 공짜 점수가 되고,
    //   싼 타워 1개만 세우고 강화만 사재는 전략이 열린다.
    public float upgradeCarryPerLevel = 0.15f; // 이월 레벨 1당 보상 (× 남은 스테이지 비율)
    public float upgradeCarryMax = 1.5f;       // 종결 이월 보상 상한

    [Header("지원 타워: 감속 가치 (DPS·가동률로는 안 잡히는 간접 효과)")]
    public float slowValueWeight = 0.12f;    // 이번 라운드에 감속을 건 적 비율 × 이 값

    [Header("한 종류 과다 배치 억제")]
    // 0으로 끔 (2026-07-26). 인위적 넛지인 데다, 업그레이드가 이득이 되는 지점이 정확히
    // '같은 종류 3개 이상'이라 sameTypeSoftCap(3) 벌과 정면충돌했다.
    // 진짜 종류 다양성은 DDA가 반례 적을 보내며 만들게 둔다.
    public int sameTypeSoftCap = 3;             // 이 개수까진 벌 없음
    public float overConcentrationPenalty = 0f; // soft cap 초과분 1개당 누적 벌 (0=끔)

    [Header("보상 - 속도/타워활동/스테이지가중")]
    public float speedWeight = 0.15f;         // 라운드를 빨리 끝낼수록 + (스폰 완료 후 잔존 시간 기준)
    public float lingerRef = 8f;              // 스폰 완료 후 이 시간(초) 안에 필드가 비면 속도 만점
    public float towerActivityWeight = 0.12f; // '많은 타워가 실제로 일할수록' + (타워수 × 가동률)
    public float activeRefCount = 8f;         // 활동 타워 정규화 기준 (이만큼이면 만점)
    // 0.5 → 1.5 (2026-07-26): 10스테이지 보상이 1스테이지의 1.5배뿐이라, 실제 난이도 상승폭
    // (적 수 ×2.8, 체력 ×3.7, DDA 상한도 더 높음)에 비해 보상 상승폭이 턱없이 작았다.
    // 그 결과 '쉬운 스테이지만 확실히 먹는' 정책이 기대값상 최적이 되어 S4 이상 클리어율이 2% 수준이었다.
    public float stageRewardWeight = 1.5f;    // 고스테이지 생존·클리어 보상 가중 (10스테이지에서 2.5배)

    [HideInInspector] public bool lastActionWasNoOp = false;

    private int T => (towerPrefabs != null) ? towerPrefabs.Length : 0;

    public override void Initialize()
    {
        if (grid == null) grid = Object.FindFirstObjectByType<GridManager>();
        if (collector == null) collector = Object.FindFirstObjectByType<GameStateCollector>();
        if (roundManager == null) roundManager = Object.FindFirstObjectByType<RoundManager>();
        Debug.Log($"[배치AI] 기대 Space Size = {SCALARS} (그리드는 PlacementGridSensor가 따로 내보냄)");
    }

    // DDA가 에피소드를 주도하므로 여기서는 상태만 초기화 (StartEpisode 호출하지 않음)
    public override void OnEpisodeBegin()
    {
        lastActionWasNoOp = false;
    }

    // ── 관찰: 스칼라 29개만. 맵 그리드 8채널은 PlacementGridSensor가 이미지로 따로 내보낸다 ──
    public override void CollectObservations(VectorSensor sensor)
    {
        // 스칼라 (총 29개)
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
        // 종류별 업그레이드 레벨 (0~maxLevel 정규화) — 13~17
        var upm = TowerUpgradeManager.Instance;
        int maxLv = (upm != null) ? Mathf.Max(1, upm.maxLevel) : 5;
        for (int i = 0; i < 5; i++)
        {
            float lv = 0f;
            if (upm != null && towerPrefabs != null && i < towerPrefabs.Length)
                lv = upm.GetLevel(TypeName(i)) / (float)maxLv;
            sensor.AddObservation(Mathf.Clamp01(lv));
        }

        // 직전 라운드 결과 — 18~19.
        // 체력비율은 누적값이라 "지난 라운드가 아슬아슬했는지"를 알 수 없다.
        // 5라운드에 체력 0.6인 상황이 '직전에 왕창 깎인 것'인지 '꾸준히 조금씩'인지 구분이 안 됐다.
        sensor.AddObservation(Mathf.Clamp01(s.lastRoundLeaked / 20f));
        sensor.AddObservation(Mathf.Clamp01(s.lastRoundHealthLost / 50f));

        // 종류별 현재 건설가 — 20~24. GetScaledCost는 지을수록 +15%씩 오르는데
        // 지금까지는 골드만 보이고 '가격'은 안 보여서, 비싸다는 판단 자체를 할 수 없었다.
        GameManager gm2 = GameManager.Instance;
        for (int i = 0; i < 5; i++)
        {
            float c = 0f;
            if (gm2 != null && towerPrefabs != null && i < towerPrefabs.Length && towerPrefabs[i] != null)
                c = gm2.GetScaledCost(PrefabCost(i), TypeName(i)) / 500f;
            sensor.AddObservation(Mathf.Clamp01(c));
        }

        // 종류별 업그레이드 비용 — 25~29. 업그레이드를 실제로 쓰기 시작했으니 가격 비교가 가능해야 한다.
        // 최대 레벨이면 1.0(= 가장 비쌈/불가)을 넣는다. 0을 넣으면 '공짜'로 읽혀 의미가 뒤집힌다.
        for (int i = 0; i < 5; i++)
        {
            float c = 1f;
            if (upm != null && towerPrefabs != null && i < towerPrefabs.Length)
            {
                string nm = TypeName(i);
                if (!string.IsNullOrEmpty(nm) && !upm.IsMaxed(nm)) c = upm.NextCost(nm) / 1000f;
            }
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
            int cost = (g != null) ? g.GetScaledCost(baseCost, TypeName(i)) : baseCost;
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
            if (mgr != null && mgr.UpgradeType(nm))
            {
                // v5: 업그레이드에도 즉시 보상. 예전엔 건설만 보상을 받고 업그레이드는 0이라,
                // 같은 골드를 써도 건설이 항상 유리해서 업그레이드 행동이 통째로 죽어 있었다.
                // 그 종류 타워가 많을수록 실제 이득이 크므로 보유 수에 비례시킨다.
                int cnt = CountTowersOfType(nm);
                float mul = Mathf.Min(upgradeMaxMul, cnt / Mathf.Max(1f, upgradeRefCount));
                AddReward(upgradeValueWeight * mul);
            }
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
        // 커버리지 계산용 사거리(= min(실제 타격 사거리, coverageRangeCap)). 미사일 999가 새지 않게.
        float range = (data != null) ? data.CoverageRange : 3.5f;

        // 판정 여유(rangeMargin)를 빼서, '애매하게 걸치는 칸'이 아니라 '온전히 때릴 수 있는 칸'만 인정한다.
        // 그런 칸이 하나도 없으면 = 사실상 쓸모없는 자리 → 짓지 않고 즉시 벌 (비용에 비례).
        float effectiveRange = Mathf.Max(0.1f, range - rangeMargin);
        if (!CoversAnyPath(x, y, effectiveRange))
        {
            AddReward(-uselessTowerPenalty * costNorm);
            return;
        }

        int cost = g.GetScaledCost(PrefabCost(typeIdx), TypeName(typeIdx));
        if (!g.CanAfford(cost)) return;

        // 짓기 전에: '아무도 못 덮던 경로'를 얼마나 새로 덮는지(거리 가중 합) + 이미 같은 종류가 몇 개인지
        float newCovered = WeightedNewPathCoverage(tile, effectiveRange);
        int sameTypeCount = CountTowersOfType(TypeName(typeIdx));

        g.SpendGold(cost);
        GameObject go = Instantiate(prefab, tile.transform.position, Quaternion.identity);
        tile.occupied = true;
        g.RegisterTowerBuilt(TypeName(typeIdx));
        if (TowerUpgradeManager.Instance != null)
            TowerUpgradeManager.Instance.ApplyCurrentLevel(go.GetComponent<Tower>());

        // 배치 보상 = 비용배율 × (새로 덮은 경로의 거리가중 합 / 기준). 이미 덮인 곳에 도배하면 0.
        // → ① 경로가 접히거나 여러 구간이 몰린 '목 좋은 자리'가 한 번에 여러 칸을 덮어 크게 받고
        //    ② 같은 칸 수라도 경로를 바짝 끼고 지은 자리가 멀리서 걸친 자리보다 크게 받는다.
        // ※ 여기엔 costNorm을 곱하지 않는다. 벌(잘못 지으면 손해가 큼)에는 비용 비례가 타당하지만
        //   보상에 비용을 곱하면 '비싸고 사거리 넓은 타워를 짓는 것' 자체가 이득이 되어
        //   지원(costNorm 1.6, DPS 0.33)·미사일(costNorm 3.0) 파밍 경사가 생긴다.
        if (newCovered > 0f)
        {
            // 기준 가중치를 그 타워의 사거리에 비례시켜, 사거리가 길다는 이유만으로 유리해지지 않게 한다
            float refW = coverageRefWeight * (effectiveRange / Mathf.Max(0.1f, coverageRangeRef));
            float covMul = Mathf.Min(coverageMaxMul, newCovered / Mathf.Max(0.1f, refW));
            AddReward(placementValueWeight * covMul);
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
    public void GradeRoundSurvived(float lossFraction, float lingerTime, int towerCount, float utilization,
                                   int slowApplied = 0, int enemiesThisRound = 0)
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

        // (3-b) 감속: 이번 라운드에 실제로 감속을 걸어본 적의 비율.
        //       지원 타워는 DPS 0.33이라 위 항목들로는 가치가 전혀 안 잡혀서, 배치 AI에게
        //       '돈만 쓰고 보상은 없는 선택지'였다. 이 항목이 있어야 학습 가능한 카드가 된다.
        if (enemiesThisRound > 0)
            r += slowValueWeight * Mathf.Clamp01(slowApplied / (float)enemiesThisRound);

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

        // 강화 이월 가치 — 클리어했을 때만 준다. 죽으면 이어질 판이 없으니 이월 가치도 없다.
        AddReward(UpgradeCarryReward(stage, stageNorm));
    }

    // 보유 강화의 '남은 판에 대한' 가치. 타워를 실제로 가진 종류만 계산에 넣는다.
    float UpgradeCarryReward(int stage, float stageNorm)
    {
        var upm = TowerUpgradeManager.Instance;
        if (upm == null || upgradeCarryPerLevel <= 0f) return 0f;

        float remain = 1f - stageNorm;          // S1 = 1.0 (9판 남음) … S10 = 0 (남은 판 없음)
        if (remain <= 0f) return 0f;

        int levels = 0;
        for (int i = 0; i < 5; i++)
        {
            string nm = TypeName(i);
            if (string.IsNullOrEmpty(nm)) continue;
            if (CountTowersOfType(nm) <= 0) continue;   // 안 쓰는 종류의 레벨은 가치 없음
            levels += upm.GetLevel(nm);
        }
        if (levels <= 0) return 0f;

        return Mathf.Min(upgradeCarryMax, upgradeCarryPerLevel * remain * levels);
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

    // 이 자리에 지으면 '아직 어떤 타워도 못 덮던 경로'를 얼마나 새로 덮는가? (월드 좌표 기준)
    // 칸 수를 그냥 세지 않고 **거리로 가중**한다: 가까운 경로 칸일수록 1.0에 가깝고, 유효사거리 끝은 0.
    //   → 같은 칸 수를 덮어도 '바짝 끼고 지은 자리'가 '멀리서 걸치기만 한 자리'보다 높은 점수를 받는다.
    // 이미 덮인 경로에 겹쳐 지으면 0 → 배치 보상 없음 (도배 파밍 방지).
    // 뱀형 경로가 접히는 자리는 한 타워가 여러 구간을 동시에 덮어 이 값이 크게 나온다.
    float WeightedNewPathCoverage(Tile tile, float myRange)
    {
        if (grid == null || tile == null) return 0f;
        var pathWorld = grid.GetPathWorldPoints();
        if (pathWorld == null || pathWorld.Count == 0) return 0f;

        Tower[] towers = Object.FindObjectsByType<Tower>(FindObjectsSortMode.None);
        Vector3 myPos = tile.transform.position; myPos.y = 0f;

        float sum = 0f;
        foreach (Vector3 pw in pathWorld)
        {
            Vector3 pp = pw; pp.y = 0f;
            float d = Vector3.Distance(myPos, pp);
            if (d > myRange) continue; // 내가 못 덮는 칸은 패스

            bool already = false;
            foreach (Tower t in towers)
            {
                if (t == null) continue;
                float te = Mathf.Max(0.1f, t.CoverageRange - rangeMargin); // 기존 타워의 커버리지 사거리
                Vector3 tp = t.transform.position; tp.y = 0f;
                if (Vector3.Distance(tp, pp) <= te) { already = true; break; }
            }
            if (already) continue;

            // 거리 가중: 거리 0 → 1.0, 유효사거리 끝 → 0
            sum += Mathf.Pow(Mathf.Clamp01(1f - d / myRange), coverageFalloffPower);
        }
        return sum;
    }
}
