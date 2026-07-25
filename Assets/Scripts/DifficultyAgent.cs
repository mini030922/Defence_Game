using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

// 난이도 조절 강화학습 에이전트 (설계상 '역할 C: DDA').
// - 관찰: 게임 상태 11개 + 라운드 진행도 + 현재 강도 + 현재 체력배율 = 14개
// - 행동: 강도 상승폭 / 종류별 가중치 3 / 체력배율 상승폭 (연속값 5개)
// - 보상: 플레이어가 '아슬아슬하게' 클리어할수록 +, 너무 쉽거나 붕괴하면 -
//
// 필요 컴포넌트(인스펙터): Behavior Parameters
//   - Vector Observation > Space Size = 14
//   - Actions > Continuous Actions = 5
//   - Behavior Type = Default(학습) / Inference Only(플레이·스파링) / Heuristic Only(검증)
public class DifficultyAgent : Agent
{
    [Header("연결 (비우면 자동으로 찾음)")]
    public RoundManager roundManager;
    public GameStateCollector collector;

    [Header("보상: 난이도 위치별 라운드당 목표 체력손실 비율")]
    public float easyTargetLoss = 0.05f;    // 최초반(쉬움) — 적 1마리 누수(=5%)와 동일. 이보다 작으면 '0손실=벌'이라 DDA가 난이도를 억지로 올림
    public float hardTargetLoss = 0.14f;    // 최후반(가혹, 완벽해야 겨우 생존 → 합계 ~140%)
    public float difficultyCurve = 1.5f;    // 클수록 어려움이 후반에 몰림(9~10스테이지 급가혹)
    public int maxStageForTarget = 10;

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
    public float maxIntensity = 2f;         // 강도 상한
    public float intensityStep = 0.2f;      // 라운드당 최대 강도 상승폭 (10라운드면 최대 +2.0까지 시도 가능)
    public float maxHealthMultiplier = 2f;  // 체력 배율 상한
    public float healthStep = 0.15f;        // 라운드당 최대 체력 배율 상승폭

    [Header("보상: 타워 가동률 페널티 (놀던 타워 많으면 = 너무 쉬움)")]
    public float minUtil = 0.3f;            // 가동률이 이 값 미만이면 마이너스 보상
    public float utilPenaltyWeight = 0.6f;  // 가동률 0일 때 -이만큼 (0.3 미만 구간에서 선형)

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
        sensor.AddObservation(Mathf.Clamp01(s.gold / 1000f));
        sensor.AddObservation(Mathf.Clamp01(s.stage / 10f));
        sensor.AddObservation(Mathf.Clamp01(s.towerCount / 20f));
        sensor.AddObservation(Mathf.Clamp01(s.totalTowerCost / 3000f));
        sensor.AddObservation(Mathf.Clamp01(s.totalTowerDps / 500f));
        sensor.AddObservation(s.pathCoverage);                          // 0~1
        sensor.AddObservation(Mathf.Clamp01(s.lastRoundLeaked / 20f));
        sensor.AddObservation(Mathf.Clamp01(s.lastRoundHealthLost / 20f));
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
        sensor.AddObservation(Mathf.InverseLerp(1f, maxIntensity, intensity));
        sensor.AddObservation(Mathf.InverseLerp(1f, maxHealthMultiplier, healthMultiplier));
    }

    // 에이전트의 행동을 받아 다음 라운드 난이도 배율로 변환
    public override void OnActionReceived(ActionBuffers actions)
    {
        var a = actions.ContinuousActions;
        // a0: 이번 라운드 강도 상승폭(0~intensityStep), a1~a3: 종류별 가중치, a4: 체력 상승폭
        // → 값 자체가 아니라 '증분'을 정하므로 단조 증가를 지키면서도 매 라운드 행동이 반영된다.
        float upI = Mathf.Clamp01((a[0] + 1f) * 0.5f) * intensityStep;
        intensity = Mathf.Clamp(prevIntensity + upI, 1f, maxIntensity);
        prevIntensity = intensity;

        typeWeights[0] = Mathf.Clamp01((a[1] + 1f) * 0.5f);
        typeWeights[1] = Mathf.Clamp01((a[2] + 1f) * 0.5f);
        typeWeights[2] = Mathf.Clamp01((a[3] + 1f) * 0.5f);

        float upH = Mathf.Clamp01((a[4] + 1f) * 0.5f) * healthStep;
        healthMultiplier = Mathf.Clamp(prevHealth + upH, 1f, maxHealthMultiplier);
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
            reward = 1f - Mathf.Abs(lossFraction - target) / target;

            // 타워 가동률이 minUtil(0.3) 미만이면 마이너스 페널티: 낮을수록 더 큰 벌.
            // 단, 초반(Dc≈0)은 적이 적어 타워가 노는 게 정상 → Dc를 곱해 후반일수록만 세게 문다.
            if (util < minUtil)
                reward -= utilPenaltyWeight * (minUtil - util) / minUtil * Dc;

            reward = Mathf.Clamp(reward, -1f, 1f);
        }
        AddReward(reward);
        Debug.Log($"[보상] S{stage} R{roundNum} D{D:0.00} · 손실 {lossFraction:0.00} 가동률 {util:0.00} died={died} lateN={lateNeutral} → {reward:0.00}");
    }
}
