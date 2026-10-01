using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Declining a bank-top climb that would stand the deck out of reach of
/// vanilla's structural support.
///
/// The failure this prevents is invisible at generation time: the bridge is
/// built, the world is saved, and some minutes later the pier's top pole drops
/// below the material minimum and the deck falls into the river. Nothing in a
/// terrain metric or a network fingerprint sees it, which is why the rule is
/// arithmetic on <see cref="BridgeSupport"/> rather than an eyeball limit.
/// </summary>
public class BridgePierCapTests : IDisposable
{
    private readonly float _saved = RoadCrossingDetector.DeclineTallClimbs;

    public void Dispose() => RoadCrossingDetector.DeclineTallClimbs = _saved;

    /// <summary>A deep channel whose banks RAMP upward away from the water,
    /// along the road that crosses it. The road runs straight across, which is
    /// the ordinary case: a crossing is roughly perpendicular to its river, so
    /// "inland along the road" and "outward along the crossing" are the same
    /// direction.
    ///
    /// Two things about this fixture are deliberate. The ramp means there are
    /// intermediate heights to find, so trimming and falling back to the
    /// water's edge produce DIFFERENT decks and a test can tell them apart.
    /// And the straight road keeps every candidate on a placeable heading, so
    /// a trim that fails here failed for a reason of its own rather than being
    /// rejected by the hammer-grid rule.</summary>
    private sealed class DeepGorge : WorldGenerator
    {
        public override float GetHeight(float x, float z) =>
            Mathf.Abs(x) < 12f ? 20f : 31f + Mathf.Min(Mathf.Abs(x) - 12f, 12f) * 0.6f;
        public override Heightmap.Biome GetBiome(float x, float z) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        {
            weight = Mathf.Abs(x) < 12f ? 1f : 0f;
            width = 24f;
        }
    }

    /// <summary>Straight across the channel, with enough road either side for
    /// the bank walk's 12 m reach to have somewhere to go.</summary>
    private static readonly List<Vector2> GorgePath = new()
    {
        new(-40f, 0f), new(-28f, 0f), new(-16f, 0f),
        new(16f, 0f), new(28f, 0f), new(40f, 0f)
    };

    private static RoadCrossing Detect(WorldGenerator world) =>
        Assert.Single(RoadCrossingDetector.Detect(GorgePath, world, bridges: true, fords: false));

    /// <summary>The control. Without the rule the climb is taken and the deck
    /// ends up past the ceiling - if this stops being true the fixture has
    /// drifted and the test below proves nothing.</summary>
    [Fact]
    public void WithoutTheRuleTheClimbBuildsAPierTallerThanSupportReaches()
    {
        RoadCrossingDetector.DeclineTallClimbs = 0f;
        var world = new DeepGorge();
        float pier = BridgeLayout.PierHeight(Detect(world), world);
        Assert.True(pier > RoadConstants.MaxBridgePierHeight,
            $"fixture no longer produces an over-tall climb: {pier:F2} m against a {RoadConstants.MaxBridgePierHeight:F2} m ceiling");
    }

    [Fact]
    public void WithTheRuleTheDeckComesBackWithinTheSupportCeiling()
    {
        RoadCrossingDetector.DeclineTallClimbs = 1f;
        var world = new DeepGorge();
        float pier = BridgeLayout.PierHeight(Detect(world), world);
        Assert.True(pier <= RoadConstants.MaxBridgePierHeight,
            $"pier {pier:F2} m still exceeds the {RoadConstants.MaxBridgePierHeight:F2} m ceiling");
    }

