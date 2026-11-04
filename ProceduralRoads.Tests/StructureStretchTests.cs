using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using static ProceduralRoads.Tests.StructureEndTests;

namespace ProceduralRoads.Tests;

/// <summary>
/// One owner for "a stretch of road replaced
/// by a structure" (StructureEnds), judged on the road as BUILT. Each case seen on a test world is reproduced on a small valley.
/// </summary>
public sealed class StructureStretchTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public StructureStretchTests()
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

    private void Use(WorldGenerator world, List<RoadSpatialGrid.PlannedPath> roads)
    {
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, Viaducts = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        RoadNetworkGenerator.PieceCommitted = (plan, _) => roads.Add(plan);
    }

    /// <summary>A road laid before, straight from <paramref name="a"/> to <paramref name="b"/> at the heights
    /// <paramref name="height"/> gives, every 2 m (committed as the network so far).</summary>
    internal static RoadSpatialGrid.PlannedPath LayRoad(Vector2 a, Vector2 b, Func<Vector2, float> height)
    {
        var points = new List<Vector2>();
        var heights = new List<float>();
        int n = Mathf.CeilToInt(Vector2.Distance(a, b) / 2f);
        for (int i = 0; i <= n; i++)
        {
            Vector2 p = Vector2.Lerp(a, b, i / (float)n);
            points.Add(p);
            heights.Add(height(p));
        }
        var plan = new RoadSpatialGrid.PlannedPath(points, heights, new List<RoadPointDebugInfo>(), 4f, Vector2.Distance(a, b), false);
        RoadSpatialGrid.Commit(plan);
        return plan;
    }

    // ---------------------------------------------------------------- the floor

    /// <summary>
    /// A viaduct on a test world (16 m, centre 5288, 4640) with roads part way under it: about 9 m
    /// over its floor, under the 12 m MinViaductPier, and built. The valley here is 14 m deep on the generator's ground (a
    /// 14 m pier: over the floor), but a road laid before runs along its bottom on 4 m of fill, so the finished floor stands
    /// 10 m under the deck. No viaduct stands less than the floor over the finished ground.
    /// </summary>
    [Fact]
    public void NoViaductStandsUnderTheFloorOverTheFinishedGround()
    {
        var world = new RimValley { Depth = 14f };
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        Use(world, roads);
        float floor = world.Top - world.Depth;
        LayRoad(new Vector2(0f, -150f), new Vector2(0f, 150f), _ => floor + 4f);
        // Without the fill the viaduct is over the floor: the case is the fill alone.
        RoadCrossing probe = DvergrViaduct.ValleyCrossing(new Vector2(-world.Rim - 1f, 0f), new Vector2(world.Rim + 1f, 0f), world, out string why)!;
        Assert.True(probe != null, why);
        Assert.True(DvergrViaduct.TallestPier(probe!, world) >= DvergrViaduct.MinViaductPier, "the valley alone is deep enough for a viaduct");

        RoadNetworkGenerator.GenerateRoad(new Vector3(-110f, 0f, 0f), 0f, new Vector3(110f, 0f, 0f), 0f, 4f, "across");
        foreach (RoadCrossing c in RoadNetworkGenerator.GetRoadCrossings().Where(c => c.ValleyJump))
        {
            float tallest = DvergrViaduct.TallestPierOnFinishedGround(c, world, null);
            Assert.True(tallest >= DvergrViaduct.MinViaductPier - 1e-3f,
                $"a viaduct {c.FromBank} -> {c.ToBank} stands {tallest:F1} m over the finished floor, under the {DvergrViaduct.MinViaductPier:F0} m floor");
        }
    }
    /// <summary>The floor over the finished ground counts road under the deck only, never the viaduct's own road at its
    /// ends: a pier 4.7 m in from a bank whose end has stairs read the road at the stairs' foot as its floor (most valley viaducts
    /// were refused as "under the floor").</summary>
    [Fact]
    public void AViaductsOwnRoadAtItsEndsIsNotItsFloor()
    {
        var world = new RimValley { Depth = 14f, Far = d => d < 3f ? 0f : -2f };
        _scope.WithWorld(world);
        float bank = world.Rim + 1f;
        RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-bank, 0f), new Vector2(bank, 0f), world, out string why)!;
        Assert.True(c != null, why);
        var (near, far) = DvergrViaduct.Ends(c!, world);
        Assert.True(far.Steps > 0, "the far end has stairs");
        RoadSpatialGrid.PlannedPath Piece(Vector2 a, Vector2 b, float h) =>
            new(new List<Vector2> { a, b }, new List<float> { h, h }, new List<RoadPointDebugInfo>(), 4f, Vector2.Distance(a, b), false);
        var own = new List<RoadSpatialGrid.PlannedPath> { Piece(near.Apron, near.Joint, near.MeetHeight), Piece(far.Joint, far.Apron, far.MeetHeight) };
        Assert.Equal(DvergrViaduct.TallestPier(c!, world), DvergrViaduct.TallestPierOnFinishedGround(c!, world, own), 3);
    }

    // ---------------------------------------------------------------- the stand point

    /// <summary>A stand point reads the road's surface (RoadHeightAt, through
    /// cli_call): the finished ground on a road, not the generator's ground over a cut, which put the player 8-13 m up beside cut
    /// banks. NaN off the road.</summary>
    [Fact]
    public void AStandPointReadsTheRoadsSurfaceNotTheGroundOverACut()
    {
        var world = new RimValley();
        _scope.WithWorld(world);
        RoadSpatialGrid.Clear();
        LayRoad(new Vector2(-100f, 30f), new Vector2(-60f, 30f), _ => world.Top - 9f);
        Assert.Equal(world.Top - 9f, RoadSpatialGrid.RoadHeightAt(-80f, 31f, 3f), 3);
        Assert.True(float.IsNaN(RoadSpatialGrid.RoadHeightAt(-80f, 40f, 3f)));
    }
    // ---------------------------------------------------------------- the road runs onto the structure

    /// <summary>A road that comes to a viaduct over a valley from the side at both ends: in from (-100, 40) and down the
    /// hillside to the near bank, off the far bank and up the hillside to (100, 40). Planned as the generator plans it
    /// (PlanRoadPathWithCrossings), the viaduct between the two banks.</summary>
    private (RoadCrossing viaduct, List<RoadSpatialGrid.PlannedPath> plans) SideOn(RimValley world)
    {
        _scope.WithWorld(world);
        float bank = world.Rim + 1f;
        RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-bank, 0f), new Vector2(bank, 0f), world, out string why)!;
        Assert.True(c != null, why);
        var path = new List<Vector2> { new(-100f, 40f), new(-60f, 40f), new(-bank - 6f, 14f), new(-bank, 0f), new(bank, 0f), new(bank + 6f, 14f), new(60f, 40f), new(100f, 40f) };
        c!.FromIndex = 3; c.ToIndex = 4;
        var plans = RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing> { c }, 4f, null, null);
        Assert.True(plans != null, RoadSpatialGrid.LastRefusal);
        return (c, plans!);
    }

    /// <summary>Over the road's last ApronLength to an end, every point on the structure's line and level at the end's
    /// height; and the road on under the joining piece (StructureEnds.JoinOverlap) at that height.</summary>
    private static void AssertRunsOntoTheLine(StructureEnds.End end, List<RoadSpatialGrid.PlannedPath> plans, string site)
    {
        Vector2 outward = -end.Up, across = new(-outward.y, outward.x);
        int seen = 0, joint = 0;
        foreach (var r in plans)
            for (int i = 0; i < r.Points.Count; i++)
            {
                float along = Vector2.Dot(r.Points[i] - end.Meet, outward), off = Vector2.Dot(r.Points[i] - end.Meet, across);
                if (Mathf.Abs(off) > 12f) continue;
                if (along >= -0.25f && along <= StructureEnds.ApronLength)
                {
                    seen++;
                    Assert.True(Mathf.Abs(off) <= 0.5f, $"{site}: {along:F1} m out from the end at {end.Meet} the road is {off:F2} m off its line");
                    Assert.True(Mathf.Abs(r.Heights[i] - end.MeetHeight) <= 0.1f, $"{site}: {along:F1} m out the road stands {r.Heights[i]:F2}, the end {end.MeetHeight:F2}");
                }
                if (end.UnderTheJoint(r.Points[i], 1f) && along < -0.25f)
                {
                    joint++;
                    Assert.True(Mathf.Abs(r.Heights[i] - end.MeetHeight) <= 0.1f, $"{site}: under the joining piece the road stands {r.Heights[i]:F2}, the end {end.MeetHeight:F2}");
                }
            }
        Assert.True(seen >= 6, $"{site}: only {seen} road point(s) on the last {StructureEnds.ApronLength} m before the end at {end.Meet}");
        Assert.True(joint >= 1, $"{site}: the road stops at the end's edge {end.Meet}; it does not run on under the joining piece");
    }

    /// <summary>
    /// A 57 m viaduct on a test world (centre 6356, 4324): the road curved in along the hillside past the deck's end at its own height and never ran onto it. Here the
    /// road comes down the hillside to both banks from the side: its last 7 m to each flush end lie on the deck's line,
    /// level with the deck, and run on under the end module.
    /// </summary>
    [Fact]
    public void TheRoadArrivingFromTheSideRunsOntoTheDecksLine()
    {
        var world = new RimValley();
        var (c, plans) = SideOn(world);
        var (near, far) = DvergrViaduct.Ends(c, world);
        Assert.Equal((0, 0), (near.Steps, far.Steps));
        AssertRunsOntoTheLine(near, plans, "flush near");
        AssertRunsOntoTheLine(far, plans, "flush far");
    }

    /// <summary>
    /// A 40 m viaduct on a test world (centre 3488, 7180): the end stairs came down the deck's line while the road passed beside their foot. Here the far end has stairs down to
    /// ground 2 m under the deck, and the road comes to it from the side: it runs onto the flight's line, level with the
    /// foot, and on under the bottom step.
    /// </summary>
    [Fact]
    public void EndStairsMeetTheRoadArrivingFromTheSide()
    {
        var world = new RimValley { Far = d => d < 3f ? 0f : -2f };
        var (c, plans) = SideOn(world);
        var (_, far) = DvergrViaduct.Ends(c, world);
        Assert.True(far.Steps > 0, "the far end has stairs");
        AssertRunsOntoTheLine(far, plans, "stairs far");
    }

    /// <summary>
    /// The 80 m viaduct on a test world at (-3968, -5384), with one black marble floor laid by hand so ground and bridge
    /// surface were nearly even (the earthwork works, only the end bit missing):
    /// the deck's walking surface stopped one module short of the levelled road. Two halves: the road stopped at the deck's
    /// edge, where its earthwork fades; and the end module's course was ruined like any other, leaving the iron floor a metre
    /// down. Now the road runs on under the end module at the deck's height, and over 40 world seeds the end modules keep
    /// every course block.
    /// </summary>
    [Fact]
    public void TheDeckReachesItsEndAndTheRoadRunsUnderIt()
    {
        var world = new RimValley();
        var (c, plans) = SideOn(world);
        var (near, far) = DvergrViaduct.Ends(c, world);
        float deck = DvergrViaduct.DeckHeight(c, world);
        foreach (var (end, which) in new[] { (near, "near"), (far, "far") })
        {
            AssertRunsOntoTheLine(end, plans, $"flush {which}");
            Vector2 module = end.Edge + end.Up * (DvergrViaduct.Module * 0.5f);
            foreach (int seed in Enumerable.Range(1, 40))
            {
                int blocks = DvergrViaduct.Solve(c, world, seed).Count(p => p.Kind == StructurePieceKind.ViaductDeck && p.Prefab == DvergrViaduct.Block
                    && Mathf.Abs(p.Position.y - (deck - DvergrViaduct.CourseDrop)) < 0.01f && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), module) <= 2.5f);
                Assert.True(blocks >= 3, $"seed {seed}, {which} end: {blocks} course block(s) on the end module at {module}: the deck's surface stops a module short");
            }
        }
    }

    /// <summary>
    /// Wooden bridge ends through the same owner (refusing such a bridge lost 11 destinations; the end is adapted instead). A road across a wide river on a slant: each end of its wooden bridge is met by road at its foot, within
    /// a step of its height, and RoadCrossingDetector's banks are no longer where the road stops short of the run.
    /// </summary>
    [Fact]
    public void AWoodenBridgesEndsAreMetByTheRoadAtTheirFoot()
    {
        var world = new BridgeTests.WideRiverWorld();
        _scope.WithWorld(world);
        RoadCrossingDetector.SetFordStyleWeights(1f, 1f, 1f);
        SetPathfinder(new RoadPathfinder(world) { Fords = true, Bridges = true, MaxGrade = 0f });
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        RoadNetworkGenerator.PieceCommitted = (plan, _) => roads.Add(plan);
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-200f, -40f), 0f, new Vector2(200f, 40f), 0f, 4f, "across the river"));
        RoadCrossing c = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), x => x.Kind == CrossingKind.Bridge);
        Assert.False(DvergrViaduct.Applies(c, world));
        var (near, far) = BridgeStructures.Ends(c, world);
        foreach (var (end, which) in new[] { (near, "near"), (far, "far") })
        {
            var at = roads.SelectMany(r => r.Points.Select((p, i) => (p, h: r.Heights[i]))).OrderBy(x => Vector2.Distance(x.p, end.Meet)).First();
            Assert.True(Vector2.Distance(at.p, end.Meet) <= 0.75f, $"{which} end: no road at its foot {end.Meet} (nearest {at.p})");
            Assert.True(StructureEnds.Reachable(at.h, end), $"{which} end: the road at its foot stands {at.h:F2}, the foot {end.MeetHeight:F2}");
        }
        Assert.Equal(0, RoadNetworkGenerator.BridgeEndsUnmet);
    }

    // ---------------------------------------------------------------- on the road as built

    /// <summary>
    /// Two viaducts on a test world: 34 m, centre 5492, 4876 (the paved road climbs steeply
    /// and passes UNDER the deck's end, which hangs overhead) and 16 m, 5288, 4640 (roads part way under the viaduct).
    /// An earlier check passed them: it asked only whether the viaduct's own road, as planned, ended at
    /// each meeting point at its height (UnreachableEnd), and that holds here; road under the deck from elsewhere (a road
    /// laid before, here climbing the west face 3 m inside the rim under the deck's west end; and the 3 m every bridge's
    /// road was painted in under its deck after the check) was never looked at. The final check on the road as built
    /// (BuiltRefusal) refuses the viaduct, and the generator searches the road again without it.
    /// </summary>
    [Fact]
    public void TheFinalCheckSeesRoadUnderTheDeck()
    {
        var world = new RimValley();
        _scope.WithWorld(world);
        float bank = world.Rim + 1f;
        RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-bank, 0f), new Vector2(bank, 0f), world, out string why)!;
        Assert.True(c != null, why);
        var (near, far) = DvergrViaduct.Ends(c!, world);
        RoadSpatialGrid.PlannedPath Piece(Vector2 a, Vector2 b, float h) =>
            new(new List<Vector2> { a, b }, new List<float> { h, h }, new List<RoadPointDebugInfo>(), 4f, Vector2.Distance(a, b), false);
        var own = new List<RoadSpatialGrid.PlannedPath> { Piece(near.Apron, near.Joint, near.MeetHeight), Piece(far.Joint, far.Apron, far.MeetHeight) };
        var crossings = new List<RoadCrossing> { c! };
        Assert.Null(RoadNetworkGenerator.BuiltRefusal(own, crossings, out _, out _));
        float x = near.Edge.x + 4f;
        LayRoad(new Vector2(x, -8f), new Vector2(x, 8f), p => world.GetHeight(p.x, p.y));
        Assert.Null(RoadNetworkGenerator.UnreachableEnd(own, crossings, out _));   // the earlier check: passed
        Assert.Same(c, RoadNetworkGenerator.BuiltRefusal(own, crossings, out Vector2 at, out string refused));
        Assert.Contains("road under the deck", refused);
        Assert.True(Mathf.Abs(at.x - x) <= 1.5f, $"found at {at}");
    }

    /// <summary>The other half of the road-under-a-deck case: a road planned after a viaduct is laid does not run under its deck. The
    /// search keeps out from under a deck but for its two bank cells, where the deck's end overhangs.</summary>
    [Fact]
    public void NoLaterRoadRunsUnderADeck()
    {
        var world = new RimValley();
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        Use(world, roads);
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-110f, 0f, 0f), 0f, new Vector3(110f, 0f, 0f), 0f, 4f, "across"), "no road");
        RoadCrossing c = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), x => x.ValleyJump);
        var (near, _) = DvergrViaduct.Ends(c, world);
        // A road planned under the deck's west end, along z, 1 m inside its edge.
        Vector2 under = near.Edge + near.Up * 1f;
        var plan = new RoadSpatialGrid.PlannedPath(new List<Vector2> { under + new Vector2(0f, -20f), under, under + new Vector2(0f, 20f) },
            new List<float> { world.Top - 3f, world.Top - 3f, world.Top - 3f }, new List<RoadPointDebugInfo>(), 4f, 40f, false);
        Assert.NotNull(RoadNetworkGenerator.UnderALaidDeck(new List<RoadSpatialGrid.PlannedPath> { plan }, out _));
        AssertNoRoadUnderTheDeck(c, world);
    }

    /// <summary>No stored road point under the deck (between its two edges, within its half width) lower than the deck, but
    /// under the joining pieces at their meeting height.</summary>
    internal static void AssertNoRoadUnderTheDeck(RoadCrossing c, WorldGenerator world)
    {
        var (near, far) = DvergrViaduct.Ends(c, world);
        float deck = DvergrViaduct.DeckHeight(c, world);
        Vector2 dir = c.Direction, side = new(-dir.y, dir.x);
        float a0 = Vector2.Dot(near.Edge - c.FromBank, dir), a1 = Vector2.Dot(far.Edge - c.FromBank, dir);
        foreach (var rp in RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(c.Center.x, 0f, c.Center.y), c.Width))
        {
            if (!rp.IsRoad) continue;
            float along = Vector2.Dot(rp.p - c.FromBank, dir), off = Vector2.Dot(rp.p - c.FromBank, side);
            if (along <= a0 + 0.25f || along >= a1 - 0.25f || Mathf.Abs(off) > DvergrViaduct.HalfWidth) continue;
            bool joint = (near.UnderTheJoint(rp.p, DvergrViaduct.HalfWidth) && Mathf.Abs(rp.h - near.MeetHeight) <= 0.1f)
                || (far.UnderTheJoint(rp.p, DvergrViaduct.HalfWidth) && Mathf.Abs(rp.h - far.MeetHeight) <= 0.1f);
            Assert.True(joint || rp.h >= deck - 0.1f, $"road at {rp.p} stands {rp.h:F2} under the deck at {deck:F2}");
        }
    }
}
