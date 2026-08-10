using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

// ─────────────────────────────────────────────────────────────────────────────
// 게임 HUD (uGUI). 2026-08-09 신설 · 2026-08-09 스프라이트/모션 적용.
//
// 왜 코드로 만드나:
//   Canvas 계층을 인스펙터에서 손으로 만들면 요소 하나 옮길 때마다 씬을 다시 저장해야 하고,
//   버전 관리에서 차이를 읽기도 어렵다. 여기서는 Awake 때 전부 코드로 짓는다.
//   → 씬에는 빈 GameObject 하나만 있으면 되고, 레이아웃 수정이 코드 수정으로 끝난다.
//
// 스프라이트:
//   인스펙터의 슬롯을 비워두면 예전처럼 **단색 사각형**으로 그려진다.
//   Kenney UI Pack 스프라이트를 꽂으면 9-slice(Sliced)로 전환된다.
//   → 에셋 없이도 동작하고, 꽂는 즉시 좋아진다.
//
// 옛 OnGUI HUD와의 관계:
//   이 컴포넌트가 살아 있으면 GameUI.LegacyHudEnabled = false 가 되어
//   GameManager / RoundManager / BuildManager 의 OnGUI가 스스로 그리기를 멈춘다.
//   **씬에서 이 오브젝트를 지우면 옛 HUD가 그대로 돌아온다.**
//
// 폰트: 한글이 필요하다. Kenney 번들 폰트에는 한글이 없으므로 쓰면 안 된다.
//   비워두면 OS 폰트(맑은 고딕 등)를 찾아 쓴다.
// ─────────────────────────────────────────────────────────────────────────────
public class GameUI : MonoBehaviour
{
    public static bool LegacyHudEnabled = true;

    [Header("기준 해상도")]
    public Vector2 referenceResolution = new Vector2(1280f, 720f);

    [Header("폰트 (비우면 OS 한글 폰트를 자동으로 찾음)")]
    public Font font;
    public int baseFontSize = 18;

    // ── 스프라이트 ──────────────────────────────────────────────────────────
    // 전부 비워도 동작한다(단색 사각형). Kenney UI Pack 기준 추천 짝은 주석에.
    [Header("스프라이트 — 비우면 단색 사각형")]
    public Sprite panelSprite;        // Grey/Default/button_rectangle_depth_flat
    public Sprite cardSprite;         // Grey/Default/button_rectangle_flat
    public Sprite cardSelectedSprite; // Yellow/Default/button_rectangle_depth_gradient
    public Sprite buttonSprite;       // Blue/Default/button_rectangle_depth_gradient
    public Sprite buttonGoodSprite;   // Green/Default/button_rectangle_depth_gradient
    public Sprite buttonBadSprite;    // Red/Default/button_rectangle_depth_gradient
    public Sprite buttonDimSprite;    // Grey/Default/button_rectangle_flat
    public Sprite barTrackSprite;     // Grey/Default/button_rectangle_flat
    public Sprite barFillSprite;      // Green/Default/button_rectangle_flat
    public Sprite barFillLowSprite;   // Red/Default/button_rectangle_flat

    // ⚠️ 아래 기본값은 **Kenney의 밝은 회색 판** 위에 올린다는 전제다.
    //    밝은 판에 흰 글씨를 쓰면 하나도 안 보인다 → 판 위 글씨는 전부 '어두운' 색이다.
    //    스프라이트를 전부 비우면 판이 다시 어두워지므로, autoPalette가 밝은 글씨로 되돌린다.
    [Header("색 (스프라이트가 있으면 '틴트', 없으면 '면 색')")]
    public bool autoPalette = true;
    public Color panelColor = new Color(0.07f, 0.08f, 0.11f, 0.82f);  // 스프라이트 없을 때의 판 색
    public Color accentColor = new Color(0.62f, 0.40f, 0.02f);   // 골드 (밝은 판 위)
    public Color healthColor = new Color(0.35f, 0.85f, 0.45f);
    public Color healthLowColor = new Color(0.80f, 0.16f, 0.14f);
    public Color okColor = new Color(0.10f, 0.48f, 0.20f);       // 성공 문구 (밝은 판 위)
    public Color textColor = new Color(0.12f, 0.13f, 0.17f);     // 판 위 본문
    public Color dimColor = new Color(0.42f, 0.45f, 0.51f);      // 살 수 없을 때
    public Color onButtonText = Color.white;                     // 색 있는 버튼(초록·빨강·파랑) 위 글씨
    public Color selectColor = new Color(0.45f, 0.30f, 0.02f);   // 선택된 카드의 번호
    // Kenney 회색 판은 거의 흰색이라 그대로 쓰면 화면이 눈부시다. 틴트로 눌러 준다.
    public Color panelTint = new Color(0.78f, 0.80f, 0.84f);      // 큰 판·체력바 트랙
    public Color spriteTint = new Color(0.88f, 0.90f, 0.93f);     // 타워 카드 등 회색 조각
    public Color spriteDimTint = new Color(0.62f, 0.64f, 0.68f);  // 비활성

