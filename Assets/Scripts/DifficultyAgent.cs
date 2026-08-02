using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

// 난이도 조절 강화학습 에이전트 (설계상 '역할 C: DDA').
// - 관찰: 기존 14개 + 여유시간·종류별타워5·감속비율·업그레이드레벨5·경로접힘·최대동시적·최대진행도 = 29개
// - 행동: 강도 상승폭 / 종류별 가중치 3 / 체력배율 상승폭 (연속값 5개)
// - 보상: 플레이어가 '아슬아슬하게' 클리어할수록 +, 너무 쉽거나 붕괴하면 -
//
// 필요 컴포넌트(인스펙터): Behavior Parameters
//   - Vector Observation > Space Size = 29
//   - Actions > Continuous Actions = 5
//   - Behavior Type = Default(학습) / Inference Only(플레이·스파링) / Heuristic Only(검증)
public class DifficultyAgent : Agent
{
    [Header("연결 (비우면 자동으로 찾음)")]
    public RoundManager roundManager;
    public GameStateCollector collector;

    [Header("보상: 난이도 위치별 라운드당 목표 체력손실 비율")]
    // ── 2026-07-31 재보정 (plc_v11 실측 환율) ──────────────────────────────
    // 최소제곱 적합: 손실 1단위당 클리어율 -565%p  (plc_v9 -556 / plc_v10 -556 → 기울기 불변 재확인)
    // 다만 **절편은 이동한다** — 플레이어가 강해지면 성적 분산이 줄어 같은 평균손실에서도 더 잘 깬다.
    //   plc_v9  : 손실 0.146 → 클리어 44.4%
    //   plc_v11 : 손실 0.116 → 클리어 66.6%  (같은 0.146이면 약 53%)
    // 새 환율로 역산한 필요 손실: S1 0.065 · S5 0.095 · S8 0.155 · S10 0.233
    // easy 0.05 → 0.065 / hard 0.22 → 0.233 / curve 2.0 → 2.1
    // 예상 곡선 98/98/95/90/82/71/59/43/25/4 — 목표(99/96/90/87/82/74/63/48/28/4)와 전 구간 ±5p

    public float easyTargetLoss = 0.065f;    // 최초반(쉬움) — 적 1마리 누수(=5%)와 동일. 이보다 작으면 '0손실=벌'이라 DDA가 난이도를 억지로 올림
    // 0.14 → 0.22 (2026-07-30). plc_v9 실측으로 '라운드 손실 ↔ 클리어율' 매핑을 확보해 역산했다.
    //   측정: 손실 0.049→클리어 96% / 0.062→91% / 0.071→87% / 0.103→69% / 0.108→65% / 0.146→44%
    //   목표 곡선(S3 90% → S10 4%)에 필요한 손실: S1 0.044 → S10 0.22
    // 이전 0.14는 dda_v6 기준으로는 S10을 0.3%로 만드는 값이었지만, 라운드 경사 완화 후에는
    // 같은 0.14가 S10 클리어율 44%에 해당한다. 목표가 바뀐 게 아니라 게임이 바뀌어서 환율이 달라졌다.
    public float hardTargetLoss = 0.233f;    // 최후반(가혹) — plc_v9 매핑 역산값
    // 1.5 → 2.0 (2026-07-30). 목표 클리어율 곡선을 최소제곱으로 맞춘 값.
    // 1.5는 중반(S5~S7) 목표가 너무 낮아 실제 클리어율이 63~69%에서 평평해졌다.
    // 2.0이면 예상 곡선 96/95/93/88/80/69/55/41/25/5 로, 목표(99/96/90/87/82/74/63/48/28/4)와
    // 전 구간 ±8p 안에 들어온다. (단일 거듭제곱으로는 이보다 더 못 맞춤 — 목표 곡선이 후반에서 더 볼록)
    public float difficultyCurve = 2.1f;      // 클수록 어려움이 후반에 몰림
    public int maxStageForTarget = 10;
    // 목표 손실 판정의 여유폭 — v5(2026-07-28)부터 **비대칭**.
    //
    // 배경: v3에서 대칭 여유폭(0.05)을 넣어 '손실 0 = 0점' 문제를 고쳤는데, 반대 극단으로 갔다.
    //   dda_v4 결과를 실측하니 DDA가 **강도 중앙값 1.00에 굳어 아무것도 안 하고** 일반 적만 보냈다.
    //   이유: "가만히 있으면 손실 0 → +0.47 확정 / 올렸다 넘치면 점수 급락 + 죽으면 에피소드 종료로
    //   남은 라운드 보상까지 상실", 게다가 강도는 단조 증가라 한 번 올리면 되돌릴 수 없다.
    //   → 위험 회피가 기대값상 최적이 되어버렸다.
    //
    // 해법: 부족(너무 쉬움)은 **좁은 분모로 세게**, 초과(너무 어려움)는 **넓은 분모로 관대하게** 채점한다.
    //   "지루하게 만드는 것이 조금 가혹한 것보다 나쁘다"는 DDA의 설계 의도와도 일치한다.
    //
    // 예 (목표 0.056): 손실 0 → 0.47 → **0.26** / 목표 적중 → 1.00 / 손실 0.20 → -0.36 → **-0.06**
    //   시도의 대가가 줄고 가만히 있는 보상이 줄어 "일단 올려보는" 쪽이 유리해진다.
    public float underTolerance = 0.02f;  // 손실 < 목표 (너무 쉬움) 쪽 여유폭 — 좁게
    public float overTolerance = 0.08f;   // 손실 > 목표 (너무 어려움) 쪽 여유폭 — 넓게
    public float lingerRefForObs = 8f;      // '여유 시간' 관측 정규화 기준(초). 이 시간 이상 걸리면 1.0

