using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A staircase clears only its walking box (assistant-written, 5 Nov 2026): its walk's width, from just over its walking
/// surface to a player's headroom over it (StairLayout.AddClearance). Rock a support or handrail stands in stays, a rock over a
/// tread goes, its clear areas leave a rock the rock clearing takes to that box, and its cuts never lower the ground under a
/// walk below just under it (a hollow under a flight beside its supporting wall).
/// </summary>
public class StairClearanceTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    private sealed class FlightWorld : SyntheticWorld
    {
        public override float GetHeight(float x, float z) => Ground(x, z);
    }

    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Flat;
    }

    private const float Flat = 30f;

    public StairClearanceTests()
    {
        StairStructures.Reset();
        StructurePlans.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        StairStructures.Reset();
        StructurePlans.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    /// <summary>A slope a flight climbs, with a 3 m dip it crosses on supports.</summary>
    private static float Ground(float x, float z) => 0.5f * z - (z > 10f && z < 16f ? 3f : 0f);

    /// <summary>Whether a point stands inside a clearance box (its own frame turned by its yaw, local +z along the walk).</summary>
    private static bool Inside(StairClearance box, Vector3 p)
    {
        Vector3 d = p - box.Centre;
        var flat = new Vector2(d.x, d.z);
        return Mathf.Abs(Vector2.Dot(flat, StairLayout.Right(box.YawDegrees))) <= box.Half.x
            && Mathf.Abs(Vector2.Dot(flat, StairLayout.Forward(box.YawDegrees))) <= box.Half.z
            && Mathf.Abs(d.y) <= box.Half.y;
    }

    /// <summary>A walked piece's walk <paramref name="along"/> its footprint from its back edge, on its middle line (a stair's
    /// rising line there, a floor's top).</summary>
    private static Vector3 WalkAt(StructurePiece p, float along)
    {
        var (back, heading, _, _, height) = StairLayout.Footprint(StairRun.PieceOf(p));
        Vector2 at = back + StairLayout.Forward(heading) * along;
        bool stair = p.Kind == StructurePieceKind.StairStep;
        return new Vector3(at.x, height + (stair ? StairLayout.PieceRise * along / StairLayout.PieceRun : 0f), at.y);
    }

    [Fact]
    public void ARockItsSupportsStandInStaysAndARockOverATreadIsCleared()
    {
        _scope.WithWorld(new FlightWorld());
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 40), Ground);
        Assert.True(plan.Reached, plan.Reason);
        var run = StairRun.FromPlan(new Vector2(0, 0), new Vector2(0, 40), 2f, plan);
        StairStructures.Add(run);
        List<StructurePiece> laid = StairStructures.PiecesOf(run, WorldGenerator.instance!);
        List<StructurePiece> supports = laid.Where(p => p.Kind == StructurePieceKind.StairSupport).ToList();
        Assert.NotEmpty(supports);

        // Every support's origin, and the rock it stands in reaching up into the piece over it to just under its walk (the
        // piece's own body), is outside every box: kept.
        foreach (StructurePiece support in supports)
            Assert.DoesNotContain(run.Clearances, box => Inside(box, support.Position));
        // The sunk step under the foot is buried on purpose, its own walk a rise under the first step's: left out.
        List<StairClearance> boxes = run.Clearances.Skip(plan.SunkClearances).ToList();
        var up = new Vector3(0f, 1f, 0f);
        foreach (StructurePiece walked in run.Pieces.Skip(plan.SunkPieces).Where(p => p.Kind != StructurePieceKind.StairSupport))
        {
            // Just under the walk at the low end of each stretch (a stair's box starts over its stretch's lowest walk): kept.
            // (A stair's own boxes: at a turn a piece overlapping it is walked over it.)
            bool stair = walked.Kind == StructurePieceKind.StairStep;
            var (_, _, _, depth, _) = StairLayout.Footprint(StairRun.PieceOf(walked));
            int stretches = stair ? StairLayout.ClearanceStretches : 1;
            List<StairClearance> own = stair ? boxes.Where(box => Over(walked, new Vector2(box.Centre.x, box.Centre.z), box.YawDegrees)).ToList() : boxes;
            if (stair) Assert.Equal(stretches, own.Count);
            for (int i = 0; i < stretches; i++)
            {
                Vector3 under = WalkAt(walked, depth * i / stretches + RoadRockPolicy.ClearanceLift) - up * (StairLayout.CutBelowSurface / 3f);
                Assert.False(own.Any(box => Inside(box, under)), $"{walked.Kind} {walked.Prefab} at {walked.Position}: under its walk {under} in a box");
            }
            // A rock over its tread (three quarters along: inside a stretch), anywhere from just over the walk to a player's
            // height over it: cleared.
            Vector3 walk = WalkAt(walked, depth * 0.75f);
            foreach (float over in new[] { 2f * RoadRockPolicy.ClearanceLift, RoadRockPolicy.PlayerHeight / 2f, RoadRockPolicy.PlayerHeight })
                Assert.Contains(run.Clearances, box => Inside(box, walk + up * over));
        }
    }

    /// <summary>No ground anywhere: a road point is skipped, as where no terrain collider answers.</summary>
    private static bool NoGround(Vector3 at, out float height) { height = 0f; return false; }

    /// <summary>
    /// What the rock clearing takes round a staircase is its walking box and nothing over it: rock is fractured only for the
    /// walk's width and a player's height over it. The probe and the carve both read
    /// RockClearances, the one owner: there a staircase's boxes are the run's own stored walking boxes, the walk's width
    /// across and a player's headroom over the walk, each its own roof, so the carve takes no chunk hanging over the walk
    /// above that headroom (such chunks were a quarter of the chunks carved along a long mountain staircase); so is a
    /// road's. A stair's stretch is its corridor and its tread layer (RockClearances.AddBox), a level stretch its box as
    /// stored.
    /// </summary>
    [Fact]
    public void TheRockClearingTakesOnlyAStaircasesWalkingBoxAndNothingOverIt()
    {
        _scope.WithWorld(new FlightWorld());
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 40), Ground);
        Assert.True(plan.Reached, plan.Reason);
        var run = StairRun.FromPlan(new Vector2(0, 0), new Vector2(0, 40), RoadStairRuns.Width, plan);
        StairStructures.Add(run);

        List<RockClearance> boxes = RockClearances.Near(new Vector3(0f, 0f, 20f), 40f, generating: false, NoGround, out _, out _, out _);
        int rising = run.Clearances.Count(Rises);
        Assert.True(rising > 0);
        Assert.True(rising < run.Clearances.Count);
        Assert.Equal(run.Clearances.Count + rising, boxes.Count);
        float corridor = 2f * RockClearances.Corridor(RoadStairRuns.Width / 2f);
        foreach (StairClearance stored in run.Clearances)
        {
            List<RockClearance> mine = boxes.Where(b => Mathf.Abs(b.Centre.x - stored.Centre.x) < 1e-4f && Mathf.Abs(b.Centre.z - stored.Centre.z) < 1e-4f
                && Mathf.Abs(b.Centre.y - b.Half.y - (stored.Centre.y - stored.Half.y)) < 1e-3f
                && Mathf.Abs(b.Turn.eulerAngles.y - Quaternion.Euler(0f, stored.YawDegrees, 0f).eulerAngles.y) < 1e-3f).ToList();
            foreach (RockClearance box in mine)
            {
                Assert.False(box.HasPoint);
                Assert.False(box.WholeOnly);
                Assert.Equal(stored.Half.z, box.Half.z, 4);
            }
            float storedTop = stored.Centre.y + stored.Half.y;
            if (!Rises(stored))
            {
                // A level stretch: its box as stored, a player's headroom over its walk.
                RockClearance box = Assert.Single(mine);
                Assert.Equal(stored.Half, box.Half);
                Assert.Equal(RoadStairRuns.Width, 2f * box.Half.x, 3);
                Assert.Equal(StairLayout.ClearanceHeight, box.Top - box.Surface, 3);
                continue;
            }
            // A stair's: its corridor, the box's full height, and its tread layer, the walk's width to StepUp over its
            // highest walk.
            Assert.Equal(2, mine.Count);
            RockClearance inner = Assert.Single(mine, b => Mathf.Abs(2f * b.Half.x - corridor) < 1e-3f);
            RockClearance tread = Assert.Single(mine, b => Mathf.Abs(2f * b.Half.x - RoadStairRuns.Width) < 1e-3f);
            Assert.Equal(storedTop, inner.Top, 3);
            Assert.Equal(storedTop - StairLayout.ClearanceHeight + StairLayout.StepUp, tread.Top, 3);
        }

        // A road's clearance is a player's height over the road, its own roof too.
        var point = new RoadSpatialGrid.RoadPoint(new Vector2(0f, -10f), RoadSpatialGrid.DefaultRoadWidth, Flat);
        RockClearance road = RockClearances.Road(point, Flat, new List<RoadSpatialGrid.RoadPoint> { point }, RoadRockPolicy.ClearanceHeight,
            wholeOnly: false, keepsSiteRocks: false);
        Assert.True(road.HasPoint);
        Assert.Equal(RockClearances.Top(Flat), road.Top, 3);
        Assert.Equal(Flat + RoadRockPolicy.ClearanceLift + RoadRockPolicy.ClearanceHeight, road.Top, 3);
    }

    /// <summary>Whether a stored walking box is a stair's stretch: its top stands a player's headroom over a walk higher than
    /// its middle one.</summary>
    private static bool Rises(StairClearance box) => box.Centre.y + box.Half.y - StairLayout.ClearanceHeight > box.Surface + 0.01f;

    /// <summary>Whether a box centred at <paramref name="centre"/>, turned to <paramref name="yaw"/>, is one of a walked piece's
    /// own: standing over its footprint, turned to its heading.</summary>
    private static bool Over(StructurePiece p, Vector2 centre, float yaw)
    {
        var (back, heading, across, depth, _) = StairLayout.Footprint(StairRun.PieceOf(p));
        Vector2 d = centre - (back + StairLayout.Forward(heading) * (depth / 2f));
        return Mathf.Abs(Vector2.Dot(d, StairLayout.Forward(heading))) < depth / 2f && Mathf.Abs(Vector2.Dot(d, StairLayout.Right(heading))) < across / 2f
            && Mathf.Abs(((yaw - heading) % 360f + 540f) % 360f - 180f) < 0.5f;
    }

    /// <summary>Whether a point stands inside a rock clearance (its own frame, turned by <see cref="RockClearance.Turn"/>).</summary>
    private static bool Inside(RockClearance box, Vector3 p)
    {
        Vector3 d = Quaternion.Inverse(box.Turn) * (p - box.Centre);
        return Mathf.Abs(d.x) <= box.Half.x + 1e-4f && Mathf.Abs(d.y) <= box.Half.y + 1e-4f && Mathf.Abs(d.z) <= box.Half.z + 1e-4f;
    }

    /// <summary>
    /// Nothing the rock clearing leaves obstructs a stair's walk, and what only clips its edge stays (assistant-written,
    /// 6 Nov 2026): over every stair's tread the walker's corridor (the way, RoadRockPolicy.MinOpening, clipped to the walk,
    /// so the whole capsule and its margin fit) is in a box from just over the walk to a player's headroom
    /// (StairLayout.ClearanceHeight) over it, so no chunk is left there; a point at the walk's edge, outside the corridor,
    /// is in the tread layer up to StairLayout.StepUp over it, and over that in none of the stair's own boxes, so a chunk
    /// only there stays. And the boxes hug the treads: none reaches more than a stretch's rise under the walk, nor more than a
    /// stretch's rise over its headroom.
    /// </summary>
    [Fact]
    public void NoRockIsLeftInAStairsWalkAndRockOnlyAtItsEdgeStays()
    {
        _scope.WithWorld(new FlightWorld());
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 40), Ground);
        Assert.True(plan.Reached, plan.Reason);
        var run = StairRun.FromPlan(new Vector2(0, 0), new Vector2(0, 40), RoadStairRuns.Width, plan);
        StairStructures.Add(run);
        List<RockClearance> boxes = RockClearances.Near(new Vector3(0f, 0f, 20f), 40f, generating: false, NoGround, out _, out _, out _);

        float rise = StairLayout.PieceRise / StairLayout.ClearanceStretches;
        float corridor = RockClearances.Corridor(RoadStairRuns.Width / 2f);
        float way = RoadRockPolicy.MinOpening / 2f;
        // The corridor is the way, clipped to the walk: a capsule's whole body and its margin fit in it.
        Assert.Equal(Mathf.Min(RoadStairRuns.Width / 2f, way), corridor, 4);
        Assert.True(2f * corridor >= RoadRockPolicy.MinOpening - 1e-4f, "the capsule and its margin fit in the corridor");
        float edge = (corridor + RoadStairRuns.Width / 2f) / 2f;
        Assert.True(edge > way, "the edge band lies outside the way the carve already left");
        var up = new Vector3(0f, 1f, 0f);
        int stairs = 0;
        foreach (StructurePiece walked in run.Pieces.Skip(plan.SunkPieces).Where(p => p.Kind == StructurePieceKind.StairStep))
        {
            stairs++;
            var (back, heading, _, depth, _) = StairLayout.Footprint(StairRun.PieceOf(walked));
            Vector2 r2 = StairLayout.Right(heading);
            var right = new Vector3(r2.x, 0f, r2.y);
            Vector2 c2 = back + StairLayout.Forward(heading) * (depth / 2f);
            // The stair's own boxes: those standing over its footprint.
            float baseHeight = StairLayout.Footprint(StairRun.PieceOf(walked)).height;
            List<RockClearance> own = boxes.Where(b => Over(walked, new Vector2(b.Centre.x, b.Centre.z), b.Turn.eulerAngles.y)
                && b.Centre.y - b.Half.y > baseHeight).ToList();
            Assert.Equal(2 * StairLayout.ClearanceStretches, own.Count);
            for (float along = 0.05f; along < depth; along += 0.1f)
            {
                Vector3 walk = WalkAt(walked, along);
                foreach (float side in new[] { -corridor + 0.01f, 0f, corridor - 0.01f })
                    for (float over = 2f * RoadRockPolicy.ClearanceLift; over <= StairLayout.ClearanceHeight + 1e-4f; over += 0.1f)
                        Assert.True(boxes.Any(b => Inside(b, walk + right * side + up * over)),
                            $"stair at {walked.Position}: {over:F2} m over its walk {along:F2} m along, {side:F2} m aside, in no box");
                // Outside the corridor, at the walk's edge, up to StepUp: the tread layer.
                foreach (float side in new[] { -edge, edge })
                    for (float over = 2f * RoadRockPolicy.ClearanceLift; over <= StairLayout.StepUp + 1e-4f; over += 0.05f)
                        Assert.True(boxes.Any(b => Inside(b, walk + right * side + up * over)),
                            $"stair at {walked.Position}: {over:F2} m over its walk {along:F2} m along, {side:F2} m aside, in no box");
                // At the walk's edge, over StepUp past the stretch's rise: in none of its own boxes.
                foreach (float side in new[] { -edge, edge })
                    foreach (float over in new[] { StairLayout.StepUp + rise + 0.05f, RoadRockPolicy.PlayerHeight / 2f, RoadRockPolicy.PlayerHeight })
                        Assert.False(own.Any(b => Inside(b, walk + right * side + up * over)),
                            $"stair at {walked.Position}: its edge {side:F2} m aside, {over:F2} m over its walk, in a box");
                // Hugging the treads: nothing more than a stretch's rise under the walk, nor over its headroom.
                Assert.False(own.Any(b => Inside(b, walk - up * (rise + 0.01f))), $"stair at {walked.Position}: under its walk {along:F2} m along");
                Assert.False(own.Any(b => Inside(b, walk + up * (StairLayout.ClearanceHeight + rise + 0.01f))),
                    $"stair at {walked.Position}: over its headroom {along:F2} m along");
            }
        }
        Assert.True(stairs > 4);
    }

    [Fact]
    public void AStaircasesClearAreaLeavesARockTheClearingTakesToItsWalkingBox()
    {
        Func<Vector3, Heightmap.Biome> meadows = _ => Heightmap.Biome.Meadows;
        var at = new Vector3(5f, 0f, 5f);
        // The game's none, a road's far away, the staircase's over the point, then a bridge's far away.
        var areas = new List<ZoneSystem.ClearArea>
        {
            new(new Vector3(100f, 0f, 100f), 2f),
            new(at, StairLayout.PieceRun * 0.75f),
            new(new Vector3(-100f, 0f, -100f), 2f),
        };
        Assert.True(RoadRockPolicy.ClearingTakes("rock4_coast", Heightmap.Biome.Meadows, RockClearingMode.Carve));
        Assert.False(RoadClearAreaManager.InsideClearArea(areas, 0, 1, at, "rock4_coast", RockClearingMode.Carve, meadows, firstRockKeepingArea: 2));
        // Vegetation stays out of it; with rock clearing off, so does the rock; a bridge's area keeps it out.
        Assert.True(RoadClearAreaManager.InsideClearArea(areas, 0, 1, at, "Beech1", RockClearingMode.Carve, meadows, firstRockKeepingArea: 2));
        Assert.True(RoadClearAreaManager.InsideClearArea(areas, 0, 1, at, "rock4_coast", RockClearingMode.Off, meadows, firstRockKeepingArea: 2));
        Assert.True(RoadClearAreaManager.InsideClearArea(areas, 0, 1, at, "rock4_coast", RockClearingMode.Carve, meadows, firstRockKeepingArea: 1));
        Assert.True(RoadClearAreaManager.InsideClearArea(areas, 0, 1, at, "rock4_coast", RockClearingMode.Carve, meadows));

        // The server's clearing of a zone generated before the staircase: the same.
        var rock = "rock4_coast".GetStableHashCode();
        var tree = "Beech1".GetStableHashCode();
        var vegetation = new HashSet<int> { rock, tree };
        var none = new List<VegetationClearing.Area>();
        var p = new Vector2(at.x, at.z);
        List<VegetationClearing.Area> Structure(bool stair) => new() { new(p, StairLayout.PieceRun * 0.75f, leavesRocks: stair) };
        bool Removed(int prefab, bool stair) => VegetationClearing.ShouldRemove(prefab, p, false, vegetation, none,
            new List<VegetationClearing.Footprint>(), new List<VegetationClearing.Footprint>(), structureAreas: Structure(stair),
            rockClearing: RockClearingMode.Carve, biomeAt: _ => Heightmap.Biome.Meadows);
        Assert.False(Removed(rock, stair: true));
        Assert.True(Removed(tree, stair: true));
        Assert.True(Removed(rock, stair: false));
    }

    // ---- The ground: a cut lowers it only down to just under a walk (StairStructures.CutFloor). ----

    private static readonly Vector2s Zone = new(0, 0);

    private void FlatNetwork()
    {
        _scope.WithWorld(new FlatWorld());
        // A road far from the staircases: the cuts are stored into a network.
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(300f, 300f), new(340f, 300f) }, 4f, WorldGenerator.instance);
        RoadSpatialGrid.FinalizeRoadNetwork();
    }

    /// <summary>A one-piece run walking +z with its back edge's middle at <paramref name="back"/>, walked at
    /// <paramref name="walk"/> there (a floor's top, a stair's low edge).</summary>
    private static StairRun OnePiece(Vector2 back, float walk, bool stair)
    {
        var run = new StairRun { From = back, To = back + new Vector2(0f, StairLayout.PieceRun), Width = StairLayout.PieceRun };
        Vector2 centre = back + new Vector2(0f, StairLayout.PieceRun / 2f);
        run.Pieces.Add(stair
            ? new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = StairKit.Stone.Stair, Position = new Vector3(centre.x, walk, centre.y), YawDegrees = 180f }
            : new StructurePiece { Kind = StructurePieceKind.StairFloor, Prefab = StairKit.Stone.Floor, Position = new Vector3(centre.x, walk - StairKit.Stone.FloorTop, centre.y) });
        StairStructures.Add(run);
        return run;
    }

    /// <summary>The cut points StairLayout.AddCuts lays under a buried piece walking +z from <paramref name="back"/>: rows every
    /// CutSpacing along, CutSpacing apart across, each CutBelowSurface under the walk at the row.</summary>
    private static List<Vector3> CutsUnder(Vector2 back, float walk, bool stair)
    {
        var cuts = new List<Vector3>();
        for (float along = StairLayout.CutSpacing / 2f; along < StairLayout.PieceRun; along += StairLayout.CutSpacing)
            for (float side = -StairLayout.PieceRun / 2f + StairLayout.CutSpacing / 2f; side < StairLayout.PieceRun / 2f; side += StairLayout.CutSpacing)
                cuts.Add(new Vector3(back.x + side, walk + (stair ? StairLayout.PieceRise * along / StairLayout.PieceRun : 0f) - StairLayout.CutBelowSurface, back.y + along));
        return cuts;
    }

    private static Func<int, int, float> Written()
    {
        Heightmap hm = Heightmap.CreateForZone(Zone);
        TerrainComp tc = hm.m_terrainComp!;
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(Zone, RoadSpatialGrid.GetRoadPointsInZone(Zone), hm, tc);
        int grid = tc.m_width + 1;
        return (x, z) => Flat + tc.m_levelDelta[(z + tc.m_width / 2) * grid + x + tc.m_width / 2];
    }

    /// <summary>A buried floor's cut and its batter reached under the raised floor beside it, a flight up, and dug
    /// the ground its supports stood in out to a hollow. Now the ground under the higher walk stays; the buried one is still
    /// uncovered.</summary>
    [Fact]
    public void ACutBesideAHigherFlightLeavesTheGroundUnderItsWalk()
    {
        FlatNetwork();
        float lowWalk = Flat - 1f, highWalk = Flat + StairLayout.PieceRise / 2f;
        var low = new Vector2(0f, 0f);
        // Beside it, a piece's width and a metre over: within the cut's reach and batter.
        var high = new Vector2(StairLayout.PieceRun + 1f, 0f);
        OnePiece(low, lowWalk, stair: false);
        OnePiece(high, highWalk, stair: false);
        RoadSpatialGrid.CommitCuts(CutsUnder(low, lowWalk, stair: false), StairLayout.CutRadius * 2f);
        // The batter does reach under the higher floor: without the floor the ground there went lower than the natural.
        float batterAtHigh = lowWalk - StairLayout.CutBelowSurface + (high.x - StairLayout.PieceRun / 2f - (StairLayout.PieceRun / 2f - StairLayout.CutSpacing / 2f) - StairLayout.CutRadius) * RoadTerrainModifier.CutBatterSlope;
        Assert.True(batterAtHigh < Flat, $"the batter reaches {batterAtHigh:F2} under the higher floor");

        Func<int, int, float> ground = Written();
        for (int x = Mathf.CeilToInt(high.x - StairLayout.PieceRun / 2f); x <= Mathf.FloorToInt(high.x + StairLayout.PieceRun / 2f); x++)
            for (int z = 0; z <= (int)StairLayout.PieceRun; z++)
                Assert.Equal(Flat, ground(x, z), 3);
        for (int x = -1; x <= 1; x++)
            for (int z = 0; z <= (int)StairLayout.PieceRun; z++)
                Assert.Equal(lowWalk - StairLayout.CutBelowSurface, ground(x, z), 3);
    }

    /// <summary>A buried stair's lower rows reached up under its own higher treads and dug them a third of a metre deeper than
    /// its walk needs. Now the ground under it follows the walk up, just under it, and still uncovers it.</summary>
    [Fact]
    public void AStairsCutFollowsItsWalkUpAndNeverDigsUnderIt()
    {
        FlatNetwork();
        var back = new Vector2(0f, 0f);
        float walk = Flat - 1f;
        OnePiece(back, walk, stair: true);
        RoadSpatialGrid.CommitCuts(CutsUnder(back, walk, stair: true), StairLayout.CutRadius * 2f);

        Func<int, int, float> ground = Written();
        for (int x = -1; x <= 1; x++)
            for (int z = 0; z <= (int)StairLayout.PieceRun; z++)
            {
                float line = walk + StairLayout.PieceRise * z / StairLayout.PieceRun;
                float g = ground(x, z);
                Assert.True(g >= line - StairLayout.CutBelowSurface - 1e-3f, $"({x}, {z}): the ground {g:F2} is dug under the walk {line:F2}");
                Assert.True(g <= line + StairLayout.CoverTolerance, $"({x}, {z}): the ground {g:F2} covers the walk {line:F2}");
            }
    }
}
