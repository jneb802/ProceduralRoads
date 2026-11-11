using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The road topology other mods read (RoadTravelApi): roads join where one ends on another at its height, bridges and
/// staircases join their two ends, a place's way in is the road within its reach, and a client's decoded copy answers
/// exactly as the server's does.
/// </summary>
public class RoadTopologyTests
{
    private const float Reach = 4f;
    private static readonly (Vector2, Vector2)[] NoLinks = Array.Empty<(Vector2, Vector2)>();
    private static readonly RoadTopology.SourcePlace[] NoPlaces = Array.Empty<RoadTopology.SourcePlace>();

    /// <summary>Points a metre apart from <paramref name="from"/> to <paramref name="to"/>, as the planner stores them
    /// (width / 4 with the default 4 m road), at <paramref name="height"/>.</summary>
    private static List<RoadTopology.SourcePoint> Road(Vector2 from, Vector2 to, float height = 40f, float width = 4f, bool walked = true)
    {
        List<RoadTopology.SourcePoint> points = new();
        int steps = Mathf.CeilToInt(Vector2.Distance(from, to));
        for (int i = 0; i <= steps; i++)
            points.Add(new RoadTopology.SourcePoint(Vector2.Lerp(from, to, i / (float)steps), height, width, walked));
        return points;
    }

    private static RoadTopology Build(IEnumerable<RoadTopology.SourcePoint> points, IEnumerable<(Vector2, Vector2)>? links = null,
        IEnumerable<RoadTopology.SourcePlace>? places = null) =>
        RoadTopology.Build(7, Reach, points, links ?? NoLinks, places ?? NoPlaces);

    private static string RouteAt(RoadTopology t, float x, float z)
    {
        Assert.True(t.TryFindRoad(new Vector3(x, 0f, z), 10f, out string route, out _, out _, out _));
        return route;
    }

    [Fact]
    public void OneRoadIsOneNetwork()
    {
        RoadTopology t = Build(Road(new Vector2(0, 0), new Vector2(200, 0)));
        Assert.Equal(1, t.Components);
        Assert.True(t.Connected(RouteAt(t, 5, 2), RouteAt(t, 195, -2)));
    }

    [Fact]
    public void TwoRoadsApartAreTwoNetworks()
    {
        RoadTopology t = Build(Road(new Vector2(0, 0), new Vector2(100, 0)).Concat(Road(new Vector2(0, 50), new Vector2(100, 50))));
        Assert.Equal(2, t.Components);
        Assert.False(t.Connected(RouteAt(t, 50, 0), RouteAt(t, 50, 50)));
    }

    [Fact]
    public void ARoadEndingOnAnotherJoinsIt()
    {
        // A branch whose last point stops beside the trunk's centreline, not on one of its points (a T junction).
        RoadTopology t = Build(Road(new Vector2(0, 0), new Vector2(100, 0))
            .Concat(Road(new Vector2(50.4f, 60), new Vector2(50.4f, 2.5f))));
        Assert.Equal(1, t.Components);
        Assert.True(t.Connected(RouteAt(t, 50.4f, 55), RouteAt(t, 95, 0)));
    }

    [Fact]
    public void ARoadPassingOverAnotherOutOfReachInHeightStaysApart()
    {
        // Past RoadBeside.Height (1.5 m) apart in height where they cross: an overpass, not a junction.
        RoadTopology t = Build(Road(new Vector2(0, 0), new Vector2(100, 0), 40f)
            .Concat(Road(new Vector2(50, -50), new Vector2(50, 50), 46f)));
        Assert.Equal(2, t.Components);
    }

    [Fact]
    public void ABridgeOrStaircaseJoinsItsTwoEnds()
    {
        List<RoadTopology.SourcePoint> banks = Road(new Vector2(0, 0), new Vector2(40, 0))
            .Concat(Road(new Vector2(80, 0), new Vector2(120, 0), 52f)).ToList();
        Assert.Equal(2, Build(banks).Components);
        RoadTopology linked = Build(banks, new[] { (new Vector2(41, 0), new Vector2(79, 0)) });
        Assert.Equal(1, linked.Components);
        Assert.Equal(1, linked.LinksJoined);
        // An end with no road in reach joins nothing.
        RoadTopology dangling = Build(banks, new[] { (new Vector2(41, 0), new Vector2(60, 30)) });
        Assert.Equal(2, dangling.Components);
        Assert.Equal(0, dangling.LinksJoined);
        Assert.Equal(1, dangling.LinksGiven);
    }

    [Fact]
    public void AWadedFordConnectsButIsNeverOffered()
    {
        List<RoadTopology.SourcePoint> points = Road(new Vector2(0, 0), new Vector2(40, 0))
            .Concat(Road(new Vector2(41, 0), new Vector2(59, 0), walked: false))
            .Concat(Road(new Vector2(60, 0), new Vector2(100, 0))).ToList();
        RoadTopology t = Build(points);
        Assert.Equal(1, t.Components);
        Assert.False(t.TryFindRoad(new Vector3(50, 0, 3), 5f, out _, out _, out _, out _));
        Assert.True(t.TryFindRoad(new Vector3(50, 0, 3), 12f, out _, out Vector3 point, out _, out _));
        // The road at the ford's bank, not on the ford: the projection stops where the walked road stops.
        Assert.Equal(40f, point.x, 1);
    }