    /// <summary>
    /// The deck coming back inside the ceiling does NOT prove the trim ran:
    /// falling all the way to the water's edge lands inside it too, and that
    /// is the outcome this rule exists to avoid. So assert the MECHANISM.
    ///
    /// This is not hypothetical. Aiming the trim at a single height trimmed
    /// zero of twelve over-tall climbs on a measured world and fell through to the
    /// water's edge every time, producing output byte-identical to the blunt
    /// rule -- network hash included -- which reads exactly like a refactor
    /// that correctly changed nothing. Only the counters said otherwise.
    /// See [[gate-pass-is-not-coverage]].
    /// </summary>
    [Fact]
    public void TheDeckComesBackByTRIMMING_NotByFallingToTheWatersEdge()
    {
        RoadCrossingDetector.DeclineTallClimbs = 1f;
        int trimmedBefore = RoadCrossingDetector.TrimTrimmed;
        int toEdgeBefore = RoadCrossingDetector.TrimToEdge;

        var world = new DeepGorge();
        RoadCrossing c = Detect(world);

        Assert.True(RoadCrossingDetector.TrimTrimmed > trimmedBefore,
            "the trim branch never fired; the deck came back some other way, which is the silent no-op this test exists to catch. "
            + $"seen={RoadCrossingDetector.TrimSeen} fine={RoadCrossingDetector.TrimAlreadyFine} "
            + $"noBanks={RoadCrossingDetector.TrimNoBanks} trimmed={RoadCrossingDetector.TrimTrimmed} "
            + $"toEdge={RoadCrossingDetector.TrimToEdge} stuck={RoadCrossingDetector.TrimStuck} "
            + $"deck={BridgeLayout.DeckHeight(c, world):F2} bed={c.RiverbedHeight:F2} pier={BridgeLayout.PierHeight(c, world):F2}");
        Assert.Equal(toEdgeBefore, RoadCrossingDetector.TrimToEdge);

        // And the trimmed crossing is genuinely sprung above the water rather
        // than sitting on it, which is the thing trimming buys.
        float deck = BridgeLayout.DeckHeight(c, world);
        Assert.True(deck > c.WaterLevel + BridgeLayout.DeckFreeboard + 0.5f,
            $"deck {deck:F2} m is at the waterline ({c.WaterLevel:F2} m), so nothing was gained over the blunt rule");
    }

    /// <summary>The counters are load-bearing, so the control has to move them
    /// too: a rule that is off must see nothing at all.</summary>
    [Fact]
    public void TheCountersStayStillWhileTheRuleIsOff()
    {
        RoadCrossingDetector.DeclineTallClimbs = 0f;
        int seen = RoadCrossingDetector.TrimSeen;
        Detect(new DeepGorge());
        Assert.Equal(seen, RoadCrossingDetector.TrimSeen);
    }

    /// <summary>It declines a climb; it does not delete a crossing. Returning
    /// null here would paint ordinary road across open water, which is worse
    /// than a tall bridge.</summary>
    [Fact]
    public void TheCrossingSurvivesTheDecline()
    {
        RoadCrossingDetector.DeclineTallClimbs = 1f;
        var world = new DeepGorge();
        RoadCrossing c = Detect(world);
        Assert.Equal(CrossingKind.Bridge, c.Kind);
        Assert.True(c.Width >= 1f);
        Assert.True(BridgeLayout.HeadingIsPlaceable(BridgeLayout.YawDegrees(c.Direction)),
            "the lower line must still be one the vanilla hammer can reproduce");
    }

    /// <summary>A crossing already inside the ceiling is not touched: the rule
    /// must not quietly demote every high bridge in the world.</summary>
    [Fact]
    public void AClimbWithinTheCeilingIsLeftAlone()
    {
        var world = new ReviewShallowBanks();
        var path = GorgePath;

        RoadCrossingDetector.DeclineTallClimbs = 0f;
        RoadCrossing before = Assert.Single(RoadCrossingDetector.Detect(path, world, bridges: true, fords: false));
        RoadCrossingDetector.DeclineTallClimbs = 1f;
        RoadCrossing after = Assert.Single(RoadCrossingDetector.Detect(path, world, bridges: true, fords: false));

        Assert.True(BridgeLayout.PierHeight(before, world) <= RoadConstants.MaxBridgePierHeight,
            "fixture must be a climb that is already safe");
        Assert.Equal(before.FromBank.x, after.FromBank.x, 3);
        Assert.Equal(before.FromBank.y, after.FromBank.y, 3);
        Assert.Equal(before.ToBank.x, after.ToBank.x, 3);
        Assert.Equal(before.ToBank.y, after.ToBank.y, 3);
    }

    /// <summary>Banks that rise enough to trigger the climb but not enough to
    /// break the support rule: the same shape, a shallower channel and a
    /// gentler ramp.</summary>
    private sealed class ReviewShallowBanks : WorldGenerator
    {
        public override float GetHeight(float x, float z) =>
            Mathf.Abs(x) < 12f ? 24f : 31f + Mathf.Min(Mathf.Abs(x) - 12f, 12f) * 0.3f;
        public override Heightmap.Biome GetBiome(float x, float z) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        {
            weight = Mathf.Abs(x) < 12f ? 1f : 0f;
            width = 24f;
        }
    }
}
