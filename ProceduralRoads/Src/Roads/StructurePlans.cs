using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The structure plans of the current network -- the pieces the network
/// builds out of vanilla prefabs, bridges today -- bucketed by zone, and the
/// record of which zones have received their pieces. The one owner of
/// structures: placement, appends, the server bake and the zone report all
/// read from here. Plans are recomputed deterministically from the network
/// and the world seed, but for the structures saved with it (a staircase, a
/// bridge whose first zone was built: BuiltBridges); the spawned-zone set is persisted with
/// the network (see RoadNetworkPersistence; its saved key and the pieces'
/// ZDO marker keep their bridge names, so existing worlds load unchanged), so
/// a zone whose pieces were all destroyed does not get them again on the next
/// load. Pure logic: the game-facing spawning lives in StructurePlacement.
/// </summary>
public static class StructurePlans
{
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;

    /// <summary>Every kind of structure the network builds. Each plans its own
    /// pieces; everything after that is shared.</summary>
    public static readonly IReadOnlyList<IStructureSource> Sources = new IStructureSource[] { new BridgeStructures(), new StairStructures() };

    private static readonly object s_planGate = new();
    private static HashSet<StairRun>? s_buildableStaircases;
    private static Dictionary<Vector2s, List<StructurePiece>>? s_plansByZone;
    /// <summary>The same pieces, by source and then zone: clear areas are each source's own business.</summary>
    private static Dictionary<IStructureSource, Dictionary<Vector2s, List<StructurePiece>>>? s_plansBySource;
    private static readonly HashSet<Vector2s> s_spawnedZones = new();

    /// <summary>Zones that have received their bridge pieces.</summary>
    public static IReadOnlyCollection<Vector2s> SpawnedZones => s_spawnedZones;

    /// <summary>Forget the plans and the spawned zones: the network is gone.</summary>
    public static void Reset()
    {
        InvalidatePlans();
        s_spawnedZones.Clear();
    }

    /// <summary>Forget the plans only; they are recomputed on the next use.</summary>
    public static void InvalidatePlans()
    {
        lock (s_planGate)
        {
            s_plansByZone = null;
            s_plansBySource = null;
            ForgetBuildableStaircases();
        }
    }

    /// <summary>Protection changed while the plans stayed the same: rebuild their buildable inventory on its next read.</summary>
    internal static void ForgetBuildableStaircases()
    {
        lock (s_planGate) s_buildableStaircases = null;
    }

    /// <summary>All emitted pieces are being destroyed for a full respawn.
    /// Append acknowledgements refer to those same pieces; keeping only the
    /// outstanding remainder would suppress the complete plan on respawn.</summary>
    public static void ForgetSpawned()
    {
        s_spawnedZones.Clear();
        StructureAppendQueue.Reset();
    }

    /// <summary>These zones' emitted pieces are being destroyed for a partial respawn: they and what their appends owe are
    /// forgotten, so each gets its whole plan again.</summary>
    public static void ForgetSpawned(ICollection<Vector2s> zones)
    {
        s_spawnedZones.ExceptWith(zones);
        StructureAppendQueue.Forget(zones);
    }

    public static bool IsSpawned(Vector2s zone) => s_spawnedZones.Contains(zone);

    public static void MarkSpawned(Vector2s zone) => s_spawnedZones.Add(zone);

    public static void MarkSpawned(IEnumerable<Vector2s> zones)
    {
        foreach (Vector2s zone in zones)
            s_spawnedZones.Add(zone);
    }

    /// <summary>The marker a spawned bridge piece carries in its ZDO.</summary>
    public static readonly int MarkerHash = "ProceduralRoads_Bridge".GetStableHashCode();

    /// <summary>The key a piece an append put out carries in its ZDO besides <see cref="MarkerHash"/>
    /// (StructureAppendQueue.Key): what the append owes is told from what stands by it.</summary>
    internal static readonly int AppendKeyHash = "ProceduralRoads_AppendPiece".GetStableHashCode();

    /// <summary>
    /// Whether the zone still holds bridge pieces that are staying: markers of
    /// pieces already condemned (<paramref name="condemned"/>) are not counted,
    /// since a destroyed ZDO is only queued for removal. With <paramref name="laidByPlan"/>, only a piece its plan laid
    /// counts (the plan stands, its record lost), not one an append put out (<see cref="AppendKeyHash"/>): an append's pieces
    /// are its own work, not the zone's plan. No side effect: the caller records the zone as built.
    /// </summary>
    public static bool ZoneHasLivePieces(Vector2s zoneID, ICollection<ZDOID>? condemned = null, bool laidByPlan = false)
    {
        if (ZDOMan.instance == null)
            return false;
        var zdos = new List<ZDO>();
        ZDOMan.instance.FindObjects(zoneID, zdos, new HashSet<ZoneSystem.SectorIndex>());
        foreach (ZDO zdo in zdos)
        {
            if (zdo.GetInt(MarkerHash) != 1)
                continue;
            if (condemned != null && condemned.Contains(zdo.m_uid))
                continue;
            if (laidByPlan && zdo.GetString(AppendKeyHash, "").Length > 0)
                continue;
            return true;
        }
        return false;
    }

