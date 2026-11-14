using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A junction's corner fill lies against both roads as they run (RoadJunctionFillet). At a start junction a road came down
/// from the north-west and met the trunk at about 35 degrees, curving over its last 10 m. Its inside corner was filled
/// against the straight line of its last 3 m: three lanes of fill 2.6, 4.6 and 6.6 m round one centre, 2 to 7 m long, up to
/// 6 m from both roads where the road had curved away from that line, read in game as five roads overlapping. The road
/// points are those read in that world (x, height, z): the trunk east from the location's door and the north-west road.
/// The lanes stored there were arcs about (-103.7, 5.1) at radii 2.6, 4.6 and 6.6 m.
/// </summary>
[Collection("RoadStatics")]
public class JunctionCornerFillTests
{
    private const float Width = 4f;

    private sealed class Ground : WorldGenerator
    {
        public override float GetHeight(float x, float z) => Mathf.Abs(x) > 400f || Mathf.Abs(z) > 400f ? 0f : 77f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    private static readonly Vector3[] Trunk =
    {
            new Vector3(-105.398f, 77.712f, 1.153f),
            new Vector3(-104.422f, 77.711f, 1.200f),
            new Vector3(-103.446f, 77.708f, 1.246f),
            new Vector3(-102.470f, 77.699f, 1.293f),
            new Vector3(-101.493f, 77.688f, 1.339f),
            new Vector3(-100.517f, 77.675f, 1.386f),
            new Vector3(-99.541f, 77.667f, 1.432f),
            new Vector3(-98.565f, 77.662f, 1.479f),
            new Vector3(-97.589f, 77.641f, 1.525f),
            new Vector3(-96.612f, 77.592f, 1.572f),
            new Vector3(-95.636f, 77.512f, 1.618f),
            new Vector3(-94.722f, 77.302f, 1.644f),
            new Vector3(-93.808f, 77.077f, 1.636f),
            new Vector3(-92.894f, 76.858f, 1.592f),
            new Vector3(-91.983f, 76.603f, 1.514f),
            new Vector3(-91.076f, 76.325f, 1.401f),
            new Vector3(-90.173f, 76.066f, 1.253f),
            new Vector3(-89.277f, 75.814f, 1.072f),
            new Vector3(-88.400f, 75.546f, 0.876f),
            new Vector3(-87.523f, 75.262f, 0.681f),
            new Vector3(-86.646f, 74.995f, 0.486f),
            new Vector3(-85.769f, 74.713f, 0.290f),
            new Vector3(-84.804f, 74.375f, 0.053f),
            new Vector3(-83.849f, 74.027f, -0.226f),
            new Vector3(-82.908f, 73.679f, -0.546f),
            new Vector3(-81.982f, 73.331f, -0.907f),
            new Vector3(-81.072f, 72.983f, -1.307f),
            new Vector3(-80.180f, 72.635f, -1.747f),
            new Vector3(-79.309f, 72.287f, -2.225f),
            new Vector3(-78.459f, 72.197f, -2.741f),
            new Vector3(-77.632f, 72.108f, -3.293f),
            new Vector3(-76.830f, 72.018f, -3.880f),
            new Vector3(-76.054f, 71.929f, -4.502f),
            new Vector3(-75.323f, 71.843f, -5.115f),
            new Vector3(-74.592f, 71.757f, -5.727f),
            new Vector3(-73.856f, 71.672f, -6.320f),
            new Vector3(-73.098f, 71.399f, -6.883f),
            new Vector3(-72.318f, 71.118f, -7.415f),
            new Vector3(-71.517f, 70.823f, -7.917f),
            new Vector3(-70.698f, 70.539f, -8.386f),
            new Vector3(-69.860f, 70.272f, -8.823f),
            new Vector3(-69.006f, 70.015f, -9.227f),
            new Vector3(-68.137f, 69.761f, -9.597f),
            new Vector3(-67.254f, 69.517f, -9.932f),
            new Vector3(-66.358f, 69.290f, -10.232f),
            new Vector3(-65.452f, 69.071f, -10.497f),
            new Vector3(-64.737f, 68.942f, -10.691f),
            new Vector3(-64.022f, 68.792f, -10.884f),
            new Vector3(-63.085f, 68.537f, -11.164f),
            new Vector3(-62.163f, 68.263f, -11.493f),
            new Vector3(-61.260f, 67.971f, -11.868f),
            new Vector3(-60.377f, 67.673f, -12.290f),
    };
    private static readonly Vector3[] Nw =
    {
            new Vector3(-124.650f, 69.493f, 50.653f),
            new Vector3(-124.096f, 69.419f, 49.871f),
            new Vector3(-123.543f, 69.369f, 49.089f),
            new Vector3(-122.989f, 69.364f, 48.308f),
            new Vector3(-122.435f, 69.416f, 47.526f),
            new Vector3(-121.854f, 69.502f, 46.739f),
            new Vector3(-121.241f, 69.614f, 45.978f),
            new Vector3(-120.598f, 69.795f, 45.241f),
            new Vector3(-119.926f, 70.021f, 44.532f),
            new Vector3(-119.290f, 70.256f, 43.888f),
            new Vector3(-118.655f, 70.509f, 43.245f),
            new Vector3(-118.019f, 70.745f, 42.601f),
            new Vector3(-117.384f, 70.943f, 41.958f),
            new Vector3(-116.749f, 71.122f, 41.315f),
            new Vector3(-116.113f, 71.292f, 40.671f),
            new Vector3(-115.478f, 71.458f, 40.028f),
            new Vector3(-114.832f, 71.592f, 39.348f),
            new Vector3(-114.213f, 71.671f, 38.643f),
            new Vector3(-113.622f, 71.713f, 37.915f),
            new Vector3(-113.060f, 71.747f, 37.164f),
            new Vector3(-112.528f, 71.812f, 36.391f),
            new Vector3(-112.027f, 71.885f, 35.599f),
            new Vector3(-111.557f, 71.921f, 34.787f),
            new Vector3(-111.119f, 71.935f, 33.958f),
            new Vector3(-110.713f, 71.939f, 33.112f),
            new Vector3(-110.336f, 71.915f, 32.283f),
            new Vector3(-109.958f, 71.896f, 31.454f),
            new Vector3(-109.580f, 71.916f, 30.625f),
            new Vector3(-109.203f, 71.974f, 29.796f),
            new Vector3(-108.817f, 72.016f, 28.989f),
            new Vector3(-108.401f, 72.067f, 28.197f),
            new Vector3(-107.956f, 72.190f, 27.420f),
            new Vector3(-107.482f, 72.375f, 26.661f),
            new Vector3(-106.980f, 72.551f, 25.920f),
            new Vector3(-106.451f, 72.729f, 25.198f),
            new Vector3(-105.895f, 72.948f, 24.497f),
            new Vector3(-105.314f, 73.193f, 23.816f),
            new Vector3(-104.708f, 73.361f, 23.158f),
            new Vector3(-104.100f, 73.485f, 22.522f),
            new Vector3(-103.492f, 73.636f, 21.887f),
            new Vector3(-102.884f, 73.861f, 21.251f),
            new Vector3(-102.224f, 74.026f, 20.530f),
            new Vector3(-101.598f, 74.175f, 19.780f),
            new Vector3(-101.005f, 74.357f, 19.003f),
            new Vector3(-100.448f, 74.548f, 18.200f),
            new Vector3(-99.927f, 74.773f, 17.373f),
            new Vector3(-99.443f, 74.971f, 16.524f),
            new Vector3(-98.998f, 75.257f, 15.654f),
            new Vector3(-98.592f, 75.599f, 14.766f),
            new Vector3(-98.226f, 75.941f, 13.860f),
            new Vector3(-97.900f, 76.283f, 12.938f),
            new Vector3(-97.616f, 76.625f, 12.003f),
            new Vector3(-97.369f, 76.947f, 11.116f),
            new Vector3(-97.121f, 77.270f, 10.229f),
            new Vector3(-96.873f, 77.592f, 9.342f),
            new Vector3(-96.625f, 77.592f, 8.456f),
            new Vector3(-96.377f, 77.512f, 7.569f),
            new Vector3(-96.130f, 77.512f, 6.682f),
            new Vector3(-95.882f, 77.512f, 5.795f),
            new Vector3(-95.634f, 77.512f, 4.908f),
            new Vector3(-95.386f, 77.512f, 4.021f),
            new Vector3(-95.056f, 77.302f, 3.235f),
            new Vector3(-94.542f, 77.302f, 2.555f),
            new Vector3(-93.875f, 77.077f, 2.023f),
    };

