using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// 한 라운드(= 단일 웨이브)의 구성 데이터
[System.Serializable]
public class RoundData
{
    public float spawnInterval = 1f;
    public float baseHealth = 10f;
    public float baseSpeed = 2f;
    public List<int> typeCounts = new List<int>();
}

// 스테이지 진행 + 강화학습 에이전트 연동.
// 에이전트가 매 라운드 난이도를 정하고(에이전트의 행동 배율), 라운드 결과로 보상을 받는다.
// 에피소드(= 스테이지 한 판)는 에이전트의 OnEpisodeBegin이 StartEpisode()로 시작한다.
public class RoundManager : MonoBehaviour
{
    [Header("연결 (비우면 자동으로 찾음)")]
    public GridManager grid;
    public GameStateCollector collector;
    public DifficultyAgent agent;
    public AutoPlayerBot bot;   // 학습용 봇 플레이어 (없으면 사람이 플레이)
    public PlacementAgent placementAgent; // 최적 배치 AI (usePlacementAgent가 true면 봇 대신 이걸로 배치)

    [Header("적 종류 (비우면 일반/빠름/탱커 자동 생성)")]
    public List<EnemyType> enemyTypes = new List<EnemyType>();

    [Header("보상 (처치 골드) — 적 체력이 아니라 '위협도(종류 비용)' 기준")]
    public float killRewardBase = 5f;          // 종류 비용 1당 기본 처치 골드
    public float killRewardStageGrowth = 0.1f; // 스테이지가 1 오를 때마다 처치 골드 +이 비율
    public int roundGoldStart = 20;            // 1라운드 시작 수입 (여기서 roundBaseGold까지 점점 증가)
    public int roundBaseGold = 60;             // lateRoundStart 라운드(=5라운드)에서 도달하는 수입
    public int lateRoundStart = 5;             // 이 라운드(0-based)부터 추가 수입 시작 (5 = 6라운드부터)
    public int lateRoundGold = 25;             // lateRoundStart 이후 라운드마다 누적 추가 (지날수록 +, 짓기·강화 재미)

    [Header("설정")]
    public int roundsPerStage = 10;
    public float prepTime = 5f;
    public float enemyHeight = 0.6f;
    public bool randomizePathEachEpisode = true; // 에피소드마다 경로를 무작위로 (학습 일반화)

    [Header("스테이지별 기본 스케일 (DDA가 아무것도 안 했을 때의 난이도)")]
    // 0.2 / 0.3 → 0.12 / 0.12 (2026-07-28).
    // 측정 결과 강도 1.0(하한)에서도 실제 손실이 보상 목표를 모든 스테이지에서 초과하고 있었다.
    //   전체 평균 실제 0.118 vs 목표 0.078, S10은 0.408 vs 0.084.
    // 즉 DDA는 소극적인 게 아니라 **내릴 수가 없어서 하한에 붙어 있었다.**
    // 원인: 스테이지가 오를 때 위협은 ×10.3(적 수 2.8 × 체력 3.7)인데
    //       플레이어 구매력은 ×1.6(수입 3.35 ÷ 타워가격 2.08)에 그쳐 6.4배 격차가 났다.
    // 원칙: **기본 스케일은 DDA의 시작점이므로 가장 쉬운 목표(easyTargetLoss)보다 아래여야 한다.**
    //       그래야 DDA에게 올릴 여지가 생긴다.
    public float stageCountGrowth = 0.12f;  // 스테이지 1당 적 수 배율 증가
    public float stageHealthGrowth = 0.12f; // 스테이지 1당 적 체력 배율 증가

