using System.Collections.Generic;
using System.Linq;
using System;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// An approach climbing to a place's landing is aimed where the landing meets the slope, but stops as soon as its top steps
/// onto the landing (StairLayout.Build's onLanding): in game, a cave's approach at 1600.2,6920.2 reached the landing's lip
/// level with its top stair, went on along it to come nearer its aim, turned onto a floor out over the drop and stepped
/// back down off it; one floor laid by hand on from the top stair, open onto the landing, ended it.
/// </summary>
public class StairLandingLipTests
{
    /// <summary>A slope climbing north at a half a metre a metre from 0 at z = -20 to a level shelf at 10 from z = 0 on.</summary>
    private static float Ground(float x, float z) => Mathf.Clamp((z + 20f) * 0.5f, 0f, 10f);

    private static StairPlan Approach(Func<Vector2, bool>? onLanding) =>
        StairLayout.Build(new Vector2(0f, -20f), new Vector2(0f, 12f), Ground, 2f, StairKit.Stone, arriveWithin: RoadStairRuns.LedgeReach,
            onLanding: onLanding);

    [Fact]
    public void AnApproachStopsWhereItStepsOntoTheLandingBeforeItsAim()
    {
        StairPlan aimed = Approach(null), stopped = Approach(p => Ground(p.x, p.y) >= 10f - 0.01f && p.y >= 0f);
        Assert.True(aimed.Reached);
        Assert.True(stopped.Reached);
        // Held to its aim, the top goes on over the shelf to within the arrival reach of it; let stop at the lip, it ends on
        // the slope's top, level with the landing, its step off onto it.
        Assert.True(aimed.End.z >= 12f - RoadStairRuns.LedgeReach - 0.01f, $"aimed top at {aimed.End}");
        Assert.True(stopped.End.z < 1f, $"stopped top at {stopped.End}");
        Assert.Equal(10f, stopped.End.y, 1);
        Assert.True(stopped.Pieces.Count < aimed.Pieces.Count);
    }

    /// <summary>A climb whose top is a road's end (its ring there, RoadEndRing) arrives on that ring only: a landing to step
    /// onto first is a place's, and does not stop it short of the road.</summary>
    [Fact]
    public void AClimbToARoadsEndArrivesOnItsRingNotOnALanding()
    {
        var ring = new RoadEndRing(new Vector2(0f, 12f), 180f, Ground(0f, 12f));
        StairPlan free = StairLayout.Build(new Vector2(0f, -20f), new Vector2(0f, 12f), Ground, 2f, StairKit.Stone, ring: ring);
        StairPlan held = StairLayout.Build(new Vector2(0f, -20f), new Vector2(0f, 12f), Ground, 2f, StairKit.Stone, ring: ring,
            onLanding: p => true);
        Assert.True(free.Reached, free.Reason);
        Assert.False(free.Reversed);
        Assert.Equal(free.Pieces.Count, held.Pieces.Count);
        Assert.Equal(free.End, held.End);
    }

    [Fact]
    public void ALayoutLaidDownOntoTheLandingIsNotStoppedByIt()
    {
        // Laid down from a road above (the plan reversed, the landing at its root): its top is the road's end, which the landing
        // never stops, wherever it stands.
        Func<float, float, float> ground = (x, z) => 20f - Ground(x, z);
        StairPlan free = StairLayout.Build(new Vector2(0f, -20f), new Vector2(0f, 12f), ground, 2f, StairKit.Stone, arriveWithin: RoadStairRuns.LedgeReach);
        StairPlan held = StairLayout.Build(new Vector2(0f, -20f), new Vector2(0f, 12f), ground, 2f, StairKit.Stone, arriveWithin: RoadStairRuns.LedgeReach,
            onLanding: p => true);
        Assert.True(held.Reversed);
        Assert.Equal(free.Reached, held.Reached);
        Assert.Equal(free.Pieces.Count, held.Pieces.Count);
        Assert.Equal(free.End, held.End);
    }

