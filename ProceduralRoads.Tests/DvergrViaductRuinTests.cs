using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using static ProceduralRoads.Tests.DvergrViaductTests;

namespace ProceduralRoads.Tests;

/// <summary>
/// Ruin of the Dvergr viaducts like the vanilla viaduct's.
/// </summary>
public sealed class DvergrViaductRuinTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public DvergrViaductRuinTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    // ---------------------------------------------------------------- ruin

    /// <summary>The vanilla Mistlands_Viaduct1 (24 modules) is missing 6 % of its iron floors, 30 % of its deck
    /// course and 46 % of its arches, with one module wholly open; ours takes the course and arches halfway back toward
    /// a light ruin (the vanilla rate looked like too much missing). Ours, over 40 seeds of a 96 m valley viaduct: each within a
    /// vanilla-like range, the iron showing through the course, and now and then a whole module open, never two running.</summary>
    [Fact]
    public void RuinIsInAVanillaLikeRangeWithTheIronShowingAndOnlyJumpableGaps()
    {
        var world = new MistValley { Depth = 12f, FloorHalf = 36f, Face = 1f };
        _scope.WithWorld(world);
        RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-48f, 0f), new Vector2(48f, 0f), world, out string why)!;
        Assert.True(c != null, why);
        float deck = DvergrViaduct.DeckHeight(c!, world);
        int floors = 0, allFloors = 0, course = 0, allCourse = 0, arches = 0, allArches = 0, holes = 0, ironShowing = 0;
        foreach (int seed in Enumerable.Range(1, 40))
        {
            List<StructurePiece> built = DvergrViaduct.Solve(c!, world, seed), complete = DvergrViaduct.SolveComplete(c!, world, seed);
            bool IsCourse(StructurePiece p) => p.Prefab == DvergrViaduct.Block && p.Kind == StructurePieceKind.ViaductDeck;
            floors += built.Count(p => p.Prefab == DvergrViaduct.Floor); allFloors += complete.Count(p => p.Prefab == DvergrViaduct.Floor);
            course += built.Count(IsCourse); allCourse += complete.Count(IsCourse);
            arches += built.Count(p => p.Prefab == DvergrViaduct.Arch); allArches += complete.Count(p => p.Prefab == DvergrViaduct.Arch);
            // Modules by their centre along the deck.
            var modules = complete.Where(p => p.Prefab == DvergrViaduct.Floor).Select(p => Mathf.RoundToInt(Along(c!, p))).Distinct().OrderBy(a => a).ToList();
            var floored = new HashSet<int>(built.Where(p => p.Prefab == DvergrViaduct.Floor).Select(p => Mathf.RoundToInt(Along(c!, p))));
            Vector2 side = new(-c!.Direction.y, c.Direction.x);
            var coursed = built.Where(IsCourse)
                .Select(p => (Mathf.RoundToInt(Along(c, p)), Mathf.RoundToInt(Vector2.Dot(new Vector2(p.Position.x, p.Position.z) - c.FromBank, side)))).ToHashSet();
            for (int i = 0; i < modules.Count; i++)
            {
                if (!floored.Contains(modules[i]))
                {
                    holes++;
                    Assert.True(i > 0 && i + 1 < modules.Count, $"seed {seed}: an end module open at {modules[i]}");
                    Assert.True(floored.Contains(modules[i - 1]) && floored.Contains(modules[i + 1]),
                        $"seed {seed}: two modules running open at {modules[i]}: not a gap a player jumps");
                }
                else if (!coursed.Contains((modules[i], 0))) ironShowing++;
            }
            Assert.All(built, p => Assert.True(p.Kind != StructurePieceKind.ViaductDeck || p.Position.y <= deck + 0.01f));
        }
        float Missing(int have, int all) => 1f - have / (float)all;
        Assert.InRange(Missing(floors, allFloors), 0.03f, 0.12f);
        // Halfway between a light and a heavy ruin: the course about 17 % gone
        // (light about 4 %, heavy 30 %), the arches about 32 % (19 %, 45 %).
        Assert.InRange(Missing(course, allCourse), 0.12f, 0.23f);
        Assert.InRange(Missing(arches, allArches), 0.24f, 0.40f);
        Assert.InRange(holes, 1, 40 * 2);
        Assert.True(ironShowing > 20, $"the iron floor shows through the course on {ironShowing} module(s) over 40 seeds");
    }

    /// <summary>Block health like vanilla (the blocks of a ruined bridge are not at 100 % health: the game damages Mistlands
    /// prefabs). Vanilla's
    /// Location.m_applyRandomDamage puts every piece of the location at 10-60 % of its health (WearNTear.Awake). Every piece
    /// of a ruined viaduct is placed within that range, spread over it, the same for the same crossing and world seed and
    /// different for another seed; the complete (unruined) viaduct stays whole.</summary>
    [Fact]
    public void EveryPieceIsDamagedLikeVanillasLocationsFixedPerCrossingAndSeed()
    {
        var world = new MistValley { Depth = 12f, FloorHalf = 36f, Face = 1f };
        _scope.WithWorld(world);
        RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-48f, 0f), new Vector2(48f, 0f), world, out string why)!;
        Assert.True(c != null, why);
        List<StructurePiece> built = DvergrViaduct.Solve(c!, world, 1234);
        Assert.NotEmpty(built);
        Assert.All(built, p => Assert.InRange(p.HealthFraction, 0.1f, 0.6f));
        var healths = built.Select(p => p.HealthFraction).ToList();
        Assert.True(healths.Min() < 0.2f && healths.Max() > 0.5f, $"health {healths.Min():F2}-{healths.Max():F2}: not spread over vanilla's range");
        Assert.True(Math.Abs(healths.Average() - 0.35f) < 0.05f, $"mean health {healths.Average():F2}, vanilla's 0.35");
        string Key(StructurePiece p) => FormattableString.Invariant($"{p.Prefab} {p.Position.x:F2} {p.Position.y:F2} {p.Position.z:F2} {p.HealthFraction:F4}");
        Assert.Equal(built.Select(Key), DvergrViaduct.Solve(c!, world, 1234).Select(Key));
        var again = DvergrViaduct.Solve(c!, world, 98765).ToDictionary(p => FormattableString.Invariant($"{p.Prefab} {p.Position.x:F2} {p.Position.y:F2} {p.Position.z:F2}"), p => p.HealthFraction);
        int differ = built.Count(p => again.TryGetValue(FormattableString.Invariant($"{p.Prefab} {p.Position.x:F2} {p.Position.y:F2} {p.Position.z:F2}"), out float h) && Math.Abs(h - p.HealthFraction) > 1e-4f);
        Assert.True(differ > built.Count / 2, $"another world seed changed the health of only {differ} of {built.Count} pieces");
        Assert.All(DvergrViaduct.SolveComplete(c!, world, 1234), p => Assert.Equal(1f, p.HealthFraction));
    }

    // ------------------------------------------------- between structures

    /// <summary>Two Mistlands valleys 18 m deep side by side along z, their rims at x -50/-4 and 4/50: an 8 m ridge
    /// between them, as at (6895,-880), where two viaducts' ends stood about 8 m apart.</summary>
    private sealed class TwinValleys : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float d = Mathf.Min(Mathf.Abs(wx + 27f), Mathf.Abs(wx - 27f));
            return 60f - Mathf.Clamp(18f - (d - 8f) * 1.2f, 0f, 18f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 79;
    }

    /// <summary>5c: no viaduct's end within one vanilla bay (16 m) of another's, on the same road or another's.</summary>
    [Fact]
    public void NoTwoViaductsStandBackToBack()
    {
        var a = (new Vector2(-48f, 0f), new Vector2(-4f, 0f));
        Assert.NotNull(DvergrViaduct.TooClose(new Vector2(4f, 0f), new Vector2(48f, 0f), new[] { a }));
        Assert.Contains("back to back", DvergrViaduct.TooClose(new Vector2(4f, 0f), new Vector2(48f, 0f), new[] { a }));
        Assert.Null(DvergrViaduct.TooClose(new Vector2(30f, 0f), new Vector2(74f, 0f), new[] { a }));
        // In line but nearer: its approach in the other's (ends 24 m apart, approaches 13 m each).
        Assert.Contains("in another viaduct", DvergrViaduct.TooClose(new Vector2(20f, 0f), new Vector2(64f, 0f), new[] { a }));
        // The same viaduct (a shared crossing) is not another.
        Assert.Null(DvergrViaduct.TooClose(new Vector2(-4f, 0f), new Vector2(-48f, 0f), new[] { a }));

        var world = new TwinValleys();
        _scope.WithWorld(world);
        var finder = new RoadPathfinder(world) { Stairs = true, Viaducts = true, TerrainVarianceThreshold = 1000f, Meander = 0f };
        Assert.NotNull(finder.FindPath(new Vector2(-110f, 0f), new Vector2(110f, 0f)));
        var jumps = finder.LastViaductJumps;
        Assert.NotEmpty(jumps);
        for (int i = 0; i < jumps.Count; i++)
            for (int j = i + 1; j < jumps.Count; j++)
                foreach (Vector2 p in new[] { jumps[i].from, jumps[i].to })
                    foreach (Vector2 q in new[] { jumps[j].from, jumps[j].to })
                        Assert.True(Vector2.Distance(p, q) >= DvergrViaduct.EndSpacing - 1e-3f, $"viaduct ends {p} and {q} back to back");

        // Another road's viaduct: laid this generation, kept clear of.
        DvergrViaduct.ResetStanding();
        DvergrViaduct.MarkLaid(RoadCrossing.Between(a.Item1, a.Item2, 42f, (a.Item1 + a.Item2) * 0.5f, 0f, CrossingKind.Bridge));
        try
        {
            var other = new RoadPathfinder(world) { Stairs = true, Viaducts = true, TerrainVarianceThreshold = 1000f, Meander = 0f };
            Assert.NotNull(other.FindPath(new Vector2(-110f, 40f), new Vector2(110f, 40f)));
            Assert.All(other.LastViaductJumps, jump => Assert.Null(DvergrViaduct.TooClose(jump.from, jump.to, new[] { a })));
        }
        finally { DvergrViaduct.ResetStanding(); }
    }

    /// <summary>5b: no viaduct inside another's deck or end-approach volume (an 80 m span's arches ran on past its
    /// end into a 64 m span's deck at (6895,-880)). One owner (DvergrViaduct.TooClose / Footprint), as stairs are kept off roads.</summary>
    [Fact]
    public void NoViaductStandsInAnothersDeckOrApproach()
    {
        var ew = (new Vector2(0f, 0f), new Vector2(80f, 0f));
        // A N-S viaduct whose line crosses the E-W one's east approach, 6 m past its bank, its ends well clear (> 16 m).
        Assert.Contains("in another viaduct", DvergrViaduct.TooClose(new Vector2(86f, -30f), new Vector2(86f, 30f), new[] { ew }) ?? "none");
        Assert.Contains("in another viaduct", DvergrViaduct.TooClose(new Vector2(40f, 30f), new Vector2(40f, -30f), new[] { ew }) ?? "none");
        // Clear of it: beside the approach, and in line beyond it.
        Assert.Null(DvergrViaduct.TooClose(new Vector2(110f, -40f), new Vector2(110f, -18f), new[] { ew }));
        Assert.Null(DvergrViaduct.TooClose(new Vector2(40f, 40f), new Vector2(40f, 20f), new[] { ew }));
        // The volume: the deck bank to bank, the most end stairs and the levelled apron past each bank, the arches' width.
        Vector2[] box = DvergrViaduct.Footprint(ew.Item1, ew.Item2);
        Assert.Equal(-DvergrViaduct.ApproachReach, box.Min(v => v.x), 3);
        Assert.Equal(80f + DvergrViaduct.ApproachReach, box.Max(v => v.x), 3);
        Assert.Equal(DvergrViaduct.HalfWidth, box.Max(v => v.y), 3);
        Assert.Equal(6f + StructureEnds.ApronLength, DvergrViaduct.ApproachReach, 3);
    }

    /// <summary>The search asks <see cref="DvergrViaduct.IsTooClose"/> on every valley jump it looks at, from every island
    /// worker at once: it gives TooClose's answer, reads the viaducts laid this generation as
    /// MarkLaid publishes them (no lock, no copy), and allocates nothing.</summary>
    [Fact]
    public void TheSearchsClearanceTestAgreesWithTooCloseAndAllocatesNothing()
    {
        var ew = (new Vector2(0f, 0f), new Vector2(80f, 0f));
        var probes = new[]
        {
            (new Vector2(86f, -30f), new Vector2(86f, 30f)), (new Vector2(40f, 30f), new Vector2(40f, -30f)),
            (new Vector2(110f, -40f), new Vector2(110f, -18f)), (new Vector2(40f, 40f), new Vector2(40f, 20f)),
            (new Vector2(88f, 0f), new Vector2(130f, 0f)), (new Vector2(80f, 0f), new Vector2(0f, 0f)),
        };
        DvergrViaduct.ResetStanding();
        try
        {
            foreach (var (a, b) in probes) Assert.False(DvergrViaduct.IsTooClose(a, b));
            DvergrViaduct.MarkLaid(RoadCrossing.Between(ew.Item1, ew.Item2, 80f, (ew.Item1 + ew.Item2) * 0.5f, 0f, CrossingKind.Bridge));
            foreach (var (a, b) in probes)
            {
                string? why = DvergrViaduct.TooClose(a, b, new[] { ew });
                Assert.Equal(why, DvergrViaduct.TooClose(a, b));
                Assert.Equal(why != null, DvergrViaduct.IsTooClose(a, b));
                Assert.Equal(why != null, DvergrViaduct.IsTooClose(a, b, new[] { ew }));
            }
            Assert.Contains("back to back", DvergrViaduct.TooClose(new Vector2(88f, 0f), new Vector2(130f, 0f)));
            Assert.Contains("in another viaduct", DvergrViaduct.TooClose(new Vector2(86f, -30f), new Vector2(86f, 30f)));
#if NET
            DvergrViaduct.IsTooClose(probes[0].Item1, probes[0].Item2);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
                foreach (var (a, b) in probes) DvergrViaduct.IsTooClose(a, b);
            Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
#endif
            DvergrViaduct.ResetStanding();
            foreach (var (a, b) in probes) Assert.False(DvergrViaduct.IsTooClose(a, b));
        }
        finally { DvergrViaduct.ResetStanding(); }
    }
}