    [Header("글자")]
    public bool boldText = true;
    public bool outlineText = true;              // 배경과 반대색 테두리 — 어떤 판 위에서도 읽힌다
    [Range(0f, 2f)] public float outlineWidth = 1.1f;
    [Range(0.8f, 1.6f)] public float fontScale = 1.15f;
    // Kenney의 'depth' 스프라이트는 아래쪽에 그림자 턱이 있다.
    // 사각형 한가운데에 글씨를 두면 그 턱만큼 아래로 내려가 보이므로 위로 올려 준다.
    [Range(0f, 10f)] public float depthLift = 4f;

    // 웨이브 예고 판은 맵 한가운데 위를 가린다. 반투명으로 두면 뒤가 비쳐 답답하지 않다.
    // 글씨는 별도 그래픽이라 이 값에 영향받지 않는다 — 판만 흐려지고 글자는 또렷하다.
    [Range(0.2f, 1f)] public float previewAlpha = 0.62f;

    [Header("모션")]
    public float goldCountSpeed = 6f;    // 골드 숫자가 목표값을 따라가는 속도
    public float healthLerpSpeed = 8f;   // 체력바가 줄어드는 속도
    public float damageFlashTime = 0.35f;
    public float pressScale = 0.94f;     // 버튼 누를 때 줄어드는 비율

    [Header("타워 아이콘 (3D 프리팹을 찍어서 씀)")]
    public bool use3dIcons = true;
    public int iconSize = 128;
    public float iconCameraSize = 1.25f;   // 1보다 크면 여백이 생긴다
    public Vector3 iconEuler = new Vector3(22f, 35f, 0f);
    public float iconLightIntensity = 3.2f;

    // ── 참조 ────────────────────────────────────────────────────────────────
    GameManager gm;
    RoundManager rm;
    BuildManager bm;
    TowerUpgradeManager upm;

    // ── 위젯 ────────────────────────────────────────────────────────────────
    Canvas canvas;
    Text stageText, roundText, goldText, healthText, phaseText;
    RectTransform healthFill;
    Image healthFillImg;

    RectTransform previewPanel;
    Text previewTitle, previewBody;

    readonly List<TowerSlot> slots = new List<TowerSlot>();
    readonly List<UpgradeRow> upgradeRows = new List<UpgradeRow>();

    RectTransform selectedPanel;
    Text selectedLabel;
    Button sellButton, moveButton;
    Text sellLabel;

    readonly List<Button> speedButtons = new List<Button>();
    readonly List<Image> speedImages = new List<Image>();
    readonly List<Text> speedLabels = new List<Text>();
    RectTransform skipRect;

    // 모션 상태
    float shownGold;
    float shownHealth = 1f;
    float flashUntil;
    int lastHealth = int.MinValue;

    class TowerSlot
    {
        public int index;
        public Image bg, chip;
        public RawImage icon;
        public Text name, cost, key;
    }

    class UpgradeRow
    {
        public string typeName;
        public Image bg;
        public Text label;
        public Button button;
    }

    // ═══════════════════════════════════════════════════════════════════════
    void Awake()
    {
        LegacyHudEnabled = false;
        ApplyAutoPalette();

        gm = GameManager.Instance != null ? GameManager.Instance : FindFirstObjectByType<GameManager>();
        rm = FindFirstObjectByType<RoundManager>();
        bm = FindFirstObjectByType<BuildManager>();
        upm = TowerUpgradeManager.Instance;

        EnsureFont();
        EnsureEventSystem();
        BuildCanvas();
        BuildTopLeft();
        BuildPreview();
        BuildSpeedRow();
        BuildTowerBar();
        BuildUpgradePanel();
        BuildSelectedPanel();

        if (gm != null) { shownGold = gm.gold; lastHealth = gm.playerHealth; }
    }

    void OnDestroy() { LegacyHudEnabled = true; }

    // 스프라이트를 전부 비우면 판이 다시 '어두운 반투명 사각형'이 된다.
    // 그때까지 어두운 글씨를 쓰면 아무것도 안 보이므로 밝은 글씨로 되돌린다.
    void ApplyAutoPalette()
    {
        if (!autoPalette || panelSprite != null) return;
        textColor    = new Color(0.94f, 0.95f, 0.98f);
        dimColor     = new Color(0.62f, 0.65f, 0.72f);
        accentColor  = new Color(1f, 0.78f, 0.28f);
        healthLowColor = new Color(0.92f, 0.30f, 0.28f);
        okColor = new Color(0.35f, 0.85f, 0.45f);
        selectColor  = new Color(1f, 0.82f, 0.30f);
        spriteDimTint = new Color(0.55f, 0.57f, 0.62f);
    }

    void EnsureFont()
    {
        if (font != null) return;

        // ⚠️ Kenney 번들 폰트(Kenney Future)는 라틴 전용이라 한글이 전부 깨진다. 쓰지 말 것.
        string[] candidates = { "Malgun Gothic", "맑은 고딕", "NanumGothic", "Noto Sans KR", "Gulim", "Dotum" };
        foreach (string c in candidates)
        {
            try
            {
                Font f = Font.CreateDynamicFontFromOSFont(c, baseFontSize);
                if (f != null) { font = f; return; }
            }
            catch { }
        }

        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (font == null) Debug.LogWarning("[GameUI] 폰트를 찾지 못했습니다. 인스펙터 Font 슬롯에 한글 TTF를 넣어주세요.");
    }

