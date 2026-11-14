using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>Logs and stumps lying across a road (swamp roads), and the Mistlands' ancient armour: turned so
/// the road runs through the armour's opening where a turn does it, else beside them where a turn does it, removed where none
/// does (VegetationClearing.Lie), at generation and on the server alike.</summary>
public class LyingVegetationTests
{
    /// <summary>A 4 m road along x through the origin, a point a metre.</summary>
    private static readonly List<VegetationClearing.RoadCentre> Road =
        Enumerable.Range(-60, 121).Select(x => new VegetationClearing.RoadCentre(new Vector2(x, 0f), 2f)).ToList();

    [Fact]
    public void AnOldLogAcrossTheRoadIsTurnedBesideItByTheLeastTurnThatClearsIt()
    {
        // 5 m off the road's middle, lying north-south (yaw 0): its 7 m length crosses the road.
        Assert.Equal(VegetationClearing.Lying.Turned, VegetationClearing.Lie("FirTree_oldLog", new Vector2(0f, 5f), 0f, 1f, Road, out float turned));
        // Its 7 m by 1.2 m, widened by the road's 2 m half-width and the 0.5 m margin (as a rectangle, its corners square), must
        // hold no road point: the least turn in 5 degree steps that does is 70 degrees.
        Assert.Equal(70f, turned, 3);
        Assert.Equal(VegetationClearing.Lying.Clear, VegetationClearing.Lie("FirTree_oldLog", new Vector2(0f, 5f), turned, 1f, Road, out _));
    }

    [Fact]
    public void ALogOffTheRoadAsLaidIsLeftAsItIs()
    {
        Assert.Equal(VegetationClearing.Lying.Clear, VegetationClearing.Lie("FirTree_oldLog", new Vector2(0f, 7f), 0f, 1.2f, Road, out float turned));
        Assert.Equal(0f, turned);
        Assert.Equal(VegetationClearing.Lying.Clear, VegetationClearing.Lie("SwampTree2_log", new Vector2(0f, 40f), 0f, 2f, Road, out _));
    }

    [Fact]
    public void TheGiantSwampLogIsRemovedWhereNoTurnKeepsItOffTheRoadAndTurnedWhereOneDoes()
    {
        // At scale 2 its branches stand up to 10.8 m to one side: 8 m off the road it lies across it whichever way.
        Assert.Equal(VegetationClearing.Lying.Remove, VegetationClearing.Lie("SwampTree2_log", new Vector2(0f, 8f), 0f, 2f, Road, out _));
        Assert.Equal(VegetationClearing.Lying.Turned, VegetationClearing.Lie("SwampTree2_log", new Vector2(0f, 15f), 0f, 2f, Road, out _));
    }

    [Fact]
    public void OneLyingOnTheRoadItselfIsRemoved()
    {
        Assert.Equal(VegetationClearing.Lying.Remove, VegetationClearing.Lie("stubbe", new Vector2(10f, 0.5f), 90f, 1f, Road, out _));
    }

    [Fact]
    public void AnythingElseIsNotJudgedLying()
    {
        Assert.Equal(VegetationClearing.Lying.NotLying, VegetationClearing.Lie("SwampTree1", new Vector2(0f, 3f), 0f, 1f, Road, out _));
    }

    [Fact]
    public void TheServerTurnsASavedLogAsGenerationWouldAndLeavesAPlayersGroundAlone()
    {
        int log = "FirTree_oldLog".GetStableHashCode();
        var none = new VegetationClearing.ZoneAreas(new List<ZoneSystem.ClearArea>());
        var nobody = new List<VegetationClearing.Footprint>();
        Assert.Equal(VegetationClearing.Lying.Turned, VegetationClearing.Decide(log, new Vector2(0f, 5f), false, new HashSet<int> { log }, none,
            nobody, nobody, Road, 1f, null, 0f, out float turned));
        Assert.Equal(70f, turned, 3);
        Assert.False(VegetationClearing.ShouldRemove(log, new Vector2(0f, 5f), false, new HashSet<int> { log }, none, nobody, nobody, Road, 1f, yaw: 0f));
        var hut = new List<VegetationClearing.Footprint> { new(new Vector2(0f, 6f), VegetationClearing.PlayerGroundRadius) };
        Assert.Equal(VegetationClearing.Lying.Clear, VegetationClearing.Decide(log, new Vector2(0f, 5f), false, new HashSet<int> { log }, none,
            nobody, hut, Road, 1f, null, 0f, out _));
    }

