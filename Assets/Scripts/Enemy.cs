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

    // ── 체력바 (2026-08-01 신설) ─────────────────────────────────────────
    // 화력 배율(Tower.powerScale)을 조절하며 체감을 재는 중이라, 적이 어디서 얼마나
    // 버티는지 눈으로 봐야 한다. 프리팹 없이 코드로 만든다(적 자체가 런타임 생성이라).
    // 학습(--no-graphics)에서는 자동으로 꺼져 오브젝트를 만들지 않는다.
    public static bool showHealthBars = true;

    public static float barWidth = 1.4f;      // 월드 단위 (적 크기와 무관하게 항상 이 크기)
    public static float barHeight = 0.18f;
    public static float barHeadMargin = 0.45f; // 적 머리 꼭대기에서 이만큼 더 위

    private Transform barRoot;
    private Transform barFill;
    private static Material barBgMat;
    private static Material barFillMat;

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
        if (showHealthBars) BuildHealthBar();
    }

    void BuildHealthBar()
    {
        if (barRoot != null) return;

        // 공용 머티리얼 (적마다 새로 만들면 드로우콜·GC가 터진다)
        if (barBgMat == null)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            barBgMat = new Material(sh);   barBgMat.color = new Color(0.05f, 0.05f, 0.05f, 1f);
            barFillMat = new Material(sh); barFillMat.color = Color.green;
            SetMatColor(barBgMat, new Color(0.05f, 0.05f, 0.05f, 1f));
        }

        GameObject root = new GameObject("HPBar");
        root.transform.SetParent(transform, false);
        barRoot = root.transform;

        // 적마다 스케일이 다르다(빠름 0.4 / 일반 0.5 / 탱커 0.8).
        // 자식은 부모 스케일을 물려받으므로 그대로 두면 **작은 적일수록 체력바도 작아진다**.
        // 역수를 걸어 상쇄하면 어떤 적이든 체력바가 같은 월드 크기로 보인다.
        float s = Mathf.Max(0.01f, transform.localScale.y);
        barRoot.localScale = Vector3.one / s;

        // 캡슐 프리미티브는 기본 높이가 2 → 반높이 = 스케일 × 1.0.
        // 그 위로 여유를 두지 않으면 체력바가 몸통에 파묻힌다.
        barRoot.localPosition = new Vector3(0f, 1f + barHeadMargin / s, 0f);

        // 배경 (검정)
        GameObject bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(bg.GetComponent<Collider>());
        bg.name = "BG";
        bg.transform.SetParent(root.transform, false);
        bg.transform.localScale = new Vector3(barWidth, barHeight, 1f);
        bg.GetComponent<Renderer>().sharedMaterial = barBgMat;

        // 채움 (초록→빨강). 피벗을 왼쪽으로 옮기려고 한 겹 더 감싼다.
        GameObject pivot = new GameObject("FillPivot");
        pivot.transform.SetParent(root.transform, false);
        pivot.transform.localPosition = new Vector3(-barWidth * 0.5f, 0f, -0.01f);
        GameObject fill = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(fill.GetComponent<Collider>());
        fill.name = "Fill";
        fill.transform.SetParent(pivot.transform, false);
        fill.transform.localPosition = new Vector3(0.5f, 0f, 0f);   // 왼쪽 끝 기준으로 자라게
        fill.transform.localScale = new Vector3(1f, barHeight * 0.8f, 1f);
        Renderer fr = fill.GetComponent<Renderer>();
        fr.material = new Material(barFillMat);   // 색을 개별로 바꿔야 하니 인스턴스
        barFill = pivot.transform;
        barFill.localScale = new Vector3(barWidth, 1f, 1f);
        fillRenderer = fr;
    }

    private Renderer fillRenderer;

    static void SetMatColor(Material m, Color c)
    {
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
    }

    // 카메라를 향하게 세우고 남은 체력만큼 채운다.
    void LateUpdate()
    {
        if (barRoot == null) return;
        // 카메라 회전을 그대로 복사하는 게 가장 안정적인 빌보드다.
        // LookRotation(위치차)는 up 벡터가 흔들려 막대가 비스듬히 기울어진다.
        Camera cam = Camera.main;
        if (cam != null) barRoot.rotation = cam.transform.rotation;

        float ratio = (maxHealth > 0f) ? Mathf.Clamp01(health / maxHealth) : 0f;
        if (barFill != null)
            barFill.localScale = new Vector3(barWidth * ratio, 1f, 1f);
        if (fillRenderer != null)
            SetMatColor(fillRenderer.material,
                        Color.Lerp(new Color(0.9f, 0.15f, 0.15f), new Color(0.2f, 0.9f, 0.25f), ratio));
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
