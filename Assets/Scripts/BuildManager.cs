using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;   // 새 Input System

// 마우스로 타일을 클릭해 타워를 설치한다.
// 여러 타워 종류를 숫자키(1~5)로 골라서 설치할 수 있다.
// 업그레이드는 '종류 단위'(화면의 [강화] 버튼)로 한다.
// 설치된 타워를 클릭하면 그 타워의 '사거리'가 원으로 표시되고, 맵 밖을 클릭하면 꺼진다.
public class BuildManager : MonoBehaviour
{
    [Header("연결")]
    public GameObject[] towerPrefabs;  // 설치할 타워 종류들 (1~5번 키로 선택)
    public Camera cam;                 // 비우면 Camera.main 사용

    [Header("HUD 기준 해상도 (이 해상도에서 1배로 보이도록 고정)")]
    // OnGUI는 실제 픽셀 좌표라 창 크기에 따라 화면 대비 크기가 달라진다.
    // 기준 해상도를 정해 GUI.matrix로 배율을 걸면 어느 해상도에서도 같은 비율로 보인다.
    public Vector2 hudReferenceResolution = new Vector2(1280f, 720f);

    [Header("사거리 표시")]
    public Color rangeColor = new Color(1f, 0.92f, 0.2f, 0.9f);
    public float rangeLineWidth = 0.08f;
    public float rangeHeight = 0.15f;   // 원을 바닥에서 살짝 띄우는 높이

    private int selectedIndex = 0;     // 지금 선택된 타워 번호

    // 강화 버튼 영역 (여길 클릭하면 월드 설치가 안 되도록 가드)
    private Rect upgradePanelRect = new Rect(0, 0, 0, 0);

    // 사거리 표시용 원 (LineRenderer) + 지금 선택된 타워
    private LineRenderer rangeCircle;
    private Tower selectedTower;
    private const int RANGE_SEGMENTS = 48;

    [Header("설치 미리보기 (2026-08-02)")]
    // 좌클릭을 '누른 순간' 설치하지 않고, 누르고 있는 동안 설치 결과를 미리 보여준다.
    //   누름 → 미리보기 시작 / 드래그 → 칸마다 갱신 / 뗌 → 설치(골드 차감) / 우클릭 → 취소
    // 사거리가 타워마다 크게 달라서(폭탄 2.0 vs 지원 4.0) 짓고 나서야 아는 문제가 있었다.
    public Color previewOkColor  = new Color(0.25f, 1f, 0.35f, 0.45f); // 설치 가능 = 초록
    public Color previewBadColor = new Color(1f, 0.25f, 0.2f, 0.45f);  // 설치 불가 = 빨강

    [Header("타워 판매·이동 (2026-08-04 플레이테스트 반영)")]
    // 테스터 지적: "AI가 빠른 적을 계속 보내 속사 타워를 깔게 유도하고, 마지막에 탱커를 몰아 보낸다."
    // DDA는 잘못 학습한 게 아니다 — 우리가 준 보상을 가장 싸게 달성하는 방법을 찾았을 뿐이다.
    // 진짜 문제는 **플레이어에게 되돌릴 수단이 없다는 것**이었다:
    //   · 타워를 팔 수 없어서 유도당한 배치가 그대로 굳고
    //   · 강화가 종류 단위라 종류를 갈아타면 투자한 골드가 전부 날아간다.
    // 판매는 원가의 일부만 돌려주고(즉흥 리셋 방지), 이동은 무료로 둔다.
    // 되돌리는 비용이 크면 '유도당함'이 그대로 남기 때문.
    //
    // ※ 판매해도 GameManager의 설치 카운터(builtByType)는 줄이지 않는다.
    //   줄이면 "짓고 팔아서 가격 상승분을 되돌리기"가 최적 전략이 되어 가격 시스템이 무너진다.
    public float sellRefundRatio = 0.65f;  // 실제 지불액의 이 비율만큼 환급
    public bool allowMove = true;          // 이동 허용 (무료)

    private Tower movingTower;             // 이동 중인 타워 (null이면 이동 모드 아님)
    private Vector3 movingFrom;            // 이동 시작 위치 (취소 시 복귀)
    private Rect towerPanelRect = new Rect(0, 0, 0, 0);  // 판매/이동 버튼 영역 (월드 클릭 가드)