    // 새 Input System 프로젝트라 StandaloneInputModule이 아니라 InputSystemUIInputModule이어야 클릭이 먹는다.
    void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        GameObject es = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        es.transform.SetParent(transform, false);
    }

    void BuildCanvas()
    {
        GameObject go = new GameObject("HUD Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        go.transform.SetParent(transform, false);

        canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        CanvasScaler cs = go.GetComponent<CanvasScaler>();
        cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        cs.referenceResolution = referenceResolution;
        cs.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        cs.matchWidthOrHeight = 0.5f;
    }

    // ── 좌상단: 스테이지 · 라운드 · 체력바 · 골드 ────────────────────────────
    void BuildTopLeft()
    {
        RectTransform panel = Panel("TopLeft", canvas.transform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                                    new Vector2(16f, -16f), new Vector2(300f, -134f), panelColor, panelSprite, panelTint);

        stageText = Label("Stage", panel, 22, TextAnchor.UpperLeft, textColor);
        Place(stageText.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(14f, -12f), new Vector2(-14f, -36f));

        roundText = Label("Round", panel, 15, TextAnchor.UpperRight, dimColor);
        Place(roundText.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(14f, -14f), new Vector2(-14f, -34f));

        RectTransform hpBg = Panel("HpBg", panel, new Vector2(0f, 1f), new Vector2(1f, 1f),
                                   new Vector2(14f, -44f), new Vector2(-14f, -70f),
                                   new Color(0.03f, 0.03f, 0.05f, 0.9f), barTrackSprite, panelTint);

        healthFill = Panel("HpFill", hpBg, new Vector2(0f, 0f), new Vector2(1f, 1f),
                           Vector2.zero, Vector2.zero, healthColor, barFillSprite);
        healthFillImg = healthFill.GetComponent<Image>();

        // 체력이 낮으면 이 글씨가 초록 채움이 아니라 회색 트랙 위에 놓인다.
        // 어두운 글씨 + 흰 테두리로 두면 두 배경 모두에서 읽힌다.
        healthText = Label("HpText", hpBg, 14, TextAnchor.MiddleCenter, textColor);
        Stretch(healthText.rectTransform);

        goldText = Label("Gold", panel, 20, TextAnchor.MiddleLeft, accentColor);
        Place(goldText.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(14f, -76f), new Vector2(-14f, -104f));

        // 예전엔 배경 없이 3D 화면 위에 그냥 얹었는데, 맵 색에 따라 글씨가 묻혔다.
        // 작은 판을 하나 깔아 어디서든 같은 대비를 유지한다.
        RectTransform phaseBg = Panel("PhaseBg", canvas.transform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                                      new Vector2(16f, -140f), new Vector2(252f, -180f), panelColor, panelSprite, panelTint);
        phaseText = Label("Phase", phaseBg, 17, TextAnchor.MiddleCenter, textColor);
        AlignLabel(phaseText, panelSprite);
    }

    // ── 상단 중앙: 웨이브 예고 ──────────────────────────────────────────────
    void BuildPreview()
    {
        previewPanel = Panel("Preview", canvas.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                             new Vector2(-320f, -16f), new Vector2(320f, -100f),
                             new Color(0.10f, 0.09f, 0.06f, previewAlpha), panelSprite,
                             new Color(panelTint.r, panelTint.g, panelTint.b, previewAlpha));

        previewTitle = Label("Title", previewPanel, 19, TextAnchor.MiddleCenter, accentColor);
        Place(previewTitle.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(10f, -10f), new Vector2(-10f, -36f));

        previewBody = Label("Body", previewPanel, 16, TextAnchor.MiddleCenter, textColor);
        Place(previewBody.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(10f, -38f), new Vector2(-10f, -78f));

        previewPanel.gameObject.SetActive(false);
    }

    // ── 상단 우측: 배속 + 바로 시작 ─────────────────────────────────────────
    void BuildSpeedRow()
    {
        if (rm == null || rm.SpeedOptions == null) return;

        for (int i = 0; i < rm.SpeedOptions.Length; i++)
        {
            int idx = i;
            float w = 48f, gap = 6f;
            float right = -16f - (rm.SpeedOptions.Length - 1 - i) * (w + gap);

            RectTransform r = Panel($"Speed{i}", canvas.transform, new Vector2(1f, 1f), new Vector2(1f, 1f),
                                    new Vector2(right - w, -16f), new Vector2(right, -48f), panelColor, buttonDimSprite);
            Button b = MakeButton(r, () => { if (rm != null) rm.UiSetSpeed(idx); });

            Text t = Label("L", r, 15, TextAnchor.MiddleCenter, textColor);
            Stretch(t.rectTransform);
            t.text = $"{rm.SpeedOptions[i]:0.#}x";

            speedButtons.Add(b);
            speedImages.Add(r.GetComponent<Image>());
            speedLabels.Add(t);
        }

        skipRect = Panel("Skip", canvas.transform, new Vector2(1f, 1f), new Vector2(1f, 1f),
                         new Vector2(-16f - 200f, -54f), new Vector2(-16f, -86f), panelColor, buttonSprite);
        MakeButton(skipRect, () => { if (rm != null) rm.UiSkip(); });
        Text st = Label("L", skipRect, 14, TextAnchor.MiddleCenter, onButtonText);
        Stretch(st.rectTransform);
        AlignLabel(st, buttonSprite);
        st.text = "▶ 바로 시작  (Space)";
    }

    // ── 하단 중앙: 타워 선택 바 ─────────────────────────────────────────────
    void BuildTowerBar()
    {
        if (bm == null || bm.towerPrefabs == null) return;

        int n = 0;
        foreach (GameObject p in bm.towerPrefabs) if (p != null) n++;
        if (n == 0) return;

        float w = 136f, h = 120f, gap = 10f;
        float total = n * w + (n - 1) * gap;
        float x0 = -total * 0.5f;

        int slot = 0;
        for (int i = 0; i < bm.towerPrefabs.Length; i++)
        {
            GameObject prefab = bm.towerPrefabs[i];
            if (prefab == null) continue;

            Tower td = prefab.GetComponent<Tower>();
            string nm = (td != null && !string.IsNullOrEmpty(td.towerName)) ? td.towerName : prefab.name;

            float x = x0 + slot * (w + gap);
            RectTransform r = Panel($"Tower{i}", canvas.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                                    new Vector2(x, 16f + h), new Vector2(x + w, 16f), panelColor, cardSprite);

            int captured = i;
            MakeButton(r, () => { if (bm != null) bm.UiSelectTower(captured); });

            TowerSlot s = new TowerSlot { index = i, bg = r.GetComponent<Image>() };

            RectTransform iconRect = MakeRect("Icon", r);
            Place(iconRect, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(8f, -8f), new Vector2(-8f, -68f));

            Texture tex = use3dIcons ? TowerIconMaker.Render(prefab, iconSize, iconCameraSize, iconEuler, iconLightIntensity) : null;
            if (tex != null)
            {
                s.icon = iconRect.gameObject.AddComponent<RawImage>();
                s.icon.texture = tex;
                s.icon.raycastTarget = false;
            }
            else
            {
                s.chip = iconRect.gameObject.AddComponent<Image>();
                s.chip.color = GuessTowerColor(prefab);
                s.chip.raycastTarget = false;
            }

            s.key = Label("Key", r, 13, TextAnchor.UpperLeft, dimColor);
            Place(s.key.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(10f, -6f), new Vector2(42f, -26f));
            s.key.text = $"{slot + 1}";

            s.name = Label("Name", r, 15, TextAnchor.MiddleCenter, textColor);
            Place(s.name.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(4f, 52f), new Vector2(-4f, 30f));
            s.name.text = nm;

            s.cost = Label("Cost", r, 16, TextAnchor.MiddleCenter, accentColor);
            Place(s.cost.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(4f, 30f), new Vector2(-4f, 8f));

            slots.Add(s);
            slot++;
        }
    }

    // ── 하단 우측: 강화 ─────────────────────────────────────────────────────
    void BuildUpgradePanel()
    {
        if (bm == null || bm.towerPrefabs == null) return;

        HashSet<string> seen = new HashSet<string>();
        List<string> names = new List<string>();
        foreach (GameObject p in bm.towerPrefabs)
        {
            if (p == null) continue;
            Tower t = p.GetComponent<Tower>();
            string nm = (t != null && !string.IsNullOrEmpty(t.towerName)) ? t.towerName : p.name;
            if (seen.Add(nm)) names.Add(nm);
        }
        if (names.Count == 0) return;

        float rowH = 32f, gap = 5f, w = 248f;
        float panelH = 34f + names.Count * (rowH + gap) + 8f;

        RectTransform panel = Panel("Upgrades", canvas.transform, new Vector2(1f, 0f), new Vector2(1f, 0f),
                                    new Vector2(-16f - w, 16f + panelH), new Vector2(-16f, 16f), panelColor, panelSprite, panelTint);

        Text head = Label("Head", panel, 15, TextAnchor.MiddleLeft, dimColor);
        Place(head.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(12f, -8f), new Vector2(-12f, -30f));
        head.text = "강화 (종류 전체)";

        for (int i = 0; i < names.Count; i++)
        {
            string nm = names[i];
            float top = -32f - i * (rowH + gap);

            RectTransform r = Panel($"Up{i}", panel, new Vector2(0f, 1f), new Vector2(1f, 1f),
                                    new Vector2(10f, top), new Vector2(-10f, top - rowH),
                                    new Color(0.16f, 0.18f, 0.24f, 0.95f), buttonGoodSprite);
            Button b = MakeButton(r, () => { if (bm != null) bm.UiUpgradeType(nm); });

            Text t = Label("L", r, 14, TextAnchor.MiddleCenter, onButtonText);
            Stretch(t.rectTransform);

            upgradeRows.Add(new UpgradeRow { typeName = nm, bg = r.GetComponent<Image>(), label = t, button = b });
        }
    }

    // ── 하단 좌측: 선택한 타워 (판매 / 이동) ────────────────────────────────
    void BuildSelectedPanel()
    {
        selectedPanel = Panel("Selected", canvas.transform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                              new Vector2(16f, 16f + 96f), new Vector2(16f + 264f, 16f), panelColor, panelSprite, panelTint);

        selectedLabel = Label("Label", selectedPanel, 16, TextAnchor.MiddleLeft, textColor);
        Place(selectedLabel.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(12f, -10f), new Vector2(-12f, -38f));

        RectTransform sr = Panel("Sell", selectedPanel, new Vector2(0f, 0f), new Vector2(0.5f, 0f),
                                 new Vector2(12f, 48f), new Vector2(-5f, 12f),
                                 new Color(0.42f, 0.18f, 0.18f, 0.95f), buttonBadSprite);
        sellButton = MakeButton(sr, () => { if (bm != null) bm.UiSell(); });
        sellLabel = Label("L", sr, 14, TextAnchor.MiddleCenter, onButtonText);
        Stretch(sellLabel.rectTransform);
        AlignLabel(sellLabel, buttonBadSprite);

        RectTransform mr = Panel("Move", selectedPanel, new Vector2(0.5f, 0f), new Vector2(1f, 0f),
                                 new Vector2(5f, 48f), new Vector2(-12f, 12f),
                                 new Color(0.18f, 0.32f, 0.42f, 0.95f), buttonSprite);
        moveButton = MakeButton(mr, () => { if (bm != null) bm.UiBeginMove(); });
        Text ml = Label("L", mr, 14, TextAnchor.MiddleCenter, onButtonText);
        Stretch(ml.rectTransform);
        AlignLabel(ml, buttonSprite);
        ml.text = "이동 (무료)";

        selectedPanel.gameObject.SetActive(false);
    }

    // ═══════════════════════════════════════════════════════════════════════
    void Update()
    {
        if (gm == null) gm = GameManager.Instance;
        if (upm == null) upm = TowerUpgradeManager.Instance;

        UpdateResources();
        UpdatePhase();
        UpdateTowerBar();
        UpdateUpgrades();
        UpdateSelected();
        UpdateSpeed();
    }

    void UpdateResources()
    {
        if (gm == null) return;

        stageText.text = $"스테이지 {GameManager.stage}";

        // 골드는 목표값으로 스르륵 따라간다 (숫자가 튀는 것보다 변화가 눈에 잘 들어온다)
        shownGold = Mathf.MoveTowards(shownGold, gm.gold,
                                      Mathf.Max(1f, Mathf.Abs(gm.gold - shownGold)) * goldCountSpeed * Time.unscaledDeltaTime);
        if (Mathf.Abs(shownGold - gm.gold) < 0.6f) shownGold = gm.gold;
        goldText.text = $"◆ {Mathf.RoundToInt(shownGold):N0}";

        int maxH = Mathf.Max(1, gm.MaxHealth());
        float ratio = Mathf.Clamp01(gm.playerHealth / (float)maxH);

        // 체력이 줄면 잠깐 번쩍인다 — 숫자만으로는 맞았다는 걸 놓치기 쉽다
        if (lastHealth != int.MinValue && gm.playerHealth < lastHealth) flashUntil = Time.unscaledTime + damageFlashTime;
        lastHealth = gm.playerHealth;

        shownHealth = Mathf.MoveTowards(shownHealth, ratio, healthLerpSpeed * Time.unscaledDeltaTime);

        bool low = ratio <= 0.35f;
        Sprite want = (low && barFillLowSprite != null) ? barFillLowSprite : barFillSprite;
        Color flat = Color.Lerp(healthLowColor, healthColor, Mathf.Clamp01(ratio * 1.6f));
        Color tint = (want != null) ? Color.white : flat;
        if (Time.unscaledTime < flashUntil) tint = Color.Lerp(tint, Color.white, 0.75f);

        // 체력바만 Skin()을 안 쓴다.
        // 9-slice(Sliced)로 그리면 체력이 낮아 폭이 테두리(8px)보다 좁아질 때 모서리가 겹쳐 뭉개진다.
        // 스프라이트가 있으면 **Filled**로 그려 모양은 그대로 두고 가로로 깎아낸다.
        // 스프라이트가 없으면 예전처럼 앵커 폭을 줄이는 단색 사각형.
        if (healthFillImg != null)
        {
            if (want != null)
            {
                if (healthFillImg.sprite != want) healthFillImg.sprite = want;
                healthFillImg.type = Image.Type.Filled;
                healthFillImg.fillMethod = Image.FillMethod.Horizontal;
                healthFillImg.fillOrigin = (int)Image.OriginHorizontal.Left;
                healthFillImg.fillAmount = Mathf.Clamp01(shownHealth);
                healthFillImg.color = tint;
                healthFill.anchorMax = Vector2.one;      // 폭은 fillAmount가 담당
            }
            else
            {
                healthFillImg.sprite = null;
                healthFillImg.type = Image.Type.Simple;
                healthFillImg.color = tint;
                healthFill.anchorMax = new Vector2(Mathf.Max(0.0001f, shownHealth), 1f);
            }
        }

        healthText.text = $"{gm.playerHealth} / {maxH}";
        if (rm != null) roundText.text = $"R {rm.CurrentRound + 1}/{rm.RoundsPerStage}";
    }

    void UpdatePhase()
    {
        if (rm == null) { phaseText.text = ""; return; }

        switch (rm.Phase)
        {
            case RoundManager.UiPhase.Prep:
                phaseText.text = $"정비 {rm.PrepRemaining:0.0}초"; SetTextColor(phaseText, accentColor); break;
            case RoundManager.UiPhase.Preview:
                phaseText.text = $"웨이브 예고 {rm.PreviewRemaining:0.0}초"; SetTextColor(phaseText, accentColor); break;
            case RoundManager.UiPhase.Spawning:
            case RoundManager.UiPhase.Fighting:
                phaseText.text = $"남은 적 {rm.AliveEnemies}"; SetTextColor(phaseText, textColor); break;
            case RoundManager.UiPhase.StageClear:
                phaseText.text = "스테이지 클리어!"; SetTextColor(phaseText, okColor); break;
            case RoundManager.UiPhase.StageFail:
                phaseText.text = "스테이지 실패..."; SetTextColor(phaseText, healthLowColor); break;
        }

        bool showPreview = rm.Phase == RoundManager.UiPhase.Preview && !string.IsNullOrEmpty(rm.WavePreviewText);
        if (previewPanel.gameObject.activeSelf != showPreview) previewPanel.gameObject.SetActive(showPreview);
        if (showPreview)
        {
            previewTitle.text = $"다음 웨이브 — {rm.PreviewRemaining:0.0}초 후 시작";
            previewBody.text = rm.WavePreviewText;
        }
    }

    void UpdateTowerBar()
    {
        if (bm == null || gm == null) return;

        foreach (TowerSlot s in slots)
        {
            GameObject prefab = bm.towerPrefabs[s.index];
            if (prefab == null) continue;

            Tower td = prefab.GetComponent<Tower>();
            string nm = (td != null && !string.IsNullOrEmpty(td.towerName)) ? td.towerName : prefab.name;
            int cost = gm.GetScaledCost(td != null ? td.cost : 0, nm);
            bool afford = gm.CanAfford(cost);
            bool selected = (bm.SelectedIndex == s.index);

            s.cost.text = $"{cost:N0}";
            SetTextColor(s.cost, afford ? accentColor : healthLowColor);
            SetTextColor(s.name, afford ? textColor : dimColor);
            if (s.icon != null) s.icon.color = afford ? Color.white : new Color(1f, 1f, 1f, 0.55f);
            if (s.chip != null) s.chip.color = afford ? GuessTowerColor(prefab) : dimColor;

            Sprite want = selected && cardSelectedSprite != null ? cardSelectedSprite : cardSprite;
            Color fallback = selected ? new Color(selectColor.r, selectColor.g, selectColor.b, 0.30f) : panelColor;
            Skin(s.bg, want, fallback, !afford ? spriteDimTint : (selected ? Color.white : spriteTint));

            SetTextColor(s.key, selected ? selectColor : dimColor);
        }
    }

    void UpdateUpgrades()
    {
        if (upm == null || gm == null) return;

        foreach (UpgradeRow row in upgradeRows)
        {
            int lv = upm.GetLevel(row.typeName);
            bool maxed = upm.IsMaxed(row.typeName);
            int cost = maxed ? 0 : upm.NextCost(row.typeName);
            bool afford = !maxed && gm.CanAfford(cost);

            row.label.text = maxed
                ? $"{row.typeName}  Lv{lv}  (MAX)"
                : $"{row.typeName}  Lv{lv}  →  {cost:N0}";
            SetTextColor(row.label, maxed ? dimColor : (afford ? onButtonText : healthLowColor));
            row.button.interactable = afford;

            Sprite want = afford ? buttonGoodSprite : buttonDimSprite;
            Color fallback = afford ? new Color(0.18f, 0.24f, 0.20f, 0.95f) : new Color(0.14f, 0.15f, 0.19f, 0.9f);
            Skin(row.bg, want, fallback, afford ? Color.white : spriteDimTint);
            AlignLabel(row.label, want);
        }
    }

    void UpdateSelected()
    {
        if (bm == null) return;

        Tower moving = bm.MovingTower;
        Tower sel = bm.SelectedTower;
        bool show = moving != null || sel != null;

        if (selectedPanel.gameObject.activeSelf != show) selectedPanel.gameObject.SetActive(show);
        if (!show) return;

        if (moving != null)
        {
            selectedLabel.text = $"이동 중: {moving.towerName}\n놓을 칸 좌클릭 · 우클릭 취소";
            sellButton.gameObject.SetActive(false);
            moveButton.gameObject.SetActive(false);
            return;
        }

        sellButton.gameObject.SetActive(true);
        moveButton.gameObject.SetActive(true);
        int lv = (upm != null) ? upm.GetLevel(sel.towerName) : 0;
        selectedLabel.text = $"선택: {sel.towerName}  Lv{lv}";
        sellLabel.text = $"판매 +{bm.UiSellRefund(sel):N0}";
    }

    void UpdateSpeed()
    {
        if (rm == null || speedButtons.Count == 0) return;

        bool on = rm.HumanUxEnabled && rm.SpeedControlAllowed;
        for (int i = 0; i < speedButtons.Count; i++)
        {
            if (speedButtons[i].gameObject.activeSelf != on) speedButtons[i].gameObject.SetActive(on);
            bool cur = (i == rm.SpeedIndex);
            SetTextColor(speedLabels[i], cur ? onButtonText : textColor);

            Sprite want = cur && buttonSprite != null ? buttonSprite : buttonDimSprite;
            Skin(speedImages[i], want, cur ? new Color(selectColor.r, selectColor.g, selectColor.b, 0.35f) : panelColor,
                 cur ? Color.white : spriteDimTint);
            AlignLabel(speedLabels[i], want);
        }

        bool skippable = on && (rm.Phase == RoundManager.UiPhase.Prep || rm.Phase == RoundManager.UiPhase.Preview);
        if (skipRect.gameObject.activeSelf != skippable) skipRect.gameObject.SetActive(skippable);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 위젯 헬퍼
    static RectTransform MakeRect(string name, Transform parent)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    // 규약: leftTop = (왼쪽, 위), rightBottom = (오른쪽, 아래).
    // 유니티 Y축이 위로 향하므로 '위'가 '아래'보다 큰 값이어야 한다(예: top -16, bottom -134).
    // Unity의 offsetMin/Max는 (left,bottom)/(right,top) 순서라 여기서 뒤집어 준다.
    static void Place(RectTransform r, Vector2 aMin, Vector2 aMax, Vector2 leftTop, Vector2 rightBottom)
    {
        r.anchorMin = aMin; r.anchorMax = aMax;
        r.offsetMin = new Vector2(leftTop.x, rightBottom.y);
        r.offsetMax = new Vector2(rightBottom.x, leftTop.y);
    }

    // 아래 턱이 있는 스프라이트인지 (Kenney는 아래 테두리를 위보다 크게 잡아 둔다)
    static bool HasDepthLip(Sprite s) => s != null && s.border.y > s.border.w;

    // Stretch로 꽉 채운 글씨를 턱 높이의 절반만큼 위로 올린다.
    void AlignLabel(Text t, Sprite s)
    {
        if (t == null) return;
        float lift = HasDepthLip(s) ? depthLift : 0f;
        RectTransform r = t.rectTransform;
        if (Mathf.Approximately(r.offsetMin.y, lift)) return;
        r.offsetMin = new Vector2(r.offsetMin.x, lift);
        r.offsetMax = new Vector2(r.offsetMax.x, lift);
    }

    static void Stretch(RectTransform r)
    {
        r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
        r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero;
    }

    // 스프라이트가 있으면 9-slice로, 없으면 단색으로 그린다.
    // 이 한 곳만 통과하므로 '에셋 없이도 동작' 보장이 흩어지지 않는다.
    static void Skin(Image img, Sprite sprite, Color flatColor, Color tint)
    {
        if (img == null) return;
        if (sprite != null)
        {
            if (img.sprite != sprite) { img.sprite = sprite; img.type = Image.Type.Sliced; }
            img.color = tint;
        }
        else
        {
            img.sprite = null;
            img.type = Image.Type.Simple;
            img.color = flatColor;
        }
    }

    static RectTransform Panel(string name, Transform parent, Vector2 aMin, Vector2 aMax,
                               Vector2 leftTop, Vector2 rightBottom, Color c, Sprite sprite = null,
                               Color? tint = null)
    {
        RectTransform r = MakeRect(name, parent);
        Image img = r.gameObject.AddComponent<Image>();
        Skin(img, sprite, c, tint ?? Color.white);
        Place(r, aMin, aMax, leftTop, rightBottom);
        return r;
    }

    Button MakeButton(RectTransform r, UnityEngine.Events.UnityAction onClick)
    {
        Button b = r.gameObject.AddComponent<Button>();
        b.targetGraphic = r.GetComponent<Image>();
        b.transition = Selectable.Transition.None;   // 색 전이는 Skin이 담당한다
        b.onClick.AddListener(onClick);
        r.gameObject.AddComponent<UiPress>().Setup(pressScale);
        return b;
    }

    Text Label(string name, Transform parent, int size, TextAnchor anchor, Color c)
    {
        RectTransform r = MakeRect(name, parent);
        Text t = r.gameObject.AddComponent<Text>();
        t.font = font;
        t.fontSize = Mathf.Max(8, Mathf.RoundToInt(size * fontScale));
        t.fontStyle = boldText ? FontStyle.Bold : FontStyle.Normal;
        t.alignment = anchor;
        t.color = c;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        Stretch(r);

        // 테두리 색은 글자 밝기를 보고 정한다.
        // 어두운 글씨 → 흰 테두리, 밝은 글씨 → 검은 테두리.
        // 이렇게 하면 초록 버튼이든 회색 판이든 **한 가지 규칙으로** 다 읽힌다.
        if (outlineText && outlineWidth > 0.01f)
        {
            Outline ol = r.gameObject.AddComponent<Outline>();
            float lum = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
            ol.effectColor = lum > 0.5f ? new Color(0f, 0f, 0f, 0.55f) : new Color(1f, 1f, 1f, 0.45f);
            ol.effectDistance = new Vector2(outlineWidth, -outlineWidth);
            ol.useGraphicAlpha = false;
        }
        return t;
    }

    // 색을 바꾼 뒤 테두리 대비도 같이 뒤집어 준다 (예: 살 수 있음 ↔ 없음).
    static void SetTextColor(Text t, Color c)
    {
        if (t == null || t.color == c) return;
        t.color = c;
        Outline ol = t.GetComponent<Outline>();
        if (ol == null) return;
        float lum = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
        ol.effectColor = lum > 0.5f ? new Color(0f, 0f, 0f, 0.55f) : new Color(1f, 1f, 1f, 0.45f);
    }

    static Color GuessTowerColor(GameObject prefab)
    {
        foreach (Renderer r in prefab.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || r.sharedMaterial == null) continue;
            if (r.sharedMaterial.HasProperty("_BaseColor")) return r.sharedMaterial.GetColor("_BaseColor");
            if (r.sharedMaterial.HasProperty("_Color")) return r.sharedMaterial.GetColor("_Color");
        }
        return new Color(0.6f, 0.65f, 0.75f);
    }
}

