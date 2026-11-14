using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A location that levels ground off its centre is placed before its roads (PlacedLocations.NeedsPlacing), so a staircase onto it is
/// laid on the ground the game leaves: the Mountain tower (StoneTowerRuins05, the saved Tower fixture), whose
/// flat round its centre and four corner levellings raise its landing out to about 15 m.
/// </summary>
public class TowerPlacedFirstTests : IDisposable
{
    /// <summary>How far over the road's level the top of a foot's sunk step may stand (a stair's top is a rise over its base):
    /// this test's tolerance, read by no rule of the layout's.</summary>
    private const float SunkStepAbove = 0.25f;

    private readonly ValheimWorldScope _scope = new();
    private readonly CapturedLandingFile _tower = CapturedLandingFile.Tower();

    /// <summary>The tower's natural ground (the fixture's grid, its edge carried on past it), in the Mountains.</summary>
    private sealed class TowerWorld : WorldGenerator
    {
        private readonly CapturedLandingFile _f;
        public TowerWorld(CapturedLandingFile f) { _f = f; }
        public override float GetHeight(float wx, float wy)
        {
            PlacedLocationRecord r = _f.Record;
            return _f.Natural(Mathf.Clamp(wx, r.X0, r.X0 + r.W - 1.001f), Mathf.Clamp(wy, r.Z0, r.Z0 + r.H - 1.001f));
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 92285445;
    }

    private readonly TowerWorld _world;

