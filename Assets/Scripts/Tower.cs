using UnityEngine;

// 사거리 안의 적을 탐지해 투사체로 공격하는 타워.
// (기존 필드 이름/구조는 그대로 유지 — 프리팹 값 호환)
public class Tower : MonoBehaviour
{
    [Header("기본 정보")]
    public string towerName;
    public bool targetArea;          // true면 광역(스플래시) 공격
    public bool targetHighestHealth; // true면 사거리 안 '최고 체력' 적을 조준(미사일=보스 저격)
    public float shootingSpeed;      // 공격 간격(초): n이면 n초에 1번
    public float damage;
    public float minRange;
    public float maxRange;      // '몇 칸'이라는 개념값. 배치 AI·UI가 쓰는 기준은 이 숫자 그대로.
    public int cost;

    [Header("사거리 보정")]
    // 타일은 중심 기준 ±0.5칸을 차지한다. 판정을 maxRange로 자르면 n번째 칸의 '중심까지'만 닿아서
    // 그 칸의 바깥 절반(n ~ n+0.5)이 사각지대가 된다. 실제 타격 판정만 +0.5 해서 n번째 칸을 온전히 덮는다.
    public float rangeBonus = 0.5f;
    public float AttackRange => maxRange + rangeBonus;

    // 보상·관측의 '경로 커버리지' 계산에만 쓰는 사거리 상한. 조준(FindTarget)은 AttackRange 그대로 사용.
    // 미사일 타워는 maxRange가 999(맵 전체 최고체력 저격)라 그대로 커버리지에 쓰면:
    //   - 맵 어디에 지어도 경로 전체를 덮는 것으로 계산돼 거리 가중이 무의미해지고
    //   - 미사일 하나만 지으면 그 뒤 모든 타워의 배치 보상이 0이 되고 (전부 '이미 덮인 곳' 처리)
    //   - uselessTowerPenalty가 미사일엔 절대 안 걸리고
    //   - GameStateCollector의 경로커버 관측이 항상 1.0으로 고정된다.
    public float coverageRangeCap = 8f;
    public float CoverageRange => Mathf.Min(AttackRange, coverageRangeCap);

    // 이 타워가 실제로 가동을 시작한 시각 (전투 중에 지어진 타워의 가동률을 공정하게 계산하기 위함)
    [System.NonSerialized] public float activeSince;

    // 이 타워를 지을 때 **실제로 낸 골드** (판매 환급의 기준).
    // cost는 프리팹의 기본가라, 가격 상승(전체 +10%/개 · 같은 종류 +12%/개 · 스테이지 +12%)이 빠져 있다.
    // 기본가로 환급하면 후반에 지은 비싼 타워를 팔 때 손해가 과도해진다.
    [System.NonSerialized] public int paidCost;

    // 직전 라운드에 이 타워 '개별'의 가동률 (0~1). RoundManager가 라운드 종료 시 채운다.
    // 평균 가동률만으로는 '12개가 고르게 70%'와 '3개가 완전히 놀고 9개가 풀가동'을 구분 못 해서,
    // 배치 AI가 어느 자리가 실패였는지 알 방법이 없었다.
    [System.NonSerialized] public float lastRoundUtil;

    [Header("광역 공격 (targetArea가 true일 때만 사용)")]
    public float splashRadius = 1.5f;

    // ── 착탄 이펙트 (2026-08-08 신설) ────────────────────────────────────
    // 광역 타워(폭탄·지원)가 착탄할 때 쓸 VFX 프리팹. 비워두면 기존 반투명 원판이 나온다.
    //
    // 여기(타워)에 두는 이유: 투사체는 Tower.FireProjectile이 CreatePrimitive로 코드 생성해서
    // 인스펙터에서 참조를 꽂을 자리가 없다. 타워는 실제 프리팹 에셋이라 꽂을 수 있다.
    // 발사 시점에 이 설정을 투사체에 건네주고, 투사체가 착탄할 때 그대로 쓴다.
    //
    // ※ 단일 타겟 타워(일반·속사)는 targetArea가 false라 이 값을 쓰지 않는다.
    //   (Projectile.Impact의 area 분기 안에서만 소환된다)
    [Header("착탄 이펙트 — 비우면 기존 반투명 원판")]
    public ImpactEffectSettings impactEffect = new ImpactEffectSettings();

