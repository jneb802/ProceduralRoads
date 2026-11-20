using System;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>A location's footprint is closed: its exact rim is inside, for every reader of membership
/// (<see cref="RoadLocationProtection.Contains(Vector2)"/>, the owner).</summary>
public sealed class FootprintRimTests : IDisposable
{
    private const float Radius = 8f;
    private const float NaturalHeight = 34f;
    private const float PlacedHeight = 47f;
    private static readonly Vector2 Centre = new(0f, 0f);
    private readonly ValheimWorldScope scope = new();

    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => NaturalHeight;
    }

    public FootprintRimTests()
    {
        scope.WithWorld(new FlatWorld());
        RoadNetworkGenerator.Reset();
        PlacedLocations.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(Centre, Radius) });
        PlacedLocations.Ready(new PlacedLocation
        {
            Record = new PlacedLocationRecord { Centre = Centre },
            Ground = (_, _) => PlacedHeight,
        });
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        PlacedLocations.Reset();
        scope.Dispose();
    }

    /// <summary>A vertex at x = 8 against a footprint one float smaller (just outside), exactly 8 (the rim) and one
    /// float larger (just inside): protection, the zero-reach lookup, the writer's placed ground and the finished-ground
    /// predictor all give the same answer.</summary>
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void EveryMembershipReaderAgreesAtTheRim(int radiusSteps, bool inside)
    {
        // Positive float encodings are ordered, so one bit step gives the adjacent float.
        int bits = BitConverter.ToInt32(BitConverter.GetBytes(Radius), 0);
        float radius = BitConverter.ToSingle(BitConverter.GetBytes(bits + radiusSteps), 0);
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(Centre, radius) });
        var vertex = new Vector2(Radius, 0f);
        float? placed = inside ? PlacedHeight : null;

        Assert.Equal(inside, RoadLocationProtection.Contains(vertex));
        Assert.Equal(inside, RoadLocationProtection.BlocksSegment(vertex, vertex, 0f, default));
        Assert.Equal(inside, RoadLocationProtection.NearestFootprint(vertex, 0f, out _));
        Assert.Equal(placed, PlacedLocations.GroundAt(vertex));
        var finished = RoadTerrainModifier.FinishedGround((_, _) => NaturalHeight);
        Assert.Equal(inside ? PlacedHeight : NaturalHeight, finished((int)Radius, 0));
    }

    /// <summary>A road that only touches the footprint, or the edge of its clearance, is blocked as one crossing it.</summary>
    [Fact]
    public void ARoadTangentToTheFootprintOrItsClearanceIsBlocked()
    {
        Assert.True(RoadLocationProtection.BlocksSegment(new Vector2(-4f, Radius), new Vector2(4f, Radius), 0f, default));

        float clearance = RoadLocationProtection.Clearance(RoadConstants.DefaultRoadWidth);
        float edge = Radius + clearance;
        Assert.True(RoadLocationProtection.BlocksSegment(new Vector2(-4f, edge), new Vector2(4f, edge), clearance, default));
        Assert.True(RoadLocationProtection.BlocksRoad(new Vector2(-4f, edge), new Vector2(4f, edge), RoadConstants.DefaultRoadWidth,
            new Vector2(-100f, edge), new Vector2(100f, edge), default));
    }

    [Fact]
    public void ASelectedTargetsExemptionStillChecksAnotherOverlappingLocation()
    {
        var start = new Vector2(-16f, 0f);
        var target = Centre;
        Assert.True(RoadLocationProtection.BlocksSegment(start, target, 0f, default));
        Assert.False(RoadLocationProtection.BlocksSegment(start, target, 0f, new RoadLocationProtection.Targets(null, target)));

        RoadLocationProtection.Set(new[]
        {
            new RoadLocationProtection.Footprint(target, Radius),
            new RoadLocationProtection.Footprint(new Vector2(-12f, 0f), Radius),
        });
        Assert.True(RoadLocationProtection.BlocksSegment(start, target, 0f, new RoadLocationProtection.Targets(null, target)));
    }
}
