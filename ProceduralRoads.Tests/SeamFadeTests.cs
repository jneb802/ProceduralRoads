using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road that leaves a zone left as it is (NetworkCutoff) for a zone the network writes fades in from the border:
/// no change on the border, part of the road part of the way, the full road past the fade. A world with nothing
/// excluded is written exactly as before, and the excluded zone itself is never written.
/// </summary>
public sealed class SeamFadeTests : IDisposable
{
    private const int Width = 64;
    private const float RoadWidth = 4f;
    private const float Lift = 1.5f;

    private static readonly Vector2s Writable = new(0, 0);
    /// <summary>East of the writable zone: their shared border is x = 32.</summary>
    private static readonly Vector2s Explored = new(1, 0);

    private readonly ValheimWorldScope _world = new ValheimWorldScope()
        .WithWorld(new SyntheticWorld { HasRiver = false, HasMountain = false }).WithZoneSystem();
    private readonly StaticOverride _plain = PlainEarthworks.Apply();

    public SeamFadeTests()
    {
        RoadSpatialGrid.Clear(); RoadTerrainModifier.ResetForWorld();
        NetworkCutoff.Forget();
    }

    public void Dispose()
    {
        NetworkCutoff.Forget();
        RoadSpatialGrid.Clear(); RoadTerrainModifier.ResetForWorld();
        _plain.Dispose();
        _world.Dispose();
    }