    /// <summary>Whether the zone's structure is started: recorded as spawned, owed pieces by an append
    /// (<see cref="StructureAppendQueue"/>), or holding live pieces (<see cref="ZoneHasLivePieces"/>). The one owner of the
    /// three facts, for <see cref="StructureSites.KeepFirstBuilt"/> and a manual road's append. The one owner of placing pieces into zones, and whether a zone's structure is started (docs/OWNERS.md).</summary>
    public static bool Started(Vector2s zone, ICollection<ZDOID>? condemned = null) =>
        IsSpawned(zone) || StructureAppendQueue.ForZone(zone) != null || ZoneHasLivePieces(zone, condemned);

    /// <summary>The pieces planned for a zone, or null when it has none.</summary>
    public static List<StructurePiece>? PlanFor(Vector2s zone)
    {
        EnsurePlans();
        return s_plansByZone != null && s_plansByZone.TryGetValue(zone, out List<StructurePiece>? list) ? list : null;
    }

    /// <summary>
    /// The pieces planned for a zone that may be built now, or null when there are none. A bridge or staircase whose body
    /// reaches (<see cref="ZonesOf"/>: any of its pieces' bodies, ends, piers, deck or stairs; debris on the riverbed does not
    /// count) a zone NetworkCutoff leaves as it is is not built at all, in any zone: no half structure appears, and the road
    /// there is left without it. Asked at spawn time, so if those zones stop being excluded the whole structure can be built.
    /// </summary>
    public static List<StructurePiece>? BuildablePlanFor(Vector2s zone) => Buildable(PlanFor(zone));

    /// <summary>The pieces of a list that may be built now (see BuildablePlanFor), or null when none may. The one owner of
    /// whether a structure may be built in the zones the network leaves alone (docs/OWNERS.md).</summary>
    internal static List<StructurePiece>? Buildable(List<StructurePiece>? pieces)
    {
        if (pieces == null || NetworkCutoff.ExcludedCount == 0)
            return pieces;
        List<StructurePiece>? buildable = null;
        for (int i = 0; i < pieces.Count; i++)
        {
            HashSet<Vector2s>? zones = pieces[i].StructureZones;
            bool whole = zones == null || !ReachesExcludedZone(zones);
            if (buildable != null) { if (whole) buildable.Add(pieces[i]); }
            else if (!whole) buildable = pieces.GetRange(0, i);
        }
        if (buildable == null)
            return pieces;
        return buildable.Count > 0 ? buildable : null;
    }

    /// <summary>The staircases with at least one piece the current plan may lay, through <see cref="Buildable"/>.
    /// Read every planned zone: a buried flight can have no pieces near its stored walking boxes while the run lays
    /// pieces elsewhere. Empty dressings (refused or waiting) and structures withheld whole contribute no run.</summary>
    internal static HashSet<StairRun> BuildableStaircases()
    {
        lock (s_planGate)
        {
            EnsurePlans();
            if (s_buildableStaircases != null) return s_buildableStaircases;
            var runs = new HashSet<StairRun>();
            if (s_plansByZone == null) return runs;
            foreach (List<StructurePiece> planned in s_plansByZone.Values)
                if (Buildable(planned) is { } pieces)
                    foreach (StructurePiece piece in pieces)
                        if (piece.Staircase is StairRun run) runs.Add(run);
            return s_buildableStaircases = runs;
        }
    }

    /// <summary>
    /// Every zone a structure's pieces stand in by their bodies, not their origins (<see cref="BodyZones"/>): the zones it
    /// is held back by where the network leaves one as it is (<see cref="StructurePiece.StructureZones"/>,
    /// <see cref="BuildablePlanFor"/>), and the zones a road's final check reads its structures by (ManualRoads.ZonesReached).
    /// Debris on the riverbed is not part of a bridge: it never holds one back (<paramref name="debris"/> false), though it is
    /// laid where it lies (true: every zone the structure writes). The one owner of placing pieces into zones (docs/OWNERS.md).
    /// </summary>
    internal static HashSet<Vector2s> ZonesOf(IEnumerable<StructurePiece> structure, bool debris = false)
    {
        var zones = new HashSet<Vector2s>();
        foreach (StructurePiece piece in structure)
            if (debris || piece.Kind != StructurePieceKind.Debris) BodyZones(piece, zones);
        return zones;
    }

