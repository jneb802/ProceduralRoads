using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A network this build generates and saves is read back by this same build. Generated whole, on a world with
/// a wading ford, a bridge and a ridge climbed by two staircases, saved through the metadata ZDO and loaded
/// through the same door a world load uses.
///
/// Every world generated with a staircase was refused at its next load as "a saved road
/// network this build cannot read", and so had no roads. A network with a staircase's cuts carries the append block
/// whether or not anything was appended; the reader still demanded at least one append there. Two such worlds (5479632 and 5579723 bytes) both failed on exactly that check, and a modded client refused the
/// server's snapshot of such a network on the same check.
/// </summary>
public sealed class NetworkRoundTripTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadNetworkPersistence.Reset();
        _scope.Dispose();
    }

    /// <summary>Meadows at 33 m inside 400 m, sea beyond. A knee-deep gully at x = -150 (a ford), a river
    /// 11 m deep and 40 m wide at x = -280 (a bridge), and a ridge along z at x = +120: faces of 1.2 rising
    /// 24 m to a 32 m top, too steep for road, so it is climbed on one staircase and descended on another.</summary>
    private sealed class MixedWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 400f || Mathf.Abs(wy) > 400f) return 0f;
            float h = 33f;
            float gully = Mathf.Abs(wx + 150f);
            if (gully < 22f) h = Mathf.Lerp(29.5f, 33f, Mathf.Clamp01((gully - 10f) / 12f));
            if (Mathf.Abs(wx + 280f) < 20f) h = 22f;
            return h + Mathf.Clamp(24f - (Mathf.Abs(wx - 120f) - 16f) * 1.2f, 0f, 24f);
        }

        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            GetHeight(wx, wy) < RoadConstants.SeaLevel - 2f ? Heightmap.Biome.Ocean : Heightmap.Biome.Meadows;

        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            float gully = Mathf.Abs(wx + 150f), river = Mathf.Abs(wx + 280f);
            if (gully < 24f) { weight = Mathf.Clamp01(1f - gully / 24f); width = 48f; return; }
            if (river < 40f) { weight = Mathf.Clamp01(1f - river / 40f); width = 80f; return; }
            weight = 0f; width = 0f;
        }

        public override int GetSeed() => 77;
    }

    /// <summary>The mixed world's network, generated: its four locations, then the whole generation.</summary>
    private MixedWorld Generate()
    {
        var world = new MixedWorld();
        _scope.WithWorld(world).WithZoneSystem();
        foreach ((string name, float x, float z, float radius) in new[]
                 {
                     ("StartTemple", -60f, 0f, 25f), ("Eikthyrnir", 260f, 30f, 10f),
                     ("Crypt4", -360f, 60f, 18f), ("Crypt4", -220f, -200f, 18f),
                 })
            TestLocations.Add(ZoneSystem.instance!, new ZoneSystem.LocationInstance
            {
                m_location = new ZoneSystem.ZoneLocation { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(name), m_exteriorRadius = radius },
                m_position = new Vector3(x, world.GetHeight(x, z), z),
            });
        _scope.WithZdos().WithNetwork(server: true);
        RoadNetworkGenerator.Reset();

        RoadNetworkGenerator.GenerateRoads(force: true);
        return world;
    }

    /// <summary>What the two whole-network gates below read of the mixed world's network, generated once for both: its
    /// staircases, the staircase-end audit (StairEnds.AuditAll) and the twins (LocationWaysIn.Twins), all read while the network
    /// stands. Each gate generated the same network again for itself, a second or two each.</summary>
    private static (int stairs, List<(StairEnds.Verdict verdict, string line)> lines, int ok, int miss, int gap, int unverified,
        int unfinished, List<string> twins, Dressed dressed, List<string> onOwnRoad)? s_gates;

    /// <summary>The network's stored road points, read with the gates above while the network stands.</summary>
    private static List<RoadSpatialGrid.RoadPoint> s_roadPoints = new();

    /// <summary>What the network's staircases lay, dressed by their kits on the finished ground as their first build lays them
    /// (StairStructures.Dressing, StairKit.Dress): the pieces laid, how many of them are wholly in that ground
    /// (BuriedPieces.Buried), and each laid piece both wholly in the ground and read by the support rule as falling
    /// (StairSupport.Holding), one line each.</summary>
    private sealed record Dressed(int laid, int buried, List<string> buriedAndFalling);

    private (int stairs, List<(StairEnds.Verdict verdict, string line)> lines, int ok, int miss, int gap, int unverified, int unfinished, List<string> twins,
        Dressed dressed, List<string> onOwnRoad) Gates()
    {
        if (s_gates is { } read) return read;
        MixedWorld world = Generate();
        var (lines, ok, miss, gap, unverified, unfinished) = StairEnds.AuditAll(run => StairEnds.GroundOf(run, world));
        s_roadPoints = RoadSpatialGrid.SnapshotAllRoadPoints();
        s_gates = (StairStructures.Runs.Count, lines, ok, miss, gap, unverified, unfinished, LocationWaysIn.Twins().Select(t => t.line).ToList(),
            Dress(world), OnTheirOwnRoads());
        return s_gates.Value;
    }

    private static Dressed Dress(WorldGenerator world)
    {
        Func<int, int, float> finished = StructureGround.Vertices(world);
        int laid = 0, buried = 0;
        var both = new List<string>();
        foreach (StairRun run in StairStructures.Runs)
        {
            List<StructurePiece> pieces = StairStructures.Dressing(run, world, finished, new StairSupport.Shortfall(), true);
            laid += pieces.Count;
            var falling = new HashSet<StructurePiece>(StairSupport.Holding(pieces, finished).Where(h => h.Falls).Select(h => h.Piece));
            foreach (StructurePiece p in pieces)
            {
                if (p.RollDegrees != 0f || !BuriedPieces.Buried(p.Prefab, p.Position, p.YawDegrees, finished, p.PitchDegrees)) continue;
                buried++;
                if (falling.Contains(p))
                    both.Add(FormattableString.Invariant($"{p.Kind} {p.Prefab} at ({p.Position.x:F1}, {p.Position.y:F2}, {p.Position.z:F1}) of the staircase {run.From} -> {run.To}"));
            }
        }
        return new Dressed(laid, buried, both);
    }

    /// <summary>Each walked piece of a staircase standing on the road that meets one of its ends (StairRun.FromRoad, ToRoad: its
    /// centre within RoadStairRuns.OnRoad of that road's line) past the piece that joins it there (further than
    /// RoadStairRuns.JoiningStep from the staircase's own end there, <see cref="OwnEnd"/>), but a floor the road is carried over level with it (within a step of the
    /// road's stored surface there: its end floors), one line each: the rule a staircase's layout keeps to
    /// (RoadStairRuns.OverRoad), read on the network as it stands.</summary>
    private static List<string> OnTheirOwnRoads()
    {
        var lines = new List<string>();
        foreach (StairRun run in StairStructures.Runs)
            foreach (var (end, road) in new[] { (run.From, run.FromRoad), (run.To, run.ToRoad) })
            {
                if (road == null || road.Points.Count < 2) continue;
                RoadSpatialGrid.RoadPoint[]? stored = road.Stored();
                Vector2 own = OwnEnd(run.Pieces, end);
                foreach (StructurePiece p in run.Pieces)
                {
                    if (p.Kind is not (StructurePieceKind.StairStep or StructurePieceKind.StairFloor)) continue;
                    var at = new Vector2(p.Position.x, p.Position.z);
                    if (Vector2.Distance(at, own) <= RoadStairRuns.JoiningStep) continue;
                    for (int i = 1; i < road.Points.Count; i++)
                        if (RoadStairRuns.DistanceToSegment(at, road.Points[i - 1], road.Points[i]) < RoadStairRuns.OnRoad)
                        {
                            // A floor the road is carried over, level with it (RoadStairRuns.CarryRoadOntoStairs), is where they join.
                            if (p.Kind == StructurePieceKind.StairFloor && stored != null
                                && Mathf.Min(Mathf.Abs(stored[i - 1].h - RoadStairRuns.Surface(p)), Mathf.Abs(stored[i].h - RoadStairRuns.Surface(p))) <= StairLayout.StepUp)
                                break;
                            lines.Add(FormattableString.Invariant($"{p.Kind} at ({at.x:F1}, {p.Position.y:F2}, {at.y:F1}) walk {RoadStairRuns.Surface(p):F2}, {Vector2.Distance(at, own):F1} m from its end at ({own.x:F1}, {own.y:F1}), on that end's road (point {i}: {(stored == null ? "not stored" : stored[i].h.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))}; run {run.From}->{run.To}, {run.Pieces.Count(q => q.Kind == StructurePieceKind.StairFloor)} floors)"));
                            break;
                        }
                }
            }
        return lines;
    }

    /// <summary>A staircase's own end nearest <paramref name="end"/> (a run's From or To): the outer edge's middle of its first
    /// walked piece from there (a foot's sunk step's low edge), where its layout measures the joining step from
    /// (RoadStairRuns.OverRoad: the piece within RoadStairRuns.JoiningStep of the point the staircase is laid from). A run's
    /// recorded end at a road's end is the centre of the ring the road sets off from (RoadStairRuns.RoadEndAt), a ring's radius
    /// or more out past the foot and to the side a turned foot is set on: measured from there, a joining step was read on its
    /// own road.</summary>
    internal static Vector2 OwnEnd(IReadOnlyList<StructurePiece> pieces, Vector2 end)
    {
        StructurePiece first = pieces.Where(p => p.Kind is StructurePieceKind.StairStep or StructurePieceKind.StairFloor)
            .OrderBy(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), end)).First();
        var (back, heading, _, depth, _) = StairLayout.Footprint(StairRun.PieceOf(first));
        Vector2 front = back + StairLayout.Forward(heading) * depth;
        return Vector2.Distance(back, end) <= Vector2.Distance(front, end) ? back : front;
    }

    /// <summary>A generated foot (a 54-staircase network's, its road ending level with the step it joins at 32.50): the sunk
    /// step, the joining step at the road's level and the stair on up. Its recorded end, the centre of the ring its road set
    /// off from, stood 3.3 m from the joining step's middle, past RoadStairRuns.JoiningStep (3.2), and the joining step was
    /// read on its own road. From the staircase's own end (the sunk step's low edge) it stands 3 m in, the stair on up 5 m.</summary>
    [Fact]
    public void AStaircasesJoiningStepIsMeasuredFromItsOwnFoot()
    {
        StructurePiece Step(float x, float y, float z) => new() { Kind = StructurePieceKind.StairStep, Prefab = "blackmarble_stair",
            Position = new Vector3(x, y, z), YawDegrees = 45f };
        var pieces = new List<StructurePiece> { Step(8902.18f, 31.5f, -383.28f), Step(8900.77f, 32.5f, -384.69f), Step(8899.35f, 33.5f, -386.11f) };
        // The ring's centre: a ring's radius out past the foot and to the side, as the end was recorded.
        var ring = new Vector2(8903.6f, -383.1f);
        Assert.True(Vector2.Distance(new Vector2(8900.77f, -384.69f), ring) > RoadStairRuns.JoiningStep);
        Vector2 own = OwnEnd(pieces, ring);
        Assert.True(Vector2.Distance(own, new Vector2(8902.89f, -382.57f)) < 0.02f, $"own end at {own}");
        Assert.True(Vector2.Distance(new Vector2(8900.77f, -384.69f), own) <= RoadStairRuns.JoiningStep);
        Assert.True(Vector2.Distance(new Vector2(8899.35f, -386.11f), own) > RoadStairRuns.JoiningStep);
    }

    /// <summary>The staircase-end gate (StairEnds, the one end reader; tools' ends check reads the same lines from a whole
    /// world's generation): every end of every staircase the generation lays has a way off it, read on its finished ground.
    /// A miss (a top with nothing laid past its last stair) or a gap (no edge stepped off) fails with its line, coordinates
    /// and all.</summary>
    [Fact]
    public void EveryStaircaseEndOfTheGeneratedNetworkHasAWayOff()
    {
        var (stairs, lines, ok, miss, gap, unverified, unfinished, _, _, _) = Gates();
        Assert.True(stairs >= 2, $"{stairs} staircase(s); the ridge should take two");
        Assert.True(miss + gap + unverified + unfinished == 0, $"ends: {ok} ok, {miss} miss, {gap} gap, {unverified} not verified, {unfinished} unfinished\n"
            + string.Join("\n", lines.Where(l => l.verdict != StairEnds.Verdict.Ok || l.line.Contains(" UNFINISHED: ")).Select(l => l.line)));
        Assert.Equal(2 * stairs, ok);
    }

    /// <summary>The grade cap gate beside it (tools' ends check reads the same over a whole world's exported roads): no two
    /// stored road points 0.8-1.5 m apart (one or two of a road's ~1 m steps, the length RoadGrade.Limit holds to the cap)
    /// climb past RoadGrade.Configured. Exempt as not road: a ford's paint-only points (the river bed), staircases' cuts and
    /// structures' mounds (RoadSpatialGrid.RoadPoint.IsRoad). A pair past it fails with its coordinates.</summary>
    [Fact]
    public void NoStoredRoadOfTheGeneratedNetworkClimbsPastTheGradeCap()
    {
        Gates();
        var road = s_roadPoints.Where(q => q.IsRoad).ToList();
        Assert.True(road.Count > 100, $"{road.Count} road points");
        var cells = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < road.Count; i++)
        {
            var key = (Mathf.FloorToInt(road[i].p.x / 2f), Mathf.FloorToInt(road[i].p.y / 2f));
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>();
            list.Add(i);
        }
        var past = new List<string>();
        for (int i = 0; i < road.Count; i++)
        {
            var (cx, cz) = (Mathf.FloorToInt(road[i].p.x / 2f), Mathf.FloorToInt(road[i].p.y / 2f));
            for (int gx = cx - 1; gx <= cx + 1; gx++)
                for (int gz = cz - 1; gz <= cz + 1; gz++)
                {
                    if (!cells.TryGetValue((gx, gz), out var list)) continue;
                    foreach (int j in list)
                    {
                        if (j <= i) continue;
                        float run = Vector2.Distance(road[i].p, road[j].p);
                        if (run < 0.8f || run > 1.5f) continue;
                        float grade = Mathf.Abs(road[j].h - road[i].h) / run;
                        if (grade > RoadGrade.Configured + RoadSpatialGrid.HeldCapMargin)
                            past.Add($"{grade:P0} from ({road[i].p.x:F1}, {road[i].p.y:F1}) to ({road[j].p.x:F1}, {road[j].p.y:F1})");
                    }
                }
        }
        Assert.True(past.Count == 0, $"{past.Count} stored road step(s) past the {RoadGrade.Configured:P0} cap:\n" + string.Join("\n", past.Take(20)));
    }

    /// <summary>The twin gate beside it (LocationWaysIn.Twins, the rule's own test over the whole network; tools' ends check
    /// reads its "twins: N" line from a whole world's generation): no location the generation reaches has two ways in arriving
    /// the same way. A twin fails with its line, its location and both ways' ends.</summary>
    [Fact]
    public void NoLocationOfTheGeneratedNetworkHasTwoWaysInUpOneFace()
    {
        List<string> twins = Gates().twins;
        Assert.True(twins.Count == 0, $"twins: {twins.Count}\n" + string.Join("\n", twins));
    }

    /// <summary>The buried-piece gate on the generated network, dressed as its first build lays it: no laid piece is both
    /// wholly in the finished ground and read by the support rule as falling (BuriedPieces.Omit leaves such a piece out:
    /// the game grounds no wholly buried piece, and one nothing carries breaks into rock litter). Offline maps read
    /// staircases before they are dressed; this reads what is laid.</summary>
    [Fact]
    public void NoLaidPieceOfTheDressedNetworkIsWhollyInTheGroundAndFalling()
    {
        var (stairs, _, _, _, _, _, _, _, dressed, _) = Gates();
        Assert.True(dressed.laid > stairs, $"{dressed.laid} piece(s) laid over {stairs} staircase(s)");
        Assert.True(dressed.buriedAndFalling.Count == 0, $"{dressed.buriedAndFalling.Count} of {dressed.buried} buried piece(s) laid and falling\n"
            + string.Join("\n", dressed.buriedAndFalling));
    }

    /// <summary>The own-road gate on the generated network: no walked piece of a staircase stands on the road that meets one
    /// of its ends past the piece joining it there (a staircase across or along its own road blocks it).</summary>
    [Fact]
    public void NoStaircaseOfTheGeneratedNetworkStandsOnItsOwnRoad()
    {
        var (stairs, _, _, _, _, _, _, _, _, onOwnRoad) = Gates();
        Assert.True(StairStructures.Runs.Count == 0 || StairStructures.Runs.Any(r => r.FromRoad != null || r.ToRoad != null) || stairs == 0,
            "no staircase records the road meeting it");
        Assert.True(onOwnRoad.Count == 0, $"{onOwnRoad.Count} walked piece(s) on their own road\n" + string.Join("\n", onOwnRoad));
    }

    [Fact]
    public void AGeneratedNetworkWithFordsBridgesAndStaircasesLoadsBackAsItWasSaved()
    {
        Generate();

        // What makes this network realistic, so the test proves something: every kind of point and structure.
        List<RoadSpatialGrid.RoadPoint> points = RoadSpatialGrid.SnapshotAllRoadPoints();
        Assert.Contains(points, p => p.IsRoad);
        Assert.Contains(points, p => p.cutOnly);           // a staircase's earthwork
        Assert.Contains(points, p => p.paintOnly);         // a waded ford
        Assert.DoesNotContain(points, p => p.addition > 0); // generated, nothing appended: the case that broke
        Assert.True(StairStructures.Runs.Count >= 2, $"{StairStructures.Runs.Count} staircase(s); the ridge should take two");
        List<RoadCrossing> crossings = RoadNetworkGenerator.GetRoadCrossings().ToList();
        Assert.Contains(crossings, c => c.Kind == CrossingKind.Ford);
        Assert.Contains(crossings, c => c.Kind == CrossingKind.Bridge);

        byte[] grid = RoadSpatialGrid.SerializeAllRoadPoints()!;
        int version = RoadSpatialGrid.RoadNetworkVersion;
        int stairs = StairStructures.Runs.Count;
        int stairPieces = StairStructures.Runs.Sum(r => r.Pieces.Count);
        Assert.Equal(4, BitConverter.ToInt32(grid, 0));
        Assert.Equal(RoadSpatialGrid.SavedEncoding, BitConverter.ToInt32(grid, 4));
        Assert.Contains(RoadSpatialGrid.SnapshotAllRoadPoints(), p => p.cutOnly);

        RoadNetworkGenerator.SaveGlobalRoadData();
        RoadNetworkGenerator.Reset();
        Assert.Equal(0, RoadSpatialGrid.StoredPointRecords);
        Assert.Empty(StairStructures.Runs);

        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData(),
            $"the network this build saved did not load back: {RoadSpatialGrid.LastReadRefusal}");
        Assert.False(RoadNetworkPersistence.LastLoadUnreadable);
        Assert.Null(RoadSpatialGrid.LastReadRefusal);
        Assert.Equal(grid, RoadSpatialGrid.SerializeAllRoadPoints());
        Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
        Assert.Equal(crossings.Count, RoadNetworkGenerator.GetRoadCrossings().Count);
        Assert.Equal(stairs, StairStructures.Runs.Count);
        Assert.Equal(stairPieces, StairStructures.Runs.Sum(r => r.Pieces.Count));
    }
    /// <summary>A world load builds the structure plans once: the generated network with its locations saved and loaded back,
    /// then each location's ground published as a load places it (PlacedLocations.Preload) and every planned zone read as its spawn
    /// reads it (StructurePlans.BuildablePlanFor), location after location. A load once made the plans again at every location's ground
    /// published, tens of thousands of pieces each time, and froze the loading hold.</summary>
    [Fact]
    public void AWorldLoadBuildsTheStructurePlansOnce()
    {
        MixedWorld world = Generate();
        Assert.NotEmpty(StairStructures.Runs);
        RoadNetworkGenerator.SaveGlobalRoadData();
        RoadNetworkGenerator.Reset();
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData(), RoadSpatialGrid.LastReadRefusal);
        RoadNetworkGenerator.MarkRoadsLoadedFromZDO();

        int before = StructurePlans.Builds;
        List<Vector2s> zones = StructurePlans.PlannedZones;
        Assert.NotEmpty(zones);
        Assert.Equal(before + 1, StructurePlans.Builds);
        var locations = new[] { new Vector2(-60f, 0f), new Vector2(260f, 30f), new Vector2(-360f, 60f), new Vector2(-220f, -200f) };
        foreach (Vector2 location in locations)
        {
            PlacedLocations.Preload(location, (x, z) => world.GetHeight(x, z));
            foreach (Vector2s zone in zones) StructurePlans.BuildablePlanFor(zone);
            StructurePlans.ClearAreas(zones[0]);
            StructurePlans.WalksNear(zones[0]);
        }
        Assert.Equal(before + 1, StructurePlans.Builds);
    }

    /// <summary>Every crossing of the generated network is one the record holds (RoadNetworkPersistence.CrossingRecordFault),
    /// and saved through the metadata ZDO each one loads back. A crossing the record cannot hold (one of no span, as a road
    /// turning back in a pool once made) is not saved, and the others load without it: one bad crossing once made the load
    /// refuse the whole record, and the world lost every crossing at each restart.</summary>
    [Fact]
    public void EveryCrossingOfTheGeneratedNetworkLoadsBackAndOneOfNoSpanTakesNoOtherWithIt()
    {
        Generate();
        var registry = (List<RoadCrossing>)RoadNetworkGenerator.GetRoadCrossings();
        Assert.NotEmpty(registry);
        Assert.All(registry, c => Assert.Null(RoadNetworkPersistence.CrossingRecordFault(c)));
        int made = registry.Count;
        var noSpan = RoadCrossing.Between(new Vector2(-150f, 40f), new Vector2(-150f, 40f), 29.5f, new Vector2(-148f, 40f), 0f,
            CrossingKind.Ford, FordStyle.Wade);
        noSpan.Shallow = true;
        registry.Add(noSpan);

        RoadNetworkGenerator.SaveGlobalRoadData();
        RoadNetworkGenerator.Reset();
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData(), RoadSpatialGrid.LastReadRefusal);
        Assert.False(RoadNetworkPersistence.LastLoadUnreadable);
        Assert.Empty(RoadNetworkPersistence.KeptRecords);
        Assert.Equal(made, RoadNetworkGenerator.GetRoadCrossings().Count);
    }

    /// <summary>Every record the network is saved with, written by this build, reads back through the load and is written
    /// again byte for byte (write -> read parity): the points, start points, crossings, shore landings, bridge zones, owed
    /// pieces, hidden icons, locations placed first, staircases, built bridges and staircases, and the cleared zones.</summary>
    [Fact]
    public void EveryRecordThisBuildSavesReadsBackAndSavesTheSameBytes()
    {
        Generate();
        string[] keys =
        {
            "ProceduralRoads_GlobalData", "ProceduralRoads_StartPoints", "ProceduralRoads_Crossings", "ProceduralRoads_CoastLandings",
            "ProceduralRoads_BridgeZones", "ProceduralRoads_AppendBridges", "ProceduralRoads_HiddenSiteIcons", "ProceduralRoads_PlacedSites",
            "ProceduralRoads_StairRuns", "ProceduralRoads_BuiltBridges", "ProceduralRoads_BuiltStairs", "ProceduralRoads_ClearedZones",
        };
        RoadNetworkGenerator.SaveGlobalRoadData();
        var owed = new Dictionary<Vector2s, int> { [new Vector2s(1, 2)] = 1 };
        RoadNetworkPersistence.SaveClearedZones(RoadSpatialGrid.RoadNetworkVersion, new[] { new Vector2s(0, 0) }, owed);
        var first = keys.ToDictionary(k => k, k => RoadNetworkPersistence.SavedRecord(k));
        Assert.All(keys, k => Assert.True(first[k] is { Length: > 0 }, $"{k} was not saved"));

        RoadNetworkGenerator.Reset();
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData(), RoadSpatialGrid.LastReadRefusal);
        Assert.Empty(RoadNetworkPersistence.KeptRecords);
        RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        foreach (string k in keys) RoadNetworkPersistence.SetSavedRecord(k, Array.Empty<byte>());
        RoadNetworkPersistence.SaveGlobalRoadData(RoadNetworkGenerator.GetRoadStartPoints(), RoadNetworkGenerator.GetRoadCrossings(),
            StructurePlans.SpawnedZones, RoadNetworkGenerator.GetCoastalLandings());
        RoadNetworkGenerator.SaveClearedZones();
        foreach (string k in keys) Assert.True(first[k]!.SequenceEqual(RoadNetworkPersistence.SavedRecord(k)!), $"{k} was not written again as it was read");
    }
}
