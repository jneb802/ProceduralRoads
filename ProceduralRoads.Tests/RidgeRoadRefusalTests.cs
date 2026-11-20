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
/// A road along a ridge (RoadRidges.Find: the ground lower on both sides by more than RoadRidges.Depth over
/// RoadRidges.MinLength or more) is refused by the one list (RoadNetworkGenerator.AlongARidge, in RoadRefusal) where the
/// ridge re-route finds no way off it, on every path that asks the list: generation's final check, an approach's trial
/// (RoadNetworkGenerator.TrialBuilds), a staircase's end floor (BuiltRoadRefusal) and a marked manual road (rule 13). A
/// crest shorter than RoadRidges.MinLength is not a ridge and is built on every path.
/// </summary>
[Collection("RoadStatics")]
public sealed class RidgeRoadRefusalTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly IDisposable _grade = GradeCap.At(0.35f);
    private readonly float _cap = RoadNetworkGenerator.EarthworkCap;
    private readonly int _retries = RoadNetworkGenerator.RidgeRetries;

    public RidgeRoadRefusalTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        NetworkCutoff.Forget();
    }

    public void Dispose()
    {
        SetPathfinder(null);
        RoadNetworkGenerator.EarthworkCap = _cap;
        RoadNetworkGenerator.RidgeRetries = _retries;
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        NetworkCutoff.Forget();
        _grade.Dispose();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pathfinder);

    private static Island Everywhere()
    {
        var i = new Island { Id = 1, CellSize = 8, WorldOffset = 0, Min = new Vector2(-280, -280), Max = new Vector2(280, 280) };
        for (int x = -35; x <= 35; x++) for (int z = -35; z <= 35; z++) i.Cells.Add(new Vector2Int(x, z));
        return i;
    }

    /// <summary>
    /// Two plateaus in the sea joined by a crest along x = 0 from z = -<see cref="Half"/> to z = <see cref="Half"/>, climbing
    /// at 19% (the south plateau at 60 m, the north one as high as the crest's top): its flanks fall 1 in 1, so the ground
    /// 7 m out on each side stands 7 m under its top, and beyond 7.5 m out they drop into water 8 m deep. No way round it
    /// and no shelf on its flanks: the road from one plateau to the other rides it.
    /// </summary>
    private sealed class Crest : WorldGenerator
    {
        public readonly float Half;
        /// <summary>How far under the crest's top its flank stands this far out (the default: 1 in 1), and how far out it
        /// runs before the water.</summary>
        private readonly Func<float, float, float> _flank;
        private readonly float _out;
        private readonly Heightmap.Biome _biome;
        public Crest(float half, Func<float, float>? flank = null, float flankOut = 7.5f, Heightmap.Biome biome = Heightmap.Biome.Meadows)
            : this(half, flank == null ? null : (x, _) => flank(x), flankOut, biome) { }
        /// <summary>A crest whose flank this far out may change along it (<paramref name="flank"/> of how far out and z).</summary>
        public Crest(float half, Func<float, float, float>? flank, float flankOut, Heightmap.Biome biome)
        { Half = half; _flank = flank ?? ((x, _) => x); _out = flankOut; _biome = biome; }
        private float Top(float z) => 60f + 0.19f * Mathf.Clamp(z + Half, 0f, 2f * Half);
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            bool plateau = Mathf.Abs(wy) >= Half && Mathf.Abs(wy) <= 120f && Mathf.Abs(wx) <= 60f;
            if (plateau) return Top(wy);
            if (Mathf.Abs(wy) < Half && Mathf.Abs(wx) <= _out) return Top(wy) - _flank(Mathf.Abs(wx), wy);
            return 22f;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => _biome;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 79;
    }

    private static readonly Vector2 South = new(0f, -80f), North = new(0f, 80f);

    private WorldGenerator Begin(float half, Func<float, float>? flank = null, float flankOut = 7.5f) => Begin(new Crest(half, flank, flankOut));

    private WorldGenerator Begin(Crest world)
    {
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady();
        RoadNetworkGenerator.EarthworkCap = RoadNetworkGenerator.DefaultEarthworkCap;
        RoadNetworkGenerator.RidgeRetries = 2;
        return world;
    }

    private static float Ground(WorldGenerator world, Vector2 p) => BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);

    /// <summary>The four paths' verdicts on the road from the south plateau to the north one: generation's (null where it
    /// built it, else its refusal), an approach trial's of the straight road along the crest (TrialBuilds: whether the road
    /// with it is built), a straight piece planned as a staircase's end floor plans its road and judged by the list
    /// with no more context (BuiltRoadRefusal), and a marked manual road's along the crest (null where it was planned, else its message);
    /// and the ridges of the end floor's piece.</summary>
    private (string? generation, bool trial, string? endFloor, string? manual, List<RoadRidges.Stretch> ridges) Verdicts(WorldGenerator world)
    {
        var finder = new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f, Fords = false, Bridges = false, Stairs = false };
        SetPathfinder(finder);
        bool generated = RoadNetworkGenerator.GenerateRoad(South, 0f, North, 0f, 4f, "over the crest");
        string? generation = generated ? null : RoadSpatialGrid.LastRefusal ?? "refused";
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.MarkLocationsReady();
        var cells = new List<Vector2>();
        for (float z = South.y; z <= North.y + 1e-3f; z += RoadPathfinder.CellSize) cells.Add(new Vector2(0f, z));
        bool trial = RoadNetworkGenerator.TrialBuilds(finder, cells, South, 0f, North, 0f, false, false, false, false, RoadNetworkGenerator.RoadWidth, 0f, 0f);
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.MarkLocationsReady();

        var line = new List<Vector2>();
        for (float z = South.y; z <= North.y + 1e-3f; z += 2f) line.Add(new Vector2(0f, z));
        RoadSpatialGrid.PlannedPath? piece = RoadSpatialGrid.PlanRoadPath(line, RoadNetworkGenerator.RoadWidth, world);
        Assert.True(piece != null, "the straight piece along the crest was not planned: " + RoadSpatialGrid.LastRefusal);
        string? endFloor = RoadNetworkGenerator.BuiltRoadRefusal(new List<RoadSpatialGrid.PlannedPath> { piece! }, new List<RoadCrossing>(), out _);
        var ridges = RoadNetworkGenerator.Ridges(new List<RoadSpatialGrid.PlannedPath> { piece! }, new List<RoadCrossing>()).FindAll(st => st.Kind == RoadRidges.Kind.Ridge);

        // The marked road searched as generation's road was (RoadNetworkGenerator.NewFinder, every search's: no staircase,
        // ford or bridge bought), so it rides the crest and its refusal is the list's, not its search's.
        string? manual = null;
        RoadNetworkGenerator.ConfigureFinders(f => { f.TerrainVarianceThreshold = 1000f; f.Meander = 0f; f.Fords = false; f.Bridges = false; f.Stairs = false; });
        try { ManualRoads.Prepare(new[] { South, new Vector2(0f, -40f), new Vector2(0f, 0f), new Vector2(0f, 40f), North }, false, new[] { Everywhere() }); }
        catch (Exception e) { manual = e.Message; }
        finally { RoadNetworkGenerator.ConfigureFinders(null); }
        return (generation, trial, endFloor, manual, ridges);
    }

    /// <summary>
    /// A crest 50 m long, climbing at 19%, the ground 7 m out on each side 7 m under it (the shape of the ridge a road rode
    /// off a staircase's top for 39.9 m, up to 7.14 m over the ground on either side, at 19%): the road along it is a ridge
    /// of more than 30 m, and with no way off it every path refuses it for the ridge. On the parent generation built it (the
    /// ridge re-route kept its plan where no way round was a third shorter) and the end floor and the marked road passed the
    /// list.
    /// </summary>
    [Fact]
    public void ARoadAlongALongRidgeIsRefusedOnEveryPath()
    {
        var (generation, trial, endFloor, manual, ridges) = Verdicts(Begin(half: 25f));
        var ridge = Assert.Single(ridges);
        Assert.True(ridge.Length >= 30f, FormattableString.Invariant($"a ridge of {ridge.Length:F0} m"));
        Assert.InRange(ridge.Worst, 6f, 8f);

        Assert.Equal(RoadNetworkGenerator.RidgeRoadRefusal, generation);
        Assert.False(trial, "the approach trial built the road along the ridge");
        Assert.True(endFloor != null && endFloor.StartsWith(RoadNetworkGenerator.RidgeRoadRefusal, StringComparison.Ordinal), endFloor ?? "the end floor's road passed");
        Assert.True(manual != null && manual.Contains(RoadNetworkGenerator.RidgeRoadRefusal), manual ?? "the marked road was planned");
    }

    /// <summary>
    /// A crest 8 m long of the same shape: the ground under the road falls away on both sides over less than
    /// RoadRidges.MinLength of it, a blip, not a ridge. Every path builds it.
    /// </summary>
    [Fact]
    public void AShortCrestIsBuiltOnEveryPath()
    {
        var (generation, trial, endFloor, manual, ridges) = Verdicts(Begin(half: 4f));
        Assert.Empty(ridges);
        Assert.True(generation == null, generation);
        Assert.True(trial, "the approach trial refused the short crest");
        Assert.True(endFloor == null, endFloor);
        Assert.True(manual == null, manual);
    }

    /// <summary>
    /// The ridge a road rode in game on the owner's walk (the road 60.1 m where he stood, the ground 8.2 m under it 8 m out
    /// and 9.8 m under it 12 m out, either side), 40 m long, climbing at 19%: a flank falling 1 in 1 to 8 m out, then 0.45
    /// in 1 to 12 m out, then the water. Generation, an approach trial and an end floor refuse the road along it for the
    /// ridge, and so does a marked road along it, searched as generation's is (with staircases its search bought a staircase
    /// off the crest that could not be laid, and it was refused for that before the list was asked). Only a crest 7 m deep at
    /// 7 m out, ending in water, was pinned.
    /// </summary>
    [Fact]
    public void ARoadAlongTheLongSteepRidgeOfTheWalkIsRefusedOnEveryPath()
    {
        static float Flank(float x) => x <= 8f ? x : 8f + (x - 8f) * 0.45f;
        var (generation, trial, endFloor, manual, ridges) = Verdicts(Begin(half: 20f, Flank, flankOut: 12.5f));
        var ridge = Assert.Single(ridges);
        Assert.True(ridge.Length >= RoadRidges.MinLength, FormattableString.Invariant($"a ridge of {ridge.Length:F0} m"));
        Assert.Equal(RoadNetworkGenerator.RidgeRoadRefusal, generation);
        Assert.False(trial, "the approach trial built the road along the ridge");
        Assert.True(endFloor != null && endFloor.StartsWith(RoadNetworkGenerator.RidgeRoadRefusal, StringComparison.Ordinal), endFloor ?? "the end floor's road passed");
        Assert.True(manual != null && manual.Contains(RoadNetworkGenerator.RidgeRoadRefusal), manual ?? "the marked road was planned");
    }

    /// <summary>
    /// The low ridge a road rode in game for more than 30 m, climbing: the natural ground 7 m out on each side 2.6 to 3.2 m
    /// under the road (read across it every 2 m), a causeway a player cannot step off. Its flank falls 2.5 m over 7 m, then
    /// the water: the road as planned along it stands 2.6 to 3.4 m over the ground on both sides at worst. Under a 4 m depth
    /// it was no ridge and every path built it. Every path refuses it for the ridge.
    /// </summary>
    [Fact]
    public void ARoadAlongTheLowLongRidgeOfTheWalkIsRefusedOnEveryPath()
    {
        static float Flank(float x) => x * 2.5f / 7f;
        var (generation, trial, endFloor, manual, ridges) = Verdicts(Begin(half: 20f, Flank));
        var ridge = Assert.Single(ridges);
        Assert.True(ridge.Length >= RoadRidges.MinLength, FormattableString.Invariant($"a ridge of {ridge.Length:F0} m"));
        Assert.InRange(ridge.Worst, 2.6f, 3.4f);
        Assert.Equal(RoadNetworkGenerator.RidgeRoadRefusal, generation);
        Assert.False(trial, "the approach trial built the road along the ridge");
        Assert.True(endFloor != null && endFloor.StartsWith(RoadNetworkGenerator.RidgeRoadRefusal, StringComparison.Ordinal), endFloor ?? "the end floor's road passed");
        Assert.True(manual != null && manual.Contains(RoadNetworkGenerator.RidgeRoadRefusal), manual ?? "the marked road was planned");
    }

    /// <summary>
    /// A road in a trench the length of the crest's: a cut 40 m long, climbing at 19%, its walls 3.5 m over the road 7 m
    /// out on each side, then the water. A trench left after the re-route is refused like a ridge, on every path (it was
    /// searched round and then built anyway).
    /// </summary>
    [Fact]
    public void ARoadInALongTrenchIsRefusedOnEveryPath()
    {
        static float Wall(float x) => -x * 0.5f;
        var (generation, trial, endFloor, manual, _) = Verdicts(Begin(half: 20f, Wall));
        Assert.Equal(RoadNetworkGenerator.TrenchRoadRefusal, generation);
        Assert.False(trial, "the approach trial built the road in the trench");
        Assert.True(endFloor != null && endFloor.StartsWith(RoadNetworkGenerator.TrenchRoadRefusal, StringComparison.Ordinal), endFloor ?? "the end floor's road passed");
        Assert.True(manual != null && manual.Contains(RoadNetworkGenerator.TrenchRoadRefusal), manual ?? "the marked road was planned");
    }

    /// <summary>A ridge is judged with no earthwork cap set too: the verdict is the road's shape, not the cap's re-route.</summary>
    [Fact]
    public void ARoadAlongALongRidgeIsRefusedWithNoEarthworkCap()
    {
        var world = Begin(half: 25f);
        RoadNetworkGenerator.EarthworkCap = 0f;
        var line = new List<Vector2>();
        for (float z = South.y; z <= North.y + 1e-3f; z += 2f) line.Add(new Vector2(0f, z));
        RoadSpatialGrid.PlannedPath? piece = RoadSpatialGrid.PlanRoadPath(line, RoadNetworkGenerator.RoadWidth, world);
        Assert.True(piece != null, "the straight piece along the crest was not planned: " + RoadSpatialGrid.LastRefusal);
        string? refused = RoadNetworkGenerator.BuiltRoadRefusal(new List<RoadSpatialGrid.PlannedPath> { piece! }, new List<RoadCrossing>(), out _);
        Assert.True(refused != null && refused.StartsWith(RoadNetworkGenerator.RidgeRoadRefusal, StringComparison.Ordinal), refused ?? "built with no cap");
    }

    // The owner's decision of 13 Nov 2026: in the Mistlands a ridge or trench that is not long is judged at the old 4 m
    // depth (RoadRules.Mistlands.Ridge, read through RoadRules.For), a long one at a player's height as everywhere.

    /// <summary>A length between RoadRidges.MinLength and RoadRidges.TrenchMinLength (15 m), metres: a crest or cut that long
    /// is a ridge or trench at a player's height, and not long in the Mistlands.</summary>
    private static float ShortRidge => (RoadRidges.MinLength + RoadRidges.TrenchMinLength) / 2f;
    private static float ShortTrench => ShortRidge;
    /// <summary>A length just past RoadRidges.TrenchMinLength (24 m), metres: long in the Mistlands.</summary>
    private static float LongRidge => RoadRidges.TrenchMinLength + 4f;
    /// <summary>The low ridge's flank (as <see cref="ARoadAlongTheLowLongRidgeOfTheWalkIsRefusedOnEveryPath"/>): 2.5 m under
    /// the crest 7 m out, the road 2.6 to 3.4 m over the ground on both sides, under 4 m and over a player's height.</summary>
    private static float LowFlank(float x) => x * 2.5f / 7f;

    private static void Built((string? generation, bool trial, string? endFloor, string? manual, List<RoadRidges.Stretch> ridges) v)
    {
        Assert.True(v.generation == null, v.generation);
        Assert.True(v.trial, "the approach trial refused the road");
        Assert.True(v.endFloor == null, v.endFloor);
        Assert.True(v.manual == null, v.manual);
    }

    private static void Refused((string? generation, bool trial, string? endFloor, string? manual, List<RoadRidges.Stretch> ridges) v, string reason)
    {
        Assert.Equal(reason, v.generation);
        Assert.False(v.trial, "the approach trial built the road");
        Assert.True(v.endFloor != null && v.endFloor.StartsWith(reason, StringComparison.Ordinal), v.endFloor ?? "the end floor's road passed");
        Assert.True(v.manual != null && v.manual.Contains(reason), v.manual ?? "the marked road was planned");
    }

    /// <summary>A low ridge (the road about 3 m over the ground on both sides) in the Mistlands, longer than
    /// RoadRidges.MinLength and shorter than RoadRidges.TrenchMinLength, on a road with no viaduct of its own: the
    /// Mistlands override holds only on a road with one (RoadRules.MistlandsRidgeViaductReach, the owner's pick of
    /// 13 Nov 2026), so the global rule judges it and every path refuses it alike.</summary>
    [Fact]
    public void AShortLowRidgeInTheMistlandsWithoutAViaductIsRefusedOnEveryPath()
    {
        var v = Verdicts(Begin(new Crest(ShortRidge / 2f, LowFlank, 7.5f, Heightmap.Biome.Mistlands)));
        Assert.Single(v.ridges);
        Refused(v, RoadNetworkGenerator.RidgeRoadRefusal);
    }

    /// <summary>The same short low Mistlands ridge as a staircase's end floor judges its road (RoadStairRuns.RoadOnto: the
    /// piece judged apart from its road's crossings, BuiltRoadRefusal then SearchedRound): on a road with a viaduct of its
    /// own, the ridge is judged from that road's viaducts (RoadRidges.Judged) as the road's commit judges it, and so built;
    /// on a road with none it is judged as before.</summary>
    [Fact]
    public void AnEndFloorOnARoadWithAViaductOfItsOwnJudgesItsRidgeFromThatViaduct()
    {
        WorldGenerator world = Begin(new Crest(ShortRidge / 2f, LowFlank, 7.5f, Heightmap.Biome.Mistlands));
        var line = new List<Vector2>();
        for (float z = South.y; z <= North.y + 1e-3f; z += 2f) line.Add(new Vector2(0f, z));
        RoadSpatialGrid.PlannedPath? piece = RoadSpatialGrid.PlanRoadPath(line, RoadNetworkGenerator.RoadWidth, world);
        Assert.True(piece != null, "the straight piece along the crest was not planned: " + RoadSpatialGrid.LastRefusal);
        var plans = new List<RoadSpatialGrid.PlannedPath> { piece! };
        // The road's own viaduct, off this piece beside its south end, its kind stored as the network stores it.
        RoadCrossing viaduct = RoadCrossing.Between(new Vector2(-40f, -82f), new Vector2(-10f, -82f), 40f, new Vector2(-25f, -82f), 0f, CrossingKind.Bridge);
        viaduct.Kit = BridgeStructures.Kit.DvergrViaduct;
        viaduct.ValleyJump = true;
        var own = new List<RoadCrossing> { viaduct };

        string? alone = RoadNetworkGenerator.BuiltRoadRefusal(plans, new List<RoadCrossing>(), out _) ?? RoadNetworkGenerator.SearchedRound(plans, out _);
        string? withIts = RoadNetworkGenerator.BuiltRoadRefusal(plans, new List<RoadCrossing>(), out _, ridgeCrossings: own)
            ?? RoadNetworkGenerator.SearchedRound(plans, out _, own);
        Assert.True(withIts == null, withIts);
        if (RoadRules.Mistlands.Ridge!.ViaductReach == null) Assert.True(alone == null, alone);
        else Assert.True(alone != null && alone.StartsWith(RoadNetworkGenerator.RidgeRoadRefusal, StringComparison.Ordinal), alone ?? "built without a viaduct");
    }

    /// <summary>The same low ridge in the Mistlands just past RoadRidges.TrenchMinLength long: long, so judged at a player's
    /// height, and refused on every path.</summary>
    [Fact]
    public void ALongLowRidgeInTheMistlandsIsRefusedOnEveryPath()
    {
        var v = Verdicts(Begin(new Crest(LongRidge / 2f, LowFlank, 7.5f, Heightmap.Biome.Mistlands)));
        var ridge = Assert.Single(v.ridges);
        Assert.True(ridge.Length >= RoadRidges.TrenchMinLength, FormattableString.Invariant($"a ridge of {ridge.Length:F0} m"));
        Refused(v, RoadNetworkGenerator.RidgeRoadRefusal);
    }

    /// <summary>The short low ridge outside the Mistlands (Meadows): still a ridge at a player's height, refused on every
    /// path. The decision is the Mistlands' alone.</summary>
    [Fact]
    public void AShortLowRidgeOutsideTheMistlandsIsStillRefusedOnEveryPath()
    {
        var v = Verdicts(Begin(new Crest(ShortRidge / 2f, LowFlank, 7.5f, Heightmap.Biome.Meadows)));
        var ridge = Assert.Single(v.ridges);
        Assert.InRange(ridge.Length, RoadRidges.MinLength, RoadRidges.TrenchMinLength);
        Refused(v, RoadNetworkGenerator.RidgeRoadRefusal);
    }

    /// <summary>A short ridge in the Mistlands deeper than 4 m (the crest's 1 in 1 flank, 7 m under it 7 m out): a ridge
    /// at RoadRules.Mistlands.Ridge.Depth over RoadRidges.MinLength, as every ridge was at b72a57d6, refused on every path.</summary>
    [Fact]
    public void AShortDeepRidgeInTheMistlandsIsRefusedOnEveryPath()
    {
        var v = Verdicts(Begin(new Crest(ShortRidge / 2f, (Func<float, float>?)null, 7.5f, Heightmap.Biome.Mistlands)));
        Assert.Single(v.ridges);
        Refused(v, RoadNetworkGenerator.RidgeRoadRefusal);
    }

    /// <summary>A trench shorter than RoadRidges.TrenchMinLength, its walls 3 m over its floor 7 m out, and 4.5 m for 2 m
    /// of it (a deep notch): outside the Mistlands it is a trench (RoadRidges.TrenchMinDepth deep) and every path refuses it;
    /// in the Mistlands it is deeper than 4 m for less than RoadRidges.MinLength, so judged at 4 m it is no trench, and
    /// every path builds it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AShortTrenchWithADeepNotchIsBuiltInTheMistlandsAndRefusedOutside(bool mistlands)
    {
        static float Wall(float x, float z) => -x * (Mathf.Abs(z) <= 1f ? 4.5f : 3f) / 7f;
        var v = Verdicts(Begin(new Crest(ShortTrench / 2f, Wall, 7.5f, mistlands ? Heightmap.Biome.Mistlands : Heightmap.Biome.Meadows)));
        if (mistlands && RoadRules.Mistlands.Ridge!.ViaductReach == null) Built(v);
        else Refused(v, RoadNetworkGenerator.TrenchRoadRefusal);
    }

    /// <summary>A trench in the Mistlands just past RoadRidges.TrenchMinLength long, its walls 3.5 m over its floor: long, so
    /// judged at a player's height, and refused on every path.</summary>
    [Fact]
    public void ALongTrenchInTheMistlandsIsRefusedOnEveryPath()
    {
        var v = Verdicts(Begin(new Crest(LongRidge / 2f, x => -x * 0.5f, 7.5f, Heightmap.Biome.Mistlands)));
        Refused(v, RoadNetworkGenerator.TrenchRoadRefusal);
    }
}
