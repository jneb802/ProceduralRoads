using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// road_generate and road_regen_island over a world that already has a network (with a river and a mountain: crossings and
/// staircases), run as the game runs them
/// (RoadLifecycleManager.BeginForOperator, then the frames): the new network replaces the old one whole, and a run that
/// publishes nothing (it fails, finds nothing to build, or is cancelled) leaves the old one as it was, in the world.
/// The place on the mountain's side (a crypt, then a village once roads had to end level with their places) stands where
/// its road climbs by staircases however a road's end meets them (RoadEndRing): 5 m east, at -220,150, once the ends met
/// the staircases on their rings, the staircases of the road's zigzag up the side were refused on one another or not
/// reached, and the road was built with none. A place on that side gets staircases or none as the search falls (for the
/// crypt: -230,150 none with the ring or without it, -220,140 none without it).
/// </summary>
public class OperatorRegenerationTests : IDisposable
{
    private readonly ValheimWorldScope _world = new();

    public OperatorRegenerationTests()
    {
        var world = new WithAMesa { HasRiver = true, HasMountain = true };
        _world.WithWorld(world);
        _world.WithZoneSystem();
        foreach (var (name, x, z, radius) in new[]
        {
            ("StartTemple", 0f, 0f, 25f), ("Eikthyrnir", 260f, 40f, 10f), ("WoodVillage1", -225f, 150f, 18f), ("WoodVillage1", 120f, -260f, 18f),
        })
            TestLocations.Add(ZoneSystem.instance!, new ZoneSystem.LocationInstance
            {
                m_location = new ZoneSystem.ZoneLocation { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(name), m_exteriorRadius = radius },
                m_position = new Vector3(x, world.GetHeight(x, z), z),
            });
        _world.WithZdos().WithScene().WithNetwork(server: true).WithCommands().WithCheats();
        NetworkCutoff.Forget();
        RoadNetworkGenerator.Reset();
        ManualRoads.Reset();
        ZoneSystem.instance!.LocationsGenerated = true;
        ManualRoadCommands.Register();
        // The places in the mountain wait for their placing (PlacedSites.NeedsPlacing): placed as the load would.
        RoadNetworkGenerator.SitesFirst = TestPlacing.PlaceAll;
    }

    public void Dispose()
    {
        RoadNetworkGenerator.SitesFirst = null;
        PlacedSites.Reset();
        RoadNetworkGenerator.Reset();
        RoadLifecycleManager.WorldCannotHold = null;
        LocationLevelling.Prime = null;
        NetworkCutoff.Forget();
        ManualRoads.Reset();
        _world.Dispose();
    }

    /// <summary>A place only the old network went to: gone from the world before the regeneration, so a road to it is
    /// what is left of the old network.</summary>
    private static readonly Vector3 Gone = new(-250f, 0f, -280f);

    private static int PointsNearGone() =>
        RoadSpatialGrid.GetRoadPointsNearPosition(Gone, 40f).Count(p => p.IsRoad);

    /// <summary>The network generated as at load, with a road to <see cref="Gone"/>: the old network an operator replaces.</summary>
    private int OldNetwork()
    {
        var world = WorldGenerator.instance!;
        var zone = ZoneSystem.GetZone(Gone);
        TestLocations.Add(ZoneSystem.instance!, new ZoneSystem.LocationInstance
        {
            m_location = new ZoneSystem.ZoneLocation { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>("WoodVillage1"), m_exteriorRadius = 18f },
            m_position = new Vector3(Gone.x, world.GetHeight(Gone.x, Gone.z), Gone.z),
        });
        RoadNetworkGenerator.GenerateRoads();
        Assert.True(PointsNearGone() > 0, "the old network has no road to the place that goes");
        ZoneSystem.instance!.m_locationInstances.Remove(zone);
        return RoadSpatialGrid.TotalRoadPoints;
    }