    [Fact]
    public void ALogIsNeverTurnedOntoGroundAStaircaseOrALocationKeepsClear()
    {
        // As the first test, but everything east of the log's middle kept clear (a staircase's clear area, say): every
        // turn that clears the road lays part of it there, so it goes.
        Assert.Equal(VegetationClearing.Lying.Remove, VegetationClearing.Lie("FirTree_oldLog", new Vector2(0f, 5f), 0f, 1f, Road, out float turned,
            p => p.x > 0.01f));
        Assert.Equal(0f, turned);
        // Kept ground the turn does not reach changes nothing.
        Assert.Equal(VegetationClearing.Lying.Turned, VegetationClearing.Lie("FirTree_oldLog", new Vector2(0f, 5f), 0f, 1f, Road, out turned,
            p => p.x > 20f));
        Assert.Equal(70f, turned, 3);
        // The server reads a staircase's clear area the same way.
        int log = "FirTree_oldLog".GetStableHashCode();
        var nobody = new List<VegetationClearing.Footprint>();
        var stairs = new VegetationClearing.ZoneAreas(new List<ZoneSystem.ClearArea> { new(new Vector3(3f, 0f, 7f), 2f) }, 0, 0);
        Assert.Equal(VegetationClearing.Lying.Turned, VegetationClearing.Decide(log, new Vector2(0f, 5f), false, new HashSet<int> { log },
            stairs, nobody, nobody, Road, 1f, null, 0f, out turned));
        Assert.Equal(VegetationClearing.Lying.Clear, VegetationClearing.Lie("FirTree_oldLog", new Vector2(0f, 5f), turned, 1f, Road, out _,
            p => VegetationClearing.InsideClearArea(stairs, new Vector3(p.x, 0f, p.y), log, null)));
    }

    [Fact]
    public void ARoadPointWithinTheMarginOfTheFurthestCornerIsSeen()
    {
        // The giant log at scale 1, lying north (yaw 0): its furthest corner is (x -3.3, z -14.5). A road point a third of a
        // metre past that corner, inside the 0.5 m margin, is on it: the near-road filter must not drop it.
        Vector2 corner = new Vector2(-3.3f, -14.5f), at = corner * ((corner.magnitude + 0.33f) / corner.magnitude);
        var road = new List<VegetationClearing.RoadCentre> { new(at, 0.01f) };
        Assert.NotEqual(VegetationClearing.Lying.Clear, VegetationClearing.Lie("SwampTree2_log", new Vector2(0f, 0f), 0f, 1f, road, out _));
    }

    [Fact]
    public void APreviewOrACountNotesNothing()
    {
        int log = "FirTree_oldLog".GetStableHashCode();
        var none = new VegetationClearing.ZoneAreas(new List<ZoneSystem.ClearArea>());
        var nobody = new List<VegetationClearing.Footprint>();
        int turned = VegetationClearing.LyingTurned, noted = VegetationClearing.LyingNoted.Count;
        VegetationClearing.ShouldRemove(log, new Vector2(0f, 5f), false, new HashSet<int> { log }, none, nobody, nobody, Road, 1f, yaw: 0f);
        VegetationClearing.Decide(log, new Vector2(0f, 5f), false, new HashSet<int> { log }, none, nobody, nobody, Road, 1f, null, 0f, out _);
        Assert.Equal(turned, VegetationClearing.LyingTurned);
        Assert.Equal(noted, VegetationClearing.LyingNoted.Count);
    }

    [Fact]
    public void AMistlandsRootAcrossTheRoadIsTurnedBesideItWhereATurnClearsIt()
    {
        // 8 m off the road's middle at yaw 90 its 28 m length runs north-south, across the road.
        Assert.Equal(VegetationClearing.Lying.Turned, VegetationClearing.Lie("YggdrasilRoot", new Vector2(0f, 8f), 90f, 1f, Road, out float turned));
        Assert.Equal(VegetationClearing.Lying.Clear, VegetationClearing.Lie("YggdrasilRoot", new Vector2(0f, 8f), turned, 1f, Road, out _));
        // Generation turns it the same way, by the yaw PlaceVegetation drew.
        float yaw = 90f;
        Assert.False(RoadClearAreaManager.OnRoad(new VegetationClearing.ZoneAreas(new List<ZoneSystem.ClearArea>(), 0, int.MaxValue), Road, new Vector3(0f, 40f, 8f), "YggdrasilRoot",
            1f, _ => Heightmap.Biome.Mistlands, ref yaw));
        Assert.Equal(turned, yaw, 3);
    }

