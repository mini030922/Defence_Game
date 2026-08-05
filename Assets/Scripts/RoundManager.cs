using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

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
    // 25 → 40 (2026-07-31). dda_v9 실측에서 원인이 특정됐다.
    //
    // 증상: DDA에게 강도 상한을 3.0까지 열어줬는데 평균 1.46밖에 안 썼다(상한 도달률 1.1%).
    //   S5는 상한 2.06인데 전 라운드 강도 1.01~1.12로 손도 대지 않았다.
    // 그런데도 라운드별 손실은:
    //   S10  R1~R7 0.012~0.111 (목표 0.233의 절반 이하)  →  R9 0.323  R10 0.512
    //   S5   R1~R8 0.069~0.111                          →  R9 0.134  R10 0.219
    //   사망의 89%가 R8~R10에 몰림.
    //
    // 즉 후반 라운드 절벽을 DDA가 만드는 게 아니라 **기본 게임의 경제**가 만들고 있었다.
    // 위협은 (4+1.4i)×(10+2i)로 이차식인데 라운드 수입은 선형이라 R9~R10에 구매력이 무너진다.
    // 강도는 단조 증가라 앞에서 올리면 뒤에서 못 내린다 →
    // "앞 라운드를 포기하고 참는 것"이 DDA의 기대값상 최적이 되어버렸다.
    //
    // 후반 수입을 늘려 R9~R10의 절벽을 완만하게 만들면, DDA가 앞 라운드에서 올릴 여유가 생긴다.
    //   10라운드 총 수입 875 → 1100골드 (+26%), 증가분은 전부 R6~R10에 집중된다.
    public int lateRoundGold = 40;             // lateRoundStart 이후 라운드마다 누적 추가

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

    // ── 학습용 강화 시드 (2026-07-30 v2: 균등 무작위 → 실측표) ──────────────
    // v1은 '스테이지당 0.7레벨을 5종류에 무작위'였다. plc_v9 결과로 두 가지가 틀렸음이 드러났다.
    //
    //  (1) 배분이 실제와 달랐다. 실측: 미사일 타워를 **0개** 지었는데 평균 레벨 1.18 —
    //      기대 시드값(0.7×9÷5=1.26)과 정확히 일치. 즉 시드의 약 40%가 안 쓰는 타워에 버려졌다.
    //      실제 플레이어는 자기가 쓰는 종류에 집중 투자한다.
    //  (2) 총량이 절반이었다. 각 스테이지 종료 레벨에서 시드분을 빼면 '실제 구매량'이 나오고,
    //      그걸 누적하면 연속 플레이 시 보유량이 나온다. S10 진입 시점 실측 12.6레벨 vs 시드 6.3레벨.
    //
    // 그리고 결정적으로: 연속 플레이라면 **폭탄 타워가 S5~S6에 이미 만렙(Lv5)**이다.
    // plc_v9는 "주력이 만렙이 됐다, 이제 돈을 어디 쓰지?"라는 후반 게임의 핵심 상황을
    // 한 번도 경험한 적이 없다. (잔금이 S10에서 353골드씩 남던 이유)
    //
    // ※ 이 표는 **하한**이다. 인센티브가 잘린 상태에서 산 양이라, 종결 보상을 넣은 뒤
    //   다시 측정해서 갱신해야 한다(고정점 반복). plc_v10 결과로 재계산할 것.
    // ※ 열 순서는 BuildManager.towerPrefabs 순서 = 일반 / 속사 / 폭탄 / 미사일 / 지원.
    //   프리팹 순서를 바꾸면 이 표도 반드시 같이 바꿔야 한다.
    public float upgradeSeedScale = 1f;   // 시드 전체 배율 (0 = 시드 끄기, 실험용)

    //
    // ── 2026-08-04 v3: 가격 개편에 맞춰 재환산 ────────────────────────────
    // v2 표는 `upgradeCostFactor 1.5` · `maxLevel 5` 시절의 실측 역산값이다.
    // 그 뒤 강화 비용이 오르고(1.5 → 2.2) 최대 레벨이 열렸으므로(5 → 10) 그대로 쓰면
    // **플레이어가 낸 적 없는 골드만큼 공짜로 강해진 채로** 배치 AI가 학습한다.
    //
    // 환산 원리는 '같은 골드로 살 수 있는 레벨'이다. 누적 강화비를 건설비 단위로 쓰면
    //     비용(L) = f·L + 0.6·L(L−1)/2 = 0.3L² + (f−0.3)L
    //     옛(f=1.5): 0.3L² + 1.2L      새(f=2.2): 0.3L² + 1.9L
    // 두 식을 같게 두고 L을 풀면 새 레벨이 나온다. 스테이지별 강화 총액(골드)은 정확히 보존된다.
    //   S5  2,658골드 → 2,658골드,  S10 4,730골드 → 4,730골드 (설계상 동일)
    //
    // 레벨 수치는 내려갔지만 그게 약화는 아니다. 레벨당 효과도 같이 바뀌었기 때문:
    //   옛 +40%/레벨 → Lv5에서 ×5.38     새 +14.2%/레벨 → Lv5에서 ×1.94
    // (사용자 설계: "최대레벨 DPS를 옛 최대레벨의 0.7배로, 대신 더 많은 돈이 들게")
    //
    // ※ 폭탄 S6~S10은 **하한**이다. 옛 측정이 Lv5 천장에 막혀 있던 구간이라
    //   "얼마나 더 사고 싶었는지"를 표에서 복원할 수 없다(당시 S10에서 353골드가 남아돌던 이유).
    //   plc_v13 로그로 재측정하면 이 칸들이 먼저 올라갈 것이다. 고정점 반복 2회차.
    static readonly float[][] UpgradeSeedTable = new float[][] {
        //          일반   속사   폭탄  미사일  지원        진입 스테이지
        new float[]{0.00f, 0.00f, 0.00f, 0.00f, 0.00f}, // S1
        new float[]{0.34f, 0.04f, 0.52f, 0.00f, 0.09f}, // S2
        new float[]{0.71f, 0.10f, 1.41f, 0.04f, 0.20f}, // S3
        new float[]{1.08f, 0.10f, 2.41f, 0.04f, 0.26f}, // S4
        new float[]{1.55f, 0.12f, 3.59f, 0.04f, 0.34f}, // S5
        new float[]{2.13f, 0.17f, 4.25f, 0.07f, 0.49f}, // S6  ← 여기부터 폭탄은 하한(옛 Lv5 천장)
        new float[]{2.78f, 0.25f, 4.25f, 0.15f, 0.72f}, // S7
        new float[]{3.31f, 0.25f, 4.25f, 0.15f, 0.87f}, // S8
        new float[]{3.89f, 0.29f, 4.25f, 0.16f, 1.05f}, // S9
        new float[]{4.25f, 0.37f, 4.25f, 0.22f, 1.27f}, // S10
    };

    [Header("스테이지")]
    public bool randomizeStageForTraining = true; // 학습용: 에피소드마다 스테이지 무작위(전 구간 학습)
    public int maxStageForTraining = 10;
    public bool advanceStageOnClear = false;      // 플레이용: 클리어 시 스테이지 +1

    [Header("플레이어 화력 배율 (2026-08-01 신설) — 계측 및 DDA 일반화용")]
    // 왜 필요한가: 사람이 13스테이지를 피해 0으로 클리어했다. DDA는 지금까지
    //   단 하나의 상대(배치 AI)만 겪어서 그 실력에 과적합돼 있고, 사람은 분포 밖이다.
    //
    //  · 계측: randomizePlayerPower=false 로 두고 playerPowerScale 을 1.0 → 0.5 → 0.3 …
    //          낮춰가며 사람이 피를 흘리기 시작하는 지점을 찾는다. 그 역수가 실력 배수다.
    //  · 학습: randomizePlayerPower=true 로 두면 에피소드마다 [min,max]에서 무작위로 뽑는다.
    //          같은 스테이지에 약한 상대와 강한 상대가 섞이므로,
    //          DDA는 **관측을 읽어서 구분하는 법을 반드시 배워야만** 점수를 받는다.
    public bool showEnemyHealthBars = true; // 적 머리 위 체력바 (학습 시 자동 해제)
    public bool showSplashEffects  = true; // 광역 공격 범위 표시 (학습 시 자동 해제)
    public bool randomizePlayerPower = false;
    public float playerPowerScale = 1f;   // randomize가 꺼져 있을 때 쓰는 고정값
    public float playerPowerMin = 1f;     // 학습용 하한
    public float playerPowerMax = 1f;     // 학습용 상한 (계측 결과로 정한다)

    [Header("정비·예고·배속 (2026-08-04 플레이테스트 반영)")]
    // 테스터 지적 3가지를 한 묶음으로 고친다.
    //
    //  (1) "스테이지 변경 후 맵을 확인할 시간을 줘야 함"
    //      → firstRoundExtraPrep. 경로가 매 스테이지 새로 생성되는데 정비 5초는 읽을 시간도 안 된다.
    //
    //  (2) "AI가 빠른 적으로 속사를 유도하고 마지막에 탱커 10마리를 몰아 보낸다"
    //      → showWavePreview. 이건 DDA의 버그가 아니라 **정보 비대칭**이다.
    //        DDA는 플레이어의 종류별 타워 구성(관측 15~19번)을 보고 반례를 고르는데,
    //        플레이어는 스폰이 시작되고 나서야 뭐가 오는지 안다. 한쪽만 상대 패를 보는 게임이었다.
    //        DDA가 구성을 확정한 '뒤에' 짧은 예고 + 2차 정비창을 준다.
    //        → 난이도는 그대로 두고 '농락'만 제거한다. (예고를 봐도 돈이 없으면 여전히 어렵다)
    //
    //  (3) "적이 타워 사거리를 벗어나면 손가락 빨면서 기다려야 함"
    //      → 배속 버튼. 죽은 시간을 플레이어가 직접 건너뛴다.
    //
    // ※ 셋 다 학습(배치 AI / --no-graphics)에서는 자동으로 꺼진다.
    //   학습 환경이 바뀌면 dda_v11 · plc_v12 가 배운 환경과 달라져 재학습이 필요해지기 때문.
    public float firstRoundExtraPrep = 8f; // 스테이지 첫 라운드에만 추가로 주는 정비 시간(초)
    public bool showWavePreview = true;    // 다음 웨이브 예고 + 2차 정비창
    public float previewTime = 7f;         // 예고 창 길이(초)
    public bool allowSpeedControl = true;  // 배속 버튼
    public float[] speedOptions = new float[] { 1f, 2f, 3f };

    [Header("최적 배치 AI")]
    public bool usePlacementAgent = false; // true면 정비 단계에 봇 대신 PlacementAgent가 타워 배치

    [Header("전투 중 건설 (사람 플레이어는 원래 가능 — 배치 AI에게도 같은 권한)")]
    public bool allowBuildDuringCombat = true;
    public float combatDecisionInterval = 1.5f; // 전투 중 배치 AI에게 결정을 묻는 간격(초)
    public int maxActionsPerCombatTick = 2;     // 한 틱에 내릴 수 있는 최대 결정 수

    private enum State { Prep, Preview, Spawning, Fighting, StageClear, StageFail }
    private State state = State.Prep;
    private int currentRound = 0;

    // ── 사람 플레이 전용 UX 상태 ────────────────────────────
    // HumanUx가 false면(학습·배치AI) 예고창·배속·스킵이 전부 무효가 된다.
    private bool HumanUx => !Application.isBatchMode && !usePlacementAgent;
    private float previewRemaining = 0f;
    private string pendingWaveText = "";
    private bool skipRequested = false;
    private int speedIndex = 0;

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

    void Update()
    {
        if (!HumanUx) return;
        var kb = Keyboard.current;
        if (kb == null) return;
        // 스페이스 = 정비/예고 즉시 종료, Tab = 배속 순환
        if (kb.spaceKey.wasPressedThisFrame && (state == State.Prep || state == State.Preview))
            skipRequested = true;
        if (kb.tabKey.wasPressedThisFrame && allowSpeedControl)
            SetSpeed(speedIndex + 1);
    }

    // 배속 변경. 학습에서는 mlagents가 --time-scale로 Time.timeScale을 직접 잡고 있으므로
    // 절대 건드리지 않는다(건드리면 학습 속도가 20배에서 1배로 떨어진다).
    void SetSpeed(int i)
    {
        if (!HumanUx || speedOptions == null || speedOptions.Length == 0) return;
        speedIndex = ((i % speedOptions.Length) + speedOptions.Length) % speedOptions.Length;
        Time.timeScale = Mathf.Max(0.1f, speedOptions[speedIndex]);
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

        // 체력바: --no-graphics(학습)에서는 오브젝트를 아예 만들지 않는다
        Enemy.showHealthBars = showEnemyHealthBars && !Application.isBatchMode;
        SplashEffect.show    = showSplashEffects  && !Application.isBatchMode;

        // 새 스테이지는 1배속으로 시작한다 (앞 스테이지 끝에 3배로 두고 넘어오면 맵을 볼 수가 없다).
        // 학습에서는 mlagents가 Time.timeScale을 잡고 있으므로 절대 손대지 않는다.
        if (HumanUx && allowSpeedControl) SetSpeed(0);

        // 이번 에피소드의 플레이어 화력 배율 확정 (타워가 생성되기 전에 정해야 한다)
        Tower.powerScale = randomizePlayerPower
            ? Random.Range(playerPowerMin, playerPowerMax)
            : Mathf.Max(0.01f, playerPowerScale);

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
        if (upm == null || upgradeSeedScale <= 0f) return;

        List<string> names = TowerTypeNames();
        if (names.Count == 0) return;

        int row = Mathf.Clamp(GameManager.stage - 1, 0, UpgradeSeedTable.Length - 1);
        float[] want = UpgradeSeedTable[row];

        for (int i = 0; i < names.Count && i < want.Length; i++)
        {
            float v = want[i] * upgradeSeedScale;
            if (v <= 0f) continue;
            // 소수점은 확률로 처리 → 평균은 표값을 유지하면서 에피소드마다 편차가 생긴다.
            // (항상 평균값만 주면 AI가 한 가지 상태만 보고 일반화를 못 한다)
            int lv = Mathf.FloorToInt(v);
            if (Random.value < (v - lv)) lv++;
            if (lv > 0) upm.SetLevel(names[i], lv);
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
                // 스테이지 첫 라운드에는 새 맵을 읽을 시간을 더 준다.
                // (경로가 에피소드마다 새로 생성되는데 5초로는 어디가 길인지도 못 본다)
                prepRemaining = prepTime + ((currentRound == 0 && HumanUx) ? firstRoundExtraPrep : 0f);
                skipRequested = false;
                while (prepRemaining > 0f && !skipRequested)
                {
                    if (IsPlayerDead()) { EndRoundFail(); yield break; }
                    // 배속은 '전투'만 빨라져야 한다. 정비 시간까지 같이 줄면 배속의 의미가 없다.
                    // 단, 배치 봇 학습(batchMode)에서는 원래대로 스케일된 시간을 써야 학습 속도가 유지된다.
                    prepRemaining -= HumanUx ? Time.unscaledDeltaTime : Time.deltaTime;
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

            // ── 다음 웨이브 예고 + 2차 정비창 ──────────────────────────────
            // DDA가 구성을 확정한 '뒤'라서, 플레이어도 이제 상대 패를 본 상태에서 대응할 수 있다.
            // 이 창 동안에도 건설·강화·판매·이동이 전부 가능하다.
            if (HumanUx && showWavePreview && previewTime > 0f)
            {
                pendingWaveText = DescribeRound(round);
                state = State.Preview;
                previewRemaining = previewTime;
                skipRequested = false;
                while (previewRemaining > 0f && !skipRequested)
                {
                    if (IsPlayerDead()) { EndRoundFail(); yield break; }
                    previewRemaining -= Time.unscaledDeltaTime;
                    yield return null;
                }
            }

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
        if (agent != null)
        {
            float hp = (GameManager.Instance != null) ? GameManager.Instance.HealthRatio() : 0f;
            agent.GradeStageEnd(true, roundsPerStage, roundsPerStage, hp);
        }
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
        if (agent != null) agent.GradeStageEnd(false, currentRound + 1, roundsPerStage, 0f);
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

    // 예고창에 띄울 웨이브 요약 — 종류 × 마리수 (개체 체력).
    // 체력을 같이 보여주는 이유: "탱커 12마리"만으로는 폭탄을 더 지어야 할지 강화해야 할지 판단이 안 된다.
    string DescribeRound(RoundData r)
    {
        var sb = new System.Text.StringBuilder();
        int total = 0;
        for (int t = 0; t < r.typeCounts.Count && t < enemyTypes.Count; t++)
        {
            int c = r.typeCounts[t];
            if (c <= 0) continue;
            EnemyType et = enemyTypes[t];
            float hp = r.baseHealth * et.healthMultiplier * (1f + et.roundHealthGrowth * currentRound);
            if (sb.Length > 0) sb.Append("    ");
            sb.Append($"{et.typeName} ×{c} (체력 {hp:0})");
            total += c;
        }
        if (sb.Length == 0) sb.Append("(없음)");
        return $"{sb}        총 {total}마리";
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
        HudScaler.Begin();
        GUIStyle style = new GUIStyle { fontSize = 20 };
        style.normal.textColor = Color.white;

        string line;
        switch (state)
        {
            case State.Prep:
                line = $"라운드 {currentRound + 1}/{roundsPerStage} — 정비 시간 {prepRemaining:0.0}초"
                     + (HumanUx ? "  (Space: 바로 시작)" : "");
                break;
            case State.Preview:
                line = $"라운드 {currentRound + 1}/{roundsPerStage} — 웨이브 예고 {previewRemaining:0.0}초  (Space: 바로 시작)";
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

        DrawWavePreview();
        DrawSpeedControls();
    }

    // 화면 상단 중앙~우측의 예고 상자. 좌측 열(골드/체력/타워 목록)과 겹치지 않는 x부터 그린다.
    void DrawWavePreview()
    {
        if (state != State.Preview || string.IsNullOrEmpty(pendingWaveText)) return;

        Rect box = new Rect(430f, 8f, 640f, 74f);
        GUI.Box(box, GUIContent.none);

        GUIStyle title = new GUIStyle { fontSize = 19, alignment = TextAnchor.MiddleCenter };
        title.normal.textColor = new Color(1f, 0.85f, 0.35f);
        GUI.Label(new Rect(box.x, box.y + 6f, box.width, 26f),
                  $"다음 웨이브 — {previewRemaining:0.0}초 후 시작", title);

        GUIStyle body = new GUIStyle { fontSize = 16, alignment = TextAnchor.MiddleCenter };
        body.normal.textColor = Color.white;
        GUI.Label(new Rect(box.x + 8f, box.y + 34f, box.width - 16f, 34f), pendingWaveText, body);
    }

    // 배속 + 즉시 시작. 죽은 시간(사거리 밖 적을 기다리는 구간)을 플레이어가 직접 건너뛴다.
    void DrawSpeedControls()
    {
        if (!HumanUx || !allowSpeedControl || speedOptions == null) return;

        for (int i = 0; i < speedOptions.Length; i++)
        {
            GUIStyle bs = new GUIStyle(GUI.skin.button) { fontSize = 15 };
            if (i == speedIndex) bs.normal.textColor = Color.yellow;
            if (GUI.Button(new Rect(1086f + i * 48f, 8f, 44f, 26f), $"{speedOptions[i]:0.#}x", bs))
                SetSpeed(i);
        }

        if (state == State.Prep || state == State.Preview)
        {
            if (GUI.Button(new Rect(1086f, 38f, 140f, 26f), "▶ 바로 시작"))
                skipRequested = true;
        }
    }
}