    private static List<string> Begin(Vector3? island)
    {
        var told = new List<string>();
        Assert.Null(RoadLifecycleManager.BeginForOperator(island, told.Add));
        return told;
    }

    /// <summary>road_generate replaces the network whole: nothing of the old one (its road to a place now gone) is left, and the new one is
    /// what a generation on an empty world builds, not one joined onto the old. Inside the run RoadsAvailable is false (its
    /// workers own the world), which once kept the old network in place.</summary>
    [Fact]
    public void RoadGenerateOverANetworkReplacesItWhole()
    {
        RoadNetworkGenerator.GenerateRoads();
        int fresh = RoadSpatialGrid.TotalRoadPoints, roads = RoadNetworkGenerator.GetRoadStartPoints().Count;
        int stairs = StairStructures.Runs.Count, crossings = RoadNetworkGenerator.GetRoadCrossings().Count;
        RoadNetworkGenerator.Reset();
        OldNetwork();

        Begin(null);
        RoadLifecycleManager.RunToEndForTests();

        Assert.Equal("Complete", RoadLifecycleManager.Progress.Phase);
        Assert.Equal(0, PointsNearGone());
        Assert.Equal(fresh, RoadSpatialGrid.TotalRoadPoints);
        Assert.Equal(roads, RoadNetworkGenerator.GetRoadStartPoints().Count);
        Assert.Equal(stairs, StairStructures.Runs.Count);
        Assert.Equal(crossings, RoadNetworkGenerator.GetRoadCrossings().Count);
    }

    /// <summary>A regeneration that fails after it cleared the network brings the old one back as it was written into the
    /// world at its start: not a half-built network on top of it, and not "completed and kept".</summary>
    [Fact]
    public void AFailedRegenerationKeepsTheOldNetwork()
    {
        int old = OldNetwork();
        LocationLevelling.Prime = () => throw new InvalidOperationException("controlled regeneration failure");

        var told = Begin(null);
        Assert.Throws<InvalidOperationException>(RoadLifecycleManager.RunToEndForTests);

        Assert.Equal("Failed", RoadLifecycleManager.Progress.Phase);
        Assert.Contains(told, l => l.Contains("controlled regeneration failure"));
        Assert.True(RoadNetworkGenerator.RoadsAvailable);
        Assert.Equal(old, RoadSpatialGrid.TotalRoadPoints);
        Assert.True(PointsNearGone() > 0);
    }

    /// <summary>road_regen_island at a point in the ocean is refused before anything is cleared: the network stays.</summary>
    [Fact]
    public void AnOceanPointKeepsTheNetwork()
    {
        int old = OldNetwork();

        Assert.False(RoadNetworkGenerator.RegenerateIslandAt(new Vector3(3000f, 0f, 3000f), out string summary));

        Assert.Contains("No island", summary);
        Assert.True(RoadNetworkGenerator.RoadsAvailable);
        Assert.Equal(old, RoadSpatialGrid.TotalRoadPoints);
    }

    /// <summary>
    /// The synthetic world with a mesa on the meadow east of the river, away from the places: a flat top
    /// <see cref="MesaRises"/> rises (StairLayout.PieceRise) over the ground round it, every face at 1.2, as the scarps of the
    /// staircase tests (8 rises, the road was carried up a face on earthwork). A road onto its top
    /// climbs a face on a staircase whose foot and top are on level ground, whatever the step-up (StairLayout.StepUp): the
    /// old network's staircase does not hang on how the search on the mountain's side falls (at a step-up of 0.25 m the
    /// road to the village there was refused for its grade at the door it first took, and the network had none).
    /// </summary>
    private sealed class WithAMesa : SyntheticWorld
    {
        public const float MesaRises = 24f;
        /// <summary>The mesa's top's middle; its faces' feet are <see cref="Half"/> from it either way, on x and on z.</summary>
        public static readonly Vector2 Top = new(250f, 170f);
        public const float Half = 40f;
        public override float GetHeight(float x, float z)
        {
            float inside = Half - Mathf.Max(Mathf.Abs(x - Top.x), Mathf.Abs(z - Top.y));
            return base.GetHeight(x, z) + Mathf.Clamp(inside * 1.2f, 0f, MesaRises * StairLayout.PieceRise);
        }
    }

