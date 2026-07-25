using UnityEngine;

// 적 종류 하나의 정의. 기준 체력/속도에 배율을 곱해 다양한 적을 만든다.
[System.Serializable]
public class EnemyType
{
    public string typeName = "일반";
    public float healthMultiplier = 1f;  // 기준 체력 대비 배율 (탱커는 크게)
    public float speedMultiplier = 1f;   // 기준 속도 대비 배율 (빠른 적은 크게)
    public float scale = 0.5f;           // 겉보기 크기
    public Color color = new Color(0.85f, 0.3f, 0.3f);
    public float cost = 1f;              // 예산제에서 이 적 1마리의 상대 비용 (탱커>빠름>일반)
    public float leakDamageFraction = 0.05f; // 골 도달 시 플레이어 최대체력의 이 비율만큼 데미지
    public float roundHealthGrowth = 0f;     // 라운드가 지날수록 체력 추가 증가율(라운드당). 탱커는 크게.
}
