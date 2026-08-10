using UnityEngine;

// 타워가 발사하는 투사체. 발사 시점의 목표 지점으로 날아가, 도착 시 데미지를 준다.
// 목표 지점에 적이 없으면(빠른 적이 이동해 벗어남) 빗나간다 → 빠른 적이 상대적으로 강해짐.
public class Projectile : MonoBehaviour
{
    private Vector3 target;      // 발사 시점의 목표 위치
    private float damage;
    private bool area;           // 광역(스플래시) 여부
    private float splashRadius;
    private float speed;
    private float hitRadius = 0.45f; // 단일 명중 판정 반경
    private float life = 3f;         // 안전장치: 3초 뒤 자동 소멸
    private float slowFactor = 1f;   // 1=감속 없음, 0.5=명중한 적 속도 절반
    private float slowDuration = 0f;

    // 착탄 이펙트 설정. Tower가 발사 직후 Init '전에' 넣어준다 (비어 있으면 기존 반투명 원판).
    // Init 인자로 받지 않는 이유: 인자가 이미 7개라 더 늘리면 호출부가 읽기 어려워진다.
    [System.NonSerialized] public ImpactEffectSettings impactEffect;

    // 날아가는 동안의 자체 회전 (바위가 구르는 연출 등). Tower가 발사 직후 넣어준다.
    // 로컬 축을 기준으로 돌리므로, 진행 방향을 바라보게 맞춰둔 회전이 흐트러지지 않는다.
    [System.NonSerialized] public float spinSpeed = 0f;
    [System.NonSerialized] public Vector3 spinAxis = new Vector3(1f, 0f, 0f);

    public void Init(Vector3 targetPos, float damage, bool area, float splashRadius, float speed,
                     float slowFactor = 1f, float slowDuration = 0f)
    {
        this.target = targetPos;
        this.damage = damage;
        this.area = area;
        this.splashRadius = splashRadius;
        this.speed = speed;
        this.slowFactor = slowFactor;
        this.slowDuration = slowDuration;
    }

    private bool finished;   // 착탄 처리가 끝나고 소멸을 기다리는 중

    void Update()
    {
        if (finished) return;   // 꼬리가 사라지는 동안에는 더 움직이지도, 다시 때리지도 않는다

        life -= Time.deltaTime;
        if (life <= 0f) { Finish(); return; }

        transform.position = Vector3.MoveTowards(transform.position, target, speed * Time.deltaTime);

        if (spinSpeed != 0f && spinAxis.sqrMagnitude > 0.0001f)
            transform.Rotate(spinAxis.normalized, spinSpeed * Time.deltaTime, Space.Self);

        if (Vector3.Distance(transform.position, target) <= 0.05f)
        {
            Impact();
            Finish();
        }
    }

    // ── 소멸 (2026-08-09 개편) ────────────────────────────────────────────
    // 예전엔 착탄과 동시에 Destroy(gameObject) 였다. 메시 투사체(구슬·화살·바위)는 그게 맞지만,
    // **파티클 프리팹을 투사체로 쓰면 날아온 꼬리가 통째로 순간 삭제되어 뚝 끊긴다.**
    //
    // 그래서 두 갈래로 나눈다.
    //   · 파티클이 없으면(메시) → 예전처럼 즉시 소멸
    //   · 파티클이 있으면 → **방출만 멈추고** 이미 나온 입자는 수명대로 사라지게 둔 뒤 정리
    // 메시 렌더러는 어느 쪽이든 즉시 끈다. 착탄했는데 구슬이 남아 있으면 안 되니까.
    void Finish()
    {
        if (finished) return;
        finished = true;

        ParticleSystem[] systems = GetComponentsInChildren<ParticleSystem>(true);

        foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            if (r != null && !(r is ParticleSystemRenderer)) r.enabled = false;

        if (systems.Length == 0) { Destroy(gameObject); return; }

        float linger = 0f;
        foreach (ParticleSystem ps in systems)
        {
            if (ps == null) continue;
            ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            ParticleSystem.MainModule m = ps.main;
            linger = Mathf.Max(linger, m.startLifetime.constantMax);
        }
        Destroy(gameObject, Mathf.Clamp(linger, 0.05f, 2f));  // 상한을 둬서 필드에 쌓이지 않게
    }

    void Impact()
    {
        Enemy[] all = Object.FindObjectsByType<Enemy>(FindObjectsSortMode.None);

        if (area)
        {
            // 착탄 연출. 2026-08-08부터 두 갈래다.
            //  · 타워에 이펙트 프리팹이 꽂혀 있으면 그것을 소환하고,
            //  · 없으면 기존 반투명 원판(감속=하늘색 / 폭탄=회색)을 그대로 띄운다.
            // 원판은 '실제 splashRadius'를 정확히 보여주는 유일한 수단이라,
            // 이펙트를 쓰더라도 밸런스를 만질 때는 alsoShowRangeDisc로 겹쳐 볼 수 있게 남겨 둔다.
            bool hasFx = impactEffect != null && impactEffect.prefab != null;
            if (hasFx) SplashEffect.SpawnEffect(impactEffect, target, splashRadius);
            if (!hasFx || impactEffect.alsoShowRangeDisc)
                SplashEffect.Spawn(target, splashRadius, slowFactor < 1f);

            // 착탄 지점 주변 splashRadius 안의 모든 적
            foreach (Enemy e in all)
            {
                if (e == null) continue;
                if (Horizontal(e.transform.position, target) <= splashRadius)
                {
                    e.TakeDamage(damage);
                    if (slowFactor < 1f) e.ApplySlow(slowFactor, slowDuration);
                }
            }
        }
        else
        {
            // 명중 반경 안 가장 가까운 적 하나 (없으면 빗나감)
            Enemy best = null;
            float bestD = float.MaxValue;
            foreach (Enemy e in all)
            {
                if (e == null) continue;
                float d = Horizontal(e.transform.position, target);
                if (d <= hitRadius && d < bestD) { bestD = d; best = e; }
            }
            if (best != null)
            {
                best.TakeDamage(damage);
                if (slowFactor < 1f) best.ApplySlow(slowFactor, slowDuration);
            }
        }
    }

    float Horizontal(Vector3 a, Vector3 b)
    {
        a.y = 0f; b.y = 0f;
        return Vector3.Distance(a, b);
    }
}
