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
    public float upgradeCostFactor = 2.0f;  // 첫 업그레이드 = 타워 건설비 × 이 값 (8번째 설치비(×2.05) 살짝 아래)
    public float costGrowthPerLevel = 0.6f; // 레벨당 추가 배율 (× 타워 건설비)
    public int maxLevel = 5;                // 종류당 최대 레벨
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
}
