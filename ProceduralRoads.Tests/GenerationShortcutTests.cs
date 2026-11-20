using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The generation's shortcuts that keep every answer: each against the long way it replaced, to the bit. The search's turn
/// table (RoadPathfinder.TurnAngle against TurnAngleOf), its cell rows (the meander on and off, and with the interior
/// water), the meander noise's lattice (its 24 bits as an int), the viaduct clearance's box (against the two tests it
/// passes over), and location protection's early pass (on and off).
/// </summary>
public sealed class GenerationShortcutTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public GenerationShortcutTests()
    {
        RoadLocationProtection.Reset();
        RoadLocationProtection.Source = null;
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        PlacedLocations.Reset();
        RoadLocationProtection.Reset();
        RoadLocationProtection.Source = null;
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    private static int Bits(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);

    /// <summary>Ground with several biomes, so the meander's biome factor varies.</summary>
    private sealed class Patchy : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f + 0.05f * x + 2f * Mathf.Sin(z * 0.03f);
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            ((Mathf.FloorToInt(x / 96f) + Mathf.FloorToInt(z / 80f)) % 3) switch
            {
                0 => Heightmap.Biome.Meadows,
                1 or -1 => Heightmap.Biome.Swamp,
                _ => Heightmap.Biome.BlackForest,
            };
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 17;
    }

    private static readonly MethodInfo TurnAngle = typeof(RoadPathfinder).GetMethod("TurnAngle", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo TurnAngleOf = typeof(RoadPathfinder).GetMethod("TurnAngleOf", BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>The turn table: the angle the search reads for an arrival and a direction is TurnAngleOf's, to the bit,
    /// for short arrivals (the table) and long ones (worked out each time), on a viaduct's approach state as on a plain one,
    /// asked again and in any order, on two searches sharing the table; and 0 where the state was arrived at by none.</summary>
    [Fact]
    public void TheTurnTableGivesTurnAngleOfToTheBit()
    {
        var world = new Patchy();
        _scope.WithWorld(world);
        var cases = new List<(Vector2i at, int dx, int dy)>();
        var cameFrom = new CellMap<Vector2i>();
        int n = 0;
        for (int dx = -5; dx <= 5; dx++)
            for (int dy = -5; dy <= 5; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                var cell = new Vector2i(20 * (n % 11) - 100, 20 * (n / 11) - 100);
                // Every third state on a viaduct's approach (its key's high bits set): its cell is what the turn reads.
                var at = n % 3 == 0 ? new Vector2i(cell.x + (1 << 16), cell.y) : cell;
                cameFrom[at] = new Vector2i(cell.x - dx, cell.y - dy);
                cases.Add((at, dx, dy));
                n++;
            }
        var none = new Vector2i(999, 999);
        var random = new System.Random(3);
        int directions = RoadPathfinder.Directions.Length;
        foreach (var finder in new[] { new RoadPathfinder(world), new RoadPathfinder(world) })
            for (int round = 0; round < 2; round++)
            {
                foreach (var (at, dx, dy) in cases.OrderBy(_ => random.Next()))
                    foreach (int direction in Enumerable.Range(0, directions).OrderBy(_ => random.Next()))
                    {
                        float expected = (float)TurnAngleOf.Invoke(null, new object[] { dx, dy, direction })!;
                        float read = (float)TurnAngle.Invoke(finder, new object[] { cameFrom, at, direction })!;
                        Assert.True(Bits(expected) == Bits(read), $"arrival ({dx}, {dy}) direction {direction}: {expected:R}, read {read:R}");
                    }
                Assert.Equal(0f, (float)TurnAngle.Invoke(finder, new object[] { cameFrom, none, 0 })!);
            }
    }

    private static readonly MethodInfo RowMeander = typeof(RoadPathfinder).GetMethod("MeanderCost", BindingFlags.NonPublic | BindingFlags.Instance, null,
        new[] { typeof(Vector2), typeof(Vector2), typeof(Vector2i), typeof(float), typeof(float), typeof(Vector2i), typeof(int) }, null)!;

    /// <summary>The meander rows: a search's meander price of a step is the same to the bit with its rows kept
    /// (GenerationMemory.KeepsMoveTerrain) and without them, and the same as the five-argument entry's, for every direction
    /// from many cells, over the whole slope range, asked again and in any order.</summary>
    [Fact]
    public void TheMeanderRowsGiveTheSamePricesOnAndOff()
    {
        var world = new Patchy();
        _scope.WithWorld(world);
        bool keeps = GenerationMemory.KeepsMoveTerrain;
        var steps = new List<(Vector2 from, Vector2 to, Vector2i toCell, float slope, float dist, Vector2i fromCell, int direction)>();
        var random = new System.Random(8);
        for (int cx = -12; cx <= 12; cx += 3)
            for (int cy = -12; cy <= 12; cy += 4)
                for (int d = 0; d < RoadPathfinder.Directions.Length; d++)
                {
                    var fromCell = new Vector2i(cx, cy);
                    Vector2Int dir = RoadPathfinder.Directions[d];
                    var toCell = new Vector2i(cx + dir.x, cy + dir.y);
                    Vector2 from = new(cx * 8f, cy * 8f), to = new(toCell.x * 8f, toCell.y * 8f);
                    float slope = (float)(random.NextDouble() * 0.34);
                    steps.Add((from, to, toCell, slope, Vector2.Distance(from, to), fromCell, d));
                }
        try
        {
            var prices = new Dictionary<bool, float[]>();
            foreach (bool on in new[] { true, false })
            {
                GenerationMemory.KeepsMoveTerrain = on;
                var finder = new RoadPathfinder(world) { Meander = 3f };
                var got = new float[steps.Count];
                for (int round = 0; round < 2; round++)
                    foreach (int i in Enumerable.Range(0, steps.Count).OrderBy(_ => random.Next()))
                    {
                        var s = steps[i];
                        float price = (float)RowMeander.Invoke(finder, new object[] { s.from, s.to, s.toCell, s.slope, s.dist, s.fromCell, s.direction })!;
                        if (round == 1) Assert.Equal(Bits(got[i]), Bits(price));
                        got[i] = price;
                        Assert.Equal(Bits(finder.MeanderCost(s.from, s.to, s.toCell, s.slope, s.dist)), Bits(price));
                    }
                prices[on] = got;
            }
            for (int i = 0; i < steps.Count; i++) Assert.Equal(Bits(prices[false][i]), Bits(prices[true][i]));
            Assert.True(prices[true].Count(p => p > 0f) > steps.Count / 2, "most steps should be priced");
            Assert.Contains(prices[true], p => p == 0f);
        }
        finally { GenerationMemory.KeepsMoveTerrain = keeps; }
    }

    /// <summary>Ground with ponds: standing water just under sea level between dry humps, so some steps run over water.</summary>
    private sealed class Ponds : WorldGenerator
    {
        public override float GetHeight(float x, float z) => RoadConstants.SeaLevel + 0.4f + 1.6f * Mathf.Sin(x * 0.045f) * Mathf.Cos(z * 0.037f);
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            (Mathf.FloorToInt(x / 72f) & 1) == 0 ? Heightmap.Biome.Meadows : Heightmap.Biome.Swamp;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 23;
    }

    private static readonly MethodInfo StepInteriorWater = typeof(RoadPathfinder).GetMethod("StepInteriorWater", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>The cell rows hold a cell's interior water and its meander fields together: kept (GenerationMemory
    /// KeepsCrossingFacts and KeepsMoveTerrain), each answer is the one worked out without rows, to the bit, whichever of the
    /// two a cell is asked first, interleaved over every direction of many cells, wet steps and dry.</summary>
    [Fact]
    public void TheCellRowsKeepWaterAndMeanderApart()
    {
        var world = new Ponds();
        _scope.WithWorld(world);
        bool keepsMove = GenerationMemory.KeepsMoveTerrain, keepsCrossings = GenerationMemory.KeepsCrossingFacts;
        var steps = new List<(Vector2 from, Vector2 to, Vector2i toCell, float slope, float dist, Vector2i fromCell, int direction)>();
        var random = new System.Random(11);
        for (int cx = -14; cx <= 14; cx += 2)
            for (int cy = -14; cy <= 14; cy += 3)
                for (int d = 0; d < RoadPathfinder.Directions.Length; d++)
                {
                    var fromCell = new Vector2i(cx, cy);
                    Vector2Int dir = RoadPathfinder.Directions[d];
                    var toCell = new Vector2i(cx + dir.x, cy + dir.y);
                    Vector2 from = new(cx * 8f, cy * 8f), to = new(toCell.x * 8f, toCell.y * 8f);
                    float slope = (float)(random.NextDouble() * 0.27);
                    steps.Add((from, to, toCell, slope, Vector2.Distance(from, to), fromCell, d));
                }
        try
        {
            var prices = new Dictionary<bool, float[]>();
            var wet = new Dictionary<bool, bool[]>();
            foreach (bool on in new[] { false, true })
            {
                GenerationMemory.KeepsMoveTerrain = GenerationMemory.KeepsCrossingFacts = on;
                var finder = new RoadPathfinder(world) { Meander = 3f };
                var price = new float[steps.Count];
                var water = new bool[steps.Count];
                for (int round = 0; round < 2; round++)
                    foreach (int i in Enumerable.Range(0, steps.Count).OrderBy(_ => random.Next()))
                    {
                        var s = steps[i];
                        // Water first on even cells, the meander first on odd ones, so both orders fill a row.
                        bool waterFirst = ((s.fromCell.x + s.fromCell.y) & 1) == 0;
                        bool w = false;
                        if (waterFirst) w = (bool)StepInteriorWater.Invoke(finder, new object[] { s.fromCell, s.direction })!;
                        float p = (float)RowMeander.Invoke(finder, new object[] { s.from, s.to, s.toCell, s.slope, s.dist, s.fromCell, s.direction })!;
                        if (!waterFirst) w = (bool)StepInteriorWater.Invoke(finder, new object[] { s.fromCell, s.direction })!;
                        if (round == 1)
                        {
                            Assert.Equal(Bits(price[i]), Bits(p));
                            Assert.Equal(water[i], w);
                        }
                        price[i] = p;
                        water[i] = w;
                    }
                prices[on] = price;
                wet[on] = water;
            }
            for (int i = 0; i < steps.Count; i++)
            {
                Assert.Equal(Bits(prices[false][i]), Bits(prices[true][i]));
                Assert.Equal(wet[false][i], wet[true][i]);
            }
            Assert.Contains(wet[true], w => w);
            Assert.Contains(wet[true], w => !w);
            Assert.True(prices[true].Count(p => p > 0f) > steps.Count / 2, "most steps should be priced");
        }
        finally
        {
            GenerationMemory.KeepsMoveTerrain = keepsMove;
            GenerationMemory.KeepsCrossingFacts = keepsCrossings;
        }
    }

    private static readonly MethodInfo Lattice =typeof(RoadPathfinder).GetMethod("Lattice", BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>The meander noise's lattice: its 24 bits converted as an int give the float the unsigned conversion
    /// gives, for every 24-bit value, and the lattice reads that float at its cells.</summary>
    [Fact]
    public void TheLatticesIntConversionIsTheUnsignedOnesFloat()
    {
        for (uint v = 0; v <= 0xFFFFFF; v++)
            if (Bits((int)v / (float)0xFFFFFF) != Bits(v / (float)0xFFFFFF))
                Assert.Fail($"{v}: {(int)v / (float)0xFFFFFF:R} against {v / (float)0xFFFFFF:R}");
        for (int x = -40; x <= 40; x += 3)
            for (int y = -40; y <= 40; y += 7)
            {
                uint h = RoadPathfinder.HashCell(x, y) & 0xFFFFFF;
                Assert.Equal(Bits(h / (float)0xFFFFFF), Bits((float)Lattice.Invoke(null, new object[] { x, y })!));
            }
    }

    private static bool Same((Vector2 from, Vector2 to) x, Vector2 a, Vector2 b) =>
        (Vector2.Distance(x.from, a) <= 1f && Vector2.Distance(x.to, b) <= 1f) || (Vector2.Distance(x.from, b) <= 1f && Vector2.Distance(x.to, a) <= 1f);

    /// <summary>Whether <paramref name="o"/>'s ends both lie past one side of a to b's box, <paramref name="margin"/> wide.</summary>
    private static bool Boxed(Vector2 a, Vector2 b, (Vector2 from, Vector2 to) o, float margin)
    {
        float minX = Mathf.Min(a.x, b.x) - margin, maxX = Mathf.Max(a.x, b.x) + margin;
        float minY = Mathf.Min(a.y, b.y) - margin, maxY = Mathf.Max(a.y, b.y) + margin;
        return (o.from.x < minX && o.to.x < minX) || (o.from.x > maxX && o.to.x > maxX)
            || (o.from.y < minY && o.to.y < minY) || (o.from.y > maxY && o.to.y > maxY);
    }

    /// <summary>The viaduct clearance's box: the verdict and the viaduct named are those of the two tests with no box
    /// before them, for viaducts on either side of the box's edge, near and far, single and in lists. Viaducts are at least
    /// a metre long (a zero-length pair has no footprint axes; no valley jump is that short).</summary>
    [Fact]
    public void TheViaductBoxPassesOverOnlyViaductsNeitherTestHolds()
    {
        float margin = Mathf.Max(2f * (DvergrViaduct.ApproachReach + DvergrViaduct.HalfWidth + 1f), DvergrViaduct.EndSpacing + 1f);
        var random = new System.Random(12);
        Vector2 Point(Vector2 near, float spread) =>
            near + new Vector2((float)(random.NextDouble() * 2.0 - 1.0) * spread, (float)(random.NextDouble() * 2.0 - 1.0) * spread);
        (Vector2, Vector2) Viaduct(Vector2 near, float spread)
        {
            Vector2 from = Point(near, spread);
            float length = 1f + (float)random.NextDouble() * 120f, heading = (float)(random.NextDouble() * Math.PI * 2.0);
            return (from, from + new Vector2(Mathf.Cos(heading), Mathf.Sin(heading)) * length);
        }
        int clash = 0, clear = 0, boxedNear = 0;
        for (int i = 0; i < 4000; i++)
        {
            var (a, b) = Viaduct(new Vector2(0f, 0f), 50f);
            var others = new List<(Vector2 from, Vector2 to)>();
            for (int k = 0; k < 6; k++) others.Add(Viaduct(Vector2.Lerp(a, b, (float)random.NextDouble()), margin * 2.5f));
            int first = -1;
            for (int k = 0; k < others.Count; k++)
            {
                bool expected = !Same(others[k], a, b) && DvergrViaduct.ClashesUnboxed(a, b, others[k]);
                Assert.Equal(expected, DvergrViaduct.IsTooClose(a, b, new[] { others[k] }));
                if (expected) clash++; else clear++;
                if (expected && first < 0) first = k;
                if (Boxed(a, b, others[k], margin) && !Boxed(a, b, others[k], 2f * margin)) boxedNear++;
            }
            Assert.Equal(first >= 0, DvergrViaduct.IsTooClose(a, b, others));
            Assert.Equal(first >= 0 ? DvergrViaduct.TooClose(a, b, new[] { others[first] }) : null, DvergrViaduct.TooClose(a, b, others));
        }
        Assert.True(clash > 1000 && clear > 1000 && boxedNear > 1000, $"{clash} clash, {clear} clear, {boxedNear} boxed out within a margin of the box");
    }

    /// <summary>Location protection's early pass: with it and without it, every segment gets the same verdict and the
    /// same footprint hit, in each mode (exempt, the search's route, the final road with its trimmed arrivals), for targets
    /// that may be entered and ones that may not, at clearances from none to a road's.</summary>
    [Fact]
    public void LocationProtectionsEarlyPassChangesNoVerdict()
    {
        var random = new System.Random(4);
        var footprints = new List<RoadLocationProtection.Footprint>();
        for (int i = 0; i < 14; i++)
        {
            var centre = new Vector2((float)(random.NextDouble() * 240.0 - 120.0), (float)(random.NextDouble() * 240.0 - 120.0));
            float radius = 4f + (float)random.NextDouble() * 18f;
            footprints.Add(new RoadLocationProtection.Footprint(centre, radius, radius * (0.4f + (float)random.NextDouble() * 0.6f)));
        }
        RoadLocationProtection.Set(footprints.ToArray());
        for (int i = 0; i < footprints.Count; i += 2) PlacedLocations.Preload(footprints[i].Centre);
        Vector2? Target() => random.Next(3) == 0 ? null : footprints[random.Next(footprints.Count)].Centre;
        int blocked = 0, passed = 0;
        for (int i = 0; i < 6000; i++)
        {
            var near = footprints[random.Next(footprints.Count)];
            float spread = near.Radius + 12f;
            Vector2 a = near.Centre + new Vector2((float)(random.NextDouble() * 2.0 - 1.0) * spread, (float)(random.NextDouble() * 2.0 - 1.0) * spread);
            Vector2 b = random.Next(10) == 0 ? a : a + new Vector2((float)(random.NextDouble() * 2.0 - 1.0) * 16f, (float)(random.NextDouble() * 2.0 - 1.0) * 16f);
            float clearance = random.Next(5) == 0 ? 0f : (float)random.NextDouble() * 6f;
            var targets = new RoadLocationProtection.Targets(Target(), Target());
            Vector2 start = random.Next(2) == 0 ? a : targets.Start ?? a, end = random.Next(2) == 0 ? b : targets.End ?? b;
            var answers = new List<string>();
            foreach (bool early in new[] { true, false })
            {
                var (exempt, route, final, hit) = RoadLocationProtection.VerdictsWithEarlyPass(early, a, b, clearance, start, end, targets);
                answers.Add($"{exempt} {route} {final} {(hit is RoadLocationProtection.Footprint h ? $"{h.Centre.x:R},{h.Centre.y:R}" : "none")}");
                if (early) { if (exempt || route || final) blocked++; else passed++; }
            }
            Assert.True(answers[0] == answers[1], $"segment ({a.x:R}, {a.y:R}) to ({b.x:R}, {b.y:R}) clearance {clearance:R}: {answers[0]} with the early pass, {answers[1]} without");
            // The public entries (production) run with the early pass on: the same answers as the early arm.
            bool pExempt = RoadLocationProtection.BlocksSegment(a, b, clearance, targets);
            bool pRoute = RoadLocationProtection.BlocksRoute(a, b, clearance, targets);
            bool pFinal = RoadLocationProtection.BlocksFinalSegment(a, b, clearance, start, end, targets, out var pHit);
            string production = $"{pExempt} {pRoute} {pFinal} {(pHit is RoadLocationProtection.Footprint ph ? $"{ph.Centre.x:R},{ph.Centre.y:R}" : "none")}";
            Assert.True(answers[0] == production, $"segment ({a.x:R}, {a.y:R}) to ({b.x:R}, {b.y:R}) clearance {clearance:R}: {answers[0]} early, {production} through the public entries");
        }
        Assert.True(blocked > 1000 && passed > 1000, $"{blocked} blocked, {passed} passed");
    }
}
