using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The preload-list rule (RoadLocationProtection.Enterable, from PlacedLocations.Preloaded): a location on the list (placed before its
/// roads, its ground published by the preload) may be entered by its road and its structures; a chosen location not on it gets a
/// road that stops at its edge. One owner, every question a path asks of a target's footprint: the ring a road is trimmed
/// at (RoadNetworkGenerator.KeepOutEdge), the final check over a shaped line (RoadLocationProtection.BlocksRoad, which an
/// approach trial and a marked road ask too), a structure's line and pieces (RoadLocationProtection.BlocksSegment,
/// RoadLocationProtection.StructureEnters, NetworkGround.StructureRefusal) and the location a stored point is written into
/// (RoadLocationProtection.Targets.Holding, Location: generation's commit and a manual road's append alike).
/// </summary>
public class PreloadListTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private static readonly Vector2 Centre = new(0f, 0f);
    /// <summary>Its footprint 20 m round, paint included; the ground it shapes 12 m.</summary>
    private const float Radius = 20f, Arrival = 12f, Width = 4f;

    private sealed class Flat : WorldGenerator { public override float GetHeight(float x, float z) => 40f; }

    public PreloadListTests()
    {
        _scope.WithWorld(new Flat());
        PlacedLocations.Reset();
        RoadSpatialGrid.Clear();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(Centre, Radius, Arrival) });
    }

    public void Dispose()
    {
        PlacedLocations.Reset();
        RoadLocationProtection.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    private static readonly RoadLocationProtection.Targets Arriving = new(null, Centre);

    /// <summary>Each question the paths ask, for the location off and on the list.</summary>
    private static (float ring, bool holds, bool location, bool endInPaint, bool structureIn, bool lineIn) Answers()
    {
        float ring = RoadNetworkGenerator.KeepOutEdge(Centre, 10f, Width);
        Vector2? holding = Arriving.Holding(new Vector2(0f, 15f));
        Vector2? location = Arriving.Location;
        // A shaped line ending over the paint the location does not shape (between Arrival and Radius).
        Vector2 end = new(0f, Arrival + RoadLocationProtection.Clearance(Width));
        bool endInPaint = !RoadLocationProtection.BlocksRoad(new Vector2(0f, 60f), end, Width, new Vector2(0f, 60f), end, Arriving);
        var piece = new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = "stone_stair", Position = new Vector3(0f, 40f, 15f) };
        bool structureIn = NetworkGround.StructureRefusal(new[] { piece }, new[] { Arriving }, out _) == null;
        bool lineIn = !RoadLocationProtection.BlocksSegment(new Vector2(0f, 40f), new Vector2(0f, 15f), 1f, Arriving);
        return (ring, holding.HasValue, location.HasValue, endInPaint, structureIn, lineIn);
    }

    [Fact]
    public void ALocationNotOnTheListIsStoppedAtItsEdgeByEveryQuestionAndOneOnItIsEntered()
    {
        var off = Answers();
        Assert.Equal(Radius + RoadLocationProtection.Clearance(Width), off.ring, 3);
        Assert.False(off.holds, "a point inside a location not on the list is stored into it");
        Assert.False(off.location, "a road carried on is stored into a location not on the list");
        Assert.False(off.endInPaint, "the final check lets a road end inside a location not on the list");
        Assert.False(off.structureIn, "a structure is let into a location not on the list");
        Assert.False(off.lineIn, "a structure's line is let into a location not on the list");

        PlacedLocations.Preload(Centre, (x, z) => 40f);
        Assert.True(PlacedLocations.Preloaded(Centre));
        var on = Answers();
        Assert.Equal(Arrival + RoadLocationProtection.Clearance(Width), on.ring, 3);
        Assert.True(on.holds && on.location && on.endInPaint && on.structureIn && on.lineIn,
            $"on the list: holds {on.holds}, location {on.location}, end in its paint {on.endInPaint}, structure {on.structureIn}, line {on.lineIn}");
    }

    /// <summary>A marked road's append (RoadSpatialGrid.CommitAppend) stores its points as generation's commit does: into a
    /// location on the list, into none not on it, so the terrain writer leaves that location's ground alone
    /// (RoadLocationProtection.Admits).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMarkedRoadIsStoredIntoALocationOnlyWhereItIsOnTheList(bool onTheList)
    {
        if (onTheList) PlacedLocations.Preload(Centre, (x, z) => 40f);
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(300f, 300f), new(340f, 300f) }, Width, WorldGenerator.instance);
        RoadSpatialGrid.FinalizeRoadNetwork();
        var points = new List<Vector2> { new(0f, 40f), new(0f, 30f), new(0f, 19f), new(0f, 15f) };
        var plan = new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => 40f), points.ConvertAll(_ => default(RoadPointDebugInfo)), Width, 25f, false)
            { Locations = Arriving };
        RoadSpatialGrid.CommitAppend(new[] { plan }, RoadSpatialGrid.RoadNetworkVersion);
        var inside = RoadSpatialGrid.SnapshotAllRoadPoints().Where(p => p.addition == 1 && p.p.magnitude <= Radius).ToList();
        Assert.NotEmpty(inside);
        Assert.All(inside, p => Assert.Equal(onTheList, p.intoLocation && p.location == Centre));
    }
}
