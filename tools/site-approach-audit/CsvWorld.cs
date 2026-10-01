using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads.Tests;

/// <summary>
/// A world read back from a dump of the real one, so road generation can run
/// offline on real terrain.
///
/// What it reproduces exactly, and what it does not, decides what a run on it
/// may claim:
///
/// * At a sample position it returns the sampled value, unchanged. A dump
///   aligned to the 8 m pathfinding grid is therefore exact for every move
///   cost, and a dump on the 128 m base-height grid is exact for island
///   detection.
/// * Off a sample position it interpolates bilinearly between the four
///   surrounding samples. The pathfinder's terrain-variance ring and every
///   crossing-depth judgement sample off-grid, so those are APPROXIMATE.
/// * Asked for a biome's height field at a point in another biome - what
///   biome blending needs - it can only answer with the height that was
///   dumped. Blending across a biome edge is therefore APPROXIMATE.
/// * Outside the dumped area it throws. A world model that guesses beyond its
///   data would turn a missing dump into a quiet wrong answer.
///
/// Finer tiles win: load the 128 m map of a world and an 8 m window over one
/// island, and queries inside the window are answered from it. Base height is
/// the exception - island detection reads it on its own 128 m lattice, where a
/// finer dump would only interpolate - so the dump of that lattice is loaded
/// with <see cref="LoadIslandGrid"/> and answers base height alone.
/// </summary>
public sealed class CsvWorld : WorldGenerator
{
    public long HeightQueries { get; private set; }
    public long BiomeQueries { get; private set; }
    /// <summary>One dumped grid: evenly spaced samples over a rectangle.</summary>
    private sealed class Layer
    {
        public string Source = "";
        public float Step;
        public float X0, Z0;
        public int Nx, Nz;
        public float[] Height = Array.Empty<float>();
        public float[] River = Array.Empty<float>();
        public float[] RiverWidth = Array.Empty<float>();
        public float[] BaseHeight = Array.Empty<float>();
        public Heightmap.Biome[] Biome = Array.Empty<Heightmap.Biome>();
        public bool HasBaseHeight;
        public bool HasRiverWidth;
        /// <summary>Loaded as the island grid: it answers base height.</summary>
        public bool IsIslandGrid;

        public float X1 => X0 + (Nx - 1) * Step;
        public float Z1 => Z0 + (Nz - 1) * Step;

        public bool Contains(float x, float z) => x >= X0 && x <= X1 && z >= Z0 && z <= Z1;

        public int Index(int ix, int iz) => iz * Nx + ix;
    }

    /// <summary>
    /// The world's own radius. Valheim generates land only inside it; beyond it
    /// there is open ocean, and the island detector does not even sample there.
    /// </summary>
    public const float WorldRadius = 10000f;

    private readonly List<Layer> m_layers = new();
    private readonly int m_seed;

    /// <summary>
    /// Queries answered from the dump's rim because they fell outside the world
    /// itself. The pathfinder walks 8 m cells and can step a cell past the rim;
    /// there is only ocean there, so the rim sample is the answer rather than a
    /// guess. Counted so a run can say how often it happened - a large number
    /// would mean a road was being planned off the edge of the world.
    /// </summary>
    public int OutsideWorldQueries { get; private set; }

    public CsvWorld(int seed = 0) => m_seed = seed;

    /// <summary>What this world cannot reproduce from its dumps. Belongs in the
    /// manifest of any run that uses it.</summary>
    public IReadOnlyList<string> Approximations
    {
        get
        {
            List<string> notes = new()
            {
                "off-grid queries are bilinear between samples (terrain variance ring, crossing depth)",
                "biome blending uses the dumped height for every biome",
            };
            foreach (Layer layer in m_layers)
            {
                // File name only: these notes are copied into run manifests
                // and study documents, which carry no local paths.
                string name = Path.GetFileName(layer.Source);
                if (!layer.HasRiverWidth)
                    notes.Add($"{name}: no river_width column, width reported as 0");
                if (!layer.HasBaseHeight)
                    notes.Add($"{name}: no base_height column, island detection unavailable");
            }
            return notes;
        }
    }

    public string Describe()
    {
        List<string> parts = new();
        foreach (Layer layer in m_layers)
            parts.Add($"{Path.GetFileName(layer.Source)} step {layer.Step:F0} m, " +
                      $"{layer.Nx}x{layer.Nz} samples, [{layer.X0:F0},{layer.Z0:F0}..{layer.X1:F0},{layer.Z1:F0}]");
        return string.Join("; ", parts);
    }

