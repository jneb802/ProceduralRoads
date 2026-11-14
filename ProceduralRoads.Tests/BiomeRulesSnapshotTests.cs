using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using Xunit.Abstractions;

namespace ProceduralRoads.Tests;

/// <summary>
/// Every value a biome changes, read for every biome through the code that answers it, against the approved table
/// (<see cref="BiomeRulesSnapshot.Expected"/>). The table is data only and changes only with an approved rule: moving where
/// a value is defined (docs/OWNERS.md, the rule values and their biome overrides) leaves it as it is. The readers below are
/// the code's own entry points; a move changes a reader's name here, never an expected line.
/// Each biome is read on a world wholly in that biome: a search over hills (the stair price, the meander, the steep ground),
/// a river crossed at two depths (fords, wades and a swamp bridge's banks), the water lines, the locations, the kits, the
/// rocks, the earthwork faces and the ridges.
/// </summary>
[Collection("RoadStatics")]
public sealed class BiomeRulesSnapshotTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly ValheimWorldScope _scope = new();
    public BiomeRulesSnapshotTests(ITestOutputHelper output) { _out = output; }
    public void Dispose() => _scope.Dispose();

    /// <summary>Gentle hills (the meander's ground: every slope under RoadPathfinder.MeanderFlat), or with
    /// <see cref="Ramp"/> a climb of 0.42 from x = -60 to 60 (the stairs and the Mountains' steep ground), all in one biome.</summary>
    private sealed class HillWorld : WorldGenerator
    {
        public Heightmap.Biome Biome;
        public bool Ramp;
        public override float GetHeight(float x, float z) => Ramp
            ? 60f + 0.42f * Mathf.Clamp(x, -60f, 60f) + 0.5f * Mathf.Sin(z / 17f)
            : 60f + 3f * Mathf.Sin(x / 23f) * Mathf.Cos(z / 31f) + 0.8f * Mathf.Sin((x + z) / 11f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 4242;
    }

    /// <summary>A river 24 m wide with its bed at <see cref="Bed"/> between walls to 33 m, all in one biome.</summary>
    private sealed class RiverWorld : WorldGenerator
    {
        public Heightmap.Biome Biome;
        public float Bed = 28.5f;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 100f || Mathf.Abs(wy) > 100f) return 20f;
            return Mathf.Abs(wx) < 12f ? Bed : 33f;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Clamp01(1f - Mathf.Abs(wx) / 24f);
            width = weight > 0f ? 48f : 0f;
        }
        public override int GetSeed() => 77;
    }

    /// <summary>Flat ground at <see cref="Height"/>, all in one biome.</summary>
    private sealed class FlatWorld : WorldGenerator
    {
        public Heightmap.Biome Biome;
        public float Height = 29f;
        public override float GetHeight(float x, float z) => Height;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 11;
    }

    // The shortest digits that read back as the same float, on every runtime: "R" gives the shortest on .NET Core but up to
    // nine digits on Mono and .NET Framework (2091.52417 for 2091.5242), so the approved table would differ by runtime.
    private static string F(float v)
    {
        for (int digits = 1; digits <= 9; digits++)
        {
            string s = v.ToString("G" + digits, CultureInfo.InvariantCulture);
            if (float.Parse(s, CultureInfo.InvariantCulture) != v) continue;
            return s.Contains("E") ? ((double)float.Parse(s, CultureInfo.InvariantCulture)).ToString("0.#########", CultureInfo.InvariantCulture) : s;
        }
        return v.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>A stable hash of a list of points, to a centimetre.</summary>
    private static string Hash(IReadOnlyList<Vector2>? points)
    {
        if (points == null) return "none";
        unchecked
        {
            uint h = 2166136261u;
            foreach (Vector2 p in points)
                foreach (int v in new[] { Mathf.RoundToInt(p.x * 100f), Mathf.RoundToInt(p.y * 100f) })
                { h ^= (uint)v; h *= 16777619u; }
            return $"{points.Count}:{h:x8}";
        }
    }

    private static string Crossings(List<RoadCrossing> found) => found.Count == 0 ? "none" : string.Join("+", found.Select(c =>
        $"{c.Kind}/{c.Style}/{F(Mathf.Round(c.FromBank.x * 100f) / 100f)}..{F(Mathf.Round(c.ToBank.x * 100f) / 100f)}"));

    /// <summary>Every biome value the game declares, and None.</summary>
    internal static IEnumerable<Heightmap.Biome> Biomes() =>
        new[] { Heightmap.Biome.None }.Concat(Enum.GetValues(typeof(Heightmap.Biome)).Cast<Heightmap.Biome>()
            .Where(b => b != Heightmap.Biome.None)).Distinct();

    // ---- The readers: the code that answers each value. A move renames a reader here; the expected table stays. ----

    private static readonly System.Reflection.MethodInfo MoveCost = typeof(RoadPathfinder).GetMethod("RoadMoveCost",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

    /// <summary>The search as it prices steps here: a gentle hill route, and one step up the ramp (0.42) with its stair
    /// price and whether it is refused, read through the search's own step price (its private RoadMoveCost: the stair price
    /// and the steep-ground refusal are read there, on the search's cell biome).</summary>
    private string Search(Heightmap.Biome b)
    {
        var hills = new HillWorld { Biome = b };
        _scope.WithWorld(hills);
        // The meander on (the test defaults switch it off), at the game default.
        var gentle = new RoadPathfinder(hills) { Meander = 3f };
        string route = Hash(gentle.FindPath(new Vector2(-150f, -120f), new Vector2(150f, 130f)));
        float meander = gentle.MeanderCost(new Vector2(4f, 4f), new Vector2(12f, 4f), new Vector2i(1, 0), 0.05f, 8f);
        var ramp = new HillWorld { Biome = b, Ramp = true };
        _scope.WithWorld(ramp);
        var finder = new RoadPathfinder(ramp) { TerrainVariancePenalty = 0f };
        object?[] args = { new Vector2i(0, 0), new Vector2i(1, 0), 0, 0, null, null, null };
        float cost = (float)MoveCost.Invoke(finder, args)!;
        return $"stair={F(RoadRules.For(b).StairPrice)} meander={F(RoadRules.For(b).Meander)} hills={route}"
            + $" meanderStep={F(meander)} rampStep={F(cost)} rampStair={F((float)args[4]!)} rampSteep={args[6]}";
    }

    private static string Water(Heightmap.Biome b, FlatWorld flat)
    {
        Vector2 p = new(3f, 4f);
        return $"shallow={RoadWater.Of(29f, 0f, flat, p)} unwaded={RoadWater.Of(29f, 0f, flat, p, wade: false)}"
            + $" floor={F(RoadWater.Floor(flat, p))} landing={F(RoadWater.LandingFloorFor(b))} onLand29.5={RoadWater.OnLand(flat, p, 29.5f)}";
    }

    private string River(Heightmap.Biome b, float bed)
    {
        var river = new RiverWorld { Biome = b, Bed = bed };
        _scope.WithWorld(river);
        var line = new List<Vector2> { new(-40f, 0f), new(40f, 0f) };
        List<Vector2>? path = new RoadPathfinder(river).FindPath(new Vector2(-80f, 0f), new Vector2(80f, 0f));
        return $"line={Crossings(RoadCrossingDetector.Detect(line, river, bridges: true))} path={Hash(path)}"
            + $" pathCross={(path == null ? "none" : Crossings(RoadCrossingDetector.Detect(path, river, bridges: true)))}";
    }

    private static string Locations(Heightmap.Biome b)
    {
        Vector2 far = new(7777f, -7777f);
        return $"approachStairs={RoadNetworkGenerator.ApproachStairsAt(far, null)} offLevel={RoadNetworkGenerator.OffLevelAllowed(far, null)}"
            + $" needsPlacing={PlacedLocations.NeedsPlacing("SnapshotLocation", false, false, b, null)}"
            + $" placesFirst={PlacedLocations.PlacesFirst("SnapshotLocation", false, false, b, null)}"
            + $" allowed={new RoadNetworkOptions().Allows(b)}";
    }

    private static string Kits(Heightmap.Biome b, FlatWorld flat)
    {
        var line = new List<Vector2> { new(0f, 0f), new(20f, 0f) };
        var bridge = new RoadCrossing { Kind = CrossingKind.Bridge, FromBank = new Vector2(0f, 0f), ToBank = new Vector2(40f, 0f), Center = new Vector2(20f, 0f) };
        return $"site={StairKit.ForSite(b).Name} line={StairKit.ForLine(line, (x, z) => flat.GetBiome(x, z)).Name}"
            + $" mistKit={StairKit.MistlandsKitBelongs(b)} stoneOverWood={StairKit.StoneOverWoodBelongs(b)}"
            + $" dvergr={BridgeStructures.Fits(BridgeStructures.Kit.DvergrWood, bridge, flat)} rail={BridgeStructures.Fits(BridgeStructures.Kit.WoodRail, bridge, flat)}"
            + $" stoneBelongs={BridgeStructures.StoneBelongs(bridge, flat)} vines={RoadRules.For(b).Vines}";
    }

    private static string Looks(Heightmap.Biome b) =>
        $"face={F(RoadEarthworkNoise.FaceBump(new Vector2(13.7f, 4.2f), 0.5f, 3f, 10f))}"
        + $" boulder={RoadRockPolicy.ClearingTakes("rock1_mountain", b)} mistRock={RoadRockPolicy.ClearingTakes("rock_mistlands1", b)}"
        + $" mistCliff={RoadRockPolicy.ClearingTakes("cliff_mistlands1", b)}"
        + $" ridge={(RoadRules.For(b).Ridge is { } o ? F(o.Depth) + "/" + F(o.Long) : "none")}";

    private List<string> Read()
    {
        var lines = new List<string>();
        foreach (Heightmap.Biome b in Biomes())
        {
            string search = Search(b);
            // Knee deep (a ford everywhere), deeper than wading but unsailable (a swamp's ford, a bridge elsewhere), sailable.
            string rivers = $"bed29.5 {River(b, 29.5f)} | bed28.9 {River(b, 28.9f)} | bed27 {River(b, 27f)}";
            var flat = new FlatWorld { Biome = b };
            _scope.WithWorld(flat);
            lines.Add($"{b}: {search} | {Water(b, flat)} | {rivers} | {Locations(b)} | {Kits(b, flat)} | {Looks(b)}");
        }
        return lines;
    }

    [Fact]
    public void EveryBiomeValueIsAsApproved()
    {
        List<string> actual = Read();
        var wrong = new StringBuilder();
        for (int i = 0; i < Math.Max(actual.Count, BiomeRulesSnapshot.Expected.Length); i++)
        {
            string a = i < actual.Count ? actual[i] : "(none)", e = i < BiomeRulesSnapshot.Expected.Length ? BiomeRulesSnapshot.Expected[i] : "(none)";
            if (a != e) wrong.Append("expected ").Append(e).Append('\n').Append("actual   ").Append(a).Append('\n');
        }
        if (wrong.Length > 0) _out.WriteLine(string.Join("\n", actual.Select(l => "        \"" + l + "\",")));
        Assert.True(wrong.Length == 0, "A biome's value changed (BiomeRulesSnapshot is the approved table; change it only with an approved rule):\n" + wrong);
    }
}