    [Fact]
    public void AMistlandsRootIsRemovedWhereNoTurnClearsTheRoadAndLeftWhereItIsClear()
    {
        // 3 m off the road's middle its trunk, near 2 m thick each side of its origin, lies on the road whichever way.
        Assert.Equal(VegetationClearing.Lying.Remove, VegetationClearing.Lie("YggdrasilRoot", new Vector2(0f, 3f), 90f, 1f, Road, out _));
        float yaw = 90f;
        Assert.True(RoadClearAreaManager.OnRoad(new VegetationClearing.ZoneAreas(new List<ZoneSystem.ClearArea>(), 0, int.MaxValue), Road, new Vector3(0f, 40f, 3f), "YggdrasilRoot",
            1f, _ => Heightmap.Biome.Mistlands, ref yaw));
        // 30 m off, pointing at the road at its largest scale, its shorter end (18.8 m at scale 1, 28.2 m at 1.5) dives
        // into the ground: on a road level with the ground at its origin that end lies 11 m and more under the road, and it
        // is left as it lies.
        Assert.Equal(VegetationClearing.Lying.Clear, VegetationClearing.Lie("YggdrasilRoot", new Vector2(0f, 30f), 90f, 1.5f, RoadAt(), out float turned,
            lean: new VegetationClearing.Lean(0f, 0f, 40f)));
        Assert.Equal(90f, turned);
        // Where the heights are not known, its whole footprint counts: that end, widened by the road's half-width and the
        // margin, reaches 30.7 m, onto the road 30 m off, and stops short of it 31 m off.
        Assert.NotEqual(VegetationClearing.Lying.Clear, VegetationClearing.Lie("YggdrasilRoot", new Vector2(0f, 30f), 90f, 1.5f, Road, out _));
        Assert.Equal(VegetationClearing.Lying.Clear, VegetationClearing.Lie("YggdrasilRoot", new Vector2(0f, 31f), 90f, 1.5f, Road, out turned));
        Assert.Equal(90f, turned);
    }

    [Fact]
    public void TheServerTurnsOrRemovesASavedMistlandsRootAsGenerationWould()
    {
        int root = "YggdrasilRoot".GetStableHashCode();
        var none = new VegetationClearing.ZoneAreas(new List<ZoneSystem.ClearArea>());
        var nobody = new List<VegetationClearing.Footprint>();
        VegetationClearing.Lie("YggdrasilRoot", new Vector2(0f, 8f), 90f, 1f, Road, out float generated);
        Assert.Equal(VegetationClearing.Lying.Turned, VegetationClearing.Decide(root, new Vector2(0f, 8f), false, new HashSet<int> { root }, none,
            nobody, nobody, Road, 1f, null, 90f, out float turned));
        Assert.Equal(generated, turned, 3);
        Assert.Equal(VegetationClearing.Lying.Remove, VegetationClearing.Decide(root, new Vector2(0f, 3f), false, new HashSet<int> { root }, none,
            nobody, nobody, Road, 1f, null, 90f, out _));
        Assert.Equal(VegetationClearing.Lying.Clear, VegetationClearing.Decide(root, new Vector2(0f, 30f), false, new HashSet<int> { root }, none,
            nobody, nobody, RoadAt(), 1.5f, null, 90f, out _, new VegetationClearing.Lean(0f, 0f, 40f)));
        Assert.Equal(VegetationClearing.Lying.Clear, VegetationClearing.Decide(root, new Vector2(0f, 31f), false, new HashSet<int> { root }, none,
            nobody, nobody, Road, 1.5f, null, 90f, out _));
    }

