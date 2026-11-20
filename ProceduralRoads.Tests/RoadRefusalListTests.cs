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
/// One list of why a planned road is refused (RoadNetworkGenerator.RoadRefusal, decisions 6a and 6e), asked by every path
/// that judges a road before anything of it is stored: generation's final check, an approach trial, a marked manual road.
/// One safety check for both of generation's re-route acceptances (RoadNetworkGenerator.SafetyFaults): the earthwork
/// re-route's and the ridge re-route's. And a road is stored as it was checked (RoadNetworkGenerator.WrittenRoad).
/// </summary>
[Collection("RoadStatics")]
public sealed class RoadRefusalListTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly IDisposable _source = StaticOverride.Keep(() => LocationLevelling.Source);
    private readonly IDisposable _dressing = StaticOverride.Keep(() => StairStructures.Dressing);
    private readonly IDisposable _grade = GradeCap.At(0.35f);
    private readonly float _cap = RoadNetworkGenerator.EarthworkCap;
    private readonly int _retries = RoadNetworkGenerator.RidgeRetries;

    public RoadRefusalListTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        NetworkCutoff.Forget();
        RoadNetworkGenerator.ConfigureFinders(f => { f.Stairs = true; f.TerrainVarianceThreshold = 1000f; f.Meander = 0f; });
    }

    public void Dispose()
    {
        SetPathfinder(null);
        RoadNetworkGenerator.ConfigureFinders(null);
        RoadNetworkGenerator.EarthworkCap = _cap;
        RoadNetworkGenerator.RidgeRetries = _retries;
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        NetworkCutoff.Forget();
        _grade.Dispose();
        _dressing.Dispose();
        _source.Dispose();
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

    /// <summary>A kit that refuses every staircase (StairStructures.Dressing: a walking piece the support rule breaks however
    /// held), as the support tests' kit is.</summary>
    private static void EveryStaircaseRefused() =>
        StairStructures.Dressing = (run, w, finished, shortfall, shared) =>
        {
            shortfall.Falling.Add(run.Pieces.First(p => p.Kind != StructurePieceKind.StairSupport).Position);
            return new List<StructurePiece>();
        };

    /// <summary>
    /// A location at the origin that levels its ground, a ridge east of it its approach goes round (LocationApproachAcceptanceTests),
    /// and 100 m further east a face of 1.2, 24 m up, in the mountains: the road from 200 m east climbs down it on a staircase
    /// before it reaches the location's approach.
    /// </summary>
    private sealed class FaceAndRidge : WorldGenerator
    {
        /// <summary>Ripples this high on the ground east of x = 60 (none by default).</summary>
        public float Ripple;
        public override float GetHeight(float x, float z) =>
            Mathf.Abs(x) > 300f || Mathf.Abs(z) > 300f ? 0f
            : 60f + 8f * (float)Math.Exp(-Math.Pow((x - 28f) / 16f, 2) - Math.Pow(z / 7f, 2)) + Mathf.Clamp((x - 110f) * 1.2f, 0f, 24f)
                + (x > 60f ? Ripple * Mathf.Sin(x * 0.9f) * Mathf.Sin(z * 0.7f) : 0f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 77;
    }

    private static readonly Vector2 East = new(200f, 0f);

    private WorldGenerator BeginFaceAndRidge(float ripple = 0f)
    {
        var world = new FaceAndRidge { Ripple = ripple };
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady();
        LocationLevelling.Source = c => c.magnitude < 0.5f ? new[] { new LevelOp(0, 0, 0f, 8, false) } : null;
        return world;
    }

    /// <summary>The road from 200 m east to the location's keep-out edge, searched (buying its staircase down the face) and
    /// planned as generation plans it, its approach improved by its trials (RoadNetworkGenerator.PlanFromPath).</summary>
    private static RoadNetworkGenerator.RoadPlan Planned(WorldGenerator world)
    {
        var finder = RoadNetworkGenerator.NewFinder(world, 4f);
        List<Vector2>? path = finder.FindPath(East, new Vector2(0f, 0f));
        Assert.True(path != null && path.Count >= 2, "no route from the east");
        using (RoadNetworkGenerator.OneRoad())
            return RoadNetworkGenerator.PlanFromPath(finder, path!, path![0], 0f, new Vector2(0f, 0f), 16f, false, false, true, false, 4f);
    }

    /// <summary>Where the road's own approach ends, as searched: planned with the location's levelling unknown, so no approach
    /// is improved (RoadNetworkGenerator.ImproveLocationApproach keeps the old one).</summary>
    private static Vector2 OwnEnd(WorldGenerator world)
    {
        var source = LocationLevelling.Source;
        LocationLevelling.Source = _ => null;
        try { var own = Planned(world); return own.Path[own.Path.Count - 1]; }
        finally { LocationLevelling.Source = source; }
    }

    /// <summary>
    /// An approach trial is refused for a staircase the support rule breaks (RoadNetworkGenerator.StairsRefusal, in the one
    /// list): with a kit that refuses every staircase, no approach a candidate proposes is the road's (each trial's whole road
    /// keeps the staircase down the face), so the road keeps its own approach. With the kit holding them, the approach round
    /// the ridge is taken. On the parent the trials asked a shorter list without the staircases' support: the refused road
    /// took the improved approach all the same, a trial generation's own final check then refused.
    /// </summary>
    [Fact]
    public void AnApproachTrialIsRefusedForAStaircaseTheSupportRuleBreaks()
    {
        WorldGenerator world = BeginFaceAndRidge();
        Vector2 own = OwnEnd(world);
        var held = Planned(world);
        Assert.True(held.Plans != null, RoadSpatialGrid.LastRefusal);
        Assert.Contains(held.Stairs, st => !st.Existing);
        Vector2 improved = held.Path[held.Path.Count - 1];
        Assert.True(Vector2.Distance(improved, own) > 2f, $"the approach ends at {improved}, as its own at {own}: none improved");

        EveryStaircaseRefused();
        var refused = Planned(world);
        Vector2 end = refused.Path[refused.Path.Count - 1];
        Assert.True(Vector2.Distance(end, own) < 0.01f, $"the approach ends at {end}, not its own at {own}: a trial whose staircase its kit refuses was taken");
    }

    /// <summary>
    /// Parity (rule 13): the same road judged by generation (GenerateRoadCore's final check), its approach trials
    /// (PlanFromPath) and a marked manual road (ManualRoads.PrepareCore) gets one verdict from the one list: with a kit that
    /// refuses every staircase, generation refuses it for the support, every trial is refused (the road keeps its own
    /// approach) and the marked road is refused for the same reason; with the kit holding them all three build it. On the
    /// parent the trials accepted what generation and the marked road refused.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerationATrialAndAMarkedRoadGiveOneVerdict(bool refusing)
    {
        WorldGenerator world = BeginFaceAndRidge();
        if (refusing) EveryStaircaseRefused();

        SetPathfinder(RoadNetworkGenerator.NewFinder(world, 4f));
        bool generated = RoadNetworkGenerator.GenerateRoad(new Vector3(East.x, 0f, East.y), 0f, new Vector3(0f, 0f, 0f), 16f, 4f, "generated");
        string? generationWhy = generated ? null : RoadSpatialGrid.LastRefusal;
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        RoadNetworkGenerator.MarkLocationsReady();

        Vector2 own = OwnEnd(world);
        var trial = Planned(world);
        Vector2 end = trial.Path[trial.Path.Count - 1];
        bool trialBuilt = Vector2.Distance(end, own) > 2f;

        string? manualWhy = null;
        try { ManualRoads.Prepare(new[] { East, new Vector2(18f, 0f) }, false, new[] { Everywhere() }); }
        catch (InvalidOperationException e) { manualWhy = e.Message; }

        if (refusing)
        {
            Assert.Equal(StairStructures.RefusedBySupport, generationWhy);
            Assert.False(trialBuilt, $"a trial was taken, its approach ending at {end}");
            Assert.True(manualWhy != null && manualWhy.Contains(StairStructures.RefusedBySupport), manualWhy ?? "the marked road was planned");
        }
        else
        {
            Assert.True(generated, generationWhy);
            Assert.True(trialBuilt, $"no trial was taken, its approach ending at {end}");
            Assert.True(manualWhy == null, manualWhy);
        }
    }
    /// <summary>A kit whose support the road's own fill decides (StairStructures.Dressing): a staircase with the finished
    /// ground it is given standing over the generator's ground under a walking piece (a vertex it stands on,
    /// StructureGround.VerticesUnder) is refused there, the piece buried by fill read as one the support rule breaks; else
    /// the real kit lays it. Staircase cuts only lower the ground, so only a road's fill, pad or mound refuses one.</summary>
    private static void StaircasesOnFillRefused()
    {
        var real = StairStructures.Dressing;
        StairStructures.Dressing = (run, w, finished, shortfall, shared) =>
        {
            foreach (StructurePiece piece in StairStructures.WalkingPieces(run))
                foreach ((int x, int z) in StructureGround.VerticesUnder(piece.Position))
                    if (finished(x, z) > BiomeBlendedHeight.GetBlendedHeight(x, z, w) + 0.05f)
                    {
                        shortfall.Falling.Add(piece.Position);
                        return new List<StructurePiece>();
                    }
            return real(run, w, finished, shortfall, shared);
        };
    }

    /// <summary>
    /// A staircase is read before its road is stored on the ground its first build reads (RoadNetworkGenerator.StairsRefusal
    /// inside RoadTerrainModifier.AsStored, over the one assembly of what the road writes, RoadNetworkGenerator.Written): the
    /// road's own fill, pads and cuts in it. With a kit that refuses a staircase where fill stands under a walking piece, the
    /// road down the face, over ground rippled east of the ridge, whose own earthwork fills under walking pieces of its staircase, is refused for the staircase by generation's
    /// final check and by a marked manual road alike (parity, rule 13), never stored to have the staircase refused at its
    /// build. On the parent the check read the network as it stood with only the staircase cuts, so no fill: the road was
    /// stored, and its staircase refused at its first build (StairStructures.Dresser).
    /// </summary>
    [Fact]
    public void AStaircaseTheRoadsOwnFillRefusesRefusesItsRoadBeforeItIsStored()
    {
        WorldGenerator world = BeginFaceAndRidge(ripple: 0.6f);
        StaircasesOnFillRefused();

        SetPathfinder(RoadNetworkGenerator.NewFinder(world, 4f));
        bool generated = RoadNetworkGenerator.GenerateRoad(new Vector3(East.x, 0f, East.y), 0f, new Vector3(0f, 0f, 0f), 16f, 4f, "generated");
        if (generated)
        {
            StairRun run = Assert.Single(StairStructures.Runs);
            List<StructurePiece> built = StairStructures.Build(run, world);
            Assert.Fail($"the road was stored with its staircase {run.From} -> {run.To}; its first build laid {built.Count} piece(s)");
        }
        Assert.Equal(StairStructures.RefusedBySupport, RoadSpatialGrid.LastRefusal);
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        RoadNetworkGenerator.MarkLocationsReady();

        string? manualWhy = null;
        try { ManualRoads.Prepare(new[] { East, new Vector2(18f, 0f) }, false, new[] { Everywhere() }); }
        catch (InvalidOperationException e) { manualWhy = e.Message; }
        Assert.True(manualWhy != null && manualWhy.Contains(StairStructures.RefusedBySupport), manualWhy ?? "the marked road was planned");
    }

    /// <summary>
    /// Flat ground at 60 m; a block 6 m higher round x = <see cref="Floor"/>, its east edge ramped over 20 m; a cliff 30 m
    /// higher from 4 m west of the gully; cut by a gully 4 m wide along x = <see cref="Floor"/> down to the flat ground. A road
    /// along the gully's floor is in a trench (RoadRidges); off it, the only way is up onto the block east of it, toward the
    /// zone east of x = 32.
    /// </summary>
    private sealed class GullyByAZone : WorldGenerator
    {
        public const float Floor = 16f;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float d = wx - Floor;
            if (d < -4f) return 90f;
            float block = Mathf.Clamp01((30f - Mathf.Abs(wy)) / 20f + 1f) * Mathf.Clamp01((40f - d) / 20f);
            float wall = Mathf.Clamp01((Mathf.Abs(d) - 2f) / 2f);
            return 60f + 6f * block * wall;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 77;
    }

    /// <summary>The road along the gully, from (Floor, -80) to (Floor, 80), generated with <paramref name="retries"/> ridge
    /// re-routes while the network goes round <paramref name="avoided"/>; its stored pieces, or null where it was refused.</summary>
    private List<RoadSpatialGrid.PlannedPath>? AlongTheGully(int retries, HashSet<Vector2s> avoided)
    {
        RoadNetworkGenerator.Reset();
        var world = new GullyByAZone();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f, Fords = false, Bridges = false, Stairs = false });
        RoadNetworkGenerator.EarthworkCap = RoadNetworkGenerator.DefaultEarthworkCap;
        RoadNetworkGenerator.RidgeRetries = retries;
        var pieces = new List<RoadSpatialGrid.PlannedPath>();
        var hook = RoadNetworkGenerator.PieceCommitted;
        RoadNetworkGenerator.PieceCommitted = (plan, _) => pieces.Add(plan);
        try
        {
            using (NetworkCutoff.AvoidWhilePlanning(avoided, 0f))
                return RoadNetworkGenerator.GenerateRoad(new Vector2(GullyByAZone.Floor, -80f), 0f, new Vector2(GullyByAZone.Floor, 80f), 0f, 4f, "gully")
                    ? pieces : null;
        }
        finally { RoadNetworkGenerator.PieceCommitted = hook; }
    }

    /// <summary>
    /// A ridge re-route (RoadNetworkGenerator.OffRidges) is held to the safety the earthwork re-route is
    /// (RoadNetworkGenerator.SafetyFaults): one whose write reaches a zone the network goes round is not taken. Off the
    /// trench, the road climbs onto the block east of the gully, its write into the zone east of it; with that zone gone
    /// round, the re-route is not taken and the road along the gully is refused for its trench (with no re-route it is too).
    /// Control: with no zone gone round, it is taken.
    /// On the parent the re-route was taken (its ridge metres alone judged), and the road was then refused by the final
    /// check for the zone: a buildable road lost.
    /// </summary>
    [Fact]
    public void ARidgeReRouteIntoAZoneTheNetworkGoesRoundIsNotTaken()
    {
        var east = new HashSet<Vector2s> { new(1, -2), new(1, -1), new(1, 0), new(1, 1), new(1, 2) };
        Assert.Null(AlongTheGully(retries: 0, east));
        Assert.StartsWith(RoadNetworkGenerator.TrenchRoadRefusal, RoadSpatialGrid.LastRefusal ?? "");

        var free = AlongTheGully(retries: 2, new HashSet<Vector2s>());
        Assert.True(free != null, "refused with no zone gone round: " + RoadSpatialGrid.LastRefusal);
        Assert.Equal(1, RoadNetworkGenerator.RidgeReroutes);
        var reached = new HashSet<Vector2s>(ManualRoads.ZonesReached(free!, new List<RoadCrossing>()));
        Assert.True(reached.Overlaps(east), "the re-route off the trench does not reach the zone east of it: the case is not shown");

        Assert.Null(AlongTheGully(retries: 2, east));
        Assert.Equal(0, RoadNetworkGenerator.RidgeReroutes);
        Assert.StartsWith(RoadNetworkGenerator.TrenchRoadRefusal, RoadSpatialGrid.LastRefusal ?? "");
    }

    /// <summary>Meadows at 33 m with a river 40 m wide and 11 m deep across x = 0, its banks sloping down to it over 40 m
    /// (within the grade cap, so a road down to it rides its banks, not a cut into them).</summary>
    private sealed class River : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 300f || Mathf.Abs(wy) > 300f ? 0f : Mathf.Abs(wx) < 20f ? 22f : Mathf.Abs(wx) < 60f ? 22f + 11f * (Mathf.Abs(wx) - 20f) / 40f : 33f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Abs(wx) < 40f ? Mathf.Clamp01(1f - Mathf.Abs(wx) / 40f) : 0f;
            width = weight > 0f ? 80f : 0f;
        }
        public override int GetSeed() => 78;
    }

    /// <summary>
    /// A marked manual road stores exactly the crossings it was checked with (RoadNetworkGenerator.WrittenRoad.Stored, the
    /// crossings a generated road stores of its own, RoadNetworkGenerator.StoredCrossings), as generation along the same
    /// line stores them: a road laid before it through the river along the same line carries the crossing there, so neither
    /// stores one. On the parent the marked road stored a bridge its checks never read (a filter of its own, by banks). Now a
    /// bridge's levelled end lying on that road a step off it refuses either (the apron rule judges a road that rides another
    /// as any): the generated road goes round on a bridge of its own, the marked road is refused.
    /// </summary>
    [Fact]
    public void AManualRoadStoresTheCrossingsItWasCheckedWith()
    {
        var world = new River();
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady();
        void Trunk()
        {
            // On dry land's floor, as every road on land is built (a road under it, the roads held to it could not meet it).
            RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(-120f, 0f), new(120f, 0f) }, 4f, world, minHeight: RoadConstants.DryRoadFloor);
            RoadSpatialGrid.FinalizeRoadNetwork();
            RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        }
        Trunk();
        SetPathfinder(RoadNetworkGenerator.NewFinder(world, 4f));
        bool generated = RoadNetworkGenerator.GenerateRoad(new Vector3(-100f, 0f, 0f), 0f, new Vector3(100f, 0f, 0f), 0f, 4f, "along");
        var generatedCrossings = RoadNetworkGenerator.GetRoadCrossings().ToList();
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.MarkLocationsReady();
        Trunk();

        Assert.True(generated, RoadSpatialGrid.LastRefusal);
        // Along the trunk its bridge's levelled end lies on the trunk 0.21 m off its surface, past a walker's step: refused for
        // it (BRIEF-RULES 17), riding or not. The generated road goes round, its own bridge off the trunk's line.
        Assert.All(generatedCrossings, c => Assert.True(Mathf.Abs(c.Center.y) > 4f, $"{c} on the trunk"));
        // The marked road, which cannot go round its marks, is refused for the same step.
        var refused = Assert.Throws<InvalidOperationException>(() =>
            ManualRoads.Prepare(new[] { new Vector2(-100f, 0f), new Vector2(100f, 0f) }, false, new[] { Everywhere() }));
        Assert.Contains(RoadSpatialGrid.LevelledOnARoadRefusal, refused.Message);
    }
}
