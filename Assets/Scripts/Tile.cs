using UnityEngine;

// 타일의 종류: 설치 가능 땅 / 적이 지나가는 경로
public enum TileType
{
    Buildable,  // 타워 설치 가능
    Path        // 적 이동 경로 (설치 불가)
}

// 그리드의 타일 한 칸.
//
// ── 구조 (2026-08-09 개편) ────────────────────────────────────────────────
// 이 오브젝트 자체는 **판정용 껍데기**다. 좌표·종류·점유 상태와 BoxCollider만 들고 있고,
// 눈에 보이는 것은 전부 자식(`visual`)이다.
//
// 왜 나눴나: 경로가 에피소드마다 새로 생성된다(`GridManager.RandomizePath`).
//   그때마다 어떤 칸은 길이 되고 어떤 칸은 땅이 되므로 **모델을 갈아끼워야 한다.**
//   콜라이더가 루트에 남아 있으면 겉모습을 아무리 바꿔도
//   `BuildManager.TileUnderMouse()`의 레이캐스트 판정은 흔들리지 않는다.
//   (Kenney FBX는 `addColliders: 0`이라 모델 자체엔 콜라이더가 없다)
public class Tile : MonoBehaviour
{
    public Vector2Int coord;   // 그리드 좌표 (x, y)
    public TileType type;      // 타일 종류

    // ── 점유 상태: 필드 → 프로퍼티 (2026-08-09) ──────────────────────────────
    // 왜 바꿨나: 타워가 올라가면 그 칸의 장식(나무·바위)을 즉시 치우고,
    //   팔거나 옮기면 원래 모습으로 되돌려야 한다.
    //   그런데 occupied를 건드리는 곳이 여섯 군데로 흩어져 있다 —
    //   BuildManager(건설·판매·이동 시작·이동 확정·이동 취소) · PlacementAgent ·
    //   AutoPlayerBot · RoundManager.ResetStage · GridManager.ApplyPathToTiles.
    //
    //   전부 찾아 고치는 대신 **대입 자체가 겉모습 교체를 부르게** 만들면
    //   기존 호출부를 한 줄도 바꾸지 않고 전부 동작한다. 앞으로 새 호출부가 생겨도 자동이다.
    //
    //   값이 그대로면 즉시 빠져나오므로, ResetStage처럼 전 칸을 false로 미는 코드는 공짜다.
    [SerializeField] private bool _occupied;
    public bool occupied
    {
        get => _occupied;
        set
        {
            if (_occupied == value) return;
            _occupied = value;
            if (visualReady) RebuildVisual();
        }
    }

    [System.NonSerialized] public GameObject visual;   // 지금 붙어 있는 겉모습

    // 겉모습 상태. '원래 모습'과 '타워가 올라갔을 때의 모습'을 둘 다 기억해 두고,
    // occupied가 바뀔 때마다 둘 사이를 오간다.
    private GameObject naturalPrefab;    // 비어 있을 때 (땅 변형 · 길 조각)
    private GameObject occupiedPrefab;   // 타워가 올라갔을 때 (보통 민짜 tile)
    private float visualYaw;
    private float visualYOffset;
    private float visualScale = 1f;
    private bool visualReady;

    private Renderer fallbackRenderer; // 모델을 안 쓸 때의 색칠 큐브 (모델을 쓰면 null)
    private Color pendingColor = Color.white;

    public void Init(Vector2Int coord, TileType type, Color color)
    {
        this.coord = coord;
        this.type = type;
        _occupied = false;          // 프로퍼티를 타지 않는다 — 아직 겉모습이 없어 rebuild가 헛돈다
        pendingColor = color;
        SetColor(color);
    }

    // 타일 색 바꾸기.
    // 모델 프리팹을 쓰는 중이면 **아무것도 하지 않는다** — Kenney 타일은 colormap 텍스처로
    // 이미 땅/길이 구분되므로, 단색을 덮으면 텍스처가 죽는다. (적 모델의 tintWithColor와 같은 판단)
    // MaterialPropertyBlock을 쓰므로 머티리얼을 복제하지 않는다(누수·드로우콜 증가 없음).
    public void SetColor(Color color)
    {
        pendingColor = color;
        if (fallbackRenderer == null) return;

        MaterialPropertyBlock mpb = new MaterialPropertyBlock();
        fallbackRenderer.GetPropertyBlock(mpb);
        mpb.SetColor("_BaseColor", color); // URP Lit
        mpb.SetColor("_Color", color);     // 빌트인 호환
        fallbackRenderer.SetPropertyBlock(mpb);
    }

    // ── 겉모습 지정 ────────────────────────────────────────────────────────
    //   natural      : 비어 있을 때 보일 모델 (null이면 예전 색칠 큐브)
    //   whenOccupied : 타워가 올라갔을 때 보일 모델 (null이면 교체하지 않음)
    // 프리팹을 하나도 안 꽂아도 기존 동작이 그대로 유지된다(점진적 교체 가능).
    public void SetVisual(GameObject natural, float yaw, GameObject whenOccupied,
                          float yOffset, float scale)
    {
        naturalPrefab = natural;
        occupiedPrefab = whenOccupied;
        visualYaw = yaw;
        visualYOffset = yOffset;
        visualScale = scale;
        visualReady = true;
        RebuildVisual();
    }

    // 지금 상태(occupied)에 맞는 모델로 자식을 다시 만든다.
    void RebuildVisual()
    {
        GameObject want = (_occupied && occupiedPrefab != null) ? occupiedPrefab : naturalPrefab;

        if (visual != null) { DestroyNow(visual); visual = null; }
        fallbackRenderer = null;

        if (want == null)
        {
            // 예전 그대로: 1×1×1 큐브를 (0.95, 0.2, 0.95)로 눌러 만든 판때기
            visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "Visual (fallback)";
            Collider own = visual.GetComponent<Collider>();
            if (own != null) DestroyNow(own);   // 판정은 루트 BoxCollider가 전담한다
            visual.transform.SetParent(transform, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = new Vector3(visualScale * 0.95f, 0.2f, visualScale * 0.95f);
            fallbackRenderer = visual.GetComponent<Renderer>();
            SetColor(pendingColor);
            return;
        }

        visual = Instantiate(want, transform);
        visual.name = "Visual";
        visual.transform.localPosition = new Vector3(0f, visualYOffset, 0f);
        visual.transform.localRotation = Quaternion.Euler(0f, visualYaw, 0f);
        visual.transform.localScale = Vector3.one * Mathf.Max(0.01f, visualScale);

        // 모델에 콜라이더가 딸려 있으면 끈다. 살아 있으면 레이캐스트가 모델을 먼저 맞아
        // 루트의 판정용 BoxCollider를 가릴 수 있다.
        foreach (Collider c in visual.GetComponentsInChildren<Collider>(true))
            if (c != null) c.enabled = false;
    }

    // 편집 모드에서도 안전하게 지운다 (인스펙터에서 회전값을 맞출 때 필요)
    static void DestroyNow(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o);
        else DestroyImmediate(o);
    }

    // 이 타일에 타워를 설치할 수 있는가?
    public bool CanBuild()
    {
        return type == TileType.Buildable && !_occupied;
    }
}