    [Header("라운드별 기본 위협 (스테이지 1의 절대 난이도)")]
    // (6 + 3i) / (14 + 4i) → (4 + 2i) / (10 + 3i) (2026-07-28).
    // S1은 스테이지 배율이 1.0이라 위 stageGrowth 완화가 전혀 적용되지 않는다.
    // 그런데 S1 실측 손실이 라운드당 0.070(10라운드 누적 70%)으로, GDD의 "1~3스테이지는
    // 체력 70% 이상 남기고 클리어"와 정반대였다. 위협 총량을 약 절반으로 낮춘다.
    //
    // 2026-07-29 — 라운드 내 경사 완화: 2.0 / 3.0 → 1.4 / 2.0
    // dda_v6 실측에서 DDA가 전 스테이지 강도 1.00(하한)에 붙어 있었다. 소극적이어서가 아니라,
    // 스테이지 안의 라운드 경사가 너무 가팔라 **올릴 수가 없었기 때문**이다.
    //   S8: R1 손실 0.047 → R10 0.244 (사망 715/4768)
    //   S9: R1 손실 0.046 → R10 0.264 (사망 764/4592)
    // 강도는 단조 증가라 한 번 올리면 못 내린다. 앞 라운드에서 목표 손실(0.05)을 맞추려고 올리면
    // R10에서 확정 사망한다 → '바닥에 붙어 있기'가 최적 정책이 되어버렸다.
    // 기본 경사를 낮추는 것은 게임을 쉽게 만들려는 게 아니라,
    // **'라운드가 갈수록 어려워지는 곡선'을 만들 권한을 DDA에게 넘기는 것**이다.
    public float roundBudgetBase = 4f;      // 1라운드 위협 예산
    public float roundBudgetGrowth = 1.4f;  // 라운드당 예산 증가
    public float roundHealthBase = 10f;     // 1라운드 적 기본 체력
    public float roundHealthGrowth = 2f;    // 라운드당 체력 증가

    [Header("업그레이드 유지 (2026-07-29 신설)")]
    // 문제: 강화 87,554회 전수 집계에서 폭탄 83.5% / 일반 16.4% / 속사 0.1% / 지원 0.05% / 미사일 0%,
    //       도달 레벨은 Lv1 63% · Lv2 31% · Lv3 이상 5.7%뿐이었다.
    //       스테이지마다 레벨이 리셋되니 '고레벨까지 굴릴 시간' 자체가 없어서
    //       회수가 빠른 싼 타워만 한 번 찍고 끝나는 죽은 시스템이었다.
    // 더 큰 문제: 적 총 체력은 S1→S10에 4.33배 커지는데 배치 AI 총 DPS는 1.87배에 그쳐
    //       플레이어에게 성장 레버가 아예 없었다. S10 절벽(클리어율 0.3%)의 직접 원인.
    public bool keepUpgradesAcrossStages = true; // 플레이용: 스테이지가 넘어가도 종류별 강화 레벨 유지
    // 학습용 시드. 학습은 '에피소드 = 랜덤 스테이지 1개'라 유지만 켜면 배치 AI가 그 가치를 영영 못 배운다.
    // 에피소드 시작 시 스테이지에 비례한 레벨을 '무작위 종류'에 뿌려주면,
    // 이미 관측 중인 upgradeLevelByType(관측 13~17번)을 읽고
    // "이미 강화된 종류를 짓는다"는 전략까지 부수적으로 학습한다.
    public float upgradeSeedPerStage = 0.7f;     // 스테이지 1당 시드 레벨 (S10 → 약 6레벨이 5종류에 분산)

    [Header("스테이지")]
    public bool randomizeStageForTraining = true; // 학습용: 에피소드마다 스테이지 무작위(전 구간 학습)
    public int maxStageForTraining = 10;
    public bool advanceStageOnClear = false;      // 플레이용: 클리어 시 스테이지 +1

    [Header("최적 배치 AI")]
    public bool usePlacementAgent = false; // true면 정비 단계에 봇 대신 PlacementAgent가 타워 배치

    [Header("전투 중 건설 (사람 플레이어는 원래 가능 — 배치 AI에게도 같은 권한)")]
    public bool allowBuildDuringCombat = true;
    public float combatDecisionInterval = 1.5f; // 전투 중 배치 AI에게 결정을 묻는 간격(초)
    public int maxActionsPerCombatTick = 2;     // 한 틱에 내릴 수 있는 최대 결정 수

    private enum State { Prep, Spawning, Fighting, StageClear, StageFail }
    private State state = State.Prep;
    private int currentRound = 0;

