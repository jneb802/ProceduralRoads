// The blend itself: the natural ground is built here from the game's corner-biome heights (WorldGenerator.GetBiomeHeight). Its
// readers are banned outside it and NaturalGround.cs, the one door every other file calls (BannedSymbols.txt).
#pragma warning disable RS0030
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Provides biome-blended terrain height queries that match what Heightmap actually renders.
/// 
/// The problem: WorldGenerator.GetHeight() does NOT blend biomes at boundaries.
/// It just calls GetBiome(x,y) and returns that single biome's height.
/// But HeightmapBuilder (what actually renders terrain) blends heights from multiple
/// biomes across chunk boundaries using bilinear interpolation.
/// 
/// Result: At biome boundaries, roads using WorldGenerator.GetHeight see "phantom cliffs"
/// that don't exist in the rendered terrain.
/// 
/// HeightmapBuilder Logic:
/// - Heightmaps are 64m x 64m (m_width=32 vertices, m_scale=2 meters/vertex)
/// - Centered on zone positions (zones are 64m apart)
/// - Biomes sampled at heightmap corners (±32m from center)
/// - Heights blended using bilinear interpolation with SmoothStep
/// 
/// This utility replicates that blending logic.
/// </summary>
public static class BiomeBlendedHeight
{
    // Heightmap dimensions matching Valheim's Heightmap class
    // IMPORTANT: m_width=32 vertices, but m_scale=2 in the actual game prefab!
    // This makes heightmaps 64m wide to match zone spacing (64m between zone centers).
    // The default m_scale=1 in decompiled code is overridden by Unity prefab serialization.
    private const int HeightmapWidth = 32;       // m_width (vertex count)
    private const float HeightmapScale = 2f;     // m_scale (actual game uses 2, not 1!)
    private const float HeightmapSize = HeightmapWidth * HeightmapScale;  // 64m total
    private const float HeightmapHalfSize = HeightmapSize / 2f;           // 32m from center
    
    // Zone size for calculating heightmap centers
    private const float ZoneSize = RoadConstants.ZoneSize;
    
    /// <summary>The zone index along one axis, as a float: the heightmap that contains <paramref name="w"/>.</summary>
    private static float ZoneFloor(float w) => Mathf.Floor((w + HeightmapHalfSize) / ZoneSize);

    /// <summary>
    /// Get terrain height with biome blending that matches rendered terrain.
    /// This replicates HeightmapBuilder.Build()'s biome blending logic exactly.
    /// The one owner of ground readers (docs/OWNERS.md).
    /// </summary>
    public static float GetBlendedHeight(float wx, float wy, WorldGenerator worldGen) => GetBlendedHeight(wx, wy, worldGen, null);

    /// <summary>
    /// <see cref="GetBlendedHeight(float, float, WorldGenerator)"/>, the heightmap corners' biomes read through the
    /// caller's <paramref name="corners"/> (null: read from the world each time). The same heights to the bit: a corner's
    /// biome is a fact of the world alone, and the corners are the same floats either way.
    /// </summary>
    public static float GetBlendedHeight(float wx, float wy, WorldGenerator worldGen, Corners? corners)
    {
        if (worldGen == null)
            return 0f;
        // While a generation's workers run in this world: a point this thread read before, the height it read, to the bit (Heights).
        HeightsWindow? window = System.Threading.Volatile.Read(ref s_heightsWindow);
        if (window == null || !ReferenceEquals(window.World, worldGen)) return Blend(wx, wy, worldGen, corners);
        Heights? memo = t_heights;
        if (memo == null || memo.Released) t_heights = memo = new Heights();
        return memo.Get(wx, wy, worldGen, corners);
    }

    [System.ThreadStatic] private static Heights? t_heights;
    /// <summary>The window the threads keep their heights in (<see cref="BeginHeights"/>), none outside a generation's workers.</summary>
    private static HeightsWindow? s_heightsWindow;
    /// <summary>Counts the openings and closings: a thread's memo from before the last is forgotten.</summary>
    private static int s_heightsEpoch;

    /// <summary>One opening of the height memo (<see cref="BeginHeights"/>): its world, or none where LowMemoryGeneration keeps
    /// no heights. Only the run that opened it closes it (<see cref="EndHeights"/>).</summary>
    internal sealed class HeightsWindow
    {
        internal readonly WorldGenerator? World;
        internal HeightsWindow(WorldGenerator? world) { World = world; }
    }

