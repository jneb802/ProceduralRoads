using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;
using Xunit.Abstractions;

namespace ProceduralRoads.Tests;

/// <summary>
/// A crossing the detector stores is one the save can read back: a road whose path goes into a pool and turns back out
/// on the bank it came from walks both wade banks to the same dry-floor point, a crossing of no span.
/// </summary>
public sealed class ZeroSpanCrossingTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly ITestOutputHelper _out;

    public ZeroSpanCrossingTests(ITestOutputHelper output) { _out = output; RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); }
    public void Dispose() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); RoadNetworkPersistence.Reset(); _scope.Dispose(); }

    // A level pool from x -8 to 8, 0.8 m deep, gentle banks: knee-deep, so a wade is eligible.
    private sealed class Pool : WorldGenerator
    {
        public override float GetHeight(float x, float z)
        {
            if (Mathf.Abs(x) > 128f || Mathf.Abs(z) > 64f) return 0f;
            return RoadConstants.SeaLevel + Mathf.Min(1f, Mathf.Max(-0.8f, (Mathf.Abs(x) - 8f) * 0.1f));
        }
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    private static List<Vector2> InAndBack() => new()
    {
        new(-40f, 0f), new(-32f, 0f), new(-24f, 0f), new(-16f, 0f), new(-8f, 0f), new(0f, 0f),
        new(-8f, 0f), new(-16f, 0f), new(-24f, 0f), new(-32f, 0f), new(-40f, 0f),
    };

    /// <summary>The detector, asked by generation, manual roads, approach trials and the search alike: a road that goes
    /// into a pool and turns back out on its own bank never crosses the water, so no crossing is stored (measured: a
    /// generated network saved such a wade, both banks at one dry-floor point, and its load then refused every crossing).
    /// What it does store saves and loads back whole.</summary>
    [Fact]
    public void APathThatTurnsBackInAPoolStoresNoCrossingTheSaveCannotRead()
    {
        using var weights = StaticOverride.Keep(() => RoadCrossingDetector.ConfiguredWadeWeight)
            .AndKeep(() => RoadCrossingDetector.ConfiguredRaiseWeight).AndKeep(() => RoadCrossingDetector.ConfiguredSpanWeight);
        RoadCrossingDetector.SetFordStyleWeights(1f, 0f, 0f);
        var world = new Pool();
        _scope.WithWorld(world).WithZdos();
        RoadNetworkPersistence.EnsureMetadataInstance();
        // The same pool crossed: one wade, banks apart.
        var through = new List<Vector2> { new(-40f, 0f), new(-24f, 0f), new(-8f, 0f), new(8f, 0f), new(24f, 0f), new(40f, 0f) };
        var crossed = Assert.Single(RoadCrossingDetector.Detect(through, world, bridges: true));
        Assert.Equal(FordStyle.Wade, crossed.Style);
        var crossings = RoadCrossingDetector.Detect(InAndBack(), world, bridges: true);
        foreach (var c in crossings)
            _out.WriteLine($"{c.Kind} {c.Style} {c.FromBank} -> {c.ToBank} width {c.Width} {c.ShallowNote}");
        Assert.All(crossings, c => Assert.True(RoadCrossing.HasSpan(c.Width), $"a crossing of {c.Width} m"));

        var all = crossings.Append(crossed).ToList();
        RoadSpatialGrid.CommitLevelled(through, through.Select(_ => 31f).ToList(), 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>(), all, new HashSet<Vector2s>());
        RoadSpatialGrid.Clear();
        var loaded = new List<RoadCrossing>();
        Assert.True(RoadNetworkPersistence.TryLoadGlobalRoadData(new List<(Vector2, string)>(), loaded, new HashSet<Vector2s>()));
        Assert.False(RoadNetworkPersistence.LastLoadUnreadable);
        Assert.Equal(all.Count, loaded.Count);
    }

    /// <summary>The crossings this build makes and the ones it never does (no span, past the longest, outside the world,
    /// a number that is not one, a bridge with a ford style, a valley ford, an end off its line or with no way onto it).</summary>
    public static IEnumerable<object[]> Cases()
    {
        RoadCrossing Ford(Vector2 from, Vector2 to, FordStyle style = FordStyle.Wade) =>
            RoadCrossing.Between(from, to, 29.2f, (from + to) * 0.5f, 0f, CrossingKind.Ford, style);
        var wade = Ford(new(-8f, 0f), new(8f, 0f)); wade.Shallow = true;
        yield return new object[] { "a wade", wade, true };
        var noSpan = Ford(new(-672.5f, -3144f), new(-672.5f, -3144f)); noSpan.Shallow = true;
        noSpan.FromEnd = new StructureEnds.End(noSpan.FromBank, new Vector2(0f, 0f), 30.5f, 0, road: true);
        noSpan.ToEnd = new StructureEnds.End(noSpan.ToBank, new Vector2(0f, 0f), 30.5f, 0, road: true);
        yield return new object[] { "a wade of no span, as generation saved it", noSpan, false };
        yield return new object[] { "half a metre", Ford(new(0f, 0f), new(0.5f, 0f)), false };
        yield return new object[] { "a metre", Ford(new(0f, 0f), new(RoadCrossing.MinSpan, 0f)), true };
        yield return new object[] { "past the longest", Ford(new(0f, 0f), new(RoadNetworkPersistence.MaxCrossingSpan + 1f, 0f)), false };
        yield return new object[] { "outside the world", Ford(new(RoadConstants.WorldRadius - 3f, 0f), new(RoadConstants.WorldRadius + 5f, 0f)), false };
        var nan = Ford(new(0f, 0f), new(8f, 0f)); nan.RiverbedHeight = float.NaN;
        yield return new object[] { "a bed that is no number", nan, false };
        var bridgeStyled = RoadCrossing.Between(new(0f, 0f), new(20f, 0f), 25f, new(10f, 0f), 8f, CrossingKind.Bridge, FordStyle.Wade);
        yield return new object[] { "a bridge with a ford style", bridgeStyled, false };
        var valleyFord = Ford(new(0f, 0f), new(20f, 0f)); valleyFord.ValleyJump = true;
        yield return new object[] { "a valley ford", valleyFord, false };
        var bridge = RoadCrossing.Between(new(0f, 0f), new(20f, 0f), 25f, new(10f, 0f), 8f, CrossingKind.Bridge);
        bridge.Kit = BridgeStructures.Kit.Undecided;
        bridge.FromEnd = new StructureEnds.End(new(0f, 0f), new(1f, 0f), 31f, 1);
        bridge.ToEnd = new StructureEnds.End(new(20f, 0f), new(-1f, 0f), 31f, 1);
        yield return new object[] { "a bridge with its ends", bridge, true };
        var offLine = RoadCrossing.Between(new(0f, 0f), new(20f, 0f), 25f, new(10f, 0f), 8f, CrossingKind.Bridge);
        offLine.ToEnd = new StructureEnds.End(new(20f, 40f), new(-1f, 0f), 31f, 1);
        yield return new object[] { "an end off its line", offLine, false };
    }

    /// <summary>One rule for a valid crossing record, asked by the save and the load: the save writes exactly the crossings
    /// the load reads back, so whatever is saved loads; a record holding a crossing the rule refuses is refused on reading;
    /// and a crossing the rule refuses is not saved, so one bad crossing never takes the others with it.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void TheSaveAndTheLoadJudgeACrossingRecordByOneRule(string what, RoadCrossing crossing, bool valid)
    {
        var good = RoadCrossing.Between(new(100f, 0f), new(112f, 0f), 29.2f, new(106f, 0f), 0f, CrossingKind.Ford, FordStyle.Raise);
        Assert.Equal(valid, RoadNetworkPersistence.CrossingRecordFault(crossing) == null);
        // Whatever the save writes, the load reads, crossing for crossing.
        var back = new List<RoadCrossing>();
        Assert.True(RoadNetworkPersistence.DeserializeRoadCrossings(RoadNetworkPersistence.SerializeRoadCrossings(new[] { good, crossing }), back), what);
        Assert.Equal(valid ? 2 : 1, back.Count);
        // The load refuses a record holding what the save would not write, by the same rule.
        Assert.Equal(valid, RoadNetworkPersistence.DeserializeRoadCrossings(RoadNetworkPersistence.EncodeRoadCrossings(new[] { crossing }), new List<RoadCrossing>()));
    }
}
