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

    // ── 겉모습 (2026-08-08 신설) ───────────────────────────────────────────
    // 그동안 적은 RoundManager가 CreatePrimitive(Capsule)로 만든 캡슐이었다.
    // 여기에 프리팹을 꽂으면 그 모델로 대체된다. 비워두면 기존 캡슐 그대로 동작한다.
    // (프리팹을 안 넣은 종류와 넣은 종류가 섞여 있어도 문제없다)
    [Header("겉모습 — 비우면 기존 캡슐 프리미티브를 쓴다")]
    public GameObject prefab;

    // 프리팹 전용 추가 배율. 최종 크기 = 프리팹 원본 배율 × scale × modelScale.
    //   · scale은 '종류끼리의 상대 크기'(빠름 0.4 / 일반 0.5 / 탱커 0.8)를 그대로 유지하고
    //   · modelScale은 '이 모델의 원본 크기가 타일 대비 큰가 작은가'만 보정한다.
    // 둘을 나눠 둔 이유: 모델을 갈아끼울 때 종류별 상대 크기를 다시 계산하지 않아도 되게 하기 위함.
    public float modelScale = 1f;

    // 프리팹 피벗이 발바닥이 아닐 때의 높이 보정(월드 단위). 보통 0으로 둔다.
    public float modelYOffset = 0f;

    // 프리팹의 머티리얼 위에 위 color를 덮어씌울지.
    // 기본 false — 모델마다 얼굴/텍스처가 달라 이미 구분이 되고, 덮으면 텍스처가 단색에 묻힌다.
    // 나중에 같은 모델을 재사용하는 종류가 생기면 그 종류만 켜서 구분하면 된다.
    public bool tintWithColor = false;
}
