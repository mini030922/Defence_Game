using System.Collections.Generic;
using UnityEngine;

// 게임 전체의 자원(골드), 플레이어 체력, 현재 스테이지를 관리한다.
// 어디서든 GameManager.Instance 로 접근할 수 있다(간단한 싱글톤).
public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    // 현재 스테이지 번호. static이라 씬을 다시 로드해도 값이 유지된다.
    // (스테이지 전환 = 씬 리로드 = 골드/타워 전부 리셋 → 설계상 '스테이지 전환 시 리셋'과 일치)
    public static int stage = 1;

    [Header("플레이어 자원 (스테이지 시작값)")]
    public int gold = 250;               // 스테이지 1 시작 골드 (기준값)
    public int startGoldPerStage = 120;  // 스테이지가 1 오를 때마다 시작 골드 +이만큼
    public int playerHealth = 20;

    private int startHealth;   // 시작 시점의 체력 (관찰·리셋용)
    private int startGold;     // 스테이지 1 기준 시작 골드 (리셋용)

    [Header("타워 가격 상승 (설치할수록 / 스테이지 오를수록 비싸짐)")]
    // 2026-07-30 — 단일 카운터 → **종류별 차등**.
    //
    // 이전에는 아무 타워를 지어도 모든 타워 가격이 똑같이 +15% 올랐다(towersBuilt 하나만 셈).
    // 그래서 '같은 종류를 계속 짓는 것'과 '여러 종류를 섞는 것'의 비용이 완전히 같았고,
    // 업그레이드가 종류 단위(= 같은 종류를 몰아 짓는 것이 유리)라 **편중을 막을 힘이 아무것도 없었다.**
    // 실측: 폭탄 96% 편중(v8) → 지원 부활 후에도 일반 41.5% / 속사 3.2% / 미사일 0.1%.
    //
    // 이제 방금 지은 종류만 더 크게 오른다. 편중하면 비싸지고, 섞으면 싸진다.
    // 대신 섞으면 업그레이드 효율(종류당 보유 수에 비례)이 떨어진다.
    // → **'깊게 몰아 키우기' vs '넓게 펴기'가 처음으로 진짜 트레이드오프가 된다.**
    public float towerCostGrowth = 0.10f;     // 전체: 아무 타워든 1개 지을 때마다 모든 종류 +10%
    public float sameTypeCostGrowth = 0.12f;  // 추가: 그 종류를 지을 때마다 **그 종류만** +12% (합 +22%)
    public float stageCostGrowth = 0.12f;     // 스테이지가 1 오를 때마다 기본가 +12%
    private int towersBuilt = 0;
    private readonly Dictionary<string, int> builtByType = new Dictionary<string, int>();

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        startHealth = playerHealth;
        startGold = gold;
    }

    // 관찰용: 시작 체력 대비 현재 체력 비율 (0~1)
    public float HealthRatio() => startHealth > 0 ? (float)playerHealth / startHealth : 0f;

    // 시작 체력 (보상 계산의 분모로 사용)
    public int MaxHealth() => startHealth;

    // 새 에피소드/스테이지 시작 시 자원 초기화 (강화학습 리셋용)
    public void ResetForNewStage()
    {
        playerHealth = startHealth;
        gold = StartGoldForStage(stage);   // 스테이지별 시작 골드로 리셋
        towersBuilt = 0;
        builtByType.Clear();
    }

    // 스테이지별 시작 골드 (후반 스테이지일수록 더 많이 쥐고 시작 → 1라운드 탱커 감당)
    public int StartGoldForStage(int stageNo)
    {
        int s = Mathf.Max(1, stageNo);
        return startGold + startGoldPerStage * (s - 1);
    }

    // 실제 가격 = 기본가 × 스테이지 배율 × (전체 설치 수 × 0.10 + 그 종류 설치 수 × 0.12)
    //
    // 예) 기본 100골드 타워, 1스테이지, 일반 타워를 1개 지은 뒤:
    //     일반  → 100 × (1 + 0.10×1 + 0.12×1) = 122골드
    //     나머지 → 100 × (1 + 0.10×1 + 0.12×0) = 110골드
    public int GetScaledCost(int baseCost, string towerName)
    {
        float stageMult = 1f + stageCostGrowth * (Mathf.Max(1, stage) - 1);

        int sameCount = 0;
        if (!string.IsNullOrEmpty(towerName))
            builtByType.TryGetValue(towerName, out sameCount);

        float growth = 1f + towerCostGrowth * towersBuilt + sameTypeCostGrowth * sameCount;
        return Mathf.RoundToInt(baseCost * stageMult * growth);
    }

    // 종류를 모르는 옛 호출부용 (종류 가산 없이 전체 상승분만)
    public int GetScaledCost(int baseCost) => GetScaledCost(baseCost, null);

    public void RegisterTowerBuilt(string towerName)
    {
        towersBuilt++;
        if (string.IsNullOrEmpty(towerName)) return;
        builtByType.TryGetValue(towerName, out int c);
        builtByType[towerName] = c + 1;
    }

    public void RegisterTowerBuilt() => RegisterTowerBuilt(null);

    public bool CanAfford(int cost) => gold >= cost;

    public bool SpendGold(int cost)
    {
        if (gold < cost) return false;
        gold -= cost;
        return true;
    }

    public void AddGold(int amount) => gold += amount;

    public void TakeDamage(int amount)
    {
        playerHealth -= amount;
        if (playerHealth <= 0)
            playerHealth = 0;
    }

    // 임시 화면 표시 (나중에 제대로 된 UI 캔버스로 교체)
    void OnGUI()
    {
        HudScaler.Begin();   // 해상도와 무관하게 같은 비율로 그린다
        GUIStyle style = new GUIStyle { fontSize = 20 };
        style.normal.textColor = Color.white;
        GUI.Label(new Rect(12, 10, 400, 28), $"스테이지 {stage}", style);
        GUI.Label(new Rect(12, 38, 400, 28), $"골드: {gold}", style);
        GUI.Label(new Rect(12, 66, 400, 28), $"체력: {playerHealth}", style);
    }
}