    [Header("보상: 초반 보호 (1~2스테이지 1~3라운드는 체력 손실 = 큰 벌)")]
    public int earlyProtectStage = 2;         // 이 스테이지 이하에서
    public int earlyProtectRound = 3;         // 이 라운드(1부터) 이하까지 보호
    public float earlyLossPenaltyScale = 18f; // 손실 비율당 벌점 (0손실=+1, 1누수(5%)≈+0.1, 2누수≈-0.8)
    public float earlyDeathPenalty = -2f;     // 초반에 플레이어를 죽이면(최악) 이 값

    [Header("보상: 후반 중립 패배 (5스테이지 5라운드부터 죽어도 ≈0)")]
    public int lateNeutralStage = 5;          // 이 스테이지의
    public int lateNeutralRound = 5;          // 이 라운드(1부터)부터 죽어도 중립
    public float lateDeathReward = 0f;        // 후반 아슬아슬한 패배 = 나쁘지도 좋지도 않게

    [Header("행동: 난이도 상승 폭 (단조 증가는 유지하되 '증분'으로 조절)")]
    // 예전엔 Mathf.Max(원하는값, 직전값)이라 1라운드에 우연히 2.0이 나오면 남은 라운드의
    // 행동이 통째로 무시(검열)됐다 → 행동↔결과 연결이 끊겨 학습 불가.
    // 이제는 '이번 라운드에 얼마나 올릴지(0~step)'를 정하는 방식 → 매 라운드 행동이 반영된다.
    public float maxIntensity = 3f;         // **최고 스테이지**에서의 강도 상한
    public float intensityStep = 0.25f;      // 라운드당 최대 강도 상승폭
    public float maxHealthMultiplier = 2.5f;  // **최고 스테이지**에서의 체력 배율 상한
    public float healthStep = 0.25f;        // 라운드당 최대 체력 배율 상승폭

    [Header("행동: 스테이지별 난이도 상한 (GDD의 '1~3스테이지는 쉬움'을 구조로 보장)")]
    // 예전엔 1스테이지에서도 DDA가 쓸 수 있는 범위가 10스테이지와 똑같이 1.0~2.0이었다.
    // 강도는 단조 증가라 한 번 올리면 못 내리는데, 강한 플레이어를 만나면 상한까지 밀어붙인 뒤
    // 그대로 굳어서 초반 스테이지조차 6~7라운드부터 갑자기 못 깨지게 됐다.
    // → DDA가 쥔 칼 자체를 스테이지에 비례시켜 천장을 만든다.
    //   1스테이지 최대 1.30×1.20 = 1.56배 / 10스테이지 최대 2.00×2.00 = 4.00배
    public float easyMaxIntensity = 1.3f;        // 1스테이지에서의 강도 상한
    public float easyMaxHealthMultiplier = 1.2f; // 1스테이지에서의 체력 배율 상한