    /// <summary>Four stairs north from (0, 0) at 10 m, their top at 14 m at z = 8; the landing past it within a step under the
    /// top for a metre (three quarters of <see cref="StairLayout.StepUp"/>, 0.15 m at 0.2) and three steps under it on from there
    /// (0.6 m), falling away from the top (in game: a cave's approach at 5704.6,3590.9, its top 0.3-0.65 m over the landing past
    /// it); far below before it. Read from the layout's step, so the rule holds whatever StepUp is set to.</summary>
    private static float Falling(float x, float z) => z < 8f ? -50f : z < 9.5f ? 14f - 0.75f * StairLayout.StepUp : 14f - 3f * StairLayout.StepUp;

    private static StairPlan FourStairs() =>
        StairLayout.LayFlight(new Vector2(0f, 0f), 10f, new[] { 0f, 0f, 0f, 0f }, StairKit.Stone, Falling);

    [Fact]
    public void ATopFloorIsLaidWhereTheGroundAtItsFarCornersIsWithinAStep()
    {
        // One rule for every top floor, read in one place (StairEnds.Lands): laid where the ground at its far corners, a
        // floor's size out from the top, is within a step of it. Here the landing falls three steps away under them: the
        // rule's stair down, not a floor, a road's and an approach's alike.
        Assert.Equal(0, StairLayout.AddTopFloor(FourStairs(), Falling, 2f).pieces);
        StairPlan plan = FourStairs();
        Assert.Equal(StairLayout.TopFinish.LandingThenDown, RoadStairRuns.FinishOverLedge(plan, Falling, 2f, _ => true));
        // Within a step under them: laid, level with the top.
        float Near(float x, float z) => z < 8f ? -50f : 14f - 0.75f * StairLayout.StepUp;
        plan = FourStairs();
        Assert.Equal(StairLayout.TopFinish.TopFloor, RoadStairRuns.FinishOverLedge(plan, Near, 2f, _ => true));
        StairPiece floor = Assert.Single(plan.Pieces, p => p.Kind == StairPieceKind.Landing);
        Assert.Equal(14f, floor.Position.y + StairKit.Stone.FloorTop, 2);
    }

    /// <summary>Four stairs north from (0, 0) at 10 m to their top at 14 m at z = 8 (as <see cref="FourStairs"/>), and past it
    /// the ground level with the top on its right (+x, within a step under it: half <see cref="StairLayout.StepUp"/>),
    /// <paramref name="left"/> under it on its left and 0.2 m more than a ledge (<see cref="StairRailing.LedgeDrop"/>) under it
    /// straight on past the top floor's far edge (z = 10); far below before the top.</summary>
    private static float OneSided(int x, int z, float left)
    {
        float level = 14f - StairLayout.StepUp / 2f;
        return z < 8 ? -50f : x >= 1 ? level : x <= -1 ? 14f - left : z >= 10 ? 14f - StairRailing.LedgeDrop - 0.2f : level;
    }

