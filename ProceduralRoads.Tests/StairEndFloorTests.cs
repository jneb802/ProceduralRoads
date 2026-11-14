using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road staircase keeps one landing row at each end (RoadStairRuns.CarryRoadOntoStairs, StairLayout.TrimEndFloors): the road
/// is carried on over the level floors its layout laid at its foot and at its top up to the row next to the stair, never
/// through it, by one rule at both ends, where it can be built there by the road's rules; where it cannot, the floors stay as
/// laid.
/// </summary>
public class StairEndFloorTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public StairEndFloorTests() => RoadNetworkGenerator.Reset();

    public void Dispose()
    {
        RoadLocationProtection.Reset();
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private sealed class World : WorldGenerator
    {
        public Func<float, float, float> Height = (x, z) => 100f;
        public override float GetHeight(float x, float z) => Height(x, z);
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public Func<float, float, float> River = (x, z) => 0f;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = River(x, z); width = 0f; }
    }

    /// <summary>The road staircase's ends carried over on <paramref name="ground"/>, the world's ground too (the road's
    /// pieces are planned on it), with the river weight <paramref name="river"/> and the next stretch <paramref name="next"/>.</summary>
    private StairPlan Carry(StairPlan plan, List<Vector2> path, int start, int to, bool foot, bool top, Func<float, float, float> ground,
        Func<float, float, float>? river = null, (int from, int to)? next = null, RoadLocationProtection.Targets locations = default)
    {
        var world = new World { Height = ground, River = river ?? ((x, z) => 0f) };
        _scope.WithWorld(world);
        return RoadStairRuns.CarryRoadOntoStairs(plan, path, start, to, foot, top, new List<RoadStairSpan>(), new List<RoadCrossing>(), next, world, ground, locations);
    }

    /// <summary>Runs <paramref name="body"/> with the mod's earthwork cap (the suite runs on plain roads, with none).</summary>
    private static void WithTheCap(Action body)
    {
        float cap = RoadNetworkGenerator.EarthworkCap;
        RoadNetworkGenerator.EarthworkCap = RoadNetworkGenerator.DefaultEarthworkCap;
        try { body(); }
        finally { RoadNetworkGenerator.EarthworkCap = cap; }
    }

    /// <summary>A plan of <paramref name="pieces"/> (kind, prefab, position, yaw), each walked piece's clearances as Emit lays
    /// them (a stair two stretches, a floor or filler one).</summary>
    private static StairPlan Laid(string kit, float startHeading, Vector3 end, float endHeading, params (StairPieceKind kind, string prefab, float x, float y, float z, float yaw)[] pieces)
    {
        var plan = new StairPlan { Kit = kit, Reached = true, StartHeading = startHeading, End = end, EndHeading = endHeading };
        foreach (var (kind, prefab, x, y, z, yaw) in pieces)
        {
            plan.Pieces.Add(new StairPiece { Kind = kind, Prefab = prefab, Position = new Vector3(x, y, z), YawDegrees = yaw });
            for (int s = kind == StairPieceKind.Stair ? StairLayout.ClearanceStretches : 1; s > 0; s--)
                plan.Clearances.Add(new StairClearance(new Vector3(x, y, z), new Vector3(1f, 1f, 1f), yaw, y));
            if (kind == StairPieceKind.Stair) plan.Stairs++; else if (kind == StairPieceKind.Landing) plan.Landings++;
        }
        plan.StartsOnFloor = pieces[0].kind == StairPieceKind.Landing;
        return plan;
    }

    private const string Floor = "stone_floor_2x2", Stair = "stone_stair";

    /// <summary>
    /// A stone staircase north from a road arriving up x = 0 to its end at (0, 0), height 40: three floors (surface 40) from
    /// the ring at its foot to its first stair at (0, 8.5), four stairs, then three floors at its top (surface 44) before the
    /// road carries on north from (0, 20.5) on level ground.
    /// </summary>
    private static (StairPlan plan, List<Vector2> path) Straight()
    {
        var pieces = new List<(StairPieceKind, string, float, float, float, float)>();
        for (int i = 0; i < 3; i++) pieces.Add((StairPieceKind.Landing, Floor, 0f, 39.5f, 2.5f + 2f * i, 0f));
        for (int i = 0; i < 4; i++) pieces.Add((StairPieceKind.Stair, Stair, 0f, 40f + i, 8.5f + 2f * i, 180f));
        for (int i = 0; i < 3; i++) pieces.Add((StairPieceKind.Landing, Floor, 0f, 43.5f, 16.5f + 2f * i, 0f));
        StairPlan plan = Laid("stone", 0f, new Vector3(0f, 44f, 21.5f), 0f, pieces.ToArray());
        plan.Ring = new RoadEndRing(new Vector2(0f, 0f), 0f, 40f);
        plan.RingStart = new Vector2(0f, 1.5f);
        var path = new List<Vector2> { new(0f, -24f), new(0f, -16f), new(0f, -8f), new(0f, 0f), new(0f, 8f), new(0f, 16f), new(0f, 24f), new(0f, 32f), new(0f, 40f) };
        return (plan, path);
    }

    /// <summary>The ground the straight staircase stands on: level just under its floors (39.9 to the foot of its flight, 43.9
    /// past its top), the flight's face between; <paramref name="under"/> changes it south of the first stair.</summary>
    private static Func<float, float, float> StraightGround(Func<float, float, float?>? under = null) => (x, z) =>
        z < 7.5f ? under?.Invoke(x, z) ?? 39.9f : z > 15.5f ? 43.9f : 39.9f + (z - 7.5f) * 0.5f;

    private static StairPiece FirstWalked(StairPlan plan) => plan.Pieces.Skip(plan.SunkPieces).First(p => p.Kind != StairPieceKind.Support);

    [Fact]
    public void TheRoadIsCarriedOverLevelFloorsAtBothEndsToTheirLandingRows()
    {
        var (plan, path) = Straight();
        StairPlan carried = Carry(plan, path, 3, 5, foot: true, top: true, StraightGround());

        // At its foot it starts on the floor next to its first stair, as laid, set off from a ring a radius behind it.
        Assert.Equal(2, carried.FootFloorsTrimmed);
        StairPiece first = FirstWalked(carried);
        Assert.Equal(StairPieceKind.Landing, first.Kind);
        Assert.Equal(6.5f, first.Position.z, 3);
        Assert.Equal(0, carried.SunkPieces);
        Assert.Equal(new Vector3(0f, 40f, 5.5f), carried.Foot!.Value);
        Assert.Equal(new Vector2(0f, 5.5f), carried.RingStart!.Value);
        RoadEndRing ring = carried.Ring!.Value;
        Assert.Equal(5.5f - ring.Radius, ring.Centre.y, 3);
        Assert.Equal(40f, ring.Height, 3);
        Assert.False(ring.Intrudes(carried, out string piece), piece);
        Assert.True(carried.StartsOnFloor);
        // At its top as at its foot: the floor next to its last stair stays, it ends on that floor's far edge.
        Assert.Equal(2, carried.TopFloorsTrimmed);
        Assert.Equal(new Vector3(0f, 44f, 17.5f), carried.End);
        Assert.Equal(0f, carried.EndHeading, 3);
        Assert.Equal(StairPieceKind.Landing, carried.LastRowKind);
        Assert.Equal(carried.Pieces.Count - 1, carried.LastRowPieces);
        Assert.Equal(44f, carried.LastRowHeight, 3);
        Assert.Equal(new Vector2(0f, 15.5f), carried.LastRowStart);
        Assert.Equal(carried.Clearances.Count - 1, carried.LastRowClearances);
        // The pieces that went took their clearances: a floor's one each end and four stairs' stretches are left.
        Assert.Equal(1 + 4 * StairLayout.ClearanceStretches + 1, carried.Clearances.Count);
        Assert.Equal(2, carried.Landings);
        Assert.Equal(1, StairLayout.EndFloorRows(carried, top: false, RoadStairRuns.Width));
        Assert.Equal(1, StairLayout.EndFloorRows(carried, top: true, RoadStairRuns.Width));
        // The plan as laid is left as it was (a road refused at commit keeps no edit: PlanCache).
        Assert.Equal(10, plan.Pieces.Count);
        Assert.Equal(0, plan.FootFloorsTrimmed);
    }

    [Fact]
    public void FloorsOverARidgeTheRoadWouldBuildToTheStairAreKept()
    {
        // The floors laid out over a gully at the end of a spine 8 m under them, the ground 7 m either side of the spine 6 m
        // lower still: the road carried on to the first stair would stand on a ridge (RoadRidges, as the road builder reads it).
        // The one list refuses a road along one (RoadNetworkGenerator.AlongARidge), this road as every road.
        var (plan, path) = Straight();
        StairPlan kept = plan;
        WithTheCap(() => kept = Carry(plan, path, 3, 5, foot: true, top: false,
            StraightGround((x, z) => z > -40f ? (Mathf.Abs(x) < 3f ? 32f : 26f) : null)));
        Assert.Same(plan, kept);
        Assert.Equal(0, kept.FootFloorsTrimmed);
        Assert.Contains("foot: " + RoadNetworkGenerator.RidgeRoadRefusal, RoadStairRuns.EndFloorsKept.Keys);
    }

    [Fact]
    public void FloorsOverEarthworkPastTheCapAreKept()
    {
        // The ground under the floors' line 9 m under them: the road held to the first stair's base would be filled past the
        // 8 m terrain limit.
        var (plan, path) = Straight();
        StairPlan kept = plan;
        WithTheCap(() => kept = Carry(plan, path, 3, 5, foot: true, top: false, StraightGround((x, z) => z > -40f ? 31f : null)));
        Assert.Same(plan, kept);
        Assert.Contains(RoadStairRuns.EndFloorsKept.Keys, k => k.StartsWith("foot: earthwork past the", StringComparison.Ordinal));
    }

    /// <summary>The same 9 m at the new end itself, where the road starts at the stair's base whatever road is planned: each
    /// carry is refused by the earthwork cap's own reading of that point (RoadNetworkGenerator.PastTerrainLimit) before its
    /// road is planned, for the reason the planned road was refused for.</summary>
    [Fact]
    public void EarthworkPastTheCapAtTheNewEndIsRefusedBeforeTheRoadIsPlanned()
    {
        var (plan, path) = Straight();
        WithTheCap(() => Carry(plan, path, 3, 5, foot: true, top: false, StraightGround((x, z) => z > -40f ? 31f : null)));
        Assert.True(RoadStairRuns.RoadOntoCalls > 0);
        Assert.Equal(RoadStairRuns.RoadOntoCalls, RoadStairRuns.RoadOntoUnplanned);
        Assert.Contains(RoadStairRuns.EndFloorsKept.Keys, k => k.StartsWith("foot: earthwork past the", StringComparison.Ordinal));
    }

    /// <summary>Carried over level ground every road is planned: nothing refuses it before planning.</summary>
    [Fact]
    public void ACarryOverLevelGroundIsPlanned()
    {
        var (plan, path) = Straight();
        Carry(plan, path, 3, 5, foot: true, top: true, StraightGround());
        Assert.Equal(2, RoadStairRuns.RoadOntoCalls);
        Assert.Equal(0, RoadStairRuns.RoadOntoUnplanned);
    }

    /// <summary>A road goes on past a carried end only straight ahead (RoadStairRuns.RoadGoesOnPast given the end's ring): the
    /// ring's straight-on arc, the arc a through ring cuts (RoadEndRing.CutArc, 41.8 degrees either side for a 4 m road).</summary>
    [Fact]
    public void TheStraightOnArcIsTheArcAThroughRingCuts()
    {
        var ring = new RoadEndRing(new Vector2(0f, 0f), 90f, 40f, through: true);
        Assert.True(ring.StraightOn(90f));
        Assert.True(ring.StraightOn(112.5f));
        Assert.True(ring.StraightOn(67.5f));
        Assert.False(ring.StraightOn(135f));
        Assert.False(ring.StraightOn(270f));
        Assert.True(ring.ArcCut(22.5f));
        Assert.False(ring.ArcCut(45f));
    }

    /// <summary>The short road is judged by generation's own refusals (RoadNetworkGenerator.BuiltRoadRefusal), so where no
    /// earthwork cap is set it is judged by none of the cap's, as generation judges every road then; but a ridge is judged
    /// with or without a cap (RoadNetworkGenerator.AlongARidge): carried on 9 m over ground under it on both sides it would
    /// stand on one, and the floors are kept.</summary>
    [Fact]
    public void WithNoEarthworkCapTheShortRoadIsStillJudgedForItsRidge()
    {
        Assert.Equal(0f, RoadNetworkGenerator.EarthworkCap);
        var (plan, path) = Straight();
        StairPlan carried = Carry(plan, path, 3, 5, foot: true, top: false, StraightGround((x, z) => z > -40f ? 31f : null));
        Assert.Same(plan, carried);
        Assert.Equal(0, carried.FootFloorsTrimmed);
        Assert.Contains("foot: " + RoadNetworkGenerator.RidgeRoadRefusal, RoadStairRuns.EndFloorsKept.Keys);
    }

    /// <summary>Water is read as the search read the road (RoadStairRuns.CarryWater: RoadWater.Walked on the world's height
    /// and river weight): the ground under the floors' line dry, but a river's core: the floors are kept.</summary>
    [Fact]
    public void FloorsOverARiversCoreAreKept()
    {
        var (plan, path) = Straight();
        StairPlan kept = Carry(plan, path, 3, 5, foot: true, top: false, StraightGround(), river: (x, z) => z < 3f ? 1f : 0f);
        Assert.Same(plan, kept);
        Assert.Contains("foot: water", RoadStairRuns.EndFloorsKept.Keys);
    }

    /// <summary>
    /// A top the next stretch's staircase sets off from is not carried onto: on a road winding back on itself, the next
    /// stretch starts 16 m from the top in a straight line (within StoneGap: RoadStairRuns.MeetsEnd, as the next stretch
    /// decides), though 35 m along the road. Read along the road the top looked open; carried, the next staircase then set
    /// off from the end the road had been carried onto.
    /// </summary>
    [Fact]
    public void ATopTheNextStaircaseSetsOffFromOnAWindingRoadIsNotCarried()
    {
        var (plan, _) = Straight();
        var path = new List<Vector2> { new(0f, -24f), new(0f, -16f), new(0f, -8f), new(0f, 0f), new(0f, 8f), new(0f, 16f), new(0f, 24f), new(8f, 32f), new(16f, 32f), new(16f, 24f), new(16f, 16f) };
        Assert.True(RoadStairRuns.Along(path, 5, 9) >= RoadStairRuns.StoneGap + RoadPathfinder.CellSize);
        Assert.True(RoadStairRuns.SetsOffFrom(path, false, 5, new Vector2(plan.End.x, plan.End.z), plan.End.y, 9, 10, StraightGround()));
        StairPlan kept = Carry(plan, path, 3, 5, foot: false, top: true, StraightGround(), next: (9, 10));
        Assert.Same(plan, kept);
        Assert.Equal(new[] { "top: the next staircase sets off from it" }, RoadStairRuns.EndFloorsKept.Keys.ToArray());
        // With no next stretch there it is carried over as before.
        RoadStairRuns.EndFloorsKept.Clear();
        Assert.Equal(2, Carry(plan, path, 3, 5, foot: false, top: true, StraightGround()).TopFloorsTrimmed);
    }

    /// <summary>An end carried onto is not moved after it was judged: the foot is not lowered toward its road
    /// (StairLayout.LowerFoot: it starts on its kept floor row, no sunk step under it to lower), nor the top carried out over
    /// a lip (RoadStairRuns.TopOntoSurface), which a staircase with no floors carried over would take there.</summary>
    [Fact]
    public void AnEndCarriedOntoIsNotMovedAfter()
    {
        var (plan, path) = Straight();
        StairPlan carried = Carry(plan, path, 3, 5, foot: true, top: true, StraightGround());
        Assert.Equal(2, carried.FootFloorsTrimmed);
        Assert.Equal(2, carried.TopFloorsTrimmed);
        // The road 10 m under the foot a few metres back: the foot stays.
        StairPlan untrimmed = carried.Copy();
        untrimmed.FootFloorsTrimmed = untrimmed.TopFloorsTrimmed = 0;
        Assert.Equal(0, carried.SunkPieces);
        Vector3 foot = carried.Foot!.Value;
        Assert.Null(StairLayout.LowerFoot(carried, StraightGround(), RoadStairRuns.Width, 30f, 4f, 0.2f));
        Assert.Equal(foot, carried.Foot!.Value);
        // A bank rising past the top, a metre over it 2.5 m on, to 3 m over it: a top not carried onto is carried out over it.
        float e = carried.End.z;
        Func<float, float, float> bank = (x, z) => z > e ? Mathf.Min(43.9f + (z - e) * 0.5f, 46.9f) : StraightGround()(x, z);
        Assert.True(RoadStairRuns.TopOntoSurface(untrimmed.Copy(), path, 6, bank, out _, out _));
        Vector3 end = carried.End;
        Assert.False(RoadStairRuns.TopOntoSurface(carried, path, 6, bank, out _, out _));
        Assert.Equal(end, carried.End);
        Assert.Null(carried.TopCrested);
    }

    /// <summary>One try, never a shorter carry: water where the carry's road would start (a ring's radius behind the floor
    /// next to the stair) but not further back, where a carry keeping more rows would start it. The foot stays as laid, all
    /// its floors kept, for the one carry's reason; its road is asked once.</summary>
    [Fact]
    public void AFootWhoseCarryFailsIsNotCarriedShorter()
    {
        var (plan, path) = Straight();
        float start = 5.5f - new RoadEndRing(new Vector2(0f, 5.5f), 0f, 40f).Radius;
        Assert.Same(plan, Carry(plan, path, 3, 5, foot: true, top: false, StraightGround(), river: (x, z) => Mathf.Abs(z - start) < 0.5f ? 1f : 0f));
        Assert.Equal(new[] { "foot: water" }, RoadStairRuns.EndFloorsKept.Keys.ToArray());
        Assert.Equal(1, RoadStairRuns.EndsTried);
        Assert.Equal(1, RoadStairRuns.RoadOntoCalls);
        Assert.Equal(0, RoadStairRuns.EndsCarried);
        // Reset with the other counters each generation.
        RoadNetworkGenerator.Reset();
        Assert.Equal(0, RoadStairRuns.EndsTried);
    }

    [Fact]
    public void AFootNoShorterCarryPassesStaysAsLaid()
    {
        var (plan, path) = Straight();
        Assert.Same(plan, Carry(plan, path, 3, 5, foot: true, top: false, StraightGround(), river: (x, z) => z < 8f ? 1f : 0f));
        Assert.Equal(new[] { "foot: water" }, RoadStairRuns.EndFloorsKept.Keys.ToArray());
    }

    [Fact]
    public void FloorsBesideADropAreCarriedOverAsAnyRoadOnABench()
    {
        // The ground level under the floors' line but falling 2 m a metre and a half west of it: a road on a bench along a
        // drop, as any road may be built; the road takes the floors over.
        var (plan, path) = Straight();
        StairPlan carried = Carry(plan, path, 3, 5, foot: true, top: false, StraightGround((x, z) => z > -2f && x < -1.5f ? 37.9f : null));
        Assert.Equal(2, carried.FootFloorsTrimmed);
    }

    /// <summary>
    /// In game: a black marble staircase's foot, the road's end at 1808,-6616 (92.6), arriving from the south.
    /// Six flat floors (walking surface 92.687) laid on north from its ring to the first stair at 1808.8,-6602.3, the ground a
    /// few metres west of them at 89.8, about 3 m under them; east of them a hillside a little under them. The pieces as laid.
    /// The drop is on one side only, so the road there is no ridge or trench as the road builder reads one (RoadRidges), and
    /// every owner of the road passes it: the road is carried on to the floor next to the stair, its back edge 9.9 m from the
    /// road's end, that floor and the fillers past it kept (nothing limits a carry but those owners). Over level ground too.
    /// </summary>
    [Fact]
    public void TheBlackMarbleFootIsCarriedToTheFloorNextToItsStair()
    {
        const string F = "blackmarble_floor", B = "blackmarble_2x1x1", S = "blackmarble_stair";
        StairPlan plan = Laid("blackmarble", 0f, new Vector3(1807.665f, 93.687f, -6599.496f), 337.5f,
            (StairPieceKind.Landing, F, 1807.894f, 92.187f, -6613.504f, 0f),
            (StairPieceKind.Landing, F, 1808.200f, 92.187f, -6611.963f, 22.5f),
            (StairPieceKind.Landing, F, 1808.966f, 92.187f, -6610.115f, 22.5f),
            (StairPieceKind.Landing, F, 1809.272f, 92.187f, -6608.574f, 0f),
            (StairPieceKind.Landing, F, 1809.272f, 92.187f, -6606.574f, 0f),
            (StairPieceKind.Landing, F, 1809.272f, 92.187f, -6604.574f, 0f),
            (StairPieceKind.Filler, B, 1809.272f, 92.187f, -6603.074f, 180f),
            (StairPieceKind.Filler, B, 1809.005f, 92.187f, -6602.729f, 337.5f),
            (StairPieceKind.Stair, S, 1808.813f, 92.687f, -6602.268f, 157.5f),
            (StairPieceKind.Landing, F, 1808.048f, 93.187f, -6600.420f, 337.5f));
        plan.Ring = new RoadEndRing(new Vector2(1808f, -6616f), 0f, 92.6f);
        plan.RingStart = new Vector2(1807.894f, -6614.504f);
        var path = new List<Vector2> { new(1808f, -6640f), new(1808f, -6632f), new(1808f, -6624f), new(1808f, -6616f), new(1800f, -6600f), new(1792f, -6584f), new(1784f, -6568f), new(1776f, -6552f) };
        float Ground(float x, float z) => z <= -6613f ? 92.6f : x <= 1809f ? 89.8f : 92.437f;
        StairPlan carried = Carry(plan, path, 3, 7, foot: true, top: false, Ground);
        Assert.NotSame(plan, carried);
        Assert.Equal(5, carried.FootFloorsTrimmed);
        Assert.Equal(StairPieceKind.Landing, carried.Pieces[0].Kind);
        Assert.Equal(-6604.574f, carried.Pieces[0].Position.z, 3);
        Assert.Equal(-6605.574f, carried.Foot!.Value.z, 2);
        Assert.Equal(1, StairLayout.EndFloorRows(carried, top: false, RoadStairRuns.Width));
        Assert.Empty(RoadStairRuns.EndFloorsKept);
        Assert.NotSame(plan, Carry(plan, path, 3, 7, foot: true, top: false, (x, z) => 92.6f));
        Assert.Empty(RoadStairRuns.EndFloorsKept);
    }

    /// <summary>
    /// In game, the same foot as laid in generation: the road came down from 1776,-6552 and the search cut its
    /// descent into two stair stretches, the second only 8 m, 1808,-6608 to 1808,-6616, out of a dip at 1808,-6608 (a valley
    /// pair, RidgePair). The second was laid off the shared floor (valleyMeet) down to the road's end at 1808,-6616, its foot
    /// there set off from its ring, the six floors as laid on to its first stair. That foot is its road's end, not the shared
    /// floor: it is carried as any foot. The ends a valley's staircases share are not: the first's end in the dip, the
    /// second's on the shared floor, and the second's end at the road where a further valley or crest meets it there.
    /// </summary>
    [Fact]
    public void AValleysSecondIsCarriedAtItsRoadsEndNotAtTheSharedFloor()
    {
        var ends = RoadStairRuns.CarriedEnds(reversed: true, valleyFirst: false, ridgeFirst: false, valleyMeet: true);
        Assert.Equal((true, false), ends);
        const string F = "blackmarble_floor", B = "blackmarble_2x1x1", S = "blackmarble_stair";
        StairPlan plan = Laid("blackmarble", 0f, new Vector3(1807.665f, 93.687f, -6599.496f), 337.5f,
            (StairPieceKind.Landing, F, 1807.894f, 92.187f, -6613.504f, 0f),
            (StairPieceKind.Landing, F, 1808.200f, 92.187f, -6611.963f, 22.5f),
            (StairPieceKind.Landing, F, 1808.966f, 92.187f, -6610.115f, 22.5f),
            (StairPieceKind.Landing, F, 1809.272f, 92.187f, -6608.574f, 0f),
            (StairPieceKind.Landing, F, 1809.272f, 92.187f, -6606.574f, 0f),
            (StairPieceKind.Landing, F, 1809.272f, 92.187f, -6604.574f, 0f),
            (StairPieceKind.Filler, B, 1809.272f, 92.187f, -6603.074f, 180f),
            (StairPieceKind.Filler, B, 1809.005f, 92.187f, -6602.729f, 337.5f),
            (StairPieceKind.Stair, S, 1808.813f, 92.687f, -6602.268f, 157.5f),
            (StairPieceKind.Landing, F, 1808.048f, 93.187f, -6600.420f, 337.5f));
        plan.Reversed = true;
        plan.Ring = new RoadEndRing(new Vector2(1808f, -6616f), 0f, 92.6f);
        plan.RingStart = new Vector2(1807.894f, -6614.504f);
        // Down from the top: the first stretch to the dip, the second out of it to the road's end, the road on south.
        var path = new List<Vector2> { new(1776f, -6552f), new(1784f, -6568f), new(1792f, -6584f), new(1800f, -6600f), new(1808f, -6608f), new(1808f, -6616f), new(1808f, -6624f), new(1808f, -6632f), new(1808f, -6640f) };
        float Ground(float x, float z) => z <= -6613f ? 92.6f : x <= 1809f ? 89.8f : 92.437f;
        StairPlan carried = ends.foot || ends.top ? Carry(plan, path, 0, 5, ends.foot, ends.top, Ground) : plan;
        Assert.NotSame(plan, carried);
        Assert.Equal(5, carried.FootFloorsTrimmed);
        Assert.Empty(RoadStairRuns.EndFloorsKept);
        // Nothing from the floor next to the stair on (that floor, the flight, and the floor shared with the valley's first at
        // its other end) is taken.
        Assert.Equal(plan.Pieces.Skip(5).Select(p => p.Position), carried.Pieces.Select(p => p.Position));

        // Climbing out of a valley (laid up from the shared floor), the second's road end is its top.
        Assert.Equal((false, true), RoadStairRuns.CarriedEnds(reversed: false, valleyFirst: false, ridgeFirst: false, valleyMeet: true));
        // Its end at the road shared too, with a further valley's or crest's staircase: neither end.
        Assert.Equal((false, false), RoadStairRuns.CarriedEnds(reversed: true, valleyFirst: true, ridgeFirst: false, valleyMeet: true));
        Assert.Equal((false, false), RoadStairRuns.CarriedEnds(reversed: false, valleyFirst: false, ridgeFirst: true, valleyMeet: true));
        // Any other staircase as before: the first into a valley keeps its foot in the dip, the first over a crest its top.
        Assert.Equal((false, true), RoadStairRuns.CarriedEnds(reversed: true, valleyFirst: true, ridgeFirst: false, valleyMeet: false));
        Assert.Equal((true, false), RoadStairRuns.CarriedEnds(reversed: false, valleyFirst: false, ridgeFirst: true, valleyMeet: false));
        Assert.Equal((true, true), RoadStairRuns.CarriedEnds(reversed: false, valleyFirst: false, ridgeFirst: false, valleyMeet: false));
    }

    /// <summary>The black marble staircase's top as laid in game (1776,-6552 to 1808,-6616): its last stair at 1782.4,-6558.9
    /// (base 106.687), four flat floors past it (walking surface 107.687), the first turned 292.5, three 315; the ground a little
    /// under them to the south-west, about 0.7 m under them to the north-east; the road on north past the last floor, rising to
    /// 108.2 at 1773.2,-6541.6. The pieces as laid; the road's next path points, a cell apart, along its line.</summary>
    private static (StairPlan plan, List<Vector2> path, Func<float, float, float> ground) BlackMarbleTop()
    {
        const string F = "blackmarble_floor", S = "blackmarble_stair";
        StairPlan plan = Laid("blackmarble", 157.5f, new Vector3(1775.727f, 107.687f, -6553.701f), 315f,
            (StairPieceKind.Stair, S, 1782.417f, 106.687f, -6558.874f, 112.5f),
            (StairPieceKind.Landing, F, 1780.569f, 107.187f, -6558.109f, 292.5f),
            (StairPieceKind.Landing, F, 1779.262f, 107.187f, -6557.236f, 315f),
            (StairPieceKind.Landing, F, 1777.848f, 107.187f, -6555.822f, 315f),
            (StairPieceKind.Landing, F, 1776.434f, 107.187f, -6554.408f, 315f));
        var path = new List<Vector2> { new(1808f, -6616f), new(1792f, -6584f), new(1784f, -6568f), new(1776f, -6552f), new(1776f, -6544f), new(1772f, -6536f) };
        Vector2 top = new(1776.434f, -6554.408f), northEast = new Vector2(1f, 1f).normalized;
        float Ground(float x, float z)
        {
            if (z > -6553.5f) return 107.7f + 0.047f * (z + 6552.2f);
            return Vector2.Dot(new Vector2(x, z) - top, northEast) > 0.5f ? 107f : 107.5f;
        }
        return (plan, path, Ground);
    }

    /// <summary>
    /// In game: the black marble top kept its floor next to the stair, the road carried over the other three (the log: 3
    /// floors carried, the road's new end 1778.0,-6555.3). Carried onto the stair itself, the road clipped it. The rule: the
    /// floor next to the stair stays, the road meeting it, the top ending on its far edge, at the first try.
    /// </summary>
    [Fact]
    public void TheBlackMarbleTopsRoadMeetsTheFloorNextToItsStair()
    {
        var (plan, path, ground) = BlackMarbleTop();
        StairPlan carried = Carry(plan, path, 0, 3, foot: false, top: true, ground);
        Assert.Empty(RoadStairRuns.EndFloorsKept);
        Assert.Equal(3, carried.TopFloorsTrimmed);
        Assert.Equal(1, StairLayout.EndFloorRows(carried, top: true, RoadStairRuns.Width));
        Assert.Equal(2, carried.Pieces.Count);
        Assert.Equal(StairPieceKind.Landing, carried.LastRowKind);
        // The floor's far edge: a run's half on from its centre the way it is laid (292.5), level with it.
        Assert.Equal(1779.645f, carried.End.x, 2);
        Assert.Equal(-6557.726f, carried.End.z, 2);
        Assert.Equal(107.687f, carried.End.y, 3);
        Assert.Equal(292.5f, carried.EndHeading, 2);
        Assert.Equal(1, RoadStairRuns.RoadOntoCalls);
        Assert.Equal(1, RoadStairRuns.EndsCarried);
    }

    /// <summary>A top on level ground, three floor rows past its last stair: the road takes over all but the row next to the
    /// stair, as a foot's road does, at the first try.</summary>
    [Fact]
    public void ATopOnLevelGroundIsCarriedAllButItsLandingRow()
    {
        var (plan, path) = Straight();
        Assert.Equal(3, StairLayout.EndFloorRows(plan, top: true, RoadStairRuns.Width));
        StairPlan carried = Carry(plan, path, 3, 5, foot: false, top: true, StraightGround());
        Assert.Equal(2, carried.TopFloorsTrimmed);
        Assert.Equal(1, StairLayout.EndFloorRows(carried, top: true, RoadStairRuns.Width));
        Assert.Equal(StairPieceKind.Landing, carried.LastRowKind);
        Assert.Equal(1, RoadStairRuns.EndsTried);
        Assert.Equal(1, RoadStairRuns.EndsCarried);
        Assert.Equal(1, RoadStairRuns.RoadOntoCalls);
        Assert.Empty(RoadStairRuns.EndFloorsKept);
    }

    /// <summary>
    /// A top whose road runs on to the location the road goes to, its end a trimmed arrival in that location's clearance, is carried
    /// with that location as its target (decision 6d: locations carried by identity), as the road's own last piece is planned into
    /// it: LayOnce hands the road's targets on (RoadStairRuns.Lay, CarryRoadOntoStairs, RoadOnto). Planned with none, the
    /// carry's road was refused by the location's clearance and the top kept its floors. Another location's footprint there keeps
    /// its clearance: a road whose target is elsewhere is refused there as before.
    /// </summary>
    [Fact]
    public void ATopRunningOnIntoItsRoadsOwnLocationIsCarriedWithThatLocationAsItsTarget()
    {
        var (plan, whole) = Straight();
        // The road ends 2.5 m past the top's floors, a trimmed arrival half way into the location's clearance.
        List<Vector2> path = whole.Take(7).ToList();
        const float radius = 10f;
        var location = new Vector2(0f, path[path.Count - 1].y + radius + RoadLocationProtection.Clearance(RoadNetworkGenerator.RoadWidth) / 2f);
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(location, radius) });
        // No target: the carry's road is in a location's footprint, refused; the top stays as laid.
        StairPlan untargeted = plan.Copy();
        Assert.Same(untargeted, Carry(untargeted, path, 3, 5, foot: false, top: true, StraightGround()));
        Assert.Equal(0, RoadStairRuns.EndsCarried);
        Assert.Single(RoadStairRuns.EndFloorsKept);
        // Another location as its target: the footprint there is not its own, refused as before.
        StairPlan elsewhere = plan.Copy();
        Assert.Same(elsewhere, Carry(elsewhere, path, 3, 5, foot: false, top: true, StraightGround(),
            locations: new RoadLocationProtection.Targets(null, new Vector2(0f, 400f))));
        Assert.Equal(0, RoadStairRuns.EndsCarried);
        // The location it goes to: carried, all but its landing row, as on open ground.
        StairPlan carried = Carry(plan.Copy(), path, 3, 5, foot: false, top: true, StraightGround(), locations: new RoadLocationProtection.Targets(null, location));
        Assert.True(carried.TopFloorsTrimmed == 2, string.Join("; ", RoadStairRuns.EndFloorsKept.Select(k => $"{k.Key} x{k.Value}")));
        Assert.Equal(1, StairLayout.EndFloorRows(carried, top: true, RoadStairRuns.Width));
        Assert.Equal(1, RoadStairRuns.EndsCarried);
        Assert.Equal(3, RoadStairRuns.RoadOntoCalls);
    }

    /// <summary>The top by the same rule: water where the carry's road would start again (a ring's radius past the floor
    /// next to the stair). It is not carried shorter: the top stays as laid, kept for that reason, its road asked once.</summary>
    [Fact]
    public void ATopWhoseCarryFailsStaysAsLaid()
    {
        var (plan, path) = Straight();
        float start = 17.5f + new RoadEndRing(new Vector2(0f, 17.5f), 0f, 44f).Radius;
        Assert.Same(plan, Carry(plan, path, 3, 5, foot: false, top: true, StraightGround(), river: (x, z) => Mathf.Abs(z - start) < 0.5f ? 1f : 0f));
        Assert.Equal(new[] { "top: water" }, RoadStairRuns.EndFloorsKept.Keys.ToArray());
        Assert.Equal(1, RoadStairRuns.EndsTried);
        Assert.Equal(1, RoadStairRuns.RoadOntoCalls);
        Assert.Equal(0, RoadStairRuns.EndsCarried);
    }

    /// <summary>
    /// A carry never takes the last landing row at either end: TrimEndFloors keeps at least one row (keep 0 lays nothing), and
    /// an end of a single row has nothing to carry. Neither end of a carried staircase is carried further when laid again:
    /// a staircase carried once keeps its one row at the foot and at the top. And floors the carry cannot trim to one row
    /// (the foot's floor next to its stair 0.1 m off the others) stay as laid, never trimmed shorter or through it.
    /// </summary>
    [Fact]
    public void ACarryNeverTakesTheLastLandingRowAtEitherEnd()
    {
        var (laid, path) = Straight();
        foreach (bool top in new[] { false, true })
        {
            Assert.Null(StairLayout.TrimEndFloors(laid, top, 0, RoadStairRuns.Width));
            for (int keep = 3; keep < 6; keep++) Assert.Null(StairLayout.TrimEndFloors(laid, top, keep, RoadStairRuns.Width));
        }
        StairPlan carried = Carry(laid, path, 3, 5, foot: true, top: true, StraightGround());
        Assert.Equal(1, StairLayout.EndFloorRows(carried, top: false, RoadStairRuns.Width));
        Assert.Equal(1, StairLayout.EndFloorRows(carried, top: true, RoadStairRuns.Width));
        foreach (bool top in new[] { false, true })
            Assert.Null(StairLayout.TrimEndFloors(carried, top, RoadStairRuns.TopLandings, RoadStairRuns.Width));
        // Carried again: nothing left to take, the row stays, its road not asked.
        carried.FootFloorsTrimmed = carried.TopFloorsTrimmed = 0;
        int calls = RoadStairRuns.RoadOntoCalls;
        StairPlan again = Carry(carried, path, 3, 5, foot: true, top: true, StraightGround());
        Assert.Same(carried, again);
        Assert.Equal(calls, RoadStairRuns.RoadOntoCalls);
        Assert.Equal(1, StairLayout.EndFloorRows(again, top: false, RoadStairRuns.Width));
        Assert.Equal(1, StairLayout.EndFloorRows(again, top: true, RoadStairRuns.Width));

        RoadNetworkGenerator.Reset();
        StairPlan plan = laid.Copy(deepPieces: true);
        plan.Pieces[2].Position += new Vector3(0f, 0.1f, 0f);
        Assert.Null(StairLayout.TrimEndFloors(plan, top: false, RoadStairRuns.TopLandings, RoadStairRuns.Width));
        Assert.Same(plan, Carry(plan, path, 3, 5, foot: true, top: false, StraightGround()));
        Assert.Equal(0, RoadStairRuns.EndsTried);
        Assert.Equal(0, RoadStairRuns.RoadOntoCalls);
    }

    /// <summary>
    /// A top whose road arrives off its level: the ground falls 0.9 m just past the floor next to the stair, and a road laid
    /// across there holds the carried road's start to its own height, under the floors. The road is not judged by how level
    /// it arrives: it meets the landing row kept next to the stair, never the stair itself, so the stair is walked as laid
    /// up to a level floor and the road starts again a ring's radius past that floor, none of the staircase in its ring.
    /// </summary>
    [Fact]
    public void ATopWhoseRoadArrivesOffLevelKeepsItsLandingRowAndIsWalkable()
    {
        var (plan, path) = Straight();
        float start = 17.5f + new RoadEndRing(new Vector2(0f, 17.5f), 0f, 44f).Radius;
        Func<float, float, float> ground = (x, z) => z > 18.5f ? 43f : StraightGround()(x, z);
        var world = new World { Height = ground };
        _scope.WithWorld(world);
        var across = new List<Vector2>();
        for (float x = -30f; x <= 30.01f; x += 2f) across.Add(new Vector2(x, start));
        Assert.True(RoadSpatialGrid.AddRoadPath(across, 4f, world));
        RoadSpatialGrid.FinalizeRoadNetwork();
        Assert.True(RoadSpatialGrid.TryGetRoadHeightWithin(new Vector2(0f, start), 1f, out float held));
        Assert.True(44f - held > StairLayout.StairFitsFrom, $"the road across at {held:F2}");

        StairPlan carried = CarryOn(world, plan.Copy(), path, foot: false, top: true);
        Assert.Empty(RoadStairRuns.EndFloorsKept);
        Assert.Equal(2, carried.TopFloorsTrimmed);
        Assert.Equal(1, StairLayout.EndFloorRows(carried, top: true, RoadStairRuns.Width));
        // Walkable: the stair as laid, its top step onto the floor kept, level with the stair's top.
        Assert.Equal(plan.Pieces.Take(8).Select(p => (p.Kind, p.Position)), carried.Pieces.Select(p => (p.Kind, p.Position)));
        Assert.Equal(StairPieceKind.Landing, carried.LastRowKind);
        Assert.Equal(44f, carried.LastRowHeight, 3);
        Assert.Equal(new Vector3(0f, 44f, 17.5f), carried.End);
        // Its road judged by every owner but none of them asked how level it arrives (RoadOnto: none of the staircase in
        // the ring round its start, its start pad laid).
        Assert.Equal(1, RoadStairRuns.RoadOntoCalls);
        Assert.Equal(1, RoadStairRuns.EndsCarried);
    }

    /// <summary>One world for every lay of a road, as a generation's: <see cref="Carry"/> makes a new one each call.</summary>
    private StairPlan CarryOn(World world, StairPlan plan, List<Vector2> path, bool foot, bool top) =>
        RoadStairRuns.CarryRoadOntoStairs(plan, path, 3, 5, foot, top, new List<RoadStairSpan>(), new List<RoadCrossing>(), null, world, world.Height, default);

    /// <summary>A staircase laid again (a road's retry or replan: a fresh copy of its layout) has each end's carry decided
    /// once a generation: the second lay asks RoadOnto's owners nothing and comes out the same, carried or kept, its tallies
    /// counted again as every lay's are.</summary>
    [Fact]
    public void LayingTheSameStaircaseTwiceJudgesItsCarryOnce()
    {
        var (plan, path) = Straight();
        float start = 17.5f + new RoadEndRing(new Vector2(0f, 17.5f), 0f, 44f).Radius;
        // The foot carried, the top kept (its road starts in water): both decisions reused.
        var world = new World { Height = StraightGround(), River = (x, z) => Mathf.Abs(z - start) < 0.5f ? 1f : 0f };
        _scope.WithWorld(world);
        StairPlan first = CarryOn(world, plan.Copy(), path, foot: true, top: true);
        int calls = RoadStairRuns.RoadOntoCalls;
        Assert.Equal(2, calls);
        Assert.Equal(0, RoadStairRuns.CarriesReused);

        StairPlan second = CarryOn(world, plan.Copy(), path, foot: true, top: true);
        Assert.Equal(calls, RoadStairRuns.RoadOntoCalls);
        Assert.Equal(2, RoadStairRuns.CarriesReused);
        Assert.Equal(first.FootFloorsTrimmed, second.FootFloorsTrimmed);
        Assert.Equal(2, second.FootFloorsTrimmed);
        Assert.Equal(0, second.TopFloorsTrimmed);
        Assert.Equal(first.End, second.End);
        Assert.Equal(first.Foot, second.Foot);
        Assert.Equal(first.RingStart, second.RingStart);
        Assert.Equal(first.Ring!.Value.Centre, second.Ring!.Value.Centre);
        Assert.Equal(first.Ring!.Value.Travel, second.Ring!.Value.Travel);
        Assert.Equal(first.Pieces.Select(p => (p.Kind, p.Position)), second.Pieces.Select(p => (p.Kind, p.Position)));
        Assert.Equal(2, RoadStairRuns.EndsCarried);
        Assert.Equal(4, RoadStairRuns.EndsTried);
        Assert.Equal(2, RoadStairRuns.EndFloorsKept["top: water"]);
        // Forgotten with the layouts, each generation.
        RoadNetworkGenerator.Reset();
        Assert.Equal(0, RoadStairRuns.CarriesReused);
        CarryOn(world, plan.Copy(), path, foot: true, top: true);
        Assert.Equal(0, RoadStairRuns.CarriesReused);
        Assert.Equal(calls, RoadStairRuns.RoadOntoCalls);
        Assert.Equal(2, CarryOn(world, plan.Copy(), path, foot: true, top: true).FootFloorsTrimmed);
        Assert.Equal(2, RoadStairRuns.CarriesReused);
    }

    /// <summary>The road there is part of the decision: the same staircase on another line out of its top, or once the island's
    /// network has changed, is decided afresh.</summary>
    [Fact]
    public void ADifferentRoadLineOrNetworkGivesAFreshDecision()
    {
        var (plan, path) = Straight();
        var world = new World { Height = StraightGround() };
        _scope.WithWorld(world);
        CarryOn(world, plan.Copy(), path, foot: false, top: true);
        Assert.Equal(1, RoadStairRuns.RoadOntoCalls);

        // The road bending off east 12 m past the top: another line, judged again.
        var bent = new List<Vector2>(path);
        bent[7] = new Vector2(6f, 32f);
        bent[8] = new Vector2(14f, 38f);
        CarryOn(world, plan.Copy(), bent, foot: false, top: true);
        int calls = RoadStairRuns.RoadOntoCalls;
        Assert.True(calls > 1);
        Assert.Equal(0, RoadStairRuns.CarriesReused);
        // The first line again: its decision is still the generation's.
        Assert.Equal(2, CarryOn(world, plan.Copy(), path, foot: false, top: true).TopFloorsTrimmed);
        Assert.Equal(calls, RoadStairRuns.RoadOntoCalls);
        Assert.Equal(1, RoadStairRuns.CarriesReused);
        // A road or staircase laid on the island since: the network the road is judged against differs.
        RoadNetworkGenerator.IslandNetworkChanged();
        CarryOn(world, plan.Copy(), path, foot: false, top: true);
        Assert.Equal(calls + 1, RoadStairRuns.RoadOntoCalls);
        Assert.Equal(1, RoadStairRuns.CarriesReused);
    }

    /// <summary>The road being planned again as the last resort, or as a trial, plans the carry's road under other switches
    /// (RoadSpatialGrid.PlanningState: the last resort's pitch), so its carry is decided afresh, not replayed from the first
    /// plan; planned so again, it is replayed.</summary>
    [Fact]
    public void ASecondPlanAsTheLastResortOrATrialDecidesItsCarryAfresh()
    {
        var (plan, path) = Straight();
        var world = new World { Height = StraightGround() };
        _scope.WithWorld(world);
        CarryOn(world, plan.Copy(), path, foot: false, top: true);
        Assert.Equal(1, RoadStairRuns.RoadOntoCalls);

        RoadPitches.AsLastResort("a test", () => CarryOn(world, plan.Copy(), path, foot: false, top: true));
        Assert.Equal(2, RoadStairRuns.RoadOntoCalls);
        Assert.Equal(0, RoadStairRuns.CarriesReused);
        RoadGrade.Trial(() => CarryOn(world, plan.Copy(), path, foot: false, top: true));
        Assert.Equal(3, RoadStairRuns.RoadOntoCalls);
        Assert.Equal(0, RoadStairRuns.CarriesReused);

        RoadPitches.AsLastResort("a test", () => CarryOn(world, plan.Copy(), path, foot: false, top: true));
        RoadGrade.Trial(() => CarryOn(world, plan.Copy(), path, foot: false, top: true));
        CarryOn(world, plan.Copy(), path, foot: false, top: true);
        Assert.Equal(3, RoadStairRuns.RoadOntoCalls);
        Assert.Equal(3, RoadStairRuns.CarriesReused);
    }

    /// <summary>The carry's road is a piece planned under what the road being planned holds on this thread (its held points,
    /// its levelled ends: RoadSpatialGrid.PlanningState, as a remembered piece is keyed), so a carry asked with another held
    /// point or levelled end is decided afresh, and asked again with the same ones is replayed.</summary>
    [Fact]
    public void AnotherHeldPointOrLevelledEndDecidesTheCarryAfresh()
    {
        var (plan, path) = Straight();
        var world = new World { Height = StraightGround() };
        _scope.WithWorld(world);
        try
        {
            CarryOn(world, plan.Copy(), path, foot: false, top: true);
            Assert.Equal(1, RoadStairRuns.RoadOntoCalls);

            RoadSpatialGrid.HeldPoints = new List<Vector2> { new(0f, 40f) };
            CarryOn(world, plan.Copy(), path, foot: false, top: true);
            Assert.Equal(2, RoadStairRuns.RoadOntoCalls);
            RoadSpatialGrid.HeldPoints = new List<Vector2> { new(0f, 41f) };
            CarryOn(world, plan.Copy(), path, foot: false, top: true);
            Assert.Equal(3, RoadStairRuns.RoadOntoCalls);
            RoadSpatialGrid.HeldPoints = null;

            RoadSpatialGrid.LevelledEnds = new List<(Vector2 at, float height)> { (new Vector2(0f, 40f), 44f) };
            CarryOn(world, plan.Copy(), path, foot: false, top: true);
            Assert.Equal(4, RoadStairRuns.RoadOntoCalls);
            RoadSpatialGrid.LevelledEnds = new List<(Vector2 at, float height)> { (new Vector2(0f, 40f), 44.5f) };
            CarryOn(world, plan.Copy(), path, foot: false, top: true);
            Assert.Equal(5, RoadStairRuns.RoadOntoCalls);
            Assert.Equal(0, RoadStairRuns.CarriesReused);

            // The same held points and levelled ends again (new lists, the same values): replayed.
            RoadSpatialGrid.LevelledEnds = new List<(Vector2 at, float height)> { (new Vector2(0f, 40f), 44.5f) };
            CarryOn(world, plan.Copy(), path, foot: false, top: true);
            RoadSpatialGrid.LevelledEnds = null;
            RoadSpatialGrid.HeldPoints = new List<Vector2> { new(0f, 40f) };
            CarryOn(world, plan.Copy(), path, foot: false, top: true);
            RoadSpatialGrid.HeldPoints = null;
            CarryOn(world, plan.Copy(), path, foot: false, top: true);
            Assert.Equal(5, RoadStairRuns.RoadOntoCalls);
            Assert.Equal(3, RoadStairRuns.CarriesReused);
        }
        finally
        {
            RoadSpatialGrid.HeldPoints = null;
            RoadSpatialGrid.LevelledEnds = null;
        }
    }

    /// <summary>A carry decided on a worker (an island's) is never replayed for the main thread (a manual road's staircase) at
    /// the same end: network versions are unique in the process, so the main thread's never names the worker's network. Counted
    /// per thread, the worker's writes reached the main thread's count and the main thread replayed the worker's decision against
    /// a network it never saw.</summary>
    [Fact]
    public void AWorkersCarryDecisionIsNeverReplayedOnAnotherThread()
    {
        var (plan, path) = Straight();
        var world = new World { Height = StraightGround() };
        _scope.WithWorld(world);
        int main = RoadNetworkGenerator.IslandNetworkVersion;
        int worker = 0;
        Exception? failed = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                // As many writes of its own as it takes to reach the main thread's version, were versions counted per thread.
                for (int i = 0; i < Math.Max(main, 0) && RoadNetworkGenerator.IslandNetworkVersion != main; i++)
                    RoadNetworkGenerator.IslandNetworkChanged();
                worker = RoadNetworkGenerator.IslandNetworkVersion;
                CarryOn(world, plan.Copy(), path, foot: false, top: true);
            }
            catch (Exception e) { failed = e; }
        });
        thread.Start();
        thread.Join();
        Assert.Null(failed);
        Assert.Equal(1, RoadStairRuns.RoadOntoCalls);
        Assert.NotEqual(main, worker);
        CarryOn(world, plan.Copy(), path, foot: false, top: true);
        Assert.Equal(2, RoadStairRuns.RoadOntoCalls);
        Assert.Equal(0, RoadStairRuns.CarriesReused);
    }
}