    // 배치 에이전트 관찰용 접근자
    public int CurrentRound => currentRound;
    public int RoundsPerStage => roundsPerStage;
    private int aliveEnemies = 0;
    private float prepRemaining = 0f;
    private int healthAtRoundStart = 0;
    private int leakedThisRound = 0;
    private int enemiesThisRound = 0;   // 이번 라운드에 스폰된 적 수 (감속 비율 계산의 분모)
    private int maxAliveThisRound = 0;  // 라운드 중 최대 동시 생존 적 수 (DDA 관측용)
    private float roundStartTime = 0f;
    private float roundSpawnEndTime = 0f; // 이번 라운드 스폰이 끝난 시각 (빠른 클리어 보상용)

    void Start()
    {
        if (grid == null) grid = Object.FindFirstObjectByType<GridManager>();
        if (collector == null) collector = Object.FindFirstObjectByType<GameStateCollector>();
        if (agent == null) agent = Object.FindFirstObjectByType<DifficultyAgent>();
        if (bot == null) bot = Object.FindFirstObjectByType<AutoPlayerBot>();
        if (placementAgent == null) placementAgent = Object.FindFirstObjectByType<PlacementAgent>();

        if (enemyTypes.Count == 0) GenerateDefaultTypes();

        // 에이전트가 있으면 그쪽 OnEpisodeBegin이 StartEpisode를 호출한다.
        // 에이전트가 없으면(배선 전) 게임이 그냥 돌아가도록 한 번 시작해 준다.
        if (agent == null)
            StartEpisode();
    }

    // ── 에피소드(스테이지 한 판) 시작 ─────────────────────────
    public void StartEpisode()
    {
        StopAllCoroutines();
        ResetStage();
        StartCoroutine(RunEpisode());
    }

    // 게임 상태를 깨끗이 초기화 (씬 리로드 없이 제자리 리셋 → 학습에 적합)
    void ResetStage()
    {
        // 학습용: 이번 에피소드 스테이지를 무작위로 (모든 난이도 구간 골고루 학습)
        if (randomizeStageForTraining)
            GameManager.stage = Random.Range(1, maxStageForTraining + 1);

        if (GameManager.Instance != null)
            GameManager.Instance.ResetForNewStage();

        foreach (Tower t in Object.FindObjectsByType<Tower>(FindObjectsSortMode.None))
            Destroy(t.gameObject);
        foreach (Enemy e in Object.FindObjectsByType<Enemy>(FindObjectsSortMode.None))
            Destroy(e.gameObject);
        foreach (Tile tile in Object.FindObjectsByType<Tile>(FindObjectsSortMode.None))
            tile.occupied = false;

        // 이번 에피소드의 경로를 무작위로 새로 생성 (다양한 맵에 일반화)
        if (randomizePathEachEpisode && grid != null)
            grid.RandomizePath();

        // 종류별 업그레이드 레벨 처리.
        //  · 플레이: keepUpgradesAcrossStages면 S2부터는 유지 (= 플레이어의 유일한 성장 레버)
        //  · 학습:   매 에피소드 초기화 후, 스테이지에 비례한 레벨을 시드로 심는다
        if (TowerUpgradeManager.Instance != null)
        {
            bool keep = keepUpgradesAcrossStages
                        && !randomizeStageForTraining
                        && GameManager.stage > 1;
            if (!keep)
            {
                TowerUpgradeManager.Instance.ResetLevels();
                SeedUpgradeLevels();
            }
        }

        currentRound = 0;
        aliveEnemies = 0;
        leakedThisRound = 0;

        // 배치 AI를 쓰면 봇은 끄고, 아니면 봇 실력 무작위화
        if (bot != null)
        {
            bot.enabled = !usePlacementAgent;
            if (!usePlacementAgent) bot.OnEpisodeReset();
        }
    }