    // 현재 스테이지에서 실제로 허용되는 상한 (관측 정규화에도 같은 값을 쓴다)
    float StageNorm01 => Mathf.Clamp01((GameManager.stage - 1) / (float)Mathf.Max(1, maxStageForTarget - 1));
    public float CurMaxIntensity => Mathf.Lerp(easyMaxIntensity, maxIntensity, StageNorm01);
    public float CurMaxHealthMultiplier => Mathf.Lerp(easyMaxHealthMultiplier, maxHealthMultiplier, StageNorm01);

    [Header("보상: 타워 가동률 페널티 (놀던 타워 많으면 = 너무 쉬움)")]
    public float minUtil = 0.3f;            // 가동률이 이 값 미만이면 마이너스 보상
    public float utilPenaltyWeight = 0.6f;  // 가동률 0일 때 -이만큼 (0.3 미만 구간에서 선형)

    [Header("스테이지 결과 보상 (2026-08-01 신설) — 이 프로젝트에서 가장 중요한 수정")]
    // 문제: 보상 함수에 **'몇 %가 클리어해야 하는가'에 대한 신호가 아예 없었다.**
    //   라운드별 손실만 채점하니 DDA 입장에서 생존/사망이 거의 무차별했고, 근소하게 생존이 유리했다.
    //   실측(plc_v12, 21,593판): S10 생존 판 총보상 4.92 vs 사망 판 4.82 → 살려두는 쪽이 이긴다.
    //   그 결과 S10 사망률이 39%에 머물렀다(목표 96%). 상한을 3.0까지 열어줘도 안 쓴 진짜 이유.
    //
    // 처방: 스테이지가 끝날 때 **결과 자체를 채점**한다.
    //   결과 점수 = 진행도(클리어 1.0 / 사망 시 도달라운드÷10) + 남은 체력비율   → 0~2
    //     · R3 사망 0.30 · R10까지 갔다 사망 1.00 · 클리어+체력30% 1.30 · 클리어+체력65% 1.65
    //   목표 점수 = 목표 클리어율에서 역산 → 스테이지에 **선형**으로 떨어진다(곡선 지수 불필요).
    //     S1 1.65(여유롭게 클리어) → S10 0.95(거의 항상 R9~R10에서 패배)
    //
    // 이로써 "아슬아슬하게 지게 만든다"는 원래 설계 의도가 처음으로 보상에 직접 들어간다.
    // ── 2026-08-01 v2 재설계 ────────────────────────────────────────────
    // dda_v10 실측에서 v1 척도의 **치명적 결함**이 드러났다.
    //   v1: 클리어 = 1.0 + 남은체력 / 사망 = 도달라운드÷10
    //   → "체력 0으로 간신히 클리어"와 "R10에서 사망"이 **둘 다 1.0**이다.
    //   즉 DDA는 목표 점수를 **아무도 죽이지 않고 체력만 깎아서** 맞출 수 있었다.
    //   실측: S3~S5 클리어율이 99.6/99.9/99.6%인데 점수는 1.44/1.36/1.28로 제각각 —
    //         DDA가 클리어율 채널은 건드리지 않고 체력 채널로만 목표를 맞추고 있었다.
    //
    // v2: **사망을 음수로 분리**한다.
    //   클리어 = 1.0 + 남은체력  (1.0 ~ 2.0)
    //   사망   = -(1 - 도달라운드÷총라운드)   (R1 -0.9 ~ R10 0.0)
    //   → 평균 점수를 1.0 아래로 내리려면 **반드시 사망이 발생해야 한다.**
    //     체력만 깎는 우회로가 원천 차단된다. 이게 v2의 핵심.
    //   부수 효과로 목표 구간 폭이 0.33 → 1.24로 넓어져 톨러런스를 좁힐 여지도 생긴다.
    //
    // ※ 주의: 통계적 정보량 자체는 늘지 않는다(클리어/사망은 어차피 이항 사건).
    //   개선되는 것은 **보상의 기울기가 클리어율 채널을 직접 가리키게 된다**는 점이다.
    public float outcomeTolerance = 0.20f;     // 이만큼 어긋나면 0점 (새 척도 기준, 약 15%p)
    public float stageOutcomeWeight = 2f;      // 결과 보상 가중
    public float outcomeMinReward = -1.5f;     // 정규화 하한 (× weight → 최대 -3.0)

