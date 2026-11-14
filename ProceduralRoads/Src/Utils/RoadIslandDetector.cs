using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Practical island grouping from walkable ground and nearby crossable water.
/// This is an approximation: meeting fronts sample depth/river weight, not the
/// full pathfinder constraints. Never use the result to reject a build search.
/// All scan arrays are local; only the returned islands outlive the scan.
/// </summary>
public static class RoadIslandDetector
{
    private static BepInEx.Logging.ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;
    public const float SampleSpacing = 8f;
    /// <summary>The smallest island, in square metres, that road generation counts.</summary>
    public const float MinIslandArea = 163840;

    /// <summary>The one owner of island detection (docs/OWNERS.md).</summary>
    public static List<Island> Detect(WorldGenerator wg, float minArea = MinIslandArea, float worldRadius = RoadConstants.WorldRadius,
        RoadNetworkOptions? options = null)
    {
        List<Island>? result=null;
        using var preparation=new RoadPreparation(p=>DetectSteps(wg,p,value=>result=value,minArea,worldRadius,options));
        preparation.Drain(); return result!;
    }

    /// <summary>How far apart a scan bounded to the world's land (<see cref="DetectSteps"/>'s <c>toLand</c>) first reads the
    /// world for land: every fourth cell.</summary>
    internal const float LandProbeSpacing = 4 * SampleSpacing;

    /// <summary>The farthest from land the grouping reads the scan's cells (a bridge's span across water, the coast's raster,
    /// <see cref="RoadCoastalLandings.RasterReach"/>), and a cell either side.</summary>
    internal static float GroupingReach => Mathf.Max(RoadConstants.MaxBridgeSpan, RoadCoastalLandings.RasterReach) + 2f * SampleSpacing;

