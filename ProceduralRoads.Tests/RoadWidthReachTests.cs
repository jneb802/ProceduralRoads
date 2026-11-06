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

    [Fact]
    public void AtTheDefaultWidthTheReachesAreTheirOldValues()
    {
        Assert.Equal(RoadConstants.DefaultRoadWidth, RoadNetworkGenerator.RoadWidth);
        Assert.Equal(4f, RoadRide.Reach);
        Assert.Equal(RoadBeside.Reach, RoadBeside.Current);
        Assert.Equal(6f, RoadBeside.Current);
    }
}