    // 목표 결과 점수 — 목표 클리어율에서 **실측 역산**한 값 (수식 적합이 아니라 표).
    //   score(p) = p × (1 + h(p)) + (1-p) × (사망 점수 ≈ -0.05),  h(p) = 0.10 + 0.15p
    //   h는 dda_v10에서 스테이지별 '클리어 시 남은 체력'을 역산해 얻은 관계다.
    //   목표 클리어율   99  96  90  87  82  74  63  48  28   4 (%)
    static readonly float[] TargetScoreTable =
        { 1.24f, 1.19f, 1.11f, 1.06f, 0.99f, 0.88f, 0.73f, 0.54f, 0.28f, 0.00f };

    // 행동 결과 (RoundManager가 읽어서 다음 라운드에 적용)
    [HideInInspector] public float intensity = 1f;               // 총 위협 강도 (단조 증가)
    [HideInInspector] public float[] typeWeights = new float[3]; // 종류별 배분 가중치 [일반,빠름,탱커]
    [HideInInspector] public float healthMultiplier = 1f;        // 적 체력 배율 (단조 증가)

    private float prevIntensity = 1f;   // 직전 라운드 강도 (단조 증가 보장용)
    private float prevHealth = 1f;

    public override void Initialize()
    {
        if (roundManager == null) roundManager = Object.FindFirstObjectByType<RoundManager>();
        if (collector == null) collector = Object.FindFirstObjectByType<GameStateCollector>();
    }

    // 새 에피소드(= 스테이지 한 판) 시작 → 게임을 리셋하고 라운드 루프 시작
    public override void OnEpisodeBegin()
    {
        // 난이도(강도·체력)는 스테이지 내에서 단조 증가 → 에피소드 시작 시 초기화
        intensity = 1f; healthMultiplier = 1f;
        prevIntensity = 1f; prevHealth = 1f;
        for (int i = 0; i < typeWeights.Length; i++) typeWeights[i] = 1f; // 균등 시작
        if (roundManager != null)
            roundManager.StartEpisode();
    }