    /// <summary>The islands of the disc of <paramref name="worldRadius"/> round the world's centre. With
    /// <paramref name="toLand"/> (the unit suite's small worlds, <see cref="IslandDetector.BoundedScan"/>) the scan first reads
    /// the disc for land every <see cref="LandProbeSpacing"/> and scans only out to the farthest land it read, two probes and
    /// <see cref="GroupingReach"/> beyond. Its cells stand where the whole disc's do (the bound a whole number of cells), so it
    /// finds the same islands; where land it did not read stands nearer the bound's edge than <see cref="GroupingReach"/>, it
    /// scans the whole disc after all.</summary>
    internal static IEnumerable<bool> DetectSteps(WorldGenerator wg, RoadPreparation preparation, System.Action<List<Island>> complete,
        float minArea=MinIslandArea,float worldRadius=RoadConstants.WorldRadius,RoadNetworkOptions? options=null,bool toLand=false)
    {
        if (wg == null)
        {
            Log.LogWarning("IslandDetector: WorldGenerator not available");
            complete(new List<Island>()); yield break;
        }

        const float cellSize = SampleSpacing;
        if (float.IsNaN(worldRadius) || worldRadius <= 0 || worldRadius > RoadConstants.WorldRadius)
            throw new System.ArgumentOutOfRangeException(nameof(worldRadius));
        float wholeRadius = worldRadius;
        int wholeGrid = Mathf.CeilToInt(wholeRadius * 2f / cellSize);
        preparation.BeginStage("Mapping island terrain",(long)wholeGrid*wholeGrid,"cells"); yield return true;
        float edge2 = float.MaxValue;
        if (toLand && wholeRadius % cellSize == 0f)
        {
            // The land's reach, read every LandProbeSpacing over the whole disc.
            var probeCorners = BiomeBlendedHeight.Corners.ForColumnScan(wholeRadius);
            int probes = Mathf.FloorToInt(wholeRadius / LandProbeSpacing);
            float farthest2 = 0f;
            for (int i = -probes; i <= probes; i++)
                for (int j = -probes; j <= probes; j++)
                {
                    float px = i * LandProbeSpacing, pz = j * LandProbeSpacing;
                    float r2 = px * px + pz * pz;
                    if (r2 > wholeRadius * wholeRadius || r2 <= farthest2) continue;
                    if (preparation.Checkpoint(0)) yield return true;
                    if (RoadWater.OnLand(wg, new Vector2(px, pz), BiomeBlendedHeight.GetBlendedHeight(px, pz, wg, probeCorners)))
                        farthest2 = r2;
                }
            float inner = Mathf.Sqrt(farthest2) + 2f * LandProbeSpacing;
            float bound = Mathf.CeilToInt((inner + GroupingReach) / cellSize) * cellSize;
            if (bound < wholeRadius)
            {
                worldRadius = bound;
                edge2 = inner * inner;
            }
        }
        bool bounded = worldRadius < wholeRadius;
        int gridSize = Mathf.CeilToInt(worldRadius * 2f / cellSize);
        float worldOffset = worldRadius;
        int n = gridSize * gridSize;

        Log.LogDebug($"IslandDetector: walkable+crossable, scanning {gridSize}x{gridSize} " +
                     $"grid (cellSize={cellSize}m)");

        bool[] walkable = new bool[n];
        float[] height = new float[n];
        int landCells = 0;
        // The scan runs down whole columns: one column's heightmap corners, and the next's, read once each.
        var corners = BiomeBlendedHeight.Corners.ForColumnScan(worldRadius);
        for (int x = 0; x < gridSize; x++)
        {
            for (int y = 0; y < gridSize; y++)
            {
                float wx = x * cellSize - worldOffset;
                float wy = y * cellSize - worldOffset;
                if (wx * wx + wy * wy > worldRadius * worldRadius)
                {
                    height[x * gridSize + y] = float.MinValue;
                    if(preparation.Checkpoint()) yield return true;
                    continue;
                }

                // The mod's own answer to "can a road stand here": the
                // shallow-water line plus the bank clearance, except in a swamp,
                // which a road wades down to the deep-water line. Using a
                // threshold of my own here would have quietly excluded every
                // swamp in the world - and the generator builds roads through
                // swamps. The height is the natural ground the game renders
                // (BiomeBlendedHeight), which the road search walks too: the
                // biome at the point alone (WorldGenerator.GetHeight) reads that
                // biome's formula wherever it reaches between four corners of
                // another: a swamp or ocean formula there reads water metres under
                // dry rendered land, a line that can cut one landmass in two.
                float h = BiomeBlendedHeight.GetBlendedHeight(wx, wy, wg, corners);
                height[x * gridSize + y] = h;
                if (RoadWater.OnLand(wg, new Vector2(wx, wy), h))
                {
                    if (bounded && wx * wx + wy * wy > edge2)
                    {
                        // Land the probes did not read, near the bound's edge: the whole disc, as without the bound.
                        Log.LogDebug($"IslandDetector: land at ({wx:F0},{wy:F0}), near the {worldRadius:F0} m bound: the whole disc is scanned");
                        foreach (var step in DetectSteps(wg, preparation, complete, minArea, wholeRadius, options)) yield return step;
                        yield break;
                    }
                    walkable[x * gridSize + y] = true;
                    landCells++;
                }
                if(preparation.Checkpoint()) yield return true;
            }
        }

        // Pieces of walkable ground, before any crossing is considered.
        preparation.BeginStage("Labeling island terrain",n,"cells"); yield return true;
        int[] piece = new int[n];
        for (int i = 0; i < n; i++) { piece[i] = -1; if(preparation.Checkpoint()) yield return true; }
        preparation.BeginStage("Grouping walkable cells",unit:"visits"); yield return true;
        List<List<Vector2Int>> pieces = new();
        Queue<int> queue = new();
        for (int start = 0; start < n; start++)
        {
            if(preparation.Checkpoint()) yield return true;
            if (!walkable[start] || piece[start] >= 0) continue;
            int id = pieces.Count;
            List<Vector2Int> cells = new();
            piece[start] = id;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                int cx = c / gridSize, cy = c % gridSize;
                cells.Add(new Vector2Int(cx, cy));
                if (cx + 1 < gridSize) Push(c + gridSize);
                if (cx > 0) Push(c - gridSize);
                if (cy + 1 < gridSize) Push(c + 1);
                if (cy > 0) Push(c - 1);
                if(preparation.Checkpoint()) yield return true;
                void Push(int nb)
                {
                    if (walkable[nb] && piece[nb] < 0) { piece[nb] = id; queue.Enqueue(nb); }
                }
            }
            pieces.Add(cells);
        }

