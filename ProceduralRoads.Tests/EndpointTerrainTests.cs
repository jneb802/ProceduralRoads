using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The terrain a road's ends leave on a slope. The ramp pins the stored
/// endpoint height to the natural ground; these tests check the ground the
/// terrain modifier actually writes there, at both ends of one road on a
/// plane slope (one end arrives uphill, the other downhill), and just beyond
/// each end, while mid-road the cross-section stays leveled.
/// </summary>
public class EndpointTerrainTests
{
    private sealed class SlopeWorld : WorldGenerator
    {
        public float AlongX = 0.5f;   // metres of rise per metre east
        public float AcrossZ = 0f;    // metres of rise per metre north

        public override float GetHeight(float wx, float wy) => 60f + AlongX * wx + AcrossZ * wy;
    }

    private const int Width = 64;
    private const float RoadWidth = 4f;
    private const float EastEnd = 200f;
    private const float WestEnd = -200f;

    private static (SlopeWorld world, List<Vector2> path) SetUp(float across = 0f)
    {
        var world = new SlopeWorld { AcrossZ = across };
        WorldGenerator.instance = world;
        RoadSpatialGrid.Clear();
        var path = new List<Vector2>();
        for (float x = WestEnd; x <= EastEnd; x += 8f)
            path.Add(new Vector2(x, 0f));
        RoadSpatialGrid.AddRoadPath(path, RoadWidth, world);
        RoadSpatialGrid.FinalizeRoadNetwork();
        return (world, path);
    }

    private static void TearDown()
    {
        Heightmap.Registered = null;
        RoadSpatialGrid.Clear();
        WorldGenerator.instance = null;
    }

