using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A narrow crossing over deep water spans bank to bank with nothing in the
/// channel.
///
/// The limit is the horizontal half of the rule that gave the pier ceiling.
/// Wood loses 0.2 of its support per metre sideways against 0.125 downward, so
/// a 2 m member keeps 58% of its neighbour and a cantilever dies 8 m out; a
/// deck anchored at BOTH ends takes the average of two support points more
/// than 100 degrees apart, which doubles the reach. 16 m holds with its
/// weakest piece at 11.3 against a minimum of 10; 20 m falls.
///
/// This does NOT try to say what a player can build: players get better
/// materials that span further, and can fill a gap themselves. 16 m is a
/// ceiling on what this generator puts down, nothing more.
/// </summary>
public class BridgeFreeSpanTests
{
    /// <summary>The number in the constant is not a preference. Walk the
    /// decompiled rule out and it lands there: 16 m holds, 20 m does not.</summary>
    [Fact]
    public void SixteenMetresIsWhatTheSupportRuleActuallyReaches()
    {
        Assert.True(WeakestPieceOfAFreeSpan(16f) >= BridgeSupport.MinSupport,
            $"a 16 m span should hold; weakest piece {WeakestPieceOfAFreeSpan(16f):F2}");
        Assert.True(WeakestPieceOfAFreeSpan(20f) < BridgeSupport.MinSupport,
            $"a 20 m span should fall; weakest piece {WeakestPieceOfAFreeSpan(20f):F2}");
    }

    /// <summary>The support a deck's weakest member settles at when the span
    /// is carried from two abutments, iterating UpdateSupport's rule: each
    /// piece takes the best of one neighbour's offer and the AVERAGE of two
    /// opposed ones.</summary>
    private static float WeakestPieceOfAFreeSpan(float spanMetres)
    {
        float step = 1f - BridgeSupport.HorizontalLoss
                        * (BridgeLayout.DeckSpan + BridgeSupport.ComDistanceMargin);
        int n = Mathf.RoundToInt(spanMetres / BridgeLayout.DeckSpan);
        float[] v = new float[n];
        for (int pass = 0; pass < 400; pass++)
        {
            float[] next = new float[n];
            for (int i = 0; i < n; i++)
            {
                float left = i == 0 ? BridgeSupport.MaxSupport : v[i - 1];
                float right = i == n - 1 ? BridgeSupport.MaxSupport : v[i + 1];
                float one = Mathf.Max(left * step, right * step);
                float both = (left * step + right * step) * 0.5f;
                next[i] = Mathf.Min(BridgeSupport.MaxSupport, Mathf.Max(one, both));
            }
            v = next;
        }
        return v.Min();
    }

    /// <summary>The widest gap left for a longship (Ships.NavigationGapWidth, at any
    /// deck height) is one the kit can span: a wider one would invite a repair that falls down.</summary>
    [Fact]
    public void TheNavigationGapIsNoWiderThanTheKitCanSpan()
    {
        float widest = Enumerable.Range(-10, 140).Select(i => Ships.NavigationGapWidth(i * 0.1f, i * 0.1f + 0.5f).piers).Max();
        Assert.True(widest <= BridgeLayout.FreeSpanMaxWidth,
            $"a {widest:F1} m gap in a kit that spans "
            + $"{BridgeLayout.FreeSpanMaxWidth:F0} m is a hole nothing we build can close");
    }

    private sealed class NarrowDeepChannel : WorldGenerator
    {
        public override float GetHeight(float x, float z) => Mathf.Abs(x) < 7f ? 6f : 31f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        {
            weight = Mathf.Abs(x) < 7f ? 1f : 0f;
            width = 14f;
        }
    }

    private static (RoadCrossing crossing, WorldGenerator world) Narrow()
    {
        var world = new NarrowDeepChannel();
        var path = new System.Collections.Generic.List<Vector2>
        {
            new(-24f, 0f), new(-16f, 0f), new(-8f, 0f),
            new(8f, 0f), new(16f, 0f), new(24f, 0f)
        };
        var c = Assert.Single(RoadCrossingDetector.Detect(path, world, bridges: true, fords: false));
        return (c, world);
    }

