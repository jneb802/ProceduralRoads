using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A staircase's earthwork as cut-only road points: the road terrain writer lowers the ground at
/// them and never raises or paints it, they survive a save, and nothing that
/// asks where the road is mistakes them for road.
/// </summary>
public class StairCutTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => 30f;
    }

    private readonly Vector2s _zone = new(0, 0);

    public StairCutTests()
    {
        _scope.WithWorld(new FlatWorld());
        RoadSpatialGrid.Clear();
        // A road far from the cuts: the append needs a network to append to.
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(300f, 300f), new(340f, 300f) }, 4f, WorldGenerator.instance);
        RoadSpatialGrid.FinalizeRoadNetwork();
    }

    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }







    /// <summary>Two roads snapped onto one point store records at the same
    /// position and width but different heights. The network version must not
    /// depend on which was stored first: worlds are stamped with it, and a
    /// change rewrites every zone's terrain on load (the
    /// height tie-break was dropped from the record order).</summary>
    [Fact]
    public void TheNetworkVersionDoesNotDependOnTheOrderPointsWereStored()
    {
        int Version(float first, float second)
        {
            RoadSpatialGrid.Clear();
            var p = new List<Vector2> { new(10f, 10f) };
            RoadSpatialGrid.CommitLevelled(p, new List<float> { first }, 4f);
            RoadSpatialGrid.CommitLevelled(p, new List<float> { second }, 4f);
            RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(12f, 10f), new(10f, 12f) }, new List<float> { 31f, 32f }, 4f);
            RoadSpatialGrid.FinalizeRoadNetwork();
            return RoadSpatialGrid.RoadNetworkVersion;
        }

        Assert.Equal(Version(30f, 33f), Version(33f, 30f));
    }


}
