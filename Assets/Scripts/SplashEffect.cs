using UnityEngine;
using UnityEngine.Rendering;

// ── 착탄 이펙트 설정 (2026-08-08 신설) ────────────────────────────────────
// 타워 프리팹의 인스펙터에서 채우는 값 묶음. Tower가 들고 있다가 발사할 때
// Projectile에 넘겨주고, 착탄 시 SplashEffect.SpawnEffect가 이걸 보고 이펙트를 만든다.
//
// 왜 타워에 두나: 투사체는 Tower.FireProjectile이 CreatePrimitive로 **코드 생성**한다.
// 프리팹이 아니라 인스펙터에서 참조를 꽂을 자리가 없다.
// 반면 타워는 실제 프리팹 에셋(BombTower/SupportTower Variant)이라 꽂을 수 있다.
// → 참조는 타워가 보관하고, 투사체는 발사 시점에 값을 건네받는 구조가 된다.
[System.Serializable]
public class ImpactEffectSettings
{
    public GameObject prefab;            // 비우면 기존 반투명 원판이 그대로 나온다
    public float scale = 1f;             // 추가 배율 (모델 원본 크기 보정)
    public bool scaleWithSplash = true;  // splashRadius에 비례해 크기를 맞출지
    public Vector3 eulerOffset;          // 이펙트가 누워/서서 나오면 여기서 돌린다
    public float yOffset = 0f;           // 바닥 높이 미세 조정
    public float life = 0f;              // 수명(초). 0이면 파티클 설정에서 자동 계산
    public bool alsoShowRangeDisc = false; // 이펙트와 함께 판정 범위 원판도 그릴지(밸런스 확인용)
}

// 광역 공격이 착탄할 때 그 범위를 바닥에 반투명 원으로 잠깐 보여준다.
// (밸런스 수치를 손으로 조절하는 중이라, 실제 splashRadius가 얼마나 되는지 눈으로 봐야 한다)
//
// 프리팹 없이 코드로만 만든다 — 투사체 자체가 런타임 생성이라 프리팹을 끼울 자리가 없다.
// 학습(--no-graphics) 중에는 show=false 로 꺼져서 오브젝트를 아예 만들지 않는다.
public class SplashEffect : MonoBehaviour
{
    public static bool show = true;

    public static Color bombColor    = new Color(0.75f, 0.75f, 0.80f, 0.60f); // 폭탄 = 회색
    public static Color supportColor = new Color(0.35f, 0.80f, 1.00f, 0.60f); // 지원 = 하늘색
    public static float duration = 0.55f;   // 이 시간에 걸쳐 사라짐

    // ⚠ 타일 지오메트리 주의 (2026-08-02 수정)
    //   타일 = Cube(1×1×1) × scale(0.95, 0.2, 0.95), 중심 y=0  →  차지 범위 -0.10 ~ +0.10
    //   즉 **타일 윗면이 y=0.10** 이다. 처음에 0.06으로 뒀더니 원판이 타일 속에 파묻혀
    //   화면에 전혀 보이지 않았다. 윗면보다 확실히 위로 올려야 한다.
    public static float groundY   = 0.16f;

    // 이펙트 프리팹이 '어느 splashRadius를 기준으로 제작됐는가'.
    // 폭탄 타워의 splashRadius가 정확히 1.5라, 이 값을 1.5로 두면
    // 폭탄에서는 scale 1.0이 곧 원본 크기가 된다 → 인스펙터에서 감을 잡기 쉽다.
    public static float effectRefRadius = 1.5f;

    // 수명 자동 계산이 빗나가거나(루프 파티클 등) 프리팹이 영원히 사는 것을 막는 상한.
    // 착탄 이펙트가 필드에 쌓이면 그대로 프레임 저하로 이어진다.
    public static float maxEffectLife = 6f;

    private static Material sharedMat;

    private Renderer rend;
    private float t;
    private float startAlpha;