    /// <summary>The whole point: a deep narrow channel gets a bridge with no
    /// posts standing in it.</summary>
    [Fact]
    public void ADeepNarrowChannelIsSpannedWithNothingInTheWater()
    {
        var (crossing, world) = Narrow();
        Assert.Equal(CrossingKind.Bridge, crossing.Kind);
        Assert.True(crossing.Width <= BridgeLayout.FreeSpanMaxWidth,
            $"fixture drifted: {crossing.Width:F1} m is not a free span");
        Assert.True(BridgeLayout.PierHeight(crossing, world) > RoadConstants.MaxBridgePierHeight,
            $"fixture drifted: pier {BridgeLayout.PierHeight(crossing, world):F1} m would stand, "
            + "so this is not the can't-build-a-pier case the rule is for");

        var pieces = BridgeStructures.Solve(crossing, world, 1);
        var postsInWater = pieces
            .Where(p => p.Kind == StructurePieceKind.Post && p.Prefab == BridgeLayout.PostPrefab)
            .Where(p => Mathf.Abs(p.Position.x) < 7f)
            .ToList();
        Assert.Empty(postsInWater);
        Assert.Contains(pieces, p => p.Kind == StructurePieceKind.Deck);
    }

    /// <summary>And it is a continuous deck, not two stubs: removing the
    /// middle stations must not remove the plates that cross them.</summary>
    [Fact]
    public void TheSpanStillCarriesADeckRightAcross()
    {
        var (crossing, world) = Narrow();
        var deck = BridgeStructures.Solve(crossing, world, 1)
            .Where(p => p.Kind == StructurePieceKind.Deck)
            .Select(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), crossing.FromBank))
            .ToList();
        Assert.NotEmpty(deck);
        Assert.True(deck.Max() >= crossing.Width - BridgeLayout.DeckSpan * 1.5f,
            $"deck reaches {deck.Max():F1} m of a {crossing.Width:F1} m crossing; a free span "
            + "whose plates stop at the bank stations is two stubs, not a bridge");
    }

    /// <summary>
    /// The second way a channel refuses a pier, and the commoner one: the boat
    /// lane covers the whole crossing, so there is nowhere a pier is allowed
    /// to stand.
    ///
    /// Before this rule such a crossing emitted a bridge with <b>no deck and
    /// no piers</b> - two stair runs with open water between them - because
    /// every station fell inside the navigation gap and a plate needs both of
    /// its stations standing. Silent, and it produced a road that walks into a
    /// river.
    ///
    /// Built directly rather than detected: the detector floors a bridge at
    /// 16 m (two cells) whatever the channel, and the fairway it measures off
    /// a synthetic river never quite fills that. The predicate is the thing
    /// under test, so it is the thing called.
    /// </summary>
    [Theory]
    // width, fairway, river, expected: over a river under a low deck the gap is Ships.NavigationGapWidth (9.8 m) + 2 m clearance
    [InlineData(11f, 11f, true, true)]     // 11.8 m covers all 11 m
    [InlineData(14f, 14f, true, false)]    // but not 14 m: piers stand either side of the gap
    [InlineData(16f, 16f, false, false)]   // as deep, but a lake: no gap, an ordinary piered bridge
    [InlineData(16f, 0f, false, false)]    // not sailable at all: ordinary piered bridge
    public void ACrossingThatIsAllBoatLaneIsSpannedRatherThanLeftEmpty(float width, float fairway, bool river, bool expected)
    {
        var world = new ShallowFlat { River = river };
        Vector2 a = new(-width * 0.5f, 0f), b = new(width * 0.5f, 0f);
        var crossing = RoadCrossing.Between(a, b, riverbedHeight: 26f, fairwayCenter: new Vector2(0f, 0f),
            fairwayWidth: fairway, CrossingKind.Bridge, FordStyle.None);

        // Must not ALSO be the pier-cannot-stand case, or it tests the other branch.
        Assert.True(BridgeLayout.PierHeight(crossing, world) <= RoadConstants.MaxBridgePierHeight,
            $"fixture drifted: pier {BridgeLayout.PierHeight(crossing, world):F1} m cannot stand, "
            + "so this exercises branch (a) rather than the boat lane");

        Assert.Equal(expected, BridgeLayout.IsFreeSpan(crossing, world));
    }

    private sealed class ShallowFlat : WorldGenerator
    {
        public bool River;
        public override float GetHeight(float x, float z) => 26f; // 4 m of water: a river a ship sails
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        {
            weight = River ? Mathf.Max(0f, 1f - Mathf.Abs(x) / 30f) : 0f;
            width = River ? 60f : 0f;
        }
    }
}