    private static RoadSpatialGrid.PlannedPath Piece(IEnumerable<Vector3> at)
    {
        var list = at.ToList();
        var points = list.Select(p => new Vector2(p.x, p.z)).ToList();
        float length = 0f;
        for (int i = 1; i < points.Count; i++) length += Vector2.Distance(points[i - 1], points[i]);
        return new RoadSpatialGrid.PlannedPath(points, list.Select(p => p.y).ToList(), points.Select(_ => default(RoadPointDebugInfo)).ToList(), Width, length, false);
    }

    private static float Off(Vector2 q, Vector3[] road) => road.Min(p => Vector2.Distance(q, new Vector2(p.x, p.z)));

    /// <summary>The north-west road's fill where it meets the stored trunk, through the production owner.</summary>
    private static List<Vector2> Fill()
    {
        var world = new Ground();
        using var scope = new ValheimWorldScope().WithWorld(world);
        float before = RoadJunctionFillet.Radius;
        RoadLocationProtection.Reset();
        RoadSpatialGrid.Clear();
        try
        {
            RoadJunctionFillet.Radius = RoadSwitchbacks.DefaultMinBendRadius;
            RoadSpatialGrid.Commit(Piece(Trunk));
            return RoadJunctionFillet.For(new List<RoadSpatialGrid.PlannedPath> { Piece(Nw) }, world).SelectMany(f => f.Points).ToList();
        }
        finally { RoadJunctionFillet.Radius = before; RoadSpatialGrid.Clear(); }
    }

    /// <summary>Every point of the fill lies within a road's width of the road it fills against, or within half a width of
    /// the trunk: none on open ground between them where the road had curved away from the line its last metres pointed
    /// along (the old fill's lanes reached 4.6 m off the road with 2.3 m to the trunk, and ran on to the location's door).
    /// Its arc now runs out onto both roads as they lie.</summary>
    [Fact]
    public void ACornerFillLiesAgainstTheRoadAsItCurvesNotItsEndsStraightLine()
    {
        var fill = Fill();
        foreach (Vector2 q in fill)
            Assert.True(Off(q, Nw) <= Width || Off(q, Trunk) <= Width * 0.5f,
                $"fill at ({q.x:F2}, {q.y:F2}): {Off(q, Nw):F2} m off the road, {Off(q, Trunk):F2} m off the trunk");
    }
}