    /// <summary>
    /// Adds the dump of the 128 m lattice island detection reads. It answers
    /// base height for the whole world; heights, biomes and rivers still come
    /// from the finest layer covering the point.
    /// </summary>
    public CsvWorld LoadIslandGrid(string path)
    {
        Load(path);
        Layer loaded = m_layers.First(layer => layer.Source == path);
        if (!loaded.HasBaseHeight)
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} has no base_height column, so it cannot serve as the island grid");
        loaded.IsIslandGrid = true;
        return this;
    }

    /// <summary>
    /// Adds a dump. Columns: x, z, height, biome, river, and optionally
    /// base_height and river_width. Samples must lie on one even grid.
    /// </summary>
    public CsvWorld Load(string path)
    {
        // Two passes, on purpose: an 8 m dump of a whole world is over six
        // million samples, and holding them in a dictionary before laying them
        // out would cost several times what the finished grid costs. The first
        // pass learns the grid's shape, the second fills it in place.
        (int cx, int cz, int ch, int cb, int cr, int cbh, int crw) = Columns(path);

        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        float secondX = float.MaxValue, secondZ = float.MaxValue;
        long rows = 0;

        foreach (string[] cells in Rows(path))
        {
            float x = Parse(cells[cx]), z = Parse(cells[cz]);
            if (x < minX) { secondX = minX; minX = x; }
            else if (x > minX && x < secondX) secondX = x;
            if (z < minZ) { secondZ = minZ; minZ = z; }
            else if (z > minZ && z < secondZ) secondZ = z;
            if (x > maxX) maxX = x;
            if (z > maxZ) maxZ = z;
            rows++;
        }

        if (rows == 0 || secondX == float.MaxValue || secondZ == float.MaxValue)
            throw new InvalidDataException($"{path}: a grid needs at least two rows and two columns");

        float stepX = secondX - minX, stepZ = secondZ - minZ;
        if (Mathf.Abs(stepX - stepZ) > stepX * 0.001f)
            throw new InvalidDataException($"{path}: x and z steps differ ({stepX} and {stepZ})");

        Layer layer = new()
        {
            Source = path,
            Step = stepX,
            X0 = minX,
            Z0 = minZ,
            Nx = Mathf.RoundToInt((maxX - minX) / stepX) + 1,
            Nz = Mathf.RoundToInt((maxZ - minZ) / stepZ) + 1,
            HasBaseHeight = cbh >= 0,
            HasRiverWidth = crw >= 0,
        };

        if ((long)layer.Nx * layer.Nz != rows)
            throw new InvalidDataException(
                $"{path}: {rows} samples do not fill a {layer.Nx}x{layer.Nz} grid; the dump is not a full grid");

        int n = layer.Nx * layer.Nz;
        layer.Height = new float[n];
        layer.River = new float[n];
        layer.RiverWidth = new float[n];
        layer.BaseHeight = new float[n];
        layer.Biome = new Heightmap.Biome[n];
        bool[] seen = new bool[n];

        foreach (string[] cells in Rows(path))
        {
            float x = Parse(cells[cx]), z = Parse(cells[cz]);
            int ix = Index(x, layer.X0, layer.Step, layer.Nx, path, "x");
            int iz = Index(z, layer.Z0, layer.Step, layer.Nz, path, "z");
            int i = layer.Index(ix, iz);
            if (seen[i])
                throw new InvalidDataException($"{path}: two samples at ({x},{z})");
            seen[i] = true;
            layer.Height[i] = Parse(cells[ch]);
            layer.Biome[i] = ParseBiome(cells[cb], path);
            layer.River[i] = Parse(cells[cr]);
            if (crw >= 0) layer.RiverWidth[i] = Parse(cells[crw]);
            if (cbh >= 0) layer.BaseHeight[i] = Parse(cells[cbh]);
        }

        m_layers.Add(layer);
        // Finest first: a window over an island answers before the world map.
        m_layers.Sort((a, b) => a.Step.CompareTo(b.Step));
        return this;
    }

    /// <summary>The index of a coordinate on the grid, or an error naming it.</summary>
    private static int Index(float value, float origin, float step, int count, string path, string axis)
    {
        float exact = (value - origin) / step;
        int index = Mathf.RoundToInt(exact);
        if (Mathf.Abs(exact - index) > 0.001f)
            throw new InvalidDataException(
                $"{path}: {axis} sample {value} is not on the grid (origin {origin}, step {step})");
        if (index < 0 || index >= count)
            throw new InvalidDataException($"{path}: {axis} sample {value} is outside the grid");
        return index;
    }

    private static (int x, int z, int height, int biome, int river, int baseHeight, int riverWidth) Columns(string path)
    {
        using StreamReader reader = new(path);
        string? header = reader.ReadLine()
            ?? throw new InvalidDataException($"{path}: empty dump");
        string[] columns = header.Split(',');

        int Column(string name)
        {
            for (int i = 0; i < columns.Length; i++)
                if (columns[i].Trim() == name) return i;
            return -1;
        }

        int cx = Column("x"), cz = Column("z"), ch = Column("height");
        int cb = Column("biome"), cr = Column("river");
        if (cx < 0 || cz < 0 || ch < 0 || cb < 0 || cr < 0)
            throw new InvalidDataException($"{path}: needs at least x,z,height,biome,river columns, has '{header}'");
        return (cx, cz, ch, cb, cr, Column("base_height"), Column("river_width"));
    }

    private static IEnumerable<string[]> Rows(string path)
    {
        using StreamReader reader = new(path);
        reader.ReadLine();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            yield return line.Split(',');
        }
    }

    private static void EvenlySpaced(float[] values, float step, string path, string axis)
    {
        for (int i = 1; i < values.Length; i++)
        {
            float delta = values[i] - values[i - 1];
            if (Mathf.Abs(delta - step) > step * 0.001f)
                throw new InvalidDataException(
                    $"{path}: {axis} samples are not evenly spaced ({values[i - 1]} to {values[i]} is {delta}, expected {step})");
        }
    }

    private static float Parse(string cell) =>
        float.Parse(cell.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static Heightmap.Biome ParseBiome(string cell, string path)
    {
        string name = cell.Trim();
        if (Enum.TryParse(name, out Heightmap.Biome biome))
            return biome;
        throw new InvalidDataException($"{path}: unknown biome '{name}'");
    }

    private Layer LayerAt(float x, float z)
    {
        foreach (Layer layer in m_layers)
            if (layer.Contains(x, z))
                return layer;

        // Outside the world, the dump's rim is the honest answer: the game has
        // only ocean out there. Inside the world a missing sample is a missing
        // dump, and answering it would turn that into a quiet wrong result.
        if (Mathf.Sqrt(x * x + z * z) > WorldRadius && m_layers.Count > 0)
        {
            OutsideWorldQueries++;
            return m_layers[m_layers.Count - 1];
        }

        throw new InvalidOperationException(
            $"({x:F1},{z:F1}) is outside every dump loaded ({Describe()}). " +
            "A world read from a dump does not guess beyond its data.");
    }

    /// <summary>The island grid covering a point, if one was loaded.</summary>
    private Layer? IslandGridAt(float x, float z)
    {
        foreach (Layer layer in m_layers)
            if (layer.IsIslandGrid && layer.Contains(x, z))
                return layer;
        return null;
    }

    /// <summary>Whether a point can be answered at all.</summary>
    public bool Covers(float x, float z)
    {
        foreach (Layer layer in m_layers)
            if (layer.Contains(x, z))
                return true;
        return false;
    }

    /// <summary>
    /// Bilinear between the four surrounding samples; exactly the sample when
    /// the point is one.
    /// </summary>
    private float Sample(Layer layer, float[] values, float x, float z)
    {
        float fx = (x - layer.X0) / layer.Step;
        float fz = (z - layer.Z0) / layer.Step;
        int ix = Mathf.Clamp((int)Mathf.Floor(fx), 0, layer.Nx - 1);
        int iz = Mathf.Clamp((int)Mathf.Floor(fz), 0, layer.Nz - 1);
        int ix1 = Mathf.Min(ix + 1, layer.Nx - 1);
        int iz1 = Mathf.Min(iz + 1, layer.Nz - 1);
        float tx = fx - ix, tz = fz - iz;

        float v00 = values[layer.Index(ix, iz)];
        float v10 = values[layer.Index(ix1, iz)];
        float v01 = values[layer.Index(ix, iz1)];
        float v11 = values[layer.Index(ix1, iz1)];
        return Mathf.Lerp(Mathf.Lerp(v00, v10, tx), Mathf.Lerp(v01, v11, tx), tz);
    }

    public override float GetHeight(float wx, float wy)
    {
        HeightQueries++;
        Layer layer = LayerAt(wx, wy);
        return Sample(layer, layer.Height, wx, wy);
    }

    /// <summary>The biome of the nearest sample: a biome is a label, not a
    /// number, and averaging two of them would invent a third.</summary>
    public override Heightmap.Biome GetBiome(float wx, float wy)
    {
        BiomeQueries++;
        Layer layer = LayerAt(wx, wy);
        int ix = Mathf.Clamp(Mathf.RoundToInt((wx - layer.X0) / layer.Step), 0, layer.Nx - 1);
        int iz = Mathf.Clamp(Mathf.RoundToInt((wy - layer.Z0) / layer.Step), 0, layer.Nz - 1);
        return layer.Biome[layer.Index(ix, iz)];
    }

    public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
    {
        Layer layer = LayerAt(wx, wy);
        weight = Sample(layer, layer.River, wx, wy);
        width = layer.HasRiverWidth ? Sample(layer, layer.RiverWidth, wx, wy) : 0f;
    }

    public override float GetBaseHeight(float wx, float wy, bool menuTerrain)
    {
        Layer layer = IslandGridAt(wx, wy) ?? LayerAt(wx, wy);
        if (!layer.HasBaseHeight)
            throw new InvalidOperationException(
                $"{Path.GetFileName(layer.Source)} has no base_height column, so island detection cannot run on it. " +
                "Dump the 128 m base grid with the column and load it.");
        return Sample(layer, layer.BaseHeight, wx, wy);
    }

    /// <summary>
    /// The dump holds the height the world actually had at each point, not one
    /// height field per biome, so a biome's own field is answered with that.
    /// Blending across a biome edge is approximate here.
    /// </summary>
    public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask)
    {
        mask = default;
        return GetHeight(wx, wy);
    }

    public override int GetSeed() => m_seed;
}