    /// <summary>An east-west road along z = 0 through both zones, standing <see cref="Lift"/> above the ground.</summary>
    private static List<RoadSpatialGrid.RoadPoint> Road()
    {
        float natural = BiomeBlendedHeight.GetBlendedHeight(0f, 0f, WorldGenerator.instance!);
        var points = new List<RoadSpatialGrid.RoadPoint>();
        for (float x = -40f; x <= 100f; x += 1f)
            points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(x, 0f), RoadWidth, natural + Lift));
        return points;
    }

    private TerrainComp Write(Vector2s zone)
    {
        Heightmap hm = _world.RegisterHeightmap(zone, Width);
        TerrainComp tc = hm.m_terrainComp!;
        for (int i = 0; i < tc.m_paintMask.Length; i++)
            tc.m_paintMask[i] = new Color(0f, 0f, 0f, 0.25f);
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, Road(), hm, tc);
        return tc;
    }

    /// <summary>Vertex index for (x, z) metres from the writable zone's centre.</summary>
    private static int Index(int x, int z) => (z + Width / 2) * (Width + 1) + (x + Width / 2);

    private static object[] Terrain(TerrainComp tc) => new object[]
    {
        tc.m_levelDelta.Clone(), tc.m_smoothDelta.Clone(), tc.m_modifiedHeight.Clone(),
        tc.m_paintMask.Clone(), tc.m_modifiedPaint.Clone(),
    };

    private static float Paint(Color c) => c.r + c.g + c.b;

    private static void ExcludeOnANewNetwork(params Vector2s[] explored)
    {
        NetworkCutoff.SnapshotWorld(explored);
        NetworkCutoff.UseNewNetwork();
    }

    [Fact]
    public void TheRoadFadesInFromTheBorderOfAnExcludedZone()
    {
        TerrainComp full = Write(Writable);
        ExcludeOnANewNetwork(Explored);
        TerrainComp faded = Write(Writable);

        // On the shared border: the road changes nothing, height or paint.
        int border = Index(32, 0);
        Assert.True(full.m_modifiedHeight[border] && full.m_modifiedPaint[border], "the unfaded road reaches the border");
        Assert.False(faded.m_modifiedHeight[border]);
        Assert.Equal(0f, faded.m_levelDelta[border]);
        Assert.False(faded.m_modifiedPaint[border]);

        // Halfway through the fade (8 m from the border): half the road's change.
        int mid = Index(32 - (int)(NetworkCutoff.SeamFadeDistance / 2f), 0);
        Assert.True(full.m_levelDelta[mid] > 0.3f, $"the road lifts the ground here ({full.m_levelDelta[mid]:F2} m)");
        Assert.Equal(full.m_levelDelta[mid] * 0.5f, faded.m_levelDelta[mid], 3);
        // The paint starts from black: how far it moved toward the road's colour.
        float fullPaint = Paint(full.m_paintMask[mid]), fadedPaint = Paint(faded.m_paintMask[mid]);
        Assert.True(fullPaint > 0.01f, "the road paints here");
        Assert.True(fadedPaint > 0f && fadedPaint < fullPaint,
            $"paint mid-fade {fadedPaint:F3} against {fullPaint:F3}");
        Assert.Equal(full.m_paintMask[mid].a, faded.m_paintMask[mid].a);

        // Past the fade: the full road, exactly.
        for (int x = -32; x < 32 - (int)NetworkCutoff.SeamFadeDistance; x++)
            for (int z = -4; z <= 4; z++)
            {
                int i = Index(x, z);
                Assert.Equal(full.m_levelDelta[i], faded.m_levelDelta[i]);
                Assert.Equal(full.m_paintMask[i], faded.m_paintMask[i]);
            }
    }

    [Fact]
    public void AWorldWithNothingExcludedIsWrittenExactlyAsBefore()
    {
        object[] noCutoff = Terrain(Write(Writable));

        ExcludeOnANewNetwork();   // a fresh world: nothing explored
        Assert.Equal(noCutoff, Terrain(Write(Writable)));

        ExcludeOnANewNetwork(new Vector2s(3, 0), new Vector2s(0, 2));   // explored, but out of the fade's reach
        Assert.Equal(noCutoff, Terrain(Write(Writable)));
    }

    [Fact]
    public void TheExcludedZoneItselfIsNeverWritten()
    {
        var path = new List<Vector2>();
        for (float x = -40f; x <= 100f; x += 4f) path.Add(new Vector2(x, 0f));
        RoadSpatialGrid.AddRoadPath(path, RoadWidth, WorldGenerator.instance!);
        RoadSpatialGrid.FinalizeRoadNetwork();
        List<RoadSpatialGrid.RoadPoint> points = RoadSpatialGrid.GetRoadPointsInZone(Explored);
        Assert.NotEmpty(points);

        ExcludeOnANewNetwork(Explored);
        Heightmap hm = _world.RegisterHeightmap(Explored, Width);
        TerrainComp tc = hm.m_terrainComp!;
        object[] before = Terrain(tc);

        RoadTerrainModifier.OnZoneSpawned(Explored, points);
        RoadTerrainModifier.OnTerrainCompilerReady(tc);
        RoadTerrainModifier.ApplyRoadTerrainMods(Explored, points);
        RoadTerrainModifier.ApplyToLoadedZones();

        Assert.Equal(before, Terrain(tc));
        Assert.Equal(0, tc.SaveCount);

        // The same writers do write it once nothing is excluded.
        NetworkCutoff.Forget();
        RoadTerrainModifier.OnZoneSpawned(Explored, points);
        Assert.True(tc.SaveCount > 0);
    }

    [Fact]
    public void TheFadeIsMeasuredToTheExcludedSquareNotAlongTheRoad()
    {
        // Only the zone diagonally north-east is excluded: it touches the writable zone at its corner (32, 32).
        var excluded = new HashSet<Vector2s> { new(1, 1) };
        float d = NetworkCutoff.SeamFadeDistance;
        Assert.Equal(0f, NetworkCutoff.SeamWeight(new Vector2(32f, 32f), excluded));
        Assert.Equal(Mathf.Sqrt(8f * 8f + 8f * 8f) / d, NetworkCutoff.SeamWeight(new Vector2(24f, 24f), excluded), 4);
        Assert.Equal(1f, NetworkCutoff.SeamWeight(new Vector2(24f, 0f), excluded));   // 32 m south of the square
        Assert.Equal(0.25f, NetworkCutoff.SeamWeight(new Vector2(28f, 40f), excluded), 4);   // 4 m west of it
        Assert.Equal(1f, NetworkCutoff.SeamWeight(new Vector2(0f, 0f), new HashSet<Vector2s>()));
    }

    /// <summary>
    /// road_generate and road_regen_island admit the excluded zones they write (like a manual road): every writer
    /// writes them from then on, they are not an edge for the seam fade, and the operator is told which they are.
    /// </summary>
    [Fact]
    public void AnOperatorsWriteAdmitsTheZonesItWritesAndSaysSo()
    {
        var path = new List<Vector2>();
        for (float x = -40f; x <= 100f; x += 4f) path.Add(new Vector2(x, 0f));
        RoadSpatialGrid.AddRoadPath(path, RoadWidth, WorldGenerator.instance!);
        RoadSpatialGrid.FinalizeRoadNetwork();
        ExcludeOnANewNetwork(Explored);
        Assert.True(NetworkCutoff.SeamWeight(new Vector2(30f, 0f)) < 1f);

        Heightmap hm = _world.RegisterHeightmap(Explored, Width);
        var log = new List<string>();
        BepInEx.Logging.ManualLogSource.Captured = log;
        try
        {
            RoadTerrainModifier.ApplyToLoadedZones(operatorRequest: true, out List<Vector2s> admitted);
            Assert.Equal(new[] { Explored }, admitted);
            Assert.Contains(log, line => line.Contains(NetworkCutoff.AdmissionWarning("this command builds roads in", admitted)));
        }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; }
        Assert.True(hm.m_terrainComp!.SaveCount > 0, "the operator's write reaches the explored zone");
        Assert.False(NetworkCutoff.Excludes(Explored), "it is the network's now, for every writer");
        Assert.Equal(1f, NetworkCutoff.SeamWeight(new Vector2(30f, 0f)));

        // A zone off the loaded ones stays as it is, and the next session starts over from the record.
        NetworkCutoff.Forget();
        ExcludeOnANewNetwork(Explored);
        Assert.True(NetworkCutoff.SeamWeight(new Vector2(30f, 0f)) < 1f);
    }

    [Fact]
    public void WritingTheFadeAgainGivesTheSameGround()
    {
        ExcludeOnANewNetwork(Explored);
        TerrainComp once = Write(Writable);
        object[] first = Terrain(once);
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(Writable, Road(), Heightmap.Registered!, once);
        Assert.Equal(first[0], Terrain(once)[0]);   // level deltas
        Assert.Equal(first[1], Terrain(once)[1]);   // smooth deltas
    }
}
