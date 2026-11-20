using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// On the fallback (<see cref="RoadNetworkGenerator.ReachOffLevel"/>) a road ending off a location's level reaches it, but not
/// from behind a ridge (<see cref="RoadNetworkGenerator.BehindRidge"/>): Mistlands_DvergrTownEntrance2 at (1344, 7168) was
/// reached by a road ending 42 m off at 46.4 m, a ridge 61.6 m high between it and the entrance at 36.1 m. A location left
/// unserved is acceptable; a road stranded behind a ridge is not. A climb up to a location the player sees from the road is
/// still reached.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>The ground in the game from the road's end (1344, 7126.3) to the entrance (1344, 7168.3), every 3.5 m.</summary>
    private static readonly float[] DvergrProfile =
        { 46.435f, 48.573f, 52.5f, 54.3f, 60.1f, 61.6f, 59.2f, 47.581f, 39.563f, 37.184f, 36.529f, 35.852f, 36.136f };

    /// <summary>The ground on the line from <paramref name="end"/> to <paramref name="location"/>, between samples
    /// <paramref name="profile"/> evenly spaced from one to the other, read straight between them.</summary>
    private static Func<Vector2, float> Along(Vector2 end, Vector2 location, float[] profile) => q =>
    {
        float t = Mathf.Clamp01(Vector2.Dot(q - end, location - end) / (location - end).sqrMagnitude) * (profile.Length - 1);
        int i = Mathf.Min((int)t, profile.Length - 2);
        return Mathf.Lerp(profile[i], profile[i + 1], t - i);
    };

    /// <summary>The Dvergr entrance at (1344, 7168): the ridge stands 15 m over the road's end and the entrance, behind a ridge.</summary>
    [Fact]
    public void ADvergrEntranceWithARidgeBeforeItIsBehindARidgeFromItsRoadsEnd()
    {
        var end = new Vector2(1344f, 7126.3f);
        var location = new Vector2(1344f, 7168.3f);
        float over = RoadNetworkGenerator.RidgeOver(end, DvergrProfile[0], location, DvergrProfile[DvergrProfile.Length - 1], Along(end, location, DvergrProfile));
        Assert.InRange(over, 14.5f, 15.2f);
        Assert.True(over > RoadNetworkGenerator.RidgeRise);
    }

    /// <summary>A climb of 8 m from the road's end up to the location, which the player sees from the road, and a slope down
    /// to it, rise over neither: no ridge. Nor does the ground the road's own blend takes at its end.</summary>
    [Fact]
    public void AClimbUpToALocationOrDownToItIsNoRidge()
    {
        var end = new Vector2(0f, 0f);
        var location = new Vector2(0f, 42f);
        float[] up = { 46.4f, 47f, 48f, 49.5f, 51f, 52f, 53f, 53.5f, 54f, 54.2f, 54.3f, 54.4f, 54.4f };
        float[] down = (float[])up.Clone();
        Array.Reverse(down);
        Assert.True(RoadNetworkGenerator.RidgeOver(end, up[0], location, up[up.Length - 1], Along(end, location, up)) <= 0f);
        Assert.True(RoadNetworkGenerator.RidgeOver(end, down[0], location, down[down.Length - 1], Along(end, location, down)) <= 0f);
        // A cut 6 m deep at the road's end, the ground falling away past it: within the road's own blend, not a ridge.
        Func<Vector2, float> cut = q => q.y < RoadNetworkGenerator.RidgePastEnd - 0.5f ? 52f : 46.4f - q.y * 0.1f;
        Assert.True(RoadNetworkGenerator.RidgeOver(end, 46.4f, location, 42f, cut) <= RoadNetworkGenerator.RidgeRise);
    }

    /// <summary>The cave, placed on its ground, with a ridge <paramref name="ridge"/> m high across the line from the road at
    /// (0, 26) to its landing, 4 m wide at its middle.</summary>
    private PlacedLocation BehindARidge(float ridge)
    {
        RoadNetworkGenerator.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        PlacedLocations.Reset();
        PlacedLocation cave = Cave();
        Vector2 landing = cave.Shape.Landing, road = new(0f, 26f);
        Vector2 across = (road - landing).normalized, middle = Vector2.Lerp(road, landing, 0.5f);
        Func<float, float, float> ground = (x, z) => cave.Ground(x, z) + (Mathf.Abs(Vector2.Dot(new Vector2(x, z) - middle, across)) <= 2f ? ridge : 0f);
        var location = new PlacedLocation { Record = cave.Record, Ground = ground, Shape = cave.Shape };
        PlacedLocations.Ready(location);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
        return location;
    }

    /// <summary>A placed location whose network ends at its ring within two junction steps of its landing: in normal generation it is reached over open ground, and with that entrance's ridge (15.2 m) between them not reached, the location
    /// unserved; a rise of 7 m, as the highest of the other fallbacks measured (7.2 m), does not stop it.</summary>
    [Theory]
    [InlineData(0f, true)]
    [InlineData(7f, true)]
    [InlineData(15.2f, false)]
    public void ALocationBehindARidgeFromItsNetworkIsNotReachedOnTheFallback(float ridge, bool reached)
    {
        _world.Biome = Heightmap.Biome.BlackForest;
        PlacedLocation cave = BehindARidge(ridge);
        Vector2 landing = cave.Shape.Landing;
        float under = cave.Ground(landing.x, landing.y) - 2f;
        var points = new List<Vector2> { new(-20f, 26f), new(0f, 26f), new(20f, 26f) };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => under), points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 40f, false));
        try
        {
            Assert.True(Vector2.Distance(landing, new Vector2(0f, 26f)) > RoadNetworkGenerator.LandingReach, "the road is on the landing");
            Assert.Equal(reached, RoadNetworkGenerator.Reached(Vector3.zero, 10f));
            bool attempted = false;
            Assert.False(RoadNetworkGenerator.ReachOffLevel("MountainCave02", Vector3.zero, 10f, () => attempted = true));
            Assert.Equal(!reached, attempted);
            Assert.Equal(reached, RoadNetworkGenerator.Reached(Vector3.zero, 10f));
            Assert.Equal(0, RoadNetworkGenerator.ReachedOffLevelCount);
            Assert.Equal(reached ? 0 : 1, RoadNetworkGenerator.BehindRidgeCount);
            Assert.Equal(!reached, RoadNetworkGenerator.BehindRidge(new Vector2(0f, 0f), 10f, new Vector2(0f, 26f), under));
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(7f, true)]
    [InlineData(15.2f, false)]
    public void ANormalRingEndKeepsTheRidgeRefusal(float ridge, bool accepted)
    {
        _world.Biome = Heightmap.Biome.BlackForest;
        PlacedLocation cave = BehindARidge(ridge);
        float height = cave.LandingHeight - 2f;
        var path = new List<Vector2> { new(0f, 120f), new(0f, 26f) };
        var plan = new RoadSpatialGrid.PlannedPath(path, path.ConvertAll(_ => height),
            path.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 94f, false);
        var refusal = RoadNetworkGenerator.EndNotLevel(path, new List<RoadSpatialGrid.PlannedPath> { plan },
            new List<RoadStairSpan>(), new List<RoadStairSpan>(), path[0], 0f, new Vector2(0f, 0f), 10f, false, true);
        Assert.Equal(accepted ? null : RoadNetworkGenerator.RidgeRefusal, refusal?.refusal);
    }

}