// 버튼을 누르는 동안 살짝 눌리게. Time.timeScale이 0이거나 배속이어도 같은 속도로 보이도록
// unscaledDeltaTime을 쓴다.
public class UiPress : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
{
    float pressed = 0.94f;
    bool down;
    RectTransform rt;

    public void Setup(float scale) { pressed = Mathf.Clamp(scale, 0.5f, 1f); }

    void Awake() { rt = (RectTransform)transform; }

    public void OnPointerDown(PointerEventData e) { down = true; }
    public void OnPointerUp(PointerEventData e) { down = false; }
    public void OnPointerExit(PointerEventData e) { down = false; }

    void Update()
    {
        if (rt == null) return;
        float want = down ? pressed : 1f;
        Vector3 s = rt.localScale;
        float v = Mathf.MoveTowards(s.x, want, 6f * Time.unscaledDeltaTime);
        rt.localScale = new Vector3(v, v, 1f);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 타워 프리팹을 작은 RenderTexture로 한 번 찍어 UI 아이콘으로 쓴다.
//
// 왜 이 방식인가: 아이콘 스프라이트를 따로 그리면 타워를 바꿀 때마다 다시 그려야 한다.
//   실제 프리팹을 찍으면 **모델을 교체하는 순간 아이콘도 따라 바뀐다.**
//
// 어떻게 격리하나: 레이어를 새로 잡으면 프로젝트 설정을 건드려야 하므로,
//   맵에서 5000유닛 아래에 임시 무대를 세우고 그곳만 비추는 카메라로 찍는다.
//   조명도 **범위가 유한한 포인트 라이트**라 게임 화면에는 전혀 영향이 없다.
//   찍고 나면 무대를 즉시 파괴한다.
// ─────────────────────────────────────────────────────────────────────────────
public static class TowerIconMaker
{
    const float StageY = -5000f;

    public static Texture Render(GameObject prefab, int size, float camSize, Vector3 euler, float lightIntensity)
    {
        if (prefab == null) return null;

        GameObject stage = null;
        try
        {
            stage = new GameObject("~IconStage");
            stage.transform.position = new Vector3(0f, StageY, 0f);

            GameObject model = Object.Instantiate(prefab, stage.transform);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.Euler(0f, euler.y, 0f);

            // 로직이 돌면 안 된다 (타워가 아이콘 무대에서 적을 찾고 투사체를 쏜다)
            foreach (MonoBehaviour mb in model.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false;
            foreach (Collider c in model.GetComponentsInChildren<Collider>(true)) c.enabled = false;

            Bounds b = CalcBounds(model);
            if (b.size == Vector3.zero) { Object.DestroyImmediate(stage); return null; }

            GameObject camGo = new GameObject("~IconCam");
            camGo.transform.SetParent(stage.transform, false);
            Camera cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            // extents.magnitude(대각선)로 잡으면 길쭉한 모델일수록 과하게 작아진다.
            // 가장 긴 변을 기준으로 잡아야 타워마다 크기가 고르게 보인다.
            float half = Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z));
            cam.orthographicSize = Mathf.Max(0.05f, half * camSize);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 50f;
            cam.enabled = false;

            camGo.transform.rotation = Quaternion.Euler(euler.x, euler.y + 180f, 0f);
            camGo.transform.position = b.center - camGo.transform.forward * 10f;

            GameObject lightGo = new GameObject("~IconLight");
            lightGo.transform.SetParent(stage.transform, false);
            Light lit = lightGo.AddComponent<Light>();
            lit.type = LightType.Point;
            lit.range = 20f;
            lit.intensity = lightIntensity;
            lightGo.transform.position = b.center + new Vector3(2f, 3f, -3f);

            RenderTexture rt = new RenderTexture(size, size, 16, RenderTextureFormat.ARGB32);
            rt.Create();
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;

            Object.DestroyImmediate(stage);
            return rt;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[GameUI] 타워 아이콘 렌더 실패 ({prefab.name}) — 색 칩으로 대체합니다. {e.Message}");
            if (stage != null) Object.DestroyImmediate(stage);
            return null;
        }
    }

    // 실제로 '보이는 덩어리'만 센다.
    // 파티클/트레일/라인 렌더러는 경계가 실제 모델보다 훨씬 크게 잡히는 경우가 많아
    // (지원 타워처럼 이펙트를 달아둔 프리팹) 아이콘이 저 혼자 작게 찍힌다.
    // 꺼져 있는 오브젝트도 마찬가지로 빼야 한다.
    static Bounds CalcBounds(GameObject go)
    {
        bool any = false;
        Bounds b = new Bounds(go.transform.position, Vector3.zero);

        foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
            if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;

            if (!any) { b = r.bounds; any = true; }
            else b.Encapsulate(r.bounds);
        }
        return any ? b : new Bounds(go.transform.position, Vector3.zero);
    }
}