    [Header("감속 (지원 타워용 — slowFactor<1이면 명중한 적을 감속)")]
    public float slowFactor = 1f;      // 1=감속 없음, 0.5=속도 절반
    public float slowDuration = 2.5f;  // 한 번 맞으면 이만큼만 느려짐(초). 이후 회복(재감속 잠금)

    [Header("투사체")]
    public float projectileSpeedFactor = 6f; // 투사체 속도 = factor / 공격간격 (공격속도에 비례)
    public float minProjectileSpeed = 4f;
    public Color projectileColor = new Color(0.1f, 0.1f, 0.12f);

    // ── 투사체 모델 (2026-08-09 신설) ────────────────────────────────────────
    // 투사체는 Tower.FireProjectile이 CreatePrimitive(Sphere)로 **코드 생성**한다.
    // 프리팹이 아니라 인스펙터에서 꽂을 자리가 없었다 — 착탄 이펙트와 같은 상황이다.
    // 여기에 슬롯을 두면 타워가 프리팹이므로 **종류마다 자연히 다른 투사체**가 된다.
    // 비워두면 예전 그대로 검은 구슬이 나간다.
    [Header("투사체 모델 — 비우면 기존 검은 구슬")]
    public GameObject projectilePrefab;
    public float projectileScale = 1f;          // 프리팹 원본 배율에 곱해진다
    public Transform muzzle;                    // 발사 위치. 무기의 자식으로 두면 포신에서 나간다
    public bool projectileFacesTravel = true;   // 진행 방향을 바라보게 (화살·볼트에 필요)
    public Vector3 projectileEulerOffset;       // 모델 기본 방향 보정 (화살이 옆을 보면 여기서 돌린다)
    public float projectileSpin = 0f;           // 자체 회전 속도(도/초). 0 = 안 돎. 바위는 굴러가게
    public Vector3 projectileSpinAxis = new Vector3(1f, 0f, 0f); // 로컬 축. (1,0,0) = 앞구르기
    public bool tintProjectile = false;         // 프리팹 위에 projectileColor를 덮을지

    [Header("업그레이드 (종류 단위: TowerUpgradeManager가 레벨 관리)")]
    public int upgradeLevel = 0;
    // 2026-08-02 — 최대 레벨 5 → 8 확장에 맞춰 레벨당 배율 하향.
    //   설계 의도: **만렙까지 가는 데 돈은 더 들지만 만렙 자체는 더 약하게.**
    //   목표는 "새 만렙(Lv8) DPS = 옛 만렙(Lv5) DPS × 0.7". 거기서 역산했다.
    //     공격력형: 1.40^5 = 5.378배 → 목표 3.765배 → 3.765^(1/10) = 1.142 → +14.2%/레벨
    //     연사형  : 1.33^5 = 4.162배 → 목표 2.913배 → 2.913^(1/10) = 1.113 → +11.3%/레벨
    //   2026-08-02 2차: 최대 레벨 8 → 10 으로 늘리며 같은 끝점(0.7배)을 유지하도록 재역산.
    //   업그레이드 비용 공식(upgradeCostFactor 1.5 + 0.6×레벨)은 그대로 두었으므로,
    //   만렙까지 총비용은 기본가 13.5배 → 28.8배(2.13배)가 되고 화력은 0.70배가 된다.
    //   = **DPS당 비용이 3.05배**. 강화 일변도 전략을 누르고 후반 골드 소비처를 넓히는 것이 목적.
    public float damageUpPerLevel = 0.142f; // 레벨당 공격력 +14.2%
    public float rateUpPerLevel = 0.113f;  // 레벨당 공격속도(연사) +11.3%
    public bool forceRateUpgrade = false;  // true면 광역이어도 업그레이드가 공격속도↑ (지원 타워)

    private float fireTimer = 0f;
    [System.NonSerialized] public int shotsThisRound = 0; // 이번 라운드 발사 수 (가동률 계산용)

    // 업그레이드 재계산의 기준이 되는 원본 스탯 (프리팹 값)
    private float baseDamage;
    private float baseShootingSpeed;
    private bool baseCaptured = false;

    // 플레이어 화력 배율 (2026-08-01 신설). 1.0 = 기본.
    //  · 계측용: 낮추면 게임이 어려워진다. 사람이 어디서 피를 흘리기 시작하는지 이분탐색으로 잰다.
    //  · 학습용: 에피소드마다 무작위로 바꿔 DDA가 '상대 실력'을 관측으로 읽도록 강제한다.
    // 지금까지 DDA는 단 하나의 상대(Placement_v12)만 겪어서 그 실력에 과적합돼 있었다.
    public static float powerScale = 1f;

