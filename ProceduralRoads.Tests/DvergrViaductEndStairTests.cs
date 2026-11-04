using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// End stairs of the Dvergr viaducts: stairs onto or off a viaduct cost much more
/// than ordinary stairs, so level banks win; a viaduct is cheaper the taller its piers, up to the ceiling; at most
/// <see cref="DvergrViaduct.MaxEndStairRise"/> of end stairs at either end, else no viaduct there; and an end's stairs
/// follow the staircases' foot rule, their foot on the road (as at the far bank at -6598, 4551), with no stairs where a
/// bank is at the deck's height. A reload's viaducts are DvergrViaductReloadTests.
/// </summary>
public sealed class DvergrViaductEndStairTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly float _maxEndStairRise = DvergrViaduct.MaxEndStairRise;

    /// <summary>A Mistlands valley along z: flats at <see cref="Top"/> (the +x flat <see cref="FarDrop"/> lower, from
    /// its rim on), a floor <see cref="Depth"/> under <see cref="Top"/> within <see cref="FloorHalf"/> of x = 0, faces
    /// of 1.2 between. Depth and drop may change with z. Sea past |x|, |z| = 200, so there is no way round.</summary>
    internal sealed class StepValley : WorldGenerator
    {
        public float Top = 60f, FloorHalf = 8f, Face = 1.2f;
        public Func<float, float> Depth = _ => 14f;
        public Func<float, float> FarDrop = _ => 0f;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float top = wx > 0f ? Top - FarDrop(wy) : Top;
            float floor = Top - Depth(wy);
            return Mathf.Max(floor, top - Mathf.Clamp(top - floor - (Mathf.Abs(wx) - FloorHalf) * Face, 0f, top - floor));
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 77;
    }

    public DvergrViaductEndStairTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        DvergrViaduct.MaxEndStairRise = _maxEndStairRise;
        DvergrViaduct.MinViaductPier = DvergrViaduct.DefaultMinViaductPier;
        RoadNetworkGenerator.PieceCommitted = null;
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        RoadNetworkPersistence.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pathfinder);

    private static RoadPathfinder Finder(WorldGenerator world) =>
        new(world) { Stairs = true, Viaducts = true, TerrainVarianceThreshold = 1000f, Meander = 0f };

    private static float Price(RoadCrossing c, WorldGenerator world) =>
        DvergrViaduct.Price(c, world, RoadConstants.DefaultViaductCostFixed, RoadConstants.DefaultViaductCostPerMeter, RoadConstants.DefaultViaductEndStairCostPerMeter);

    /// <summary>The valley crossed straight along x at <paramref name="z"/>, rim to rim.</summary>
    private static RoadCrossing? Across(StepValley world, float z, float half = 24f) =>
        DvergrViaduct.ValleyCrossing(new Vector2(-half, z), new Vector2(half, z), world, out _);

    // ------------------------------------------------- rule 1: level to level

    [Fact]
    public void ALevelToLevelViaductIsPricedUnderOneWithEndStairs()
    {
        var level = new StepValley();
        var stepped = new StepValley { FarDrop = _ => 2f };
        _scope.WithWorld(level);
        RoadCrossing a = Across(level, 0f)!;
        var (an, af) = DvergrViaduct.Ends(a, level);
        Assert.Equal((0, 0), (an.Steps, af.Steps));
        _scope.WithWorld(stepped);
        RoadCrossing b = Across(stepped, 0f)!;
        var (bn, bf) = DvergrViaduct.Ends(b, stepped);
        Assert.Equal((0, 2), (bn.Steps, bf.Steps));
        // Same span, same piers: the difference is the end stairs, at ten times an ordinary stair's metre.
        Assert.Equal(a.Width, b.Width, 2);
        Assert.Equal(DvergrViaduct.TallestPier(a, level), DvergrViaduct.TallestPier(b, stepped), 2);
        Assert.Equal(Price(a, level) + 2f * RoadConstants.DefaultViaductEndStairCostPerMeter, Price(b, stepped), 1);
        Assert.True(RoadConstants.DefaultViaductEndStairCostPerMeter >= 10f * RoadConstants.DefaultStairCostPerMeterClimbed);
    }

    /// <summary>A staircase's price is fixed in code, with no setting: 1000 to start one, 10 a metre
    /// climbed, in the Mountains.</summary>
    [Fact]
    public void AStaircasesPriceIsFixedInCode()
    {
        Assert.Equal((1000f, 10f), (RoadConstants.DefaultStairCostFixed, RoadConstants.DefaultStairCostPerMeterClimbed));
        Assert.DoesNotContain(typeof(ProceduralRoadsPlugin).GetFields(), f => f.Name.StartsWith("StairCost"));
        Assert.DoesNotContain(typeof(RoadPathfinder).GetFields(), f => f.IsStatic && f.Name.StartsWith("ConfiguredStairCost"));
    }

    [Fact]
    public void TheSearchGoesToALevelCrossingOverOneWithEndStairs()
    {
        // North of z = 0 the far flat is 2 m lower: a viaduct there has 2 m of stairs at its far end. South of it the
        // banks are level. The road runs along z = 12.
        var world = new StepValley { FarDrop = z => z >= 0f ? 2f : 0f };
        _scope.WithWorld(world);
        RoadPathfinder finder = Finder(world);
        Assert.NotNull(finder.FindPath(new Vector2(-100f, 12f), new Vector2(100f, 12f)));
        var (from, to) = Assert.Single(finder.LastViaductJumps);
        RoadCrossing chosen = DvergrViaduct.ValleyCrossing(from, to, world, out string why)!;
        Assert.True(chosen != null, why);
        var (near, far) = DvergrViaduct.Ends(chosen!, world);
        Assert.True(near.Steps == 0 && far.Steps == 0, $"the viaduct at z {chosen!.Center.y:F0} has end stairs {near.Steps} and {far.Steps}");

        // Control: with end stairs free, the straight crossing is cheaper, and taken.
        RoadPathfinder free = Finder(world);
        free.ViaductEndStairCostPerMeter = 0f;
        Assert.NotNull(free.FindPath(new Vector2(-100f, 12f), new Vector2(100f, 12f)));
        var (f1, f2) = Assert.Single(free.LastViaductJumps);
        var (fn, ff) = DvergrViaduct.Ends(DvergrViaduct.ValleyCrossing(f1, f2, world, out _)!, world);
        Assert.True(fn.Steps + ff.Steps > 0, "without the end stairs' price the road should cross where it is, stairs and all");
    }

    // ------------------------------------------------- rule 2: taller is better

    [Fact]
    public void ATallerViaductIsPricedUnderAShorterOne()
    {
        // Priced all the way down to the 6 m least, under the viaduct floor too (that floor is DvergrViaductHeightTests').
        DvergrViaduct.MinViaductPier = DvergrViaduct.MinClearance;
        float last = float.MaxValue, lastBase = 0f;
        foreach (float depth in new[] { 7f, 10f, 14f, 18f })
        {
            var world = new StepValley { Depth = _ => depth };
            _scope.WithWorld(world);
            RoadCrossing c = Across(world, 0f, 8f + depth / 1.2f + 2f)!;
            Assert.True(c != null, $"no viaduct over a {depth} m valley");
            float price = Price(c!, world), ownBase = RoadConstants.DefaultViaductCostFixed + RoadConstants.DefaultViaductCostPerMeter * c!.Width;
            // A deeper valley is wider here, so its viaduct is longer and its price before the height dearer; the
            // height brings it under the shallower one's.
            Assert.True(ownBase > lastBase);
            Assert.True(price < last, $"{depth} m: {price:F0}, not under {last:F0}");
            Assert.Equal(ownBase * DvergrViaduct.HeightFactor(DvergrViaduct.TallestPier(c, world)), price, 1);
            last = price;
            lastBase = ownBase;
        }
    }

    [Fact]
    public void TheSearchCrossesWhereTheValleyIsDeep()
    {
        // 18 m deep south of z = 0, 12 m north of it; the road runs along z = 12.
        var world = new StepValley { Depth = z => z < 0f ? 18f : 12f };
        _scope.WithWorld(world);
        RoadPathfinder finder = Finder(world);
        Assert.NotNull(finder.FindPath(new Vector2(-100f, 12f), new Vector2(100f, 12f)));
        var (from, to) = Assert.Single(finder.LastViaductJumps);
        RoadCrossing chosen = DvergrViaduct.ValleyCrossing(from, to, world, out _)!;
        Assert.True(DvergrViaduct.TallestPier(chosen, world) > 15f, $"crossed {DvergrViaduct.Describe(chosen, world)}");
        Assert.Empty(finder.LastStairSteps);
    }

    // ------------------------------------------------- rule 3: the end-stair cap

    [Fact]
    public void MoreEndStairsThanTheMostAllowedIsNoViaduct()
    {
        Assert.Equal(3f, DvergrViaduct.DefaultMaxEndStairRise);
        // The far flat 4 m under the near one: 4 m of stairs at the far end. A bridge's banks may differ 2.5 m at most,
        // so the valley search never offers this one; a crossing made by hand is asked the kit's own question.
        var world = new StepValley { FarDrop = _ => 4f };
        _scope.WithWorld(world);
        RoadCrossing c = RoadCrossing.Between(new Vector2(-24f, 0f), new Vector2(24f, 0f), 46f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        Assert.Equal(4, DvergrViaduct.Ends(c, world).far.Steps);
        string? refused = DvergrViaduct.Refusal(c, world);
        Assert.NotNull(refused);
        Assert.Contains("end stairs 4 m", refused);
        Assert.False(DvergrViaduct.Applies(c, world));
        DvergrViaduct.MaxEndStairRise = 5f;
        Assert.True(DvergrViaduct.Applies(c, world), DvergrViaduct.Refusal(c, world));

        // The setting is the cap: 2 m of stairs is a viaduct at 3 m and at 2 m, not at 1 m.
        var two = new StepValley { FarDrop = _ => 2f, Depth = _ => 18f };
        _scope.WithWorld(two);
        foreach (var (most, built) in new[] { (3f, true), (2f, true), (1f, false) })
        {
            DvergrViaduct.MaxEndStairRise = most;
            RoadCrossing? v = Across(two, 0f);
            Assert.True((v != null) == built, $"at {most} m: {(v == null ? "refused" : "built")}");
            // And the search: no viaduct it buys has more end stairs than the cap. (An end's height comes from the
            // road's arrival, so the search may find a deck a rise lower, its near end levelled into the rim, within it.)
            RoadPathfinder finder = Finder(two);
            Assert.NotNull(finder.FindPath(new Vector2(-100f, 0f), new Vector2(100f, 0f)));
            foreach (var (from, to) in finder.LastViaductJumps)
            {
                var (n, f) = DvergrViaduct.Ends(DvergrViaduct.ValleyCrossing(from, to, two, out _)!, two);
                Assert.True(n.Rise <= most + 1e-3f && f.Rise <= most + 1e-3f, $"at {most} m: end stairs {n.Rise} and {f.Rise}");
            }
        }
    }

    // ------------------------------------------------- rule 4: the foot on the road

    /// <summary>A road coming at the valley from the side, its far flat 2 m lower: the far end has 2 m of stairs.
    /// The road has to arrive at their foot along their line, with every step over it. On plain roads, and with the
    /// mod's own shaping (the pull, which skipped the foot, and the sway's resampling, which cut past it, until the
    /// foot was held: RoadSpatialGrid.HeldPoints).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheEndStairsFootLandsOnTheRoadsArrival(bool modShaping)
    {
        if (modShaping) PlainRoads.WithModDefaults(FootLandsOnTheRoad);
        else FootLandsOnTheRoad();
    }

    private void FootLandsOnTheRoad()
    {
        var world = new StepValley { FarDrop = _ => 2f, Depth = _ => 18f };
        _scope.WithWorld(world);
        SetPathfinder(Finder(world));
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        RoadNetworkGenerator.PieceCommitted = (plan, _) => roads.Add(plan);
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-110f, 0f, -50f), 0f, new Vector3(110f, 0f, 50f), 0f, 4f, "side on"));
        RoadCrossing viaduct = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), c => c.ValleyJump);
        var (near, far) = DvergrViaduct.Ends(viaduct, world);
        StructureEnds.End stairs = near.Steps > 0 ? near : far;
        Assert.Equal(2, stairs.Steps);
        Assert.Equal(0, (near.Steps > 0 ? far : near).Steps);
        // (StructureEnds) the road ends at the stairs' foot at its height, having come in along the flight's line
        // over its last ApronLength, levelled to the foot; it does not run on under the flight. Within a metre of the line
        // (a quarter of the road's width): the mod's bend shaping rounds the turn just past the apron.
        Vector2 back = -stairs.Up;
        float ToRoad(Vector2 at) => roads.Min(r => Enumerable.Range(1, r.Points.Count - 1).Select(i => ToSegment(at, r.Points[i - 1], r.Points[i])).DefaultIfEmpty(float.MaxValue).Min());
        Assert.True(ToRoad(stairs.Meet) <= 0.75f, $"the road is {ToRoad(stairs.Meet):F1} m from the stairs' foot {stairs.Meet}");
        for (float s = 0f; s <= StructureEnds.ApronLength; s += 0.5f)
        {
            Vector2 at = stairs.Meet + back * s;
            Assert.True(ToRoad(at) <= 1f, $"{s:F1} m out from the foot the road is {ToRoad(at):F1} m off the flight's line");
        }
        var (plan, k) = roads.SelectMany(r => new[] { (r, 0), (r, r.Points.Count - 1) }).OrderBy(x => Vector2.Distance(x.r.Points[x.Item2], stairs.Meet)).First();
        Assert.True(StructureEnds.Reachable(plan.Heights[k], stairs), $"the road at the foot stands {plan.Heights[k]:F2}, the foot {stairs.MeetHeight:F2}");
        for (int i = 0; i < plan.Points.Count; i++)
            if (Vector2.Distance(plan.Points[i], stairs.Meet) <= StructureEnds.ApronLength)
                Assert.True(Mathf.Abs(plan.Heights[i] - stairs.MeetHeight) <= 0.05f, $"apron at {plan.Points[i]} stands {plan.Heights[i]:F2}, not level at {stairs.MeetHeight:F2}");
        // No road under the flight past its bottom step (the road runs on under the bottom step, the joining piece,
        // StructureEnds.JoinOverlap, levelled at the foot's height, and stops there).
        Vector2 across = new(-stairs.Up.y, stairs.Up.x);
        float flight = stairs.Steps * BridgeLayout.StairRun;
        Assert.True(roads.All(r => r.Points.All(p => Vector2.Dot(p - stairs.Meet, stairs.Up) <= StructureEnds.JoinOverlap + 0.3f
            || Vector2.Dot(p - stairs.Meet, stairs.Up) > flight + 0.5f || Mathf.Abs(Vector2.Dot(p - stairs.Meet, across)) > 3f)), "road under the flight");
        Assert.True(roads.All(r => r.Points.Select((p, i) => (p, h: r.Heights[i])).All(x => !stairs.UnderTheJoint(x.p, 1.5f) || Mathf.Abs(x.h - stairs.MeetHeight) <= 0.05f)), "road under the bottom step not at the foot's height");
        Assert.Null(RoadNetworkGenerator.UnreachableEnd(roads, new List<RoadCrossing> { viaduct }, out _));
    }

    private static float ToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>The guard behind the search: a path come at a viaduct's stair end from the side is put through the
    /// stairs' foot (its point beside the flight dropped); one coming in line is left alone; one ending under the
    /// flight cannot be, and is counted.</summary>
    [Fact]
    public void APathFromTheSideIsPutThroughTheStairsFoot()
    {
        var world = new StepValley { FarDrop = _ => 2f };
        _scope.WithWorld(world);
        RoadCrossing c = Across(world, 0f)!;
        c.FromIndex = 1; c.ToIndex = 2;
        var (near, far) = DvergrViaduct.Ends(c, world);
        Assert.Equal(2, far.Steps);
        Assert.Equal(0, near.Steps);
        // Every end, flush or stepped, is met through its apron, ApronLength out along its line from where the road
        // ends (the stairs' foot, or the deck's edge).
        Vector2 an = near.Apron, af = far.Apron;
        Assert.Equal(StructureEnds.ApronLength + 2f * BridgeLayout.StairRun, Vector2.Distance(af, far.Edge), 3);

        // From the side: square to the deck, 10 m off the far bank. The point beside the flight
        // goes, the apron comes in; the near end, in line, is pinned.
        var side = new List<Vector2> { new(-60f, 0f), c.FromBank, c.ToBank, c.ToBank + new Vector2(0f, 10f), new(80f, 30f) };
        Assert.Equal(1, StructureEnds.MeetTheRoad(side, new[] { c }, world, out int unmet, out int pinned));
        Assert.Equal((0, 1), (unmet, pinned));
        Assert.Equal(new[] { new Vector2(-60f, 0f), an, c.FromBank, c.ToBank, af, new Vector2(80f, 30f) }, side);
        // From behind the apron at an angle: it comes in, nothing goes.
        var angled = new List<Vector2> { new(-60f, 0f), c.FromBank, c.ToBank, new(40f, 14f), new(80f, 30f) };
        Assert.Equal(1, StructureEnds.MeetTheRoad(angled, new[] { c }, world, out unmet, out _));
        Assert.Equal(new[] { new Vector2(-60f, 0f), an, c.FromBank, c.ToBank, af, new Vector2(40f, 14f), new Vector2(80f, 30f) }, angled);

        // In line: nothing turned; both aprons pinned on the line, so a corner further on cannot bow the road off it.
        var inLine = new List<Vector2> { new(-60f, 0f), c.FromBank, c.ToBank, c.ToBank + new Vector2(16f, 0f), new(80f, 30f) };
        Assert.Equal(0, StructureEnds.MeetTheRoad(inLine, new[] { c }, world, out unmet, out pinned));
        Assert.Equal((0, 2), (unmet, pinned));
        Assert.Equal(new[] { new Vector2(-60f, 0f), an, c.FromBank, c.ToBank, af, c.ToBank + new Vector2(16f, 0f), new Vector2(80f, 30f) }, inLine);

        // Ending under the flight: the foot is past the road's end.
        var shortEnd = new List<Vector2> { new(-60f, 0f), c.FromBank, c.ToBank, c.ToBank + new Vector2(1f, 0f) };
        Assert.Equal(0, StructureEnds.MeetTheRoad(shortEnd, new[] { c }, world, out unmet, out _));
        Assert.Equal(1, unmet);
    }

    [Fact]
    public void AtDeckHeightBanksThereAreNoStairsAndTheRoadRunsOntoTheDeck()
    {
        var world = new StepValley { Depth = _ => 18f };
        _scope.WithWorld(world);
        SetPathfinder(Finder(world));
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        RoadNetworkGenerator.PieceCommitted = (plan, _) => roads.Add(plan);
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-110f, 0f, -50f), 0f, new Vector3(110f, 0f, 50f), 0f, 4f, "level"));
        RoadCrossing viaduct = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), c => c.ValleyJump);
        var (near, far) = DvergrViaduct.Ends(viaduct, world);
        Assert.Equal((0, 0), (near.Steps, far.Steps));
        List<StructurePiece> pieces = BridgeStructures.Solve(viaduct, world, world.GetSeed());
        Assert.DoesNotContain(pieces, p => p.Kind == StructurePieceKind.Stair);
        // The road comes to each bank at the deck's height, so it runs onto the deck.
        float deck = DvergrViaduct.DeckHeight(viaduct, world);
        foreach (Vector2 bank in new[] { viaduct.FromBank, viaduct.ToBank })
        {
            var (plan, i) = roads.SelectMany(r => r.Points.Select((p, k) => (r, k))).OrderBy(x => Vector2.Distance(x.r.Points[x.k], bank)).First();
            Assert.True(Vector2.Distance(plan.Points[i], bank) <= 2f, $"the road does not come to the bank at {bank}");
            Assert.True(Mathf.Abs(plan.Heights[i] - deck) <= BridgeLayout.StairRise * 0.5f, $"road at {plan.Heights[i]:F2} against the deck at {deck:F2}");
        }
    }
}
