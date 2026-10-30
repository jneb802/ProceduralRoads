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

    private readonly ValheimWorldScope _scope = new ValheimWorldScope();

    public ManualRoadPlayerZoneTests()
    {
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld(); NetworkCutoff.Forget(); ManualRoads.Reset();
        _scope.WithWorld(new FlatWorld()).WithZdos().WithZoneSystem(); RoadNetworkGenerator.MarkLocationsReady();
        RoadGrade.Configured = .35f;
        RoadNetworkGenerator.RoadWidth = 4;
        RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>());
        // A network far away (along z = -400), generated on this world.
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(-40f, -400f), new(40f, -400f) }, 4f, WorldGenerator.instance!);
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        NetworkCutoff.SnapshotWorld(Array.Empty<Vector2s>());
        NetworkCutoff.UseNewNetwork();
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
        NetworkCutoff.UseNewNetwork();

        HashSet<Vector2s> changed = PlayerChanges.Zones(null, NetworkCutoff.Excludes);
        Assert.Contains(dugExplored, changed);
        Assert.DoesNotContain(networkZone, changed);
        Assert.Contains(Base, changed);
    }
}