    /// <summary>A generation's workers start building in <paramref name="world"/> (RoadNetworkGenerator.GenerationWork.Compute,
    /// beside RiverWeightReads): from now on each thread keeps the heights it reads there (<see cref="Heights"/>), read afresh.
    /// Kept where the generation spends memory for speed (GenerationMemory.KeepsHeights). The window this opened, for
    /// <see cref="EndHeights"/>: it replaces any window open now (a cancelled run still ending).</summary>
    internal static HeightsWindow BeginHeights(WorldGenerator world)
    {
        var window = new HeightsWindow(GenerationMemory.KeepsHeights ? world : null);
        System.Threading.Interlocked.Increment(ref s_heightsEpoch);
        System.Threading.Volatile.Write(ref s_heightsWindow, window);
        return window;
    }

    /// <summary>The workers that opened <paramref name="opened"/> have ended: if it is still the open window, heights are read
    /// from the world again and each thread's memo is forgotten at its next read. A window another run opened since (a
    /// cancelled run that ends after the next one starts) is left open: a run closes only what it opened.</summary>
    internal static void EndHeights(HeightsWindow? opened)
    {
        if (opened == null) return;
        if (!ReferenceEquals(System.Threading.Interlocked.CompareExchange(ref s_heightsWindow, null, opened), opened)) return;
        System.Threading.Interlocked.Increment(ref s_heightsEpoch);
    }

    /// <summary>
    /// A thread's memo of the heights it read (<see cref="GetBlendedHeight(float, float, WorldGenerator, Corners)"/>), by the
    /// exact point: a height is a fact of the world and the point alone (the corners' memo holds only facts of the world), so
    /// a point asked again is answered with the height the world gave it, to the bit. Direct-mapped, <see cref="Slots"/>
    /// slots (about 768 KB a thread), a point replacing the one before it in its slot. Used only while a generation's workers
    /// run in that world (<see cref="BeginHeights"/>, <see cref="EndHeights"/>), in which the world does not change; forgotten
    /// at each opening and closing.
    /// </summary>
    private sealed class Heights : RoadNetworkGenerator.GenerationScratch
    {
        internal const int Slots = 1 << 16;
        /// <summary>The key of an empty slot. It is also the key of one point (both coordinates the NaN with bits 0xFFC00001):
        /// that point is never kept, so it is read from the world each time and an empty slot never answers.</summary>
        private const long Empty = unchecked((long)0xFFC00001FFC00001UL);
        private long[] m_keys = new long[Slots];
        private float[] m_heights = new float[Slots];
        private int m_epoch = -1;
        private WorldGenerator? m_world;

        /// <summary>Let go with the generation's other per-thread scratch (RoadNetworkGenerator.ReleaseGenerationResources).</summary>
        internal override void Clear() { m_keys = System.Array.Empty<long>(); m_heights = System.Array.Empty<float>(); m_world = null; }

        internal float Get(float wx, float wy, WorldGenerator world, Corners? corners)
        {
            // Each array read once: a Clear (the generation's release) between two reads cannot leave this read half on the
            // empty arrays.
            long[] keys = m_keys;
            float[] heights = m_heights;
            if (keys.Length != Slots || heights.Length != Slots) return Blend(wx, wy, world, corners);
            int epoch = System.Threading.Volatile.Read(ref s_heightsEpoch);
            if (epoch != m_epoch || !ReferenceEquals(world, m_world))
            {
                for (int i = 0; i < Slots; i++) keys[i] = Empty;
                m_epoch = epoch;
                m_world = world;
            }
            int bx = FloatBits.ToInt(wx), by = FloatBits.ToInt(wy);
            long key = ((long)bx << 32) | (uint)by;
            int slot = (int)((uint)(bx * -1640531535 ^ by * 668265263) >> 16);
            if (keys[slot] == key && key != Empty) return heights[slot];
            float height = Blend(wx, wy, world, corners);
            if (key != Empty) { keys[slot] = key; heights[slot] = height; }
            return height;
        }
    }