    [Fact]
    public void TheNearestRoadIsProjectedOntoItsCentrelineWithItsHeadingAndWidth()
    {
        RoadTopology t = Build(Road(new Vector2(0, 0), new Vector2(100, 100), 40f, 5f));
        Assert.True(t.TryFindRoad(new Vector3(50, 0, 56), 10f, out _, out Vector3 point, out Vector3 direction, out float width));
        // Across from (50, 56) on the line x = z: (53, 53).
        Assert.Equal(53f, point.x, 1);
        Assert.Equal(53f, point.z, 1);
        Assert.Equal(40f, point.y, 2);
        Assert.Equal(5f, width, 2);
        // Along x = z, either way round.
        Assert.True(Mathf.Abs(Mathf.Abs(direction.x + direction.z) / Mathf.Sqrt(2f) - 1f) < 1e-3f);
        Assert.False(t.TryFindRoad(new Vector3(50, 0, 80), 10f, out _, out _, out _, out _));
    }

    [Fact]
    public void APlaceIsReachedByTheNearestRoadWithinItsReachKeyedByItsZone()
    {
        RoadTopology t = Build(Road(new Vector2(0, 0), new Vector2(200, 0)), places: new[]
        {
            new RoadTopology.SourcePlace(new Vector2(100, 20), 30f),
            new RoadTopology.SourcePlace(new Vector2(100, 400), 30f),
        });
        Assert.Equal(1, t.AccessCount);
        Assert.True(t.TryGetPlaceAccess(new Vector3(100, 0, 20), out string route, out Vector3 point, out _, out _));
        Assert.Equal(100f, point.x, 1);
        Assert.Equal(0f, point.z, 1);
        Assert.True(t.Connected(route, RouteAt(t, 10, 0)));
        Assert.False(t.TryGetPlaceAccess(new Vector3(100, 0, 400), out _, out _, out _, out _));
    }

    [Fact]
    public void TheSameInputInAnyOrderGivesTheSameBytes()
    {
        List<RoadTopology.SourcePoint> points = Road(new Vector2(0, 0), new Vector2(100, 0))
            .Concat(Road(new Vector2(0, 50), new Vector2(100, 50))).ToList();
        // The grid stores a point in every cell its width reaches: duplicates are dropped.
        List<RoadTopology.SourcePoint> shuffled = points.Concat(points.Take(20)).OrderBy(p => (p.Position.x * 7919f + p.Position.y) % 13f).ToList();
        RoadTopology a = Build(points), b = Build(shuffled);
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(a.PointCount, b.PointCount);
        Assert.Equal(RouteAt(a, 50, 50), RouteAt(b, 50, 50));
    }

    [Fact]
    public void AClientCopyAnswersAsTheServerDoes()
    {
        RoadTopology server = Build(Road(new Vector2(0, 0), new Vector2(100, 37)).Concat(Road(new Vector2(0, 80), new Vector2(60, 80))),
            places: new[] { new RoadTopology.SourcePlace(new Vector2(30, 90), 20f) });
        RoadTopology? client = RoadTopology.Deserialize(server.Bytes);
        Assert.NotNull(client);
        Assert.Equal(server.Hash, client!.Hash);
        Assert.Equal(server.NetworkVersion, client.NetworkVersion);
        Assert.Equal(server.Components, client.Components);
        Assert.Equal(server.AccessCount, client.AccessCount);
        foreach (Vector3 at in new[] { new Vector3(20, 0, 9), new Vector3(55, 0, 78), new Vector3(99, 0, 30) })
        {
            Assert.True(server.TryFindRoad(at, 10f, out string sr, out Vector3 sp, out Vector3 sd, out float sw));
            Assert.True(client.TryFindRoad(at, 10f, out string cr, out Vector3 cp, out Vector3 cd, out float cw));
            Assert.Equal(sr, cr); Assert.Equal(sp, cp); Assert.Equal(sd, cd); Assert.Equal(sw, cw);
        }
        Assert.True(server.TryGetPlaceAccess(new Vector3(30, 0, 90), out string a, out _, out _, out _));
        Assert.True(client.TryGetPlaceAccess(new Vector3(30, 0, 90), out string b, out _, out _, out _));
        Assert.Equal(a, b);
    }

    [Fact]
    public void ARouteFromAnotherCopyNeverConnects()
    {
        RoadTopology first = Build(Road(new Vector2(0, 0), new Vector2(100, 0)));
        RoadTopology second = Build(Road(new Vector2(0, 0), new Vector2(120, 0)));
        Assert.NotEqual(first.Hash, second.Hash);
        string route = RouteAt(first, 50, 0);
        Assert.True(first.Connected(route, route));
        Assert.False(second.Connected(route, RouteAt(second, 50, 0)));
        Assert.False(first.Connected(route, "garbage"));
        Assert.False(first.Connected(route, first.Hash.ToString("x16") + ":99"));
    }

    [Fact]
    public void DamagedBytesAreRefused()
    {
        byte[] bytes = Build(Road(new Vector2(0, 0), new Vector2(100, 0))).Bytes;
        Assert.Null(RoadTopology.Deserialize(Array.Empty<byte>()));
        Assert.Null(RoadTopology.Deserialize(bytes.Take(bytes.Length / 2).ToArray()));
        Assert.Null(RoadTopology.Deserialize(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }

    [Fact]
    public void AnEmptyNetworkHasNoRoad()
    {
        RoadTopology t = Build(Array.Empty<RoadTopology.SourcePoint>());
        Assert.Equal(0, t.PointCount);
        Assert.Equal(0, t.Components);
        Assert.False(t.TryFindRoad(Vector3.zero, 100f, out _, out _, out _, out _));
        Assert.NotNull(RoadTopology.Deserialize(t.Bytes));
    }
}
