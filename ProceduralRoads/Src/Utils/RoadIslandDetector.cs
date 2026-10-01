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

    public static List<Island> Detect(WorldGenerator wg, float minArea = 163840, float worldRadius = 10000,
        RoadNetworkOptions? options = null)
    {
        if (wg == null)
        {
            Log.LogWarning("IslandDetector: WorldGenerator not available");
            return new List<Island>();
        }

        const float cellSize = SampleSpacing;
        if (float.IsNaN(worldRadius) || worldRadius <= 0 || worldRadius > 10000)
            throw new System.ArgumentOutOfRangeException(nameof(worldRadius));
        int gridSize = Mathf.CeilToInt(worldRadius * 2f / cellSize);
        float worldOffset = worldRadius;
        int n = gridSize * gridSize;

        Log.LogDebug($"IslandDetector: walkable+crossable, scanning {gridSize}x{gridSize} " +
                     $"grid (cellSize={cellSize}m)");

        bool[] walkable = new bool[n];
        float[] height = new float[n];
        int landCells = 0;
        for (int x = 0; x < gridSize; x++)
        {
            for (int y = 0; y < gridSize; y++)
            {
                float wx = x * cellSize - worldOffset;
                float wy = y * cellSize - worldOffset;
                if (wx * wx + wy * wy > worldRadius * worldRadius)
                {
                    height[x * gridSize + y] = float.MinValue;
                    continue;
                }

                // The mod's own answer to "can a road stand here": the
                // shallow-water line plus the bank clearance, except in a swamp,
                // which a road wades down to the deep-water line. Using a
                // threshold of my own here would have quietly excluded every
                // swamp in the world - and the generator builds roads through
                // swamps.
                float h = wg.GetHeight(wx, wy);
                height[x * gridSize + y] = h;
                if (h >= RoadPathfinder.FloorFor(wg.GetBiome(wx, wy)))
                {
                    walkable[x * gridSize + y] = true;
                    landCells++;
                }
            }
        }

        // Pieces of walkable ground, before any crossing is considered.
        int[] piece = new int[n];
        for (int i = 0; i < n; i++) piece[i] = -1;
        List<List<Vector2Int>> pieces = new();
        Queue<int> queue = new();
        for (int start = 0; start < n; start++)
        {
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
                void Push(int nb)
                {
                    if (walkable[nb] && piece[nb] < 0) { piece[nb] = id; queue.Enqueue(nb); }
                }
            }
            pieces.Add(cells);
        }

        // Rejoin across crossable water.
        int[] parent = new int[pieces.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
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
        for (int i = 0; i < n; i++) if (owner[i] >= 0) front.Enqueue(i);

        int joinedFord = 0, joinedBridge = 0;
        while (front.Count > 0)
        {
            int c = front.Dequeue();
            int d = steps[c];
            if (d >= reach) continue;
            int cx = c / gridSize, cy = c % gridSize;
            if (cx + 1 < gridSize) Visit(c + gridSize);
            if (cx > 0) Visit(c - gridSize);
            if (cy + 1 < gridSize) Visit(c + 1);
            if (cy > 0) Visit(c - 1);

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
                float depth = RoadPathfinder.LandingFloor - height[nb];
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
        Dictionary<int, List<Vector2Int>> merged = new();
        for (int i = 0; i < pieces.Count; i++)
        {
            int root = Find(i);
            if (!merged.TryGetValue(root, out List<Vector2Int>? cells))
                merged[root] = cells = new List<Vector2Int>();
            cells.AddRange(pieces[i]);
        }

        List<Island> islands = new();
        foreach (List<Vector2Int> cells in merged.Values)
        {
            if (cells.Count * cellSize * cellSize < minArea) continue;
            islands.Add(Build(cells, cellSize, worldOffset));
        }

        islands.Sort(BySizeThenPosition);
        for (int i = 0; i < islands.Count; i++) islands[i].Id = i;
        Log.LogDebug($"IslandDetector: {landCells} walkable cells, {pieces.Count} pieces, " +
                     $"{joinedFord} ford join(s), {joinedBridge} bridge join(s), " +
                     $"{islands.Count} islands at or above {minArea / 1_000_000f:F2} km²");
        return islands;
    }

    /// <summary>Order for Id assignment: biggest first, then by position.
    ///
    /// Id is load-bearing - it breaks ties in island selection and, with routed
    /// connections off, chooses an island's plan by parity - so it may not come
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
    private static Island Build(List<Vector2Int> cells, float cellSize, float worldOffset)
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
        }

        Vector2 centroid = new(sumX / cells.Count, sumY / cells.Count);
        float best = float.MaxValue;
        Vector2 closest = centroid;
        foreach (Vector2Int cell in cells)
        {
            Vector2 p = IslandDetector.CellToWorld(cell, cellSize, worldOffset);
            float d = (p.x - centroid.x) * (p.x - centroid.x) + (p.y - centroid.y) * (p.y - centroid.y);
            if (d < best) { best = d; closest = p; }
        }

        island.Center = closest;
        island.Min = new Vector2(minX, minY);
        island.Max = new Vector2(maxX, maxY);
        island.ApproxArea = cells.Count * cellSize * cellSize;
        island.CellSize = cellSize;
        island.WorldOffset = worldOffset;
        return island;
    }

}
