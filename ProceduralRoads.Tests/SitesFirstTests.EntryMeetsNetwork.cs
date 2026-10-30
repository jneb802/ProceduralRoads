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
    /// lies across the cave's front, between the ring and its landing; every way onto the landing crosses it, and the road is
    /// refused, where it was built across it.</summary>
    [Fact]
    public void AWayOntoALandingThatCrossesAnotherRoadIsNotBuilt()
    {
        PlacedSite cave = Cave();
        PlacedSites.Ready(cave);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
        Vector2 landing = cave.Shape.Landing;
        float x = landing.x + 14f;
        var across = new List<Vector2>();
        for (float z = -14f; z <= 14f; z += 1f) across.Add(new Vector2(x, z));
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(across, across.ConvertAll(_ => 50f), across.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 28f, false));
        SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });

        bool built = RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the cave");

        string dbg = string.Join(" ", RoadSpatialGrid.GetRoadPointsNearPosition(Vector3.zero, 60f).Where(q => q.IsRoad && Mathf.Abs(q.p.x - x) > 0.5f).Select(q => $"({q.p.x:F0},{q.p.y:F0})").Distinct().Take(80))
            + " stairs " + string.Join(" ", StairStructures.Runs.SelectMany(r => r.Pieces).Select(q => $"({q.Position.x:F0},{q.Position.z:F0})").Distinct().Take(40));
        Assert.False(built, $"built onto the landing at {landing} across the road at x {x:F1}: {dbg}");
        Assert.Equal("no way onto a placed place's landing", RoadSpatialGrid.LastRefusal);
    }
}
