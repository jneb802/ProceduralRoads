using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The game's river weight (WorldGenerator.GetRiverWeight) read without its shared cache while a generation's island
/// workers run. The game keeps one last river grid cell for every thread, behind one ReaderWriterLockSlim: workers reading
/// different cells keep replacing it, so most reads wait on its write lock. Here each thread keeps its own last cell, read
/// from the world's river points with the game's own grid (WorldGenerator.GetRiverGrid) and weight
/// (WorldGenerator.GetWeight): the same weight and width to the bit. The river points are made with the world and are not
/// written while the workers run (the game's own read of them takes no lock either).
/// <para>Opened for one world by the generation's workers (<see cref="Begin"/>, RoadNetworkGenerator.GenerationWork.Compute, and
/// RoadIslandDetector.DetectSteps while it maps the world's terrain on them)
/// and closed when they end (<see cref="End"/>); outside that, and for any other world, the game's own method runs
/// (Patches/WorldGenerator_Patch.cs, installed once with the mod's other patches). Each opening forgets every thread's last
/// cell; a thread's last cell is its world's, so two worlds open at once never share one. The one owner of how the mod reads the game's rivers during generation (docs/OWNERS.md).</para>
/// </summary>
internal static class RiverWeightReads
{
    /// <summary>The worlds whose workers run now, once per run (a cancelled run may still be ending as the next starts):
    /// replaced whole under <see cref="s_gate"/>, read without it.</summary>
    private static WorldGenerator[] s_open = System.Array.Empty<WorldGenerator>();
    private static readonly object s_gate = new();
    private static int s_epoch;
    private sealed class RiverScratch : RoadNetworkGenerator.GenerationScratch
    {
        public int Epoch;
        public WorldGenerator? World;
        public Vector2i Grid;
        public WorldGenerator.RiverPoint[]? Points;
        /// <summary>The cell's points by sub-cell (<see cref="CellIndex"/>), where the reads are indexed; else null.</summary>
        public CellIndex? Index;
        internal override void Clear()
        {
            World = null;
            Points = null;
            Index = null;
        }
    }

    [System.ThreadStatic] private static RiverScratch? t_cell;

    /// <summary>The other mods that patch the game's GetRiverWeight (their Harmony ids), asked at each <see cref="Begin"/>:
    /// set by the patch when it is installed (Patches/WorldGenerator_Patch.cs); none where no patch is installed (tests,
    /// offline tools).</summary>
    internal static System.Func<string[]> PatchedByOthers = () => System.Array.Empty<string>();

    /// <summary>The mods that patch the game's GetWeight (their Harmony ids), asked at each <see cref="Begin"/>: the index
    /// (<see cref="CellIndex"/>) sums the river points itself and so would pass such a patch by, so where there is one every
    /// point of a cell is read through the game's GetWeight, patch and all. Set by the patch when it is installed
    /// (Patches/WorldGenerator_Patch.cs); none where no patch is installed (tests, offline tools).</summary>
    internal static System.Func<string[]> WeightPatchedBy = () => System.Array.Empty<string>();

    /// <summary>How many river points the start compares (<see cref="SelfCheck"/>): the first cells' first points.</summary>
    private const int CheckedCells = 32;