        // Rejoin across crossable water.
        preparation.BeginStage("Preparing water crossings",n+pieces.Count,"entries"); yield return true;
        int[] parent = new int[pieces.Count];
        for (int i = 0; i < parent.Length; i++) { parent[i] = i; if(preparation.Checkpoint()) yield return true; }
        int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        int fordCells = RoadConstants.MaxRiverCrossingCells;
        int bridgeCells = RoadConstants.MaxBridgeCrossingCells;
        float fordMetres = fordCells * RoadPathfinder.CellSize;
        float bridgeMetres = bridgeCells * RoadPathfinder.CellSize;
        int reach = Mathf.CeilToInt(bridgeMetres / cellSize);
        // Reuse the component labels; the land cells keep their original id.
        // Water fronts need at most 16 steps on the finest supported grid.
        int[] owner = piece;
        byte[] steps = new byte[n];
        Queue<int> front = new();
        for (int i = 0; i < n; i++) { if (owner[i] >= 0) front.Enqueue(i); if(preparation.Checkpoint()) yield return true; }
        preparation.BeginStage("Joining crossable shores",unit:"visits"); yield return true;

        int joinedFord = 0, joinedBridge = 0;
        while (front.Count > 0)
        {
            int c = front.Dequeue();
            int d = steps[c];
            if (d >= reach) { if(preparation.Checkpoint()) yield return true; continue; }
            int cx = c / gridSize, cy = c % gridSize;
            if (cx + 1 < gridSize) Visit(c + gridSize);
            if (cx > 0) Visit(c - gridSize);
            if (cy + 1 < gridSize) Visit(c + 1);
            if (cy > 0) Visit(c - 1);
            if(preparation.Checkpoint()) yield return true;

            void Visit(int nb)
            {
                if (walkable[nb] || height[nb] == float.MinValue) return;
                if (owner[nb] < 0)
                {
                    owner[nb] = owner[c];
                    steps[nb] = (byte)(d + 1);
                    front.Enqueue(nb);
                    return;
                }
                if (Find(owner[nb]) == Find(owner[c])) return;

                // Two shores meet in this cell: the gap is both walks plus it.
                float gap = (d + 1 + steps[nb]) * cellSize;
                float depth = RoadWater.LandingFloor - height[nb];
                bool ford = gap <= fordMetres && depth <= RoadConstants.FordWadeDepth;
                bool bridge = false;
                if (!ford && gap <= bridgeMetres)
                {
                    float wx = (nb / gridSize) * cellSize - worldOffset;
                    float wy = (nb % gridSize) * cellSize - worldOffset;
                    wg.GetRiverWeight(wx, wy, out float weight, out _);
                    bridge = weight > 0f;
                }
                if (!ford && !bridge) return;
                Union(owner[nb], owner[c]);
                if (ford) joinedFord++; else joinedBridge++;
            }
        }

        // Pieces that were joined become one island.
        preparation.BeginStage("Merging island groups",unit:"cells"); yield return true;
        Dictionary<int, List<Vector2Int>> merged = new();
        for (int i = 0; i < pieces.Count; i++)
        {
            int root = Find(i);
            if (!merged.TryGetValue(root, out List<Vector2Int>? cells))
                merged[root] = cells = new List<Vector2Int>();
            foreach(var cell in pieces[i]) { cells.Add(cell); if(preparation.Checkpoint()) yield return true; }
        }