    // An ancient root across a road below a marble staircase in the Mistlands, as read in game: its origin, yaw and scale,
    // and the road points near where its far end crosses the road (half-width 2), each with the road's height.
    private static readonly Vector2 RootAt = new(4329.777f, -5733.032f);
    private const float RootYaw = 47f, RootScale = 1.2865f, RootOrigin = 78.946f;
    private static readonly (Vector2 at, float h)[] RootRoad =
    {
        (new Vector2(4306.3f, -5716.6f), 70.63f), (new Vector2(4309.4f, -5715.2f), 70.76f), (new Vector2(4310.5f, -5714.8f), 70.79f),
        (new Vector2(4311.6f, -5714.3f), 70.85f), (new Vector2(4312.7f, -5713.8f), 70.90f), (new Vector2(4313.8f, -5713.4f), 70.89f),
        (new Vector2(4318.4f, -5711.5f), 71.23f),
    };

    /// <summary><see cref="RootRoad"/>, each point at the height <paramref name="height"/> gives it, moved
    /// <paramref name="towards"/> metres along the root's length towards its origin.</summary>
    private static List<VegetationClearing.RoadCentre> RootRoadAt(System.Func<float, float> height, float towards = 0f)
    {
        float r = RootYaw * Mathf.Deg2Rad;
        // The root's length is its local x, along (cos, -sin) at its yaw; the road crosses it on the negative side.
        Vector2 length = new(Mathf.Cos(r), -Mathf.Sin(r));
        return RootRoad.Select(p => new VegetationClearing.RoadCentre(p.at + length * towards, 2f, height(p.h))).ToList();
    }

    /// <summary>The root judged on <paramref name="road"/> by generation (RoadClearAreaManager.OnRoad, with the tilt and origin
    /// PlaceVegetation draws) and by the server bake (VegetationClearing.Decide, with the saved one's), which must agree: the
    /// verdict and the yaw it is turned to.</summary>
    private static (VegetationClearing.Lying verdict, float turned) RootOnBothPaths(List<VegetationClearing.RoadCentre> road)
    {
        var laid = new VegetationClearing.Lean(0f, 0f, RootOrigin);
        VegetationClearing.Lying lying = VegetationClearing.Lie("YggdrasilRoot", RootAt, RootYaw, RootScale, road, out float turned, lean: laid);
        int root = "YggdrasilRoot".GetStableHashCode();
        var nobody = new List<VegetationClearing.Footprint>();
        Assert.Equal(lying, VegetationClearing.Decide(root, RootAt, false, new HashSet<int> { root }, new VegetationClearing.ZoneAreas(new List<ZoneSystem.ClearArea>()),
            nobody, nobody, road, RootScale, null, RootYaw, out float baked, laid));
        Assert.Equal(turned, baked, 3);
        float yaw = RootYaw;
        Assert.Equal(lying == VegetationClearing.Lying.Remove, RoadClearAreaManager.OnRoad(new VegetationClearing.ZoneAreas(new List<ZoneSystem.ClearArea>(), 0, int.MaxValue),
            road, new Vector3(RootAt.x, RootOrigin, RootAt.y), "YggdrasilRoot", RootScale, _ => Heightmap.Biome.Mistlands, ref yaw, laid));
        if (lying != VegetationClearing.Lying.Remove) Assert.Equal(turned, yaw, 3);
        return (lying, turned);
    }

    [Fact]
    public void AnAncientRootsEndStandingOnTheRoadDownASlopeIsTurnedOrRemoved()
    {
        // The ground falls 8 m from the root's origin to the road, so the end that dives into the ground at its origin
        // stands on the road 26 m away: it is in the road's walking box, and the root is turned or removed, on both paths.
        var (verdict, _) = RootOnBothPaths(RootRoadAt(h => h));
        Assert.NotEqual(VegetationClearing.Lying.Clear, verdict);
    }

    [Fact]
    public void AnAncientRootsBuriedEndUnderALevelRoadIsLeftAsItLies()
    {
        // The same road at the height of the ground at the root's origin: its end lies 4 m and more under the road there.
        var (verdict, turned) = RootOnBothPaths(RootRoadAt(_ => RootOrigin));
        Assert.Equal(VegetationClearing.Lying.Clear, verdict);
        Assert.Equal(RootYaw, turned);
    }

