using UnityEngine;
using Xunit;
namespace ProceduralRoads.Tests;

/// <summary>Exact paint: distance from the road point, not the snapped vertex.</summary>
public class PaintDistanceTests
{
    [Fact]
    public void SnappedDistanceIgnoresWhereThePointReallyIs()
    {
        // A road point 0.5 m off its snapped vertex: the vertex 2 m out on the
        // far side is really 2.5 m away, past the 1.7 m paint edge of a 4 m road.
        var point = new Vector2(0.5f, 0f);
        var vertex = new Vector2(-2f, 0f);
        float snapped = RoadTerrainModifier.PaintDistance(vertex, point, -2, 0, 1f, exact: false);
        float exact = RoadTerrainModifier.PaintDistance(vertex, point, -2, 0, 1f, exact: true);
        Assert.Equal(2f, snapped, 3);
        Assert.Equal(2.5f, exact, 3);
        Assert.True(RoadProfile.PaintStrength(exact, 4f) == 0f);
    }
}
