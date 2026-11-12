using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The ground a location's level operations shape, read from its saved record for a location placed before its roads
/// (LocationLevelling.GroundReach): its road can stop past its exterior radius, and the road topology looks that far.
/// </summary>
public class GroundReachTests
{
    [Fact]
    public void AnOperationReachesItsOffsetPlusTheLargerOfItsLevelAndSmoothRadius()
    {
        List<LevelOp> ops = new()
        {
            new LevelOp(0f, 0f, 0f, 10f, false),
            new LevelOp(12f, 5f, 0f, 4f, false, smoothRadius: 9f),
        };
        // 13 m off the centre, smoothing 9 m round it.
        Assert.Equal(22f, LocationLevelling.GroundReach(ops), 3);
    }

    [Fact]
    public void ASquareOperationReachesItsCorner()
    {
        List<LevelOp> ops = new() { new LevelOp(0f, 0f, 0f, 10f, true) };
        Assert.Equal(10f * Mathf.Sqrt(2f), LocationLevelling.GroundReach(ops), 3);
    }

    [Fact]
    public void NoOperationsShapeNoGround() => Assert.Equal(0f, LocationLevelling.GroundReach(new List<LevelOp>()));
}
