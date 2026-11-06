using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A staircase at the path's first or last index ends the road there: no stub of road runs from its end back to the path
/// point it stopped short of (a short piece, possibly steep or doubled back, that could cost
/// the road its stairs).
/// </summary>
public class StairPathEndTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : 40f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 77;
    }

    public StairPathEndTests()
    {
        _scope.WithWorld(new FlatWorld());
        RoadNetworkGenerator.Reset();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    /// <summary>Laid up to 3 m short of the path's last point, the road kept a 3 m piece from the staircase's top to it;
    /// at the path's first point there was none.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStaircaseAtThePathsEndLeavesNoStubOfRoadBehindIt(bool atEnd)
    {
        var path = new List<Vector2>();
        for (float x = -40f; x <= 0.01f; x += 8f) path.Add(new Vector2(x, 0f));
        if (!atEnd) path.Reverse();
        int last = path.Count - 1;
        // Over the last segment (or the first), its end 3 m short of the path point.
        var span = atEnd
            ? new RoadStairSpan { FromIndex = last - 1, ToIndex = last, FromPoint = path[last - 1], FromHeight = 40f, ToPoint = new Vector2(-3f, 0f), ToHeight = 40f }
            : new RoadStairSpan { FromIndex = 0, ToIndex = 1, FromPoint = new Vector2(-3f, 0f), FromHeight = 40f, ToPoint = path[1], ToHeight = 40f };

        var plans = RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing>(), 4f, null, null, new List<RoadStairSpan> { span });

        Assert.NotNull(plans);
        Assert.DoesNotContain(plans!.SelectMany(p => p.Points), p => p.x > -3f + 0.01f);
    }
}