    /// <summary>The height <see cref="GetBlendedHeight(float, float, WorldGenerator, Corners)"/> gives, read from the world.</summary>
    private static float Blend(float wx, float wy, WorldGenerator worldGen, Corners? corners)
    {
        Ground ground = GroundAt(wx, wy, worldGen, corners);

        // If all corners have the same biome, no blending needed
        // (This is the fast path that HeightmapBuilder uses)
        //
        // The game builds the WHOLE heightmap from the corners' biome here,
        // not from the biome at the point: HeightmapBuilder.Build calls
        // GetBiomeHeight(cornerBiome, wx, wy). WorldGenerator.GetHeight asks
        // GetBiome(wx, wy) instead, and the two differ wherever a biome
        // narrower than a zone lies between four corners of another - the
        // tip of a Black Forest tongue inside a Meadows zone, a knoll that
        // reads Mountain, an inlet that reads Ocean. A road across such a
        // patch was levelled to the patch's formula - a Black Forest height
        // metres above the Meadows ground the game actually rendered there.
        if (ground.OneBiome)
            return worldGen.GetBiomeHeight(ground.B00, wx, wy, out _);

        // Get heights for each corner biome at THIS point (not at corners)
        // This is exactly what HeightmapBuilder does
        // A biome two corners share gives the same height at this point: asked once (GetBiomeHeight is a fact of the world,
        // the biome and the point).
        Color mask;  // Unused, but required by API
        Heightmap.Biome biome00 = ground.B00, biome10 = ground.B10, biome01 = ground.B01, biome11 = ground.B11;
        float h00 = worldGen.GetBiomeHeight(biome00, wx, wy, out mask);
        float h10 = biome10 == biome00 ? h00 : worldGen.GetBiomeHeight(biome10, wx, wy, out mask);
        float h01 = biome01 == biome00 ? h00 : biome01 == biome10 ? h10 : worldGen.GetBiomeHeight(biome01, wx, wy, out mask);
        float h11 = biome11 == biome00 ? h00 : biome11 == biome10 ? h10 : biome11 == biome01 ? h01 : worldGen.GetBiomeHeight(biome11, wx, wy, out mask);
        return ground.Mix(h00, h10, h01, h11);
    }

    /// <summary>
    /// The biomes the game builds a point's ground from, and how much of each: the four corners of the heightmap the point is
    /// in, weighted as HeightmapBuilder.Build blends their heights (bilinear, SmoothStep over the heightmap). The one reading
    /// of those weights: the blended height (<see cref="GetBlendedHeight(float, float, WorldGenerator, Corners)"/>) and any
    /// biome rule value that must follow the ground's own blend across a border (<see cref="Of"/>, asked through
    /// RoadBiome.GroundAt).
    /// </summary>
    internal readonly struct Ground
    {
        internal readonly Heightmap.Biome B00, B10, B01, B11;
        /// <summary>The SmoothStep weights toward the far corners, along x and along z.</summary>
        internal readonly float Tx, Tz;
        internal Ground(Heightmap.Biome b00, Heightmap.Biome b10, Heightmap.Biome b01, Heightmap.Biome b11, float tx, float tz)
        { B00 = b00; B10 = b10; B01 = b01; B11 = b11; Tx = tx; Tz = tz; }

        /// <summary>Whether all four corners are one biome (HeightmapBuilder's fast path: no blend).</summary>
        internal bool OneBiome => B00 == B10 && B00 == B01 && B00 == B11;

        /// <summary>Four corner values mixed as the game mixes the corner heights (HeightmapBuilder.Build:
        /// Lerp(Lerp(h00, h10, tx), Lerp(h01, h11, tx), tz)).</summary>
        internal float Mix(float v00, float v10, float v01, float v11)
        {
            float bottom = Mathf.Lerp(v00, v10, Tx);
            float top = Mathf.Lerp(v01, v11, Tx);
            return Mathf.Lerp(bottom, top, Tz);
        }

        /// <summary>A per-biome value as the ground carries it here: the corner biomes' values mixed by the corners' weights;
        /// one biome's own value where all four corners are that biome.</summary>
        internal float Of(System.Func<Heightmap.Biome, float> value)
        {
            float v00 = value(B00);
            if (OneBiome) return v00;
            return Mix(v00, B10 == B00 ? v00 : value(B10), B01 == B00 ? v00 : value(B01), B11 == B00 ? v00 : value(B11));
        }
    }