    // 에이전트가 보는 관찰값 (9개, 대략 0~1로 정규화)
    public override void CollectObservations(VectorSensor sensor)
    {
        GameState s = (collector != null) ? collector.Collect() : default;
        sensor.AddObservation(s.playerHealthRatio);                     // 0~1
        sensor.AddObservation(Mathf.Clamp01(s.gold / 3000f));
        sensor.AddObservation(Mathf.Clamp01(s.stage / 10f));
        sensor.AddObservation(Mathf.Clamp01(s.towerCount / 30f));
        sensor.AddObservation(Mathf.Clamp01(s.totalTowerCost / 10000f));
        // 500 → 2500 (2026-08-01). 봇 S10 실측 DPS가 474로 이미 천장에 붙어 있었다.
        // 사람은 2000+ 이 나오는데 DDA 눈에는 500과 구분이 안 됐다 = 적응할 정보가 없었다.
        sensor.AddObservation(Mathf.Clamp01(s.totalTowerDps / 2500f));
        sensor.AddObservation(s.pathCoverage);                          // 0~1
        sensor.AddObservation(Mathf.Clamp01(s.lastRoundLeaked / 20f));
        sensor.AddObservation(Mathf.Clamp01(s.lastRoundHealthLost / 60f)); // 체력 최대가 100인데 20에서 잘리고 있었다
        sensor.AddObservation(s.towerUtilization);              // 0~1 (타워 평균 가동률: 낮으면 놀던 타워 많음=쉬움)
        sensor.AddObservation(Mathf.Clamp01(s.pathLength / 100f)); // 경로 칸 수 (길수록 쉬운 맵)

        // ── 아래 3개는 보상과 직결된 필수 관찰 ────────────────────────────
        // (1) 라운드 진행도: GradeRound의 난이도 위치 D가 라운드에 절반(0.5) 의존한다.
        //     이걸 안 보여주면 "똑같아 보이는 상태에서 어떤 땐 +1, 어떤 땐 -1"이 되어 수렴 불가.
        float roundNorm = (roundManager != null && roundManager.RoundsPerStage > 1)
            ? Mathf.Clamp01(roundManager.CurrentRound / (float)(roundManager.RoundsPerStage - 1))
            : 0f;
        sensor.AddObservation(roundNorm);
        // (2)(3) 자기가 지금까지 올려둔 난이도. 라운드를 넘어 누적되는 상태라 관찰이 없으면 부분관측.
        //        (CollectObservations는 OnActionReceived보다 먼저 불리므로 '직전 라운드까지의 값'이다)
        // (상한이 스테이지마다 다르므로 '지금 스테이지의 상한 대비 어디까지 올렸나'로 정규화한다)
        sensor.AddObservation(Mathf.InverseLerp(1f, CurMaxIntensity, intensity));
        sensor.AddObservation(Mathf.InverseLerp(1f, CurMaxHealthMultiplier, healthMultiplier));

        // ── 관측 보강 15개 (2026-07-26) — 14 → 29 ──────────────────────────
        // (15) 직전 라운드 '여유': 스폰 완료 후 소탕까지 걸린 시간.
        //      체력 손실은 5% 단위라 해상도가 낮고 '손실 0'인 두 판을 구분 못 한다.
        //      압살한 판과 골 앞에서 간신히 잡은 판이 지금까지 DDA 눈에 똑같았다. '아슬아슬'의 직접 측정치.
        sensor.AddObservation(Mathf.Clamp01(s.lastRoundLinger / lingerRefForObs));

        // (16~20) 종류별 타워 수 — 플레이어 '구성'을 알아야 반례 적(탱커/물량)을 고를 수 있다.
        //         지금까지는 총 타워 수와 총 DPS만 봐서, 종류별 가중치 행동 3개가 사실상 눈감고 쏘는 셈이었다.
        for (int i = 0; i < 5; i++)
        {
            int c = (s.towerCountByType != null && i < s.towerCountByType.Length) ? s.towerCountByType[i] : 0;
            sensor.AddObservation(Mathf.Clamp01(c / 10f));
        }

        // (21) 감속 비율 — 지원 타워의 간접 기여. 적이 사거리에 머무는 시간을 직접 바꾸는 변수.
        sensor.AddObservation(Mathf.Clamp01(s.lastRoundSlowRatio));

        // (22~26) 종류별 업그레이드 레벨 — 같은 DPS라도 '적은 수+고레벨'과 '많은 수+0레벨'은 다른 상대다.
        for (int i = 0; i < 5; i++)
        {
            int lv = (s.upgradeLevelByType != null && i < s.upgradeLevelByType.Length) ? s.upgradeLevelByType[i] : 0;
            sensor.AddObservation(Mathf.Clamp01(lv / 5f));
        }

        // (27) 경로 접힘 정도 — 같은 40칸이어도 일직선 맵과 뱀형 맵은 체감 난이도가 완전히 다르다.
        sensor.AddObservation(Mathf.Clamp01(s.pathFoldRatio));

        // (28~29) 직전 라운드 최대 동시 생존 적 수 / 적이 도달한 최대 경로 진행도.
        //         안 샜어도 골 앞까지 갔는지 = 얼마나 아슬아슬했는지.
        sensor.AddObservation(Mathf.Clamp01(s.lastRoundMaxAlive / 30f));
        sensor.AddObservation(Mathf.Clamp01(s.lastRoundMaxProgress));
    }

