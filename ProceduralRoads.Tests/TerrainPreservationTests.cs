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

    public TerrainPreservationTests() { RoadTerrainModifier.ResetForWorld(); RoadSpatialGrid.Clear(); }
    public void Dispose() { RoadTerrainModifier.ResetForWorld(); RoadSpatialGrid.Clear(); _world.Dispose(); }

    private Heightmap Load(int zoneX)
    {
        var hm = _world.RegisterHeightmap(new Vector2s(zoneX, 0));
        hm.AuthoredHeight = CrossSlope.GetHeight;
        return hm;
    }

    private static void Spawn(int zoneX) =>
        RoadTerrainModifier.OnZoneSpawned(new Vector2s(zoneX, 0), RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(zoneX, 0)));



}
