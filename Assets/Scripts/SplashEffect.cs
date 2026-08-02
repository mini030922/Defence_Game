using UnityEngine;
using UnityEngine.Rendering;

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

    private static Material sharedMat;

    private Renderer rend;
    private float t;
    private float startAlpha;

    // 착탄 지점에 반경 radius 짜리 원을 띄운다.
    public static void Spawn(Vector3 pos, float radius, bool isSlow)
    {
        if (!show || radius <= 0f || radius > 50f) return;   // 미사일의 999 같은 값은 무시

        EnsureMaterial();

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

        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Unlit/Color");
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