    // ── 학습용 강화 시드 ────────────────────────────────────────────────
    // 스테이지 S로 시작하는 에피소드에, "S까지 오는 동안 쌓았을 법한" 강화 레벨을 미리 준다.
    // 종류를 무작위로 고르는 것이 핵심: 매 에피소드 다른 종류가 강화된 상태로 시작하므로
    // 배치 AI는 '어느 종류가 이미 강한지'를 관측해서 그 종류를 짓는 법을 배워야 한다.
    // (고정 배분이면 그냥 특정 타워를 외워버려서 관측을 읽을 이유가 없어진다)
    void SeedUpgradeLevels()
    {
        var upm = TowerUpgradeManager.Instance;
        if (upm == null || upgradeSeedPerStage <= 0f) return;

        int total = Mathf.RoundToInt(upgradeSeedPerStage * (GameManager.stage - 1));
        if (total <= 0) return;

        List<string> names = TowerTypeNames();
        if (names.Count == 0) return;

        for (int k = 0; k < total; k++)
        {
            // 무작위 종류에 1레벨. 이미 최대면 다른 종류를 찾아본다(전부 최대면 중단).
            bool placed = false;
            for (int attempt = 0; attempt < names.Count * 2; attempt++)
            {
                string nm = names[Random.Range(0, names.Count)];
                if (string.IsNullOrEmpty(nm) || upm.IsMaxed(nm)) continue;
                upm.SetLevel(nm, upm.GetLevel(nm) + 1);
                placed = true;
                break;
            }
            if (!placed) break;
        }
    }

    private List<string> cachedTypeNames;

    // 타워 종류 이름 목록 — 배치 AI / BuildManager와 같은 순서를 쓴다
    List<string> TowerTypeNames()
    {
        if (cachedTypeNames != null && cachedTypeNames.Count > 0) return cachedTypeNames;
        // 빈 결과는 캐시하지 않는다 — Start()에서 참조가 아직 안 잡힌 시점에 호출되면
        // 영구히 빈 목록이 굳어 시드가 조용히 죽는다.
        cachedTypeNames = new List<string>();

        GameObject[] prefabs = null;
        if (placementAgent != null && placementAgent.towerPrefabs != null && placementAgent.towerPrefabs.Length > 0)
            prefabs = placementAgent.towerPrefabs;
        else
        {
            BuildManager bm = Object.FindFirstObjectByType<BuildManager>();
            if (bm != null) prefabs = bm.towerPrefabs;
        }
        if (prefabs == null) return cachedTypeNames;

        foreach (GameObject p in prefabs)
        {
            if (p == null) continue;
            Tower t = p.GetComponent<Tower>();
            string nm = (t != null && !string.IsNullOrEmpty(t.towerName)) ? t.towerName : p.name;
            if (!string.IsNullOrEmpty(nm)) cachedTypeNames.Add(nm);
        }
        return cachedTypeNames;
    }

