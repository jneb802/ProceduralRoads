using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

[Collection("RoadStatics")]
public class RaiseCopyOnWriteTests
{
    /// <summary>A committed cell array is read by other threads without the grid's lock, so raising a stored point replaces
    /// the array and never edits the one already published (with or without an append).</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(7)]
    public void RaiseLeavesTheCommittedArrayUnchanged(int? addition)
    {
        var at = new RoadSpatialGrid.RoadPoint(new Vector2(10f, 10f), 4f, 20f);
        Vector2i cell = RoadSpatialGrid.GetRoadGrid(at.p.x, at.p.y);
        var committed = new[] { at, new RoadSpatialGrid.RoadPoint(new Vector2(11f, 10f), 4f, 20f) };
        var cells = new ConcurrentDictionary<Vector2i, RoadSpatialGrid.RoadPoint[]>();
        cells[cell] = committed;
        MethodInfo raise = typeof(RoadSpatialGrid).GetMethod("Raise", BindingFlags.NonPublic | BindingFlags.Static)!;

        object? moved = raise.Invoke(null, new object?[] { cells, new List<(RoadSpatialGrid.RoadPoint, float)> { (at, 25f) }, addition });

        Assert.Equal(1, moved);
        Assert.Equal(20f, committed[0].h);
        Assert.Equal(0, committed[0].addition);
        Assert.NotSame(committed, cells[cell]);
        Assert.Equal(25f, cells[cell][0].h);
        Assert.Equal(20f, cells[cell][1].h);
    }
}
