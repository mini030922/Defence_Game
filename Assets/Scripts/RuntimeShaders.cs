using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// 런타임에 new Material(...) 로 쓸 셰이더를 안전하게 찾아 준다.
//
// ── 왜 필요한가 (2026-08-09) ────────────────────────────────────────────────
// Shader.Find 는 **빌드에 포함된 셰이더만** 찾는다.
// 에디터에서는 프로젝트 전체를 뒤지므로 항상 성공하지만, 빌드에서는
// "어떤 머티리얼 '에셋'도 참조하지 않는 셰이더"는 통째로 빠져서 null 이 된다.
//
// 이 프로젝트의 URP/Unlit 이 정확히 그 경우였다 — 체력바·건설 고스트·착탄 원판은
// 전부 런타임 생성이라, 이 셰이더를 참조하는 머티리얼 에셋이 하나도 없다.
//
// 증상: **빌드에서만** 적 체력바가 새까맣게 나옴.
//   Shader.Find("Universal Render Pipeline/Unlit")  → null (빌드에 없음)
//   → 폴백이던 "Sprites/Default" 가 잡힘 (이건 Always Included 라 살아 있다)
//   → 스프라이트 셰이더는 **정점 컬러를 곱한다.** 그런데 Quad 프리미티브 메시에는
//     색 채널이 아예 없어서 (0,0,0,0) 이 들어가고, 결과가 전부 검정이 된다.
//   → 체력이 가득 차 있어도 검은 막대로 보인다.
//
// ── 두 겹으로 막는다 ───────────────────────────────────────────────────────
// 1) ProjectSettings > Graphics > **Always Included Shaders** 에 URP/Unlit 을 넣었다.
//    (Assets 밖 파일이라 실수로 지우면 이 증상이 그대로 돌아온다)
// 2) 그래도 못 찾으면 **씬에서 실제로 그려지고 있는 머티리얼의 셰이더를 빌려온다.**
//    쓰이고 있으니 빌드에 반드시 들어 있다. 조명을 받는 셰이더라 색이 조금
//    달라질 수는 있어도, 검게 사라지는 것보다는 훨씬 낫다.
//
// ❌ "Sprites/Default" 로는 절대 폴백하지 않는다. 위의 이유로 검게 나온다.
// ─────────────────────────────────────────────────────────────────────────────
public static class RuntimeShaders
{
    static Shader cached;
    static bool warned;

    public static Shader Unlit
    {
        get
        {
            if (cached != null) return cached;

            cached = Shader.Find("Universal Render Pipeline/Unlit");
            if (cached != null) return cached;

            cached = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (cached != null)
            {
                Warn("URP/Unlit 이 빌드에 없어 Simple Lit 으로 대체합니다.");
                return cached;
            }

            cached = Borrow();
            if (cached != null)
            {
                Warn($"URP Unlit 계열을 못 찾아 '{cached.name}' 을(를) 빌려 씁니다.");
                return cached;
            }

            Warn("쓸 수 있는 셰이더를 하나도 못 찾았습니다. " +
                 "ProjectSettings > Graphics > Always Included Shaders 에 " +
                 "'Universal Render Pipeline/Unlit' 이 들어 있는지 확인하세요.");
            return null;
        }
    }

    // 씬에서 실제로 그려지고 있는 머티리얼의 셰이더 = 빌드에 확실히 포함된 셰이더.
    static Shader Borrow()
    {
        foreach (Renderer r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (r == null) continue;
            Material m = r.sharedMaterial;
            if (m != null && m.shader != null) return m.shader;
        }
        return null;
    }

    // 색 프로퍼티 이름은 셰이더마다 다르다. URP 는 _BaseColor, 빌트인은 _Color.
    // 어느 쪽이 걸릴지 모르니 있는 것만 골라 둘 다 넣는다.
    public static void SetColor(Material m, Color c)
    {
        if (m == null) return;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
    }

    // URP Unlit 은 기본이 불투명이라 알파 블렌딩을 손으로 켜 줘야 한다.
    // 세 군데(고스트 · 착탄 원판 · 그 외)에서 똑같이 쓰던 코드를 여기로 모았다.
    public static void MakeTransparent(Material m)
    {
        if (m == null || !m.HasProperty("_Surface")) return;

        m.SetFloat("_Surface", 1f);   // 0 = Opaque, 1 = Transparent
        m.SetFloat("_Blend", 0f);     // 0 = Alpha
        m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    static void Warn(string msg)
    {
        if (warned) return;
        warned = true;
        Debug.LogWarning($"[RuntimeShaders] {msg}");
    }
}