    /// <summary>
    /// A generation's workers start on <paramref name="world"/>: its river weights are read here until <see cref="End"/>,
    /// every thread's last cell forgotten; true. False, nothing opened and the game's own method read throughout, where
    /// another mod patches GetRiverWeight (its rivers are not the game's) or where the game's method does not give this
    /// read's weight and width to the bit at the world's river points (<see cref="SelfCheck"/>: a game update, or a
    /// change no patch list shows). Says which in the log.
    /// </summary>
    internal static bool Begin(WorldGenerator world)
    {
        // The whole start under the gate: a run that ends meanwhile (End, which forgets every flag once no run is left)
        // cannot drop the flag this start sets, nor end the last run between this start's look and its opening. The checks
        // read the game's method, which takes no part of the gate (TryRead reads the open worlds without it).
        lock (s_gate)
        {
            bool? indexed = null;
            if (!IsOpen(world))
            {
                string[] others = PatchedByOthers();
                if (others.Length > 0)
                {
                    ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(
                        $"[ROADS] river reads: GetRiverWeight is patched by {string.Join(", ", others)}; the game's own method is read this generation (slower, the same roads as that mod's rivers give).");
                    return false;
                }
                string? differs = SelfCheck(world, out int compared);
                if (differs != null)
                {
                    ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(
                        $"[ROADS] river reads: {differs}; the game's own method is read this generation (slower).");
                    return false;
                }
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"[ROADS] river reads: {compared} river point(s) read as the game reads them.");
                // The river points near each point asked (CellIndex), where they give the game's weight and width to the bit.
                int indexCompared = 0;
                string[] weightPatches = GenerationMemory.KeepsRiverIndex ? WeightPatchedBy() : System.Array.Empty<string>();
                string? indexDiffers = !GenerationMemory.KeepsRiverIndex ? "not kept (LowMemoryGeneration)"
                    : weightPatches.Length > 0 ? $"GetWeight is patched by {string.Join(", ", weightPatches)}"
                    : IndexCheck(world, out indexCompared);
                indexed = indexDiffers == null;
                if (indexDiffers == null)
                    ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"[ROADS] river reads: indexed by their nearby river points ({indexCompared} point(s) checked to the bit).");
                else if (weightPatches.Length > 0)
                    ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning($"[ROADS] river reads: every river point of a cell read through the game's GetWeight ({indexDiffers}; the index would pass that patch by).");
                else
                    ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"[ROADS] river reads: every river point of a cell read ({indexDiffers}).");
            }
            if (indexed is bool keep)
            {
                foreach (var key in s_indexes.Keys) if (ReferenceEquals(key.world, world)) s_indexes.TryRemove(key, out _);
                s_indexed[world] = keep;
            }
            Interlocked.Increment(ref s_epoch);
            var open = new WorldGenerator[s_open.Length + 1];
            s_open.CopyTo(open, 0);
            open[s_open.Length] = world;
            Volatile.Write(ref s_open, open);
        }
        return true;
    }

    /// <summary>The workers on <paramref name="world"/> that <see cref="Begin"/> opened for (it returned true) are done: once
    /// no run on it is left, the game's own method runs again. A caller whose Begin returned false opened nothing and does not
    /// call this: it would close another run's opening.</summary>
    internal static void End(WorldGenerator world)
    {
        lock (s_gate)
        {
            int at = System.Array.IndexOf(s_open, world);
            if (at < 0) return;
            var open = new WorldGenerator[s_open.Length - 1];
            for (int i = 0, k = 0; i < s_open.Length; i++)
                if (i != at) open[k++] = s_open[i];
            Volatile.Write(ref s_open, open);
            // The cells' indexes go with the last run: they hold the world's river points.
            if (open.Length == 0) { s_indexes.Clear(); s_indexed.Clear(); }
        }
    }

    /// <summary>Null where the game's GetRiverWeight and this read (<see cref="ReadAt"/>) give the same weight and width,
    /// to the bit, at each of the first <see cref="CheckedCells"/> river cells' first point and half its width beside it
    /// (<paramref name="compared"/> points); else the first difference. Asked before the world is opened, so the game's
    /// method runs unchanged.</summary>
    internal static string? SelfCheck(WorldGenerator world, out int compared)
    {
        compared = 0;
        int cells = 0;
        foreach (var cell in world.m_riverPoints)
        {
            if (cells++ >= CheckedCells) break;
            if (cell.Value == null || cell.Value.Length == 0) continue;
            WorldGenerator.RiverPoint first = cell.Value[0];
            for (int k = 0; k < 2; k++)
            {
                Vector2 at = first.p + new Vector2(k * first.w * 0.5f, 0f);
                world.GetRiverWeight(at.x, at.y, out float weight, out float width);
                ReadAt(world, at.x, at.y, out float read, out float readWidth);
                compared++;
                if (!weight.Equals(read) || !width.Equals(readWidth))
                    return FormattableString.Invariant($"at ({at.x:F1}, {at.y:F1}) the game gives weight {weight:R} width {width:R}, this read {read:R} {readWidth:R}");
            }
        }
        return null;
    }

    /// <summary>The weight and width at a point from the world's river points, as <see cref="TryRead"/> reads them, with no
    /// thread's last cell.</summary>
    private static void ReadAt(WorldGenerator world, float wx, float wy, out float weight, out float width)
    {
        if (world.m_riverPoints.TryGetValue(world.GetRiverGrid(wx, wy), out var points) && points != null)
            world.GetWeight(points, wx, wy, out weight, out width);
        else
        {
            weight = 0f;
            width = 0f;
        }
    }

    /// <summary>Whether each open world's reads go through the index: its own self-check passed (<see cref="IndexCheck"/>).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<WorldGenerator, bool> s_indexed = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(WorldGenerator world, Vector2i grid), CellIndex> s_indexes = new();

    /// <summary>The index of <paramref name="world"/>'s river cell <paramref name="grid"/> (its <paramref name="points"/>),
    /// made the first time any thread asks; two threads that make it make the same one.</summary>
    private static CellIndex IndexOf(WorldGenerator world, Vector2i grid, WorldGenerator.RiverPoint[] points)
    {
        var key = (world, grid);
        if (s_indexes.TryGetValue(key, out CellIndex? index) && ReferenceEquals(index.Points, points)) return index;
        index = new CellIndex(grid, points);
        s_indexes[key] = index;
        return index;
    }

    /// <summary>Null where the index (<see cref="CellIndex.Weight"/>) gives the game's GetWeight weight and width to the bit
    /// at every point of a lattice over the first river cells (2 m apart), and at each river point, its rim and half its
    /// width beside it, into <paramref name="compared"/>; else the first difference.</summary>
    internal static string? IndexCheck(WorldGenerator world, out int compared)
    {
        compared = 0;
        int cells = 0;
        foreach (var cell in world.m_riverPoints)
        {
            if (cells++ >= CheckedCells) break;
            WorldGenerator.RiverPoint[]? points = cell.Value;
            if (points == null || points.Length == 0) continue;
            var index = new CellIndex(cell.Key, points);
            float ox = cell.Key.x * 64f - 32f, oy = cell.Key.y * 64f - 32f;
            for (int i = 0; i <= 32; i++)
                for (int k = 0; k <= 32; k++)
                {
                    float x = ox + i * 2f, y = oy + k * 2f;
                    if (Differs(world, index, points, x, y, ref compared) is string d) return d;
                }
            foreach (var rp in points)
                for (int k = 0; k < 4; k++)
                {
                    float x = rp.p.x + (k == 1 ? rp.w : k == 2 ? -rp.w * 0.999f : 0f), y = rp.p.y + (k == 3 ? rp.w * 0.5f : 0f);
                    if (world.GetRiverGrid(x, y) != cell.Key) continue;
                    if (Differs(world, index, points, x, y, ref compared) is string d) return d;
                }
        }
        return null;
    }

    private static string? Differs(WorldGenerator world, CellIndex index, WorldGenerator.RiverPoint[] points, float x, float y, ref int compared)
    {
        world.GetWeight(points, x, y, out float weight, out float width);
        index.Weight(x, y, out float read, out float readWidth);
        compared++;
        return Bits(weight) == Bits(read) && Bits(width) == Bits(readWidth) ? null
            : FormattableString.Invariant($"at ({x:R}, {y:R}) the game gives weight {weight:R} width {width:R}, the index {read:R} {readWidth:R}");
    }

    private static int Bits(float f) => FloatBits.ToInt(f);

    /// <summary>
    /// One river cell's points (the game's, in its order) filed by the 8 m sub-cells of the cell their circles can reach (a
    /// point's radius and a metre more), so a read looks only at the points of its sub-cell, in their order in the cell,
    /// with the weight and width summed as the game sums them: every point whose circle holds the point asked is in its
    /// sub-cell's list, and the others add nothing. Self-checked against the game at each opening (<see cref="IndexCheck"/>).
    /// </summary>
    internal sealed class CellIndex
    {
        private const int Sub = 8;
        private const float SubSize = 64f / Sub;
        internal readonly WorldGenerator.RiverPoint[] Points;
        private readonly float m_ox, m_oy;
        private readonly int[] m_start;
        private readonly int[] m_points;

        internal CellIndex(Vector2i grid, WorldGenerator.RiverPoint[] points)
        {
            Points = points;
            m_ox = grid.x * 64f - 32f;
            m_oy = grid.y * 64f - 32f;
            var lists = new List<int>[Sub * Sub];
            for (int i = 0; i < lists.Length; i++) lists[i] = new List<int>();
            for (int n = 0; n < points.Length; n++)
            {
                float px = points[n].p.x, py = points[n].p.y, reach = points[n].w + 1f;
                for (int sy = 0; sy < Sub; sy++)
                    for (int sx = 0; sx < Sub; sx++)
                    {
                        float x0 = m_ox + sx * SubSize - 0.5f, x1 = x0 + SubSize + 1f;
                        float y0 = m_oy + sy * SubSize - 0.5f, y1 = y0 + SubSize + 1f;
                        float dx = px < x0 ? x0 - px : px > x1 ? px - x1 : 0f;
                        float dy = py < y0 ? y0 - py : py > y1 ? py - y1 : 0f;
                        if (dx * dx + dy * dy <= reach * reach) lists[sy * Sub + sx].Add(n);
                    }
            }
            m_start = new int[Sub * Sub + 1];
            int total = 0;
            for (int i = 0; i < lists.Length; i++) { m_start[i] = total; total += lists[i].Count; }
            m_start[lists.Length] = total;
            m_points = new int[total];
            for (int i = 0, at = 0; i < lists.Length; i++) foreach (int n in lists[i]) m_points[at++] = n;
        }

        /// <summary>The river weight and width at (<paramref name="wx"/>, <paramref name="wy"/>), a point of this cell.</summary>
        internal void Weight(float wx, float wy, out float weight, out float width)
        {
            int sx = (int)System.Math.Floor((wx - m_ox) / SubSize), sy = (int)System.Math.Floor((wy - m_oy) / SubSize);
            if (sx < 0) sx = 0; else if (sx >= Sub) sx = Sub - 1;
            if (sy < 0) sy = 0; else if (sy >= Sub) sy = Sub - 1;
            int slot = sy * Sub + sx;
            Vector2 at = new Vector2(wx, wy);
            weight = 0f;
            width = 0f;
            float widthSum = 0f, weightSum = 0f;
            for (int j = m_start[slot], end = m_start[slot + 1]; j < end; j++)
            {
                WorldGenerator.RiverPoint rp = Points[m_points[j]];
                float distSq = Vector2.SqrMagnitude(rp.p - at);
                if (!(distSq < rp.w2)) continue;
                float dist = (float)System.Math.Sqrt(distSq);
                float near = (float)(1.0 - (double)dist / (double)rp.w);
                if (near > weight) weight = near;
                widthSum = (float)((double)widthSum + (double)rp.w * (double)near);
                weightSum = (float)((double)weightSum + (double)near);
            }
            if (weightSum > 0f) width = (float)((double)widthSum / (double)weightSum);
        }
    }

    /// <summary>Whether <paramref name="world"/>'s reads go through the index now (for tests).</summary>
    internal static bool Indexed(WorldGenerator world) => IsOpen(world) && s_indexed.TryGetValue(world, out bool indexed) && indexed;

    private static bool IsOpen(WorldGenerator world)
    {
        WorldGenerator[] open = Volatile.Read(ref s_open);
        for (int i = 0; i < open.Length; i++)
            if (ReferenceEquals(open[i], world)) return true;
        return false;
    }

    /// <summary>
    /// The river weight and width at (<paramref name="wx"/>, <paramref name="wy"/>) of <paramref name="world"/>, as the game's
    /// GetRiverWeight gives them, where the workers on it run (true); else false, nothing read, for the game's own method.
    /// </summary>
    internal static bool TryRead(WorldGenerator world, float wx, float wy, out float weight, out float width)
    {
        if (!IsOpen(world))
        {
            weight = 0f;
            width = 0f;
            return false;
        }
        int epoch = Volatile.Read(ref s_epoch);
        Vector2i grid = world.GetRiverGrid(wx, wy);
        RiverScratch? cell = t_cell;
        if (cell == null || cell.Epoch != epoch || !ReferenceEquals(cell.World, world) || cell.Grid != grid)
        {
            if (cell == null || cell.Released) t_cell = cell = new RiverScratch();
            world.m_riverPoints.TryGetValue(grid, out cell.Points);
            cell.Index = cell.Points != null && s_indexed.TryGetValue(world, out bool indexed) && indexed ? IndexOf(world, grid, cell.Points) : null;
            cell.Grid = grid;
            cell.World = world;
            cell.Epoch = epoch;
        }
        if (cell.Index != null)
            cell.Index.Weight(wx, wy, out weight, out width);
        else if (cell.Points != null)
            world.GetWeight(cell.Points, wx, wy, out weight, out width);
        else
        {
            weight = 0f;
            width = 0f;
        }
        return true;
    }
}