    // ── 이펙트 프리팹 소환 (2026-08-08 신설) ──────────────────────────────
    // show가 false면(학습 --no-graphics) 아무것도 만들지 않는다 — 원판과 같은 규칙.
    public static void SpawnEffect(ImpactEffectSettings s, Vector3 pos, float splashRadius)
    {
        if (!show || s == null || s.prefab == null) return;

        Vector3 p = new Vector3(pos.x, groundY + s.yOffset, pos.z);
        GameObject go = Object.Instantiate(s.prefab, p, Quaternion.Euler(s.eulerOffset));

        // 프리팹 원본 배율을 '곱한다'(덮어쓰지 않는다).
        // VFX 프리팹은 루트에 배율이 들어 있는 경우가 흔해서 덮어쓰면 크기가 튄다.
        float mul = s.scale * (s.scaleWithSplash
                                ? splashRadius / Mathf.Max(0.01f, effectRefRadius)
                                : 1f);
        go.transform.localScale = go.transform.localScale * Mathf.Max(0.01f, mul);

        float life = (s.life > 0f) ? s.life : EstimateLife(go);
        Object.Destroy(go, Mathf.Clamp(life, 0.1f, maxEffectLife));
    }

    // 파티클 설정에서 '이 이펙트가 끝나는 데 걸리는 시간'을 어림한다.
    // duration(방출 기간) + startLifetime(입자 하나의 수명) + startDelay 가 최댓값 근사.
    // 루프 파티클은 이 값이 의미가 없으므로 maxEffectLife가 상한으로 잡아준다.
    static float EstimateLife(GameObject go)
    {
        float life = 0f;
        foreach (ParticleSystem ps in go.GetComponentsInChildren<ParticleSystem>(true))
        {
            if (ps == null) continue;
            ParticleSystem.MainModule m = ps.main;
            float d = m.duration + m.startLifetime.constantMax + m.startDelay.constantMax;
            if (d > life) life = d;
        }
        return (life > 0f) ? life : 2f;
    }

    // 착탄 지점에 반경 radius 짜리 원을 띄운다.
    public static void Spawn(Vector3 pos, float radius, bool isSlow)
    {
        if (!show || radius <= 0f || radius > 50f) return;   // 미사일의 999 같은 값은 무시

        EnsureMaterial();
        if (sharedMat == null) return;                 // 셰이더를 못 찾았으면 조용히 건너뛴다

        // 실린더는 기본 반지름 0.5 · 높이 2 → x,z 를 지름으로, y 를 아주 얇게
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = "SplashFX";
        Collider c = go.GetComponent<Collider>();
        if (c != null) Destroy(c);

        go.transform.position = new Vector3(pos.x, groundY, pos.z);
        go.transform.localScale = new Vector3(radius * 2f, 0.02f, radius * 2f);

        Renderer r = go.GetComponent<Renderer>();
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.material = new Material(sharedMat);          // 알파를 개별로 줄여야 하니 인스턴스

        SplashEffect fx = go.AddComponent<SplashEffect>();
        fx.rend = r;
        fx.startAlpha = (isSlow ? supportColor : bombColor).a;
        SetColor(r.material, isSlow ? supportColor : bombColor);
    }

    void Update()
    {
        t += Time.deltaTime;
        float k = Mathf.Clamp01(t / Mathf.Max(0.01f, duration));
        if (k >= 1f) { Destroy(gameObject); return; }

        if (rend != null)
        {
            Color c = rend.material.HasProperty("_BaseColor")
                    ? rend.material.GetColor("_BaseColor")
                    : rend.material.color;
            c.a = startAlpha * (1f - k * k);          // 초반 진하게 유지 → 끝에서 급히 사라짐
            SetColor(rend.material, c);
        }
    }

    static void SetColor(Material m, Color c)
    {
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
    }

    // 반투명 머티리얼 한 벌을 만들어 공유한다 (매 착탄마다 새로 만들면 GC가 터진다)
    static void EnsureMaterial()
    {
        if (sharedMat != null) return;

        // ⚠️ Shader.Find 직접 호출 금지 — RuntimeShaders.cs 주석 참고.
        Shader sh = RuntimeShaders.Unlit;
        if (sh == null) return;
        sharedMat = new Material(sh);

        // URP Unlit 은 기본이 불투명이라 알파 블렌딩을 손으로 켜줘야 한다
        if (sharedMat.HasProperty("_Surface"))
        {
            sharedMat.SetFloat("_Surface", 1f);   // 0=Opaque 1=Transparent
            sharedMat.SetFloat("_Blend", 0f);     // Alpha
            sharedMat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            sharedMat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            sharedMat.SetFloat("_ZWrite", 0f);
            sharedMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            sharedMat.renderQueue = (int)RenderQueue.Transparent;
        }
    }
}
