using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads.Tests;

/// <summary>
/// A way onto a placed location's landing as generation laid it, captured offline at RoadNetworkGenerator.PlanEntryFrom on a
/// generated world (Fixtures/approach-capture-*.txt): what the planner was asked (the request, the staircase row the road ends
/// on, the location's record and those whose ground reaches it, the footprints round it), the world's own samples over a window
/// round it, and the staircase it laid. Replayed (<see cref="Replay"/>), the approach is laid again through the production
/// planner with production's arguments: the planning lattice of a world answering from those samples, the locations' preload,
/// RoadLocationProtection.Contains, the record's rocks and shape. The file format: "seed", "request", "tail", "top", "biome",
/// "footprint" rows, the location's centre ("placesite"), the records (PlacedLocations.Write), the window ("window" then one
/// "cell" row per sample, rows of x within z, on the 8 m grid from -10000), then the laid "span" and its "piece" rows.
/// </summary>
internal sealed class ApproachCaptureFile
{
    public string Name = "";
    public int Seed;
    public LocationApproachRequest Request = new();
    public StairContinuation? Top;
    public Heightmap.Biome Biome;
    public Vector2 Location;
    public readonly List<RoadLocationProtection.Footprint> Footprints = new();
    public List<PlacedLocationRecord> Records = new();
    public int Ix0, Iz0, Nx, Nz;
    public float[] Height = Array.Empty<float>(), River = Array.Empty<float>(), RiverWidth = Array.Empty<float>();
    public Heightmap.Biome[] Biomes = Array.Empty<Heightmap.Biome>();
    public string Kit = "";
    public Vector2 From, To;
    public float FromHeight, ToHeight;
    public readonly List<StairPiece> Laid = new();

    public PlacedLocationRecord Record => Records.Single(r => r.Centre == Location);

