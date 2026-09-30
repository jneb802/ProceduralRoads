using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// What a road write leaves alone and where zones meet, through the zone-spawn path the game drives
/// (OnZoneSpawned, the zone's own compiler and road points, the heightmap pass on rebuild).
/// (f) Outside the road's reach every vertex keeps its earlier edits exactly: level and smooth deltas, paint and both
/// modified flags, on a zone whose background is edited everywhere.
/// (g) A road crossing a seam leaves both zones agreeing along it when the neighbour loads after the first zone was
/// written; while only the first zone is written the seam visibly disagrees, so the check can tell.
/// </summary>
public sealed class TerrainPreservationTests : IDisposable
{
    private const float Width = 4f;
    private static readonly PlaneTerrain CrossSlope = new(64f, 0f, 0.25f); // rises 0.25 m per metre north, across an east-west road
    private readonly ValheimWorldScope _world = new ValheimWorldScope().WithTerrain(CrossSlope).WithZdos().WithZoneSystem();
    private readonly StaticOverride _plain = PlainEarthworks.Apply().And(() => RoadTerrainModifier.BatterPerMetre, 0f);

    public TerrainPreservationTests() { RoadTerrainModifier.ResetDebugCounters(); RoadSpatialGrid.Clear(); }
    public void Dispose() { RoadTerrainModifier.ResetDebugCounters(); RoadSpatialGrid.Clear(); _plain.Dispose(); _world.Dispose(); }

    private Heightmap Load(int zoneX)
    {
        var hm = _world.RegisterHeightmap(new Vector2s(zoneX, 0));
        hm.AuthoredHeight = CrossSlope.GetHeight;
        return hm;
    }

    private static void Spawn(int zoneX) =>
        RoadTerrainModifier.OnZoneSpawned(new Vector2s(zoneX, 0), RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(zoneX, 0)));

    private static void EastWestRoad(float fromX, float toX)
    {
        var path = new List<Vector2>();
        for (float x = fromX; x <= toX; x += 8f) path.Add(new Vector2(x, 0f));
        Assert.True(RoadSpatialGrid.AddRoadPath(path, Width, WorldGenerator.instance!));
        RoadSpatialGrid.FinalizeRoadNetwork();
    }

    [Fact]
    public void OutsideTheRoadsReachEveryEarlierEditSurvivesExactly()
    {
        var hm = Load(0); var tc = hm.m_terrainComp!;
        // A player-edited background everywhere: varied deltas, paint and flags, none of them the road's values.
        for (int i = 0; i < tc.m_levelDelta.Length; i++)
        {
            tc.m_levelDelta[i] = 0.25f + (i % 7) * 0.05f; tc.m_smoothDelta[i] = -0.1f + (i % 5) * 0.02f;
            tc.m_modifiedHeight[i] = i % 3 != 0;
            tc.m_paintMask[i] = new Color(0.1f + (i % 4) * 0.1f, 0.5f, 0.2f + (i % 3) * 0.1f, 0.4f);
            tc.m_modifiedPaint[i] = i % 2 == 0;
        }
        hm.RebuildTerrain();
        var before = TerrainSnapshot.Of(tc);
        EastWestRoad(-48f, 48f);
        Spawn(0);

        // The reach the writer itself declares, plus one vertex for rounding onto the grid.
        float reach = RoadTerrainModifier.MaxInfluenceRadius(Width) + hm.m_scale;
        TerrainAssert.OnlyChangedWithin(before, tc, (x, z) => Mathf.Abs(z) <= reach, "edited zone");
        // The check is not vacuous: most of the zone lies outside the reach.
        Assert.True(reach < 16f, $"reach {reach:F1} m leaves too little of the zone outside it to test");
    }

    [Fact]
    public void ALateNeighbourIsWrittenToMatchTheSeam()
    {
        EastWestRoad(0f, 96f); // Crosses the seam at x = 32 between zones (0,0) and (1,0).
        var west = Load(0);
        Spawn(0);
        var east = Load(1); // Loads after the west zone was written.
        west.RebuildTerrain(); east.RebuildTerrain();
        Assert.Throws<TerrainAssertException>(() => TerrainAssert.SeamAgrees(west, east));

        Spawn(1);
        west.RebuildTerrain(); east.RebuildTerrain();
        TerrainAssert.SeamAgrees(west, east);
        // The road really reached the seam: its paint is on the shared vertex under the centreline.
        int shared = 32 * 65 + 64;
        Assert.True(west.m_terrainComp!.m_modifiedPaint[shared], "the road did not reach the seam, so the check compared untouched ground");
    }
}
