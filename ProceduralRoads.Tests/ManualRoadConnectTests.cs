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
/// road_connect is the generator's join (RoadNetworkGenerator.JoinTo, the one owner of how a location or a point is joined
/// to the network): a road to the first junction its search finds that is refused is tried again to the next, as the
/// generator tries it; inside a location it warns once and, run again, joins the location as the generator would; and what it
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

    private readonly ITestOutputHelper _output;

    public ManualRoadConnectTests(ITestOutputHelper output)
    {
        _output = output;
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld(); PlacedLocations.Reset(); StructureGround.Rebuild();
        RoadNetworkGenerator.MarkLocationsReady(); RoadNetworkGenerator.RoadWidth = RoadConstants.DefaultRoadWidth;
        RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
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
        RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld(); PlacedLocations.Reset(); StructureGround.Rebuild();
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

    private const string AlreadyConnected = "Already connected to a road on this island. Nothing added.";

    private enum Connection { AlreadyConnected, Built, Refused }

    private static Connection Said(string line) =>
        line == AlreadyConnected ? Connection.AlreadyConnected
        : line.StartsWith("Road added: ") ? Connection.Built
        : line.StartsWith("Road not added: ") ? Connection.Refused
        : throw new InvalidOperationException($"not a road_connect result: {line}");

    /// <summary>The shared join (RoadNetworkGenerator.JoinTo) from a plain point, planned and not stored, as road_connect
    /// plans it: already served, a road to the network, or refused.</summary>
    private static Connection ThroughTheSharedJoin(Vector2 mark)
    {
        var world = WorldGenerator.instance!;
        var finder = RoadNetworkGenerator.NewFinder(world, RoadLocationProtection.Clearance(RoadNetworkGenerator.RoadWidth));
        var hints = RoadNetworkGenerator.JoinTargets(_ => true);
        var capture = new RoadNetworkGenerator.RoadCapture();
        bool joined;
        using (RoadNetworkGenerator.PlanningWith(finder, capture))
            joined = RoadNetworkGenerator.JoinTo(finder, mark, hints, "road_connect", new Vector3(mark.x, 0f, mark.y), 0f, null, null, null);
        return !joined ? Connection.Refused : capture.Road == null ? Connection.AlreadyConnected : Connection.Built;
    }

    /// <summary>
    /// A mound under a pier (StructureGround.Mound) is ground, not network: a mark on one, a step from its top, is not
    /// served by it (RoadNetworkGenerator.Reached, road_connect's test), nor is a location or a junction point there reached by
    /// it (the automatic reachability), nor is it a junction a road's search may join (RoadSpatialGrid.TryGetRoadWithin,
    /// TryGetRoadAtGradeWithin) or a join target (RoadNetworkGenerator.JoinTargets); road_connect does not say already
    /// connected. Before, the mound's points were road a mark at a pier's foot was "already connected" by.
    /// </summary>
    [Fact]
    public void AMoundIsNoNetworkToConnectTo()
    {
        float ground = WorldGenerator.instance!.GetHeight(0f, 60f);
        var mark = new Vector2(0f, 60f);
        var crossing = new RoadNetworkGenerator.CrossingGround();
        crossing.Mounds.Add(new StructureGround.Mound(new List<Vector2> { mark + new Vector2(-1f, 0f), mark, mark + new Vector2(1f, 0f) }, ground + 0.1f));
        crossing.CommitGround();
        RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        Assert.NotEmpty(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(mark.x, 0f, mark.y), 0.01f));

        Assert.False(RoadNetworkGenerator.Reached(mark, null), "the mark is served by the mound");
        Assert.False(RoadNetworkGenerator.Reached(new Vector3(mark.x, 0f, mark.y), 0f), "a point on the mound is reached by it");
        Assert.False(RoadNetworkGenerator.Reached(new Vector3(mark.x, 0f, mark.y + 6f), 4f), "a location by the mound is reached by it");
        Assert.False(RoadSpatialGrid.TryGetRoadWithin(mark, 4f, out _), "the mound is a junction");
        Assert.False(RoadSpatialGrid.TryGetRoadAtGradeWithin(mark, 4f, ground, RoadPathfinder.JunctionStep, out _), "the mound is a junction at grade");
        Assert.Empty(RoadNetworkGenerator.JoinTargets(_ => true));
        Assert.NotEqual(Connection.AlreadyConnected, ThroughTheSharedJoin(mark));
    }

    /// <summary>
    /// road_connect says already connected only where the mark walks onto the road: a road on it or within one path cell
    /// past its edge (no shorter road can be built), whose surface is a step from the marked ground; beside an at-grade road
    /// just off its edge too. Beside a road raised or cut two junction steps off that ground, or one junction step off (a
    /// junction the search may plan at grade, not a step a player walks), it builds a road or refuses with its reason, and
    /// never says already connected. The marked ground is held by a saved structure (StructureGround.Hold): otherwise the
    /// road's levelling could grade it to the road. The terminal command and the shared join give the same verdict.
    /// </summary>
    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(2f, 1f)]
    [InlineData(-2f, 1f)]
    [InlineData(1f, 1f)]
    [InlineData(0f, 3f)]
    public void RoadConnectIsAlreadyConnectedOnlyWhereTheMarkWalksOntoTheRoad(float junctionSteps, float markZ)
    {
        var world = WorldGenerator.instance!;
        float natural = world.GetHeight(0f, 0f);
        float surface = natural + junctionSteps * RoadPathfinder.JunctionStep;
        var mark = new Vector2(0f, markZ);
        RoadSpatialGrid.Commit(Laid(new Vector2(-12f, 0f), new Vector2(12f, 0f), surface));
        RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        StructureGround.Hold(new[] { new GroundPin(0, Mathf.RoundToInt(markZ), natural) });

        float ground = StructureGround.At(world)(mark);
        Assert.Equal(natural, ground);
        float reach = RoadNetworkGenerator.RoadWidth / 2f + RoadNetworkGenerator.ReachPastEdge;
        bool near = RoadSpatialGrid.TryGetRoadHeightWithin(mark, reach, out float nearest);
        Assert.Equal(markZ <= reach, near);
        bool walks = near && StairLayout.Walkable(ground, nearest);
        Assert.Equal(junctionSteps == 0f && near, walks);
        // The search's own candidate test, at the mark and at the first cell: a junction it may plan, not proof of a walk.
        Assert.Equal(Mathf.Abs(junctionSteps) <= 1f, RoadSpatialGrid.TryGetRoadAtGradeWithin(mark, RoadPathfinder.CellSize,
            RoadSpatialGrid.JunctionGround(mark, world), RoadPathfinder.JunctionStep, out _));

        Connection shared = ThroughTheSharedJoin(mark);
        int appends = RoadSpatialGrid.AppendCount;
        string[] output = Run(FormattableString.Invariant($"road_connect {mark.x} {mark.y}"));
        Connection said = Said(output.Last());
        _output.WriteLine($"shared join: {shared}; road_connect: {string.Join(" | ", output)}");
        if (walks)
        {
            Assert.Equal(Connection.AlreadyConnected, said);
            Assert.Equal(appends, RoadSpatialGrid.AppendCount);
        }
        else
        {
            Assert.NotEqual(Connection.AlreadyConnected, said);
            if (said == Connection.Refused)
            {
                Assert.EndsWith("Nothing added.", output.Last());
                Assert.Equal(appends, RoadSpatialGrid.AppendCount);
            }
            else
            {
                // Built: one road, and from the mark it now walks onto the network.
                Assert.Equal(appends + 1, RoadSpatialGrid.AppendCount);
                Assert.Equal(AlreadyConnected, Run(FormattableString.Invariant($"road_connect {mark.x} {mark.y}")).Last());
                Assert.Equal(appends + 1, RoadSpatialGrid.AppendCount);
            }
        }
        Assert.Equal(shared, said);
    }

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
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(RuinAt, RuinRadius) });
    }

    [Fact]
    public void ACustomLocationsCentreOnlyArrivalKeepsItsStandOffAfterLoading()
    {
        using var height = StaticOverride.Keep(() => LocationLevelling.PlacementHeightSource);
        LocationLevelling.PlacementHeightSource = c => c == RuinAt ? 48f : null;
        RoadNetworkGenerator.RegisterLocation(Ruin);
        try
        {
            ARuin();
            RoadNetworkGenerator.NoteCustomLocation(Ruin, new Vector3(RuinAt.x, 0f, RuinAt.y));
            Vector2 toward = RuinAt + new Vector2(40f, 0f);
            float expected = RoadNetworkGenerator.OffLevelStandOff(RuinAt, RuinRadius, toward);
            Assert.True(expected > 0f);
            RoadSpatialGrid.FinalizeRoadNetwork();
            RoadNetworkPersistence.EnsureMetadataInstance();
            RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), new HashSet<Vector2s>());
            RoadNetworkGenerator.Reset();
            Assert.Equal(0f, RoadNetworkGenerator.OffLevelStandOff(RuinAt, RuinRadius, toward));
            Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData());
            Assert.Equal(expected, RoadNetworkGenerator.OffLevelStandOff(RuinAt, RuinRadius, toward));
        }
        finally { RoadNetworkGenerator.UnregisterLocation(Ruin); }
    }

    [Fact]
    public void ALocationReachedOffLevelStaysConnectedAfterSavingAndLoading()
    {
        ARuin();
        float ring = RoadNetworkGenerator.KeepOutEdge(RuinAt, RuinRadius, RoadNetworkGenerator.RoadWidth);
        Vector2 end = RuinAt + new Vector2(ring, 0f);
        float under = WorldGenerator.instance!.GetHeight(end.x, end.y) - 2f;
        RoadSpatialGrid.Commit(Laid(end, end + new Vector2(40f, 0f), under));
        RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        Assert.False(RoadNetworkGenerator.Reached(RuinAt, RuinRadius, Ruin));
        Assert.True(RoadNetworkGenerator.ReachOffLevel(Ruin, new Vector3(RuinAt.x, 0f, RuinAt.y), RuinRadius, () => false));
        string line = FormattableString.Invariant($"road_connect {RuinAt.x} {RuinAt.y}");
        string connected = $"{Ruin} at (80, 80) is already served by a road. Nothing added.";
        Assert.StartsWith("Not built yet: ", Assert.Single(Run(line)));
        Assert.Equal(connected, Run(line).Last());

        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), new HashSet<Vector2s>());
        RoadNetworkGenerator.Reset();
        Assert.True(RoadNetworkGenerator.HeightNeeded(RuinAt));
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData());
        RoadNetworkGenerator.MarkRoadsLoadedFromZDO(); RoadNetworkGenerator.MarkLocationsReady();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(RuinAt, RuinRadius) });

        Assert.True(RoadNetworkGenerator.Reached(RuinAt, RuinRadius, Ruin));
        Assert.StartsWith("Not built yet: ", Assert.Single(Run(line)));
        Assert.Equal(connected, Run(line).Last());
        Assert.Equal(0, RoadSpatialGrid.AppendCount);
    }

    [Fact]
    public void InsideALocationTheFirstRunWarnsAndTheSecondJoinsTheLocation()
    {
        float ground = WorldGenerator.instance!.GetHeight(0f, 0f);
        RoadSpatialGrid.Commit(Laid(new Vector2(-40f, -16f), new Vector2(40f, -16f), ground));
        RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        ARuin();
        int appends = RoadSpatialGrid.AppendCount;
        // Inside the ruin's clearance, outside its footprint.
        var at = RuinAt + new Vector2(RuinRadius + RoadLocationProtection.Clearance(RoadNetworkGenerator.RoadWidth) * 0.5f, 0f);
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

    /// <summary>A location joined by road_connect is arrived at as road: once the road is committed (not when it is only planned)
    /// that arrival is a way into the location (LocationWaysIn.Record), as a generated road's is once stored, so a second way in
    /// up the same face is refused or joins it (read with the mod's corridor; the suite runs plain roads).</summary>
    [Fact]
    public void AJoinedLocationsRoadIsAWayIntoItOnceCommitted()
    {
        using var corridor = StaticOverride.Keep(() => RoadNetworkGenerator.RoadSnap).AndKeep(() => RoadNetworkGenerator.CorridorSnap);
        RoadNetworkGenerator.RoadSnap = 6f;
        RoadNetworkGenerator.CorridorSnap = 14f;
        LocationWaysIn.Reset();
        try
        {
            float ground = WorldGenerator.instance!.GetHeight(0f, 0f);
            RoadSpatialGrid.Commit(Laid(new Vector2(-40f, -16f), new Vector2(40f, -16f), ground));
            RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
            ARuin();
            var at = RuinAt + new Vector2(RuinRadius + RoadLocationProtection.Clearance(RoadNetworkGenerator.RoadWidth) * 0.5f, 0f);
            string line = FormattableString.Invariant($"road_connect {at.x} {at.y}");
            Assert.StartsWith("Not built yet: ", Assert.Single(Run(line)));
            Assert.Empty(LocationWaysIn.There(RuinAt));
            Assert.StartsWith("Road added: ", Run(line)[1]);
            LocationWaysIn.Way way = Assert.Single(LocationWaysIn.There(RuinAt));
            Assert.Null(way.Approach);
            Assert.Same(way, LocationWaysIn.Joining(RuinAt, way, out _));
        }
        finally { LocationWaysIn.Reset(); }
    }

    [Fact]
    public void AnotherLocationOrALapsedWindowAsksAgain()
    {
        ARuin();
        var location = new ManualRoads.Location(Ruin, RuinAt, RuinRadius);
        var other = new ManualRoads.Location("OtherRuin", -RuinAt, RuinRadius);
        var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(ManualRoads.ConfirmLocation("host", location, now));
        Assert.False(ManualRoads.ConfirmLocation("host", other, now));
        Assert.False(ManualRoads.ConfirmLocation("host", location, now));
        Assert.False(ManualRoads.ConfirmLocation("console", location, now));
        Assert.False(ManualRoads.ConfirmLocation("host", location, now + ManualRoads.ConfirmWindow + TimeSpan.FromSeconds(1)));
        var asked = now + ManualRoads.ConfirmWindow + TimeSpan.FromSeconds(1);
        Assert.True(ManualRoads.ConfirmLocation("host", location, asked + ManualRoads.ConfirmWindow));
        // Kept while the window lasts: a run held back for the zones players changed is still from the location.
        Assert.True(ManualRoads.ConfirmLocation("host", location, asked + ManualRoads.ConfirmWindow));
        ManualRoads.ForgetLocation();
        Assert.False(ManualRoads.ConfirmLocation("host", location, asked));
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
