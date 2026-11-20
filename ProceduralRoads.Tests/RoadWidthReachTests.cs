using System.Collections.Generic;
using System;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The rules that read "a road's width" read the Roads/RoadWidth setting (assistant-written, 6 Nov 2026): "one road" (RoadRide)
/// and "beside a road" (RoadBeside); how far past a rock a road can touch it (RoadRockCarve.WidestRoadHalfWidth) reads it too, but
/// runs in the game only. At the default width they are the values they always had (4 m, 6 m).
/// </summary>
[Collection("RoadStatics")]
public class RoadWidthReachTests
{
    [Theory]
    [InlineData(4f, 4f, 6f)]
    [InlineData(8f, 8f, 10f)]
    [InlineData(10f, 10f, 12f)]
    [InlineData(2f, 2f, 4f)]
    public void TheReachesFollowTheRoadWidthSetting(float width, float ride, float beside)
    {
        float before = RoadNetworkGenerator.RoadWidth;
        try
        {
            RoadNetworkGenerator.RoadWidth = width;
            Assert.Equal(ride, RoadRide.Reach);
            Assert.Equal(beside, RoadBeside.Current);
        }
        finally { RoadNetworkGenerator.RoadWidth = before; }
    }

    /// <summary>The stored road's height a crossing's bank and a junction's fillet read (the one reader of a met road's height,
    /// RoadSpatialGrid.TryGetRoadHeightWithin, within RoadRide.Reach) is found within the configured width on every side: at a
    /// width of 8 a road point 7 m off, west as well as east, is found. At the default width it is out of reach either way.</summary>
    [Theory]
    [InlineData(7f, 0f)]
    [InlineData(-7f, 0f)]
    [InlineData(0f, 7f)]
    [InlineData(0f, -7f)]
    public void TheMetRoadsHeightIsFoundOnEitherSideAtTheConfiguredWidth(float x, float z)
    {
        const float bank = 42f;
        float before = RoadNetworkGenerator.RoadWidth;
        RoadSpatialGrid.Clear();
        try
        {
            var at = new List<Vector2> { new(x, z) };
            RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(at, new List<float> { bank }, new List<RoadPointDebugInfo> { default }, 4f, 0f, false));
            RoadNetworkGenerator.RoadWidth = 8f;
            Assert.True(RoadSpatialGrid.TryGetRoadHeightWithin(new Vector2(0f, 0f), RoadRide.Reach, out float met));
            Assert.Equal(bank, met, 2);
            RoadNetworkGenerator.RoadWidth = RoadConstants.DefaultRoadWidth;
            Assert.False(RoadSpatialGrid.TryGetRoadHeightWithin(new Vector2(0f, 0f), RoadRide.Reach, out _));
            Assert.True(RoadSpatialGrid.TryGetRoadHeightWithin(new Vector2(x, z) * (4f / 7f), RoadRide.Reach, out met));
            Assert.Equal(bank, met, 2);
        }
        finally { RoadNetworkGenerator.RoadWidth = before; RoadSpatialGrid.Clear(); }
    }

    [Fact]
    public void AtTheDefaultWidthTheReachesAreTheirOldValues()
    {
        Assert.Equal(RoadConstants.DefaultRoadWidth, RoadNetworkGenerator.RoadWidth);
        Assert.Equal(4f, RoadRide.Reach);
        Assert.Equal(RoadBeside.CorridorDistance(RoadConstants.DefaultRoadWidth), RoadBeside.Current);
        Assert.Equal(6f, RoadBeside.Current);
    }

    /// <summary>The corridor is one distance for the width it is given: the beside-road rules give it the setting's width
    /// (Current), the sway the width of the road it plans, which need not be the setting's.</summary>
    [Fact]
    public void TheCorridorReadsTheWidthItIsGiven()
    {
        float before = RoadNetworkGenerator.RoadWidth;
        try
        {
            RoadNetworkGenerator.RoadWidth = 8f;
            Assert.Equal(10f, RoadBeside.Current);
            Assert.Equal(6f, RoadBeside.CorridorDistance(4f));
            Assert.Equal(RoadBeside.CorridorDistance(8f), RoadBeside.Current);
        }
        finally { RoadNetworkGenerator.RoadWidth = before; }
    }
}