    [Fact]
    public void ARoadCutThatUncoversAnAncientRootsEndSeesIt()
    {
        // The road 2.6 m nearer the root's origin, past the end the old footprint kept: level with the ground at its origin,
        // the end lies under it; cut 3 m into that ground (within the earthwork cap), the end stands in the cut.
        Assert.Equal(VegetationClearing.Lying.Clear, RootOnBothPaths(RootRoadAt(_ => RootOrigin, 2.6f)).verdict);
        Assert.NotEqual(VegetationClearing.Lying.Clear, RootOnBothPaths(RootRoadAt(_ => RootOrigin - 3f, 2.6f)).verdict);
    }

    /// <summary><see cref="Road"/> with its height: 40 m, or <paramref name="height"/>.</summary>
    private static List<VegetationClearing.RoadCentre> RoadAt(float height = 40f) =>
        Road.Select(point => new VegetationClearing.RoadCentre(point.Centre, point.HalfWidth, height)).ToList();

    /// <summary>The first helmet tilted back 25.5 degrees and 4.5 about z (as found in game), its origin on ground at 40 m:
    /// at that tilt a player walks straight through it along phi 136, 1 to 2.3 m to one side of its origin.</summary>
    private static readonly VegetationClearing.Lean TiltedBack = new(-25.5f, -4.5f, 40f);

    [Fact]
    public void AncientArmourAcrossTheRoadIsTurnedSoTheRoadRunsThroughItsOpening()
    {
        // 1.6 m off the road's middle it lies across the road whichever way it is turned; at its tilt the opening is 1 to
        // 2.3 m to one side of its origin, so a turn lines the opening up with the road.
        var road = RoadAt();
        Assert.Equal(VegetationClearing.Lying.Through, VegetationClearing.Lie("giant_helmet1", new Vector2(0f, 1.6f), 0f, 1f, road, out float turned,
            lean: TiltedBack));
        // The opening runs along the road (x) at the turn, within the few degrees its width allows.
        Assert.InRange(((turned + 136f) % 180f + 180f) % 180f, 85f, 95f);
        // Judged again at that yaw, it stays as it is.
        Assert.Equal(VegetationClearing.Lying.Through, VegetationClearing.Lie("giant_helmet1", new Vector2(0f, 1.6f), turned, 1f, road, out float again,
            lean: TiltedBack));
        Assert.Equal(turned, again);
        // The server reads the tilt from a saved rotation's Euler angles (0 to 360) and turns it the same way.
        int helmet = "giant_helmet1".GetStableHashCode();
        var none = new VegetationClearing.ZoneAreas(new List<ZoneSystem.ClearArea>());
        var nobody = new List<VegetationClearing.Footprint>();
        Assert.Equal(VegetationClearing.Lying.Through, VegetationClearing.Decide(helmet, new Vector2(0f, 1.6f), false, new HashSet<int> { helmet }, none,
            nobody, nobody, road, 1f, null, 0f, out float saved, new VegetationClearing.Lean(334.5f, 355.5f, 40f)));
        Assert.Equal(turned, saved, 3);
        Assert.False(VegetationClearing.ShouldRemove(helmet, new Vector2(0f, 1.6f), false, new HashSet<int> { helmet }, none, nobody, nobody, road, 1f,
            yaw: 0f, lean: TiltedBack));
        // Generation turns it the same way, by the yaw and tilts PlaceVegetation drew.
        float yaw = 0f;
        Assert.False(RoadClearAreaManager.OnRoad(new VegetationClearing.ZoneAreas(new List<ZoneSystem.ClearArea>(), 0, int.MaxValue), road, new Vector3(0f, 40f, 1.6f), "giant_helmet1",
            1f, _ => Heightmap.Biome.Mistlands, ref yaw, TiltedBack));
        Assert.Equal(turned, yaw, 3);
    }

    [Fact]
    public void AncientArmourIsTurnedBesideTheRoadWhereNoTurnRunsTheRoadThroughIt()
    {
        // The second helmet level (no opening at that tilt), 8.5 m off the road's middle and turned so its 6.6 m side lies
        // across the road: a turn lays it beside the road, its length along it.
        var road = RoadAt();
        Assert.Equal(VegetationClearing.Lying.Turned, VegetationClearing.Lie("giant_helmet2", new Vector2(0f, 8.5f), 90f, 1f, road, out float turned,
            lean: new VegetationClearing.Lean(0f, 0f, 39f)));
        Assert.Equal(VegetationClearing.Lying.Clear, VegetationClearing.Lie("giant_helmet2", new Vector2(0f, 8.5f), turned, 1f, road, out _,
            lean: new VegetationClearing.Lean(0f, 0f, 39f)));
    }

