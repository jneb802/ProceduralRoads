using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A staircase's earthwork as cut-only road points: the road terrain writer lowers the ground at
/// them and never raises or paints it, they survive a save, and nothing that
/// asks where the road is mistakes them for road.
/// </summary>
public class StairCutTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => 30f;
    }

    private readonly Vector2s _zone = new(0, 0);

    public StairCutTests()
    {
        _scope.WithWorld(new FlatWorld());
        RoadSpatialGrid.Clear();
        // A road far from the cuts: the append needs a network to append to.
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(300f, 300f), new(340f, 300f) }, 4f, WorldGenerator.instance);
        RoadSpatialGrid.FinalizeRoadNetwork();
    }

    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    private static void Cut(params Vector3[] cuts) =>
        RoadSpatialGrid.CommitAppendCuts(cuts, StairLayout.CutRadius * 2f, RoadSpatialGrid.RoadNetworkVersion);

    [Fact]
    public void ACutLowersTheGroundButNeverRaisesOrPaintsIt()
    {
        // One cut a metre under the ground at the zone centre, one a metre above it to the side.
        Cut(new Vector3(0f, 29f, 0f), new Vector3(10f, 31f, 0f));
        Heightmap hm = Heightmap.CreateForZone(_zone);
        var points = RoadSpatialGrid.GetRoadPointsInZone(_zone);
        Assert.All(points, p => Assert.True(p.cutOnly));

        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(_zone, points, hm, hm.m_terrainComp!);
        TerrainComp tc = hm.m_terrainComp!;
        int grid = tc.m_width + 1;
        float spacing = RoadConstants.ZoneSize / tc.m_width;
        int Index(float x, float z) =>
            Mathf.RoundToInt(z / spacing + tc.m_width / 2f) * grid + Mathf.RoundToInt(x / spacing + tc.m_width / 2f);

        Assert.Equal(-1f, tc.m_levelDelta[Index(0f, 0f)], 3);
        Assert.Equal(0f, tc.m_levelDelta[Index(10f, 0f)], 3);
        Assert.Equal(0f, tc.m_levelDelta[Index(5f, 0f)], 3);
        Assert.All(tc.m_levelDelta, d => Assert.True(d <= 1e-3f, $"a cut raised the ground by {d:F3} m"));
        Assert.DoesNotContain(tc.m_modifiedPaint, m => m);
    }

    /// <summary>A cut reaches 0.75 m and its batter 3 m more, so a zone neither
    /// touches does not gather it: gathered with a road's reach, that zone waited
    /// for it forever, unstamped, and was rebuilt every 2 s.</summary>
    [Fact]
    public void AZoneDoesNotGatherACutThatCannotReachIt()
    {
        // Past the batter beyond the east edge of zone (0,0), inside zone (1,0).
        float edge = RoadConstants.HalfZoneSize;
        Cut(new Vector3(edge + StairLayout.CutRadius + RoadTerrainModifier.CutBatterReach + 1.5f, 29f, 0f));
        Assert.DoesNotContain(RoadSpatialGrid.GetRoadPointsInZone(_zone), p => p.cutOnly);
        Assert.Contains(RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(1, 0)), p => p.cutOnly);
    }

    /// <summary>Where the ground already stands below the cut, nothing changes,
    /// and the zone is still stamped as carrying the network, so it is not
    /// queued again.</summary>
    [Fact]
    public void AZoneWithOnlyACutThatChangesNothingIsStillStamped()
    {
        Cut(new Vector3(0f, 31f, 0f));
        Heightmap hm = Heightmap.CreateForZone(_zone);
        TerrainComp tc = hm.m_terrainComp!;
        Assert.False(RoadTerrainModifier.CarriesCurrentRoads(tc));

        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(_zone, RoadSpatialGrid.GetRoadPointsInZone(_zone), hm, tc);

        Assert.DoesNotContain(tc.m_modifiedHeight, m => m);
        Assert.True(RoadTerrainModifier.CarriesCurrentRoads(tc));
    }

    /// <summary>A cut added to a zone whose terrain was already dug deeper
    /// (an older road, a player) leaves it there: it lowers from the terrain as
    /// it stands, not from the generated ground.</summary>
    [Fact]
    public void ACutNeverLiftsGroundAlreadyDugDeeper()
    {
        Heightmap hm = Heightmap.CreateForZone(_zone);
        TerrainComp tc = hm.m_terrainComp!;
        int grid = tc.m_width + 1;
        int centre = (tc.m_width / 2) * grid + tc.m_width / 2;
        tc.m_levelDelta[centre] = -2f;
        tc.m_modifiedHeight[centre] = true;

        // The same cut on the dug vertex and on untouched ground 5 m east.
        Cut(new Vector3(0f, 29f, 0f), new Vector3(5f, 29f, 0f));
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(_zone, RoadSpatialGrid.GetRoadPointsInZone(_zone), hm, tc);

        Assert.Equal(-2f, tc.m_levelDelta[centre], 3);
        Assert.Equal(-1f, tc.m_levelDelta[centre + Mathf.RoundToInt(5f / (RoadConstants.ZoneSize / tc.m_width))], 3);
    }

    [Fact]
    public void ARoadOnlyNetworkSavesAsBeforeAndCutsSaveAsFormatFour()
    {
        // Save format v4 (road statistics): the envelope, then the geometry encoding the network needs.
        byte[] roadOnly = RoadSpatialGrid.SerializeAllRoadPoints()!;
        Assert.Equal(4, BitConverter.ToInt32(roadOnly, 0));
        Assert.Equal(2, BitConverter.ToInt32(roadOnly, 4));

        Cut(new Vector3(0f, 29f, 0f));
        int version = RoadSpatialGrid.RoadNetworkVersion;
        byte[] withCuts = RoadSpatialGrid.SerializeAllRoadPoints()!;
        Assert.Equal(4, BitConverter.ToInt32(withCuts, 0));
        Assert.Equal(4, BitConverter.ToInt32(withCuts, 4));

        RoadSpatialGrid.Clear();
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(withCuts));
        RoadSpatialGrid.FinalizeRoadNetwork();
        Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
        Assert.Contains(RoadSpatialGrid.GetRoadPointsInZone(_zone), p => p.cutOnly && Mathf.Abs(p.h - 29f) < 1e-4f);
        Assert.Equal(withCuts, RoadSpatialGrid.SerializeAllRoadPoints());
    }

    /// <summary>Two roads snapped onto one point store records at the same
    /// position and width but different heights. The network version must not
    /// depend on which was stored first: worlds are stamped with it, and a
    /// change rewrites every zone's terrain on load (the
    /// height tie-break was dropped from the record order).</summary>
    [Fact]
    public void TheNetworkVersionDoesNotDependOnTheOrderPointsWereStored()
    {
        int Version(float first, float second)
        {
            RoadSpatialGrid.Clear();
            var p = new List<Vector2> { new(10f, 10f) };
            RoadSpatialGrid.CommitLevelled(p, new List<float> { first }, 4f);
            RoadSpatialGrid.CommitLevelled(p, new List<float> { second }, 4f);
            RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(12f, 10f), new(10f, 12f) }, new List<float> { 31f, 32f }, 4f);
            RoadSpatialGrid.FinalizeRoadNetwork();
            return RoadSpatialGrid.RoadNetworkVersion;
        }

        Assert.Equal(Version(30f, 33f), Version(33f, 30f));
    }

    /// <summary>A cut climbs back to the ground on a 1:1 batter instead of standing
    /// as a wall (a terrain blip at the end of a road or stairs).</summary>
    [Fact]
    public void ACutEasesBackToTheGround()
    {
        // A cut 2 m under flat ground at 30.
        Cut(new Vector3(0f, 28f, 0f));
        Heightmap hm = Heightmap.CreateForZone(_zone);
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(_zone, RoadSpatialGrid.GetRoadPointsInZone(_zone), hm, hm.m_terrainComp!);
        TerrainComp tc = hm.m_terrainComp!;
        int grid = tc.m_width + 1;
        float spacing = RoadConstants.ZoneSize / tc.m_width;
        float Delta(float x) => tc.m_levelDelta[Mathf.RoundToInt(tc.m_width / 2f) * grid + Mathf.RoundToInt(x / spacing + tc.m_width / 2f)];

        Assert.Equal(-2f, Delta(0f), 2);
        // A metre past its reach it has climbed a metre; past the batter it has stopped.
        float x1 = StairLayout.CutRadius + 1f;
        Assert.InRange(Delta(Mathf.Round(x1 / spacing) * spacing), -1.6f, -0.4f);
        Assert.Equal(0f, Delta((float)Math.Ceiling((StairLayout.CutRadius + RoadTerrainModifier.CutBatterReach + 0.5f) / spacing) * spacing), 3);
    }

    [Fact]
    public void ACutIsNotRoad()
    {
        Cut(new Vector3(0f, 29f, 0f));

        Assert.False(RoadSpatialGrid.TryGetRoadHeightWithin(new Vector2(0f, 0f), 5f, out _));
        Assert.False(RoadSpatialGrid.TryGetRoadWithin(new Vector2(0f, 0f), 5f, out _));
        Assert.DoesNotContain(RoadSpatialGrid.RoadTargets(_ => true), p => p.magnitude < 5f);
        RoadSpatialGrid.GetRoadWeight(0f, 0f, out float weight, out _);
        Assert.Equal(0f, weight);
        Assert.Contains(RoadSpatialGrid.RoadTargets(_ => true), p => p.x > 299f);
    }
}
