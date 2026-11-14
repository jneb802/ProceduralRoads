using System;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A last-resort pitch goes only where nothing else reaches (RoadPitches.AsLastResort, RouteIsland's last round): a road
/// whose bridge end it cannot plan to for a climb too steep for its rests is given up as any other unmet end (the crossing
/// banned, searched again), never planned again with a pitch inside its plan (RoadNetworkGenerator.PlanCandidate), and is
/// built with one only when planned as the last resort, as its destination's last round plans it. A road that climbs to
/// the bridge within its rests on another candidate takes no pitch.
/// The bank west of a 14 m channel climbs to the bridge (bank 45 m, water at 20 m), walled beside the road (300 %); the
/// rests are made tighter (6 m every 8 m) and a pitch longer (30 m) than the mod's, so a 40 m climb at 28 % is too steep
/// for its rests at the bridge's end and one pitch makes it.
/// </summary>
[Collection("RoadStatics")]
public sealed class StructureEndLastResortTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithNetwork(server: true);
    private readonly IDisposable _grade = GradeCap.At(0.35f);

    private sealed class BankClimb : WorldGenerator
    {
        public float Grade;
        public override float GetHeight(float x, float z)
        {
            if (Mathf.Abs(x) > 400f || Mathf.Abs(z) > 400f) return 0f;
            float wall = 3f * Mathf.Max(0f, Mathf.Abs(z) - 10f);
            if (Mathf.Abs(x) < 7f) return 20f;
            return (x >= 7f ? 45f : 45f - Grade * Mathf.Clamp(-7f - x, 0f, 40f)) + wall;
        }
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = Mathf.Abs(x) < 7f ? 1f : 0f; width = 14f; }
        public override int GetSeed() => 80;
    }

    public StructureEndLastResortTests()
    {
        RoadNetworkGenerator.Reset();
        RoadPitches.ResetCounters();
        RoadNetworkGenerator.ConfigureFinders(f => { f.Stairs = false; f.Viaducts = false; f.TerrainVarianceThreshold = 1000f; f.Meander = 0f; });
    }

    public void Dispose()
    {
        RoadNetworkGenerator.ConfigureFinders(null);
        RoadNetworkGenerator.Reset();
        RoadPitches.ResetCounters();
        _grade.Dispose();
        _scope.Dispose();
    }

    /// <summary>The road from the low ground to the far bank as generation plans it (captured), plainly or as the last resort.</summary>
    private static RoadNetworkGenerator.RoadCapture Road(WorldGenerator world, bool lastResort)
    {
        var capture = new RoadNetworkGenerator.RoadCapture();
        using (RoadNetworkGenerator.PlanningWith(RoadNetworkGenerator.NewFinder(world, RoadLocationProtection.Clearance(4f)), capture))
        {
            bool Generate() => RoadNetworkGenerator.GenerateRoad(new Vector3(-87f, 0f, 0f), 0f, new Vector3(60f, 0f, 0f), 0f, 4f, "to the bridge");
            if (lastResort) RoadPitches.AsLastResort("the destination", Generate); else Generate();
        }
        return capture;
    }

    private void Tight(float grade, Action<WorldGenerator> body)
    {
        var world = new BankClimb { Grade = grade };
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady();
        PlainRoads.WithModDefaults(() =>
        {
            float everyMin = RoadPitches.EaseEveryMin, everyMax = RoadPitches.EaseEveryMax, easeMin = RoadPitches.EaseMin, easeMax = RoadPitches.EaseMax, length = RoadPitches.PitchLength;
            RoadPitches.EaseEveryMin = RoadPitches.EaseEveryMax = 8f;
            RoadPitches.EaseMin = RoadPitches.EaseMax = 6f;
            RoadPitches.PitchLength = 30f;
            try { body(world); }
            finally
            {
                RoadPitches.EaseEveryMin = everyMin; RoadPitches.EaseEveryMax = everyMax;
                RoadPitches.EaseMin = easeMin; RoadPitches.EaseMax = easeMax; RoadPitches.PitchLength = length;
            }
        });
    }

    [Fact]
    public void ABridgesEndTooSteepForItsRestsTakesAPitchOnlyAsTheLastResort() => Tight(0.28f, world =>
    {
        int rests = RoadPitches.RestsRefusals;
        RoadNetworkGenerator.RoadCapture plain = Road(world, lastResort: false);
        Assert.True(RoadPitches.RestsRefusals > rests, "the bridge's end was never too steep for its rests");
        Assert.Null(plain.Road);
        Assert.Contains(plain.Refusals, r => r.why.Contains("a bridge end the road does not meet"));
        Assert.Null(RoadPitches.LastResortRunFor);
        Assert.Equal(0, RoadPitches.PitchClimbs);

        RoadNetworkGenerator.RoadCapture last = Road(world, lastResort: true);
        Assert.True(last.Road != null, string.Join("; ", last.Refusals.Select(r => r.why)));
        Assert.Single(last.Road!.Written!.Footprint.Pitches);
        Assert.Contains(last.Road.Crossings, c => c.Kind == CrossingKind.Bridge);
    });

    [Fact]
    public void ARoadThatMeetsTheBridgeWithinItsRestsOnAnotherCandidateTakesNoPitch() => Tight(0.24f, world =>
    {
        int rests = RoadPitches.RestsRefusals;
        RoadNetworkGenerator.RoadCapture plain = Road(world, lastResort: false);
        Assert.True(RoadPitches.RestsRefusals > rests, "no candidate was too steep for its rests");
        Assert.True(plain.Road != null, string.Join("; ", plain.Refusals.Select(r => r.why)));
        Assert.Empty(plain.Road!.Written!.Footprint.Pitches);
        Assert.Contains(plain.Road.Crossings, c => c.Kind == CrossingKind.Bridge);
        Assert.Equal(0, RoadPitches.PitchClimbs);
    });
}

