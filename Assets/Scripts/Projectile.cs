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

    void Update()
    {
        life -= Time.deltaTime;
        if (life <= 0f) { Destroy(gameObject); return; }

        transform.position = Vector3.MoveTowards(transform.position, target, speed * Time.deltaTime);
        if (Vector3.Distance(transform.position, target) <= 0.05f)
        {
            Impact();
            Destroy(gameObject);
        }
    }

    void Impact()
    {
        Enemy[] all = Object.FindObjectsByType<Enemy>(FindObjectsSortMode.None);

        if (area)
        {
            // 범위를 눈으로 볼 수 있게 착탄 지점에 반투명 원을 띄운다.
            // 감속을 거는 투사체(지원 타워)면 하늘색, 아니면(폭탄) 회색.
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
