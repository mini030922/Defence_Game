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

    void Start()
    {
        if (cam == null) cam = Camera.main;
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

        // 왼쪽 클릭으로 설치 (단, 강화 버튼 위 클릭은 무시 → OnGUI가 처리)
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            Vector2 mp = Mouse.current.position.ReadValue();
            // GUI 좌표는 좌상단 원점, 마우스는 좌하단 원점 → y 뒤집어 비교
            Vector2 gui = new Vector2(mp.x, Screen.height - mp.y);
            if (upgradePanelRect.Contains(gui)) return;
            TryBuildAtMouse();
        }

        // 선택했던 타워가 사라졌으면(스테이지 리셋·파괴 등) 사거리 표시 끄기
        if (rangeCircle != null && rangeCircle.enabled && selectedTower == null)
            rangeCircle.enabled = false;
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

    void TryBuildAtMouse()
    {
        if (cam == null) return;

        Vector2 mousePos = Mouse.current.position.ReadValue();
        Ray ray = cam.ScreenPointToRay(mousePos);
        if (!Physics.Raycast(ray, out RaycastHit hit, 1000f))
        {
            HideRange(); // 맵 밖(아무것도 안 맞음) 클릭 → 사거리 끄기
            return;
        }

        // 이미 있는 타워를 클릭 → 그 타워의 사거리 표시
        Tower existing = hit.collider.GetComponentInParent<Tower>();
        if (existing != null) { ShowRange(existing); return; }

        // 빈 타일 클릭 → 건설
        Tile tile = hit.collider.GetComponentInParent<Tile>();
        if (tile == null) return;

        GameObject prefab = SelectedPrefab();
        if (prefab == null) return;

        if (!tile.CanBuild())
        {
            Debug.Log("여기엔 설치할 수 없어요 (경로이거나 이미 타워가 있음)");
            return;
        }

        // 비용 확인 (설치할수록 / 스테이지 오를수록 오른 실제 가격)
        Tower towerData = prefab.GetComponent<Tower>();
        int baseCost = (towerData != null) ? towerData.cost : 0;
        int cost = (GameManager.Instance != null) ? GameManager.Instance.GetScaledCost(baseCost) : baseCost;

        if (GameManager.Instance != null && !GameManager.Instance.CanAfford(cost))
        {
            Debug.Log("골드가 부족해요");
            return;
        }

        // 설치!
        GameObject go = Instantiate(prefab, tile.transform.position, Quaternion.identity);
        tile.occupied = true;

        if (GameManager.Instance != null)
        {
            GameManager.Instance.SpendGold(cost);
            GameManager.Instance.RegisterTowerBuilt();
        }

        // 이미 강화된 종류면 새 타워도 즉시 그 레벨로
        if (TowerUpgradeManager.Instance != null)
            TowerUpgradeManager.Instance.ApplyCurrentLevel(go.GetComponent<Tower>());
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

    // 타워를 선택해 사거리 원을 그린다
    void ShowRange(Tower t)
    {
        if (t == null) return;
        EnsureRangeCircle();
        selectedTower = t;
        Vector3 center = t.transform.position; center.y += rangeHeight;
        float radius = Mathf.Max(0.1f, t.maxRange);
        for (int i = 0; i < RANGE_SEGMENTS; i++)
        {
            float ang = (i / (float)RANGE_SEGMENTS) * Mathf.PI * 2f;
            rangeCircle.SetPosition(i, center + new Vector3(Mathf.Cos(ang) * radius, 0f, Mathf.Sin(ang) * radius));
        }
        rangeCircle.enabled = true;
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
        if (towerPrefabs == null) return;

        GUIStyle header = new GUIStyle { fontSize = 18 };
        header.normal.textColor = Color.white;
        float y = 122f;
        GUI.Label(new Rect(12, y, 560, 24), "설치할 타워 (숫자키로 선택, 좌클릭으로 설치):", header);
        y += 26f;

        for (int i = 0; i < towerPrefabs.Length; i++)
        {
            if (towerPrefabs[i] == null) continue;

            Tower t = towerPrefabs[i].GetComponent<Tower>();
            string nm = (t != null && !string.IsNullOrEmpty(t.towerName)) ? t.towerName : towerPrefabs[i].name;
            int c = (t != null) ? t.cost : 0;
            if (GameManager.Instance != null) c = GameManager.Instance.GetScaledCost(c); // 오른 현재 가격 표시

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
    }
}
