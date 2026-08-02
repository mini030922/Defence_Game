using System.Collections.Generic;
using UnityEngine;

// 타워 '종류' 단위 업그레이드를 관리한다.
// - 종류마다 레벨을 하나만 둔다 (같은 종류의 모든 타워가 공유).
// - 업그레이드 비용은 종류마다 다르다: 그 타워의 '건설비'를 기준으로 계산(비싼 타워일수록 강화도 비쌈).
//   첫 업그레이드부터 건설비보다 비싸다. 보유 수와는 무관.
// - 새로 설치되는 타워는 그 종류의 현재 레벨을 즉시 물려받는다.
public class TowerUpgradeManager : MonoBehaviour
{
    [Header("업그레이드 비용 (종류별 — 그 타워의 건설비 기준, 보유 수 무관)")]
    // 2.0 → 1.5 (2026-07-26): 2.0이면 첫 업그레이드가 신축보다 비싸서, 즉시 보상까지 없는 배치 AI가
    // 업그레이드 행동을 아예 학습하지 못했다(plc_v5 로그에서 모든 타워가 레벨 0으로 확인).
    // 1.5 → 2.2 (2026-08-02). 최대 레벨 10 확장과 함께 진입 비용을 올렸다.
    //   첫 강화가 건설비의 2.2배 → "일단 한 번 찍고 보는" 선택을 억제하고,
    //   타워를 더 지을지 / 강화할지의 판단을 실제 고민으로 만든다.
    public float upgradeCostFactor = 2.2f;  // 첫 업그레이드 = 타워 건설비 × 이 값
    public float costGrowthPerLevel = 0.6f; // 레벨당 추가 배율 (× 타워 건설비)
    // 5 → 8 → 10 (2026-08-02). 레벨당 배율을 낮추면서 단계를 늘렸다.
    // 만렙 DPS는 옛 Lv5의 0.70배로 고정한 채 단계만 늘렸으므로,
    // 만렙까지 총비용은 기본가 13.5배 → 49.0배(3.6배)가 되고 화력은 0.70배.
    // = DPS당 비용 5.2배. 후반 골드가 갈 곳이 생겨 "주력 만렙 후 살 게 없다"는
    // 엔드게임 정체(잔금 430골드)도 함께 완화된다.
    public int maxLevel = 10;               // 종류당 최대 레벨
    public int fallbackTowerCost = 100;     // 그 종류 타워를 씬에서 못 찾을 때 기본 건설비

    private readonly Dictionary<string, int> levelByType = new Dictionary<string, int>();

    private static TowerUpgradeManager _instance;
    public static TowerUpgradeManager Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = Object.FindFirstObjectByType<TowerUpgradeManager>();
                if (_instance == null)
                {
                    // 씬에 안 넣어놨어도 자동 생성 (기본 수치 사용)
                    var go = new GameObject("TowerUpgradeManager (auto)");
                    _instance = go.AddComponent<TowerUpgradeManager>();
                }
            }
            return _instance;
        }
    }

    void Awake()
    {
        if (_instance != null && _instance != this) { Destroy(gameObject); return; }
        _instance = this;
    }

    public int GetLevel(string type)
    {
        if (string.IsNullOrEmpty(type)) return 0;
        return levelByType.TryGetValue(type, out int lv) ? lv : 0;
    }

    public bool IsMaxed(string type) => GetLevel(type) >= maxLevel;

    // 다음 레벨로 올리는 비용 — '그 타워 건설비 × (factor + growth×현재레벨)'.
    // 종류마다 다르고(비싼 타워일수록 강화도 비쌈), 첫 업그레이드부터 건설비보다 비싸다.
    public int NextCost(string type)
    {
        int lv = GetLevel(type);
        int baseCost = BaseTowerCost(type);
        return Mathf.RoundToInt(baseCost * (upgradeCostFactor + costGrowthPerLevel * lv));
    }

    // 그 종류 타워의 기본 건설 비용 (씬의 타워 인스턴스에서 읽음)
    int BaseTowerCost(string type)
    {
        foreach (Tower t in Object.FindObjectsByType<Tower>(FindObjectsSortMode.None))
            if (t != null && t.towerName == type) return t.cost;
        return fallbackTowerCost;
    }

    // 그 종류 전체를 한 레벨 올린다. 골드 부족/최대레벨이면 false.
    public bool UpgradeType(string type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        if (IsMaxed(type)) return false;

        GameManager gm = GameManager.Instance;
        int cost = NextCost(type);
        if (gm == null || !gm.CanAfford(cost)) return false;
        gm.SpendGold(cost);

        int newLevel = GetLevel(type) + 1;
        levelByType[type] = newLevel;

        // 현재 씬의 그 종류 타워 전부에 새 레벨 적용
        foreach (Tower t in Object.FindObjectsByType<Tower>(FindObjectsSortMode.None))
            if (t != null && t.towerName == type)
                t.SetUpgradeLevel(newLevel);

        Debug.Log($"[강화] {type} → Lv{newLevel} (비용 {cost})");
        return true;
    }

    // 새로 지은 타워에 그 종류의 현재 레벨을 반영 (이미 강화된 종류면 바로 강해짐)
    public void ApplyCurrentLevel(Tower t)
    {
        if (t == null) return;
        int lv = GetLevel(t.towerName);
        if (lv > 0) t.SetUpgradeLevel(lv);
    }

    // 스테이지/에피소드 리셋 시 모든 종류 레벨 0으로
    public void ResetLevels() => levelByType.Clear();

    // 골드 지불 없이 레벨을 직접 지정한다 (학습용 시드 전용).
    // 왜 필요한가 (2026-07-29): 학습은 '에피소드 = 랜덤 스테이지 1개'라서,
    //   업그레이드를 스테이지 간에 유지시켜도 배치 AI는 그 가치를 관측할 기회가 없다.
    //   그래서 에피소드 시작 시 "이 스테이지까지 왔다면 이 정도는 강화돼 있었을 것"을 미리 심어준다.
    //   씬에 타워가 없는 시점에도 호출되므로 비용 계산(BaseTowerCost)을 타지 않는다.
    public void SetLevel(string type, int level)
    {
        if (string.IsNullOrEmpty(type)) return;
        levelByType[type] = Mathf.Clamp(level, 0, maxLevel);
    }
}
