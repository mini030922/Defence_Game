using System.Collections.Generic;
using UnityEngine;

// N×M 사각형 타일 그리드를 생성하고, 경로/설치가능 타일을 구분한다.
// 편집 모드에서도 우클릭 메뉴로 그리드를 생성해 볼 수 있고,
// 런타임에는 에피소드마다 사행(뱀형) 경로를 무작위로 새로 만든다.
public class GridManager : MonoBehaviour
{
    [Header("그리드 크기")]
    public int width = 16;
    public int height = 12;
    public float tileSize = 1f;

    [Header("색상 (타일 모델을 안 쓸 때만 사용)")]
    public Color buildableColor = new Color(0.35f, 0.65f, 0.35f);
    public Color pathColor = new Color(0.82f, 0.72f, 0.45f);

    // ── 타일 모델 (2026-08-09 신설) ──────────────────────────────────────────
    // 전부 비워두면 예전과 똑같이 '색칠한 큐브'가 나온다. 하나씩 꽂으며 점진적으로 교체할 수 있다.
    //
    // 경로가 에피소드마다 새로 생성되므로 맵을 손으로 배치할 수 없다.
    // 대신 `pathCoords`가 **순서 있는 리스트**라는 점을 이용해, 각 칸의 앞뒤 좌표로
    // 직선/코너/시작/끝을 판정하고 회전각까지 계산해서 코드로 조립한다.
    [Header("타일 모델 — 비우면 기존 색칠 큐브")]
    public GameObject buildablePrefab;      // 설치 가능 땅 (Kenney: tile)
    public GameObject[] buildableVariants;  // 가끔 섞어 쓸 변형 (tile-dirt, tile-bump, tile-hill …)
    [Range(0f, 1f)] public float variantChance = 0.25f;

    public GameObject pathStraightPrefab;   // 직선 (tile-straight)
    public GameObject pathCornerPrefab;     // 코너 (tile-corner-round / -square)
    public GameObject pathStartPrefab;      // 적이 나오는 첫 칸 (tile-spawn)
    public GameObject pathEndPrefab;        // 골 (tile-end)

    // 모델 피벗이 밑면이면 -0.1.
    // ⚠️ 타일 윗면은 반드시 y = 0.10에 와야 한다 — `RoundManager.enemyGroundY 0.1`,
    //    `SplashEffect.groundY 0.16`, 타워 배치가 전부 이 값을 전제로 한다.
    public float tileVisualYOffset = -0.1f;

    // 겉모습 크기 배율. 1.0 = 칸을 꽉 채움(모듈러 타일의 정상 상태 — 길이 끊기지 않는다).
    //   · 예전 색칠 큐브가 0.95였던 건 **평평한 단색을 칸으로 읽히게 하려고 일부러 준 틈**이다.
    //     모델 타일은 모서리 굴곡이 이미 있어서 맞붙는 게 맞다.
    //   · 인접 타일이 겹쳐 깜빡이면(z-fighting) 0.98쯤으로 살짝 줄인다.
    //   · 틈이 있는 옛 느낌이 좋으면 0.95.
    // ※ 클릭 판정용 BoxCollider는 이 값과 무관하게 항상 tileSize를 유지한다 —
    //   겉모습을 줄여도 칸 사이에서 클릭이 새지 않는다.
    [Range(0.5f, 1.2f)] public float tileVisualScale = 1f;

    // 타워를 지으면 그 칸을 민짜 타일(buildablePrefab)로 즉시 바꾼다. 팔거나 옮기면 원래대로 돌아온다.
    // 나무·바위가 올라간 변형 칸에 타워를 세우면 서로 뚫고 나오는데, 그걸 막는 가장 단순한 방법이다.
    // (교체는 Tile.occupied 프로퍼티가 알아서 한다 — 건설·판매·이동 호출부는 손대지 않았다)
    public bool swapTileOnBuild = true;

    // 모델마다 '기본으로 바라보는 방향'이 달라서 90° 단위로 어긋날 수 있다.
    // 눈으로 보고 0/90/180/270 중 맞는 값을 넣으면 그 종류 전체가 한 번에 맞춰진다.
    // ⚠️ 시작과 끝은 **따로** 둔다 (2026-08-09 수정).
    //    처음엔 하나로 묶었는데, 두 타일이 기준으로 삼는 방향이 서로 반대라서 같은 값으로는 못 맞춘다.
    //      · 시작 칸: 길이 '나가는 쪽'(dirOut)으로 열려 있다
    //      · 끝 칸  : 길이 '들어온 쪽'(-dirIn)으로 열려 있다
    //    게다가 tile-spawn과 tile-end는 애초에 다른 모델이라 기본 방향도 다를 수 있다.
    [Header("모델 기본 방향 보정 (90° 단위)")]
    public float straightYawOffset = 0f;
    public float cornerYawOffset = 0f;
    public float startYawOffset = 0f;
    public float endYawOffset = 0f;