    // ── 조준 회전 (2026-08-09 신설) ──────────────────────────────────────────
    // 그동안 타워는 적을 찾아 투사체만 쏘고 **모델은 가만히 있었다.**
    // 포탑이 적을 따라 도는 것은 타워 디펜스에서 사실상 필수 피드백이라 이것부터 넣는다.
    //
    // 프리팹 구조: TowerModel 아래에 [몸체]와 [무기]를 따로 두고, 무기 Transform을 여기에 꽂는다.
    // 비워두면 회전 없이 예전과 완전히 같게 동작한다.
    [Header("조준 회전 — weaponPivot을 비우면 회전 안 함")]
    public Transform weaponPivot;
    public float weaponYawOffset = 0f;      // 무기 모델의 '기본으로 바라보는 방향' 보정 (90° 단위)
    public float turnSpeed = 540f;          // 초당 회전 각도
    public float retargetInterval = 0.15f;  // 대상을 다시 찾는 간격 — FindObjectsByType이 비싸서 필요

    // ── 발사 후 회전 정지 (2026-08-09 신설) ─────────────────────────────────
    // 발사 **직후** 이 시간 동안 무기를 쏜 방향에 고정한다. 반동을 버티며 탄착을 지켜보는 느낌.
    //
    // 이게 없으면 대포가 쏘자마자 다음 적 쪽으로 홱 돌아버려서 무게감이 사라진다.
    // 느리고 무거운 타워(폭탄 1.9초)일수록 효과가 크고, 빠른 타워(속사 0.2초)엔 넣으면 안 된다.
    // → 프리팹마다 따로 정하는 값이라 기본은 0(끔). 폭탄만 0.35~0.5 정도 주면 된다.
    //
    // ※ 발사는 이 정지와 무관하게 쿨다운대로 나간다. 순전히 연출용 손잡이다.
    public float postFireHold = 0f;

    // ── 조준 판정 (2026-08-09 재설계) ───────────────────────────────────
    // 순간이동(스냅) 없이 자연스럽게 만들기 위한 값들.
    //   · 대충이라도 대상 쪽을 보고 있으면 쏜다 (aimTolerance)
    //   · 각도가 크면 잠깐 기다리되, 무한정은 아니다 (maxAimWait)
    //   · 기다린 시간은 fireTimer에 '+=' 로 상쇄되어 **장기 발사 수가 보존된다**
    public float aimTolerance = 15f;   // 이 각도 안이면 '보고 있다'로 인정
    public float maxAimWait = 0.35f;   // 조준을 기다려 줄 최대 시간. 공격 간격의 절반으로도 잘린다

    // ── 동급 대상 중 '덜 도는 쪽' 고르기 ────────────────────────────────
    // 거의 같은 거리의 적이 양쪽에 있으면 포신이 좌우로 계속 휘둘린다.
    // 최우선 대상과 이 범위 안이면 '동급'으로 보고, 그중 회전량이 가장 작은 적을 고른다.
    // 지금 보고 있는 적은 회전각이 0에 가까워 자연히 유지되므로, 별도 고정 로직이 필요 없다.
    public float tieBandDistance = 0.8f;     // 최근접 대상과 이 거리(칸) 안이면 동급
    [Range(0.1f, 1f)] public float tieBandHealthRatio = 0.9f; // 최고체력의 이 비율 이상이면 동급
    // 정지가 공격 간격에 비해 너무 길면 무기가 다음 발까지 새 대상 쪽으로 못 돈다.
    // 공격 간격의 이 비율을 넘지 않게 잘라서, 회전할 시간이 항상 남게 한다.
    [Range(0.1f, 0.9f)] public float postFireHoldMaxRatio = 0.6f;

    private float holdRotationUntil;   // 이 시각까지는 무기를 돌리지 않는다

