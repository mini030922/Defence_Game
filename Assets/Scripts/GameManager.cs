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
    public float towerCostGrowth = 0.15f; // 같은 스테이지에서 1개 지을 때마다 +15%
    public float stageCostGrowth = 0.12f; // 스테이지가 1 오를 때마다 기본가 +12%
    private int towersBuilt = 0;

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
    }

    // 스테이지별 시작 골드 (후반 스테이지일수록 더 많이 쥐고 시작 → 1라운드 탱커 감당)
    public int StartGoldForStage(int stageNo)
    {
        int s = Mathf.Max(1, stageNo);
        return startGold + startGoldPerStage * (s - 1);
    }

    // 실제 가격 = 기본가 × 스테이지 배율 × (이번 스테이지에서 설치한 수만큼 상승)
    public int GetScaledCost(int baseCost)
    {
        float stageMult = 1f + stageCostGrowth * (Mathf.Max(1, stage) - 1);
        return Mathf.RoundToInt(baseCost * stageMult * (1f + towerCostGrowth * towersBuilt));
    }

    public void RegisterTowerBuilt()
    {
        towersBuilt++;
    }

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
        GUIStyle style = new GUIStyle { fontSize = 20 };
        style.normal.textColor = Color.white;
        GUI.Label(new Rect(12, 10, 400, 28), $"스테이지 {stage}", style);
        GUI.Label(new Rect(12, 38, 400, 28), $"골드: {gold}", style);
        GUI.Label(new Rect(12, 66, 400, 28), $"체력: {playerHealth}", style);
    }
}