    public TowerPlacedFirstTests()
    {
        _world = new TowerWorld(_tower);
        _scope.WithWorld(_world);
        RoadNetworkGenerator.Reset();
        PlacedLocations.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(_tower.Record.Centre, 30f) });
    }

    public void Dispose()
    {
        SetPathfinder(null);
        PlacedLocations.Reset();
        RoadLocationProtection.Reset();
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    /// <summary>The one rule: a location the game turns as it places it, that levels or smooths anywhere off its centre and has a
    /// flat to come onto, is placed first where the network comes close to it (turned to face downhill, the Mistlands, a boss's
    /// entrance), and every location in the Mountains. The tower's flat is round its centre: placed first by its corners.</summary>
    [Fact]
    public void ALocationThatLevelsOffItsCentreIsPlacedBeforeItsRoadsWhereTheNetworkComesClose()
    {
        IReadOnlyList<LevelOp> tower = _tower.Record.Ops;
        Assert.True(RoadStairRuns.FlatOf(tower)!.Value.Concentric);
        Assert.True(PlacedLocations.NeedsPlacing("StoneTowerRuins05", true, true, Heightmap.Biome.Mountain, tower));
        Assert.True(PlacedLocations.NeedsPlacing("StoneTowerRuins05", false, true, Heightmap.Biome.Mountain, tower));
        Assert.True(PlacedLocations.NeedsPlacing("StoneTowerRuins05", false, true, Heightmap.Biome.Mistlands, tower));
        Assert.True(PlacedLocations.NeedsPlacing("StoneTowerRuins05", true, false, Heightmap.Biome.BlackForest, tower));
        Assert.True(PlacedLocations.NeedsPlacing("Mistlands_DvergrBossEntrance1", false, true, Heightmap.Biome.Meadows, tower));
        // Turned at random elsewhere: the outskirts end (a ruin; a crypt has a door, placed first for it).
        Assert.False(PlacedLocations.NeedsPlacing("StoneTowerRuins05", false, true, Heightmap.Biome.BlackForest, tower));
        Assert.True(PlacedLocations.NeedsPlacing("Crypt3", false, true, Heightmap.Biome.BlackForest, tower));
        // Not turned: its ground is known before it is placed (outside the Mountains).
        Assert.False(PlacedLocations.NeedsPlacing("StoneTowerRuins05", false, false, Heightmap.Biome.Mistlands, tower));
        // Everything round its centre: the same whichever way it is turned (outside the Mountains).
        var round = new[] { new LevelOp(0f, 0f, 0f, 8f, false, 15f, 3f), new LevelOp(0.2f, -0.3f, 0f, 0f, false, 20f, 3f) };
        Assert.False(PlacedLocations.NeedsPlacing("MountainWell1", true, true, Heightmap.Biome.Mistlands, round));
        // No flat: nothing to carry its road onto (outside the Mountains).
        var smoothing = new[] { new LevelOp(6f, 0f, 0f, 0f, false, 9f, 3f) };
        Assert.False(PlacedLocations.NeedsPlacing("StoneHenge1", true, true, Heightmap.Biome.Mistlands, smoothing));
        // In the Mountains every one is placed first: too varied to guess.
        Assert.True(PlacedLocations.NeedsPlacing("StoneTowerRuins05", false, false, Heightmap.Biome.Mountain, tower));
        Assert.True(PlacedLocations.NeedsPlacing("MountainWell1", true, true, Heightmap.Biome.Mountain, round));
        Assert.True(PlacedLocations.NeedsPlacing("StoneHenge1", true, true, Heightmap.Biome.Mountain, smoothing));
        // A frost cave, as before.
        var cave = new[] { new LevelOp(-0.06f, -4.75f, -4.09f, 4f, false, 7f, 3f) };
        Assert.True(PlacedLocations.NeedsPlacing("MountainCave02", true, false, Heightmap.Biome.Mountain, cave));
    }

    /// <summary>The tower's end, not placed first (not placed before its roads): the staircase onto the flat round its centre is no
    /// longer laid over the ground predicted from that flat alone, which buried its top in the landing its corners raise
    /// as a raised landing had; none is laid, and the tower gets no road (PlacedLocations.NotPlaced). Placed first, the ground the
    /// staircases read is the ground the game leaves there, and the staircase onto its landing, laid on it, buries nothing in
    /// it: from the captured road's end, every staircase crosses the road there, so it is laid from where the road is cut back
    /// for it.</summary>
    [Fact]
    public void TheTowersStaircaseIsLaidOnTheGroundItsCornersRaiseNotUnderIt()
    {
        PlacedLocationRecord r = _tower.Record;
        LocationApproachRequest q = _tower.Request;
        float Real(Vector2 p) => _tower.Ground(p.x, p.y);
        // Walked surfaces, edges included (StairLayout.Cover, the reading the approach's search and refusal make), over the
        // ground the location raised, deeper under it than an approach may be; but the last piece, a stair whose foot meets the
        // ledge it climbs onto. Read on the real ground, not the ground the layout was given.
        string Buried(StairPlan plan)
        {
            StairPiece last = plan.Pieces.Last(p => p.Kind != StairPieceKind.Support);
            var under = RoadStairRuns.CoverSamples(plan, (x, z) => Real(new Vector2(x, z))).Where(s => s.piece != last)
                .Select(s => (at: new Vector2(s.at.x, s.at.z), s.depth))
                .Where(s => Real(s.at) > _tower.Natural(s.at.x, s.at.y) + 0.01f && s.depth > StairCover.ApproachRefusal)
                .OrderByDescending(s => s.depth).ToList();
            return under.Count == 0 ? "" : FormattableString.Invariant(
                $"{under.Count} walked samples, deepest {under[0].depth:F2} m under at ({under[0].at.x:F1},{under[0].at.y:F1})");
        }

        RoadStairRuns.ResetCache();
        RoadStairSpan? centred = RoadStairRuns.LayLocationApproach(q.RoadEnd, q.RoadHeight, q.Arriving, r.Centre, r.Root, r.Ops, true,
            q.RoadTail, q.RoadTail.Count - 1, new List<RoadStairSpan>(), _world, out string why);
        if (centred != null) Assert.True(Buried(centred.Plan) == "", "laid on its centred levelling alone: " + Buried(centred.Plan));
        Assert.Null(centred);
        Assert.Equal(RoadStairRuns.LevelsOffItsCentre, why);

        PlacedLocations.Remember(r);
        PlacedLocations.Prepare((x, z) => _world.GetHeight(x, z));
        Assert.True(PlacedLocations.TryGetReady(r.Centre, out PlacedLocation placedLocation));
        // Across its landing and the corners round it, the stair ground is the game's.
        for (float d = 6f; d <= 16f; d += 2f)
            for (float a = 0f; a < 360f; a += 45f)
            {
                Vector2 p = r.Centre + new Vector2(Mathf.Sin(a * Mathf.Deg2Rad), Mathf.Cos(a * Mathf.Deg2Rad)) * d;
                Assert.True(Mathf.Abs(RoadStairRuns.GroundAt(p.x, p.y, _world) - Real(p)) <= 0.1f,
                    FormattableString.Invariant($"at ({p.x:F1},{p.y:F1}): {RoadStairRuns.GroundAt(p.x, p.y, _world):F2} against {Real(p):F2}"));
            }
        RoadStairSpan? Lay(LocationApproachRequest from, out string refused) => RoadStairRuns.PlanPlacedLocationApproach(from, r.Yaw, r.Ops,
            RoadStairRuns.LayoutGround.Lattice(_world), (_, _) => Heightmap.Biome.Mountain, _tower.Seed, out refused, r.RockRadius,
            RoadLocationProtection.Contains, r.Rocks, r.KeepSmallRocks, r.Root, placed: placedLocation.Shape);
        RoadStairSpan? span = Lay(q, out why);
        // Every staircase from the captured road's end crosses the road there (RoadEndRing): laid from where
        // the road is cut back for it, the first cut back along its last stretch (RoadNetworkGenerator.EntryCutBacks).
        if (span == null && why.Contains(RoadStairRuns.AcrossItsRoad))
        {
            span = Lay(_tower.CutBack(RoadNetworkGenerator.EntryCutBacks[1]), out string fromBack);
            why += "; cut back: " + fromBack;
        }
        Assert.True(span != null, why);
        Assert.True(span!.Plan.Reached, span.Plan.Reason);
        Assert.True(Buried(span.Plan) == "", "under the landing: " + Buried(span.Plan));
        Assert.True(RailAcrossTheWalk(span, Real) == "", RailAcrossTheWalk(span, Real));
    }

    /// <summary>
    /// A rail the kit lays (StairKit.Dress, as the network joins the approach: RoadNetworkGenerator.JoinNetwork) across the walk
    /// where it leaves a floor it ends on: the line from the floor's middle straight on 2.5 m past its far edge, where no
    /// other stair or floor carries the walk on beside it (in game: the top floor of an approach to a stone tower at
    /// -1930.0,6720.0 railed across the way onto the landing). "" where none is.
    /// </summary>
    internal static string RailAcrossTheWalk(RoadStairSpan span, Func<Vector2, float> ground)
    {
        var run = StairRun.FromPlan(span.FromPoint, span.ToPoint, RoadStairRuns.Width, span.Plan, span.Kit);
        run.KeepsLocationRocks = true;
        List<StructurePiece> pieces = span.Kit.Dress(run, (x, z) => ground(new Vector2(x, z)), (x, z) => ground(new Vector2(x, z)), false);
        var rails = pieces.Where(p => p.Prefab is StairRailing.LevelRailPrefab or StairRailing.ShortRailPrefab).ToList();
        var walks = pieces.Where(p => p.Kind is StructurePieceKind.StairStep or StructurePieceKind.StairFloor)
            .Select(p => StairSupport.WalkOf(p, span.Kit)!.Value).ToList();
        foreach (StairSupport.Walk floor in walks.Where(w => !w.Stair))
        {
            Vector2 centre = floor.At(floor.Depth / 2f, 0f);
            bool Carried(Vector2 p) => walks.Any(o => !o.Equals(floor) && Vector2.Dot(p - o.Back, o.Dir) is float along && along >= 0f && along <= o.Depth
                && Mathf.Abs(Vector2.Dot(p - o.Back, o.Right)) <= o.Across / 2f && Mathf.Abs(o.Underside - floor.Underside) <= 1.6f);
            if (Carried(floor.At(floor.Depth + 0.3f, 0f)) || Carried(floor.At(floor.Depth / 2f, floor.Across / 2f + 0.3f))
                || Carried(floor.At(floor.Depth / 2f, -floor.Across / 2f - 0.3f)) || !Carried(floor.At(-0.3f, 0f))) continue;
            Vector2 a = centre, b = centre + floor.Dir * 2.5f;
            foreach (StructurePiece rail in rails)
            {
                Vector2 c = new(rail.Position.x, rail.Position.z), d = StairLayout.Right(rail.YawDegrees) * (rail.Prefab == StairRailing.ShortRailPrefab ? 0.5f : 1f);
                Vector2 q1 = c - d, q2 = c + d;
                static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;
                if (Cross(q2 - q1, a - q1) * Cross(q2 - q1, b - q1) < 0f && Cross(b - a, q1 - a) * Cross(b - a, q2 - a) < 0f)
                    return FormattableString.Invariant($"rail at ({rail.Position.x:F2},{rail.Position.y:F2},{rail.Position.z:F2}) yaw {rail.YawDegrees} across the walk off the floor at ({centre.x:F2},{centre.y:F2}) heading {floor.Heading}");
            }
        }
        return "";
    }

    /// <summary>
    /// The towers checked in game on 2 Nov: a staircase leaving its road's end mostly perpendicular to it set off from the road's
    /// middle, its sunk first stair a stair back across the road from the way it turns and its first flight over the road's
    /// end (if we are mostly perpendicular to the road we need to start off to the side; it seems we started off to the side,
    /// but the wrong side). At -4801,1727 the road arrived heading about 230 and the stairs left 73 degrees to its left,
    /// settling back to 28 after two stairs; at -1917,6724 it arrived heading north and they left 105 degrees to its right.
    /// On the tower's own ground, roads arriving so the flight leaves 65 degrees to the left (its first stairs, settling to
    /// 42.5) and 87.5 to the right (settling to 42.5): the foot starts on the ring at the road's end (RoadEndRing), or at the
    /// end of the road cut back for it where nothing from its end keeps out of the ground and off the road, on the side the
    /// stairs turn to, its first stair at the road's height, no walked piece buried in the ground deeper than an approach may
    /// be (StairLayout.Cover), and no walked piece stands across the road's line from it nor inside the ring. Before the ring it was set beside the road, the edge it climbs from on the road's edge (in game
    /// 5: almost too far off the road).
    /// </summary>
    [Theory]
    [InlineData(-4791f, 1751f, 215f, -1)]
    [InlineData(-4778f, 1730f, 295f, 1)]
    public void AStaircaseLeavingItsRoadsEndTurnedStartsBesideItOnTheSideItTurnsTo(float x, float z, float arriving, int side)
    {
        PlacedLocationRecord r = _tower.Record;
        PlacedLocations.Remember(r);
        PlacedLocations.Prepare((a, b) => _world.GetHeight(a, b));
        Assert.True(PlacedLocations.TryGetReady(r.Centre, out PlacedLocation placedLocation));
        float Ground(float a, float b) => RoadStairRuns.GroundAt(a, b, _world);
        Vector2 end = default;
        List<Vector2> tail = new();
        LocationApproachRequest q = new();
        RoadStairSpan? span = null;
        string why = "";
        // From the road's end, else, where nothing from there both keeps out of the tower's ground (StairLayout.Cover, a
        // piece's edges included) and off its road, from where the road is cut back for it (RoadNetworkGenerator.EntryCutBacks,
        // as PlanEntry does: arriving on 215, every layout from the end that does not stand on the road buried a filler's edge
        // 0.72 m and a stair's 0.64 m, read before at their insides only; 12 m back one fits).
        foreach (float back in RoadNetworkGenerator.EntryCutBacks)
        {
            if (back > 0f && !why.Contains(RoadStairRuns.OnItsRoad) && !why.Contains(RoadStairRuns.AcrossItsRoad)) break;
            end = new Vector2(x, z) - StairLayout.Forward(arriving) * back;
            // The road straight in along its arrival, 40 m of it.
            Vector2 at = end;
            tail = Enumerable.Range(0, 6).Select(k => at - StairLayout.Forward(arriving) * (8f * (5 - k))).ToList();
            q = new() { Centre = r.Centre, RoadEnd = end, RoadHeight = Ground(end.x, end.y), Arriving = arriving, Reach = _tower.Request.Reach,
                TotalExpansions = _tower.Request.TotalExpansions, Stall = _tower.Request.Stall, Leg = _tower.Request.Leg, RoadTail = tail };
            span = RoadStairRuns.PlanPlacedLocationApproach(q, r.Yaw, r.Ops, RoadStairRuns.LayoutGround.Lattice(_world), (_, _) => Heightmap.Biome.Mountain, _tower.Seed,
                out string refused, r.RockRadius, RoadLocationProtection.Contains, r.Rocks, r.KeepSmallRocks, r.Root, placed: placedLocation.Shape);
            why += (back > 0f ? FormattableString.Invariant($"; {back:F0} m back: ") : "") + refused;
            if (span != null) break;
        }
        Assert.True(span != null, why);
        StairPlan plan = span!.Plan;
        Vector2 right = StairLayout.Right(arriving);
        float Across(Vector2 p) => side * Vector2.Dot(p - end, right);
        string Pieces() => string.Join(" ", plan.Pieces.Where(p => p.Kind != StairPieceKind.Support).Take(6)
            .Select(p => FormattableString.Invariant($"{p.Kind}({p.Position.x:F2},{p.Position.y:F2},{p.Position.z:F2})y{p.YawDegrees}")));
        // The first stair: the one nearest the road's end, climbing the way it turns.
        StairPiece first = plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair)
            .OrderBy(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), end)).First();
        float heading = StairLayout.Wrap(first.YawDegrees + 180f);
        float turn = StairLayout.Wrap(heading - arriving + 180f) - 180f;
        Assert.True(side * turn >= StairLayout.HeadingStep, FormattableString.Invariant($"sets off {heading}, {turn:F1} off the road: {Pieces()}"));
        // From the ring at the road's end, on the side it turns to: its first piece's back edge on the circle.
        var ring = new RoadEndRing(end, arriving, q.RoadHeight);
        Assert.True(plan.RingStart is Vector2 start && Mathf.Abs(Vector2.Distance(start, end) - ring.Radius) <= RoadEndRing.Delta && Across(start) > 0f,
            FormattableString.Invariant($"set off from {plan.RingStart}: {Pieces()}"));
        Assert.False(ring.Intrudes(plan, out string inside), inside);
        Assert.True(RoadStairRuns.Cover(plan, Ground) <= StairCover.ApproachRefusal, FormattableString.Invariant($"buried {RoadStairRuns.Cover(plan, Ground):F2} m"));
        // At the road's height.
        Assert.True(Mathf.Abs(first.Position.y + StairLayout.PieceRise - q.RoadHeight) <= SunkStepAbove,
            FormattableString.Invariant($"first stair's top {first.Position.y + StairLayout.PieceRise:F2} against the road's {q.RoadHeight:F2}: {Pieces()}"));
        // Nothing walked across the road's line from the side it turns to at the road's end, within a junction's height of it
        // (OverRoad's reach; a flight higher up may come back over the line): it never crosses the road's end.
        foreach (StairPiece p in plan.Pieces.Where(p => p.Kind != StairPieceKind.Support
            && Vector2.Dot(new Vector2(p.Position.x, p.Position.z) - end, StairLayout.Forward(arriving)) <= RoadNetworkGenerator.RoadWidth / 2f + StairLayout.PieceRun / 2f
            && p.Position.y - q.RoadHeight <= RoadStairRuns.JunctionHeight))
        {
            var at = new Vector2(p.Position.x, p.Position.z);
            Assert.True(Across(at) >= 0f, FormattableString.Invariant($"{p.Kind} at ({at.x:F2},{p.Position.y:F2},{at.y:F2}) {-Across(at):F2} m across the road from its turn: {Pieces()}"));
        }
        Assert.False(RoadStairRuns.OverRoad(plan, tail, tail.Count - 1, tail.Count - 1, Ground), "on its road");
        Assert.True(AcrossItsRoad(plan, end, arriving, q.RoadHeight) == "", AcrossItsRoad(plan, end, arriving, q.RoadHeight));
        Assert.True(RailAcrossTheWalk(span, p => Ground(p.x, p.y)) == "", RailAcrossTheWalk(span, p => Ground(p.x, p.y)));
    }

    /// <summary>
    /// A walked piece of a staircase setting off turned from its road's end at <paramref name="end"/> (its first stair 45
    /// degrees or more off the road arriving on <paramref name="arriving"/>) that stands across the road's line from the side
    /// it sets off to, on or just past the road's end and within a junction's height of the road: its sunk first step left in
    /// the road's middle, or its first flight over the road's end. The side is the one its first stair stands on where that
    /// stands beside the road, else the one it turns to. "" where none does (or it carries on along the road from its middle).
    /// The step sunk under the foot is not judged (since the ring, RoadEndRing, its foot's sunk step stands in the road's end).
    /// </summary>
    internal static string AcrossItsRoad(StairPlan plan, Vector2 end, float arriving, float roadHeight)
    {
        StairPiece? first = plan.Pieces.Skip(plan.SunkPieces).Where(p => p.Kind == StairPieceKind.Stair)
            .OrderBy(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), end)).FirstOrDefault();
        if (first == null) return "";
        float turn = StairLayout.Wrap(first.YawDegrees + 180f - arriving + 180f) - 180f;
        Vector2 right = StairLayout.Right(arriving);
        float firstAcross = Vector2.Dot(new Vector2(first.Position.x, first.Position.z) - end, right);
        bool beside = Mathf.Abs(firstAcross) >= RoadNetworkGenerator.RoadWidth / 2f;
        if (!beside && Mathf.Abs(turn) < 45f) return "";
        int side = beside ? (firstAcross > 0f ? 1 : -1) : turn > 0f ? 1 : -1;
        // Not the step sunk under its foot, under the road's level on purpose.
        foreach (StairPiece p in plan.Pieces.Skip(plan.SunkPieces))
        {
            if (p.Kind == StairPieceKind.Support) continue;
            var at = new Vector2(p.Position.x, p.Position.z);
            if (Vector2.Dot(at - end, StairLayout.Forward(arriving)) > RoadNetworkGenerator.RoadWidth / 2f + StairLayout.PieceRun / 2f) continue;
            if (p.Position.y - roadHeight > RoadStairRuns.JunctionHeight) continue;
            float across = side * Vector2.Dot(at - end, right);
            if (across < 0f)
                return FormattableString.Invariant($"{p.Kind} at ({at.x:F2},{p.Position.y:F2},{at.y:F2}) {-across:F2} m across the road from the side it sets off to (first stair {turn:F1} off the road)");
        }
        return "";
    }

    /// <summary>
    /// A staircase never crosses its road (2 Nov: you would never place a stairs over a road like that, even if it means we
    /// can't get to it; the road should be shorter so the stairs can find a proper way to reach it). Roads arriving at the
    /// tower's end at -4791,1751 heading 200 and 180: the flight from there turns off the road, and no layout from beside the
    /// road's end fits; laid from the road's middle, its sunk first stair and first flight stood across the road's end. It is
    /// refused there: none is laid from the road's end, or one that does not cross it.
    /// </summary>
    [Theory]
    [InlineData(200f)]
    [InlineData(180f)]
    public void NoStaircaseIsLaidAcrossItsRoadsEndWhereNoneFitsBesideIt(float arriving)
    {
        PlacedLocationRecord r = _tower.Record;
        PlacedLocations.Remember(r);
        PlacedLocations.Prepare((a, b) => _world.GetHeight(a, b));
        Assert.True(PlacedLocations.TryGetReady(r.Centre, out PlacedLocation placedLocation));
        float Ground(float a, float b) => RoadStairRuns.GroundAt(a, b, _world);
        var end = new Vector2(-4791f, 1751f);
        var tail = Enumerable.Range(0, 6).Select(k => end - StairLayout.Forward(arriving) * (8f * (5 - k))).ToList();
        LocationApproachRequest q = new() { Centre = r.Centre, RoadEnd = end, RoadHeight = Ground(end.x, end.y), Arriving = arriving, Reach = _tower.Request.Reach,
            TotalExpansions = _tower.Request.TotalExpansions, Stall = _tower.Request.Stall, Leg = _tower.Request.Leg, RoadTail = tail };
        RoadStairSpan? span = RoadStairRuns.PlanPlacedLocationApproach(q, r.Yaw, r.Ops, RoadStairRuns.LayoutGround.Lattice(_world), (_, _) => Heightmap.Biome.Mountain, _tower.Seed,
            out string why, r.RockRadius, RoadLocationProtection.Contains, r.Rocks, r.KeepSmallRocks, r.Root, placed: placedLocation.Shape);
        if (span == null) return;
        Assert.True(AcrossItsRoad(span.Plan, end, arriving, q.RoadHeight) == "", AcrossItsRoad(span.Plan, end, arriving, q.RoadHeight));
        Assert.False(RoadStairRuns.OverRoad(span.Plan, tail, tail.Count - 1, tail.Count - 1, Ground), "on its road");
    }

    /// <summary>
    /// In game, StoneTowerRuins05 at -1917,6724: the road came in above the tower and its staircase
    /// came down to the landing (very neat), but its last flight ran along the tower's wall and ended into it; it would be better
    /// if it turned towards the location vs into the wall, and so for a staircase climbing to it. On the saved tower's own ground
    /// (turned 180 degrees from that one, the road's end behind it as there): road ends above it to the north west, 26 m out at
    /// 315 and 337.5 degrees (about -4819.5,1745.3 and -4811.1,1751.0, 9 and 3 m over its landing), whose staircases come down
    /// round its side, and one below it, 34 m out at 45 degrees (-4777.1,1751.0), whose staircase climbs round its north side.
    /// The flight that arrives at the landing faces into the location: its last stair, walked onto the landing, heads within 90
    /// degrees of the tower's centre as seen from it (anywhere from a tangent to the location's radius up to
    /// straight in, never away; 45 before, StairLayout.ArrivalSteps), give or take its standing a metre or two off where it was
    /// aimed: not away from the tower. Laid as PlanEntry lays it: from the road's end, else from where the road is cut back for
    /// it (RoadNetworkGenerator.EntryCutBacks). From the end below, 34 m out at 45 degrees, nothing meets the raised landing's
    /// lip without a walked piece's edge in the ground (StairLayout.Cover) nor sets off from the end's ring; 12 m back one
    /// climbs round its north side facing in. From the end above at 337.5 degrees, held within 45 degrees, the lip points it aims at
    /// first (the joined edge at -4803,1743, at the foot of the bank north west of the landing, and the level landing at
    /// -4796,1733) had every first stair facing into the tower further into the ground than the search allows; laid not facing
    /// in, it arrived 65 degrees off. So the lip points beside such an aim are tried, a few metres along the lip, before laying it not
    /// facing in (RoadStairRuns.BesideOnTheLip): it came down onto the lip 2 m along from the level landing, facing in. Held
    /// within 90 degrees, a first stair faces in from the aims it is given and none beside them is tried. Every walked piece
    /// stands no deeper in the ground than an approach is allowed (StairCover.ApproachRefusal).
    /// </summary>
    [Theory]
    [InlineData(315f, 26f, Skip = "Waits on a decision on a road's end on a summit: from this end, 9 m over the landing on a knoll that falls away on every side, no staircase from the landing arrives on the end's ring (RoadEndRing), facing in or not, nor with the ring's tolerance doubled; with no ring one faces in, 36 degrees off. Cut back, the road runs down this test's straight-in road, steeper than a road is planned, and none is laid from there either.")]
    [InlineData(337.5f, 26f)]
    [InlineData(45f, 34f)]
    public void TheFlightArrivingAtTheLandingFacesIntoTheLocation(float bearing, float distance)
    {
        PlacedLocationRecord r = _tower.Record;
        PlacedLocations.Remember(r);
        PlacedLocations.Prepare((a, b) => _world.GetHeight(a, b));
        Assert.True(PlacedLocations.TryGetReady(r.Centre, out PlacedLocation placedLocation));
        float Ground(float a, float b) => RoadStairRuns.GroundAt(a, b, _world);
        // The road straight in toward the location, 40 m of it, cut back along itself as PlanEntry cuts it back.
        float arriving = StairLayout.Wrap(bearing + 180f);
        RoadStairSpan? span = null;
        string why = "";
        foreach (float back in RoadNetworkGenerator.EntryCutBacks)
        {
            Vector2 end = r.Centre + StairLayout.Forward(bearing) * (distance + back);
            var tail = Enumerable.Range(0, 6).Select(k => end - StairLayout.Forward(arriving) * (8f * (5 - k))).ToList();
            LocationApproachRequest q = new() { Centre = r.Centre, RoadEnd = end, RoadHeight = Ground(end.x, end.y), Arriving = arriving, Reach = _tower.Request.Reach,
                TotalExpansions = _tower.Request.TotalExpansions, Stall = _tower.Request.Stall, Leg = _tower.Request.Leg, RoadTail = tail };
            span = RoadStairRuns.PlanPlacedLocationApproach(q, r.Yaw, r.Ops, RoadStairRuns.LayoutGround.Lattice(_world), (_, _) => Heightmap.Biome.Mountain, _tower.Seed,
                out string refused, r.RockRadius, RoadLocationProtection.Contains, r.Rocks, r.KeepSmallRocks, r.Root, placed: placedLocation.Shape);
            why += (back > 0f ? FormattableString.Invariant($"; {back:F0} m back: ") : "") + refused;
            if (span != null) break;
        }
        Assert.True(span != null, why);
        StairPlan plan = span!.Plan;
        // Where the walk ends on the landing: the aim a staircase laid up from it set off from, else its top.
        Vector2 location = plan.Reversed ? span.ToPoint : new Vector2(plan.End.x, plan.End.z);
        var (walk, toward, at) = ArrivingStair(plan, location, r.Centre);
        float off = Mathf.Abs(StairLayout.Wrap(walk - toward + 180f) - 180f);
        Assert.True(off <= StairLayout.ArrivalSteps * StairLayout.HeadingStep + 5f, FormattableString.Invariant(
            $"{(plan.Reversed ? "down" : "up")} to ({location.x:F1},{location.y:F1}): its last stair at ({at.x:F1},{at.y:F1}) walked heading {walk:F1}, the tower's centre at {toward:F1} from it, {off:F1} off"));
        float buried = RoadStairRuns.Cover(plan, Ground);
        Assert.True(buried <= StairCover.ApproachRefusal + 0.001f, FormattableString.Invariant($"a walked piece {buried:F2} m into the ground"));
        Assert.True(RailAcrossTheWalk(span, p => Ground(p.x, p.y)) == "", RailAcrossTheWalk(span, p => Ground(p.x, p.y)));
    }

    /// <summary>
    /// The points beside an aim on the lip (tried where a layout facing in is buried at its aim): on the tower's
    /// landing, the lip cell at -4803,1743 ends a spur, the lip's row 3 m south of it. Beside it: points on the lip, nearest
    /// first, each 1.2 m (RoadStairRuns.LipStep) or more from the aim and from each other, none past 3.6 m and half a step,
    /// at most two per step, the row's included.
    /// </summary>
    [Fact]
    public void ThePointsBesideAnAimLieOnTheLipNearestFirstAStepApart()
    {
        PlacedLocationRecord r = _tower.Record;
        PlacedLocations.Remember(r);
        PlacedLocations.Prepare((a, b) => _world.GetHeight(a, b));
        Assert.True(PlacedLocations.TryGetReady(r.Centre, out PlacedLocation placedLocation));
        float Ground(float a, float b) => RoadStairRuns.GroundAt(a, b, _world);
        var landing = placedLocation.Shape.ConnectedLanding(Ground, 1f);
        var aim = new Vector2(-4803f, 1743f);
        Assert.Contains(landing.Lip, l => l.at == aim);
        var beside = RoadStairRuns.BesideOnTheLip(landing, aim);
        Assert.InRange(beside.Count, 1, 2 * RoadStairRuns.LipSteps);
        float step = RoadStairRuns.LipStep;
        for (int i = 0; i < beside.Count; i++)
        {
            Vector2 p = beside[i].at;
            float d = Vector2.Distance(p, aim);
            Assert.InRange(d, step - 0.01f, RoadStairRuns.LipSteps * step + step / 2f);
            Assert.True(landing.Contains(p) && RoadStairRuns.WithinLip(landing.Depth(p)), $"{p} off the landing's lip");
            if (i > 0) Assert.True(d >= Vector2.Distance(beside[i - 1].at, aim) - 0.01f, "not nearest first");
            for (int j = 0; j < i; j++) Assert.True(Vector2.Distance(p, beside[j].at) >= step - 0.01f, $"{p} and {beside[j].at} under a step apart");
        }
        Assert.Contains(beside, b => b.at == new Vector2(-4803f, 1740f));
    }

    /// <summary>The stair a walker steps onto a location's landing from at <paramref name="location"/> (sunk steps aside), the heading
    /// they walk it on and the bearing of <paramref name="centre"/> from it: of its two ends, they leave it by the one nearer the
    /// landing, up it or down it (a top finished with a step down is walked down).</summary>
    internal static (float walk, float toward, Vector2 at) ArrivingStair(StairPlan plan, Vector2 location, Vector2 centre)
    {
        StairPiece last = plan.Pieces.Skip(plan.SunkPieces).Where(p => p.Kind == StairPieceKind.Stair)
            .OrderBy(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), location)).First();
        Vector2 at = new Vector2(last.Position.x, last.Position.z);
        float climb = StairLayout.Wrap(last.YawDegrees + 180f);
        Vector2 high = at + StairLayout.Forward(climb), low = at - StairLayout.Forward(climb);
        float walk = Vector2.Distance(high, location) <= Vector2.Distance(low, location) ? climb : StairLayout.Wrap(climb + 180f);
        return (walk, StairLayout.Bearing(at, centre), at);
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pathfinder);

    /// <summary>
    /// The road shortened so the staircase finds a proper way onto the landing (2 Nov: if anything the road should be shorter so
    /// the stairs can find a proper way to reach it). A road 48 m straight in to the tower's end at -4791,1751, heading 200 or
    /// 180 (as captured), on its ground, carried on onto the landing through the one owner (PlanEntry): no staircase from its
    /// end fits beside it, and none is laid across it: one that does not cross it, else the road cut back along its last
    /// stretch (or started lower) and the staircase laid from there, or the location gets none. Whatever is laid crosses no road.
    /// </summary>
    [Theory]
    [InlineData(200f)]
    [InlineData(180f)]
    public void TheRoadIsCutBackForAStaircaseThatWouldCrossItsEnd(float arriving)
    {
        PlacedLocationRecord r = _tower.Record;
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        PlacedLocations.Remember(r);
        PlacedLocations.Prepare((a, b) => _world.GetHeight(a, b));
        Assert.True(PlacedLocations.TryGetReady(r.Centre, out PlacedLocation placedLocation));
        float Ground(float a, float b) => RoadStairRuns.GroundAt(a, b, _world);
        SetPathfinder(new RoadPathfinder(_world) { Stairs = false, TerrainVarianceThreshold = 1000f, Meander = 0f });
        var end = new Vector2(-4791f, 1751f);
        Vector2 At(float back) => end - StairLayout.Forward(arriving) * back;
        // Its waypoints a cell apart, its planned points a metre apart on its ground (a road this steep is its ground's, held
        // to no grade here: only its end matters).
        var path = Enumerable.Range(0, 7).Select(k => At(RoadPathfinder.CellSize * (6 - k))).ToList();
        var points = Enumerable.Range(0, 49).Select(k => At(48 - k)).ToList();
        var piece = new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(q => Ground(q.x, q.y)), points.ConvertAll(_ => default(RoadPointDebugInfo)),
            RoadNetworkGenerator.RoadWidth, 48f, true);
        var plans = new List<RoadSpatialGrid.PlannedPath> { piece };
        bool entered = RoadNetworkGenerator.PlanEntry(placedLocation, false, path, plans, new List<RoadStairSpan>(), new List<RoadCrossing>(), 4f,
            out RoadSpatialGrid.PlannedPath? road, out RoadStairSpan? span, out string why);
        if (!entered) { Assert.Contains(RoadStairRuns.AcrossItsRoad, why); return; }
        if (span == null) return;
        Vector2 now = path[path.Count - 1];
        Assert.Equal(now, span.FromPoint);
        float heading = RoadStairRuns.ArrivingTravel(path, now, false);
        Assert.True(AcrossItsRoad(span.Plan, now, heading, span.FromHeight) == "", FormattableString.Invariant(
            $"from ({now.x:F1},{now.y:F1}), {Vector2.Distance(now, end):F0} m back: {AcrossItsRoad(span.Plan, now, heading, span.FromHeight)}"));
        Assert.False(RoadStairRuns.OverRoad(span.Plan, path, path.Count - 1, path.Count - 1, Ground), "on its road");
    }
}