    // ── 강화 시각화 (2026-08-09 신설) ────────────────────────────────────────
    // 강화는 종류 단위 최대 Lv10이고 플레이어의 **유일한 성장 레버**인데,
    // 지금까지 화면에는 아무 표시가 없었다 — Lv0과 Lv8 타워가 똑같이 생겼다.
    //
    // 프리팹 안에 파츠를 미리 배치해 두고 코드는 켜고 끄기만 한다(Instantiate 없음).
    // 위치·크기를 손으로 잡을 수 있어 조립 결과가 정확하고, 런타임 비용도 0이다.
    [Header("강화 시각화 — 프리팹 안 자식을 레벨에 따라 켠다")]
    public GameObject[] upgradeParts;       // 앞에서부터 하나씩 켜진다 (아래→위 순서로 배치할 것)
    public float weaponLiftPerPart = 0f;    // 파츠가 켜질 때마다 무기를 이만큼 올린다 (0 = 안 올림)

    private Enemy currentTarget;
    private float retargetTimer;
    private Vector3 weaponBaseLocalPos;
    private bool weaponBaseCaptured;

    void Awake()
    {
        CaptureBase();
        CaptureWeaponBase();
        ApplyStats();
        activeSince = Time.time;
    }

    void CaptureBase()
    {
        if (baseCaptured) return;
        baseDamage = damage;
        baseShootingSpeed = shootingSpeed;
        baseCaptured = true;
    }

    void CaptureWeaponBase()
    {
        if (weaponBaseCaptured || weaponPivot == null) return;
        weaponBaseLocalPos = weaponPivot.localPosition;
        weaponBaseCaptured = true;
    }

    void Update()
    {
        if (shootingSpeed <= 0f) return; // 간격 0 이하면 공격 안 함

        UpdateTarget();
        AimWeapon();

        fireTimer -= Time.deltaTime;
        if (fireTimer > 0f) return;

        // ── 대상이 없을 때 (2026-08-09 수정) ──────────────────────────────
        // 예전에는 여기서 fireTimer를 통째로 리셋했다(원본 코드부터 그랬다).
        // 그러면 **사거리가 빈 채로 쿨다운이 끝났을 때 쿨다운이 헛돈다** —
        // 폭탄(1.9초) 기준으로, 쿨다운이 끝난 0.1초 뒤에 적이 들어오면 1.8초를 더 기다렸다.
        // 플레이어 눈에는 "준비된 타워가 왜 안 쏘지"로 보인다.
        //
        // 0으로 두면 '장전된 채 대기' 상태가 되어, 적이 들어오는 즉시 발사한다.
        // (다음 프레임에 음수가 되지만 여기서 다시 0으로 잡히므로 값이 흘러내리지 않는다)
        if (currentTarget == null)
        {
            fireTimer = 0f;
            return;
        }

        // ── 조준 게이트 ───────────────────────────────────────────────────
        // '대충 보고 있고 + 쿨다운이 끝났으면' 발사한다.
        // 아직 크게 어긋나 있으면 잠깐 기다리되, 무한정 막지는 않는다 —
        // 대기 상한은 maxAimWait과 '공격 간격의 절반' 중 작은 쪽이라
        // 속사(0.2초)처럼 빠른 타워는 자동으로 거의 안 기다린다.
        bool aimed = IsAimedAt(currentTarget);
        if (weaponPivot != null && !aimed &&
            fireTimer > -Mathf.Min(maxAimWait, shootingSpeed * 0.5f))
            return;

        Fire(currentTarget, aimed);

        // '=' 가 아니라 '+=' 인 이유: fireTimer는 프레임 단위로 조금씩 음수로 넘어간다.
        // 더해 주면 그 초과분이 다음 주기에서 상쇄되어 장기 발사 수가 정확히 유지된다.
        fireTimer += shootingSpeed;
        if (fireTimer < 0f) fireTimer = 0f;
    }

    // 조준 대상 갱신. 매 프레임 FindTarget을 돌리면 FindObjectsByType이 너무 비싸므로,
    // 대상이 죽거나 사거리를 벗어났을 때 + 일정 간격마다만 다시 찾는다.
    void UpdateTarget()
    {
        retargetTimer -= Time.deltaTime;
        bool stale = currentTarget == null || !InRange(currentTarget);
        if (stale || retargetTimer <= 0f)
        {
            currentTarget = FindTarget();
            retargetTimer = Mathf.Max(0.02f, retargetInterval);
        }
    }

    bool InRange(Enemy e)
    {
        if (e == null) return false;
        float d = HorizontalDistance(transform.position, e.transform.position);
        return d >= minRange && d <= AttackRange;
    }

