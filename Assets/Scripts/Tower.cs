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

    // 직전 라운드에 이 타워 '개별'의 가동률 (0~1). RoundManager가 라운드 종료 시 채운다.
    // 평균 가동률만으로는 '12개가 고르게 70%'와 '3개가 완전히 놀고 9개가 풀가동'을 구분 못 해서,
    // 배치 AI가 어느 자리가 실패였는지 알 방법이 없었다.
    [System.NonSerialized] public float lastRoundUtil;

    [Header("광역 공격 (targetArea가 true일 때만 사용)")]
    public float splashRadius = 1.5f;

    [Header("감속 (지원 타워용 — slowFactor<1이면 명중한 적을 감속)")]
    public float slowFactor = 1f;      // 1=감속 없음, 0.5=속도 절반
    public float slowDuration = 2.5f;  // 한 번 맞으면 이만큼만 느려짐(초). 이후 회복(재감속 잠금)

    [Header("투사체")]
    public float projectileSpeedFactor = 6f; // 투사체 속도 = factor / 공격간격 (공격속도에 비례)
    public float minProjectileSpeed = 4f;
    public Color projectileColor = new Color(0.1f, 0.1f, 0.12f);

    [Header("업그레이드 (종류 단위: TowerUpgradeManager가 레벨 관리)")]
    public int upgradeLevel = 0;
    // 2026-08-02 — 최대 레벨 5 → 8 확장에 맞춰 레벨당 배율 하향.
    //   설계 의도: **만렙까지 가는 데 돈은 더 들지만 만렙 자체는 더 약하게.**
    //   목표는 "새 만렙(Lv8) DPS = 옛 만렙(Lv5) DPS × 0.7". 거기서 역산했다.
    //     공격력형: 1.40^5 = 5.378배 → 목표 3.765배 → 3.765^(1/8) = 1.180  → +18.0%/레벨
    //     연사형  : 1.33^5 = 4.162배 → 목표 2.913배 → 2.913^(1/8) = 1.143  → +14.3%/레벨
    //   업그레이드 비용 공식(upgradeCostFactor 1.5 + 0.6×레벨)은 그대로 두었으므로,
    //   만렙까지 총비용은 기본가 13.5배 → 28.8배(2.13배)가 되고 화력은 0.70배가 된다.
    //   = **DPS당 비용이 3.05배**. 강화 일변도 전략을 누르고 후반 골드 소비처를 넓히는 것이 목적.
    public float damageUpPerLevel = 0.18f; // 레벨당 공격력 +18%
    public float rateUpPerLevel = 0.143f;  // 레벨당 공격속도(연사) +14.3%
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

    void Awake() { CaptureBase(); ApplyStats(); activeSince = Time.time; }

    void CaptureBase()
    {
        if (baseCaptured) return;
        baseDamage = damage;
        baseShootingSpeed = shootingSpeed;
        baseCaptured = true;
    }

    void Update()
    {
        if (shootingSpeed <= 0f) return; // 간격 0 이하면 공격 안 함
        fireTimer -= Time.deltaTime;
        if (fireTimer <= 0f)
        {
            TryAttack();
            fireTimer = shootingSpeed;
        }
    }

    void TryAttack()
    {
        Enemy target = FindTarget();
        if (target == null) return;
        shotsThisRound++;

        // 광역 타워는 적이 가장 많이 모인 지점으로 던진다(착탄 지점에서 범위 피해)
        Vector3 aimPos = targetArea ? FindBestSplashPos(target) : target.transform.position;
        FireProjectile(aimPos);
    }

    // 사거리 안 적들 중, 그 위치를 중심으로 splashRadius 안에 적이 가장 많이 들어오는 지점
    Vector3 FindBestSplashPos(Enemy fallback)
    {
        Enemy[] all = Object.FindObjectsByType<Enemy>(FindObjectsSortMode.None);
        Vector3 best = fallback.transform.position;
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
            if (count > bestCount) { bestCount = count; best = anchor.transform.position; }
        }
        return best;
    }

    // 투사체 생성 후 목표 지점으로 발사
    void FireProjectile(Vector3 targetPos)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "Projectile";
        go.transform.localScale = Vector3.one * 0.25f;
        go.transform.position = transform.position + Vector3.up * 0.5f;

        Collider col = go.GetComponent<Collider>();
        if (col != null) Destroy(col); // 물리 충돌 불필요

        Renderer rend = go.GetComponent<Renderer>();
        MaterialPropertyBlock mpb = new MaterialPropertyBlock();
        rend.GetPropertyBlock(mpb);
        mpb.SetColor("_BaseColor", projectileColor);
        mpb.SetColor("_Color", projectileColor);
        rend.SetPropertyBlock(mpb);

        float ps = Mathf.Max(minProjectileSpeed, projectileSpeedFactor / Mathf.Max(0.05f, shootingSpeed));
        Projectile p = go.AddComponent<Projectile>();
        p.Init(targetPos, damage, targetArea, splashRadius, ps, slowFactor, slowDuration);
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
    }
    // ────────────────────────────────────────────

    // 사거리[minRange, maxRange] 안에서 가장 가까운 적을 찾는다.
    Enemy FindTarget()
    {
        Enemy[] all = Object.FindObjectsByType<Enemy>(FindObjectsSortMode.None);
        Enemy best = null;
        float bestScore = targetHighestHealth ? -1f : float.MaxValue;

        foreach (Enemy e in all)
        {
            if (e == null) continue;
            float d = HorizontalDistance(transform.position, e.transform.position);
            if (d < minRange || d > AttackRange) continue;

            if (targetHighestHealth)
            {
                if (e.CurrentHealth > bestScore) { bestScore = e.CurrentHealth; best = e; } // 최고 체력 저격
            }
            else
            {
                if (d < bestScore) { bestScore = d; best = e; }  // 가장 가까운 적
            }
        }
        return best;
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
