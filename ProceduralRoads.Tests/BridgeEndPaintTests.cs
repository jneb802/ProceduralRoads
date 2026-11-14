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
/// At a bridge end's stairs' foot, road points past it are levelled (the stairs stand on level ground) but do not paint
/// (RoadSpatialGrid.RoadPoint.unpainted, StructureEnds.Unpainted). Paint from before the foot reaches only as far as its
/// vertex or grass-cell profile read asks. An end met level paints its road on under the deck's end, as any road.
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
    /// step at the foot's height) is all unpainted, and every point before the foot is painted; the ground under the deck is
    /// cut to its underside (BridgeStructures.GroundUnderDeck). Written to the terrain, the ground under the stairs is levelled
    /// and unpainted beyond the paint blend of the foot, and the road before the foot is painted.
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
        SetPathfinder(new RoadPathfinder(world) { Fords = true, Bridges = true });
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-200f, -40f), 0f, new Vector2(200f, 40f), 0f, 4f, "across the river"),
            RoadSpatialGrid.LastRefusal);
        RoadCrossing c = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), x => x.Kind == CrossingKind.Bridge);
        Assert.False(DvergrViaduct.Applies(c, world));
        var (near, far) = BridgeStructures.Ends(c, world);
        int stepped = 0;
        float depth = BridgeStructures.DeckDepth(c, world);
        foreach (var (end, which) in new[] { (near, "near"), (far, "far") })
        {
            if (end.Steps == 0) continue;
            stepped++;
            Vector2 across = new(-end.Up.y, end.Up.x);
            float stairs = StairLayout.PieceRun * end.Steps;
            int underStairs = 0, before = 0;
            foreach (var rp in RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(end.Meet.x, 0f, end.Meet.y), stairs + 12f))
            {
                if (!rp.IsRoad) continue;
                float along = Vector2.Dot(rp.p - end.Meet, end.Up), off = Vector2.Dot(rp.p - end.Meet, across);
                if (Mathf.Abs(off) > 2f) continue;
                if (along > 0.25f)
                {
                    Assert.True(rp.unpainted, $"{which} end: painted road at {rp.p}, {along:F2} m past the stairs' foot {end.Meet}");
                    if (along <= StructureEnds.JoinOverlap + 0.25f && Mathf.Abs(rp.h - end.MeetHeight) <= 0.1f) underStairs++;
                }
                else if (along > -StructureEnds.ApronLength)
                {
                    Assert.False(rp.unpainted, $"{which} end: unpainted road at {rp.p}, {-along:F2} m before the stairs' foot");
                    before++;
                }
            }
            Assert.True(underStairs > 0, $"{which} end: no levelled road under the bottom step");
            // The ground under the deck, past the cuts' batter from its edge, cut to the deck's underside.
            Vector2 inDeck = end.Edge + end.Up * (RoadNetworkGenerator.CrossingGround.CutWidth * 0.5f + depth + 1f);
            Assert.Contains(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(inDeck.x, 0f, inDeck.y), 1.1f),
                rp => rp.cutOnly && Mathf.Abs(rp.h - (end.Height - depth)) < 1e-3f);
            Assert.True(before > 0, $"{which} end: no road before the stairs' foot");

            // The terrain writer on the zone of the foot: only painted road points contribute at either profile read.
            Vector2s zone = ZoneSystem.GetZone(new Vector3(end.Meet.x, 0f, end.Meet.y));
            Heightmap hm = _scope.RegisterHeightmap(zone, Width);
            TerrainComp tc = hm.m_terrainComp!;
            RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, RoadSpatialGrid.GetRoadPointsInZone(zone), hm, tc);
            var paintingPoints = RoadSpatialGrid.GetRoadPointsInZone(zone)
                .Where(p => !p.cutOnly && !p.unpainted && !p.mound).ToList();
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
                    // Snap to the texel actually checked: toward -x/-z its grass-cell middle can still
                    // meet the last painted point's fade. Beyond both reads it must stay unpainted.
                    var vertex = new Vector2(origin.x + i % (Width + 1) - Width / 2,
                        origin.z + i / (Width + 1) - Width / 2);
                    bool asksForPaint = paintingPoints.Any(p => Mathf.Max(
                        RoadProfile.PaintStrength(Vector2.Distance(vertex, p.p), p.w),
                        RoadProfile.PaintStrength(Vector2.Distance(vertex + new Vector2(0.5f, 0.5f), p.p), p.w)) > 0f);
                    Assert.Equal(asksForPaint, tc.m_modifiedPaint[i]);
                    Assert.True(tc.m_modifiedHeight[i], $"{which} end: not levelled {along:F1} m past the stairs' foot {end.Meet}");
                }
            Assert.True(checkedPast > 0, $"{which} end: no cell past the foot in zone {zone}");
            if (Cell(end.Meet - end.Up * 3f, out int foot))
                Assert.True(tc.m_modifiedPaint[foot], $"{which} end: the road 3 m before the stairs' foot is not painted");
        }
        Assert.True(stepped > 0, "neither end has stairs");
    }

    /// <summary>
    /// An end met level (no stairs): its road runs on under the deck's end at the deck's height, levelled and painted like
    /// any road (StructureEnds.Unpainted: only the road under an end's stairs is unpainted), and the road before it painted.
    /// A viaduct between banks at its deck's height is met level at both ends.
    /// </summary>
    [Fact]
    public void AnEndMetLevelPaintsItsRoadOnUnderTheDecksEnd()
    {
        var level = new DvergrViaductEndStairTests.StepValley { Depth = _ => 18f };
        _scope.WithWorld(level);
        SetPathfinder(new RoadPathfinder(level) { Stairs = true, Viaducts = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-110f, 0f, -50f), 0f, new Vector3(110f, 0f, 50f), 0f, 4f, "level"), RoadSpatialGrid.LastRefusal);
        RoadCrossing viaduct = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), x => x.ValleyJump);
        var (vn, vf) = BridgeStructures.Ends(viaduct, level);
        Assert.Equal((0, 0), (vn.Steps, vf.Steps));
        foreach (var (end, which) in new[] { (vn, "near"), (vf, "far") })
        {
            var around = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(end.Meet.x, 0f, end.Meet.y), 6f).Where(rp => rp.IsRoad).ToList();
            Vector2 across = new(-end.Up.y, end.Up.x);
            bool Under(Vector2 p)
            {
                float along = Vector2.Dot(p - end.Meet, end.Up);
                return along > 0.25f && along <= StructureEnds.JoinOverlap + 0.25f && Mathf.Abs(Vector2.Dot(p - end.Meet, across)) <= 2f;
            }
            var under = around.Where(rp => Under(rp.p)).ToList();
            Assert.True(under.Count > 0, $"{which} end: no road run on under the deck's end");
            Assert.All(under, rp => Assert.False(rp.unpainted, $"{which} end: the road at {rp.p}, under the deck's end, is unpainted"));
            Assert.All(under, rp => Assert.True(Mathf.Abs(rp.h - end.MeetHeight) <= 0.1f, $"{which} end: the road under the deck's end at {rp.p} stands {rp.h:F2}, the end {end.MeetHeight:F2}"));
            var before = around.Where(rp => Vector2.Dot(rp.p - end.Meet, end.Up) < -1f).ToList();
            Assert.NotEmpty(before);
            Assert.All(before, rp => Assert.False(rp.unpainted, $"{which} end: the road before it at {rp.p} is unpainted"));
        }
    }

    /// <summary>
    /// A road arriving at a deck met level, its points in the order it was laid (towards the deck, every 0.5 m, as the
    /// paint pass meets them), run on under the deck's end at its height: the ground is painted (cleared of grass, paved
    /// over half) at the deck's edge and on under its end. Before, the road under the deck's end was unpainted and the
    /// paint pass, which then painted one point of every 1.5 m it reached, left the edge
    /// painted only by a point up to 1.5 m before it: at one bridge the paint faded out 1-2 m short of the planks, grass
    /// over the road's end and up through the deck.
    /// </summary>
    [Fact]
    public void ARoadArrivingAtADeckMetLevelIsPaintedToAndUnderItsEnd()
    {
        using var scope = new ValheimWorldScope();
        using var plain = PlainEarthworks.Apply();
        scope.WithWorld(new SyntheticWorld { HasRiver = false, HasMountain = false });
        var zone = new Vector2s(0, 0);
        Heightmap hm = scope.RegisterHeightmap(zone, Width);
        TerrainComp tc = hm.m_terrainComp!;
        float natural = BiomeBlendedHeight.GetBlendedHeight(0f, 0f, WorldGenerator.instance);
        // The deck's edge at the origin, the deck to the west; the road comes from the east.
        var end = new StructureEnds.End(new Vector2(0f, 0f), new Vector2(-1f, 0f), natural + 1f, 0);
        var line = new List<Vector2>();
        for (float x = 30f; x >= -StructureEnds.JoinOverlap - 0.01f; x -= 0.5f) line.Add(new Vector2(x, 0f));
        List<bool>? unpainted = StructureEnds.Unpainted(line, new[] { end }, 2f);
        var points = new List<RoadSpatialGrid.RoadPoint>();
        for (int i = 0; i < line.Count; i++)
            points.Add(new RoadSpatialGrid.RoadPoint(line[i], 4f, end.MeetHeight, unpainted: unpainted?[i] ?? false));
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, tc);
        foreach (var (x, z) in new[] { (1, 0), (0, -1), (0, 0), (0, 1), (-1, 0), (-2, 0) })
        {
            int i = Index(x, z);
            float paved = tc.m_modifiedPaint[i] ? tc.m_paintMask[i].b : 0f;
            Assert.True(paved >= 0.5f, $"paved {paved:F2} at ({x}, {z}), {-x} m past the deck's edge");
        }
    }
}