    [Theory]
    [InlineData(StairRailing.LedgeDrop + 0.5f, true)]
    [InlineData(StairRailing.LedgeDrop - 0.1f, false)]
    public void ATopWithTheGroundFallingOnOneSideGetsItsFloorOpenWhereItIsLevel(float left, bool leftRailed)
    {
        // In game: an approach's top stair at 5704.6,3590.9, the ground under the top floor's place falling away on one side
        // (0.4-0.7 m) and past it, level enough on the other, got no floor at all and could not be walked onto from the low
        // side; a 2x2 landing laid by hand fixed it. Laid now, level with the top: open where it is level, railed only where
        // the ground beside it stands more than a metre under it (StairRailing.LedgeDrop).
        Func<float, float, float> ground = (x, z) => BuriedPieces.MeshHeight(x, z, (i, k) => OneSided(i, k, left));
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), 10f, new[] { 0f, 0f, 0f, 0f }, StairKit.Stone, ground);
        Assert.Equal(StairLayout.TopFinish.TopFloor, RoadStairRuns.FinishOverLedge(plan, ground, 2f, _ => true));
        StairPiece floor = Assert.Single(plan.Pieces, p => p.Kind == StairPieceKind.Landing);
        Assert.True(Vector3.Distance(floor.Position, new Vector3(0f, 14f - StairKit.Stone.FloorTop, 9f)) < 0.01f, $"floor at {floor.Position}");
        var run = StairRun.FromPlan(new Vector2(0f, -1f), new Vector2(0f, 10f), 2f, plan, StairKit.Stone);
        run.KeepsSiteRocks = true;
        List<StructurePiece> rails = StairKit.Stone.Dress(run, ground, (i, k) => OneSided(i, k, left), false)
            .Where(p => p.Prefab is StairRailing.LevelRailPrefab or StairRailing.ShortRailPrefab).ToList();
        bool RailedAt(float x, float z) => rails.Any(r => Vector2.Distance(new Vector2(r.Position.x, r.Position.z), new Vector2(x, z)) < 0.05f);
        Assert.False(RailedAt(1f, 9f));            // its level side, open
        Assert.Equal(leftRailed, RailedAt(-1f, 9f)); // its low side, railed only over more than a metre
        Assert.True(RailedAt(0f, 10f));            // its far edge, more than a ledge over the ground past it
    }

    /// <summary>A flight north from (0, 0) at 10 m onto a flat whose lip is at z = 2.5 (depth into it z - 2.5): two stairs
    /// and a last piece, a floor or a third stair, centred 2.5 m inside the lip.</summary>
    private static StairPlan OntoTheFlat(StairPieceKind last) => StairLayout.LayFlight(new Vector2(0f, 0f), 10f, new[] { 0f, 0f, 0f },
        StairKit.Stone, (x, z) => -50f, new[] { StairPieceKind.Stair, StairPieceKind.Stair, last });

    private static float Depth(Vector2 p) => Mathf.Max(0f, p.y - 2.5f);

    [Fact]
    public void AnApproachsLastFloorMayStandPastTheLipOntoTheLanding()
    {
        // The floor it finishes on stands past the lip onto the landing by up to its own depth (in game: a cave's approach at
        // 5708.8,3579.1 ended on a stair against the lip, with no floor to walk off it on); any other piece does not.
        Assert.False(RoadStairRuns.PastTheLip(OntoTheFlat(StairPieceKind.Landing), Depth));
        Assert.True(RoadStairRuns.PastTheLip(OntoTheFlat(StairPieceKind.Stair), Depth));
        // Nor further: the floor a whole floor deeper is past it.
        Assert.True(RoadStairRuns.PastTheLip(OntoTheFlat(StairPieceKind.Landing), p => Mathf.Max(0f, p.y - 0.5f)));
    }

    /// <summary>A Mountain cave placed before its roads (Fixtures/cave-approach-lip.txt): its record and the planning ground
    /// read from the game round its approach's top, ground not read far below.</summary>
    private static (PlacedSiteRecord record, Func<float, float, float> ground) CaveAtTheLip()
    {
        string[] lines = System.IO.File.ReadAllLines(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "cave-approach-lip.txt"));
        PlacedSiteRecord record = PlacedSites.Read(string.Join("\n", lines.Where(l => !l.StartsWith("#") && !l.StartsWith("ground\t")))).Single();
        var heights = new System.Collections.Generic.Dictionary<(int, int), float>();
        foreach (string l in lines.Where(l => l.StartsWith("ground\t")))
        {
            string[] f = l.Split('\t');
            heights[(int.Parse(f[1]), int.Parse(f[2]))] = float.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture);
        }
        float Vertex(int x, int z) => heights.TryGetValue((x, z), out float h) ? h : -1000f;
        return (record, (x, z) => BuriedPieces.MeshHeight(x, z, Vertex));
    }

    private static StairPiece Piece(StairPieceKind kind, string prefab, float x, float y, float z, float yaw) =>
        new() { Kind = kind, Prefab = prefab, Position = new Vector3(x, y, z), YawDegrees = yaw };

    [Fact]
    public void AnApproachEndsWhereItFirstStepsOntoTheLandingLevelWithIt()
    {
        // In game: a cave's approach from the road's end at 1578.1,6933.4, as laid: three stairs up onto a walkway of floors
        // at 156.2 m, a stair up south onto the landing's lip at 157.2 m (its top at 1600.15,6920.23), and on past it a turn,
        // a floor and a stair back down toward the cave's mouth. The lip is where that top stair ends (in game: one floor laid
        // by hand on from it at 1600.15,6919.23, level with the ground, and nothing past it).
        var (record, ground) = CaveAtTheLip();
        PlacedSiteShape shape = PlacedSiteShape.Of(record.Centre, record.Yaw, RoadStairRuns.FlatOf(record.Ops)!.Value, record.RockRadius, record.Rocks,
            ground, record.KeepSmallRocks, record.Root, record.Prefab);
        PlacedSiteShape.LandingRegion landing = shape.ConnectedLanding(ground, 1f);
        // The landing reaches the approach's side: the step off the top stair is onto it, a metre inside its lip.
        Vector2 off = new(1600.15f, 6919.73f);
        Assert.True(landing.Contains(off));
        Assert.True(RoadStairRuns.WithinLip(landing.Depth(off)));
        var s = StairPieceKind.Stair; var f = StairPieceKind.Landing; var w = StairPieceKind.Filler;
        var plan = new StairPlan { Kit = "stone", Reached = true, EndHeading = 225f, End = new Vector3(1598.44f, 157.226f, 6918.11f) };
        plan.Pieces.AddRange(new[]
        {
            Piece(s, "stone_stair", 1577.996f, 153.226f, 6934.286f, 247.5f), Piece(s, "stone_stair", 1579.844f, 154.226f, 6935.051f, 247.5f),
            Piece(w, "stone_wall_2x1", 1581.230f, 154.726f, 6935.625f, 247.5f), Piece(w, "stone_wall_1x1", 1581.263f, 154.726f, 6936.087f, 292.5f),
            Piece(s, "stone_stair", 1582.457f, 155.226f, 6935.051f, 292.5f),
            Piece(f, "stone_floor_2x2", 1584.305f, 155.726f, 6934.286f, 112.5f), Piece(f, "stone_floor_2x2", 1586.153f, 155.726f, 6933.521f, 112.5f),
            Piece(f, "stone_floor_2x2", 1588.001f, 155.726f, 6932.755f, 112.5f), Piece(f, "stone_floor_2x2", 1589.542f, 155.726f, 6932.449f, 90f),
            Piece(f, "stone_floor_2x2", 1591.083f, 155.726f, 6932.143f, 112.5f), Piece(f, "stone_floor_2x2", 1592.931f, 155.726f, 6931.377f, 112.5f),
            Piece(f, "stone_floor_2x2", 1593.696f, 155.726f, 6930.612f, 157.5f), Piece(f, "stone_floor_2x2", 1594.462f, 155.726f, 6928.765f, 157.5f),
            Piece(f, "stone_floor_2x2", 1595.227f, 155.726f, 6926.917f, 157.5f), Piece(f, "stone_floor_2x2", 1595.992f, 155.726f, 6925.069f, 157.5f),
            Piece(f, "stone_floor_2x2", 1596.758f, 155.726f, 6924.304f, 112.5f), Piece(f, "stone_floor_2x2", 1598.606f, 155.726f, 6923.539f, 112.5f),
            Piece(w, "stone_wall_2x1", 1599.991f, 155.726f, 6922.965f, 292.5f), Piece(w, "stone_wall_2x1", 1600.147f, 155.726f, 6922.732f, 0f),
            Piece(s, "stone_stair", 1600.147f, 156.226f, 6921.232f, 0f),
            Piece(w, "stone_wall_2x1", 1600.147f, 156.726f, 6919.732f, 0f), Piece(w, "stone_wall_1x1", 1600.561f, 156.726f, 6919.525f, 45f),
            Piece(f, "stone_floor_2x2", 1599.147f, 156.726f, 6918.818f, 225f),
            Piece(s, "stone_stair", 1597.733f, 156.226f, 6917.404f, 225f),
        });
        Assert.True(RoadStairRuns.StopAtTheLip(plan, landing, 2f, StairKit.Stone));
        // Ended on the top stair: the turn, the floor and the stair down past it gone.
        Assert.Equal(20, plan.Pieces.Count);
        Assert.True(Vector3.Distance(plan.End, new Vector3(1600.147f, 157.226f, 6920.232f)) < 0.01f, $"top at {plan.End}");
        Assert.Equal(180f, plan.EndHeading, 1);
        // Finished there as the hand did: one floor on from the top stair, level with it and the ground, nothing past it.
        Assert.Equal(StairLayout.TopFinish.TopFloor, RoadStairRuns.FinishOverLedge(plan, ground, 2f, _ => true));
        Assert.Equal(21, plan.Pieces.Count(p => p.Kind != StairPieceKind.Support));
        StairPiece floor = plan.Pieces.Last(p => p.Kind != StairPieceKind.Support);
        Assert.Equal(StairPieceKind.Landing, floor.Kind);
        Assert.True(Vector3.Distance(floor.Position, new Vector3(1600.147f, 156.726f, 6919.232f)) < 0.01f, $"floor at {floor.Position}");
    }

    [Fact]
    public void ALastFloorWhoseWayOffStandsOverTheGroundBecomesAStairDownInItsPlace()
    {
        // In game: a cave's approach's last floor at 5046.8,-5892.6 (surface 255.76), the ground across its way off 0.11, 0.32
        // and 0.62 m under it, 0.26 m a metre past its middle: walkable off one side only; replaced by hand with a stair down
        // in its own place, onto the landing. By the end rule (5 Nov) a floor is read at its own far corners (StairEnds.Lands):
        // within a step at either, it stays a floor (the sides disagree, one is within a step); more than a step and up to a
        // ledge under both, it becomes the rule's stair down. Here a floor facing +x at 10 m, its corners in steps
        // (StairLayout.StepUp; the numbers at 0.2), so the rule holds whatever StepUp is set to.
        StairPlan Floor() => new() { Kit = StairKit.Stone.Name, LastRowPieces = 0, LastRowClearances = 0, LastRowPieceCount = 1, LastRowClearanceCount = 0,
            LastRowKind = StairPieceKind.Landing, LastRowStart = new Vector2(0f, 0f), LastRowHeading = 90f, LastRowHeight = 10f, Landings = 1,
            Pieces = { new StairPiece { Kind = StairPieceKind.Landing, Prefab = StairKit.Stone.Floor, Position = new Vector3(1f, 9.5f, 0f), YawDegrees = 90f } } };
        float Steps(float x, float z) => x < 2f ? 1f : 1.6f + z * (z < 0f ? 0.5f : 1f);
        float Ground(float x, float z) => 10f - StairLayout.StepUp * Steps(x, z);
        float OneSide(float x, float z) => 10f - StairLayout.StepUp * (x < 2f ? 1f : z < 0f ? 0.5f : 3.1f);
        Assert.False(StairLayout.StepDownTop(Floor(), OneSide, 2f));
        StairPlan plan = Floor();
        Assert.True(StairLayout.StepDownTop(plan, Ground, 2f));
        StairPiece step = Assert.Single(plan.Pieces, p => p.Kind == StairPieceKind.Stair);
        Assert.Equal(9f, step.Position.y, 3);
        Assert.InRange(step.Position.x, 0.9f, 1.1f);
        // Stepped off within a step across the whole way off, it stays a floor.
        Assert.False(StairLayout.StepDownTop(Floor(), (x, z) => 10f - StairLayout.StepUp / 2f, 2f));
    }
}