    private bool previewing;
    private GameObject ghost;        // 반투명 타워 실루엣
    private int ghostTypeIndex = -1; // 지금 고스트가 어떤 종류인지 (도중에 숫자키로 바꿀 수 있음)
    private Tile previewTile;
    private bool previewValid;
    private int previewCost;
    private static Material ghostMat;

    void Start()
    {
        if (cam == null) cam = Camera.main;
        HudScaler.reference = hudReferenceResolution;
    }

    void OnValidate()
    {
        if (hudReferenceResolution.x > 1f && hudReferenceResolution.y > 1f)
            HudScaler.reference = hudReferenceResolution;
    }

    void Update()
    {
        // 숫자키로 타워 종류 선택
        if (Keyboard.current != null)
        {
            var k = Keyboard.current;
            if (k.digit1Key.wasPressedThisFrame) SelectTower(0);
            if (k.digit2Key.wasPressedThisFrame) SelectTower(1);
            if (k.digit3Key.wasPressedThisFrame) SelectTower(2);
            if (k.digit4Key.wasPressedThisFrame) SelectTower(3);
            if (k.digit5Key.wasPressedThisFrame) SelectTower(4);
        }

        if (Mouse.current != null)
        {
            // ── 타워 이동 모드 ──
            // 설치 미리보기(누른 채 끌기)와 달리 '클릭해서 놓기'다.
            // 이동은 GUI 버튼으로 시작하는데, 버튼은 마우스를 뗄 때 눌리므로
            // 누른 채 끌기 방식이면 시작하자마자 취소돼 버린다.
            if (movingTower != null)
            {
                if (Mouse.current.rightButton.wasPressedThisFrame) CancelMove();
                else if (Mouse.current.leftButton.wasPressedThisFrame)
                {
                    Vector2 g = HudScaler.MouseToGui(Mouse.current.position.ReadValue());
                    if (!upgradePanelRect.Contains(g) && !towerPanelRect.Contains(g)) ConfirmMove();
                }
                else UpdateMovePreview();
            }
            // ── 설치 미리보기 진행 중 ──
            else if (previewing)
            {
                if (Mouse.current.rightButton.wasPressedThisFrame)      CancelPreview();   // 우클릭 = 취소
                else if (Mouse.current.leftButton.wasReleasedThisFrame) ConfirmPreview();  // 떼면 설치
                else if (Mouse.current.leftButton.isPressed)            UpdatePreview();   // 끌면 따라옴
                else                                                    CancelPreview();   // 안전장치(포커스 상실 등)
            }
            // ── 새로 누름 ──
            else if (Mouse.current.leftButton.wasPressedThisFrame)
            {
                // GUI.matrix로 배율이 걸려 있으므로 마우스도 같은 좌표계로 변환해야
                // 버튼 영역 판정이 어긋나지 않는다.
                Vector2 gui = HudScaler.MouseToGui(Mouse.current.position.ReadValue());
                if (!upgradePanelRect.Contains(gui) && !towerPanelRect.Contains(gui)) OnPressWorld();
            }
        }

        // 선택했던 타워가 사라졌으면(스테이지 리셋·파괴 등) 사거리 표시 끄기
        if (!previewing && movingTower == null && rangeCircle != null && rangeCircle.enabled && selectedTower == null)
            rangeCircle.enabled = false;
    }

    // ── 판매 ────────────────────────────────────────────────
    int SellRefund(Tower t)
    {
        if (t == null) return 0;
        int basis = (t.paidCost > 0) ? t.paidCost : t.cost; // 봇/배치AI가 지은 타워는 paidCost가 비어 있을 수 있다
        return Mathf.Max(0, Mathf.RoundToInt(basis * Mathf.Clamp01(sellRefundRatio)));
    }

    void SellSelected()
    {
        Tower t = selectedTower;
        if (t == null) return;

        int refund = SellRefund(t);
        Tile tile = FindTileAt(t.transform.position);
        if (tile != null) tile.occupied = false;
        if (GameManager.Instance != null) GameManager.Instance.AddGold(refund);
        Debug.Log($"[판매] {t.towerName} → +{refund}골드");

        HideRange();
        Destroy(t.gameObject);
    }

