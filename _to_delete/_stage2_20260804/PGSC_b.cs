using UnityEngine;
using Unity.MLAgents.Sensors;

// 배치 AI의 '맵 그리드' 관측을 평면 벡터가 아니라 **이미지(채널×높이×너비)**로 내보내는 커스텀 센서 컴포넌트.
//
// 왜 바꿨나 (2026-07-26):
//   그리드를 벡터로 넣으면(20×16×8 = 2560개) 신경망 첫 레이어가 2560×256 ≈ 65만 파라미터를 먹는다.
//   그보다 더 큰 문제는 **타일 위치마다 가중치가 따로**라는 것. "경로가 접히는 모퉁이 옆이 좋은 자리"라는
//   패턴을 맵 왼쪽에서 배워도, 오른쪽에서 같은 모양이 나오면 처음부터 다시 배워야 한다.
//   (경로 근접도·밀집도 채널을 손으로 만들어 넣은 것도 MLP가 스스로 못 해서였다.)
//   합성곱은 같은 필터를 맵 전체에 미끄러뜨리므로 위치와 무관하게 패턴을 인식하고,
//   파라미터도 커널 크기로만 결정되어 수만 단위로 떨어진다.
//
// 사용법:
//   1) PlacementAgent와 **같은 GameObject**에 이 컴포넌트를 붙인다 (Agent가 초기화 시 자동 수집).
//   2) Behavior Parameters의 Vector Observation Space Size = **스칼라 29개만** (그리드는 여기서 별도로 나감).
//   3) placement.yaml에 `vis_encode_type: match3` 지정 (그리드가 16×20이라 기본 simple 인코더는 최소 해상도 미달).
//
// ※ 실제 센서 구현은 PlacementGridSensor.cs 에 있다.
//   (Unity는 MonoBehaviour의 클래스 이름과 파일 이름이 같아야 컴포넌트로 붙일 수 있어서 파일을 나눴다)
public class PlacementGridSensorComponent : SensorComponent
{
    [Header("그리드 크기 (PlacementAgent와 같아야 함)")]
    public int gridW = 20;
    public int gridH = 16;

    [Header("파생 채널 계산 기준")]
    public float proxRef = 5f;            // 경로 근접도: 가장 가까운 경로가 이 거리 이상이면 0
    public float densityRefRange = 3f;    // 경로 밀집도: 이 반경 안의 경로 칸 수를 셈
    public float densityRefTiles = 10f;   // 이만큼 덮이면 1.0
    public float enemyDensRefCount = 3f;  // 한 칸에 적이 이만큼 겹치면 1.0

    public GridManager grid;              // 비우면 자동으로 찾음

    public override ISensor[] CreateSensors()
    {
        if (grid == null) grid = Object.FindFirstObjectByType<GridManager>();
        return new ISensor[] { new PlacementGridSensor(this) };
    }

    // 확인용 로그는 여기서 딱 한 번만 찍는다.
    // CreateSensors()에 넣으면 안 된다 — Behavior Parameters 인스펙터가 모델 호환성 검사를 하려고
    // 인스펙터를 다시 그릴 때마다 센서를 새로 만들어서, 그 오브젝트를 선택해두면 콘솔이 도배된다.
    void Start()
    {
        Debug.Log($"[배치AI] 그리드 센서 = {PlacementGridSensor.CHANNELS}채널 × {gridH} × {gridW} " +
                  $"(Space Size에는 포함되지 않음 — 스칼라만 넣을 것)");
    }
}