    IEnumerator RunEpisode()
    {
        for (currentRound = 0; currentRound < roundsPerStage; currentRound++)
        {
            // 라운드 시작 수입: 1~5라운드는 roundGoldStart→roundBaseGold로 점증, 6라운드부터 더 점증
            if (GameManager.Instance != null)
            {
                int roundGold;
                if (currentRound < lateRoundStart)
                {
                    float t = (lateRoundStart > 1) ? currentRound / (float)(lateRoundStart - 1) : 1f;
                    roundGold = Mathf.RoundToInt(Mathf.Lerp(roundGoldStart, roundBaseGold, t));
                }
                else
                {
                    roundGold = roundBaseGold + lateRoundGold * (currentRound - lateRoundStart + 1);
                }
                GameManager.Instance.AddGold(roundGold);
            }

            // ── 정비 페이즈 (플레이어/봇/배치AI가 타워 배치) ──
            state = State.Prep;
            if (usePlacementAgent && placementAgent != null)
            {
                // 배치 AI가 정비 단계에 여러 번 결정을 내려 타워를 배치
                yield return StartCoroutine(placementAgent.BuildPhase());
                if (IsPlayerDead()) { EndRoundFail(); yield break; }
            }
            else
            {
                prepRemaining = prepTime;
                while (prepRemaining > 0f)
                {
                    if (IsPlayerDead()) { EndRoundFail(); yield break; }
                    prepRemaining -= Time.deltaTime;
                    yield return null;
                }
            }

            // ── 에이전트에게 다음 라운드 난이도 결정을 요청 ──
            if (agent != null)
            {
                agent.RequestDecision();
                // 결정(OnActionReceived)이 적용될 시간을 준다
                yield return new WaitForFixedUpdate();
                yield return new WaitForFixedUpdate();
            }

            RoundData round = BuildRound(currentRound);

            healthAtRoundStart = (GameManager.Instance != null) ? GameManager.Instance.playerHealth : 0;
            leakedThisRound = 0;
            enemiesThisRound = 0;
            maxAliveThisRound = 0;
            Enemy.slowAppliedThisRound = 0;   // 지원 타워 감속 집계 리셋
            Enemy.maxProgressThisRound = 0f;  // 적 최대 경로 진행도 리셋
            ResetTowerShots();
            roundStartTime = Time.time;

            // ── 스폰 → 소탕 대기 ──
            state = State.Spawning;

            // 전투 중에도 배치 AI가 증축할 수 있게 병렬 루프를 띄운다 (처치 골드를 바로 재투자)
            Coroutine combatBuild = null;
            if (usePlacementAgent && placementAgent != null && allowBuildDuringCombat)
                combatBuild = StartCoroutine(CombatBuildLoop());

            yield return StartCoroutine(SpawnRound(round));
            roundSpawnEndTime = Time.time; // 스폰 완료 시각 기록

            state = State.Fighting;
            while (aliveEnemies > 0)
            {
                if (IsPlayerDead())
                {
                    if (combatBuild != null) StopCoroutine(combatBuild);
                    EndRoundFail();
                    yield break;
                }
                yield return null;
            }
            if (combatBuild != null) StopCoroutine(combatBuild);

            // ── 라운드 클리어 → 보상 ──
            ComputeTowerUtilization(); // 이번 라운드 가동률 먼저 계산 (보상에서 사용)
            GradeSurvivedRound();
            LogRoundState();
            Debug.Log($"라운드 {currentRound + 1} 클리어!");
        }

        // ── 스테이지 클리어 → 에피소드 종료 ──
        state = State.StageClear;
        Debug.Log("스테이지 클리어! 🎉");
        if (collector != null) collector.LogComposition(true, roundsPerStage, roundsPerStage);
        if (advanceStageOnClear)
            GameManager.stage = Mathf.Max(1, GameManager.stage + 1); // 플레이용 진행
        yield return new WaitForSeconds(1.5f); // 결과 잠깐 표시
        if (usePlacementAgent && placementAgent != null)
        {
            placementAgent.StageClearBonus();
            placementAgent.EndEpisode();
        }
        if (agent != null) agent.EndEpisode();  // → 다음 에피소드 자동 시작
    }

    // 전투 중 배치: 일정 간격으로 배치 AI에게 "지금 더 지을래?"를 묻는다.
    // (중첩 코루틴을 쓰지 않고 여기서 직접 결정을 요청해야 라운드 종료 시 확실히 멈출 수 있다)
    IEnumerator CombatBuildLoop()
    {
        while (state == State.Spawning || state == State.Fighting)
        {
            yield return new WaitForSeconds(combatDecisionInterval);
            if (state != State.Spawning && state != State.Fighting) yield break;
            if (IsPlayerDead()) yield break;

            for (int i = 0; i < maxActionsPerCombatTick; i++)
            {
                placementAgent.lastActionWasNoOp = false;
                placementAgent.RequestDecision();
                yield return new WaitForFixedUpdate();
                yield return new WaitForFixedUpdate();
                if (placementAgent.lastActionWasNoOp) break;   // 지금은 안 짓겠다 → 이번 틱 종료
                if (state != State.Spawning && state != State.Fighting) yield break;
            }
        }
    }

    // 살아남은 라운드의 보상: 체력 손실 비율을 목표치와 비교
    void GradeSurvivedRound()
    {
        int healthNow = (GameManager.Instance != null) ? GameManager.Instance.playerHealth : 0;
        int maxH = (GameManager.Instance != null) ? GameManager.Instance.MaxHealth() : 1;
        float lossFrac = Mathf.Max(0f, (maxH > 0) ? (float)(healthAtRoundStart - healthNow) / maxH : 0f);
        float linger = Mathf.Max(0f, Time.time - roundSpawnEndTime); // 스폰 완료 후 소탕까지 걸린 시간

        // DDA가 다음 라운드 결정 때 볼 관측을 먼저 채워둔다 (RequestDecision보다 먼저 실행됨)
        if (collector != null)
        {
            collector.lastRoundLinger = linger;
            collector.lastRoundSlowRatio = (enemiesThisRound > 0)
                ? Mathf.Clamp01(Enemy.slowAppliedThisRound / (float)enemiesThisRound) : 0f;
            collector.lastRoundMaxAlive = maxAliveThisRound;
            collector.lastRoundMaxProgress = Enemy.maxProgressThisRound;
        }

        if (agent != null) agent.GradeRound(lossFrac, false, currentRound, roundsPerStage);
        if (usePlacementAgent && placementAgent != null)
        {
            int towerCount = Object.FindObjectsByType<Tower>(FindObjectsSortMode.None).Length;
            float util = (collector != null) ? collector.lastRoundTowerUtilization : 0f;
            placementAgent.GradeRoundSurvived(lossFrac, linger, towerCount, util,
                                              Enemy.slowAppliedThisRound, enemiesThisRound);
        }
    }

