using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The world's pitch ledger (RoadPitches: PitchRefusal, Record, Serialize, Load) through the production paths: a road
/// generated on an island worker within its island's share (BeginIsland, Shares) and recorded as it is committed
/// (GenerateRoad), a marked manual road judged against every pitch the world stores and recorded as it is appended
/// (ManualRoads.Prepare, Commit), and the ledger kept across a save and a load. Off an island worker the world's whole
/// budget applies, against every stored pitch, never a fresh one.
/// The pitch is laid by the test (<see cref="Pitching"/>) on the road as the one list of refusals reads it, as the last
/// resort's PlacePitches would on a climb too steep for its rests (pinned on its own in LastResortPitchTests): the search
/// and the planner find a way up any climb a small test world holds without one, so a road that takes one through the
/// search is not set up here.
/// </summary>
[Collection("RoadStatics")]
public sealed class PitchLedgerTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithNetwork(server: true);
    private readonly IDisposable _grade = GradeCap.At(0.35f);
    private readonly IDisposable _budget = StaticOverride.Keep(() => RoadPitches.PitchBudget);

    /// <summary>Meadows level at 40 m out to 700 m; water past.</summary>
    private sealed class Flat : WorldGenerator
    {
        public override float GetHeight(float x, float z) => Mathf.Abs(x) > 700f || Mathf.Abs(z) > 700f ? 0f : 40f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 93;
    }

    private readonly Flat _world = new();

    public PitchLedgerTests()
    {
        RoadNetworkGenerator.Reset();
        RoadPitches.ResetCounters();
        RoadNetworkGenerator.ConfigureFinders(f => { f.Stairs = false; f.Viaducts = false; f.TerrainVarianceThreshold = 1000f; f.Meander = 0f; });
        RoadNetworkGenerator.RefusedFirst = Pitching;
        _scope.WithWorld(_world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.RefusedFirst = null;
        SetPathfinder(null);
        RoadNetworkGenerator.ConfigureFinders(null);
        RoadPitches.ResetCounters();
        RoadNetworkGenerator.Reset();
        _budget.Dispose();
        _grade.Dispose();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pathfinder);

    private static Island Everywhere()
    {
        var i = new Island { Id = 1, CellSize = 8, WorldOffset = 0, Min = new Vector2(-680, -680), Max = new Vector2(680, 680) };
        for (int x = -85; x <= 85; x++) for (int z = -85; z <= 85; z++) i.Cells.Add(new Vector2Int(x, z));
        return i;
    }

    private readonly HashSet<RoadSpatialGrid.PlannedPath> _pitched = new();

    /// <summary>The test's last-resort pitch: on the longest piece of each road judged, a hump round its point nearest x = 0,
    /// eight edges at 50 % (<see cref="RoadPitches.PitchGrade"/>) up then down, allowed so by its schedule
    /// (PlannedPath.Grades), the rest as planned: one pitch of about 8 m to RoadPitches.PitchesOf. Refuses nothing.</summary>
    private RoadNetworkGenerator.Refusal? Pitching(RoadNetworkGenerator.PlannedRoad road)
    {
        if (road.Plans.Count == 0) return null;
        RoadSpatialGrid.PlannedPath piece = road.Plans.OrderByDescending(p => p.Points.Count).First();
        if (piece.Points.Count < 20 || !_pitched.Add(piece)) return null;
        int at = 0;
        for (int i = 1; i < piece.Points.Count; i++) if (Mathf.Abs(piece.Points[i].x) < Mathf.Abs(piece.Points[at].x)) at = i;
        at = Mathf.Clamp(at, 5, piece.Points.Count - 6);
        var grades = piece.Grades ?? Enumerable.Repeat(RoadGrade.Configured, piece.Points.Count).ToList();
        float rise = 0f;
        for (int i = at - 4; i <= at + 4; i++)
        {
            if (i > at - 4)
            {
                float edge = Vector2.Distance(piece.Points[i - 1], piece.Points[i]);
                rise += (i <= at ? 1f : -1f) * RoadPitches.PitchGrade * edge;
                grades[i] = RoadPitches.PitchGrade;
            }
            piece.Heights[i] += rise;
        }
        piece.Grades = grades;
        return null;
    }

    /// <summary>A road along z on island worker <paramref name="island"/> (0: off any), through GenerateRoad and its commit.</summary>
    private bool Generated(int island, float z, string label)
    {
        SetPathfinder(RoadNetworkGenerator.NewFinder(_world, 4f));
        try { return CrossIslandReads.On(island, () => RoadNetworkGenerator.GenerateRoad(new Vector3(-60f, 0f, z), 0f, new Vector3(60f, 0f, z), 0f, 4f, label)); }
        finally { SetPathfinder(null); }
    }

    /// <summary>A marked road along z, planned and committed; null when built, else why it was refused.</summary>
    private static string? Manual(float z)
    {
        try
        {
            ManualRoads.Plan plan = ManualRoads.Prepare(new[] { new Vector2(-60f, z), new Vector2(60f, z) }, false, new[] { Everywhere() });
            ManualRoads.Commit(plan);
            return null;
        }
        catch (InvalidOperationException e) { return e.Message; }
    }

    [Fact]
    public void ThePitchBudgetAndSpacingAreTheApprovedNumbers()
    {
        Assert.Equal(1000f, RoadPitches.PitchBudget);
        Assert.Equal(200f, RoadPitches.PitchSpacing);
        Assert.Equal(0.5f, RoadPitches.PitchGrade);
        Assert.Equal(8f, RoadPitches.PitchLength);
    }

    /// <summary>Each island's share is the budget times its chosen destinations over all the islands' (GenerationWork's
    /// shares): they sum to the budget, and an island with none gets none.</summary>
    [Fact]
    public void TheIslandsSharesAreTheBudgetByTheirDestinations()
    {
        float[] shares = RoadPitches.Shares(new[] { 6, 3, 1, 0 });
        Assert.Equal(new[] { 600f, 300f, 100f, 0f }, shares);
        Assert.Equal(RoadPitches.PitchBudget, shares.Sum(), 3);
        Assert.Equal(new[] { 0f }, RoadPitches.Shares(new[] { 0 }));
    }

    /// <summary>Through GenerateRoad on island workers: a road with a pitch is built and its pitch recorded at its commit;
    /// a second on the same island past its share (10 m, a pitch of about 8 m stored) is refused for the budget, however
    /// far off; the same road on another island, with its own share, is built: islands are separate networks.</summary>
    [Fact]
    public void AnIslandsRoadsKeepToItsShareAndAreRecordedAtTheirCommit()
    {
        RoadPitches.BeginIsland(1, 10f);
        RoadPitches.BeginIsland(2, 10f);
        Assert.True(Generated(1, 0f, "first"), RoadSpatialGrid.LastRefusal);
        var (count, metres, _) = RoadPitches.Stored();
        Assert.Equal(1, count);
        Assert.InRange(metres, 6f, 10f);

        Assert.False(Generated(1, 600f, "second"));
        Assert.Equal(RoadPitches.PitchBudgetRefusal, RoadSpatialGrid.LastRefusal);
        Assert.Equal(1, RoadPitches.Stored().count);

        Assert.True(Generated(2, 600f, "other island"), RoadSpatialGrid.LastRefusal);
        Assert.Equal(2, RoadPitches.Stored().count);
    }

    /// <summary>
    /// The ledger covers the stored world: generation stores a pitch at z = 0; a marked road with a pitch within PitchSpacing
    /// of it (z = 100) is refused for the spacing, one 400 m off is built and its pitch recorded as it is appended. After a
    /// save and a load both pitches are the world's still: another marked road 150 m from the manual one is refused for
    /// the spacing, and with the budget at what the two stored hold, one far from both is refused for the budget, and
    /// built within the whole budget.
    /// </summary>
    [Fact]
    public void GeneratedAndManualPitchesAreJudgedTogetherAndKeptAcrossASaveAndLoad()
    {
        RoadPitches.BeginIsland(1, RoadPitches.PitchBudget);
        Assert.True(Generated(1, 0f, "generated"), RoadSpatialGrid.LastRefusal);
        Assert.Equal(1, RoadPitches.Stored().count);

        string? near = Manual(100f);
        Assert.True(near != null && near.Contains(RoadPitches.PitchSpacingRefusal), near ?? "built");
        string? far = Manual(400f);
        Assert.True(far is null, far);
        var (count, metres, _) = RoadPitches.Stored();
        Assert.Equal(2, count);

        RoadLifecycleManager.OnPrepareSave(); RoadNetworkGenerator.Reset();
        Assert.Equal(0, RoadPitches.Stored().count);
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData()); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        RoadNetworkGenerator.MarkLocationsReady();
        Assert.Equal((count, metres), (RoadPitches.Stored().count, RoadPitches.Stored().metres));

        string? nearManual = Manual(550f);
        Assert.True(nearManual != null && nearManual.Contains(RoadPitches.PitchSpacingRefusal), nearManual ?? "built");
        RoadPitches.PitchBudget = metres + 1f;
        string? past = Manual(-400f);
        Assert.True(past != null && past.Contains(RoadPitches.PitchBudgetRefusal), past ?? "built");
        Assert.Equal(2, RoadPitches.Stored().count);
        RoadPitches.PitchBudget = 1000f;
        string? within = Manual(-400f);
        Assert.True(within is null, within);
        Assert.Equal(3, RoadPitches.Stored().count);
    }
}