    /// <summary>Writes the road terrain of the zone containing world x and returns its compiler.</summary>
    private static TerrainComp Apply(float worldX)
    {
        Vector2i zone = ZoneSystem.GetZone(new Vector3(worldX, 0f, 0f));
        Heightmap hm = Heightmap.CreateForZone(zone, Width);
        Heightmap.Registered = hm;
        var points = RoadSpatialGrid.GetRoadPointsInZone(zone);
        Assert.True(points.Count > 0, $"no road points in zone {zone}");
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, hm.m_terrainComp!);
        return hm.m_terrainComp!;
    }

    /// <summary>Level delta at world (x, z) in the given compiler's zone (1 m vertices).</summary>
    private static float DeltaAt(TerrainComp tc, float worldX, float worldZ)
    {
        Vector3 origin = tc.m_hmap.transform.position;
        int vx = Mathf.RoundToInt(worldX - origin.x) + Width / 2;
        int vz = Mathf.RoundToInt(worldZ - origin.z) + Width / 2;
        return tc.m_levelDelta[vz * (Width + 1) + vx];
    }

    [Theory]
    [InlineData(EastEnd)]   // road arrives going uphill
    [InlineData(WestEnd)]   // road arrives going downhill
    public void TerrainAtARoadEndStaysOnTheNaturalGround(float end)
    {
        SetUp();
        try
        {
            TerrainComp tc = Apply(end);
            float outward = end > 0f ? 1f : -1f;

            float atEnd = DeltaAt(tc, end, 0f);
            Assert.True(Mathf.Abs(atEnd) < 0.05f, $"terrain at the road end is off the ground by {atEnd:F3} m");

            // The blend margin reaches 4 m past the end: no shelf there either.
            for (int d = 1; d <= 4; d++)
            {
                float beyond = DeltaAt(tc, end + outward * d, 0f);
                Assert.True(Mathf.Abs(beyond) < 0.05f, $"terrain {d} m beyond the road end is off the ground by {beyond:F3} m");
            }

            // And the last metres of road sit on the ground the ramp put them on.
            for (int d = 1; d <= 4; d++)
            {
                float inside = DeltaAt(tc, end - outward * d, 0f);
                Assert.True(Mathf.Abs(inside) < 0.1f, $"terrain {d} m inside the road end is off the ground by {inside:F3} m");
            }
        }
        finally { TearDown(); }
    }

    [Fact]
    public void RampOnABendStaysLevelAcrossTheRoad()
    {
        // Near a road end the ramp makes the lift grow with the square of
        // the distance from the end; on a bend the lateral offset of the
        // centreline grows the same way, and a free plane fit attributed
        // the one to the other: a tilt across the road of up to the clamp,
        // 1.5 m dips one metre off the centreline. The fit must follow the
        // road only: the leveled ground within the flat core stays at the
        // centreline height next to it.
        var world = new SlopeWorld { AlongX = 0f, AcrossZ = 0f };
        WorldGenerator.instance = world;
        RoadSpatialGrid.Clear();
        try
        {
            // A bend of radius 40 m through the zone centre, one point per metre,
            // lifted 0.01 m per metre squared from its start (0.64 m at 8 m).
            const float radius = 40f;
            var points = new List<RoadSpatialGrid.RoadPoint>();
            for (float sArc = 0f; sArc <= 30f; sArc += 1f)
            {
                float angle = sArc / radius;
                var pos = new Vector2(radius * Mathf.Sin(angle), radius * (1f - Mathf.Cos(angle)));
                float h = world.GetHeight(pos.x, pos.y) + 0.01f * sArc * sArc;
                points.Add(new RoadSpatialGrid.RoadPoint(pos, RoadWidth, h));
            }
            Vector2i zone = ZoneSystem.GetZone(Vector3.zero);
            Heightmap hm = Heightmap.CreateForZone(zone, Width);
            Heightmap.Registered = hm;
            RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, hm.m_terrainComp!);
            TerrainComp tc = hm.m_terrainComp!;

            // Every grid vertex within 1 m of the arc (inside the 1.2 m flat
            // core) between 3 and 27 m along it must sit at the road height
            // there: natural plus the lift at that arc position.
            int checkedVertices = 0;
            float worst = 0f;
            for (int vx = 0; vx <= 32; vx++)
            {
                for (int vz = 0; vz <= 32; vz++)
                {
                    float dcx = vx, dcy = vz - radius;               // from the arc's centre (0, radius)
                    float lateral = Mathf.Sqrt(dcx * dcx + dcy * dcy) - radius;
                    if (Mathf.Abs(lateral) > 1f) continue;
                    float sArc = radius * (float)System.Math.Atan2(dcx, -dcy);     // arc position from the start
                    if (sArc < 3f || sArc > 27f) continue;
                    float expected = world.GetHeight(vx, vz) + 0.01f * sArc * sArc;
                    float ground = world.GetHeight(vx, vz) + DeltaAt(tc, vx, vz);
                    float off = ground - expected;
                    if (Mathf.Abs(off) > Mathf.Abs(worst)) worst = off;
                    checkedVertices++;
                }
            }
            Assert.True(checkedVertices >= 30, $"only {checkedVertices} vertices checked");
            Assert.True(Mathf.Abs(worst) < 0.15f, $"leveled ground in the flat core is off the road height by up to {worst:0.00} m");
        }
        finally { TearDown(); }
    }

    [Fact]
    public void MidRoadCrossSectionStaysLeveledAcrossASideSlope()
    {
        // With the ground rising 0.2 m per metre across the road, the flat
        // core (1.2 m half-width) either side of the centreline is brought to
        // the centreline height: -0.2 m uphill, +0.2 m downhill.
        SetUp(across: 0.2f);
        try
        {
            TerrainComp tc = Apply(0f);
            float centre = DeltaAt(tc, 0f, 0f);
            Assert.True(Mathf.Abs(centre) < 0.05f, $"centreline delta {centre:F3}");
            float uphill = DeltaAt(tc, 0f, 1f);
            float downhill = DeltaAt(tc, 0f, -1f);
            Assert.True(Mathf.Abs(uphill + 0.2f) < 0.03f, $"uphill core vertex delta {uphill:F3}, expected -0.200");
            Assert.True(Mathf.Abs(downhill - 0.2f) < 0.03f, $"downhill core vertex delta {downhill:F3}, expected +0.200");
        }
        finally { TearDown(); }
    }
}
