using UnityEngine;

// 타일의 종류: 설치 가능 땅 / 적이 지나가는 경로
public enum TileType
{
    Buildable,  // 타워 설치 가능
    Path        // 적 이동 경로 (설치 불가)
}

// 그리드의 타일 한 칸을 나타내는 스크립트.
public class Tile : MonoBehaviour
{
    public Vector2Int coord;   // 그리드 좌표 (x, y)
    public TileType type;      // 타일 종류
    public bool occupied;      // 이미 타워가 설치됐는지 (Buildable 타일용)

    private Renderer rend;

    public void Init(Vector2Int coord, TileType type, Color color)
    {
        this.coord = coord;
        this.type = type;
        this.occupied = false;
        SetColor(color);
    }

    // 타일 색 바꾸기.
    // MaterialPropertyBlock을 쓰면 머티리얼을 복제(누수)하지 않고 색만 바꿀 수 있다.
    // 편집 모드에서도 "material leak" 경고가 뜨지 않는다.
    public void SetColor(Color color)
    {
        if (rend == null) rend = GetComponent<Renderer>();
        if (rend == null) return;

        MaterialPropertyBlock mpb = new MaterialPropertyBlock();
        rend.GetPropertyBlock(mpb);
        mpb.SetColor("_BaseColor", color); // URP Lit 셰이더의 색상 속성
        mpb.SetColor("_Color", color);     // 빌트인 파이프라인 호환용
        rend.SetPropertyBlock(mpb);
    }

    // 이 타일에 타워를 설치할 수 있는가?
    public bool CanBuild()
    {
        return type == TileType.Buildable && !occupied;
    }
}
