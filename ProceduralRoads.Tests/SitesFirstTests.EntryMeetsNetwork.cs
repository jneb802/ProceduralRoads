using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

public partial class SitesFirstTests
{
    /// <summary>The way onto a placed place's landing is part of its road: it may not cross another road. Here a short road
    /// lies across the cave's front, between the ring and its landing; searched to every door at once, the road may come in
    /// where its way onto the landing does not cross it, and built or not, nothing of it is laid across that road.</summary>
    [Fact]
    public void AWayOntoALandingFromADoorDoesNotCrossAnotherRoad()
    {
        PlacedSite cave = Cave();
        PlacedSites.Ready(cave);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
        float x = cave.Shape.Landing.x + 14f;
        var across = new List<Vector2>();
        for (float z = -14f; z <= 14f; z += 1f) across.Add(new Vector2(x, z));
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(across, across.ConvertAll(_ => 50f), across.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 28f, false));
        SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        int OnTheOther() => RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(x, 0f, 0f), 20f).Count(q => q.IsRoad && Mathf.Abs(q.p.x - x) < 2f && Mathf.Abs(q.p.y) <= 16f);
        int before = OnTheOther();
        RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the cave");
        Assert.Equal(before, OnTheOther());
    }
}
