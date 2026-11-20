using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A manual road (road_connect, road_path, road_mark build) goes round every zone players changed (something a player
/// built, or a terrain edit where the network left the zone as it is), keeping its levelling out of them. The operator
/// can still build into one: a mark in it (road_connect's own position counts) builds through that zone once the same
/// command is run a second time; force builds through anything at once.
/// </summary>
public sealed class ManualRoadPlayerZoneTests : IDisposable
{
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0; width = 0; }
    }

    /// <summary>x 32 to 96, z -32 to 32: right across the line from (-16, 0) to (176, 0). A player's workbench stands in it.</summary>
    private static readonly Vector2s Base = new(1, 0);

    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithNetwork(server: true);

    public ManualRoadPlayerZoneTests()
    {
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld(); NetworkCutoff.Forget(); ManualRoads.Reset();
        _scope.WithWorld(new FlatWorld()).WithZdos().WithZoneSystem(); RoadNetworkGenerator.MarkLocationsReady();
        RoadGrade.Configured = RoadConstants.DefaultMaxRoadGrade;
        RoadNetworkGenerator.RoadWidth = RoadConstants.DefaultRoadWidth;
        RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
        // A network far away (along z = -400), generated on this world.
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(-40f, -400f), new(40f, -400f) }, 4f, WorldGenerator.instance!);
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        NetworkCutoff.SnapshotWorld(Array.Empty<Vector2s>());
        NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
        ZDO bench = ZDOMan.instance!.CreateNewZDO(ZoneSystem.GetZonePos(Base) + new Vector3(10f, 0f, 2f), "piece_workbench".GetStableHashCode());
        bench.Set(ZDOVars.s_creator, 1234L);
    }

    public void Dispose()
    {
        ManualRoads.Reset(); RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld(); NetworkCutoff.Forget();
        _scope.Dispose();
    }

    private static Island Island()
    {
        var i = new Island { Id = 1, Min = new Vector2(-1024, -1024), Max = new Vector2(1024, 1024), CellSize = 8, WorldOffset = 0 };
        for (int x = -128; x <= 128; x++) for (int z = -128; z <= 128; z++) i.Cells.Add(new Vector2Int(x, z));
        return i;
    }

    private static bool Touches(ManualRoads.Plan plan, Vector2s zone) =>
        ManualRoads.ZonesReached(plan.Paths, plan.Crossings).Contains(zone);

    [Fact]
    public void ByDefaultTheRoadGoesRoundAZonePlayersChanged()
    {
        var plan = ManualRoads.Prepare(new[] { new Vector2(-16f, 0f), new Vector2(176f, 0f) }, false, new[] { Island() });
        Assert.False(Touches(plan, Base), "the road or its levelling reaches the player's base");
        Assert.Empty(plan.PlayerZones);
        Assert.True(ManualRoads.ConfirmPlayerZones("host", "path", plan, DateTime.UtcNow), "nothing to confirm");
        Assert.False(NetworkCutoff.AvoidsAny, "the avoidance outlived the planning");
    }

    /// <summary>
    /// A road past a protected zone keeps its whole footprint out of it, not only its centreline, generated or manual alike:
    /// the terrain writer gathers none of its points for that zone (RoadSpatialGrid.GetRoadPointsInZone, the writer's own
    /// reach). The same zone and the same ends, 8 m beside its border (z = 40): for the manual road a zone players changed
    /// (the workbench), for the generated one a zone explored before the network (NetworkCutoff.Excludes). Before, the
    /// manual road kept the writer's reach and the generated one only its centreline: its levelling reached 12 m, into it.
    /// </summary>
    [Fact]
    public void AGeneratedRoadAndAManualRoadKeepTheirWholeFootprintOutOfAProtectedZoneAlike()
    {
        var from = new Vector2(-16f, 40f);
        var to = new Vector2(176f, 40f);
        ManualRoads.Commit(ManualRoads.Prepare(new[] { from, to }, false, new[] { Island() }));
        Assert.True(RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(1, 1)).Count > 0, "the manual road was not laid past the zone");
        Assert.True(RoadSpatialGrid.GetRoadPointsInZone(Base).Count == 0, "manual: the writer gathers road points for the protected zone");

        RoadSpatialGrid.Clear();
        NetworkCutoff.SnapshotWorld(new[] { Base });
        NetworkCutoff.BeginNewNetwork();
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, new RoadPathfinder(WorldGenerator.instance!));
        try { Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(from.x, 0f, from.y), 0f, new Vector3(to.x, 0f, to.y), 0f, 4f, "past")); }
        finally
        {
            typeof(RoadNetworkGenerator).GetField("m_pathfinder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, null);
            NetworkCutoff.UseNewNetwork();
        }
        Assert.True(RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(1, 1)).Count > 0, "the generated road was not laid past the zone");
        Assert.True(RoadSpatialGrid.GetRoadPointsInZone(Base).Count == 0, "generated: the writer gathers road points for the protected zone");
    }

    /// <summary>
    /// road_connect to a network that runs 12 m from a zone players changed (the workbench's, east of x = 32): the search keeps
    /// its own road the levelling's reach from that zone, and the end it carries onto the network's centreline reaches into
    /// it, so every road planned writes into it (the one list's last check, RoadNetworkGenerator.RoadRefusal) and none is
    /// built. The operator is told which zone and how to build into it (a mark in it, or force), as a marked road is.
    /// Before, the refusal said only that it would write into a zone the network leaves as it is.
    /// </summary>
    [Fact]
    public void ARoadConnectRefusedForAZonePlayersChangedNamesTheZoneAndForce()
    {
        RoadSpatialGrid.Clear();
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(20f, -30f), new(20f, 30f) }, 4f, WorldGenerator.instance!);
        RoadSpatialGrid.FinalizeRoadNetwork();
        var from = new Vector2(-40f, 0f);
        var refused = Assert.Throws<InvalidOperationException>(() => ManualRoads.Prepare(new[] { from }, true, new[] { Island() }));
        Assert.True(refused.Message.StartsWith("no road could be planned from -40, 0 to the network", StringComparison.Ordinal), refused.Message);
        // The marked road's words, as before the one list of refusals (decision 6a) took the check over.
        Assert.True(refused.Message.EndsWith(" The road would reach zone(s) players changed (buildings or terrain edits): (1, 0). " +
            "Mark a point in a zone to build into it, or add force. Nothing added.", StringComparison.Ordinal), refused.Message);
        // Forced, it is built through the zone.
        Assert.Contains(Base, ManualRoads.Prepare(new[] { from }, true, new[] { Island() }, force: true).PlayerZones);
    }

    [Fact]
    public void AMarkInAChangedZoneBuildsThereOnTheSecondRun()
    {
        var points = new[] { new Vector2(-16f, 0f), new Vector2(64f, 0f) };
        var plan = ManualRoads.Prepare(points, false, new[] { Island() });
        Assert.Equal(new[] { Base }, plan.PlayerZones);
        Assert.True(Touches(plan, Base));

        DateTime now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(ManualRoads.ConfirmPlayerZones("host", "path", plan, now), "built into a player's zone without a warning");
        string warning = ManualRoads.PlayerZonesWarning(plan.PlayerZones, built: false);
        Assert.Contains("(1, 0)", warning);
        Assert.Contains("again", warning);
        // Another actor's run is not this one's confirmation.
        Assert.False(ManualRoads.ConfirmPlayerZones("console", "path", plan, now.AddSeconds(5)));
        Assert.False(ManualRoads.ConfirmPlayerZones("host", "path", plan, now.AddSeconds(10)), "the warning was replaced, so this is a first run again");
        Assert.True(ManualRoads.ConfirmPlayerZones("host", "path", ManualRoads.Prepare(points, false, new[] { Island() }), now.AddSeconds(20)));
        // Used up: a third run warns again.
        Assert.False(ManualRoads.ConfirmPlayerZones("host", "path", plan, now.AddSeconds(30)));
        // Too late: past the window it is a first run.
        Assert.False(ManualRoads.ConfirmPlayerZones("host", "path", plan, now.AddSeconds(30) + ManualRoads.ConfirmWindow + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ForceBuildsThroughAtOnceAndSaysWhere()
    {
        var plan = ManualRoads.Prepare(new[] { new Vector2(-16f, 0f), new Vector2(176f, 0f) }, false, new[] { Island() }, force: true);
        Assert.True(plan.Forced);
        Assert.Contains(Base, plan.PlayerZones);
        Assert.True(ManualRoads.ConfirmPlayerZones("host", "path", plan, DateTime.UtcNow));
        ManualRoads.Commit(plan);
        Assert.Contains("(1, 0)", ManualRoads.PlayerZonesWarning(plan.PlayerZones, built: true));
    }

    [Fact]
    public void ATerrainEditCountsOnlyWhereTheNetworkLeftTheZoneAsItIs()
    {
        var dugExplored = new Vector2s(5, 5);
        var networkZone = new Vector2s(-5, -5);
        int compiler = "_TerrainCompiler".GetStableHashCode();
        ZDOMan.instance!.CreateNewZDO(ZoneSystem.GetZonePos(dugExplored), compiler);
        ZDOMan.instance.CreateNewZDO(ZoneSystem.GetZonePos(networkZone), compiler);
        NetworkCutoff.Forget();
        NetworkCutoff.SnapshotWorld(new[] { dugExplored });
        NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();

        HashSet<Vector2s> changed = PlayerChanges.Zones(null, NetworkCutoff.Excludes);
        Assert.Contains(dugExplored, changed);
        Assert.DoesNotContain(networkZone, changed);
        Assert.Contains(Base, changed);
    }
}