    /// <summary>A manual road added to the old network: road_path from the meadow up onto the mesa's top
    /// (<see cref="WithAMesa"/>), on a staircase.</summary>
    private static int ManualRoad()
    {
        int before = RoadSpatialGrid.TotalRoadPoints, stairs = StairStructures.Runs.Count;
        var console = new Terminal();
        Vector2 top = WithAMesa.Top;
        console.TryRunCommand(FormattableString.Invariant($"road_path {top.x - WithAMesa.Half - 30f:F0},{top.y:F0} {top.x:F0},{top.y:F0}"));
        Assert.True(RoadSpatialGrid.TotalRoadPoints > before, "the manual road was not added: " + string.Join(" | ", console.Output));
        Assert.True(StairStructures.Runs.Count > stairs, "the manual road climbed onto the mesa with no staircase: " + string.Join(" | ", console.Output));
        return RoadSpatialGrid.TotalRoadPoints;
    }

    /// <summary>Cancel on an operator's run stops it and keeps the old roads, a manual one among them, and their staircases
    /// and crossings; the host stays in the world.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellingAnOperatorsRunKeepsTheOldRoadsAndTheWorld(bool oneIsland)
    {
        OldNetwork();
        int old = ManualRoad();
        int stairs = StairStructures.Runs.Count, crossings = RoadNetworkGenerator.GetRoadCrossings().Count;
        int menus = 0;
        Assert.True(stairs > 0 && crossings > 0, $"the old network has no staircase or crossing to keep ({stairs}, {crossings})");
        RoadLifecycleManager.ReturnToMenuForTests = () => menus++;
        try
        {
            var told = Begin(oneIsland ? Vector3.zero : null);
            Assert.True(RoadLifecycleManager.OperatorRunActive);
            RoadLifecycleManager.Tick();
            Assert.True(RoadLifecycleManager.RequestCancel(RoadLifecycleManager.Progress.RunId));
            RoadLifecycleManager.RunToEndForTests();

            Assert.Equal(0, menus);
            Assert.Equal("Cancelled", RoadLifecycleManager.Progress.Phase);
            Assert.Contains(told, l => l.Contains("cancelled"));
            Assert.True(RoadNetworkGenerator.RoadsAvailable);
            Assert.Equal(old, RoadSpatialGrid.TotalRoadPoints);
            Assert.True(PointsNearGone() > 0);
            Assert.Equal(stairs, StairStructures.Runs.Count);
            Assert.Equal(crossings, RoadNetworkGenerator.GetRoadCrossings().Count);
            Assert.Contains(told, l => l.Contains("The roads are as they were."));
        }
        finally { RoadLifecycleManager.ReturnToMenuForTests = null; }
    }

    /// <summary>An old network that was itself a replacement left alone the zones explored this session before it. Read back
    /// after a run that published nothing, it leaves them alone still: from the world alone it would count only the zones
    /// explored before this session, write its roads into the rest, and save them as its own.</summary>
    [Fact]
    public void TheNetworkReadBackLeavesAloneWhatItLeftAlone()
    {
        OldNetwork();
        var explored = new Vector2s(9, 9);
        ZoneSystem.instance!.Generated.Add(explored);      // explored this session, under the first network
        Begin(null);                                       // a replacement: it leaves that zone alone
        RoadLifecycleManager.RunToEndForTests();
        Assert.Equal("Complete", RoadLifecycleManager.Progress.Phase);
        Assert.True(NetworkCutoff.Excludes(explored));
        int replaced = RoadSpatialGrid.TotalRoadPoints;

        Begin(null);
        RoadLifecycleManager.Tick();
        Assert.True(RoadLifecycleManager.RequestCancel(RoadLifecycleManager.Progress.RunId));
        RoadLifecycleManager.RunToEndForTests();

        Assert.Equal("Cancelled", RoadLifecycleManager.Progress.Phase);
        Assert.Equal(replaced, RoadSpatialGrid.TotalRoadPoints);
        Assert.True(NetworkCutoff.Excludes(explored), "the network read back writes a zone it left alone");
    }