    // 에이전트의 행동을 받아 다음 라운드 난이도 배율로 변환
    public override void OnActionReceived(ActionBuffers actions)
    {
        var a = actions.ContinuousActions;
        // a0: 이번 라운드 강도 상승폭(0~intensityStep), a1~a3: 종류별 가중치, a4: 체력 상승폭
        // → 값 자체가 아니라 '증분'을 정하므로 단조 증가를 지키면서도 매 라운드 행동이 반영된다.
        // 2026-07-31 — 스텝을 **고정값에서 '가용 범위의 비율'로** 바꿨다.
        // dda_v8 실측: S7~S10이 전부 자기 스테이지 상한을 정확히 찍고 있었다(S10은 16%의 라운드가 2.0).
        // 상한을 올려도 고정 스텝(0.2)으로는 10라운드 × 0.2 = +2.0이 물리적 최대라
        // 새 상한 3.0에 닿으려면 매 라운드 최대치를 눌러야만 한다 = 사실상 도달 불가.
        // 비율 방식이면 상한을 바꿔도 "몇 라운드 만에 상한에 닿는가"가 스테이지와 무관하게 일정해진다.
        float rangeI = Mathf.Max(0f, CurMaxIntensity - 1f);
        float upI = Mathf.Clamp01((a[0] + 1f) * 0.5f) * intensityStep * rangeI;
        intensity = Mathf.Clamp(prevIntensity + upI, 1f, CurMaxIntensity);
        prevIntensity = intensity;

        typeWeights[0] = Mathf.Clamp01((a[1] + 1f) * 0.5f);
        typeWeights[1] = Mathf.Clamp01((a[2] + 1f) * 0.5f);
        typeWeights[2] = Mathf.Clamp01((a[3] + 1f) * 0.5f);

        float rangeH = Mathf.Max(0f, CurMaxHealthMultiplier - 1f);
        float upH = Mathf.Clamp01((a[4] + 1f) * 0.5f) * healthStep * rangeH;
        healthMultiplier = Mathf.Clamp(prevHealth + upH, 1f, CurMaxHealthMultiplier);
        prevHealth = healthMultiplier;

        Debug.Log($"[난이도] 강도 {intensity:0.00}(+{upI:0.00}) · 가중치 [{typeWeights[0]:0.0}/{typeWeights[1]:0.0}/{typeWeights[2]:0.0}] · 체력 {healthMultiplier:0.00}(+{upH:0.00})");
    }