    // 무기를 대상 쪽으로 돌린다. 대상이 없으면 그 자리에 멈춘다.
    void AimWeapon()
    {
        if (weaponPivot == null) return;

        // 발사 직후에는 쏜 방향을 유지한다 (반동/여운).
        // 조준 자체(currentTarget)는 계속 갱신되므로, 정지가 풀리는 순간 새 대상으로 돌기 시작한다.
        if (Time.time < holdRotationUntil) return;

        if (!TryGetAimRotation(currentTarget, out Quaternion want)) return;
        weaponPivot.rotation = Quaternion.RotateTowards(
            weaponPivot.rotation, want, Mathf.Max(1f, turnSpeed) * Time.deltaTime);
    }

    // 대상을 바라보는 회전값(Y축만). weaponYawOffset은 모델의 '기본으로 바라보는 방향' 보정.
    bool TryGetAimRotation(Enemy target, out Quaternion want)
    {
        want = Quaternion.identity;
        if (target == null) return false;
        return TryGetAimRotation(target.AimPoint, out want);
    }

    // 임의의 지점 버전. 광역 타워는 적이 아니라 '가장 많이 모인 착탄점'을 쏘므로 이쪽이 필요하다.
    bool TryGetAimRotation(Vector3 worldPoint, out Quaternion want)
    {
        want = Quaternion.identity;
        if (weaponPivot == null) return false;

        Vector3 dir = worldPoint - weaponPivot.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return false;

        float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg - weaponYawOffset;
        want = Quaternion.Euler(0f, yaw, 0f);
        return true;
    }

    // 지금 무기가 대상 쪽을 '대충' 보고 있는가. 돌릴 무기가 없으면 항상 true.
    bool IsAimedAt(Enemy target)
    {
        if (weaponPivot == null) return true;
        if (!TryGetAimRotation(target, out Quaternion want)) return true;
        return Quaternion.Angle(weaponPivot.rotation, want) <= Mathf.Max(0.5f, aimTolerance);
    }

    void Fire(Enemy target, bool aimed)
    {
        if (target == null) return;
        shotsThisRound++;

        // 광역 타워는 적이 가장 많이 모인 지점으로 던진다(착탄 지점에서 범위 피해)
        // 2026-08-08 — transform.position → AimPoint 로 교체.
        // 모델 프리팹은 피벗이 발바닥이라 그대로 두면 투사체가 바닥을 향해 날아가 발밑을 긁는다.
        // 캡슐(기존)은 AimPoint == transform.position 이므로 동작이 완전히 같다.
        Vector3 aimPos = targetArea ? FindBestSplashPos(target) : target.AimPoint;

        // 발사 후 회전 정지는 **조준이 맞은 발사에만** 건다.
        // 대기 시간이 초과되어 어쩔 수 없이 쏜 발까지 굳히면
        // 엉뚱한 방향을 본 채로 멈춰 있는 그림이 된다.
        if (postFireHold > 0f && weaponPivot != null && aimed)
        {
            float hold = Mathf.Min(postFireHold, shootingSpeed * postFireHoldMaxRatio);
            holdRotationUntil = Time.time + hold;
        }

        FireProjectile(aimPos);
    }

    // 사거리 안 적들 중, 그 위치를 중심으로 splashRadius 안에 적이 가장 많이 들어오는 지점
    Vector3 FindBestSplashPos(Enemy fallback)
    {
        Enemy[] all = Object.FindObjectsByType<Enemy>(FindObjectsSortMode.None);
        Vector3 best = fallback.AimPoint;
        int bestCount = -1;
        foreach (Enemy anchor in all)
        {
            if (anchor == null) continue;
            float d = HorizontalDistance(transform.position, anchor.transform.position);
            if (d < minRange || d > AttackRange) continue; // 사거리 안 적만 조준점 후보
            int count = 0;
            foreach (Enemy e in all)
                if (e != null && HorizontalDistance(anchor.transform.position, e.transform.position) <= splashRadius)
                    count++;
            if (count > bestCount) { bestCount = count; best = anchor.AimPoint; }
        }
        return best;
    }