    // 라운드 도중 플레이어 사망 → 붕괴 보상 후 에피소드 종료
    void EndRoundFail()
    {
        state = State.StageFail;
        Debug.Log("스테이지 실패...");
        if (collector != null) collector.LogComposition(false, currentRound + 1, roundsPerStage);
        if (usePlacementAgent && placementAgent != null)
        {
            placementAgent.GradeDied();
            placementAgent.EndEpisode();
        }
        if (agent != null)
        {
            agent.GradeRound(1f, true, currentRound, roundsPerStage); // died
            agent.EndEpisode();
        }
    }

    // ── 종류별 '위협 비용' (예산제의 환율) ────────────────────────────────
    // 탱커만 roundHealthGrowth=0.30이라 라운드마다 체력이 30%씩 누적되는데 비용이 2.5로 고정이었다.
    // 그 결과 예산 1점당 체력이 3라운드 1.9배 · 5라운드 2.6배 · 10라운드 4.4배가 되어
    // DDA가 "탱커에 전부 몰기"를 최적 정책으로 학습했다(= 탱커만 나오는 라운드).
    // 비용이 실제 강함을 따라 오르게 만들어 예산 시스템이 거짓말을 하지 않게 한다.
    public float ThreatCost(EnemyType t, int roundIdx)
    {
        return Mathf.Max(0.1f, t.cost * (1f + t.roundHealthGrowth * Mathf.Max(0, roundIdx)));
    }

    // 에이전트가 정한 예산·가중치·체력으로 이번 라운드 구성 생성
    RoundData BuildRound(int i)
    {
        int stage = GameManager.stage;
        float countScale = 1f + (stage - 1) * stageCountGrowth;
        float healthScale = 1f + (stage - 1) * stageHealthGrowth;

        float intensity = (agent != null) ? agent.intensity : 1f;       // 총 위협 강도(단조 증가)
        float healthMult = (agent != null) ? agent.healthMultiplier : 1f; // 체력 배율(단조 증가)

        // 이번 라운드 총 위협 예산 (라운드·스테이지·에이전트 강도)
        float totalBudget = (roundBudgetBase + i * roundBudgetGrowth) * countScale * intensity;

        // 에이전트의 종류별 가중치 (없으면 일반에 몰기)
        int n = enemyTypes.Count;
        float[] w = new float[n];
        float wsum = 0f;
        for (int t = 0; t < n; t++)
        {
            float raw = (agent != null && agent.typeWeights != null && t < agent.typeWeights.Length)
                        ? Mathf.Max(0f, agent.typeWeights[t]) : (t == 0 ? 1f : 0f);
            w[t] = raw; wsum += raw;
        }
        if (wsum <= 0f) { w[0] = 1f; wsum = 1f; }

        // 예산을 종류별로 배분 → 개수 = 배정 예산 / 그 종류의 비용
        List<int> counts = new List<int>();
        for (int t = 0; t < n; t++)
        {
            float budgetT = totalBudget * (w[t] / wsum);
            float c = ThreatCost(enemyTypes[t], i);   // 라운드 성장을 반영한 비용
            counts.Add(Mathf.Max(0, Mathf.RoundToInt(budgetT / c)));
        }

        return new RoundData
        {
            spawnInterval = Mathf.Max(0.25f, 0.8f - i * 0.05f),
            baseHealth = (roundHealthBase + i * roundHealthGrowth) * healthScale * healthMult,
            baseSpeed = 2f + (stage - 1) * 0.1f,
            typeCounts = counts
        };
    }