    [Header("경로 길이 하한 (2026-07-28 추가)")]
    // 짧은 경로는 타워가 쏠 시간도, 한 타워가 덮을 구간도 적어 게임이 급격히 어려워진다.
    // 실측(dda_v5 로그): 경로 20~29칸 맵의 S2 클리어율 26% vs 80~89칸 맵 85%.
    //                   라운드당 손실도 0.152 vs 0.047로 3.2배 차이.
    // 즉 난이도 평균이 아니라 **맵 변동성**이 클리어 가능 여부를 지배하고 있었다.
    //
    // 2026-08-04 — 맵 20×16(320칸) → 16×12(192칸)로 줄이면서도 **45를 그대로 둔다.**
    // 시뮬(3000회 생성)로 확인한 것:
    //   · 16×12에서도 45칸 경로는 1회 시도당 25% 확률로 나온다 → 60회 재시도 안에 실패할 확률 3e-8. 안전.
    //   · 45를 유지하면 경로 평균 54.8 → 49.0칸, 타워 4개 최적배치 커버리지 77% → 84%.
    //     둘을 곱한 '실효 노출량'(적이 사격받는 총 거리)은 41.9 → 41.0으로 **-2%**, 사실상 난이도 중립이다.
    //     → Difficulty_v11 의 보정이 그대로 살아 있다. (하한을 36까지 낮추면 -9%로 눈에 띄게 쉬워진다)
    //   · 덤: 경로 길이 분포가 45~108 → 45~61로 좁아진다. 위에 적은 '맵 변동성' 문제도 같이 줄어든다.
    public int minPathLength = 45;         // 이 길이 미만이면 경로를 다시 뽑는다
    public float straightLaneChance = 0f;  // 레인 1줄짜리 단조로운 경로가 나올 확률 (0 = 안 나옴)

    [Header("경로 좌표 (비워두면 기본 직선 경로 자동 사용)")]
    public List<Vector2Int> pathCoords = new List<Vector2Int>();

    private Tile[,] tiles;

    void Awake()
    {
        if (Application.isPlaying)
            Rebuild();
    }

    [ContextMenu("그리드 생성 / 갱신 (Generate Grid)")]
    public void Rebuild()
    {
        ClearGrid();
        GenerateGrid();
    }