    // 투사체 생성 후 목표 지점으로 발사
    void FireProjectile(Vector3 targetPos)
    {
        // 발사 위치. muzzle을 무기의 자식으로 두면 포신이 돌아갈 때 같이 돌아서
        // 투사체가 총구에서 나가는 것처럼 보인다. 비우면 예전과 똑같이 타워 중심 위 0.5.
        Vector3 start = (muzzle != null) ? muzzle.position : transform.position + Vector3.up * 0.5f;

        GameObject go;
        if (projectilePrefab != null)
        {
            go = Instantiate(projectilePrefab, start, Quaternion.identity);

            // 프리팹 원본 배율을 곱한다(덮어쓰지 않는다) — 적 모델·이펙트와 같은 이유
            go.transform.localScale = go.transform.localScale * Mathf.Max(0.01f, projectileScale);

            // 콜라이더는 끈다. 살아 있으면 타일 레이캐스트를 가로채고 물리도 불필요하다.
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
                if (c != null) c.enabled = false;

            if (tintProjectile) TintAll(go, projectileColor);
        }
        else
        {
            // 기존 동작 그대로: 작은 검은 구슬
            go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.transform.localScale = Vector3.one * (0.25f * Mathf.Max(0.01f, projectileScale));
            go.transform.position = start;

            Collider col = go.GetComponent<Collider>();
            if (col != null) Destroy(col); // 물리 충돌 불필요

            TintAll(go, projectileColor);
        }
        go.name = "Projectile";

        // 진행 방향 바라보기. 화살·볼트처럼 방향이 있는 모델에 필요하다.
        // (구슬은 어차피 티가 안 나므로 켜 두어도 무해)
        Quaternion offset = Quaternion.Euler(projectileEulerOffset);
        Vector3 dir = targetPos - start;
        go.transform.rotation = (projectileFacesTravel && dir.sqrMagnitude > 0.0001f)
            ? Quaternion.LookRotation(dir) * offset
            : offset;

        float ps = Mathf.Max(minProjectileSpeed, projectileSpeedFactor / Mathf.Max(0.05f, shootingSpeed));
        Projectile p = go.AddComponent<Projectile>();
        p.impactEffect = impactEffect;   // 참조만 넘긴다 (타워를 팔아도 날아가던 투사체는 무사하다)
        p.spinSpeed = projectileSpin;
        p.spinAxis = projectileSpinAxis;
        p.Init(targetPos, damage, targetArea, splashRadius, ps, slowFactor, slowDuration);
    }

    // 하위 렌더러 전체에 색을 입힌다. MaterialPropertyBlock이라 머티리얼을 복제하지 않는다.
    static void TintAll(GameObject go, Color color)
    {
        MaterialPropertyBlock mpb = new MaterialPropertyBlock();
        foreach (Renderer rend in go.GetComponentsInChildren<Renderer>(true))
        {
            if (rend == null) continue;
            rend.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", color);
            mpb.SetColor("_Color", color);
            rend.SetPropertyBlock(mpb);
        }
    }

    // ── 업그레이드 (종류 단위) ──────────────────────
    // 이 타워의 레벨을 설정하고, 스탯을 '원본 기준'으로 다시 계산한다.
    // (곱해 나가는 방식이 아니라 매번 base에서 재계산 → 오차 누적 없음)
    public void SetUpgradeLevel(int level)
    {
        CaptureBase();
        upgradeLevel = Mathf.Max(0, level);
        ApplyStats();
    }

    // 기본 스탯 + 업그레이드 레벨 + 화력 배율 → 최종 스탯
    void ApplyStats()
    {
        CaptureBase();
        float ps = Mathf.Max(0.01f, powerScale);

        // 타겟팅 방식에 따라 업그레이드 효과를 분리한다.
        //  - 범위/저격(폭탄=targetArea, 미사일=targetHighestHealth): 레벨당 공격력↑ (공격속도는 유지)
        //  - 단일 타겟(일반·속사) + 지원(forceRateUpgrade): 레벨당 공격속도↑ (공격력은 유지)
        bool boostsDamage = (targetArea || targetHighestHealth) && !forceRateUpgrade;
        if (boostsDamage)
        {
            damage = baseDamage * Mathf.Pow(1f + damageUpPerLevel, upgradeLevel) * ps;
            shootingSpeed = baseShootingSpeed;
        }
        else
        {
            shootingSpeed = baseShootingSpeed * Mathf.Pow(1f / (1f + rateUpPerLevel), upgradeLevel);
            damage = baseDamage * ps;
        }

        ApplyUpgradeVisual();
    }