    /// <summary>The <see cref="Ground"/> at (<paramref name="wx"/>, <paramref name="wy"/>): the corners' biomes read through
    /// <paramref name="corners"/> (null: the open <see cref="CornerGrid"/>, else the world).</summary>
    internal static Ground GroundAt(float wx, float wy, WorldGenerator worldGen, Corners? corners)
    {
        // Find which zone/heightmap contains this point
        // Zones are at 64m intervals (0, 64, 128, -64, etc.)
        // Each heightmap is 64m wide centered on zone position (covers ±32m from center)
        // So zone at x=64 has heightmap covering [32, 96]
        //
        // To find which zone contains point wx:
        // - Shift by half heightmap size (32m) so the floor division aligns with heightmap boundaries
        // - Floor divide by zone size
        float zoneX = ZoneFloor(wx) * ZoneSize;
        float zoneZ = ZoneFloor(wy) * ZoneSize;

        // HeightmapBuilder calculates corner from center:
        // corner = center + new Vector3(m_width * m_scale * -0.5f, 0, m_width * m_scale * -0.5f)
        // With m_width=32 and m_scale=2, this is center + (-32, 0, -32)
        float cornerX = zoneX - HeightmapHalfSize;
        float cornerZ = zoneZ - HeightmapHalfSize;

        // Biome sampling points (at the 4 corners of the heightmap)
        float x0 = cornerX;
        float x1 = cornerX + HeightmapSize;
        float z0 = cornerZ;
        float z1 = cornerZ + HeightmapSize;

        // Get biomes at the four corners
        Heightmap.Biome biome00, biome10, biome01, biome11;
        int ix = (int)ZoneFloor(wx);
        int iz = (int)ZoneFloor(wy);
        if (corners != null)
        {
            biome00 = corners.Get(worldGen, ix, iz, x0, z0);
            biome10 = corners.Get(worldGen, ix + 1, iz, x1, z0);
            biome01 = corners.Get(worldGen, ix, iz + 1, x0, z1);
            biome11 = corners.Get(worldGen, ix + 1, iz + 1, x1, z1);
        }
        else
        {
            // The world's corner biomes, each read from the generator once (CornerGrid): the same biomes as asking it here.
            CornerGrid? grid = CornerGrid.For(worldGen);
            biome00 = grid != null ? grid.Get(worldGen, ix, iz, x0, z0) : worldGen.GetBiome(x0, z0);  // bottom-left
            biome10 = grid != null ? grid.Get(worldGen, ix + 1, iz, x1, z0) : worldGen.GetBiome(x1, z0);  // bottom-right
            biome01 = grid != null ? grid.Get(worldGen, ix, iz + 1, x0, z1) : worldGen.GetBiome(x0, z1);  // top-left
            biome11 = grid != null ? grid.Get(worldGen, ix + 1, iz + 1, x1, z1) : worldGen.GetBiome(x1, z1);  // top-right
        }

        // Calculate position within the heightmap (0 to m_width)
        float localX = (wx - cornerX) / HeightmapScale;
        float localZ = (wy - cornerZ) / HeightmapScale;

        // Normalize to 0-1 range and apply SmoothStep (matches HeightmapBuilder)
        // HeightmapBuilder: float tx = DUtils.SmoothStep(0f, 1f, l / data.m_width);
        float tx = SmoothStep(localX / HeightmapWidth);
        float tz = SmoothStep(localZ / HeightmapWidth);
        return new Ground(biome00, biome10, biome01, biome11, tx, tz);
    }

    /// <summary>
    /// A caller-owned memo of heightmap corner biomes for <see cref="GetBlendedHeight(float, float, WorldGenerator, Corners)"/>:
    /// every sample reads four corner biomes, and the samples of one heightmap (and its neighbours, which share corners)
    /// read the same ones. Direct-mapped on the corner lattice: <c>columns</c> by <c>rows</c> slots, a corner at
    /// (ix mod columns, iz mod rows), replaced by the next corner landing there. Two by two holds every corner of the last
    /// heightmap; a scan down columns of the world wants two columns and as many rows as a column has corners. Not
    /// thread-safe: one owner, one thread. It forgets everything when asked about another world generator.
    /// </summary>
    public sealed class Corners
    {
        private readonly int m_columns, m_rows;
        private readonly int[] m_x, m_z;
        private readonly Heightmap.Biome[] m_biome;
        private readonly bool[] m_has;
        private WorldGenerator? m_world;

        public Corners(int columns = 2, int rows = 2)
        {
            if (columns < 1 || rows < 1) throw new System.ArgumentOutOfRangeException(columns < 1 ? nameof(columns) : nameof(rows));
            m_columns = columns;
            m_rows = rows;
            int n = columns * rows;
            m_x = new int[n];
            m_z = new int[n];
            m_biome = new Heightmap.Biome[n];
            m_has = new bool[n];
        }