    void GenerateDefaultTypes()
    {
        enemyTypes.Add(new EnemyType {
            typeName = "일반", healthMultiplier = 1f, speedMultiplier = 1f,
            scale = 0.5f, color = new Color(0.85f, 0.3f, 0.3f), cost = 1.0f, leakDamageFraction = 0.05f
        });
        // 빠름 비용 1.4 → 1.1: 속도 1.8배라 사거리 안에 머무는 시간이 절반뿐인데(=실효 생존력이 높은데)
        // 비용이 1.4로 매겨져 예산 대비 가치가 꼴찌였다. DDA가 아예 안 고르는 '죽은 카드'였음.
        enemyTypes.Add(new EnemyType {
            typeName = "빠름", healthMultiplier = 0.6f, speedMultiplier = 1.8f,
            scale = 0.4f, color = new Color(0.95f, 0.85f, 0.2f), cost = 1.1f, leakDamageFraction = 0.04f
        });
        enemyTypes.Add(new EnemyType {
            typeName = "탱커", healthMultiplier = 3f, speedMultiplier = 0.6f,
            scale = 0.8f, color = new Color(0.55f, 0.35f, 0.7f), cost = 2.5f, leakDamageFraction = 0.12f,
            // 0.30 → 0.18 (2026-07-29). 라운드 내 경사 완화의 일부.
            // 탱커만 라운드마다 체력이 누적 성장해서, R10 탱커는 R1의 3.7배였다.
            // 스테이지 후반 라운드의 손실 급증(S9 R9 0.134 → R10 0.264)의 주범이라
            // DDA가 초·중반 라운드에서 강도를 올릴 여지를 전부 잡아먹고 있었다.
            roundHealthGrowth = 0.18f   // 라운드마다 +18%씩 누적 → R10 탱커는 R1의 약 2.6배
        });
    }

    IEnumerator SpawnRound(RoundData round)
    {
        List<Vector3> path = grid != null ? grid.GetPathWorldPoints() : null;
        if (path == null || path.Count == 0) yield break;

        List<int> order = new List<int>();
        for (int t = 0; t < round.typeCounts.Count && t < enemyTypes.Count; t++)
            for (int n = 0; n < round.typeCounts[t]; n++)
                order.Add(t);
        Shuffle(order);

        foreach (int typeIdx in order)
        {
            SpawnOne(round, enemyTypes[typeIdx], path);
            yield return new WaitForSeconds(round.spawnInterval);
        }
    }

    void Shuffle(List<int> list)
    {
        for (int a = list.Count - 1; a > 0; a--)
        {
            int b = Random.Range(0, a + 1);
            (list[a], list[b]) = (list[b], list[a]);
        }
    }

    void SpawnOne(RoundData round, EnemyType type, List<Vector3> path)
    {
        float baseTypeHealth = round.baseHealth * type.healthMultiplier;
        // 라운드가 지날수록 추가 체력 (탱커는 후반 라운드에 급격히 단단해짐)
        float roundGrow = 1f + type.roundHealthGrowth * currentRound;
        float health = baseTypeHealth * roundGrow;
        float speed = round.baseSpeed * type.speedMultiplier;
        // 처치 골드 = 적의 '위협도' × 스테이지 완만 성장. (적 체력 배율과는 여전히 분리 → 후반 소득 폭증 방지)
        // 위협 비용과 같은 값을 쓰므로 예산 1점당 수입이 종류와 무관하게 같아진다.
        // → DDA가 구성(탱커 몰기)으로 플레이어의 경제를 말려 죽이는 경로가 막힌다.
        int stg = GameManager.stage;
        float stageFactor = 1f + killRewardStageGrowth * Mathf.Max(0, stg - 1);
        int reward = Mathf.Max(1, Mathf.RoundToInt(killRewardBase * ThreatCost(type, currentRound) * stageFactor));

        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = $"Enemy_{type.typeName}";
        go.transform.localScale = Vector3.one * type.scale;

        Vector3 start = path[0];
        go.transform.position = new Vector3(start.x, enemyHeight, start.z);

        Renderer rend = go.GetComponent<Renderer>();
        MaterialPropertyBlock mpb = new MaterialPropertyBlock();
        rend.GetPropertyBlock(mpb);
        mpb.SetColor("_BaseColor", type.color);
        mpb.SetColor("_Color", type.color);
        rend.SetPropertyBlock(mpb);

        Enemy enemy = go.AddComponent<Enemy>();
        enemy.Init(path, health, speed);
        enemy.reward = reward;
        // 골 도달 데미지 = 플레이어 최대체력 × 종류별 비율 (탱커가 더 아픔)
        int maxH = (GameManager.Instance != null) ? GameManager.Instance.MaxHealth() : 20;
        enemy.damageToPlayer = Mathf.Max(1, Mathf.RoundToInt(maxH * type.leakDamageFraction));

        aliveEnemies++;
        enemiesThisRound++;
        if (aliveEnemies > maxAliveThisRound) maxAliveThisRound = aliveEnemies;
        enemy.onDied = OnEnemyDied;
        enemy.onReachedEnd = OnEnemyLeaked;
    }

