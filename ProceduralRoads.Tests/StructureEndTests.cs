using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The one owner of how a structure's end meets the road (StructureEnds): one owner for how blocks/stairs and roads meet
/// each other, so they are consistent. Four viaduct ends a
/// player could not walk onto, reproduced as valleys whose far bank point stands off the road that comes to it; a
/// good end stays as it was; and a bank seam.
/// <para>Written against what any version has (DvergrViaduct.Ends: the deck's end edge and
/// the stairs' foot; the road as stored).</para>
/// </summary>
public sealed class StructureEndTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>A Mistlands valley along z: the near (west) flat at <see cref="Top"/>, a floor <see cref="Depth"/> under
    /// it, faces of 1.2; past the far (east) rim the ground is <see cref="Far"/> of the metres beyond the rim. Sea past
    /// |x|, |z| = 200.</summary>
    internal sealed class RimValley : WorldGenerator
    {
        public float Top = 60f, Depth = 18f, FloorHalf = 8f, Face = 1.2f;
        public Func<float, float> Far = _ => 0f;
        public float Rim => FloorHalf + Depth / Face;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float floor = Top - Depth;
            float valley = Mathf.Min(Top, floor + Mathf.Max(0f, Mathf.Abs(wx) - FloorHalf) * Face);
            if (wx <= Rim) return valley;
            return Top + Far(wx - Rim);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 78;
    }

    public StructureEndTests()
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

    /// <summary>The road across <paramref name="world"/> from west to east (z from <paramref name="z0"/> to
    /// <paramref name="z1"/>), and its one viaduct.</summary>
    private RoadCrossing Build(WorldGenerator world, List<RoadSpatialGrid.PlannedPath> roads, float z0 = 0f, float z1 = 0f)
    {
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, Viaducts = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        RoadNetworkGenerator.PieceCommitted = (plan, _) => roads.Add(plan);
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-110f, 0f, z0), 0f, new Vector3(110f, 0f, z1), 0f, 4f, "across"), "no road");
        return Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), c => c.ValleyJump);
    }

    /// <summary>The road's own height where it meets each end of <paramref name="viaduct"/> (the stairs' foot, or the deck's
    /// edge), against the end's walking height there: a player walks on within a step.</summary>
    private static void AssertEveryEndReachable(RoadCrossing viaduct, WorldGenerator world, List<RoadSpatialGrid.PlannedPath> roads, string site)
    {
        float deck = DvergrViaduct.DeckHeight(viaduct, world);
        var (near, far) = DvergrViaduct.Ends(viaduct, world);
        foreach (var (end, which) in new[] { (near, "near"), (far, "far") })
        {
            float walk = deck - end.Steps * BridgeLayout.StairRise;
            // The stored road nearest the end's meeting point.
            var points = roads.SelectMany(r => r.Points.Select((p, i) => (p, h: r.Heights[i]))).OrderBy(x => Vector2.Distance(x.p, end.Meet)).ToList();
            Assert.True(points.Count > 0 && Vector2.Distance(points[0].p, end.Meet) <= 1f, $"{site}, {which} end: no road at its foot {end.Meet}");
            float road = points[0].h;
            Assert.True(Mathf.Abs(road - walk) <= 0.5f,
                $"{site}, {which} end: the road meets it at {road:F2} m, its {(end.Steps == 0 ? "deck" : $"{end.Steps} stairs' foot")} at {walk:F2} m: a player cannot walk on");
        }
    }

    /// <summary>The 80 m span's east end at (6986.4, 50.8, -883.6) on a test world: a player can't walk up from the road to the top of
    /// the deck. The rim is a lip: 45.0 m at the span's recorded bank point but 51.4 m eight metres on, where the road comes.
    /// Here the far bank point stands 2 m under the near flat and the ground climbs 0.9 a metre past it to 4.6 m over it.</summary>
    [Fact]
    public void AStepRimRisingPastTheBankPoint()
    {
        var world = new RimValley { Far = d => -2f + Mathf.Min(d, 7.3f) * 0.9f };
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        AssertEveryEndReachable(Build(world, roads), world, roads, "east");
    }

    /// <summary>A 64 m N-S span's end that can't even be reached: a deck end block about 2 m
    /// over the road, no stairs. The far bank holds the deck's height for 3 m past the rim, then the road's ground lies 2 m
    /// lower.</summary>
    [Fact]
    public void ADeckEndTwoMetresOverTheRoad()
    {
        var world = new RimValley { Far = d => d < 3f ? 0f : -2f };
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        AssertEveryEndReachable(Build(world, roads), world, roads, "N-S");
    }

    /// <summary>A 57 m span's north-west end, with another large step up that can't be walked over: the bank point at the deck's height, the ground falling away past it, 0.25 a metre to 1.5 m under it,
    /// where the road comes.</summary>
    [Fact]
    public void TheBankFallingAwayPastItsPoint()
    {
        var world = new RimValley { Far = d => -0.25f * Mathf.Min(d, 6f) };
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        AssertEveryEndReachable(Build(world, roads), world, roads, "far");
    }

    /// <summary>A 56 m span at (-5476.7, 82.1, 5717.3) with a similar deck height problem: the road climbs between
    /// rocks to a deck end about one marble block and the course, 2-2.5 m, over it. A 1.5 m lip at the deck's height at the
    /// rim, the road's ground 2.5 m under it.</summary>
    [Fact]
    public void ALipAtTheDecksHeightTheRoadTwoAndAHalfMetresUnder()
    {
        var world = new RimValley { Far = d => d < 1.5f ? 0f : -2.5f };
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        AssertEveryEndReachable(Build(world, roads), world, roads, "lip");
    }

    /// <summary>A 57 m span's south-east end, which was fine: level banks at the deck's height. It stays as it was: the deck at the
    /// flats' height, no stairs, the road onto it level.</summary>
    [Fact]
    public void AGoodEndStaysAsItWas()
    {
        var world = new RimValley();
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        RoadCrossing viaduct = Build(world, roads);
        Assert.Equal(world.Top, DvergrViaduct.DeckHeight(viaduct, world), 3);
        var (near, far) = DvergrViaduct.Ends(viaduct, world);
        Assert.Equal((0, 0), (near.Steps, far.Steps));
        Assert.DoesNotContain(BridgeStructures.Solve(viaduct, world, 1), p => p.Kind == StructurePieceKind.Stair);
        AssertEveryEndReachable(viaduct, world, roads, "good end");
    }

    /// <summary>A 66 m river viaduct's north-west bank (2187, -6265), where the road/bridge boundary needed a
    /// smoother transition, such as extending the earthwork slightly: the road came in high and to one side of the deck
    /// end. Its last 6-8 m is levelled to the deck and centred on its line. Here the road comes at the valley on a slant
    /// (z -40 to 40) over a gently rising near flat.</summary>
    [Fact]
    public void TheRoadsLastMetresAreLevelWithTheDeckAndOnItsLine()
    {
        var world = new RimValley { Far = _ => 0f };
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        RoadCrossing viaduct = Build(world, roads, -40f, 40f);
        float deck = DvergrViaduct.DeckHeight(viaduct, world);
        var (near, far) = DvergrViaduct.Ends(viaduct, world);
        foreach (var end in new[] { near, far })
        {
            float walk = deck - end.Steps * BridgeLayout.StairRise;
            Vector2 line = -end.Up, across = new(-line.y, line.x);
            int seen = 0;
            foreach (var r in roads)
                for (int i = 0; i < r.Points.Count; i++)
                {
                    float along = Vector2.Dot(r.Points[i] - end.Meet, line);
                    if (along < -0.25f || along > 6f || Vector2.Distance(r.Points[i], end.Meet) > 8f) continue;
                    seen++;
                    Assert.True(Mathf.Abs(r.Heights[i] - walk) <= 0.1f, $"{along:F1} m out from the end at {end.Meet} the road stands {r.Heights[i]:F2}, the end {walk:F2}");
                    Assert.True(Mathf.Abs(Vector2.Dot(r.Points[i] - end.Meet, across)) <= 0.5f, $"{along:F1} m out from the end the road is {Vector2.Dot(r.Points[i] - end.Meet, across):F2} m off its line");
                }
            Assert.True(seen >= 6, $"only {seen} road point(s) in the last 6 m before the end at {end.Meet}");
        }
    }

    // ------------------------------------------------- the final check

    private static RoadSpatialGrid.PlannedPath Piece(Vector2 a, Vector2 b, float h) =>
        new(new List<Vector2> { a, b }, new List<float> { h, h }, new List<RoadPointDebugInfo>(), 4f, Vector2.Distance(a, b), false);

    /// <summary>(d): an end is met when a piece of the road ends at its meeting point within a step of its height. Not met
    /// 2 m under it, nor where no piece ends there.</summary>
    [Fact]
    public void AnEndIsMetOnlyByRoadAtItsHeight()
    {
        var world = new RimValley { Far = d => d < 3f ? 0f : -2f };
        _scope.WithWorld(world);
        RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), world, out string why)!;
        Assert.True(c != null, why);
        var (near, far) = DvergrViaduct.Ends(c!, world);
        Assert.Equal(0, near.Steps);
        Assert.True(far.Steps > 0, "the far end has stairs down to the road's lower ground");
        var crossings = new List<RoadCrossing> { c! };
        var nearRoad = Piece(near.Apron, near.Meet, near.MeetHeight);
        Assert.Null(RoadNetworkGenerator.UnreachableEnd(new List<RoadSpatialGrid.PlannedPath> { nearRoad, Piece(far.Meet, far.Apron, far.MeetHeight) }, crossings, out _));
        Assert.Same(c, RoadNetworkGenerator.UnreachableEnd(new List<RoadSpatialGrid.PlannedPath> { nearRoad, Piece(far.Meet, far.Apron, far.MeetHeight - 2f) }, crossings, out Vector2 at));
        Assert.Equal(far.Meet, at);
        Assert.Same(c, RoadNetworkGenerator.UnreachableEnd(new List<RoadSpatialGrid.PlannedPath> { nearRoad }, crossings, out _));
    }

    /// <summary>(d): a valley viaduct whose end the road does not meet is not built, and the road is searched again without
    /// it: the search never buys a jump the road was refused with.</summary>
    [Fact]
    public void AViaductTheRoadWasRefusedWithIsNotBoughtAgain()
    {
        var world = new RimValley();
        _scope.WithWorld(world);
        var finder = new RoadPathfinder(world) { Stairs = true, Viaducts = true, TerrainVarianceThreshold = 1000f, Meander = 0f };
        Assert.NotNull(finder.FindPath(new Vector2(-110f, 0f), new Vector2(110f, 0f)));
        var (from, to) = Assert.Single(finder.LastViaductJumps);
        finder.BanViaduct(from, to);
        Assert.NotNull(finder.FindPath(new Vector2(-110f, 0f), new Vector2(110f, 0f)));
        Assert.DoesNotContain(finder.LastViaductJumps, j => (Vector2.Distance(j.from, from) < 1f && Vector2.Distance(j.to, to) < 1f) || (Vector2.Distance(j.from, to) < 1f && Vector2.Distance(j.to, from) < 1f));
        finder.ClearNoStairs();
        Assert.NotNull(finder.FindPath(new Vector2(-110f, 0f), new Vector2(110f, 0f)));
        Assert.Single(finder.LastViaductJumps);
    }
}
