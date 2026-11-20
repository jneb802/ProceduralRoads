using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A way onto a placed Location's landing planned as the road's own last stretch changes the road only from its last structure on
/// (RoadNetworkGenerator.PlanWayIn): everything before that structure stays as it was planned. Offline, planned again whole, a
/// crypt's road was refused for a trench at a bridge's end 370 m from the crypt, another for a protected location crossed far off.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>The height of a hump the far part of the road climbs over on the ground (a tent <see cref="HumpHalfWidth"/> m either side
    /// of its crest), and where its crest is.</summary>
    private const float HumpHeight = 2f, HumpHalfWidth = 8f, HumpAt = -260f;

    /// <summary>The crypt's dirt everywhere, but for the hump across the road's far part.</summary>
    private static float GroundWithAFarHump(float x, float z) => CryptDirt + HumpHeight * Mathf.Clamp01(1f - Mathf.Abs(z - HumpAt) / HumpHalfWidth);

    /// <summary>A piece of road along the z axis from <paramref name="from"/> to <paramref name="to"/>, a point a metre, on the ground.</summary>
    private static RoadSpatialGrid.PlannedPath OnTheGround(float from, float to)
    {
        var points = Enumerable.Range(0, Mathf.RoundToInt(to - from) + 1).Select(k => new Vector2(0f, from + k)).ToList();
        return new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(p => GroundWithAFarHump(p.x, p.y)), points.ConvertAll(_ => default(RoadPointDebugInfo)),
            RoadNetworkGenerator.RoadWidth, points.Count - 1, false);
    }

    /// <summary>A road from far off in front of a crypt to its ring end 22 m out, over a hump on the ground far off and up a staircase
    /// half way: its far part follows the hump on the ground as it was planned (no fill, no cut), planned again it would be smoothed
    /// through it. The way in changes the road only past its staircase: the far part is the very road it was, every point and every
    /// height, and the road with its way in has no ridge nor trench.</summary>
    [Fact]
    public void AWayInChangesARoadOnlyPastItsLastStructure()
    {
        PlacedLocation crypt = SunkenCryptInASwamp(GroundWithAFarHump, Heightmap.Biome.Meadows);
        float cell = RoadPathfinder.CellSize;
        var path = Enumerable.Range(0, 41).Select(k => new Vector2(0f, -22f - cell * (40 - k))).ToList();
        var stair = new RoadStairSpan
        {
            FromIndex = 20, ToIndex = 21, FromPoint = path[20], ToPoint = path[21], FromHeight = CryptDirt, ToHeight = CryptDirt,
        };
        RoadSpatialGrid.PlannedPath far = OnTheGround(path[0].y, path[20].y), near = OnTheGround(path[21].y, path[40].y);
        far.StairAtEnd = (stair, false);
        near.StairAtStart = (stair, true);
        var farPoints = new List<Vector2>(far.Points);
        var farHeights = new List<float>(far.Heights);
        var plans = new List<RoadSpatialGrid.PlannedPath> { far, near };
        var stairs = new List<RoadStairSpan> { stair };

        bool entered = RoadNetworkGenerator.PlanEntry(crypt, false, path, plans, stairs, new List<RoadCrossing>(), RoadNetworkGenerator.RoadWidth,
            out RoadSpatialGrid.PlannedPath? road, out RoadStairSpan? span, out string why);

        Assert.True(entered && road != null && span == null, $"entered {entered}, staircase {span != null}: {why}");
        RoadSpatialGrid.PlannedPath kept = plans[0];
        Assert.Equal(farPoints, kept.Points);
        for (int i = 0; i < farHeights.Count; i++)
            Assert.True(kept.Heights[i] == farHeights[i],
                FormattableString.Invariant($"at ({kept.Points[i].x:F1}, {kept.Points[i].y:F1}) the far part stands at {kept.Heights[i]:F2}, planned at {farHeights[i]:F2}"));
        Assert.Null(RoadNetworkGenerator.AlongARidge(RoadNetworkGenerator.InTravelOrder(plans, new[] { road! }), new List<RoadCrossing>(), out Vector2 at, out string ridge));
        Assert.Equal(20, stair.FromIndex);
    }
}