    // 학습 없이(사람이 플레이) 검증할 때 쓰는 기본 행동: 난이도 안 올림(상승폭 0)
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        ActionSegment<float> c = actionsOut.ContinuousActions;
        c[0] = -1f;                          // 강도 상승폭 0 → 1.0 유지
        c[1] = 0f; c[2] = -1f; c[3] = -1f;   // 일반 위주 배분
        c[4] = -1f;                          // 체력 상승폭 0 → 1.0 유지
    }

    // RoundManager가 라운드 종료 시 호출 → 아슬아슬함을 보상으로 환산
    public void GradeRound(float lossFraction, bool died, int roundIndex, int totalRounds)
    {
        // 난이도 위치 D: 스테이지·라운드 진행을 합침 (0=최초반, 1=최후반)
        int stage = GameManager.stage;
        int roundNum = roundIndex + 1; // 1부터 시작하는 라운드 번호
        float stageNorm = Mathf.Clamp01((stage - 1) / (float)Mathf.Max(1, maxStageForTarget - 1));
        float roundNorm = (totalRounds > 1) ? Mathf.Clamp01(roundIndex / (float)(totalRounds - 1)) : 1f;
        float D = 0.5f * stageNorm + 0.5f * roundNorm;
        float Dc = Mathf.Pow(D, difficultyCurve); // 어려움을 후반에 몰아주는 곡선

        // 이번 라운드 타워 평균 가동률 (낮으면 놀던 타워 많음 = 너무 쉬웠다는 신호)
        float util = (collector != null) ? collector.lastRoundTowerUtilization : 1f;

        // ── 구간 1: 초반 보호 (1~2스테이지의 1~3라운드) ───────────────
        // 플레이어가 게임을 배우는 구간. 여기서 체력을 깎을수록 큰 벌 → DDA가
        // 초반 난이도를 함부로 못 올리게 한다(강도 1 근처 유지 유도).
        //   0손실 = +1(완벽), 손실이 커질수록 급하강, 죽이면 최악(earlyDeathPenalty).
        if (stage <= earlyProtectStage && roundNum <= earlyProtectRound)
        {
            float r = died
                ? earlyDeathPenalty
                : Mathf.Clamp(1f - lossFraction * earlyLossPenaltyScale, earlyDeathPenalty, 1f);
            AddReward(r);
            Debug.Log($"[보상:초반보호] S{stage} R{roundNum} 손실 {lossFraction:0.00} died={died} → {r:0.00}");
            return;
        }

        // ── 구간 3: 후반 중립 패배 (5스테이지 5라운드부터) ──────────────
        // 이 지점부터는 '거의 못 이기는' 설계 구간이라 죽어도 벌하지 않는다(≈0).
        bool lateNeutral = (stage > lateNeutralStage) ||
                           (stage == lateNeutralStage && roundNum >= lateNeutralRound);

        // ── 구간 2: 중반 (그 사이) — 아슬아슬함을 목표 손실로 채점 ────────
        float reward;
        if (died)
        {
            // 후반 중립 구간이면 죽어도 0, 그 전 중반은 약한 벌(후반으로 갈수록 0에 수렴)
            reward = lateNeutral ? lateDeathReward : Mathf.Lerp(-1f, 0f, Dc);
        }
        else
        {
            // 목표 손실도 곡선에 따라: 중반 살살(easy=1누수), 후반 급가혹(hard)
            float target = Mathf.Lerp(easyTargetLoss, hardTargetLoss, Dc);
            float diff = lossFraction - target;
            // 부족(너무 쉬움)은 좁은 분모로 세게, 초과(너무 어려움)는 넓은 분모로 관대하게
            float denom = Mathf.Max(0.01f, target + (diff < 0f ? underTolerance : overTolerance));
            reward = 1f - Mathf.Abs(diff) / denom;

            // 타워 가동률이 minUtil(0.3) 미만이면 마이너스 페널티: 낮을수록 더 큰 벌.
            // 단, 초반(Dc≈0)은 적이 적어 타워가 노는 게 정상 → Dc를 곱해 후반일수록만 세게 문다.
            if (util < minUtil)
                reward -= utilPenaltyWeight * (minUtil - util) / minUtil * Dc;

            reward = Mathf.Clamp(reward, -1f, 1f);
        }
        AddReward(reward);
        Debug.Log($"[보상] S{stage} R{roundNum} D{D:0.00} · 손실 {lossFraction:0.00} 가동률 {util:0.00} died={died} lateN={lateNeutral} → {reward:0.00}");
    }

    // ── 스테이지 종료 채점 ────────────────────────────────────────────
    // RoundManager가 클리어/사망 시점에 딱 한 번 호출한다.
    // 라운드별 손실 보상이 '과정'을 채점한다면, 이건 '결과'를 채점한다.
    public void GradeStageEnd(bool cleared, int roundReached, int totalRounds, float healthLeftRatio)
    {
        int stage = GameManager.stage;
        float stageNorm = Mathf.Clamp01((stage - 1) / (float)Mathf.Max(1, maxStageForTarget - 1));

        // v2 척도: 클리어와 사망 사이에 간격을 둬서 '체력만 깎기' 우회로를 막는다.
        float score = cleared
            ?  1f + Mathf.Clamp01(healthLeftRatio)                                   //  1.0 ~ 2.0
            : -(1f - Mathf.Clamp01(roundReached / (float)Mathf.Max(1, totalRounds))); // -0.9 ~ 0.0

        int row = Mathf.Clamp(stage - 1, 0, TargetScoreTable.Length - 1);
        float target = TargetScoreTable[row];
        float norm = 1f - Mathf.Abs(score - target) / Mathf.Max(0.01f, outcomeTolerance);
        norm = Mathf.Clamp(norm, outcomeMinReward, 1f);
        float reward = stageOutcomeWeight * norm;

        AddReward(reward);
        Debug.Log($"[결과] S{stage} {(cleared ? "클리어" : $"패배R{roundReached}")} " +
                  $"점수 {score:0.00} 목표 {target:0.00} → {reward:0.00}");
    }
}