    [Fact]
    public void AncientArmourIsRemovedWhereTheRoadNeitherRunsThroughItNorPassesBeside()
    {
        // As the first armour case, but the road a metre above the ground at the helmet's origin: higher than its opening
        // was measured for, so it does not run through, and 1.6 m off the road no turn lays it beside.
        Assert.Equal(VegetationClearing.Lying.Remove, VegetationClearing.Lie("giant_helmet1", new Vector2(0f, 1.6f), 0f, 1f, RoadAt(41f), out _,
            lean: TiltedBack));
        // Nor where its tilt has no opening, or is not known, or the road's height is not.
        Assert.Equal(VegetationClearing.Lying.Remove, VegetationClearing.Lie("giant_helmet1", new Vector2(0f, 1.6f), 0f, 1f, RoadAt(), out _,
            lean: new VegetationClearing.Lean(0f, 0f, 40f)));
        Assert.Equal(VegetationClearing.Lying.Remove, VegetationClearing.Lie("giant_helmet1", new Vector2(0f, 1.6f), 0f, 1f, RoadAt(), out _));
        Assert.Equal(VegetationClearing.Lying.Remove, VegetationClearing.Lie("giant_helmet1", new Vector2(0f, 1.6f), 0f, 1f, Road, out _,
            lean: TiltedBack));
    }

    [Fact]
    public void AncientArmourWellOffTheRoadIsLeftAsItLies()
    {
        // The rib cage reaches 13 m from its origin: 30 m off the road nothing of it is near, whichever way it lies.
        int ribs = "giant_ribs".GetStableHashCode();
        Assert.Equal(VegetationClearing.Lying.Clear, VegetationClearing.Lie("giant_ribs", new Vector2(0f, 30f), 0f, 1f, RoadAt(), out float turned,
            lean: new VegetationClearing.Lean(0f, 0f, 40f)));
        Assert.Equal(0f, turned);
        Assert.False(VegetationClearing.ShouldRemove(ribs, new Vector2(0f, 30f), false, new HashSet<int> { ribs }, new VegetationClearing.ZoneAreas(new List<ZoneSystem.ClearArea>()),
            new List<VegetationClearing.Footprint>(), new List<VegetationClearing.Footprint>(), RoadAt(), 1f, yaw: 90f));
    }

#if NET
    [Fact]
    public void ACandidateThatDoesNotLieAlongTheGroundCostsNoAllocation()
    {
        // Generation asks this of every vegetation candidate in a zone: the kept-ground test a turn needs is built only for
        // one that lies along the ground. The server's Decide likewise.
        var areas = new List<ZoneSystem.ClearArea>();
        int tree = "SwampTree1".GetStableHashCode();
        var trees = new HashSet<int> { tree };
        var none = new VegetationClearing.ZoneAreas(new List<ZoneSystem.ClearArea>());
        var nobody = new List<VegetationClearing.Footprint>();
        System.Func<Vector3, Heightmap.Biome> biome = _ => Heightmap.Biome.Swamp;
        void Ask()
        {
            float yaw = 0f;
            RoadClearAreaManager.OnRoad(new VegetationClearing.ZoneAreas(areas, 0, int.MaxValue), Road, new Vector3(0f, 40f, 30f), "SwampTree1", 1f, biome, ref yaw);
            VegetationClearing.Decide(tree, new Vector2(0f, 30f), false, trees, none, nobody, nobody, Road, 1f, null, 0f, out _);
        }
        Ask();
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Ask();
        Assert.Equal(0L, System.GC.GetAllocatedBytesForCurrentThread() - before);
    }
#endif

    [Fact]
    public void ZonesGatherTheRoadAsFarAsTheLongestLyingOneReaches()
    {
        // The Mistlands root at scale 1.5 reaches 37.8 m from its origin, its whole length counted (its ends dive into the
        // ground, and stand on a road down a slope or in a cut), past the giant log's 29.7 m at scale 2 and a standing
        // tree's 15.5 m.
        Assert.True(VegetationClearing.TreeReach >= VegetationClearing.LyingReach);
        Assert.InRange(VegetationClearing.LyingReach, 38f, 39f);
    }
}
