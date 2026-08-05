using System.Collections.Generic;
using UnityEngine;

// N×M 사각형 타일 그리드를 생성하고, 경로/설치가능 타일을 구분한다.
// 편집 모드에서도 우클릭 메뉴로 그리드를 생성해 볼 수 있고,
// 런타임에는 에피소드마다 사행(뱀형) 경로를 무작위로 새로 만든다.
public class GridManager : MonoBehaviour
{
    [Header("그리드 크기")]
    public int width = 10;
    public int height = 8;
    public float tileSize = 1f;

    [Header("색상")]
    public Color buildableColor = new Color(0.35f, 0.65f, 0.35f);
    public Color pathColor = new Color(0.82f, 0.72f, 0.45f);

    [Header("경로 길이 하한 (2026-07-28 추가)")]
    // 짧은 경로는 타워가 쏠 시간도, 한 타워가 덮을 구간도 적어 게임이 급격히 어려워진다.
    // 실측(dda_v5 로그): 경로 20~29칸 맵의 S2 클리어율 26% vs 80~89칸 맵 85%.
    //                   라운드당 손실도 0.152 vs 0.047로 3.2배 차이.
    // 즉 난이도 평균이 아니라 **맵 변동성**이 클리어 가능 여부를 지배하고 있었다.
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
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"Tile_{x}_{y}";
                go.transform.SetParent(transform);
                go.transform.position = GridToWorld(coord);
                go.transform.localScale = new Vector3(tileSize * 0.95f, 0.2f, tileSize * 0.95f);

                Tile tile = go.AddComponent<Tile>();
                bool isPath = pathSet.Contains(coord);
                tile.Init(coord, isPath ? TileType.Path : TileType.Buildable,
                          isPath ? pathColor : buildableColor);
                tiles[x, y] = tile;
            }
        }
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
