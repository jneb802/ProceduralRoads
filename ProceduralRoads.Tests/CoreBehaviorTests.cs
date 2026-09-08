using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Tests for core mechanisms that exist on every base, aimed at regression
/// classes the project has actually suffered: serialization round-trips
/// (four separate persistence-fix commits in upstream history), spline
/// densification, path trimming, and road-weight falloff.
/// </summary>
public class CoreBehaviorTests
{
    private static void WithWorld(SyntheticWorld world, System.Action body)
    {
        WorldGenerator.instance = world;
        RoadSpatialGrid.Clear();
        try { body(); }
        finally
        {
            RoadSpatialGrid.Clear();
            WorldGenerator.instance = null;
        }
    }

    private static List<Vector2> StraightPath(float x0, float x1, float y, float step = 8f)
    {
        var path = new List<Vector2>();
        for (float x = x0; x <= x1; x += step)
            path.Add(new Vector2(x, y));
        return path;
    }

    [Fact]
    public void RoadPointsSurviveSerializationRoundTrip()
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        WithWorld(world, () =>
        {
            RoadSpatialGrid.AddRoadPath(StraightPath(-200f, 200f, 10f), 4f, world);
            RoadSpatialGrid.AddRoadPath(StraightPath(-100f, 100f, -150f), 6f, world);
            RoadSpatialGrid.FinalizeRoadNetwork();

            int points = RoadSpatialGrid.TotalRoadPoints;
            float length = RoadSpatialGrid.TotalRoadLength;
            RoadSpatialGrid.GetRoadWeight(0f, 10f, out float weightBefore, out float widthBefore);
            Assert.True(points > 0 && weightBefore > 0f, "Setup produced no road data");

            byte[]? data = RoadSpatialGrid.SerializeAllRoadPoints();
            Assert.NotNull(data);
            Assert.True(data!.Length > 0);

            RoadSpatialGrid.Clear();
            Assert.Equal(0, RoadSpatialGrid.TotalRoadPoints);

            Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(data), "Deserialize failed");

            // FINDING (upstream master): TotalRoadPoints after a round trip
            // differs from before (e.g. 552 -> 628) — the counter measures
            // different things on the two paths. The invariants that MUST
            // hold: road queries are functionally identical, and a second
            // serialize produces a byte-identical blob (no accumulation
            // across save/load cycles — the historical persistence bug class).
            RoadSpatialGrid.GetRoadWeight(0f, 10f, out float weightAfter, out float widthAfter);
            Assert.Equal(weightBefore, weightAfter, 3);
            Assert.Equal(widthBefore, widthAfter, 3);
            RoadSpatialGrid.GetRoadWeight(0f, -150f, out float weight2, out _);
            Assert.True(weight2 > 0f, "Second road lost in round trip");

            byte[]? data2 = RoadSpatialGrid.SerializeAllRoadPoints();
            Assert.NotNull(data2);
            Assert.Equal(data.Length, data2!.Length);
            Assert.Equal(data, data2);
        });
    }

    [Fact]
    public void SerializedRoadDataRejectsGarbageWithoutThrowing()
    {
        var world = new SyntheticWorld();
        WithWorld(world, () =>
        {
            Assert.False(RoadSpatialGrid.DeserializeAllRoadPoints(new byte[] { 1, 2, 3 }));
            Assert.False(RoadSpatialGrid.DeserializeAllRoadPoints(new byte[0]));

            byte[] truncated;
            RoadSpatialGrid.AddRoadPath(StraightPath(-50f, 50f, 0f), 4f, world);
            byte[] good = RoadSpatialGrid.SerializeAllRoadPoints()!;
            truncated = new byte[good.Length / 2];
            System.Array.Copy(good, truncated, truncated.Length);
            RoadSpatialGrid.Clear();

            Assert.False(RoadSpatialGrid.DeserializeAllRoadPoints(truncated),
                "Truncated data should be rejected, not partially loaded");
        });
    }

    [Fact]
    public void SplinePathDensifiesWithBoundedSpacingAndKeepsEndpoints()
    {
        var spline = typeof(RoadSpatialGrid).GetMethod("SplinePath",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(spline);

        var waypoints = new List<Vector2>
        {
            new(-100f, 0f), new(-40f, 30f), new(20f, -10f), new(90f, 40f),
        };
        const float spacing = 1f;
        var dense = (List<Vector2>)spline!.Invoke(null, new object[] { waypoints, spacing })!;

        Assert.True(dense.Count > waypoints.Count * 10, "Spline did not densify");
        Assert.True(Vector2.Distance(dense[0], waypoints[0]) < 2f, "Start moved");
        Assert.True(Vector2.Distance(dense[dense.Count - 1], waypoints[waypoints.Count - 1]) < 2f, "End moved");

        for (int i = 1; i < dense.Count; i++)
        {
            float d = Vector2.Distance(dense[i - 1], dense[i]);
            Assert.True(d <= spacing * 3f, $"Spline gap {d:F2}m at {i} (spacing {spacing})");
        }
    }

    [Fact]
    public void TrimPathToRadiiCutsIntoLocationCircles()
    {
        var trim = typeof(RoadNetworkGenerator).GetMethod("TrimPathToRadii",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(trim);

        var path = StraightPath(-100f, 100f, 0f, 4f);
        Vector2 startCenter = new(-100f, 0f);
        Vector2 endCenter = new(100f, 0f);

        var trimmed = (List<Vector2>?)trim!.Invoke(null,
            new object[] { new List<Vector2>(path), startCenter, 20f, endCenter, 20f });

        Assert.NotNull(trimmed);
        Assert.True(trimmed!.Count >= 2);

        // No trimmed point sits deep inside either location's exclusion circle
        // (the first/last points may sit ON the circle boundary).
        foreach (var p in trimmed)
        {
            Assert.True(Vector2.Distance(p, startCenter) > 20f - 9f,
                $"Point {p} deep inside start radius");
            Assert.True(Vector2.Distance(p, endCenter) > 20f - 9f,
                $"Point {p} deep inside end radius");
        }

        // CHARACTERIZATION (upstream master): a path lying entirely inside
        // both location radii is returned UNTRIMMED rather than rejected —
        // adjacent locations can get stub roads through their interiors.
        var tiny = new List<Vector2> { new(0f, 0f), new(4f, 0f) };
        var gone = (List<Vector2>?)trim.Invoke(null,
            new object[] { tiny, new Vector2(0f, 0f), 50f, new Vector2(4f, 0f), 50f });
        Assert.NotNull(gone);
        Assert.Equal(2, gone!.Count);
    }

    [Fact]
    public void RoadWeightFallsOffFromCenterlineToZero()
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        WithWorld(world, () =>
        {
            RoadSpatialGrid.AddRoadPath(StraightPath(-200f, 200f, 0f), 4f, world);

            RoadSpatialGrid.GetRoadWeight(0f, 0f, out float center, out _);
            RoadSpatialGrid.GetRoadWeight(0f, 30f, out float far, out _);

            Assert.True(center > 0.5f, $"Centerline weight {center:F2} too weak");
            Assert.Equal(0f, far);

            float prev = float.MaxValue;
            for (float off = 0f; off <= 10f; off += 1f)
            {
                RoadSpatialGrid.GetRoadWeight(0f, off, out float w, out _);
                Assert.True(w <= prev + 0.15f, $"Weight rose at offset {off}");
                prev = w;
            }
        });
    }
}