    // ── 이동 ────────────────────────────────────────────────
    void BeginMoveSelected()
    {
        if (!allowMove || selectedTower == null) return;
        movingTower = selectedTower;
        movingFrom = movingTower.transform.position;

        // 원래 칸을 먼저 비워 둔다 → 제자리에 다시 놓는 것(= 취소나 다름없음)도 허용된다
        Tile from = FindTileAt(movingFrom);
        if (from != null) from.occupied = false;

        // 옮기는 동안에는 공격을 멈춘다. 안 그러면 전투 중에 타워를 끌고 다니며
        // 적을 쫓는 조작이 가능해져서 '되돌리기 수단'이 아니라 새로운 지배 전략이 된다.
        movingTower.enabled = false;
        // 콜라이더도 꺼야 한다. 타워가 커서를 따라다니므로 켜 두면 레이캐스트가 항상 타워를 먼저 맞고
        // 그 아래 타일을 영영 못 찾아서 '놓을 수 있는 칸'이 하나도 없게 된다.
        SetCollidersEnabled(movingTower, false);

        selectedTower = null;   // 패널은 닫고 사거리 원은 이동 미리보기가 이어받는다
        UpdateMovePreview();
    }

    static void SetCollidersEnabled(Tower t, bool on)
    {
        if (t == null) return;
        foreach (Collider c in t.GetComponentsInChildren<Collider>(true))
            if (c != null) c.enabled = on;
    }

    void UpdateMovePreview()
    {
        if (movingTower == null) { HideRange(); return; }

        Tile tile = TileUnderMouse(out Tower existing);
        // 자기 자신 위에 있는 경우는 '빈 칸'으로 친다 (이미 occupied를 풀어놨다)
        if (existing != null && existing != movingTower) tile = null;

        previewTile = tile;
        previewValid = (tile != null) && tile.CanBuild();

        if (tile != null) movingTower.transform.position = tile.transform.position;

        Color c = previewValid ? previewOkColor : previewBadColor;
        c.a = 0.95f;
        DrawCircle(movingTower.transform.position, Mathf.Max(0.1f, movingTower.AttackRange), c);
    }

    void ConfirmMove()
    {
        Tower t = movingTower;
        if (t == null) { movingTower = null; HideRange(); return; }

        if (previewValid && previewTile != null)
        {
            t.transform.position = previewTile.transform.position;
            previewTile.occupied = true;
        }
        else
        {
            RestoreMovedTower(t);
        }

        t.enabled = true;
        SetCollidersEnabled(t, true);
        movingTower = null;
        previewTile = null;
        previewValid = false;
        HideRange();
    }

    void CancelMove()
    {
        Tower t = movingTower;
        if (t != null) { RestoreMovedTower(t); t.enabled = true; SetCollidersEnabled(t, true); }
        movingTower = null;
        previewTile = null;
        previewValid = false;
        HideRange();
    }

    void RestoreMovedTower(Tower t)
    {
        t.transform.position = movingFrom;
        Tile back = FindTileAt(movingFrom);
        if (back != null) back.occupied = true;
    }

    // 월드 좌표에서 가장 가까운 타일 (타워는 타일 중심에 정확히 놓이므로 거리 0에 가깝다)
    Tile FindTileAt(Vector3 pos)
    {
        Tile best = null;
        float bestDist = 0.45f;
        foreach (Tile tl in Object.FindObjectsByType<Tile>(FindObjectsSortMode.None))
        {
            if (tl == null) continue;
            Vector3 a = tl.transform.position; a.y = 0f;
            Vector3 b = pos; b.y = 0f;
            float d = Vector3.Distance(a, b);
            if (d < bestDist) { bestDist = d; best = tl; }
        }
        return best;
    }

    void SelectTower(int i)
    {
        if (towerPrefabs != null && i >= 0 && i < towerPrefabs.Length)
            selectedIndex = i;
    }

    GameObject SelectedPrefab()
    {
        if (towerPrefabs == null || towerPrefabs.Length == 0) return null;
        if (selectedIndex < 0 || selectedIndex >= towerPrefabs.Length) return null;
        return towerPrefabs[selectedIndex];
    }

