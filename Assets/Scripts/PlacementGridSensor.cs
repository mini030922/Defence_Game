using UnityEngine;
using Unity.MLAgents.Sensors;

// 배치 AI의 맵 그리드를 이미지(채널×높이×너비)로 굽는 실제 센서.
// 설정값과 부착은 PlacementGridSensorComponent 가 담당한다.
// (MonoBehaviour가 아니므로 이 파일을 GameObject에 직접 끌어다 붙일 수 없다)
public class PlacementGridSensor : ISensor
{
    // 채널: 설치가능-빈칸 / 경로 / 타워있음 / 경로진행도 / 경로근접도 / 경로밀집도 / 적밀도 / 타워별가동률
    public const int CHANNELS = 8;

    private readonly PlacementGridSensorComponent c;
    private readonly ObservationSpec spec;
    private readonly float[,,] buf;   // [채널, y, x]

    public PlacementGridSensor(PlacementGridSensorComponent comp)
    {
        c = comp;
        spec = ObservationSpec.Visual(CHANNELS, c.gridH, c.gridW);
        buf = new float[CHANNELS, c.gridH, c.gridW];
    }

    public ObservationSpec GetObservationSpec() => spec;
    public string GetName() => "PlacementGrid";
    public byte[] GetCompressedObservation() => null;
    public CompressionSpec GetCompressionSpec() => CompressionSpec.Default();
    public void Reset() { System.Array.Clear(buf, 0, buf.Length); }

    // ML-Agents가 매 결정마다 Write 직전에 호출한다 → 여기서 최신 상태를 굽는다
    public void Update() { Rebuild(); }

    public int Write(ObservationWriter writer)
    {
        int n = 0;
        for (int ch = 0; ch < CHANNELS; ch++)
            for (int y = 0; y < c.gridH; y++)
                for (int x = 0; x < c.gridW; x++)
                {
                    writer[ch, y, x] = buf[ch, y, x];
                    n++;
                }
        return n;
    }

    void Rebuild()
    {
        System.Array.Clear(buf, 0, buf.Length);
        GridManager grid = c.grid;
        if (grid == null) return;

        int W = c.gridW, H = c.gridH;

        // ch0~2: 타일 상태 (설치가능-빈칸 / 경로 / 타워 있음)
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                Tile t = grid.GetTile(new Vector2Int(x, y));
                if (t == null) continue;
                buf[0, y, x] = t.CanBuild() ? 1f : 0f;
                buf[1, y, x] = (t.type == TileType.Path) ? 1f : 0f;
                buf[2, y, x] = (t.type == TileType.Buildable && t.occupied) ? 1f : 0f;
            }

        // ch3~5: 경로 진행도 / 근접도 / 밀집도
        var pl = grid.pathCoords;
        int n = (pl != null) ? pl.Count : 0;
        if (n > 0)
        {
            for (int i = 0; i < n; i++)
            {
                Vector2Int p = pl[i];
                if (p.x >= 0 && p.x < W && p.y >= 0 && p.y < H)
                    buf[3, p.y, p.x] = (i + 1) / (float)n;   // 입구≈작은 값 → 골=1
            }

            float dr2 = c.densityRefRange * c.densityRefRange;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float bestSq = float.MaxValue;
                    int within = 0;
                    for (int i = 0; i < n; i++)
                    {
                        float dx = pl[i].x - x, dy = pl[i].y - y;
                        float sq = dx * dx + dy * dy;
                        if (sq <= dr2) within++;
                        if (sq < bestSq) bestSq = sq;
                    }
                    buf[4, y, x] = Mathf.Clamp01(1f - Mathf.Sqrt(bestSq) / Mathf.Max(0.1f, c.proxRef));
                    buf[5, y, x] = Mathf.Clamp01(within / Mathf.Max(1f, c.densityRefTiles));
                }
        }

        // ch6: 지금 필드의 적 밀도
        foreach (Enemy e in Object.FindObjectsByType<Enemy>(FindObjectsSortMode.None))
        {
            if (e == null) continue;
            Vector2Int g = grid.WorldToGrid(e.transform.position);
            if (g.x < 0 || g.x >= W || g.y < 0 || g.y >= H) continue;
            buf[6, g.y, g.x] = Mathf.Clamp01(buf[6, g.y, g.x] + 1f / Mathf.Max(1f, c.enemyDensRefCount));
        }

        // ch7: 그 칸 타워가 직전 라운드에 얼마나 일했나 (나쁜 배치를 교정할 근거)
        foreach (Tower t in Object.FindObjectsByType<Tower>(FindObjectsSortMode.None))
        {
            if (t == null) continue;
            Vector2Int g = grid.WorldToGrid(t.transform.position);
            if (g.x < 0 || g.x >= W || g.y < 0 || g.y >= H) continue;
            buf[7, g.y, g.x] = Mathf.Clamp01(t.lastRoundUtil);
        }
    }
}
