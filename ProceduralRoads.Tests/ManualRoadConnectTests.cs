using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// road_connect is the generator's join (RoadNetworkGenerator.JoinTo, the one owner of how a place or a point is joined
/// to the network): a road to the first junction its search finds that is refused is tried again to the next, as the
/// generator tries it; inside a place it warns once and, run again, joins the place as the generator would; and what it
/// refuses it says in one line, without a stack.
/// </summary>
public sealed class ManualRoadConnectTests : IDisposable
{
    /// <summary>Flat meadows at 40 m, 400 m round, in the sea.</summary>
    private sealed class SquareIsland : WorldGenerator
    {
        public override float GetHeight(float x, float z) => Mathf.Abs(x) < 400f && Mathf.Abs(z) < 400f ? 40f : -50f;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0; width = 0; }
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => GetHeight(x, z) > 0f ? Heightmap.Biome.Meadows : Heightmap.Biome.Ocean;
        public override int GetSeed() => 77;
    }

    private readonly ValheimWorldScope _world = new ValheimWorldScope()
        .WithWorld(new SquareIsland()).WithZdos().WithZoneSystem().WithScene().WithNetwork(server: true).WithCommands().WithCheats();
    private readonly Terminal _console = new();
    private readonly int _joinRetries = RoadNetworkGenerator.JoinRetries;
    private readonly float _junctionStep = RoadPathfinder.JunctionStep;

    public ManualRoadConnectTests()
    {
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld(); PlacedSites.Reset();
        RoadNetworkGenerator.MarkLocationsReady(); RoadNetworkGenerator.RoadWidth = RoadConstants.DefaultRoadWidth;
        RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>());
        RoadPathfinder.JunctionStep = RoadPathfinder.DefaultJunctionStep;
        RoadNetworkGenerator.ConfigureFinders(f => { f.TerrainVarianceThreshold = 1000f; f.Meander = 0f; });
        ManualRoadCommands.Register();
    }

    public void Dispose()
    {
        ManualRoadCommands.NextFrame = null;
        RoadNetworkGenerator.JoinRetries = _joinRetries;
        RoadPathfinder.JunctionStep = _junctionStep;
        RoadNetworkGenerator.ConfigureFinders(null);
        RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>());
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld(); PlacedSites.Reset();
        _world.Dispose();
    }

    private string[] Run(string line) { int from = _console.Output.Count; _console.TryRunCommand(line); return _console.Output.Skip(from).ToArray(); }

    private static RoadSpatialGrid.PlannedPath Laid(Vector2 a, Vector2 b, float h)
    {
        var points = new List<Vector2>();
        int n = Mathf.CeilToInt(Vector2.Distance(a, b));
        for (int i = 0; i <= n; i++) points.Add(Vector2.Lerp(a, b, i / (float)n));
        return new RoadSpatialGrid.PlannedPath(points, points.Select(_ => h).ToList(), points.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, n, false);
    }

    /// <summary>A trunk along z = 0 at the ground's height, and straight south of the point to connect, a short road on a
    /// fill a junction step and more over the ground (not a junction: off grade). The road to the trunk's nearest junction
    /// crosses it and is refused for it.</summary>
    private void TrunkWithAFillInTheWay()
    {
        float ground = WorldGenerator.instance!.GetHeight(0f, 0f);
        RoadSpatialGrid.Commit(Laid(new Vector2(-100f, 0f), new Vector2(100f, 0f), ground));
        RoadSpatialGrid.Commit(Laid(new Vector2(-6f, -20f), new Vector2(6f, -20f), ground + RoadPathfinder.DefaultJunctionStep * 5f));
        RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
    }

    private static bool HasStack(string line) => line.Contains("   at ") || line.Contains("Exception");

    [Fact]
    public void ARoadToTheFirstJunctionThatIsRefusedIsTriedAgainToTheNext()
    {
        TrunkWithAFillInTheWay();
        int appends = RoadSpatialGrid.AppendCount;
        var from = new Vector2(0f, -40f);

        // Without the join's tries again, the one road it plans is refused, and the refusal is said once, in one line.
        RoadNetworkGenerator.JoinRetries = 0;
        var refused = Run($"road_connect {from.x} {from.y}");
        Assert.Equal(ManualRoads.ConnectStarting(from), refused[0]);
        string said = Assert.Single(refused.Skip(1));
        Assert.StartsWith("Road not added: no road could be planned from 0, -40 to the network: ", said);
        Assert.EndsWith("Nothing added.", said);
        Assert.False(HasStack(said), said);
        Assert.Equal(appends, RoadSpatialGrid.AppendCount);

        // With the generator's own tries again, the next junction is reached clear of the fill.
        RoadNetworkGenerator.JoinRetries = _joinRetries;
        Assert.True(RoadNetworkGenerator.JoinRetries > 0);
        var plan = ManualRoads.Prepare(new[] { from }, true);
        Assert.False(plan.AlreadyConnected);
        Assert.NotEmpty(plan.Refused);
        Assert.True(plan.Refused.Count <= RoadNetworkGenerator.JoinRetries, $"{plan.Refused.Count} refused");
        Assert.True(Mathf.Abs(plan.End.x) > RoadNetworkGenerator.JoinRetryApart * 0.5f, $"ends at {plan.End}, the refused junction");
        ManualRoads.Commit(plan);
        Assert.Equal(appends + 1, RoadSpatialGrid.AppendCount);
    }

    [Fact]
    public void AnAddedRoadSaysItsLengthAndWhereItMeetsTheNetwork()
    {
        TrunkWithAFillInTheWay();
        var output = Run("road_connect 0 -40");
        Assert.Equal(ManualRoads.ConnectStarting(new Vector2(0f, -40f)), output[0]);
        Assert.Matches(@"^Road added: \d+ m from 0, -40 to the network at \(-?\d+, -?\d+\), after \d+ road\(s\) refused\.$", output.Last());
    }

    [Fact]
    public void TheStartLineIsPrintedBeforeTheSearchAndTheResultAfterItAsTheLastLine()
    {
        TrunkWithAFillInTheWay();
        var queued = new List<Action>();
        ManualRoadCommands.NextFrame = queued.Add;
        int appends = RoadSpatialGrid.AppendCount;
        var from = new Vector2(0f, -40f);

        var first = Run($"road_connect {from.x} {from.y}");
        Assert.Equal(new[] { ManualRoads.ConnectStarting(from) }, first);
        Assert.Single(queued);
        Assert.True(ManualRoadCommands.ConnectPending);
        Assert.Equal(appends, RoadSpatialGrid.AppendCount);

        // A second road_connect while one is pending is refused in one line and queues nothing.
        Assert.Equal(new[] { ManualRoadCommands.ConnectBusy }, Run("road_connect 10 -40"));
        Assert.Single(queued);

        int before = _console.Output.Count;
        queued[0]();
        var after = _console.Output.Skip(before).ToArray();
        Assert.False(ManualRoadCommands.ConnectPending);
        Assert.Equal(appends + 1, RoadSpatialGrid.AppendCount);
        Assert.StartsWith("Road added: ", after.Last());
        Assert.All(after, l => Assert.False(HasStack(l) || l.Contains("[Debug"), l));
    }

    [Fact]
    public void ARefusedDeferredSearchEndsWithOneRoadNotAddedLineAndFreesTheGuard()
    {
        // No road network on the island: the search is refused when it runs.
        var queued = new List<Action>();
        ManualRoadCommands.NextFrame = queued.Add;
        Assert.Single(Run("road_connect 80 80"));
        int before = _console.Output.Count;
        queued[0]();
        var after = _console.Output.Skip(before).ToArray();
        Assert.Equal("Road not added: No road network exists on this island.", Assert.Single(after));
        Assert.False(ManualRoadCommands.ConnectPending);
        Run("road_connect 80 80");
        Assert.Equal(2, queued.Count);
        queued[1]();
    }

    private const string Ruin = "TestRuin";
    private static readonly Vector2 RuinAt = new(80f, 80f);
    private const float RuinRadius = 10f;

    private void ARuin()
    {
        TestLocations.Add(ZoneSystem.instance!, new ZoneSystem.LocationInstance
        {
            m_location = new ZoneSystem.ZoneLocation { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(Ruin), m_exteriorRadius = RuinRadius },
            m_position = new Vector3(RuinAt.x, WorldGenerator.instance!.GetHeight(RuinAt.x, RuinAt.y), RuinAt.y),
        });
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(RuinAt, RuinRadius) });
    }

    [Fact]
    public void InsideAPlaceTheFirstRunWarnsAndTheSecondJoinsThePlace()
    {
        float ground = WorldGenerator.instance!.GetHeight(0f, 0f);
        RoadSpatialGrid.Commit(Laid(new Vector2(-40f, -16f), new Vector2(40f, -16f), ground));
        RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        ARuin();
        int appends = RoadSpatialGrid.AppendCount;
        // Inside the ruin's clearance, outside its footprint.
        var at = RuinAt + new Vector2(RuinRadius + RoadSiteProtection.Clearance(RoadNetworkGenerator.RoadWidth) * 0.5f, 0f);
        string line = FormattableString.Invariant($"road_connect {at.x} {at.y}");

        string warning = Assert.Single(Run(line));
        Assert.StartsWith("Not built yet: ", warning);
        Assert.Contains($"{Ruin} at (80, 80)", warning);
        Assert.Contains("Run the same command again", warning);
        Assert.Equal(appends, RoadSpatialGrid.AppendCount);

        var output = Run(line);
        Assert.Equal(ManualRoads.ConnectStarting(at), output[0]);
        Assert.StartsWith($"Road added: ", output[1]);
        Assert.Contains($"from {Ruin} at (80, 80) to the network", output[1]);
        Assert.Equal(appends + 1, RoadSpatialGrid.AppendCount);
        Assert.True(RoadNetworkGenerator.Reached(new Vector3(RuinAt.x, 0f, RuinAt.y), RuinRadius, Ruin), "the ruin is not served");

        // Built: a third run is the first again.
        Assert.StartsWith("Not built yet: ", Assert.Single(Run(line)));
    }

    /// <summary>A place joined by road_connect is arrived at as road: once the road is committed (not when it is only planned)
    /// that arrival is a way into the place (PlaceWaysIn.Record), as a generated road's is once stored, so a second way in
    /// up the same face is refused or joins it (read with the mod's corridor; the suite runs plain roads).</summary>
    [Fact]
    public void AJoinedPlacesRoadIsAWayIntoItOnceCommitted()
    {
        using var corridor = StaticOverride.Keep(() => RoadNetworkGenerator.RoadSnap).AndKeep(() => RoadNetworkGenerator.CorridorSnap);
        RoadNetworkGenerator.RoadSnap = 6f;
        RoadNetworkGenerator.CorridorSnap = 14f;
        PlaceWaysIn.Reset();
        try
        {
            float ground = WorldGenerator.instance!.GetHeight(0f, 0f);
            RoadSpatialGrid.Commit(Laid(new Vector2(-40f, -16f), new Vector2(40f, -16f), ground));
            RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
            ARuin();
            var at = RuinAt + new Vector2(RuinRadius + RoadSiteProtection.Clearance(RoadNetworkGenerator.RoadWidth) * 0.5f, 0f);
            string line = FormattableString.Invariant($"road_connect {at.x} {at.y}");
            Assert.StartsWith("Not built yet: ", Assert.Single(Run(line)));
            Assert.Empty(PlaceWaysIn.There(RuinAt));
            Assert.StartsWith("Road added: ", Run(line)[1]);
            PlaceWaysIn.Way way = Assert.Single(PlaceWaysIn.There(RuinAt));
            Assert.Null(way.Approach);
            Assert.Same(way, PlaceWaysIn.Joining(RuinAt, way, out _));
        }
        finally { PlaceWaysIn.Reset(); }
    }

    [Fact]
    public void AnotherPlaceOrALapsedWindowAsksAgain()
    {
        ARuin();
        var place = new ManualRoads.Place(Ruin, RuinAt, RuinRadius);
        var other = new ManualRoads.Place("OtherRuin", -RuinAt, RuinRadius);
        var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(ManualRoads.ConfirmPlace("host", place, now));
        Assert.False(ManualRoads.ConfirmPlace("host", other, now));
        Assert.False(ManualRoads.ConfirmPlace("host", place, now));
        Assert.False(ManualRoads.ConfirmPlace("console", place, now));
        Assert.False(ManualRoads.ConfirmPlace("host", place, now + ManualRoads.ConfirmWindow + TimeSpan.FromSeconds(1)));
        var asked = now + ManualRoads.ConfirmWindow + TimeSpan.FromSeconds(1);
        Assert.True(ManualRoads.ConfirmPlace("host", place, asked + ManualRoads.ConfirmWindow));
        // Kept while the window lasts: a run held back for the zones players changed is still from the place.
        Assert.True(ManualRoads.ConfirmPlace("host", place, asked + ManualRoads.ConfirmWindow));
        ManualRoads.ForgetPlace();
        Assert.False(ManualRoads.ConfirmPlace("host", place, asked));
    }

    [Fact]
    public void ARefusalIsOneLineAndADefectKeepsItsStack()
    {
        Exception Caught(Action a) { try { a(); } catch (Exception e) { return e; } throw new InvalidOperationException("nothing thrown"); }
        Assert.True(ManualRoads.IsRefusal(Caught(() => ManualRoads.Prepare(new[] { new Vector2(80f, 80f) }, true))));
        Assert.True(ManualRoads.IsRefusal(Caught(() => ManualRoadDraft.ParsePoint("x", "1"))));
        var list = new List<int> { 1, 2 };
        Assert.False(ManualRoads.IsRefusal(Caught(() => { foreach (int i in list) list.Add(i); })));
        Assert.False(ManualRoads.IsRefusal(Caught(() => _ = ((string?)null)!.Length)));

        Assert.Equal("Road not added: No road network exists on this island.", Run("road_connect 80 80").Last());
    }

    [Fact]
    public void ARefusalSaysTheMainReasonAndWhereWithTheTriesAndTheirReasons()
    {
        var from = new Vector2(10f, -20f);
        var refused = new List<(string why, Vector2? at)>
        {
            ("refused: " + RoadNetworkGenerator.EarthworkRefusal, new Vector2(51f, 7f)),
            ("refused: " + RoadNetworkGenerator.EarthworkRefusal, new Vector2(60f, 9f)),
            ("no route to the network", from),
        };
        Assert.Equal(FormattableString.Invariant(
            $"no road could be planned from 10, -20 to the network: {RoadNetworkGenerator.EarthworkRefusal} at (51, 7). Tried 3 roads: {RoadNetworkGenerator.EarthworkRefusal} (2), no route to the network. Nothing added."),
            ManualRoads.ConnectRefusal(from, refused));
        Assert.Equal("no road could be planned from 10, -20 to the network within the routing limits. Nothing added.",
            ManualRoads.ConnectRefusal(from, Array.Empty<(string, Vector2?)>()));
    }
}