    // 마우스 아래의 타일을 찾는다 (없으면 null)
    Tile TileUnderMouse(out Tower existingTower)
    {
        existingTower = null;
        if (cam == null) return null;
        Ray ray = cam.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit, 1000f)) return null;
        existingTower = hit.collider.GetComponentInParent<Tower>();
        return hit.collider.GetComponentInParent<Tile>();
    }

    // 좌클릭을 누른 순간
    void OnPressWorld()
    {
        Tile tile = TileUnderMouse(out Tower existing);

        if (existing != null) { ShowRange(existing); return; }   // 기존 타워 클릭 = 사거리 보기
        if (tile == null)     { HideRange(); return; }           // 맵 밖
        if (SelectedPrefab() == null) return;

        previewing = true;
        HideRange();
        UpdatePreview();
    }

    // 누르고 있는 동안 매 프레임: 고스트 위치·색·사거리 원 갱신
    void UpdatePreview()
    {
        GameObject prefab = SelectedPrefab();
        if (prefab == null) { CancelPreview(); return; }

        // 도중에 숫자키로 종류를 바꿨다면 고스트를 새로 만든다
        if (ghost == null || ghostTypeIndex != selectedIndex) BuildGhost(prefab);

        Tile tile = TileUnderMouse(out _);
        previewTile = tile;

        Tower td = prefab.GetComponent<Tower>();
        string nm = (td != null && !string.IsNullOrEmpty(td.towerName)) ? td.towerName : prefab.name;
        int baseCost = (td != null) ? td.cost : 0;
        previewCost = (GameManager.Instance != null)
                    ? GameManager.Instance.GetScaledCost(baseCost, nm) : baseCost;

        bool afford = GameManager.Instance == null || GameManager.Instance.CanAfford(previewCost);
        previewValid = (tile != null) && tile.CanBuild() && afford;

        if (tile == null) { if (ghost != null) ghost.SetActive(false); HideRange(); return; }

        if (ghost != null)
        {
            ghost.SetActive(true);
            ghost.transform.position = tile.transform.position;
            SetGhostColor(previewValid ? previewOkColor : previewBadColor);
        }

        float radius = (td != null) ? Mathf.Max(0.1f, td.maxRange + td.rangeBonus) : 1f;
        Color c = previewValid ? previewOkColor : previewBadColor;
        c.a = 0.95f;
        DrawCircle(tile.transform.position, radius, c);
    }

    // 손을 뗌 → 유효하면 설치
    void ConfirmPreview()
    {
        bool ok = previewValid && previewTile != null;
        Tile tile = previewTile;
        EndPreview();
        if (!ok) return;

        GameObject prefab = SelectedPrefab();
        if (prefab == null) return;

        Tower towerData = prefab.GetComponent<Tower>();
        int baseCost = (towerData != null) ? towerData.cost : 0;
        string builtName = (towerData != null && !string.IsNullOrEmpty(towerData.towerName))
                         ? towerData.towerName : prefab.name;
        int cost = (GameManager.Instance != null)
                 ? GameManager.Instance.GetScaledCost(baseCost, builtName) : baseCost;

        if (GameManager.Instance != null && !GameManager.Instance.CanAfford(cost))
        { Debug.Log("골드가 부족해요"); return; }

        GameObject go = Instantiate(prefab, tile.transform.position, Quaternion.identity);
        tile.occupied = true;

        if (GameManager.Instance != null)
        {
            GameManager.Instance.SpendGold(cost);
            GameManager.Instance.RegisterTowerBuilt(builtName);
        }

        Tower built = go.GetComponent<Tower>();
        if (built != null) built.paidCost = cost;   // 판매 환급의 기준 (오른 가격을 그대로 기억)

        // 이미 강화된 종류면 새 타워도 즉시 그 레벨로
        if (TowerUpgradeManager.Instance != null)
            TowerUpgradeManager.Instance.ApplyCurrentLevel(built);
    }

    void CancelPreview() { EndPreview(); }

    void EndPreview()
    {
        previewing = false;
        previewTile = null;
        previewValid = false;
        if (ghost != null) { Destroy(ghost); ghost = null; }
        ghostTypeIndex = -1;
        HideRange();
    }

    // 반투명 실루엣 생성: 실제 프리팹을 띄우되 로직·충돌을 제거하고 머티리얼만 갈아끼운다
    void BuildGhost(GameObject prefab)
    {
        if (ghost != null) Destroy(ghost);
        ghost = Instantiate(prefab);
        ghost.name = "BuildPreview";
        ghostTypeIndex = selectedIndex;

        // 타워 로직 제거 (안 그러면 미리보기가 실제로 공격한다)
        Tower t = ghost.GetComponent<Tower>();
        if (t != null) Destroy(t);
        // 충돌 제거 (레이캐스트가 고스트를 맞아 타일을 못 찾는 문제 방지)
        foreach (Collider c in ghost.GetComponentsInChildren<Collider>(true)) Destroy(c);

        EnsureGhostMat();
        foreach (Renderer r in ghost.GetComponentsInChildren<Renderer>(true))
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            Material[] mats = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < mats.Length; i++) mats[i] = new Material(ghostMat);
            r.materials = mats;
        }
    }

    void SetGhostColor(Color col)
    {
        if (ghost == null) return;
        foreach (Renderer r in ghost.GetComponentsInChildren<Renderer>(true))
            foreach (Material m in r.materials)
            {
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", col);
                if (m.HasProperty("_Color")) m.SetColor("_Color", col);
            }
    }

    // 반투명 머티리얼 (URP Unlit은 기본이 불투명이라 블렌딩을 손으로 켜야 한다)
    static void EnsureGhostMat()
    {
        if (ghostMat != null) return;
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        ghostMat = new Material(sh);
        if (ghostMat.HasProperty("_Surface"))
        {
            ghostMat.SetFloat("_Surface", 1f);
            ghostMat.SetFloat("_Blend", 0f);
            ghostMat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            ghostMat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            ghostMat.SetFloat("_ZWrite", 0f);
            ghostMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            ghostMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
    }

    // 종류 전체 업그레이드 (골드 소비)
    void TryUpgradeType(string typeName)
    {
        var mgr = TowerUpgradeManager.Instance;
        if (mgr == null) return;
        if (mgr.IsMaxed(typeName)) { Debug.Log($"{typeName}: 이미 최대 레벨"); return; }
        if (!mgr.UpgradeType(typeName))
            Debug.Log("골드가 부족해요 (강화)");
    }

    // ── 사거리 표시 ─────────────────────────────
    void EnsureRangeCircle()
    {
        if (rangeCircle != null) return;
        GameObject go = new GameObject("RangeIndicator");
        rangeCircle = go.AddComponent<LineRenderer>();
        rangeCircle.loop = true;
        rangeCircle.useWorldSpace = true;
        rangeCircle.positionCount = RANGE_SEGMENTS;
        rangeCircle.widthMultiplier = rangeLineWidth;
        rangeCircle.numCapVertices = 2;
        rangeCircle.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rangeCircle.receiveShadows = false;
        Shader sh = Shader.Find("Sprites/Default");
        if (sh != null) rangeCircle.material = new Material(sh);
        rangeCircle.startColor = rangeColor;
        rangeCircle.endColor = rangeColor;
        rangeCircle.enabled = false;
    }

    // 임의 위치에 사거리 원을 그린다 (설치된 타워 선택 · 설치 미리보기 공용)
    void DrawCircle(Vector3 pos, float radius, Color col)
    {
        EnsureRangeCircle();
        rangeCircle.startColor = col;
        rangeCircle.endColor = col;
        Vector3 center = pos; center.y += rangeHeight;
        for (int i = 0; i < RANGE_SEGMENTS; i++)
        {
            float ang = (i / (float)RANGE_SEGMENTS) * Mathf.PI * 2f;
            rangeCircle.SetPosition(i, center + new Vector3(Mathf.Cos(ang) * radius, 0f, Mathf.Sin(ang) * radius));
        }
        rangeCircle.enabled = true;
    }

    // 타워를 선택해 사거리 원을 그린다
    void ShowRange(Tower t)
    {
        if (t == null) return;
        selectedTower = t;
        DrawCircle(t.transform.position, Mathf.Max(0.1f, t.AttackRange), rangeColor);
    }

    void HideRange()
    {
        selectedTower = null;
        if (rangeCircle != null) rangeCircle.enabled = false;
    }
    // ────────────────────────────────────────────

    // 임시 화면 표시: 타워 목록 + 종류별 강화 버튼
    void OnGUI()
    {
        HudScaler.Begin();
        if (towerPrefabs == null) return;

        GUIStyle header = new GUIStyle { fontSize = 18 };
        header.normal.textColor = Color.white;
        float y = 122f;
        GUI.Label(new Rect(12, y, 560, 24), "설치할 타워 (숫자키 선택 · 좌클릭 누른 채 이동 → 떼면 설치 · 우클릭 취소):", header);
        y += 26f;

        for (int i = 0; i < towerPrefabs.Length; i++)
        {
            if (towerPrefabs[i] == null) continue;

            Tower t = towerPrefabs[i].GetComponent<Tower>();
            string nm = (t != null && !string.IsNullOrEmpty(t.towerName)) ? t.towerName : towerPrefabs[i].name;
            int c = (t != null) ? t.cost : 0;
            if (GameManager.Instance != null) c = GameManager.Instance.GetScaledCost(c, nm); // 오른 현재 가격 표시(종류별)

            GUIStyle s = new GUIStyle { fontSize = 18 };
            s.normal.textColor = (i == selectedIndex) ? Color.yellow : Color.white;
            string mark = (i == selectedIndex) ? "▶ " : "    ";
            GUI.Label(new Rect(12, y, 560, 24), $"{mark}[{i + 1}] {nm} — {c}골드", s);
            y += 24f;
        }

        // ── 종류별 강화 버튼 ───────────────────────────
        y += 8f;
        GUI.Label(new Rect(12, y, 560, 24), "강화 (버튼 클릭 시 그 종류 전체 레벨업):", header);
        y += 26f;

        float panelTop = y;
        var mgr = TowerUpgradeManager.Instance;
        HashSet<string> shown = new HashSet<string>();
        int rows = 0;

        for (int i = 0; i < towerPrefabs.Length; i++)
        {
            if (towerPrefabs[i] == null) continue;
            Tower t = towerPrefabs[i].GetComponent<Tower>();
            string nm = (t != null && !string.IsNullOrEmpty(t.towerName)) ? t.towerName : towerPrefabs[i].name;
            if (!shown.Add(nm)) continue; // 같은 종류 중복 제외

            int lv = (mgr != null) ? mgr.GetLevel(nm) : 0;
            bool maxed = (mgr != null) && mgr.IsMaxed(nm);
            string label = maxed
                ? $"[강화] {nm}  Lv{lv}  (MAX)"
                : $"[강화] {nm}  Lv{lv} → {(mgr != null ? mgr.NextCost(nm) : 0)}골드";

            Rect r = new Rect(12, y, 300, 26);
            if (GUI.Button(r, label) && mgr != null && !maxed)
                TryUpgradeType(nm);

            y += 28f;
            rows++;
        }

        // 이 영역 안의 클릭은 월드 설치로 넘어가지 않게 기록
        upgradePanelRect = new Rect(12, panelTop, 300, Mathf.Max(0, rows * 28f));

        // ── 선택한 타워: 판매 / 이동 ───────────────────────────
        if (movingTower != null)
        {
            y += 10f;
            GUIStyle hint = new GUIStyle { fontSize = 17 };
            hint.normal.textColor = new Color(0.4f, 1f, 0.5f);
            GUI.Label(new Rect(12, y, 560, 24),
                      $"이동 중: {movingTower.towerName} — 놓을 칸을 좌클릭 · 우클릭 취소", hint);
            towerPanelRect = new Rect(0, 0, 0, 0);
        }
        else if (selectedTower != null)
        {
            y += 10f;
            int lv = (mgr != null) ? mgr.GetLevel(selectedTower.towerName) : 0;
            GUI.Label(new Rect(12, y, 560, 24), $"선택한 타워: {selectedTower.towerName} Lv{lv}", header);
            y += 26f;

            float top2 = y;
            if (GUI.Button(new Rect(12, y, 145, 26), $"판매 +{SellRefund(selectedTower)}골드"))
                SellSelected();
            if (allowMove && GUI.Button(new Rect(165, y, 145, 26), "이동 (무료)"))
                BeginMoveSelected();

            towerPanelRect = new Rect(12, top2, 300, 28f);
        }
        else
        {
            towerPanelRect = new Rect(0, 0, 0, 0);
        }
    }
}