    void OnEnemyDied(Enemy e)
    {
        aliveEnemies = Mathf.Max(0, aliveEnemies - 1);
    }

    void OnEnemyLeaked(Enemy e)
    {
        aliveEnemies = Mathf.Max(0, aliveEnemies - 1);
        leakedThisRound++;
    }

    // 이번 라운드 타워 평균 가동률 계산 → 수집기에 전달 (놀던 타워 많으면 낮음)
    void ComputeTowerUtilization()
    {
        if (collector == null) return;
        Tower[] towers = Object.FindObjectsByType<Tower>(FindObjectsSortMode.None);
        if (towers.Length == 0) { collector.lastRoundTowerUtilization = 0f; return; }

        float sum = 0f;
        foreach (Tower t in towers)
        {
            // 전투 중에 지어진 타워는 '지어진 뒤부터'를 기준으로 가동률을 잰다.
            // (라운드 전체 시간으로 나누면 늦게 지은 타워가 놀았다고 잘못 집계돼,
            //  towerActivity 보상이 전투 중 건설이라는 행동을 오히려 벌하게 된다)
            float since = Mathf.Max(roundStartTime, t.activeSince);
            float duration = Mathf.Max(0.1f, Time.time - since);
            float maxShots = (t.shootingSpeed > 0f) ? duration / t.shootingSpeed : 0f;
            float util = (maxShots > 0f) ? Mathf.Clamp01(t.shotsThisRound / maxShots) : 0f;
            t.lastRoundUtil = util;   // 배치 AI 관측용 (타워별 가동률 채널)
            sum += util;
        }
        collector.lastRoundTowerUtilization = sum / towers.Length;
    }

    void ResetTowerShots()
    {
        foreach (Tower t in Object.FindObjectsByType<Tower>(FindObjectsSortMode.None))
            t.shotsThisRound = 0;
    }

    void LogRoundState()
    {
        if (collector == null) return;
        int healthNow = (GameManager.Instance != null) ? GameManager.Instance.playerHealth : 0;
        collector.lastRoundHealthLost = Mathf.Max(0, healthAtRoundStart - healthNow);
        collector.lastRoundLeaked = leakedThisRound;
        collector.LogRoundEnd(currentRound + 1, roundsPerStage);
    }

    bool IsPlayerDead()
    {
        return GameManager.Instance != null && GameManager.Instance.playerHealth <= 0;
    }

    void OnGUI()
    {
        GUIStyle style = new GUIStyle { fontSize = 20 };
        style.normal.textColor = Color.white;

        string line;
        switch (state)
        {
            case State.Prep:
                line = $"라운드 {currentRound + 1}/{roundsPerStage} — 정비 시간 {prepRemaining:0.0}초";
                break;
            case State.Spawning:
            case State.Fighting:
                line = $"라운드 {currentRound + 1}/{roundsPerStage} — 남은 적 {aliveEnemies}";
                break;
            case State.StageClear:
                line = "스테이지 클리어!";
                break;
            case State.StageFail:
                line = "스테이지 실패...";
                break;
            default:
                line = "";
                break;
        }
        GUI.Label(new Rect(12, 94, 600, 28), line, style);
    }
}
