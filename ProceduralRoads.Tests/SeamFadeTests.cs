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
    private const float RoadWidth = RoadConstants.DefaultRoadWidth;
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

    private TerrainComp Write(Vector2s zone, params RoadSpatialGrid.RoadPoint[] more)
    {
        Heightmap hm = _world.RegisterHeightmap(zone, Width);
        TerrainComp tc = hm.m_terrainComp!;
        for (int i = 0; i < tc.m_paintMask.Length; i++)
            tc.m_paintMask[i] = new Color(0f, 0f, 0f, 0.25f);
        List<RoadSpatialGrid.RoadPoint> points = Road();
        points.AddRange(more);
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, tc);
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
        NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
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
    /// The kept zone holds an earlier network's cut along the border they share (x = 32), and its paint: the zone written
    /// now starts its fade from that ground, so the two hold the same heights and paint on every shared vertex, whether
    /// the kept zone's compiler is live or only saved. Started from the generator's ground, the border stood 1.86 m
    /// above the kept zone's, a step that opened a hole at the seam.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheFadeStartsFromTheGroundAKeptZoneHoldsAtTheBorder(bool keptLive)
    {
        _world.WithZdos();
        const float Cut = -1.86f;
        Heightmap kept = _world.RegisterHeightmap(Explored, Width);
        TerrainComp keptTc = kept.m_terrainComp!;
        for (int i = 0; i < keptTc.m_paintMask.Length; i++)
            keptTc.m_paintMask[i] = new Color(0f, 0f, 0f, 0.25f);
        for (int z = -12; z <= 12; z++)
        {
            int i = (z + Width / 2) * (Width + 1);   // the kept zone's west edge, x = 32
            keptTc.m_levelDelta[i] = Cut;
            keptTc.m_modifiedHeight[i] = true;
            if (Mathf.Abs(z) <= 2) { keptTc.m_paintMask[i] = Heightmap.m_paintMaskPaved; keptTc.m_modifiedPaint[i] = true; }
        }
        keptTc.Save();
        kept.RebuildTerrain();
        if (!keptLive) Heightmap.s_heightmaps.Remove(kept);

        ExcludeOnANewNetwork(Explored);
        TerrainComp written = Write(Writable);
        written.m_hmap.RebuildTerrain();
        if (!keptLive) Heightmap.s_heightmaps.Add(kept);
        TerrainAssert.SeamAgrees(written.m_hmap, kept);
        Assert.Equal(Cut, written.m_levelDelta[Index(32, 10)], 4);

        // Writing again gives the same ground: the fade is written from the kept ground, not from what this zone holds.
        object[] first = Terrain(written);
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(Writable, Road(), written.m_hmap, written);
        Assert.Equal(first[0], Terrain(written)[0]);   // level deltas
        Assert.Equal(first[1], Terrain(written)[1]);   // smooth deltas
        Assert.Equal(first[2], Terrain(written)[2]);   // modified flags
    }

    /// <summary>
    /// A location's footprint in the fade, no road onto its landing: its ground is left as it is, as anywhere in a footprint,
    /// though the kept zone holds a cut beside it. Only on the shared border is it written, where the two zones must agree.
    /// </summary>
    [Fact]
    public void AFootprintInTheFadeKeepsItsGroundButOnTheBorder()
    {
        _world.WithZdos();
        const float Cut = -1.86f;
        Heightmap kept = _world.RegisterHeightmap(Explored, Width);
        TerrainComp keptTc = kept.m_terrainComp!;
        for (int z = -30; z <= 30; z++)
        {
            int i = (z + Width / 2) * (Width + 1);   // the kept zone's west edge, x = 32
            keptTc.m_levelDelta[i] = Cut;
            keptTc.m_modifiedHeight[i] = true;
        }
        keptTc.Save();
        kept.RebuildTerrain();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(30f, 20f), 4f) });
        try
        {
            ExcludeOnANewNetwork(Explored);
            TerrainComp written = Write(Writable);
            Assert.False(written.m_modifiedHeight[Index(28, 20)], "a footprint vertex in the fade");
            Assert.Equal(0f, written.m_levelDelta[Index(28, 20)]);
            Assert.Equal(Cut, written.m_levelDelta[Index(32, 20)], 4);
            // Outside the footprint the fade runs from the kept cut as before.
            Assert.True(written.m_modifiedHeight[Index(28, 10)]);
        }
        finally { RoadLocationProtection.Reset(); }
    }

    /// <summary>
    /// The same footprint in the fade, with a road onto another location's landing in the zone: away from that road its ground
    /// is still left as it is. Only the road onto the landing and the shared border write inside a footprint; before, the
    /// fade moved the footprint's ground under its pieces wherever the zone held any road onto a landing.
    /// </summary>
    [Fact]
    public void AFootprintInTheFadeKeepsItsGroundBesideARoadOntoAnotherLanding()
    {
        _world.WithZdos();
        const float Cut = -1.86f;
        Heightmap kept = _world.RegisterHeightmap(Explored, Width);
        TerrainComp keptTc = kept.m_terrainComp!;
        for (int z = -30; z <= 30; z++)
        {
            int i = (z + Width / 2) * (Width + 1);   // the kept zone's west edge, x = 32
            keptTc.m_levelDelta[i] = Cut;
            keptTc.m_modifiedHeight[i] = true;
        }
        keptTc.Save();
        kept.RebuildTerrain();
        var landing = new Vector2(-20f, 20f);
        RoadLocationProtection.Set(new[]
        {
            new RoadLocationProtection.Footprint(new Vector2(30f, 20f), 4f), new RoadLocationProtection.Footprint(landing, 4f),
        });
        try
        {
            ExcludeOnANewNetwork(Explored);
            float natural = BiomeBlendedHeight.GetBlendedHeight(landing.x, landing.y, WorldGenerator.instance!);
            // The other location's ground, published by its preload.
            PlacedLocations.Preload(landing, (x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, WorldGenerator.instance!));
            TerrainComp written = Write(Writable, new RoadSpatialGrid.RoadPoint(landing, RoadWidth, natural + Lift, intoLocation: true, location: landing));
            Assert.True(written.m_modifiedHeight[Index(-20, 20)], "the road onto the other landing writes its footprint");
            Assert.False(written.m_modifiedHeight[Index(28, 20)], "a footprint vertex in the fade, away from the road onto a landing");
            Assert.Equal(0f, written.m_levelDelta[Index(28, 20)]);
            Assert.Equal(Cut, written.m_levelDelta[Index(32, 20)], 4);
            Assert.True(written.m_modifiedHeight[Index(28, 10)]);
        }
        finally { PlacedLocations.Reset(); RoadLocationProtection.Reset(); }
    }

    /// <summary>
    /// Paint in the fade is held to the ground the fade writes. Halfway through it, next to a kept zone holding a 3 m cut along
    /// the road, the road's surface is the cut walked half out: painted, where the paint read half the road's own lift as its
    /// surface, 1.5 m over the ground, and skipped the whole fade. Next to a 3 m fill held under the road's middle only, the
    /// road's middle stands 1.5 m over the ground a metre off it: that ground is a face and is not painted, where the paint
    /// read its surface 1.5 m under the fill and painted it.
    /// </summary>
    [Theory]
    [InlineData(-3f)]
    [InlineData(3f)]
    public void PaintInTheFadeFollowsTheGroundItWrites(float held)
    {
        _world.WithZdos();
        Heightmap kept = _world.RegisterHeightmap(Explored, Width);
        TerrainComp keptTc = kept.m_terrainComp!;
        int rows = held < 0f ? 12 : 0;
        for (int z = -rows; z <= rows; z++)
        {
            int i = (z + Width / 2) * (Width + 1);   // the kept zone's west edge, x = 32
            keptTc.m_levelDelta[i] = held;
            keptTc.m_modifiedHeight[i] = true;
        }
        keptTc.Save();
        kept.RebuildTerrain();
        float face = RoadTerrainModifier.PaintFace;
        RoadTerrainModifier.PaintFace = 0f;   // the paint's own height test alone
        try
        {
            int mid = Index(32 - (int)(NetworkCutoff.SeamFadeDistance / 2f), 0);
            int beside = Index(32 - (int)(NetworkCutoff.SeamFadeDistance / 2f), 1);
            float road = Write(Writable).m_levelDelta[mid];
            Assert.True(road > 0.3f, $"the road lifts the ground here ({road:F2} m)");
            ExcludeOnANewNetwork(Explored);
            TerrainComp written = Write(Writable);
            // The ground written: the kept change walked half out to the road's.
            Assert.Equal(held + 0.5f * (road - held), written.m_levelDelta[mid], 3);
            Assert.True(written.m_modifiedPaint[mid] && Paint(written.m_paintMask[mid]) > 0f, "the road's middle mid-fade is painted");
            if (held < 0f)
                Assert.True(written.m_modifiedPaint[beside] && Paint(written.m_paintMask[beside]) > 0f, "beside the middle, on the cut walked out");
            else
            {
                Assert.True(written.m_levelDelta[mid] - written.m_levelDelta[beside] > RoadTerrainModifier.PaintBelow, "a face beside the held fill");
                Assert.False(written.m_modifiedPaint[beside], "the face under the held fill is painted");
            }
        }
        finally { RoadTerrainModifier.PaintFace = face; }
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