/// <summary>
/// A Mistlands viaduct's end is the one structure end that takes the last resort's pitch on its own road
/// (RoadNetworkGenerator.PlanCandidate): on a generated road (an island worker's), an end that every way of meeting refused
/// for a climb too steep for its rests is planned once more as the last resort, and the road is built with its viaduct and
/// one pitch; a manual road (off an island worker) never plans as the last resort and is refused round the same end. The
/// ban that would follow drops the viaduct: nothing but the pitch reaches its end.
/// Flats at 60 m either side of a dry Mistlands valley (its floor 14 m down, |x| under 8 m, its sides at 120 %); west of
/// the near rim the ground climbs to it at <see cref="RimClimb.Grade"/> over 40 m from x = -20; sea beside the road
/// past |z| = 12. Rests as in <see cref="StructureEndLastResortTests"/>.
/// </summary>
[Collection("RoadStatics")]
public sealed class ViaductEndLastResortTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithNetwork(server: true);
    private readonly IDisposable _grade = GradeCap.At(0.35f);

    private sealed class RimClimb : WorldGenerator
    {
        public float Grade;
        public override float GetHeight(float x, float z)
        {
            if (Mathf.Abs(x) > 400f || Mathf.Abs(z) > 12f) return 0f;
            float valley = Mathf.Clamp(14f - (Mathf.Abs(x) - 8f) * 1.2f, 0f, 14f);
            float climb = x < -20f ? Grade * Mathf.Clamp(-20f - x, 0f, 40f) : 0f;
            return 60f - valley - climb;
        }
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 81;
    }

    public ViaductEndLastResortTests()
    {
        RoadNetworkGenerator.Reset();
        RoadPitches.ResetCounters();
        RoadNetworkGenerator.ConfigureFinders(f => { f.Stairs = false; f.Viaducts = true; f.TerrainVarianceThreshold = 1000f; f.Meander = 0f; });
    }

    public void Dispose()
    {
        RoadNetworkGenerator.ConfigureFinders(null);
        RoadNetworkGenerator.Reset();
        RoadPitches.ResetCounters();
        _grade.Dispose();
        _scope.Dispose();
    }

    private static RoadNetworkGenerator.RoadCapture Road(WorldGenerator world, int island)
    {
        var capture = new RoadNetworkGenerator.RoadCapture();
        using (RoadNetworkGenerator.PlanningWith(RoadNetworkGenerator.NewFinder(world, RoadLocationProtection.Clearance(4f)), capture))
            CrossIslandReads.On(island, () => RoadNetworkGenerator.GenerateRoad(new Vector3(-90f, 0f, 0f), 0f, new Vector3(60f, 0f, 0f), 0f, 4f, "over the valley"));
        return capture;
    }

    private void Tight(float grade, Action<WorldGenerator> body)
    {
        var world = new RimClimb { Grade = grade };
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady();
        PlainRoads.WithModDefaults(() =>
        {
            float everyMin = RoadPitches.EaseEveryMin, everyMax = RoadPitches.EaseEveryMax, easeMin = RoadPitches.EaseMin, easeMax = RoadPitches.EaseMax, length = RoadPitches.PitchLength;
            RoadPitches.EaseEveryMin = RoadPitches.EaseEveryMax = 8f;
            RoadPitches.EaseMin = RoadPitches.EaseMax = 6f;
            RoadPitches.PitchLength = 30f;
            RoadPitches.BeginIsland(1, RoadPitches.PitchBudget);
            try { body(world); }
            finally
            {
                RoadPitches.EaseEveryMin = everyMin; RoadPitches.EaseEveryMax = everyMax;
                RoadPitches.EaseMin = easeMin; RoadPitches.EaseMax = easeMax; RoadPitches.PitchLength = length;
            }
        });
    }

    [Fact]
    public void AViaductsEndOnlyAPitchMeetsTakesOneOnAGeneratedRoadAndNeverOnAManualOne() => Tight(0.28f, world =>
    {
        // Off an island worker: the viaduct's end is given up (banned), and with no other way over the valley, no road.
        int rests = RoadPitches.RestsRefusals;
        RoadNetworkGenerator.RoadCapture manual = Road(world, island: 0);
        Assert.True(RoadPitches.RestsRefusals > rests, "the viaduct's end was never too steep for its rests");
        Assert.Null(manual.Road);
        Assert.Equal(0, RoadPitches.PitchClimbs);

        RoadNetworkGenerator.RoadCapture generated = Road(world, island: 1);
        Assert.True(generated.Road != null, string.Join("; ", generated.Refusals.Select(r => r.why)));
        Assert.Single(generated.Road!.Written!.Footprint.Pitches);
        Assert.Contains(generated.Road.Crossings, c => c.ValleyJump);
    });
}
