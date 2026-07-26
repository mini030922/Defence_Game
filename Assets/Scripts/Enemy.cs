using System.Collections.Generic;
using UnityEngine;

// 경로(월드 좌표 목록)를 따라 이동하는 적.
// 체력이 0이 되면 죽고, 경로 끝(골)에 도달하면 사라진다.
public class Enemy : MonoBehaviour
{
    public float maxHealth = 10f;
    public float moveSpeed = 2f;
    public int reward = 10;         // 처치 시 지급할 골드
    public int damageToPlayer = 1;  // 골 도달 시 깎을 플레이어 체력

    private float health;
    private List<Vector3> path;   // 따라갈 경로 (월드 좌표)
    private int targetIndex = 0;  // 지금 향하고 있는 경로 지점 번호
    private const float ReachThreshold = 0.05f;

    private float baseMoveSpeed = 2f; // 감속 없는 기준 속도
    private float slowTimer = 0f;
    private float slowFactor = 1f;

    // 이번 라운드에 '새로' 감속이 걸린 적의 수 (RoundManager가 라운드 시작 시 0으로 리셋).
    // 지원 타워의 가치는 DPS·가동률로는 안 잡히는 간접 효과라, 배치 AI 보상에 쓰려고 집계한다.
    public static int slowAppliedThisRound = 0;

    // 이번 라운드에 적이 도달한 최대 경로 진행도(0~1). 골에 안 샜어도 '얼마나 아슬아슬했나'를 나타낸다.
    public static float maxProgressThisRound = 0f;

    // 외부(스포너/게임매니저)에서 결과를 받기 위한 콜백
    public System.Action<Enemy> onReachedEnd; // 골 도달
    public System.Action<Enemy> onDied;        // 사망

    // 스포너가 적을 만든 직후 호출해 초기화한다.
    // 시작 위치의 높이(y)는 유지해서 타일 위에 떠 있게 한다.
    public void Init(List<Vector3> pathPoints, float hp, float speed)
    {
        path = pathPoints;
        maxHealth = hp;
        health = hp;
        moveSpeed = speed;
        baseMoveSpeed = speed;
        targetIndex = 0;
    }

    void Update()
    {
        if (path == null || path.Count == 0) return;
        if (targetIndex >= path.Count) return;

        // 감속 지속시간 경과 → 원래 속도로 복구
        if (slowTimer > 0f)
        {
            slowTimer -= Time.deltaTime;
            if (slowTimer <= 0f) { slowFactor = 1f; moveSpeed = baseMoveSpeed; }
        }

        // 다음 경로 지점으로 이동 (높이는 현재 높이 유지)
        Vector3 t = path[targetIndex];
        Vector3 targetPos = new Vector3(t.x, transform.position.y, t.z);
        transform.position = Vector3.MoveTowards(transform.position, targetPos, moveSpeed * Time.deltaTime);

        // 지점에 도착하면 다음 지점으로
        if (Vector3.Distance(transform.position, targetPos) <= ReachThreshold)
        {
            targetIndex++;
            float prog = targetIndex / (float)path.Count;
            if (prog > maxProgressThisRound) maxProgressThisRound = prog;
            if (targetIndex >= path.Count)
                ReachEnd();
        }
    }

    // 타워가 나중에 이 함수를 호출해 데미지를 준다.
    // 현재 남은 체력 (타워의 '최고 체력 조준'에서 사용)
    public float CurrentHealth => health;

    public void TakeDamage(float amount)
    {
        health -= amount;
        if (health <= 0f)
            Die();
    }

    // 지원 타워 등이 호출: 이동 속도를 factor배(0.5=절반)로 duration초 감속.
    // 사거리에 계속 있으면 재적용되어 유지되고, 사거리를 벗어나면 duration 뒤 회복.
    public void ApplySlow(float factor, float duration)
    {
        // 이미 감속 중이면 갱신일 뿐이고, 안 걸려 있던 적에게 새로 건 경우만 집계한다.
        // (지원 타워가 3초마다 재적용하는 걸 중복으로 세면 값이 의미를 잃는다)
        if (slowTimer <= 0f) slowAppliedThisRound++;

        factor = Mathf.Clamp01(factor);
        slowFactor = factor;
        slowTimer = duration;
        moveSpeed = baseMoveSpeed * slowFactor;
    }

    void ReachEnd()
    {
        // 골에 도달 → 플레이어 체력 감소
        if (GameManager.Instance != null)
            GameManager.Instance.TakeDamage(damageToPlayer);
        onReachedEnd?.Invoke(this);
        Destroy(gameObject);
    }

    void Die()
    {
        // 처치됨 → 골드 지급
        if (GameManager.Instance != null)
            GameManager.Instance.AddGold(reward);
        onDied?.Invoke(this);
        Destroy(gameObject);
    }
}