    private static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);

    public static ApproachCaptureFile Load(string name)
    {
        var c = new ApproachCaptureFile { Name = name };
        var records = new System.Text.StringBuilder();
        int cell = 0;
        foreach (string raw in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            string[] f = line.Split(',');
            // A rock's real top: "top,v;v;..." follows its "rock" row (PlacedLocations.Write), unlike the request's "top" row.
            if (f[0] == "top" && records.Length > 0 && LastWasRock(records)) { records.Append(line).Append('\n'); continue; }
            switch (f[0])
            {
                case "seed": c.Seed = I(f[1]); break;
                case "request":
                    c.Request = new LocationApproachRequest
                    {
                        RoadEnd = new Vector2(F(f[1]), F(f[2])), RoadHeight = F(f[3]), Arriving = F(f[4]), Centre = new Vector2(F(f[5]), F(f[6])),
                        Reach = F(f[7]), TotalExpansions = I(f[8]), Stall = I(f[9]), Leg = I(f[10]),
                    };
                    break;
                case "tail":
                    c.Request.RoadTail = line.Substring(5).Split('|').Select(p => p.Split(';')).Select(p => new Vector2(F(p[0]), F(p[1]))).ToList();
                    break;
                case "top":
                    if (f[1] != "none")
                        c.Top = new StairContinuation(new Vector2(F(f[1]), F(f[2])), F(f[3]), F(f[4]), (StairPieceKind)Enum.Parse(typeof(StairPieceKind), f[5]));
                    break;
                case "biome": c.Biome = (Heightmap.Biome)Enum.Parse(typeof(Heightmap.Biome), f[1]); break;
                case "footprint": c.Footprints.Add(new RoadLocationProtection.Footprint(new Vector2(F(f[1]), F(f[2])), F(f[3]), F(f[4]))); break;
                case "placesite": c.Location = new Vector2(F(f[1]), F(f[2])); break;
                case "site": case "op": case "rock": records.Append(line).Append('\n'); break;
                case "window":
                    c.Ix0 = I(f[1]); c.Iz0 = I(f[2]); c.Nx = I(f[3]); c.Nz = I(f[4]);
                    int n = c.Nx * c.Nz;
                    c.Height = new float[n]; c.River = new float[n]; c.RiverWidth = new float[n]; c.Biomes = new Heightmap.Biome[n];
                    break;
                case "cell":
                    c.Height[cell] = F(f[1]); c.Biomes[cell] = (Heightmap.Biome)Enum.Parse(typeof(Heightmap.Biome), f[2]);
                    c.River[cell] = F(f[3]); c.RiverWidth[cell] = F(f[4]);
                    cell++;
                    break;
                case "span":
                    c.From = new Vector2(F(f[1]), F(f[2])); c.FromHeight = F(f[3]); c.To = new Vector2(F(f[4]), F(f[5])); c.ToHeight = F(f[6]); c.Kit = f[7];
                    break;
                case "piece":
                    c.Laid.Add(new StairPiece { Kind = (StairPieceKind)Enum.Parse(typeof(StairPieceKind), f[1]), Prefab = f[2],
                        Position = new Vector3(F(f[3]), F(f[4]), F(f[5])), YawDegrees = F(f[6]) });
                    break;
            }
        }
        if (cell != c.Nx * c.Nz) throw new FormatException($"{name}: {cell} cells for a {c.Nx}x{c.Nz} window");
        c.Records = PlacedLocations.Read(records.ToString());
        return c;
    }

    private static bool LastWasRock(System.Text.StringBuilder records)
    {
        string text = records.ToString().TrimEnd('\n');
        int at = text.LastIndexOf('\n');
        return text.Substring(at + 1).StartsWith("rock,", StringComparison.Ordinal);
    }

    /// <summary>
    /// The world the capture was taken on, over its window: the samples answered as the offline world answers them (bilinear
    /// between the 8 m samples from -10000, to the bit; a biome by its nearest sample; a biome's own height field by the
    /// height dumped), and nothing outside the window: a replay reading past it is an incomplete capture, not a guess.
    /// </summary>
    internal sealed class World : WorldGenerator
    {
        private const float Origin = -10000f, Step = 8f;
        private const int Samples = 2501;
        private readonly ApproachCaptureFile _c;
        public World(ApproachCaptureFile c) { _c = c; }

        private int At(int ix, int iz)
        {
            int i = ix - _c.Ix0, k = iz - _c.Iz0;
            if (i < 0 || k < 0 || i >= _c.Nx || k >= _c.Nz)
                throw new InvalidOperationException(FormattableString.Invariant($"{_c.Name}: sample ({Origin + Step * ix}, {Origin + Step * iz}) is outside the captured window"));
            return k * _c.Nx + i;
        }

        private float Sample(float[] values, float x, float z)
        {
            float fx = (x - Origin) / Step, fz = (z - Origin) / Step;
            int ix = Mathf.Clamp((int)Mathf.Floor(fx), 0, Samples - 1), iz = Mathf.Clamp((int)Mathf.Floor(fz), 0, Samples - 1);
            int ix1 = Mathf.Min(ix + 1, Samples - 1), iz1 = Mathf.Min(iz + 1, Samples - 1);
            float tx = fx - ix, tz = fz - iz;
            float v00 = values[At(ix, iz)], v10 = values[At(ix1, iz)], v01 = values[At(ix, iz1)], v11 = values[At(ix1, iz1)];
            return Mathf.Lerp(Mathf.Lerp(v00, v10, tx), Mathf.Lerp(v01, v11, tx), tz);
        }

        public override float GetHeight(float wx, float wy) => Sample(_c.Height, wx, wy);

        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false)
        {
            int ix = Mathf.Clamp(Mathf.RoundToInt((wx - Origin) / Step), 0, Samples - 1), iz = Mathf.Clamp(Mathf.RoundToInt((wy - Origin) / Step), 0, Samples - 1);
            return _c.Biomes[At(ix, iz)];
        }

        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Sample(_c.River, wx, wy);
            width = Sample(_c.RiverWidth, wx, wy);
        }

        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask, bool preGeneration = false, bool riverPreDN = true)
        {
            mask = default;
            return GetHeight(wx, wy);
        }

        public override int GetSeed() => _c.Seed;
    }

    /// <summary>The captured world made the planners' as generation had it: the footprints, then the locations placed first made
    /// ready on its natural ground (PlacedLocations.Prepare, as RoadNetworkGenerator prepares them), the lattice forgotten.
    /// The caller holds a ValheimWorldScope with <paramref name="world"/> set.</summary>
    public PlacedLocation Prepare(World world)
    {
        RoadNetworkGenerator.Reset();
        RoadStairRuns.ResetCache();
        PlacedLocations.Reset();
        RoadLocationProtection.Set(Footprints);
        foreach (PlacedLocationRecord r in Records) PlacedLocations.Remember(r);
        PlacedLocations.Prepare((x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world));
        if (!PlacedLocations.TryGetReady(Location, out PlacedLocation location)) throw new InvalidOperationException($"{Name}: the location at {Location} is not ready");
        return location;
    }

    /// <summary>The approach laid again as RoadNetworkGenerator.PlanEntryFrom lays it: the request and the staircase row it
    /// carries on from, the location's turn, levelling, rock, rocks and shape, the planning lattice
    /// (RoadStairRuns.LayoutGround.Lattice), the world's biomes and seed, and the footprints' own test of a point
    /// (RoadLocationProtection.Contains). <paramref name="joinedAims"/> false: aimed at the level landings in front of the location
    /// only (the planner's own knob for tests), not first where its level ground joins the slope.</summary>
    public RoadStairSpan? Replay(World world, PlacedLocation location, out string why, bool joinedAims = true)
    {
        PlacedLocationRecord r = location.Record;
        var request = new LocationApproachRequest
        {
            RoadEnd = Request.RoadEnd, RoadHeight = Request.RoadHeight, Arriving = Request.Arriving, Centre = Request.Centre,
            RoadTail = new List<Vector2>(Request.RoadTail), Reach = Request.Reach, TotalExpansions = Request.TotalExpansions,
            Stall = Request.Stall, Leg = Request.Leg,
        };
        return RoadStairRuns.PlanPlacedLocationApproach(request, r.Yaw, r.Ops, RoadStairRuns.LayoutGround.Lattice(world),
            (x, z) => world.GetBiome(x, z), world.GetSeed(), out why, r.RockRadius, RoadLocationProtection.Contains, r.Rocks,
            r.KeepSmallRocks, r.Root, Top, joinedAims, placed: location.Shape);
    }

    /// <summary>The pieces of <paramref name="plan"/> that differ from the captured ones (kind, prefab, position to a
    /// millimetre, yaw), one line each, or none: the replay laid what generation laid.</summary>
    public List<string> Differences(StairPlan plan)
    {
        var lines = new List<string>();
        if (plan.Pieces.Count != Laid.Count) lines.Add($"{plan.Pieces.Count} pieces laid, {Laid.Count} captured");
        for (int i = 0; i < Mathf.Min(plan.Pieces.Count, Laid.Count); i++)
        {
            StairPiece a = plan.Pieces[i], b = Laid[i];
            if (a.Kind != b.Kind || a.Prefab != b.Prefab || Vector3.Distance(a.Position, b.Position) > 0.001f || Mathf.Abs(StairLayout.Wrap(a.YawDegrees - b.YawDegrees + 180f) - 180f) > 0.01f)
                lines.Add(FormattableString.Invariant($"piece {i}: {a.Kind} {a.Prefab} ({a.Position.x:F3},{a.Position.y:F3},{a.Position.z:F3}) y{a.YawDegrees} against {b.Kind} {b.Prefab} ({b.Position.x:F3},{b.Position.y:F3},{b.Position.z:F3}) y{b.YawDegrees}"));
            if (lines.Count > 6) break;
        }
        return lines;
    }
}