        preparation.BeginStage("Measuring islands",unit:"cells"); yield return true;
        List<Island> islands = new();
        foreach (List<Vector2Int> cells in merged.Values)
        {
            if (cells.Count * cellSize * cellSize < minArea) continue;
            foreach(var step in BuildSteps(cells,cellSize,worldOffset,preparation,islands.Add)) yield return step;
        }

        preparation.BeginStage("Ordering islands"); yield return true;
        islands.Sort(BySizeThenPosition);
        for (int i = 0; i < islands.Count; i++) islands[i].Id = i;
        // Landings filter by the options' biomes; without options (a bare grouping) there are none.
        if (options != null)
            foreach (var island in islands)
                foreach(var step in RoadCoastalLandings.FindSteps(island,height,gridSize,(x,z)=>RoadBiome.At(wg,x,z),options,
                    preparation,landings=>island.CoastalLandings=landings)) yield return step;

        Log.LogDebug($"IslandDetector: {landCells} walkable cells, {pieces.Count} pieces, " +
                     $"{joinedFord} ford join(s), {joinedBridge} bridge join(s), " +
                     $"{islands.Count} islands at or above {minArea / 1_000_000f:F2} km²");
        complete(islands);
    }

    /// <summary>Order for Id assignment: biggest first, then by position.
    ///
    /// Id is load-bearing - it breaks ties in island selection - so it may not come
    /// from how the pieces happened to be stored. List.Sort is unstable and the
    /// islands arrive in Dictionary enumeration order, so cell count alone left
    /// two equal islands taking their ids from hash order. The bounding box is
    /// a property of the island itself, so this is reproducible.</summary>
    internal static int BySizeThenPosition(Island a, Island b)
    {
        int cmp = b.CellCount.CompareTo(a.CellCount);
        if (cmp != 0) return cmp;
        cmp = a.Min.x.CompareTo(b.Min.x);
        if (cmp != 0) return cmp;
        cmp = a.Min.y.CompareTo(b.Min.y);
        if (cmp != 0) return cmp;
        cmp = a.Max.x.CompareTo(b.Max.x);
        return cmp != 0 ? cmp : a.Max.y.CompareTo(b.Max.y);
    }

    /// <summary>Build an island from the joined land cells.</summary>
    private static IEnumerable<bool> BuildSteps(List<Vector2Int> cells, float cellSize, float worldOffset,
        RoadPreparation preparation,System.Action<Island> complete)
    {
        Island island = new() { Cells = cells, CellCount = cells.Count };
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        float sumX = 0f, sumY = 0f;
        foreach (Vector2Int cell in cells)
        {
            Vector2 p = IslandDetector.CellToWorld(cell, cellSize, worldOffset);
            sumX += p.x; sumY += p.y;
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.y < minY) minY = p.y;
            if (p.y > maxY) maxY = p.y;
            if(preparation.Checkpoint()) yield return true;
        }

        Vector2 centroid = new(sumX / cells.Count, sumY / cells.Count);
        float best = float.MaxValue;
        Vector2 closest = centroid;
        foreach (Vector2Int cell in cells)
        {
            Vector2 p = IslandDetector.CellToWorld(cell, cellSize, worldOffset);
            float d = (p.x - centroid.x) * (p.x - centroid.x) + (p.y - centroid.y) * (p.y - centroid.y);
            if (d < best) { best = d; closest = p; }
            if(preparation.Checkpoint()) yield return true;
        }

        island.Center = closest;
        island.Min = new Vector2(minX, minY);
        island.Max = new Vector2(maxX, maxY);
        island.ApproxArea = cells.Count * cellSize * cellSize;
        island.CellSize = cellSize;
        island.WorldOffset = worldOffset;
        complete(island);
    }

}
