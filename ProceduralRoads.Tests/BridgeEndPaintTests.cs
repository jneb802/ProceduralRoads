using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The paint stops at a bridge end's stairs' foot. Past it, under the stairs and under the deck's end, the road is
/// levelled (the stairs stand on level ground, no terrain pokes through the deck) but not painted
/// (RoadSpatialGrid.RoadPoint.unpainted, StructureEnds.Unpainted). An end met level stops its paint at the deck's edge.
/// </summary>
public sealed class BridgeEndPaintTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public BridgeEndPaintTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.PieceCommitted = null;
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pathfinder);

    private const int Width = 64;
    private static int Index(int x, int z) => (z + Width / 2) * (Width + 1) + (x + Width / 2);

    /// <summary>A 4 m road along z = 0, 1 m over flat ground, from x = -40 to 6; from x = 0.5 on its points are
    /// <paramref name="unpaintedPast"/>. The terrain writer's result.</summary>
    private static TerrainComp Write(bool unpaintedPast)
    {
        using var scope = new ValheimWorldScope();
        using var plain = PlainEarthworks.Apply();
        scope.WithWorld(new SyntheticWorld { HasRiver = false, HasMountain = false });
        var zone = new Vector2s(0, 0);
        Heightmap hm = scope.RegisterHeightmap(zone, Width);
        TerrainComp tc = hm.m_terrainComp!;
        float natural = BiomeBlendedHeight.GetBlendedHeight(0f, 0f, WorldGenerator.instance);
        var points = new List<RoadSpatialGrid.RoadPoint>();
        for (float x = -40f; x <= 6f; x += 0.5f)
            points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(x, 0f), 4f, natural + 1f, unpainted: unpaintedPast && x > 0.25f));
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, tc);
        return tc;
    }

    /// <summary>The terrain writer levels toward unpainted points as toward any road point, and paints none of them: the
    /// paint ends within the paint blend of the last painted point. With every point painted the same road is painted on.</summary>
    [Fact]
    public void UnpaintedPointsAreLevelledNotPainted()
    {
        var tc = Write(unpaintedPast: true);
        Assert.True(tc.m_modifiedPaint[Index(-4, 0)] && tc.m_modifiedPaint[Index(0, 0)], "the painted road lost its paint");
        for (int x = 3; x <= 6; x++)
            Assert.False(tc.m_modifiedPaint[Index(x, 0)], $"painted at x = {x}, past the last painted point");
        Assert.True(tc.m_modifiedHeight[Index(4, 0)], "the unpainted road was not levelled");
        Assert.True(Mathf.Abs(tc.m_levelDelta[Index(4, 0)] - tc.m_levelDelta[Index(-4, 0)]) < 0.05f,
            $"the unpainted road stands {tc.m_levelDelta[Index(4, 0)]:F2} over the ground, the painted road {tc.m_levelDelta[Index(-4, 0)]:F2}");

        var painted = Write(unpaintedPast: false);
        Assert.True(painted.m_modifiedPaint[Index(4, 0)], "the control road is not painted at x = 4");
        Assert.Equal(painted.m_levelDelta[Index(4, 0)], tc.m_levelDelta[Index(4, 0)]);
    }

    /// <summary>
    /// A wooden bridge over a wide river, its east bank 1 m over the west, so both its ends are low over the road
    /// (StructureEnds.Low) and each has its one step: past each stairs' foot the stored road (under the bottom
    /// step at the foot's height, and the ground under the deck's end where it is land) is all unpainted, and every point
    /// before the foot is painted. Written to the terrain, the ground under the stairs and the deck's end is levelled and
    /// unpainted beyond the paint blend of the foot, and the road before the foot is painted.
    /// </summary>
    [Fact]
    public void PaintStopsAtAWoodenBridgesStairsFoot()
    {
        // The bridge ends walked to the water, as the mod lays them: the ground under a deck's end is levelled.
        using var toWater = StaticOverride.Keep(() => RoadCrossingDetector.ToWater);
        RoadCrossingDetector.ToWater = true;
        var world = new BridgeTests.WideRiverWorld { EastRise = 1f };
        _scope.WithWorld(world);
        RoadCrossingDetector.SetFordStyleWeights(1f, 1f, 1f);
        SetPathfinder(new RoadPathfinder(world) { Fords = true, Bridges = true, MaxGrade = 0f });
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-200f, -40f), 0f, new Vector2(200f, 40f), 0f, 4f, "across the river"),
            RoadSpatialGrid.LastRefusal);
        RoadCrossing c = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), x => x.Kind == CrossingKind.Bridge);
        Assert.False(DvergrViaduct.Applies(c, world));
        var (near, far) = BridgeStructures.Ends(c, world);
        int deckEnds = 0, stepped = 0;
        foreach (var (end, which) in new[] { (near, "near"), (far, "far") })
        {
            if (end.Steps == 0) continue;
            stepped++;
            Vector2 across = new(-end.Up.y, end.Up.x);
            float stairs = BridgeLayout.StairRun * end.Steps;
            int underStairs = 0, before = 0;
            bool underDeck = false;
            foreach (var rp in RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(end.Meet.x, 0f, end.Meet.y), stairs + 12f))
            {
                if (!rp.IsRoad) continue;
                float along = Vector2.Dot(rp.p - end.Meet, end.Up), off = Vector2.Dot(rp.p - end.Meet, across);
                if (Mathf.Abs(off) > 2f) continue;
                if (along > 0.25f)
                {
                    Assert.True(rp.unpainted, $"{which} end: painted road at {rp.p}, {along:F2} m past the stairs' foot {end.Meet}");
                    if (along <= StructureEnds.JoinOverlap + 0.25f && Mathf.Abs(rp.h - end.MeetHeight) <= 0.1f) underStairs++;
                    underDeck |= along > stairs;
                }
                else if (along > -StructureEnds.ApronLength)
                {
                    Assert.False(rp.unpainted, $"{which} end: unpainted road at {rp.p}, {-along:F2} m before the stairs' foot");
                    before++;
                }
            }
            Assert.True(underStairs > 0, $"{which} end: no levelled road under the bottom step");
            if (underDeck) deckEnds++;
            Assert.True(before > 0, $"{which} end: no road before the stairs' foot");

            // The terrain writer on the zone of the foot: unpainted past the foot's paint blend, painted before it.
            Vector2s zone = ZoneSystem.GetZone(new Vector3(end.Meet.x, 0f, end.Meet.y));
            Heightmap hm = _scope.RegisterHeightmap(zone, Width);
            TerrainComp tc = hm.m_terrainComp!;
            RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, RoadSpatialGrid.GetRoadPointsInZone(zone), hm, tc);
            Vector3 origin = hm.transform.position;
            bool Cell(Vector2 at, out int index)
            {
                int vx = Mathf.RoundToInt(at.x - origin.x) + Width / 2, vz = Mathf.RoundToInt(at.y - origin.z) + Width / 2;
                index = vz * (Width + 1) + vx;
                return vx >= 0 && vz >= 0 && vx <= Width && vz <= Width;
            }
            int checkedPast = 0;
            for (float along = 2.5f; along <= stairs + 2f; along += 0.5f)
                if (Cell(end.Meet + end.Up * along, out int i))
                {
                    checkedPast++;
                    Assert.False(tc.m_modifiedPaint[i], $"{which} end: painted {along:F1} m past the stairs' foot {end.Meet}");
                    Assert.True(tc.m_modifiedHeight[i], $"{which} end: not levelled {along:F1} m past the stairs' foot {end.Meet}");
                }
            Assert.True(checkedPast > 0, $"{which} end: no cell past the foot in zone {zone}");
            if (Cell(end.Meet - end.Up * 3f, out int foot))
                Assert.True(tc.m_modifiedPaint[foot], $"{which} end: the road 3 m before the stairs' foot is not painted");
        }
        Assert.True(stepped > 0, "neither end has stairs");
        // Ground under a deck's end is levelled only over land: the west bank's (the east bank's shelf is under the waterline).
        Assert.True(deckEnds > 0, "no levelled ground under either deck's end");
    }

    /// <summary>
    /// An end met level (no stairs): its road runs on under the deck's end module at the deck's height,
    /// levelled there but unpainted, the paint stopping at the deck's edge as it stops at a stairs' foot: at every end, no
    /// painted road point inside the deck's edge or past the foot, and the road before it painted.
    /// </summary>
    [Fact]
    public void AnEndMetLevelStopsItsPaintAtTheDecksEdge()
    {
        var world = new StructureEndTests.BankRisingBeside();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, Viaducts = true, Bridges = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-45f, 0f, -40f), 0f, new Vector3(70f, 0f, 0f), 0f, 4f, "from the side"), RoadSpatialGrid.LastRefusal);
        RoadCrossing c = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), x => x.Kind == CrossingKind.Bridge);
        var (near, far) = BridgeStructures.Ends(c, world);
        Assert.Equal(0, near.Steps);
        foreach (var (end, which) in new[] { (near, "near"), (far, "far") })
        {
            var around = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(end.Meet.x, 0f, end.Meet.y), 6f).Where(rp => rp.IsRoad).ToList();
            var under = around.Where(rp => end.UnderTheStructure(rp.p, 2f)).ToList();
            Assert.True(under.Count > 0, $"{which} end: no road run on under the structure");
            Assert.All(under, rp => Assert.True(rp.unpainted, $"{which} end ({end.Steps} steps): the road at {rp.p}, under the structure, is painted"));
            Assert.All(under, rp => Assert.True(Mathf.Abs(rp.h - end.MeetHeight) <= 0.1f, $"{which} end: the road under the structure at {rp.p} stands {rp.h:F2}, the end {end.MeetHeight:F2}"));
            var before = around.Where(rp => Vector2.Dot(rp.p - end.Meet, end.Up) < -1f).ToList();
            Assert.NotEmpty(before);
            Assert.All(before, rp => Assert.False(rp.unpainted, $"{which} end: the road before it at {rp.p} is unpainted"));
        }
    }
}