    /// <summary>An operator's run goes on only while the world can be held for it (RoadGeneration_Patch in the game: a player
    /// who connects meanwhile is neither held nor given new land). Asked before it starts, and every frame: the run stops,
    /// says why, and keeps the old roads.</summary>
    [Fact]
    public void AnOperatorsRunStopsWhenTheWorldCanNoLongerBeHeld()
    {
        int old = OldNetwork();
        string? cannot = "1 other player(s) are connected";
        RoadLifecycleManager.WorldCannotHold = () => cannot;
        Assert.Equal(cannot, RoadLifecycleManager.BeginForOperator(null, _ => { }));
        Assert.False(RoadLifecycleManager.IsGenerating);

        cannot = null;
        var told = Begin(null);
        RoadLifecycleManager.Tick();
        cannot = "1 other player(s) are connected";
        RoadLifecycleManager.RunToEndForTests();

        Assert.Equal("Cancelled", RoadLifecycleManager.Progress.Phase);
        Assert.Contains(told, l => l.Contains("1 other player(s) are connected") && l.Contains("The roads are as they were."));
        Assert.True(RoadNetworkGenerator.RoadsAvailable);
        Assert.Equal(old, RoadSpatialGrid.TotalRoadPoints);
    }

    /// <summary>A client, or a host still asked what an explored world's first network does, is answered at once: no run
    /// starts, nothing is held.</summary>
    [Fact]
    public void AClientsCommandIsRefusedAtOnce()
    {
        _world.WithNetwork(server: false);
        string? refused = RoadLifecycleManager.BeginForOperator(null, _ => { });
        Assert.Contains("only on the server", refused);
        Assert.False(RoadLifecycleManager.IsGenerating);
    }

    private static readonly DateTime Noon = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private static string? Command(string command, Vector3? island = null, bool force = false, DateTime? now = null) =>
        RoadLifecycleManager.BeginForOperator(island, _ => { }, command: command, force: force, now: now ?? Noon);

    /// <summary>road_generate over a generated network refuses first and says what it would replace; nothing starts and the
    /// network stays. The same command again within the window runs it.</summary>
    [Fact]
    public void RoadGenerateOverANetworkAsksThenRunsOnTheRepeat()
    {
        int old = OldNetwork();

        string? asked = Command("road_generate");
        Assert.NotNull(asked);
        Assert.Contains($"{RoadNetworkGenerator.GetRoadStartPoints().Count} road(s)", asked);
        Assert.Contains(RoadSpatialGrid.Statistics.LengthText("F1", 1000f, " km"), asked);
        Assert.Contains($"{StairStructures.Runs.Count} staircase(s)", asked);
        Assert.Contains("bridge(s)", asked);
        Assert.Contains("again within 2 minutes", asked);
        Assert.False(RoadLifecycleManager.IsGenerating);
        Assert.Equal(old, RoadSpatialGrid.TotalRoadPoints);

        Assert.Null(Command("road_generate", now: Noon + ManualRoads.ConfirmWindow - TimeSpan.FromSeconds(1)));
        Assert.True(RoadLifecycleManager.IsGenerating);
        RoadLifecycleManager.RunToEndForTests();
        Assert.Equal("Complete", RoadLifecycleManager.Progress.Phase);
    }

    [Fact]
    public void TheRepeatAfterTheWindowAsksAgain()
    {
        OldNetwork();
        Assert.NotNull(Command("road_generate"));
        Assert.NotNull(Command("road_generate", now: Noon + ManualRoads.ConfirmWindow + TimeSpan.FromSeconds(1)));
        Assert.False(RoadLifecycleManager.IsGenerating);
    }