    // 레벨에 비례해 파츠를 켠다. Lv0이면 전부 끈다.
    // 예) 파츠 3개 · 최대 Lv10 → Lv1~4 = 1개, Lv5~7 = 2개, Lv8~10 = 3개
    void ApplyUpgradeVisual()
    {
        if (upgradeParts == null || upgradeParts.Length == 0) return;

        TowerUpgradeManager upm = TowerUpgradeManager.Instance;
        int maxLv = (upm != null) ? Mathf.Max(1, upm.maxLevel) : 10;

        int on = Mathf.Clamp(
            Mathf.CeilToInt(upgradeLevel / (float)maxLv * upgradeParts.Length),
            0, upgradeParts.Length);

        for (int i = 0; i < upgradeParts.Length; i++)
            if (upgradeParts[i] != null) upgradeParts[i].SetActive(i < on);

        // 파츠가 쌓이면 무기도 같이 올라가야 탑이 자라는 것처럼 보인다.
        if (weaponPivot != null && weaponLiftPerPart != 0f)
        {
            CaptureWeaponBase();
            weaponPivot.localPosition = weaponBaseLocalPos + Vector3.up * (weaponLiftPerPart * on);
        }
    }
    // ────────────────────────────────────────────

    // 사거리[minRange, maxRange] 안에서 공격할 적을 고른다.
    //
    // 2단계로 나뉜다.
    //   1차: 원래 우선순위대로 최우선 대상을 찾는다 (가장 가까운 적 / 최고 체력)
    //   2차: 그와 **동급**인 후보들 중에서 **가장 적게 돌아도 되는** 적을 고른다
    //
    // 왜 2차가 필요한가: 거의 같은 거리의 적이 좌우에 있으면 재탐색(0.15초)마다
    //   대상이 바뀌어 포신이 계속 휘둘린다. 그러면 발사 시점에 조준이 안 맞아
    //   발사가 밀리고, 발사 후 정지도 엉뚱한 방향에 걸린다.
    //   지금 보고 있는 적은 회전각이 0에 가까우므로 자연히 유지된다 — 고정 로직이 따로 필요 없다.
    //
    // ※ 밸런스에는 영향이 없다. 동급 범위 안에서만 고르므로 '더 먼 적을 우선한다'는 일이 없다.
    Enemy FindTarget()
    {
        Enemy[] all = Object.FindObjectsByType<Enemy>(FindObjectsSortMode.None);

        Enemy best = null;
        float bestScore = targetHighestHealth ? -1f : float.MaxValue;
        int inRange = 0;

        foreach (Enemy e in all)
        {
            if (e == null) continue;
            float d = HorizontalDistance(transform.position, e.transform.position);
            if (d < minRange || d > AttackRange) continue;
            inRange++;

            if (targetHighestHealth)
            {
                if (e.CurrentHealth > bestScore) { bestScore = e.CurrentHealth; best = e; } // 최고 체력 저격
            }
            else
            {
                if (d < bestScore) { bestScore = d; best = e; }  // 가장 가까운 적
            }
        }

        // 돌릴 무기가 없거나 후보가 하나뿐이면 예전과 동일하게 동작한다
        if (best == null || weaponPivot == null || inRange < 2) return best;

        Enemy pick = best;
        float pickTurn = float.MaxValue;

        foreach (Enemy e in all)
        {
            if (e == null) continue;
            float d = HorizontalDistance(transform.position, e.transform.position);
            if (d < minRange || d > AttackRange) continue;

            bool tied = targetHighestHealth
                ? e.CurrentHealth >= bestScore * Mathf.Clamp01(tieBandHealthRatio)
                : d <= bestScore + Mathf.Max(0f, tieBandDistance);
            if (!tied) continue;

            if (!TryGetAimRotation(e, out Quaternion want)) continue;
            float turn = Quaternion.Angle(weaponPivot.rotation, want);
            if (turn < pickTurn) { pickTurn = turn; pick = e; }
        }
        return pick;
    }

    float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f; b.y = 0f;
        return Vector3.Distance(a, b);
    }

    // 씬에서 타워 선택 시 사거리 원 표시 (개발 편의)
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        DrawCircle(AttackRange);   // 실제 타격 범위를 그린다
        if (minRange > 0f)
        {
            Gizmos.color = Color.gray;
            DrawCircle(minRange);
        }
    }

    void DrawCircle(float radius)
    {
        int seg = 32;
        Vector3 prev = transform.position + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= seg; i++)
        {
            float ang = (i / (float)seg) * Mathf.PI * 2f;
            Vector3 next = transform.position + new Vector3(Mathf.Cos(ang) * radius, 0f, Mathf.Sin(ang) * radius);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }
}
