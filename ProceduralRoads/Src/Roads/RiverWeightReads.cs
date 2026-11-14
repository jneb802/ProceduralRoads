using System;
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
/// <para>Opened for one world by the generation's workers (<see cref="Begin"/>, RoadNetworkGenerator.GenerationWork.Compute)
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
        internal override void Clear()
        {
            World = null;
            Points = null;
        }
    }

    [System.ThreadStatic] private static RiverScratch? t_cell;

    /// <summary>The other mods that patch the game's GetRiverWeight (their Harmony ids), asked at each <see cref="Begin"/>:
    /// set by the patch when it is installed (Patches/WorldGenerator_Patch.cs); none where no patch is installed (tests,
    /// offline tools).</summary>
    internal static System.Func<string[]> PatchedByOthers = () => System.Array.Empty<string>();

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
        }
        lock (s_gate)
        {
            Interlocked.Increment(ref s_epoch);
            var open = new WorldGenerator[s_open.Length + 1];
            s_open.CopyTo(open, 0);
            open[s_open.Length] = world;
            Volatile.Write(ref s_open, open);
        }
        return true;
    }

    /// <summary>The workers on <paramref name="world"/> that <see cref="Begin"/> opened for are done: once no run on it is
    /// left, the game's own method runs again.</summary>
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
            cell.Grid = grid;
            cell.World = world;
            cell.Epoch = epoch;
        }
        if (cell.Points != null)
            world.GetWeight(cell.Points, wx, wy, out weight, out width);
        else
        {
            weight = 0f;
            width = 0f;
        }
        return true;
    }
}