    [ContextMenu("그리드 지우기 (Clear Grid)")]
    public void ClearGrid()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            GameObject child = transform.GetChild(i).gameObject;
            if (Application.isPlaying) Destroy(child);
            else DestroyImmediate(child);
        }
        tiles = null;
    }

    void GenerateGrid()
    {
        tiles = new Tile[width, height];
        HashSet<Vector2Int> pathSet = new HashSet<Vector2Int>(EffectivePath());

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                Vector2Int coord = new Vector2Int(x, y);

                // 타일 루트는 '판정용 껍데기'다 — 렌더러가 없고 콜라이더만 있다.
                // 겉모습은 Tile.SetVisual이 자식으로 붙인다(경로가 바뀌면 자식만 갈아끼우면 됨).
                GameObject go = new GameObject($"Tile_{x}_{y}");
                go.transform.SetParent(transform);
                go.transform.position = GridToWorld(coord);

                // 클릭 판정은 칸을 **꽉 채운다**(0.95가 아니라 tileSize).
                // 예전엔 큐브가 곧 콜라이더라 칸 사이 5% 틈에서 클릭이 새고 있었다.
                BoxCollider bc = go.AddComponent<BoxCollider>();
                bc.size = new Vector3(tileSize, 0.2f, tileSize);

                Tile tile = go.AddComponent<Tile>();
                bool isPath = pathSet.Contains(coord);
                tile.Init(coord, isPath ? TileType.Path : TileType.Buildable,
                          isPath ? pathColor : buildableColor);
                tiles[x, y] = tile;
            }
        }

        RefreshTileVisuals();
    }

    List<Vector2Int> EffectivePath()
    {
        if (pathCoords != null && pathCoords.Count > 0)
            return new List<Vector2Int>(pathCoords);

        List<Vector2Int> defaultPath = new List<Vector2Int>();
        int midY = height / 2;
        for (int x = 0; x < width; x++)
            defaultPath.Add(new Vector2Int(x, midY));
        return defaultPath;
    }

    // ── 런타임 경로 랜덤화 ──
    // ContextMenu를 붙여둔 이유: 편집 모드의 기본 경로는 **가로 일직선**이라 코너가 한 번도 안 나온다.
    // 코너 회전값을 맞추려면 Play를 눌러야 했는데, 그러면 반복이 너무 느리다.
    // 이걸로 편집 모드에서 뱀형 경로를 뽑아놓고 오프셋을 조정하면 즉시 확인된다.
    [ContextMenu("테스트용 무작위 경로 (Randomize Path)")]
    public void RandomizePath()
    {
        pathCoords = GenerateRandomPath();
        if (tiles == null) { GenerateGrid(); return; }
        ApplyPathToTiles();
    }

    void ApplyPathToTiles()
    {
        HashSet<Vector2Int> pathSet = new HashSet<Vector2Int>(pathCoords);
        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
            {
                Tile t = tiles[x, y];
                if (t == null) continue;
                bool isPath = pathSet.Contains(new Vector2Int(x, y));
                t.type = isPath ? TileType.Path : TileType.Buildable;
                t.occupied = false;
                t.SetColor(isPath ? pathColor : buildableColor);
            }

        // 경로가 바뀌었으니 어떤 칸이 길이고 어떤 칸이 땅인지도 바뀐다 → 모델을 다시 조립한다.
        RefreshTileVisuals();
    }

    // ── 타일 겉모습 조립 (2026-08-09) ────────────────────────────────────────
    // 에피소드마다 한 번(경로 재생성 시) 돈다. 192칸이라 비용은 무시할 만하다.
    [ContextMenu("타일 모델 새로고침 (Refresh Tile Visuals)")]
    public void RefreshTileVisuals()
    {
        if (tiles == null) return;

        // 좌표 → 경로상의 순번. 앞뒤 칸을 알아야 직선/코너를 구분할 수 있다.
        List<Vector2Int> path = EffectivePath();
        Dictionary<Vector2Int, int> order = new Dictionary<Vector2Int, int>();
        for (int i = 0; i < path.Count; i++) order[path[i]] = i;

        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
            {
                Tile t = tiles[x, y];
                if (t == null) continue;

                GameObject prefab;
                float yaw;
                if (t.type == TileType.Path && order.TryGetValue(new Vector2Int(x, y), out int idx))
                    PickPathVisual(path, idx, out prefab, out yaw);
                else
                    PickGroundVisual(out prefab, out yaw);

                // 타워가 올라갔을 때 쓸 모델. 길 타일은 점유될 일이 없으므로 null.
                GameObject occ = (swapTileOnBuild && t.type == TileType.Buildable)
                                 ? buildablePrefab : null;

                t.SetVisual(prefab, yaw, occ, tileVisualYOffset, tileSize * tileVisualScale);
            }
    }

    void PickGroundVisual(out GameObject prefab, out float yaw)
    {
        prefab = buildablePrefab;
        if (buildableVariants != null && buildableVariants.Length > 0 && Random.value < variantChance)
        {
            GameObject v = buildableVariants[Random.Range(0, buildableVariants.Length)];
            if (v != null) prefab = v;
        }
        // 평평한 땅은 어느 쪽으로 돌려도 되므로 90°씩 무작위로 돌려 반복감을 줄인다.
        yaw = Random.Range(0, 4) * 90f;
    }

    // 경로 i번째 칸이 직선인지 코너인지 시작/끝인지 판정하고, 모델과 회전각을 정한다.
    void PickPathVisual(List<Vector2Int> path, int i, out GameObject prefab, out float yaw)
    {
        Vector2Int cur = path[i];
        bool hasPrev = i > 0;
        bool hasNext = i < path.Count - 1;
        Vector2Int dirIn  = hasPrev ? cur - path[i - 1] : Vector2Int.zero;
        Vector2Int dirOut = hasNext ? path[i + 1] - cur : Vector2Int.zero;

        // 첫 칸 = 적이 나오는 곳, 마지막 칸 = 골
        // 두 경우 모두 '길이 열려 있는 쪽'을 기준으로 각을 잡는다 (의미를 통일해야 보정값이 헷갈리지 않는다).
        if (!hasPrev)
        {
            prefab = (pathStartPrefab != null) ? pathStartPrefab : pathStraightPrefab;
            yaw = Yaw(dirOut) + startYawOffset;          // 나가는 쪽으로 열림
            return;
        }
        if (!hasNext)
        {
            prefab = (pathEndPrefab != null) ? pathEndPrefab : pathStraightPrefab;
            yaw = Yaw(-dirIn) + endYawOffset;            // 들어온 쪽으로 열림 (진행 방향의 반대)
            return;
        }

        if (dirIn == dirOut)   // 들어온 방향과 나가는 방향이 같다 = 직선
        {
            prefab = pathStraightPrefab;
            yaw = Yaw(dirOut) + straightYawOffset;
            return;
        }

        // 코너. 열린 두 변은 '들어온 쪽'(-dirIn)과 '나가는 쪽'(dirOut)이다.
        // 두 방향을 더하면 대각선이 나오고, 그 각도가 45/135/225/315로 네 방향에 딱 갈린다.
        // 경로를 거꾸로 지나가도 같은 값이 나오므로 진행 방향과 무관하게 일관된다.
        if (pathCornerPrefab != null)
        {
            prefab = pathCornerPrefab;
            yaw = Yaw((-dirIn) + dirOut) + cornerYawOffset;
        }
        else
        {
            // 코너 모델이 없으면 직선으로 때운다 (대각선 각을 쓰면 어색해지므로 진행 방향 기준)
            prefab = pathStraightPrefab;
            yaw = Yaw(dirOut) + straightYawOffset;
        }
    }

    // 그리드 방향(x, y) → Y축 회전각. (0,1)=+Z=0° · (1,0)=+X=90° · (0,-1)=180° · (-1,0)=270°
    static float Yaw(Vector2Int d)
    {
        if (d == Vector2Int.zero) return 0f;
        return Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
    }

    // 사행(뱀형) 경로: 오른쪽↔왼쪽으로 왕복하며 세로로 훑다가, 마지막에 오른쪽 끝으로 빠져나간다.
    // 왕복 횟수(레인 수)를 무작위로 → 가끔 단조(1줄), 가끔 큰 사행.
    List<Vector2Int> GenerateRandomPath()
    {
        // 1차: 길이 하한을 만족하는 경로만 채택
        for (int attempt = 0; attempt < 60; attempt++)
        {
            List<Vector2Int> p = TrySerpentine();
            if (p != null && p.Count >= minPathLength) return p;
        }
        // 2차: 그리드가 작아 하한을 못 채우는 경우를 위해 조건을 푼다
        for (int attempt = 0; attempt < 20; attempt++)
        {
            List<Vector2Int> p = TrySerpentine();
            if (p != null && p.Count >= 2) return p;
        }
        // 실패 시 기본 직선
        List<Vector2Int> fb = new List<Vector2Int>();
        int midY = height / 2;
        for (int x = 0; x < width; x++) fb.Add(new Vector2Int(x, midY));
        return fb;
    }

    List<Vector2Int> TrySerpentine()
    {
        List<Vector2Int> path = new List<Vector2Int>();
        HashSet<Vector2Int> inPath = new HashSet<Vector2Int>();

        int y = Mathf.Clamp(Random.Range(height / 5, (4 * height) / 5 + 1), 0, height - 1);
        int x = 0;
        if (!AddSafe(path, inPath, x, y)) return null;

        int vDir = (y <= (height - 1) / 2) ? 1 : -1;   // 항상 공간이 넓은 쪽으로 (왕복 여유 확보)

        int spacing = 3;                          // 레인 간격 3칸 → 잔길(범프) 넣을 여유
        int room = (vDir > 0) ? (height - 1 - y) : y;
        int maxLanes = room / spacing + 1;
        int maxOdd = (maxLanes % 2 == 1) ? maxLanes : maxLanes - 1; // 마지막 레인이 오른쪽이 되게 홀수
        if (maxOdd < 1) maxOdd = 1;
        int choices = (maxOdd + 1) / 2;                 // 홀수 후보 개수 (1,3,5,...)
        int pick;
        if (choices <= 1) pick = 0;                     // 공간이 작으면 어쩔 수 없이 1줄
        // 1줄짜리 직선 경로는 약 20칸밖에 안 돼서 S2 클리어율이 26%까지 떨어졌다.
        // '다양성'을 위해 15% 확률로 뽑고 있었는데, 실제로는 못 깨는 맵을 만들고 있었다. → 기본 0.
        else if (Random.value < straightLaneChance) pick = 0;
        else pick = Random.Range(1, choices);           // 대부분 여러 번 왕복하는 스네이크
        int laneCap = 1 + 2 * pick;

        int dirX = 1;   // 짝수 레인=오른쪽, 홀수 레인=왼쪽
        for (int lane = 0; lane < laneCap; lane++)
        {
            bool lastLane = (lane == laneCap - 1);

            int target;
            if (lastLane) target = width - 1;
            else if (dirX > 0) target = Random.Range(Mathf.Max(x + 1, width / 2), width);
            else target = Random.Range(0, Mathf.Min(x, width / 2) + 1);
            target = Mathf.Clamp(target, 0, width - 1);

            if (!RunLane(path, inPath, ref x, ref y, target, vDir)) return null;

            if (lastLane) break;

            int ny = y + spacing * vDir;
            if (ny < 0 || ny > height - 1) break;
            for (int k = 1; k <= spacing; k++)          // 세로 연결 타일들
                if (!AddSafe(path, inPath, x, y + vDir * k)) return null;
            y = ny;
            dirX = -dirX;
        }

        while (x < width - 1)
        {
            x++;
            if (!AddSafe(path, inPath, x, y)) return null;
        }
        return path;
    }

    // 한 레인을 targetX까지 그리되, ±1칸 세로 폭 안에서 오르내리며 흐른다.
    // 반드시 복귀하지 않으므로, 끝난 행(by)이 시작 행과 달라질 수 있다(비복귀 잔길).
    bool RunLane(List<Vector2Int> path, HashSet<Vector2Int> inPath, ref int x, ref int by, int targetX, int vDir)
    {
        int startRow = by;
        int step = (targetX >= x) ? 1 : -1;
        int guard = 0;
        while (x != targetX && guard++ < width * height)
        {
            // 가끔(드물게) 가로 대신 세로로 한 칸 — 스네이크는 유지하고 양념만
            if (Random.value < 0.15f)
            {
                int vd = (Random.value < 0.5f) ? 1 : -1;
                int ny = by + vd;
                if (Mathf.Abs(ny - startRow) <= 1 && AddSafe(path, inPath, x, ny))
                {
                    by = ny;   // 복귀하지 않고 그 행에서 계속
                    continue;
                }
            }
            x += step;
            if (!AddSafe(path, inPath, x, by)) return false;
        }
        return x == targetX;
    }

    // (x,y)를 경로에 추가. 경계 밖/중복/‘직전 타일 외 경로와 맞닿음(뭉침)’이면 실패.
    bool AddSafe(List<Vector2Int> path, HashSet<Vector2Int> inPath, int x, int y)
    {
        if (x < 0 || x > width - 1 || y < 0 || y > height - 1) return false;
        Vector2Int n = new Vector2Int(x, y);
        if (inPath.Contains(n)) return false;
        if (path.Count > 0 && TouchesOtherPath(n, path[path.Count - 1], inPath)) return false;
        path.Add(n);
        inPath.Add(n);
        return true;
    }

    bool TouchesOtherPath(Vector2Int n, Vector2Int cur, HashSet<Vector2Int> inPath)
    {
        Vector2Int[] dirs =
        {
            new Vector2Int(1, 0), new Vector2Int(-1, 0),
            new Vector2Int(0, 1), new Vector2Int(0, -1)
        };
        foreach (Vector2Int d in dirs)
        {
            Vector2Int m = new Vector2Int(n.x + d.x, n.y + d.y);
            if (m == cur) continue;
            if (inPath.Contains(m)) return true;
        }
        return false;
    }

    public Vector3 GridToWorld(Vector2Int coord)
    {
        float offsetX = (width - 1) * tileSize * 0.5f;
        float offsetZ = (height - 1) * tileSize * 0.5f;
        return new Vector3(coord.x * tileSize - offsetX, 0f, coord.y * tileSize - offsetZ);
    }

    // 월드 좌표 → 가장 가까운 타일 좌표 (GridToWorld의 역변환). 범위 밖이면 (-1,-1)
    public Vector2Int WorldToGrid(Vector3 world)
    {
        float offsetX = (width - 1) * tileSize * 0.5f;
        float offsetZ = (height - 1) * tileSize * 0.5f;
        int gx = Mathf.RoundToInt((world.x + offsetX) / Mathf.Max(0.0001f, tileSize));
        int gy = Mathf.RoundToInt((world.z + offsetZ) / Mathf.Max(0.0001f, tileSize));
        if (gx < 0 || gx >= width || gy < 0 || gy >= height) return new Vector2Int(-1, -1);
        return new Vector2Int(gx, gy);
    }

    public Tile GetTile(Vector2Int coord)
    {
        if (tiles == null) return null;
        if (coord.x < 0 || coord.x >= width || coord.y < 0 || coord.y >= height) return null;
        return tiles[coord.x, coord.y];
    }

    public List<Vector3> GetPathWorldPoints()
    {
        List<Vector3> points = new List<Vector3>();
        foreach (var c in EffectivePath())
            points.Add(GridToWorld(c));
        return points;
    }
}