        /// <summary>A memo for a scan down whole columns of a square of <paramref name="radius"/> round the world centre:
        /// two columns of corners, and every corner of one column.</summary>
        public static Corners ForColumnScan(float radius) => new(2, Mathf.CeilToInt(radius * 2f / ZoneSize) + 2);

        /// <summary>Corner biomes read from the world generator, and corner biomes the memo answered (for measuring it).</summary>
        public long Reads, Hits;

        /// <summary>The biome of the corner at lattice (<paramref name="ix"/>, <paramref name="iz"/>), which stands at
        /// (<paramref name="x"/>, <paramref name="z"/>): from the memo, else read from the world and kept.</summary>
        internal Heightmap.Biome Get(WorldGenerator world, int ix, int iz, float x, float z)
        {
            if (!ReferenceEquals(world, m_world))
            {
                System.Array.Clear(m_has, 0, m_has.Length);
                m_world = world;
            }
            int cx = ix % m_columns, cz = iz % m_rows;
            if (cx < 0) cx += m_columns;
            if (cz < 0) cz += m_rows;
            int slot = cz * m_columns + cx;
            if (m_has[slot] && m_x[slot] == ix && m_z[slot] == iz)
            {
                Hits++;
                return m_biome[slot];
            }
            Reads++;
            Heightmap.Biome biome = world.GetBiome(x, z);
            m_x[slot] = ix;
            m_z[slot] = iz;
            m_biome[slot] = biome;
            m_has[slot] = true;
            return biome;
        }
    }

    /// <summary>
    /// Every heightmap corner's biome of the world a generation's workers build in, each read from the world generator once
    /// and shared by every thread while they run (opened by <see cref="Begin"/> and closed by <see cref="End"/>,
    /// RoadNetworkGenerator.GenerationWork.Compute, beside RiverWeightReads): a corner's biome is a fact of the world alone
    /// (WorldGenerator.GetBiome at the corner), which does not change while the workers run, so a corner read here is the
    /// biome the generator gives there, to the bit. Outside that, and for any other world, the corners are read from the
    /// generator each time, as they always were. A flat array over the corner lattice of the world and a margin
    /// (<see cref="Reach"/> corners each way from the centre, about 1.3 MB, let go at <see cref="End"/>); a corner outside it
    /// is read from the generator each time. A slot is written whole (one int) before anyone reads it as known; two threads
    /// that read the same corner write the same value. Kept where the generation spends memory for speed
    /// (GenerationMemory.KeepsCornerBiomes).
    /// </summary>
    internal sealed class CornerGrid
    {
        /// <summary>Corners each way from the world centre: the world's 10,500 m radius and Valheim's edge, with a margin.</summary>
        internal const int Reach = 288;
        private const int Side = 2 * Reach + 1;
        private readonly WorldGenerator m_world;
        private readonly int[] m_biome = new int[Side * Side];
        private static CornerGrid? s_grid;
        private CornerGrid(WorldGenerator world) { m_world = world; }

        /// <summary>A generation's workers start building in <paramref name="world"/>: its corners are held from now on, read
        /// afresh. The grid this opened, for <see cref="End"/> (it replaces any grid open now: a cancelled run still ending);
        /// none where LowMemoryGeneration holds no grid.</summary>
        internal static CornerGrid? Begin(WorldGenerator world)
        {
            if (world == null || !GenerationMemory.KeepsCornerBiomes) return null;
            var grid = new CornerGrid(world);
            System.Threading.Volatile.Write(ref s_grid, grid);
            return grid;
        }

        /// <summary>The workers that opened <paramref name="opened"/> have ended: if it is still the open grid, the corners are
        /// read from the generator again and the grid let go. A grid another run opened since is left open: a run closes only
        /// what it opened.</summary>
        internal static void End(CornerGrid? opened)
        {
            if (opened != null) System.Threading.Interlocked.CompareExchange(ref s_grid, null, opened);
        }

        /// <summary>The open grid of <paramref name="world"/>, or none.</summary>
        internal static CornerGrid? For(WorldGenerator world)
        {
            CornerGrid? grid = System.Threading.Volatile.Read(ref s_grid);
            return grid != null && ReferenceEquals(grid.m_world, world) ? grid : null;
        }