    /// <summary>The zones one piece's body stands in, into <paramref name="zones"/>: its measured box
    /// (<see cref="BuriedPieces.BoundsOf"/>) turned as it is laid (<see cref="StructurePlacement.Rotation"/>), across x and z; a piece whose bounds
    /// are not measured, by its origin.</summary>
    internal static void BodyZones(StructurePiece piece, ISet<Vector2s> zones)
    {
        if (!BodyBox(piece, out Vector2 low, out Vector2 high))
        {
            zones.Add(ZoneSystem.GetZone(piece.Position));
            return;
        }
        NetworkCutoff.ZonesOver(low, high, zones);
    }

    /// <summary>The ground one piece's body covers, across x and z (<paramref name="low"/> to <paramref name="high"/>): its
    /// measured box (<see cref="BuriedPieces.BoundsOf"/>) turned as it is laid (<see cref="StructurePlacement.Rotation"/>).
    /// False for a piece whose bounds are not measured (its origin stands for it). The one body the zones it is held back by
    /// (<see cref="BodyZones"/>) and the locations it keeps out of (RoadLocationProtection.StructureEnters) read.</summary>
    internal static bool BodyBox(StructurePiece piece, out Vector2 low, out Vector2 high)
    {
        low = high = new Vector2(piece.Position.x, piece.Position.z);
        if (!BuriedPieces.BoundsOf(piece.Prefab, out Vector3 min, out Vector3 max)) return false;
        Quaternion turn = StructurePlacement.Rotation(piece);
        low = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        high = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 at = piece.Position + turn * new Vector3((corner & 1) == 0 ? min.x : max.x, (corner & 2) == 0 ? min.y : max.y,
                (corner & 4) == 0 ? min.z : max.z);
            low = new Vector2(Mathf.Min(low.x, at.x), Mathf.Min(low.y, at.z));
            high = new Vector2(Mathf.Max(high.x, at.x), Mathf.Max(high.y, at.z));
        }
        return true;
    }

    /// <summary>A structure held back whole by its bodies' zones (<see cref="ZonesOf"/>): one set shared by its pieces.</summary>
    internal static void HoldWhole(List<StructurePiece> structure)
    {
        HashSet<Vector2s> zones = ZonesOf(structure);
        foreach (StructurePiece piece in structure) piece.StructureZones = zones;
    }

    /// <summary>
    /// The planned pieces of every structure <paramref name="admitted"/> lets be built (NetworkCutoff.Admit): one whose bodies
    /// reach an admitted zone and no zone the network still leaves as it is. It was held back whole until now
    /// (<see cref="BuildablePlanFor"/>), so a zone built meanwhile never got its part of it: the caller owes it whole
    /// (ManualRoads.AppendManualRoad). One that still reaches a zone left as it is stays held back whole.
    /// </summary>
    internal static List<StructurePiece> ReleasedBy(ICollection<Vector2s> admitted)
    {
        var released = new List<StructurePiece>();
        if (admitted.Count == 0) return released;
        EnsurePlans();
        if (s_plansByZone == null) return released;
        foreach (List<StructurePiece> plan in s_plansByZone.Values)
            foreach (StructurePiece piece in plan)
                if (piece.StructureZones is HashSet<Vector2s> zones && zones.Overlaps(admitted) && !ReachesExcludedZone(zones))
                    released.Add(piece);
        return released;
    }

    private static bool ReachesExcludedZone(HashSet<Vector2s> zones)
    {
        foreach (Vector2s zone in zones)
            if (NetworkCutoff.Excludes(zone))
                return true;
        return false;
    }

    public static int PlannedPieceCount(Vector2s zone) => PlanFor(zone)?.Count ?? 0;

    public static int TotalPlannedPieces
    {
        get
        {
            EnsurePlans();
            int total = 0;
            if (s_plansByZone != null)
                foreach (var kv in s_plansByZone)
                    total += kv.Value.Count;
            return total;
        }
    }

    public static int PlannedZoneCount
    {
        get
        {
            EnsurePlans();
            return s_plansByZone?.Count ?? 0;
        }
    }

    /// <summary>The zones the plans put pieces in (a copy).</summary>
    public static List<Vector2s> PlannedZones
    {
        get
        {
            EnsurePlans();
            return s_plansByZone != null ? new List<Vector2s>(s_plansByZone.Keys) : new List<Vector2s>();
        }
    }

    /// <summary>
    /// The planned zones a respawn has to put pieces back into: every zone the
    /// world has already generated and which is not recorded as spawned. Asked
    /// of the world, not of the zones with a Heightmap: a dedicated server keeps
    /// its players' surroundings as ghost zones with none. A zone not generated
    /// yet gets its pieces when it is generated, from the zone-spawn hook.
    /// </summary>
    public static List<Vector2s> ZonesNeedingPieces(Func<Vector2s, bool> isZoneGenerated)
    {
        List<Vector2s> zones = new();
        foreach (Vector2s zone in PlannedZones)
        {
            if (IsSpawned(zone) || !isZoneGenerated(zone))
                continue;
            zones.Add(zone);
        }
        return zones;
    }

    /// <summary>Squares to keep vegetation out of round a zone's planned pieces, from every source
    /// (<see cref="IStructureSource.AddClearAreas"/>: the staircases').</summary>
    public static List<(Vector3 centre, float radius)> ClearAreas(Vector2s zone)
    {
        var areas = new List<(Vector3 centre, float radius)>();
        EnsurePlans();
        if (s_plansBySource == null)
            return areas;
        foreach (IStructureSource source in Sources)
            if (s_plansBySource.TryGetValue(source, out var byZone) && byZone.TryGetValue(zone, out List<StructurePiece>? planned)
                && Buildable(planned) is { } pieces)
                source.AddClearAreas(pieces, areas);
        return areas;
    }

    /// <summary>
    /// The walking surfaces of every source's structures (<see cref="IStructureSource.AddWalksNear"/>: a bridge's buildable
    /// pieces, a staircase's stored walking boxes, laid or buried) in a zone and its eight neighbours: a structure's walk
    /// reaches across a zone's border, and the vegetation of the zone is judged against it wherever it stands (a tree rooted
    /// in one zone, a deck in the next; the farthest any vegetation reaches, VegetationClearing.TreeReach, is under a zone's
    /// size).
    /// </summary>
    public static List<StairSupport.Walk> WalksNear(Vector2s zone)
    {
        var walks = new List<StairSupport.Walk>();
        EnsurePlans();
        if (s_plansBySource == null)
            return walks;
        List<Vector2s> near = NearZones(zone);
        foreach (IStructureSource source in Sources)
        {
            s_plansBySource.TryGetValue(source, out var byZone);
            source.AddWalksNear(near, z => byZone != null && byZone.TryGetValue(z, out List<StructurePiece>? planned) ? Buildable(planned) : null, walks);
        }
        return walks;
    }

    /// <summary>The neighbourhood generation reads a structure's walk from, also used in reverse to find the zones
    /// whose saved vegetation the bake must judge.</summary>
    private static List<Vector2s> NearZones(Vector2s zone)
    {
        var near = new List<Vector2s>(9);
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                near.Add(new Vector2s(zone.x + dx, zone.y + dy));
        return near;
    }

    /// <summary>The zones generation reads structures' walks in: candidates from planned pieces and stored stair boxes
    /// (buried walks included), in the same neighbourhood as <see cref="WalksNear"/>, kept only when that owner supplies a
    /// walk. Supports alone add no vegetation reach.</summary>
    internal static HashSet<Vector2s> ZonesWithWalks()
    {
        var walkZones = new HashSet<Vector2s>(PlannedZones);
        foreach (StairClearance box in StairStructures.WalkBoxes())
            walkZones.Add(ZoneSystem.GetZone(box.Centre));
        return WithWalks(walkZones);
    }

    /// <summary>The zones generation reads these structures' walks in (<see cref="ZonesWithWalks()"/>, limited to the
    /// structures that changed: a manual road's, or those built once their ground was known): candidates from the zones
    /// their pieces stand in and their staircases' stored walking boxes (those of <paramref name="runs"/> and of any planned
    /// piece's staircase), in the same neighbourhood, kept only when <see cref="WalksNear"/> supplies a walk. Read after the
    /// structures are in the plans: what a zone already cleared without them owes (VegetationClearing.Reached).</summary>
    internal static HashSet<Vector2s> ZonesWithWalks(IEnumerable<StructurePiece> pieces, IEnumerable<StairRun> runs)
    {
        var walkZones = new HashSet<Vector2s>();
        var staircases = new HashSet<StairRun>(runs);
        foreach (StructurePiece piece in pieces)
        {
            walkZones.Add(ZoneSystem.GetZone(piece.Position));
            if (piece.Staircase is StairRun run) staircases.Add(run);
        }
        foreach (StairRun run in staircases)
            foreach (StairClearance box in run.Clearances)
                walkZones.Add(ZoneSystem.GetZone(box.Centre));
        return WithWalks(walkZones);
    }

    /// <summary>The zones round <paramref name="walkZones"/> (<see cref="NearZones"/>, the neighbourhood generation reads a
    /// walk from) whose vegetation <see cref="WalksNear"/> judges against a walk: the one reach of a structure's walk, for the
    /// whole network and for the structures that changed alike.</summary>
    private static HashSet<Vector2s> WithWalks(IEnumerable<Vector2s> walkZones)
    {
        var candidates = new HashSet<Vector2s>();
        foreach (Vector2s zone in walkZones)
            candidates.UnionWith(NearZones(zone));
        candidates.RemoveWhere(zone => WalksNear(zone).Count == 0);
        return candidates;
    }

    /// <summary>A source's buildable planned pieces (<see cref="BuildablePlanFor"/>) whose origin stands within
    /// <paramref name="reach"/> of a point across the ground.</summary>
    public static List<StructurePiece> BuildableNear(IStructureSource source, Vector3 centre, float reach)
    {
        var near = new List<StructurePiece>();
        EnsurePlans();
        if (s_plansBySource == null || !s_plansBySource.TryGetValue(source, out var byZone))
            return near;
        Vector2s low = ZoneSystem.GetZone(centre - new Vector3(reach, 0f, reach)), high = ZoneSystem.GetZone(centre + new Vector3(reach, 0f, reach));
        var c = new Vector2(centre.x, centre.z);
        for (int x = low.x; x <= high.x; x++)
            for (int y = low.y; y <= high.y; y++)
                if (byZone.TryGetValue(new Vector2s(x, y), out List<StructurePiece>? planned) && Buildable(planned) is { } pieces)
                    foreach (StructurePiece piece in pieces)
                        if (Vector2.Distance(new Vector2(piece.Position.x, piece.Position.z), c) <= reach) near.Add(piece);
        return near;
    }

    /// <summary>Where a walker on any structure needs rock cleared, within reach of a point.</summary>
    public static List<StructureClearance> ClearancesNear(Vector3 centre, float radius)
    {
        var clearances = new List<StructureClearance>();
        foreach (IStructureSource source in Sources)
            source.AddClearances(centre, radius, clearances);
        return clearances;
    }

    /// <summary>Whether any structure of any source needs rock cleared within reach of a point.</summary>
    public static bool AnyStructureNear(Vector3 centre, float radius) => ClearancesNear(centre, radius).Count > 0;

    /// <summary>How many times the plans were made (<see cref="EnsurePlans"/>): a load makes them once, not once per location or
    /// zone (they were once made again at every location's ground published, tens of thousands of pieces each time).</summary>
    internal static int Builds;

    private static void EnsurePlans()
    {
        lock (s_planGate)
        {
            if (RoadLifecycleManager.WorkersOwnWorld) throw new InvalidOperationException("Road generation is in progress.");
            if (s_plansByZone != null || WorldGenerator.instance == null)
                return;
            Builds++;

            // Built whole, then published: a source that throws leaves no half-built plan behind for the next caller.
            var plansByZone = new Dictionary<Vector2s, List<StructurePiece>>();
            var plansBySource = new Dictionary<IStructureSource, Dictionary<Vector2s, List<StructurePiece>>>();
            int seed = WorldGenerator.instance.GetSeed();
            foreach (IStructureSource source in Sources)
            {
                var byZone = new Dictionary<Vector2s, List<StructurePiece>>();
                plansBySource[source] = byZone;
                int total = 0;
                foreach (StructurePiece piece in source.Plan(WorldGenerator.instance, seed))
                {
                    Vector2s zone = ZoneSystem.GetZone(piece.Position);
                    if (!plansByZone.TryGetValue(zone, out List<StructurePiece>? list))
                    {
                        list = new List<StructurePiece>();
                        plansByZone[zone] = list;
                    }
                    list.Add(piece);
                    if (!byZone.TryGetValue(zone, out List<StructurePiece>? own))
                    {
                        own = new List<StructurePiece>();
                        byZone[zone] = own;
                    }
                    own.Add(piece);
                    total++;
                }
                if (total > 0)
                    Log.LogInfo($"{source.LogTag} planned {total} pieces across {byZone.Count} zones");
            }
            if (SearchStats.Enabled) Log.LogInfo($"[BRIDGES] site solves: {BridgeStructures.SolveTimes.Summary()}");
            s_plansBySource = plansBySource;
            s_plansByZone = plansByZone;
        }
    }
}