    [Fact]
    public void ForceRunsRoadGenerateAtOnce()
    {
        OldNetwork();
        Assert.Null(Command("road_generate", force: true));
        Assert.True(RoadLifecycleManager.IsGenerating);
        RoadLifecycleManager.RunToEndForTests();
    }

    /// <summary>road_regen_island clears the whole network first, so it asks too; another command does not confirm it.</summary>
    [Fact]
    public void RoadRegenIslandOverANetworkAsksToo()
    {
        OldNetwork();
        Assert.NotNull(Command("road_regen_island", Vector3.zero));
        Assert.NotNull(Command("road_generate"));
        Assert.False(RoadLifecycleManager.IsGenerating);
        Assert.Null(Command("road_generate"));
        RoadLifecycleManager.RunToEndForTests();
    }

    /// <summary>road_regen_island's repeat is the same island, wherever on it the operator stands: 15 m on from where it was
    /// asked, it runs.</summary>
    [Fact]
    public void RoadRegenIslandIsConfirmedOnTheSameIslandAnywhere()
    {
        OldNetwork();
        Vector3 asked = new(0f, 0f, 0f), moved = new(12f, 0f, -9f);
        Assert.Same(ManualRoads.IslandAt(asked), ManualRoads.IslandAt(moved));
        Assert.NotNull(Command("road_regen_island", asked));
        Assert.Null(Command("road_regen_island", moved));
        RoadLifecycleManager.RunToEndForTests();
    }

    /// <summary>road_regen_island off any island is confirmed only in the same zone: asked at one point in the ocean, the same
    /// command at another one zones away asks again, where both counted as one "no island".</summary>
    [Fact]
    public void RoadRegenIslandOffAnyIslandIsNotConfirmedElsewhere()
    {
        OldNetwork();
        Vector3 asked = new(3000f, 0f, 3000f), elsewhere = new(3200f, 0f, 3000f);
        Assert.Null(ManualRoads.IslandAt(asked));
        Assert.Null(ManualRoads.IslandAt(elsewhere));
        Assert.NotNull(Command("road_regen_island", asked));
        Assert.NotNull(Command("road_regen_island", elsewhere));
        Assert.False(RoadLifecycleManager.IsGenerating);
    }

    /// <summary>The one force reading (ManualRoads.EndsInForce): the last word, as the manual roads take it; the help text
    /// reads the confirm window (ManualRoads.ConfirmWindow).</summary>
    [Fact]
    public void ForceIsTheLastWordAndTheHelpReadsTheWindow()
    {
        Assert.True(ManualRoads.EndsInForce(new[] { "10", "20", "force" }));
        Assert.True(ManualRoads.EndsInForce(new[] { "force" }));
        Assert.False(ManualRoads.EndsInForce(new[] { "force", "10" }));
        Assert.False(ManualRoads.EndsInForce(Array.Empty<string>()));
        Assert.Contains($"within {ManualRoads.ConfirmWindow.TotalMinutes:F0} minutes", RoadLifecycleManager.AsksFirstHelp);
    }

    /// <summary>A world with no network is generated straight away.</summary>
    [Fact]
    public void AWorldWithNoNetworkRunsAtOnce()
    {
        Assert.False(RoadNetworkGenerator.NetworkInMemory);
        Assert.Null(Command("road_generate"));
        Assert.True(RoadLifecycleManager.IsGenerating);
        RoadLifecycleManager.RunToEndForTests();
    }

    /// <summary>The existing refusals come before the question.</summary>
    [Fact]
    public void ARefusalToHoldTheWorldComesBeforeTheQuestion()
    {
        OldNetwork();
        RoadLifecycleManager.WorldCannotHold = () => "1 other player(s) are connected";
        Assert.Equal("1 other player(s) are connected", Command("road_generate"));
        RoadLifecycleManager.WorldCannotHold = null;
        Assert.Contains("Not run yet", Command("road_generate"));
    }
}