        /// <summary>The biome of the corner at lattice (<paramref name="ix"/>, <paramref name="iz"/>), which stands at
        /// (<paramref name="x"/>, <paramref name="z"/>).</summary>
        internal Heightmap.Biome Get(WorldGenerator world, int ix, int iz, float x, float z)
        {
            int gx = ix + Reach, gz = iz + Reach;
            if ((uint)gx >= Side || (uint)gz >= Side) return world.GetBiome(x, z);
            int slot = gz * Side + gx;
            int known = System.Threading.Volatile.Read(ref m_biome[slot]);
            if (known != 0) return (Heightmap.Biome)((uint)known >> 1);
            Heightmap.Biome biome = world.GetBiome(x, z);
            // Kept only where the value survives the shift (bit 31 clear): a mod's biome with the top bit set would read back
            // as another, so it is read from the generator each time.
            if ((int)biome >= 0) System.Threading.Volatile.Write(ref m_biome[slot], ((int)biome << 1) | 1);
            return biome;
        }
    }

    /// <summary>
    /// Smooth step function matching Valheim's DUtils.SmoothStep(0, 1, t)
    /// Standard smoothstep: 3t² - 2t³
    /// </summary>
    private static float SmoothStep(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }
    
    /// <summary>
    /// Get detailed debug info about height calculations at a point.
    /// Returns a struct with all the intermediate values for diagnosis.
    /// </summary>
    public static BlendDebugInfo GetBlendDebugInfo(float wx, float wy, WorldGenerator worldGen)
    {
        var info = new BlendDebugInfo();
        
        if (worldGen == null)
            return info;
        
        // Find containing heightmap using same logic as GetBlendedHeight
        float zoneX = ZoneFloor(wx) * ZoneSize;
        float zoneZ = ZoneFloor(wy) * ZoneSize;
        float cornerX = zoneX - HeightmapHalfSize;
        float cornerZ = zoneZ - HeightmapHalfSize;
        
        info.ZoneCenter = new Vector2(zoneX, zoneZ);
        info.HeightmapCorner = new Vector2(cornerX, cornerZ);
        
        // Get corner biomes
        info.Biome00 = worldGen.GetBiome(cornerX, cornerZ);
        info.Biome10 = worldGen.GetBiome(cornerX + HeightmapSize, cornerZ);
        info.Biome01 = worldGen.GetBiome(cornerX, cornerZ + HeightmapSize);
        info.Biome11 = worldGen.GetBiome(cornerX + HeightmapSize, cornerZ + HeightmapSize);
        
        info.IsBiomeBoundary = !(info.Biome00 == info.Biome10 && 
                                  info.Biome00 == info.Biome01 && 
                                  info.Biome00 == info.Biome11);
        
        // Calculate interpolation parameters
        float localX = (wx - cornerX) / HeightmapScale;
        float localZ = (wy - cornerZ) / HeightmapScale;
        info.LocalPosition = new Vector2(localX, localZ);
        info.Tx = SmoothStep(localX / HeightmapWidth);
        info.Tz = SmoothStep(localZ / HeightmapWidth);
        
        // Get heights
        Color mask;
        info.Height00 = worldGen.GetBiomeHeight(info.Biome00, wx, wy, out mask);
        info.Height10 = worldGen.GetBiomeHeight(info.Biome10, wx, wy, out mask);
        info.Height01 = worldGen.GetBiomeHeight(info.Biome01, wx, wy, out mask);
        info.Height11 = worldGen.GetBiomeHeight(info.Biome11, wx, wy, out mask);
        
        info.PointBiome = worldGen.GetBiome(wx, wy);
        info.RawHeight = worldGen.GetHeight(wx, wy);
        info.BlendedHeight = GetBlendedHeight(wx, wy, worldGen);
        info.HeightDifference = info.BlendedHeight - info.RawHeight;
        
        return info;
    }
    
    public struct BlendDebugInfo
    {
        public Vector2 ZoneCenter;
        public Vector2 HeightmapCorner;
        public Vector2 LocalPosition;
        public float Tx, Tz;
        
        public Heightmap.Biome Biome00, Biome10, Biome01, Biome11;
        /// <summary>The biome WorldGenerator.GetHeight uses at the point. When
        /// it is not the corners' shared biome, the raw height is a formula
        /// the rendered terrain does not use here.</summary>
        public Heightmap.Biome PointBiome;
        public bool IsBiomeBoundary;
        
        public float Height00, Height10, Height01, Height11;
        public float RawHeight;
        public float BlendedHeight;
        public float HeightDifference;
    }
}
