using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Orchestrates road network generation after POI locations are known.
/// </summary>
public static partial class RoadNetworkGenerator
{
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;
    public static bool IsGenerating => RoadLifecycleManager.IsGenerating;
    
    /// <summary>The priority of a boss: the top tier of <see cref="LocationPriorities"/>, and a required destination.</summary>
    private const int BossPriority = 100;

    /// <summary>Whether a location is a boss (the only names at <see cref="BossPriority"/>).</summary>
    internal static bool IsBossLocation(string name) => LocationPriorities.TryGetValue(name, out int priority) && priority == BossPriority;

    // Destination priorities for the vanilla locations reviewed on Valheim 1.0.12.
    private static readonly Dictionary<string, int> LocationPriorities = new()
    {
        { "Bonemass", BossPriority },
        { "Dragonqueen", BossPriority },
        { "Eikthyrnir", BossPriority },
        { "GDKing", BossPriority },
        { "GoblinKing", BossPriority },
        { "Mistlands_DvergrBossEntrance1", BossPriority },

        { "Crypt2", 80 },
        { "Crypt3", 80 },
        { "Crypt4", 80 },
        { "Hildir_cave", 80 },
        { "Hildir_crypt", 80 },
        { "Hildir_plainsfortress", 80 },
        { "Mistlands_DvergrTownEntrance1", 80 },
        { "Mistlands_DvergrTownEntrance2", 80 },
        { "MountainCave02", 80 },
        { "SunkenCrypt4", 80 },

        { "Mistlands_Harbour1", 70 },

        { "GoblinCamp2", 60 },
        { "GoblinCamp2_1", 60 },
        { "WoodVillage1", 60 },
        { "WoodVillage2", 60 },

        { "WoodFarm1", 55 },

        { "Mistlands_GuardTower1_new", 50 },
        { "Mistlands_GuardTower2_new", 50 },
        { "Mistlands_GuardTower3_new", 50 },
        { "Mistlands_Lighthouse1_new", 50 },

        { "Mistlands_Excavation1", 45 },
        { "Mistlands_Excavation2", 45 },

        { "BearCave", 40 },
        { "StoneTower1", 40 },
        { "StoneTower3", 40 },
        { "TrollCave02", 40 },

        { "GoblinHut01", 35 },
        { "GoblinHut02", 35 },
        { "GoblinHut03", 35 },

        { "AbandonedLogCabin02", 30 },
        { "AbandonedLogCabin03", 30 },
        { "AbandonedLogCabin04", 30 },
        { "Mistlands_Excavation3", 30 },
        { "Mistlands_GuardTower1_ruined_new", 30 },
        { "Mistlands_GuardTower1_ruined_new2", 30 },
        { "Mistlands_GuardTower3_ruined_new", 30 },
        { "MountainWell1", 30 },
        { "Ruin1", 30 },
        { "Ruin2", 30 },
        { "Ruin3", 30 },
        { "StoneHouse3", 30 },
        { "StoneHouse4", 30 },
        { "StoneTowerRuins03", 30 },
        { "StoneTowerRuins04", 30 },
        { "StoneTowerRuins05", 30 },
        { "StoneTowerRuins05_leet", 30 },
        { "StoneTowerRuins07", 30 },
        { "StoneTowerRuins08", 30 },
        { "StoneTowerRuins09", 30 },
        { "StoneTowerRuins10", 30 },

        { "CombatRuin01", 25 },
        { "StoneHenge1", 25 },
        { "StoneHenge2", 25 },
        { "StoneHenge3", 25 },
        { "StoneHenge4", 25 },
        { "StoneHenge5", 25 },
        { "SwampRuin1", 25 },
        { "SwampRuin2", 25 },
    };
    
    private const int DefaultPriority = 20;
    /// <summary>Selection weight of a custom location (named by config or API).</summary>
    private const int CustomLocationPriority = 80;
    public static RoadNetworkOptions NetworkOptions { get; set; } = new();

    /// <summary>The shipped Roads.MaxLocationsPerIsland.</summary>
    public const int DefaultMaxLocationsPerIsland = 48;
    public static int MaxLocationsPerIsland = DefaultMaxLocationsPerIsland;


    /// <summary>
    /// Location names explicitly registered via the mod API.
    /// </summary>
    private static readonly HashSet<string> RegisteredLocationNames = new HashSet<string>();

    private static readonly HashSet<string> ConfiguredLocationNames = new();

    #region Location Registration API

    /// <summary>
    /// Register a location name for road generation.
    /// Call this from other mods to include custom locations in the road network.
    /// </summary>
    public static void RegisterLocation(string locationName)
    {
        if (string.IsNullOrWhiteSpace(locationName))
            return;
        
        string trimmed = locationName.Trim();
        if (DeferWhileGenerating(trimmed, true)) return;
        if (RegisteredLocationNames.Add(trimmed))
        {
            Log.LogDebug($"Registered location for roads: {trimmed}");
        }
    }

    /// <summary>
    /// Unregister a location name from road generation.
    /// </summary>
    public static void UnregisterLocation(string locationName)
    {
        if (string.IsNullOrWhiteSpace(locationName))
            return;
        
        string trimmed = locationName.Trim();
        if (DeferWhileGenerating(trimmed, false)) return;
        if (RegisteredLocationNames.Remove(trimmed))
        {
            Log.LogDebug($"Unregistered location from roads: {trimmed}");
        }
    }

    /// <summary>Registrations asked for while a generation runs: its island workers read the
    /// registered names, so the set cannot change under them. Another mod registering from a
    /// world-load hook now overlaps the asynchronous initial generation; it is applied when the
    /// generation ends (it takes effect from the next generation), never refused.</summary>
    private static readonly List<(string name, bool add)> m_pendingRegistrations = new();

    private static bool DeferWhileGenerating(string name, bool add)
    {
        if (!IsGenerating) return false;
        lock (m_pendingRegistrations) m_pendingRegistrations.Add((name, add));
        Log.LogDebug($"Road location {(add ? "registration" : "removal")} of {name} queued until road generation ends");
        return true;
    }

    /// <summary>Apply registrations queued while a generation ran. Called as it ends.</summary>
    internal static void ApplyPendingRegistrations()
    {
        (string name, bool add)[] pending;
        lock (m_pendingRegistrations) { pending = m_pendingRegistrations.ToArray(); m_pendingRegistrations.Clear(); }
        foreach (var (name, add) in pending)
            if (add) RegisterLocation(name); else UnregisterLocation(name);
    }

    /// <summary>
    /// Get all currently registered location names.
    /// </summary>
    public static IReadOnlyCollection<string> GetRegisteredLocations()
    {
        return RegisteredLocationNames.Union(ConfiguredLocationNames).ToArray();
    }

    #endregion

    /// <summary>The one owner of road width, and every rule that follows it (docs/OWNERS.md).</summary>
    public static float RoadWidth = RoadConstants.DefaultRoadWidth;
    /// <summary>The shipped Roads.IslandRoadPercentage.</summary>
    public const int DefaultIslandRoadPercentage = 33;
    public static int IslandRoadPercentage = DefaultIslandRoadPercentage;

    private static bool m_roadsGenerated = false;
    private static bool m_locationsReady = false;
    private static bool m_roadsLoadedFromZDO = false;
    /// <summary>The network in memory is not the saved one: generated this session, or appended to. Saved in full
    /// (<see cref="SaveGlobalRoadData"/>); a loaded, unchanged network saves only its bridge zones.</summary>
    private static bool m_networkDirty = false;
    /// <summary>A client: the network in memory is the one the server sent (RoadNetworkSync), read only.</summary>
    private static bool m_networkReceived = false;
    private static RoadPathfinder? m_pathfinder;
    // Islands are isolated networks, so they are built on worker threads. A
    // pathfinder holds a terrain cache and LastPathCost, so each thread needs
    // its own; Finder hands out the calling thread's.
    private static System.Threading.ThreadLocal<RoadPathfinder>? m_threadPathfinders;
    private static RoadPathfinder? Finder =>
        t_finder ?? (m_threadPathfinders != null ? m_threadPathfinders.Value : m_pathfinder);
    /// <summary>The search a road planned outside generation is planned with on this thread (<see cref="PlanningWith"/>).</summary>
    [System.ThreadStatic] private static RoadPathfinder? t_finder;
    /// <summary>Where a road planned outside generation is handed over instead of stored (<see cref="PlanningWith"/>): the
    /// road the generator plans (GenerateRoad, with every search again it makes) is captured as it would be stored.</summary>
    [System.ThreadStatic] private static RoadCapture? t_capture;

    /// <summary>
    /// Plan roads on this thread as the generator plans them, outside generation and storing nothing: with
    /// <paramref name="finder"/>, each road GenerateRoad would store captured into <paramref name="capture"/> instead (a
    /// manual road stores it in its one append), and every refusal noted there. Until disposed. The staircases onto placed
    /// locations' landings a generation found it could not lay (<see cref="m_entryStairsFailed"/>, <see cref="m_entryStairsTried"/>)
    /// are its own memo, for the network it was planning: forgotten here, so a road planned on the network as it is now lays
    /// them out afresh. Never while a generation runs (its workers read that memo): a manual road is refused then.
    /// </summary>
    internal static IDisposable PlanningWith(RoadPathfinder finder, RoadCapture capture)
    {
        if (RoadLifecycleManager.IsGenerating) throw new InvalidOperationException("A road is planned outside generation only while none runs.");
        m_entryStairsFailed.Clear();
        m_entryStairsTried.Clear();
        var (finderBefore, captureBefore) = (t_finder, t_capture);
        (t_finder, t_capture) = (finder, capture);
        return new Restore(() => (t_finder, t_capture) = (finderBefore, captureBefore));
    }

    private sealed class Restore : IDisposable
    {
        private Action? m_restore;
        public Restore(Action restore) => m_restore = restore;
        public void Dispose() { m_restore?.Invoke(); m_restore = null; }
    }

    /// <summary>What a road's planning outside generation tried (<see cref="PlanningWith"/>): each refusal, why and where
    /// (where it says), and the road planned, as it would be stored.</summary>
    internal sealed class RoadCapture
    {
        public readonly List<(string why, Vector2? at)> Refusals = new();
        /// <summary>The zones the network goes round that a refused road would have written into
        /// (<see cref="Refusal.Zones"/>, of every <see cref="ProtectedZoneRefusal"/>).</summary>
        public readonly HashSet<Vector2s> Protected = new();
        public PlannedRoad? Road;
        public void Refused(string why, Vector2? at) => Refusals.Add((why, at));
    }

    /// <summary>A road as the generator planned it (GenerateRoadCore), before anything of it is stored.</summary>
    internal sealed class PlannedRoad
    {
        public Vector2 StartCenter, EndCenter;
        public List<Vector2> Path = new();
        public List<RoadSpatialGrid.PlannedPath> Plans = new();
        public List<RoadCrossing> Crossings = new();
        /// <summary>The crossings its ridges are judged from (Ridges: its own viaducts) where its pieces are judged apart from
        /// them (a staircase's end floor, RoadStairRuns.RoadOnto, judged with no crossing of its own); null: <see cref="Crossings"/>.</summary>
        public IReadOnlyList<RoadCrossing>? RidgeCrossings;
        /// <summary>The staircases its search bought (some already laid, <see cref="RoadStairSpan.Existing"/>), and those
        /// laid at its locations' ends.</summary>
        public List<RoadStairSpan> Stairs = new(), Approaches = new();
        /// <summary>Its ways onto placed locations' landings (<see cref="PlanEntry"/>).</summary>
        public List<(PlacedLocation placedLocation, RoadSpatialGrid.PlannedPath? road, RoadStairSpan? stairs)> Entries = new();
        /// <summary>Its ends carried on into their locations (<see cref="CarryOnToEdges"/>).</summary>
        public List<RoadSpatialGrid.PlannedPath> Carried = new();
        /// <summary>Its ends arriving at their locations as road: ways into them once it is committed (LocationWaysIn.Record).</summary>
        public List<(Vector2 location, LocationWaysIn.Way way)> WaysIn = new();
        /// <summary>The landing it is laid onto a staircase's flight with (<see cref="MeetingRefusal(List{RoadSpatialGrid.PlannedPath}, out Vector2, out RoadStairRuns.FlightLanding?, bool, RoadStairRuns.FlightLanding?)"/>), or null.</summary>
        public RoadStairRuns.FlightLanding? Landing;
        /// <summary>What it writes as its refusals read it (<see cref="RoadRefusal"/>): what its commit stores.</summary>
        public WrittenRoad? Written;
        /// <summary>Its ends at its locations, as its location ends were planned (<see cref="PlanLocationEnds"/>); none for a road known
        /// with no location ends (a marked manual road, a staircase's end floor): its location ends' refusals then have nothing to
        /// judge.</summary>
        public LocationEndContext Ends = LocationEndContext.None;
    }

    /// <summary>
    /// A road's ends at its locations, as generation plans them (<see cref="PlanLocationEnds"/>) and the one list judges them
    /// (<see cref="LocationEndRefusal"/>): the radius asked of each location (0: no location), whether each end is at its location (not
    /// moved to a junction), the placed location there (placed first), the road's width, the keep-out edges a way onto a landing
    /// reads its far end at. Generation and its approach trials plan the same ways onto placed locations' landings.
    /// <see cref="NoEntry"/>: why no way onto each end's
    /// landing was laid, where none was.
    /// </summary>
    internal sealed class LocationEndContext
    {
        public static readonly LocationEndContext None = new(0f, 0f, false, false, null, null, RoadWidth, 0f, 0f);
        public readonly float StartRadius, EndRadius, Width, StartEdge, EndEdge;
        public readonly bool StartAtLocation, EndAtLocation;
        public readonly PlacedLocation? StartLocation, EndLocation;
        public readonly string?[] NoEntry = new string?[2];
        /// <summary>The meeting with the network as its location ends were planned, before its approaches and their landed ends
        /// changed it (<see cref="PlanLocationEnds"/>: MeetingRefusal, with a landing on a flight where one fits): whether asked,
        /// and why refused and where. Judged among its location ends in that order (<see cref="LocationEndRefusal"/>); the road as
        /// finished meets the network again in the list's own location.</summary>
        public bool MeetingAsked;
        public string? MeetingRefused;
        public Vector2 MeetingAt;
        /// <summary>Whether its plans changed after that meeting was asked (a landing laid onto a flight, LandOnTheFlight; an
        /// approach whose foot levelled the road's end, LocationApproaches): only then is the road as finished met again.</summary>
        public bool ChangedSinceMeeting;
        /// <summary>Whether its location ends are counted and said (generation's road), or not (an approach trial).</summary>
        public bool Tally;
        public LocationEndContext(float startRadius, float endRadius, bool startAtLocation, bool endAtLocation, PlacedLocation? startLocation, PlacedLocation? endLocation,
            float width, float startEdge, float endEdge)
        {
            (StartRadius, EndRadius, StartAtLocation, EndAtLocation, StartLocation, EndLocation) = (startRadius, endRadius, startAtLocation, endAtLocation, startLocation, endLocation);
            (Width, StartEdge, EndEdge) = (width, startEdge, endEdge);
        }

        /// <summary>Whether the road has location ends at all (not <see cref="None"/>): generation's road and an approach trial.</summary>
        public bool Any => !ReferenceEquals(this, None);

    }

    /// <summary>
    /// A planned road's location ends (<paramref name="road"/>'s <see cref="PlannedRoad.Ends"/>), as generation lays them and an
    /// approach trial plans them (<see cref="PlanFromPath"/>), into <paramref name="road"/>: its ways onto placed locations'
    /// landings (<see cref="PlanEntry"/>, where planned), the landing it lands on a staircase's flight with
    /// (<paramref name="landOnFlight"/>: MeetingRefusal, LandOnTheFlight), its approach staircases (<see cref="LocationApproaches"/>)
    /// and its ends carried on into their locations (<see cref="CarryOnToEdges"/>). Plans only: whether the road may be built so is
    /// the one list's (<see cref="RoadRefusal"/>, <see cref="LocationEndRefusal"/> first); where a step finds it may not, the
    /// rest is not planned. <paramref name="tally"/>: counted and said (generation's road, not a trial).
    /// </summary>
    internal static void PlanLocationEnds(PlannedRoad road, string? label, bool tally, bool landOnFlight)
    {
        LocationEndContext ctx = road.Ends;
        ctx.Tally = tally;
        List<Vector2> path = road.Path;
        List<RoadSpatialGrid.PlannedPath> plans = road.Plans;
        List<RoadStairSpan> stairs = road.Stairs;
        var entries = road.Entries;
        bool said = tally && RoadsLog;
        if (ctx.Any)
            foreach (var (placedLocation, atStart, atLocation) in new[] { (ctx.StartLocation, true, ctx.StartAtLocation), (ctx.EndLocation, false, ctx.EndAtLocation) })
            {
                if (placedLocation == null || !atLocation) continue;
                // The ground the road's other end was planned to meet (PlanFromPath), held there where the way onto the landing
                // starts lower and the road is planned again (EntryFromLower); where a way onto its own location's landing was laid
                // already, the height it was laid from.
                float? farGround = entries.Count > 0 ? plans[0].Heights[0]
                    : atStart ? (ctx.EndAtLocation ? ApproachGround(path[path.Count - 1], road.EndCenter, ctx.EndEdge) : null)
                    : ctx.StartAtLocation ? ApproachGround(path[0], road.StartCenter, ctx.StartEdge) : null;
                bool entered = PlanEntry(placedLocation, atStart, path, plans, stairs, road.Crossings, ctx.Width, out var entryRoad, out var entryStairs, out string noEntry, farGround);
                if (entered)
                {
                    entries.Add((placedLocation, entryRoad, entryStairs));
                    if (said) m_locationApproaches.AddOrUpdate(entryRoad != null ? "placed first: road onto its landing" : "placed first: staircase onto its landing", 1, (_, n) => n + 1);
                    continue;
                }
                ctx.NoEntry[atStart ? 0 : 1] = noEntry;
                if (NoWayOntoTheLanding(road, placedLocation, atStart) is Refusal)
                {
                    if (said && !(noEntry.Contains(RoadStairRuns.BesideAnApproach) && WayInRefused(placedLocation.Centre, atStart, plans) != null))
                    {
                        m_locationApproaches.AddOrUpdate("placed first: refused", 1, (_, n) => n + 1);
                        Log.LogDebug($"Road {label ?? "(again without stairs)"}: no way onto the landing of the location at {placedLocation.Centre}: {noEntry}");
                    }
                    return;
                }
                if (said)
                {
                    System.Threading.Interlocked.Increment(ref m_closeEnds);
                    m_locationApproaches.AddOrUpdate("placed first: kept at its ring outside the mountains", 1, (_, n) => n + 1);
                    Log.LogDebug($"Road {label ?? "(again without stairs)"}: no road onto the landing of the location at {placedLocation.Centre}, kept at its ring: {noEntry}");
                }
            }
        // No road crosses another, nor runs beside one at its height, nor on a staircase's flight but onto a landing level
        // with the row it reaches (MeetingRefusal): the landing it is laid with.
        ctx.MeetingRefused = MeetingRefusal(plans, out ctx.MeetingAt, out RoadStairRuns.FlightLanding? landing, landOnFlight);
        ctx.MeetingAsked = true;
        if (ctx.MeetingRefused != null) return;
        if (landing != null) { LandOnTheFlight(plans, landing); road.Landing = landing; }
        // Its approach staircases from its ends at their locations, none at a placed location's (its way onto the landing is).
        road.Approaches = LocationApproaches(path, plans, stairs, road.StartCenter, ctx.StartLocation != null ? 0f : ctx.StartRadius,
            road.EndCenter, ctx.EndLocation != null ? 0f : ctx.EndRadius, ctx.StartAtLocation, ctx.EndAtLocation, tally);
        ctx.ChangedSinceMeeting = landing != null || road.Approaches.Count > 0;
        // Arriving level with a location that has no flat nor landing, carried on from its ring to the location's edge (CarryOnToEdge).
        road.Carried = CarryOnToEdges(path, plans, stairs, road.Approaches, ctx.Width, road.StartCenter, ctx.StartRadius, road.EndCenter, ctx.EndRadius,
            ctx.StartAtLocation && ctx.StartLocation == null, ctx.EndAtLocation && ctx.EndLocation == null);
    }

    /// <summary>Why a planned road may not be built for no way onto a placed location's landing.</summary>
    internal const string NoWayOntoLandingRefusal = "no way onto a placed location's landing";

    /// <summary>Why a road kept at a location's ring may not be built: its end is too far off the landing's level.</summary>
    internal const string RingLevelRefusal = "ring end off its landing level";

    /// <summary>
    /// Why a road may not end at <paramref name="placedLocation"/>, a placed location, at its start or end, where no way onto its landing was
    /// laid (<see cref="PlanEntry"/>): its staircase there refused at every aim as another way into the location has
    /// (RoadStairRuns.BesideAnApproach), it ends instead on that way's road (<see cref="WayInRefused"/>, a re-end); else, but
    /// at a location entered at its ring from the side it is entered from (<see cref="RingEnterable"/>,
    /// PlacedLocationShape.OnEntranceSide), it does not reach it. Null where it may.
    /// </summary>
    private static Refusal? NoWayOntoTheLanding(PlannedRoad road, PlacedLocation placedLocation, bool atStart)
    {
        Vector2 end = road.Path[atStart ? 0 : road.Path.Count - 1];
        string noEntry = road.Ends.NoEntry[atStart ? 0 : 1] ?? "";
        if (noEntry.Contains(RoadStairRuns.BesideAnApproach) && WayInRefused(placedLocation.Centre, atStart, road.Plans) is Vector2 onItsRoad)
            return new Refusal(BesideItsApproachRefusal, end) { ReEnd = (placedLocation.Centre, onItsRoad) };
        if (RingEnterable(placedLocation) && placedLocation.Shape.OnEntranceSide(end))
        {
            return RoadEnd(atStart, road.Path, road.Plans, road.Stairs) is { } arrival
                && !RingLevel(placedLocation.Centre, atStart ? road.Ends.StartRadius : road.Ends.EndRadius, arrival.end, arrival.height)
                ? new Refusal(RingLevelRefusal, end) : null;
        }
        return new Refusal(NoWayOntoLandingRefusal, end);
    }

    /// <summary>
    /// Why a road may not be built for its ends at its locations (<see cref="PlannedRoad.Ends"/>), and where: the first of the
    /// one list (<see cref="RoadRefusal"/>), in the order generation plans its location ends. No way onto a placed location's
    /// landing (<see cref="NoWayOntoTheLanding"/>); crossing or running beside another road, or on a staircase's flight
    /// where no landing was laid with it (MeetingRefusal); arriving at its location the way another way into it does
    /// (<see cref="ReEndOnAWayIn"/>: a re-end, <see cref="Refusal.ReEnd"/>); an end not level with its location and no
    /// staircase onto it (<see cref="EndNotLevel"/>). A road with no location ends (<see cref="LocationEndContext.None"/>: a marked
    /// manual road, a staircase's end floor) has none of these, and meets the network at its location in the list's own order.
    /// </summary>
    private static Refusal? LocationEndRefusal(PlannedRoad road)
    {
        LocationEndContext ctx = road.Ends;
        List<Vector2> path = road.Path;
        List<RoadSpatialGrid.PlannedPath> plans = road.Plans;
        bool Entered(PlacedLocation? placedLocation) => placedLocation != null && road.Entries.Exists(e => e.placedLocation == placedLocation);
        if (ctx.Any && path.Count > 1)
            foreach (var (placedLocation, atStart, atLocation) in new[] { (ctx.StartLocation, true, ctx.StartAtLocation), (ctx.EndLocation, false, ctx.EndAtLocation) })
                if (placedLocation != null && atLocation && !Entered(placedLocation) && NoWayOntoTheLanding(road, placedLocation, atStart) is Refusal noWay)
                    return noWay;
        // A road with location ends meets the network among them, in generation's order; one with none, in the list's own.
        if (!ctx.Any) return null;
        // The meeting as its location ends were planned, before its approaches: then the re-end and the level, as generation
        // judged them, so a road arriving as another way in is still searched again to it.
        string? planned = ctx.MeetingAsked ? ctx.MeetingRefused : MeetingRefusal(plans, out ctx.MeetingAt, out _, landOnFlight: false, laidWith: road.Landing);
        if (planned != null) return new Refusal(planned, ctx.MeetingAt);
        if (path.Count < 2) return null;
        // A location has one way in up a face (LocationWaysIn, the one owner): an end arriving at its location the way another way into
        // it does, by staircase or as road, is not built so.
        foreach (var (atStart, centre, asked, atLocation, placedLocation) in new[]
            { (true, road.StartCenter, ctx.StartRadius, ctx.StartAtLocation, ctx.StartLocation), (false, road.EndCenter, ctx.EndRadius, ctx.EndAtLocation, ctx.EndLocation) })
        {
            if (!atLocation || asked <= 0f) continue;
            Vector2 location = placedLocation?.Centre ?? centre;
            if (ReEndOnAWayIn(location, ArrivalAt(location, atStart, plans, road.Entries, road.Approaches, placedLocation)) is Vector2 onItsRoad)
                return new Refusal(BesideItsApproachRefusal, path[atStart ? 0 : path.Count - 1]) { ReEnd = (location, onItsRoad) };
        }
        // A road whose end at its location does not arrive level with it, and from which no approach staircase onto it can be
        // laid, does not reach it: refused, even at the cost of a POI; the planner tries its other candidates. A placed
        // location's end is judged where no way onto its landing was laid (kept at its ring outside the Mountains).
        if (EndNotLevel(path, plans, road.Stairs, road.Approaches, road.StartCenter, ctx.StartRadius, road.EndCenter, ctx.EndRadius,
                ctx.StartAtLocation && !Entered(ctx.StartLocation), ctx.EndAtLocation && !Entered(ctx.EndLocation), ctx.Tally)
            is (string refusal, string notLevel))
            return new Refusal(refusal, notLevel, path[0], path[0], path[path.Count - 1]);
        return null;
    }
    private static readonly object m_recordGate = new object();
    [System.ThreadStatic] private static int m_islandRoads;
    /// <summary>This island's network version: one more at every write to what a road's search, its build and the served
    /// test read (the grid's points, the staircases and their laid layouts, the viaducts laid, the locations landed on, the
    /// crossings recorded, the locations off their level, the join's hints). Each owner bumps it as it writes, on the thread
    /// that writes, which is the island's own worker: islands are disjoint, so another island's writes leave it alone.
    /// Unchanged between two calls, the network an island's roads read is the same (<see cref="GenerateRoutedRoads"/>'s
    /// join memo). Each value is taken once in the process (<see cref="s_networkVersions"/>), a thread's first read included,
    /// so it names one thread's network state only: the carry cache (<see cref="RoadStairRuns"/>' CarryKey) outlives the
    /// generation and is read on the main thread too (manual roads), where a per-thread count met a worker's. Its readers
    /// only compare it for equality.</summary>
    [System.ThreadStatic] private static int t_networkVersion;
    /// <summary>The last network version handed out in the process (<see cref="IslandNetworkVersion"/>); 0 is never one.</summary>
    private static int s_networkVersions;
    internal static int IslandNetworkVersion =>
        t_networkVersion != 0 ? t_networkVersion : t_networkVersion = System.Threading.Interlocked.Increment(ref s_networkVersions);
    /// <summary>The points of the stored network a join may aim at, by the rule generation's hints follow (GenerateRoadCore):
    /// none on a crossing and none on a staircase, since a join aimed at one built a second bridge out to a waypoint in
    /// mid-river, and there is no road on a staircase to join. Generation adds its hints as it builds, from the road's own
    /// path; a stored network has no paths, so these are its points within half a road width of a crossing's banks or a
    /// staircase's ends, bank to bank and end to end, dropped. The one owner of joining a network (docs/OWNERS.md).</summary>
    internal static List<Vector2> JoinTargets(Func<Vector2, bool> allowed)
    {
        var spans = new List<(Vector2 a, Vector2 b)>();
        foreach (RoadCrossing crossing in m_roadCrossings) spans.Add((crossing.FromBank, crossing.ToBank));
        foreach (StairRun run in StairStructures.Runs) spans.Add((run.From, run.To));
        bool OnSpan(Vector2 p)
        {
            foreach (var (a, b) in spans)
            {
                Vector2 ab = b - a;
                float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                if (Vector2.Distance(p, a + ab * t) <= RoadWidth * 0.5f) return true;
            }
            return false;
        }
        return RoadSpatialGrid.RoadTargets(p => allowed(p) && !OnSpan(p));
    }

    internal static void IslandNetworkChanged() => t_networkVersion = System.Threading.Interlocked.Increment(ref s_networkVersions);
    [System.ThreadStatic] internal static RoadIslandActivity.Token? IslandActivity;
    /// <summary>How many of an island's destinations the finished network
    /// reaches. Measured rather than tallied from what the planner attempted:
    /// a destination can be reached by a road built to its neighbour, and a
    /// road refused between two touching locations can leave one reached anyway
    /// because the network already runs through its footprint. Counting
    /// attempts answers neither.
    ///
    /// Islands are disjoint, so this is stable while other islands commit.</summary>
    private static void CountDestinationsReached(Island island,
        List<(string name, Vector3 position, float radius)> selected)
    {
        RecordCoastalLandings(island, selected);
        bool[] served = ServedDestinations(selected);
        int reached = 0; foreach (bool s in served) if (s) reached++;
        if (RoadsLog) LogUnconnected(selected);
        IslandActivity?.Measured(served);
        System.Threading.Interlocked.Add(ref m_destinationsSelected, selected.Count);
        System.Threading.Interlocked.Add(ref m_destinationsConnected, reached);
    }

    /// <summary>Notes each of the island's shore landings with whether its destinations took it and whether the network
    /// reaches it: the landings that were not chosen too, a road passing one reaches it.</summary>
    private static void RecordCoastalLandings(Island island, List<(string name, Vector3 position, float radius)> selected)
    {
        var places = new List<CoastalLandingPlace>(island.CoastalLandings.Count);
        foreach (Vector3 p in island.CoastalLandings)
        {
            bool chosen = selected.Exists(d => d.name == RoadCoastalLandings.Name && d.position.Equals(p));
            places.Add(new CoastalLandingPlace(p, island.Id, chosen, Reached(p, RoadCoastalLandings.Radius, RoadCoastalLandings.Name)));
        }
        lock (m_recordGate)
        {
            m_coastalLandings.RemoveAll(l => l.IslandId == island.Id);
            m_coastalLandings.AddRange(places);
        }
    }

    /// <summary>Marks a location done, noting whether the network serves it. Called by the island's serial planner.</summary>
    internal static void SettleDestination(List<(string name, Vector3 position, float radius)> locations, int index)
    {
        var activity=IslandActivity;
        if(activity==null || index<0 || index>=locations.Count) return;
        var d=locations[index];
        activity.Settled(index, Reached(d.position,d.radius,d.name));
    }

    /// <summary>With <see cref="RoadsLog"/>: every destination the network does not reach, and how its last road attempt failed.</summary>
    private static void LogUnconnected(List<(string name, Vector3 position, float radius)> selected)
    {
        foreach (var d in selected)
        {
            if (Reached(d.position, d.radius, d.name)) continue;
            var key = LocationKey(new Vector2(d.position.x, d.position.z));
            string last = UnreachedWhy(d.position);
            string stairs = m_stairsFailed.TryGetValue(key, out string? st) ? $"; stairs tried: {st}" : "; stairs not tried";
            if (PlacedLocations.TryGetReady(new Vector2(d.position.x, d.position.z), out PlacedLocation placed))
                stairs += NearestToLanding(placed);
            Log.LogInfo(FormattableString.Invariant($"  unconnected: {d.name} at ({d.position.x:F0}, {d.position.z:F0}): {last}{stairs}"));
        }
    }

    /// <summary>How the last road attempt to an unreached destination failed, as the log says it.</summary>
    internal static string UnreachedWhy(Vector3 position)
    {
        var key = LocationKey(new Vector2(position.x, position.z));
        if (PlacedLocations.NotPlacedWhy(new Vector2(position.x, position.z)) is string why) return $"given no road ({why})";
        return m_builtTo.ContainsKey(key)
            ? "a road was built to it but stops short" + (m_lastFailure.TryGetValue(key, out string? f0) ? $" (a refused attempt: {f0})" : "")
            : m_lastFailure.TryGetValue(key, out string? f) ? f : "never attempted";
    }

    private static void RefreshDestinationsReached(List<(string name, Vector3 position, float radius)> selected)
    {
        var activity=IslandActivity;
        if(activity!=null && activity.ReachSampleDue()) activity.Measured(ServedDestinations(selected));
    }

    private static bool[] ServedDestinations(List<(string name, Vector3 position, float radius)> selected)
    {
        var served=new bool[selected.Count];
        for(int i=0;i<selected.Count;i++) served[i]=Reached(selected[i].position,selected[i].radius,selected[i].name);
        return served;
    }

    /// <summary><see cref="Reached(Vector2, float, string?)"/> for a location's world position. The one owner of what counts as reached (docs/OWNERS.md).</summary>
    internal static bool Reached(Vector3 centre, float radius, string? name = null) => Reached(new Vector2(centre.x, centre.z), radius, name);

    /// <summary>Whether the network already serves a plain point, a location of none (road_connect's mark), with a connection a
    /// player uses as it stands: network <paramref name="allowed"/> admits (the point's island) on the road there or within one
    /// path cell past its edge (<see cref="ReachPastEdge"/>, as a location is reached past its keep-out edge: no road shorter than
    /// the search's resolution can be built to it), road or a staircase's walked piece, whose surface is a step
    /// (<see cref="StairLayout.Walkable"/>) from the point's finished ground (<see cref="StructureGround.At"/>). Nearness alone is not: a road raised or cut beside the
    /// point is no way onto it. Nor is the search's junction at grade (<see cref="RoadSpatialGrid.TryGetRoadAtGradeWithin"/>,
    /// <see cref="RoadPathfinder.JunctionStep"/>): that is where a road may be built to the network, not a way already walked.
    /// <see cref="JoinTo"/>'s served test for a point.</summary>
    internal static bool Reached(Vector2 point, Func<Vector2, bool>? allowed)
    {
        float ground = StructureGround.At(GenerationWorld)(point);
        return NetworkNear(point, RoadWidth * 0.5f + ReachPastEdge, (q, h) => (allowed == null || allowed(q)) && StairLayout.Walkable(ground, h), true);
    }

    /// <summary>How far past a location's keep-out edge (<see cref="KeepOutEdge"/>) the network may run and still reach
    /// it: one path cell, the search's own resolution.</summary>
    internal const float ReachPastEdge = RoadPathfinder.CellSize;

    /// <summary>
    /// How far over or under the location's own level (<see cref="ArrivalHeight"/>) the network may end and still reach it: the one
    /// height for "reached" (<see cref="Reached"/>, <see cref="LandingReached"/>) and for refusing a road whose end does not
    /// (<see cref="EndNotLevel"/>). About a step (a road ending more than about a step under or over the location's
    /// ground is not reached; it gets an approach or the location goes unserved). 1.5 m: a stair and a half
    /// (<see cref="StairLayout.PieceRise"/>), which the player walks up over the ground between a road's end and the location's
    /// edge (at least half a road's width, its blend and a path cell, 4 m or more: under 21 degrees). More is a climb: a
    /// staircase is laid onto it where one can be (from <see cref="StairLayout.StairFitsFrom"/> off, where a stair fits), and without one
    /// the end does not reach it. Before, a location with no single levelled flat (AbandonedLogCabin04, which only smooths) had no
    /// known height and was reached by a road 10 m under it, and outside the Mountains every location was reached at any height.
    /// </summary>
    public const float ReachRise = 1.5f;

    /// <summary>
    /// Whether an approach staircase is laid onto the location at <paramref name="centre"/> (its prefab <paramref name="prefab"/>,
    /// null where not known) where a road's end does not come level with it (<see cref="LocationApproaches"/>, and onto a placed
    /// location's landing, <see cref="PlanEntryFrom"/>): at a boss's entrance anywhere (<see cref="OwnLandingOnly"/>), and at
    /// every other location only in the Mountains, by the biome at its centre. Everywhere the network must first end level with
    /// the location to reach it (<see cref="ReachRise"/>, <see cref="Reached"/>): elsewhere an end not level and with no road onto
    /// its landing is refused (<see cref="EndNotLevel"/>); a location outside the Mountains left unserved that way then falls back
    /// to a road ending off its level (<see cref="ReachOffLevel"/>). The one owner of that choice. From a road's end at
    /// <paramref name="fromHeight"/> (null where not known), none down to a custom location standing below it (its own level,
    /// <see cref="CustomLevel"/>, more than <see cref="StairLayout.StairFitsFrom"/> under the end, where a stair would fit): its first
    /// attempt leaves it unserved, and its fallback ends the road at its ring off its level, with no stand-off
    /// (<see cref="StandOffLevel"/> stands an end off only below the location). A staircase down onto such a location's flat ended
    /// in the rocks the location keeps round it.
    /// </summary>
    internal static bool ApproachStairsAt(Vector2 centre, string? prefab, float? fromHeight = null)
    {
        if (prefab != null && OwnLandingOnly(prefab)) return true;
        // Every location on the fallback (HeightNeeded false, OffLevelAllowed): the road ends at its ring, no staircase.
        if (!HeightNeeded(centre)) return false;
        if (fromHeight is float from && CustomLevel(centre, prefab, out _) is float own && from - own > StairLayout.StairFitsFrom) return false;
        var world = GenerationWorld;
        return world == null || RoadRules.For(RoadBiome.OfLocation(world, centre)).ApproachStairs;
    }

    /// <summary>Whether a road may reach a placed location at its ring: not where it takes a staircase (<see cref="ApproachStairsAt"/>),
    /// nor a location with a door (<see cref="PlacedLocationShape.HasDoor"/>: only a road to its door reaches it). The reached test
    /// (<see cref="Reached"/>) and the refusal of a road kept at a ring both read it.</summary>
    private static bool RingEnterable(PlacedLocation placedLocation) =>
        !ApproachStairsAt(placedLocation.Centre, placedLocation.Record.Prefab) && !PlacedLocationShape.HasDoor(placedLocation.Record.Prefab);

    /// <summary>How many of the search's junction steps (RoadPathfinder.DefaultJunctionStep) an end kept at a ring may stand off
    /// its landing and still reach it (<see cref="RingLevel"/>).</summary>
    private const float RingLevelSteps = 2f;

    /// <summary>Whether an end kept at a location's ring reaches its arrival level: within the search's existing two junction
    /// steps of its landing (ArrivalHeight), even on the fallback. Mistlands locations retain arrivals at any height.
    /// A custom location standing above its end still uses its
    /// separate stand-off (OffLevelAt); it is not an arrival kept at the ring. Without a preload, ArrivalHeight supplies
    /// the expected flat or the ground at the footprint's edge on this bearing, as for every other arrival.</summary>
    private static bool RingLevel(Vector2 centre, float radius, Vector2 from, float height) =>
        GenerationWorld is { } world && RoadRules.For(RoadBiome.OfLocation(world, centre)).RingAnyHeight
        || StandOffLevel(centre, radius) is float own && height < own
        || ArrivalHeight(centre, radius, from) is not float landing || Mathf.Abs(height - landing) <= RingLevelSteps * RoadPathfinder.DefaultJunctionStep;

    /// <summary>The one test of whether the network reaches the location at <paramref name="flat"/> (exterior
    /// <paramref name="radius"/>, as asked, not trimmed), measured at the trim's keep-out edge (<see cref="KeepOutEdge"/>):
    /// for the count, the planner's join (<see cref="JoinLocation"/>) and whether a road's own stub to the location is dropped
    /// where it merges (<see cref="EndReached"/>).
    ///
    /// Reached only where the network ends level with the location (<see cref="ReachRise"/> of <see cref="ArrivalHeight"/>, the
    /// location's own level, or its ground at its edge on the network's side), by road or by a staircase's walked piece, its top or foot, in every biome. A placed location is reached
    /// on its landing (<see cref="LandingReached"/>), and outside the Mountains (<see cref="ApproachStairsAt"/>) at its ring as
    /// well, within two junction steps of its landing (Mistlands exempt, <see cref="RingLevel"/>), but for a location with a door (a crypt, <see cref="PlacedLocationShape.DoorLandings"/>): only
    /// a road that reaches its door reaches it. A location that waits for its placing and was not placed before its roads is never reached
    /// (<see cref="PlacedLocations.NotPlaced"/>). A point with no radius (a junction) is reached at any height, and so is a
    /// location on the fallback on its landing; at its ring it still asks RingLevel (<see cref="HeightNeeded"/>, <see cref="ReachOffLevel"/>), and is not reached by
    /// network a ridge stands between it and the location (<see cref="BehindRidge"/>).</summary>
    internal static bool Reached(Vector2 flat, float radius, string? name = null)
    {
        // A location placed before its roads is reached on its landing, not at its ring.
        // A boss's entrance only by a road onto its own landing (OwnLandingOnly): placed, once one was carried onto it;
        // not placed, never by a road passing within reach (the count and the planner's join alike).
        // A location with a door only on its door landing (PlacedLocationShape.DoorLandings: a crypt's road reaches its door).
        // At any height only on the fallback, its first attempt at height having left it unserved (HeightNeeded).
        bool height = HeightNeeded(flat);
        // A custom location reached without its own height stands its road's end off (StandOffLevel): on the fallback, or
        // with no flat nor landing.
        float? standOff = radius > 0f ? StandOffLevel(flat, radius, name) : null;
        // The fallback keeps its ring height guard (RingLevel), and never reaches from behind a ridge (BehindRidge).
        // At its ring, and only there (not on its landing), a custom location standing its end off is reached at its stand-off
        // (AtStandOff, the cheap test first).
        Func<Vector2, float, bool>? atRing = radius > 0f && !height ? (q, h) => OffLevelAt(flat, radius, standOff, q, h) : null;
        float fallbackReach = radius > 0f ? FallbackReach(flat, radius, standOff) : 0f;
        if (radius > 0f && PlacedLocations.TryGetReady(flat, out PlacedLocation placed))
        {
            if (OwnLandingOnly(placed.Record.Prefab)) return PlacedLocations.Landed(placed.Centre);
            if (height ? LandingReached(placed) : PlacedLocations.Landed(placed.Centre))
                return true;
            if (!RingEnterable(placed)) return false;
            // At its ring only on the side it is entered from (PlacedLocationShape.OnEntranceSide): a Dvergr town entrance's back
            // is blocked rock.
            PlacedLocationShape shape = placed.Shape;
            Func<Vector2, float, bool> ring = (q, h) => shape.OnEntranceSide(q) && OffLevelAt(flat, radius, standOff, q, h);
            return NetworkNear(flat, height ? KeepOutEdge(flat, radius, RoadWidth) + ReachPastEdge : fallbackReach, ring, !height);
        }
        // One that waits for its placing and was not placed before its roads gets no road
        // (PlacedLocations.NotPlaced), nor is it reached by one passing within reach of its ring.
        if (radius > 0f && PlacedLocations.NotPlaced(flat)) return false;
        if (name != null && OwnLandingOnly(name)) return false;
        float reach = radius > 0f
            ? KeepOutEdge(flat, radius, RoadWidth)
            : radius;
        // A staircase is the network too: a road that reaches a location by one ends on its top or foot,
        // with no road there (MountainCave02 -2560,1536: its staircase's top 38.8 m out, the
        // nearest road 52 m, counted unreached).
        if (atRing != null) return NetworkNear(flat, fallbackReach, atRing, true);
        // With no flat nor landing, at height a custom location stands its road's end off where it stands above it; one
        // under the network is reached level with its edge, as every other location.
        if (standOff is float own && ArrivalLevelFrom(flat, radius) is Func<Vector2, float> edge)
            return NetworkNear(flat, fallbackReach, (q, h) => h >= own
                ? (q - flat).sqrMagnitude <= (reach + ReachPastEdge) * (reach + ReachPastEdge) && Mathf.Abs(h - edge(q)) <= ReachRise
                : OffLevelAt(flat, radius, standOff, q, h), false);
        return NetworkNear(flat, reach + ReachPastEdge, radius > 0f ? ArrivalLevelFrom(flat, radius) : null);
    }

    /// <summary>Whether a road must end level with the location at <paramref name="centre"/> to reach it (<see cref="ReachRise"/>,
    /// <see cref="Reached"/>), the one owner behind the reached test, the refusal of a road whose end is not
    /// (<see cref="EndNotLevel"/>) and the search to level doors only (<see cref="OnlyLevelDoors"/>): everywhere, but at a
    /// location whose first attempt, at height, left it unserved and which is tried again without it
    /// (<see cref="ReachOffLevel"/>, in the Mountains only a custom location). Mountains strict; everywhere else first arrive at
    /// height, and only where no road can, fall back to the road ending at the location's ring off its level, the location reached.
    /// Before, the Mistlands needed no height at all, and then, tried, nowhere but the Mountains.</summary>
    internal static bool HeightNeeded(Vector2 centre) => !m_offLevel.ContainsKey(LocationKey(centre));

    /// <summary>The locations reached without height (<see cref="ReachOffLevel"/>), by identity (<see cref="LocationKey"/>):
    /// while a location's second attempt runs, and after it for good where it reached the location.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, float), bool> m_offLevel = new();
    /// <summary>The custom locations among the destinations (<see cref="IsModLocation"/>), by identity (<see cref="LocationKey"/>), for the
    /// tests that know a location by its centre only (a road's trim, <see cref="OffLevelStandOff"/>): noted as an island's
    /// destinations are planned (<see cref="GenerateRoutedRoads"/>) and as a location falls back (<see cref="ReachOffLevel"/>).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, float), bool> m_customLocations = new();

    /// <summary>Changes to the arrival state since it was read: lets the saved-record owner retain an unread record
    /// until this version rewrites that state. Not saved and not part of the network's identity.</summary>
    internal static long ReachedLocationsRevision => System.Threading.Interlocked.Read(ref s_reachedLocationsRevision);
    private static long s_reachedLocationsRevision;

    /// <summary>The network's arrival identities, in stable order for its saved metadata.</summary>
    internal static (Vector2 centre, bool offLevel, bool custom)[] ReachedLocations() =>
        m_offLevel.Keys.Union(m_customLocations.Keys).OrderBy(k => k.Item1).ThenBy(k => k.Item2)
            .Select(k => (new Vector2(k.Item1, k.Item2), m_offLevel.ContainsKey(k), m_customLocations.ContainsKey(k))).ToArray();

    /// <summary>Replace the arrival identities when the network is loaded or reset; no locations means none are known.</summary>
    internal static void RestoreReachedLocations(IEnumerable<(Vector2 centre, bool offLevel, bool custom)>? locations = null)
    {
        m_offLevel.Clear();
        m_customLocations.Clear();
        m_standOffRadius.Clear();
        System.Threading.Interlocked.Increment(ref s_reachedLocationsRevision);
        if (locations != null)
            foreach (var location in locations)
            {
                var key = LocationKey(location.centre);
                if (location.offLevel) m_offLevel[key] = true;
                if (location.custom) m_customLocations[key] = true;
            }
        IslandNetworkChanged();
    }

    /// <summary>Notes the location <paramref name="name"/> at <paramref name="position"/> among <see cref="m_customLocations"/> where it is a custom location.</summary>
    internal static void NoteCustomLocation(string name, Vector3 position)
    {
        if (IsModLocation(name) && m_customLocations.TryAdd(LocationKey(new Vector2(position.x, position.z)), true))
            System.Threading.Interlocked.Increment(ref s_reachedLocationsRevision);
    }

    /// <summary>
    /// The level a road's end at the location at <paramref name="centre"/> (<paramref name="name"/>, null where only its centre
    /// is known) stands off from, or null where it does not: a custom location's in any biome (<see cref="IsModLocation"/>) reached
    /// or ended at without its own height, on the fallback (<see cref="HeightNeeded"/>) or with no flat nor landing (not placed
    /// first, no single levelled platform, <see cref="LocationLevelling.ApproachHeight"/>: its ground at its edge is all the
    /// reached test reads, the foot of the rock a fort stands on). Its own level: its landing's ground placed first, its flat,
    /// else its root (<see cref="LocationLevelling.CentreHeight"/>). Null for every vanilla location.
    /// </summary>
    private static float? StandOffLevel(Vector2 centre, float radius, string? name = null)
    {
        if (radius <= 0f || CustomLevel(centre, name, out bool levelled) is not float own) return null;
        return levelled && HeightNeeded(centre) ? null : own;
    }

    /// <summary>A custom location's own level (<see cref="IsModLocation"/>, by <paramref name="name"/>, else by its
    /// centre among <see cref="m_customLocations"/>): its landing's ground placed first, its flat, else its root
    /// (<see cref="LocationLevelling.CentreHeight"/>), <paramref name="levelled"/> false for the root. Null for every vanilla
    /// location. The level <see cref="StandOffLevel"/> and <see cref="ApproachStairsAt"/> read.</summary>
    private static float? CustomLevel(Vector2 centre, string? name, out bool levelled)
    {
        levelled = false;
        var world = GenerationWorld;
        if (world == null) return null;
        if (!(name != null ? IsModLocation(name) : m_customLocations.ContainsKey(LocationKey(centre)))) return null;
        levelled = true;
        if (PlacedLocations.TryGetReady(centre, out PlacedLocation placedLocation)) return placedLocation.LandingHeight;
        float root = LocationLevelling.CentreHeight(centre, world);
        float? flat = LocationLevelling.ApproachHeight(root, LocationLevelling.OpsAt(centre));
        levelled = flat.HasValue;
        return flat ?? root;
    }

    /// <summary>The one stand-off rule: how far from its centre a road's end at height <paramref name="h"/> stands at a custom
    /// location of level <paramref name="location"/> standing its end off (<see cref="StandOffLevel"/>), its ring
    /// <paramref name="ring"/> (<see cref="KeepOutEdge"/>) and past it the height the location stands over the end, over the road's
    /// grade cap (<see cref="RoadGrade.Configured"/>): a location standing above the road's end (never one below it) gets a road
    /// ending out in the open, the last climb the player's, not an end under a cliff.</summary>
    private static float StandOffRadius(float ring, float locationLevel, float h) =>
        ring + Mathf.Max(0f, locationLevel - h) / RoadGrade.Floored; // inline, not EaseLength: one rounding, as on the game's Mono

    /// <summary>MWL stand-off, 30 m past the ring: a road's end at a custom location standing above it is
    /// first tried this far past its ring (<see cref="StandOffInBand"/>); only where no end there works does it stand off at the
    /// grade (<see cref="StandOffRadius"/>). Where the grade's stand-off is nearer (a small rise), the grade's is kept.</summary>
    internal const float StandOffPastRing = 30f;

    /// <summary>Whether the grade's stand-off <paramref name="graded"/> (<see cref="StandOffRadius"/>) past the ring
    /// <paramref name="ring"/> lies beyond <see cref="StandOffPastRing"/>, so that is tried first.</summary>
    private static bool BandBefore(float ring, float graded) => graded - ring > StandOffPastRing;

    /// <summary>The end along <paramref name="dir"/> at <see cref="StandOffPastRing"/> past the ring <paramref name="ring"/>, or the
    /// nearest acceptable cell short of it within one march step (<see cref="ReachPastEdge"/>), that the owners an end is judged by
    /// accept, on the ground: dry (<see cref="RoadWater.BuiltDry(WorldGenerator, Vector2)"/>), the ground along the bearing within
    /// the grade cap (<see cref="RoadGrade.Configured"/>), not behind a ridge from the location (<see cref="BehindRidge"/>). Null where
    /// none does. The earthwork cap is the planner's: a road ending there that it refuses is searched again at the grade's
    /// stand-off (<see cref="GenerateRoad"/>). The one owner of the MWL stand-off's ends: the trim asks it
    /// (<see cref="OffLevelStandOff"/>) and so does the reached test (<see cref="AtBandEnd"/>).</summary>
    private static float? StandOffInBand(Vector2 centre, float radius, float ring, Vector2 dir, WorldGenerator world)
    {
        float H(float r) { Vector2 q = centre + dir * r; return BiomeBlendedHeight.GetBlendedHeight(q.x, q.y, world); }
        for (float r = BandOuter(ring); r >= BandInner(ring) - 0.01f; r -= ReachPastEdge)
        {
            Vector2 at = centre + dir * r;
            if (!RoadWater.BuiltDry(world, at)) continue;
            if (Mathf.Abs(H(r + ReachPastEdge) - H(r - ReachPastEdge)) > 2f * ReachPastEdge * RoadGrade.Configured) continue;
            if (BehindRidge(centre, radius, at, H(r))) continue;
            return r;
        }
        return null;
    }

    /// <summary>The MWL stand-off's outermost end past the ring <paramref name="ring"/> (<see cref="StandOffPastRing"/>).</summary>
    private static float BandOuter(float ring) => ring + StandOffPastRing;
    /// <summary>The MWL stand-off's innermost end: one march step (<see cref="ReachPastEdge"/>) short of <see cref="BandOuter"/>.</summary>
    private static float BandInner(float ring) => BandOuter(ring) - ReachPastEdge;

    /// <summary>Whether network <paramref name="d"/> out from the location at <paramref name="centre"/> (at <paramref name="q"/>)
    /// stands at the MWL stand-off's end on its own bearing, within a path cell of it: the end the trim would choose there
    /// (<see cref="StandOffInBand"/>), so a bearing whose band end is wet, too steep or behind a ridge has none. The band's
    /// span first, the cheap test.</summary>
    private static bool AtBandEnd(Vector2 centre, float radius, float ring, Vector2 q, float d)
    {
        if (d < BandInner(ring) - ReachPastEdge - 0.01f || d > BandOuter(ring) + ReachPastEdge + 0.01f) return false;
        var world = GenerationWorld;
        if (world == null || d < 1e-3f) return false;
        return StandOffInBand(centre, radius, ring, (q - centre) * (1f / d), world) is float end && Mathf.Abs(d - end) <= ReachPastEdge;
    }

    /// <summary>Set on the road's thread while it is searched again with no MWL stand-off (<see cref="GenerateRoad"/>).</summary>
    [System.ThreadStatic] private static bool t_noStandOffBand;
    /// <summary>The road's ends on this thread stood off at the MWL stand-off (<see cref="OffLevelStandOff"/>), nearer than the
    /// grade's: each location's centre and the grade's stand-off there, the farthest the road searched again would end.</summary>
    [System.ThreadStatic] private static List<(Vector2 centre, float graded)>? t_bandedEnds;
    /// <summary>Where the road last refused on this thread was refused (<see cref="GenerateRoadCore"/>), where it says: null
    /// where it does not.</summary>
    [System.ThreadStatic] private static List<Vector2>? t_refusedAt;
    /// <summary>A road on this thread whose way onto a placed location ran beside the approach another road laid there
    /// (<see cref="ReEndOnAWayIn"/>): that location's centre, and where the approach's own road meets it, which the road is
    /// searched again to end on (<see cref="GenerateRoad"/>). Null where none.</summary>
    [System.ThreadStatic] private static (Vector2 location, Vector2 onItsRoad)? t_reEnd;

    /// <summary>The refusal of a road whose way onto a location runs beside the approach already laid there (<see cref="ReEndOnAWayIn"/>).</summary>
    internal const string BesideItsApproachRefusal = "its way onto the landing beside the location's approach";

    /// <summary>Where a road arriving at <paramref name="location"/> as <paramref name="arrival"/> ends instead, on the network, or
    /// null: where another way into that location arrives the same way (<see cref="LocationWaysIn.Joining"/>, the one owner), on that
    /// way's road (an approach's foot, where its road meets it; a road's arrival where it passes nearest), as a junction
    /// (radius 0, as a location's join to the network ends, <see cref="JoinLocation"/>).</summary>
    internal static Vector2? ReEndOnAWayIn(Vector2 location, LocationWaysIn.Way? arrival) =>
        LocationWaysIn.Joining(location, arrival, out Vector2 at) != null ? at : null;

    /// <summary>Where a road whose staircase onto <paramref name="location"/> was refused at every aim and start as a way in the
    /// location has (<see cref="RoadStairRuns.BesideAnApproach"/>) ends instead: as its road arrives (<see cref="ReEndOnAWayIn"/>),
    /// else at the foot of the approach of that location nearest its end, the way in its staircase would have built twice.</summary>
    private static Vector2? WayInRefused(Vector2 location, bool atStart, List<RoadSpatialGrid.PlannedPath> plans)
    {
        if (ReEndOnAWayIn(location, ArrivalAt(location, atStart, plans, new(), new(), null)) is Vector2 on) return on;
        var (points, _, _) = Flatten(plans);
        if (points.Count == 0) return null;
        Vector2 end = points[atStart ? 0 : points.Count - 1];
        Vector2? best = null;
        foreach (LocationWaysIn.Way way in LocationWaysIn.There(location))
            if (way.Approach is StairRun run && RoadStairRuns.RoadEndOf(run, location) is var foot
                && (best == null || Vector2.Distance(foot, end) < Vector2.Distance(best.Value, end)))
                best = foot;
        return best;
    }

    /// <summary>The staircase a road lays onto its location at its start or end: its way onto a placed location's landing, or its
    /// approach (<see cref="LocationApproaches"/>, laid at its path's first or last point). Null where none.</summary>
    private static RoadStairSpan? StairsAt(bool atStart, List<(PlacedLocation placedLocation, RoadSpatialGrid.PlannedPath? road, RoadStairSpan? stairs)> entries,
        List<RoadStairSpan> approaches, PlacedLocation? placedLocation)
    {
        if (placedLocation != null && entries.Find(e => e.placedLocation == placedLocation).stairs is RoadStairSpan entry) return entry;
        return approaches.Find(a => (a.FromIndex == 0) == atStart);
    }

    /// <summary>How a road arrives at <paramref name="location"/> at its start or end (<see cref="LocationWaysIn.Way"/>): its staircase
    /// there where it lays one (<see cref="StairsAt"/>), else its road with its road onto a placed location's landing, over its last
    /// stretch.</summary>
    private static LocationWaysIn.Way? ArrivalAt(Vector2 location, bool atStart, List<RoadSpatialGrid.PlannedPath> plans,
        List<(PlacedLocation placedLocation, RoadSpatialGrid.PlannedPath? road, RoadStairSpan? stairs)> entries, List<RoadStairSpan> approaches, PlacedLocation? placedLocation)
    {
        if (StairsAt(atStart, entries, approaches, placedLocation) is RoadStairSpan stairs) return LocationWaysIn.Of(stairs.Plan, location);
        var (points, heights, _) = Flatten(plans);
        if (points.Count == 0) return null;
        var line = new List<Vector2>(points);
        var hs = new List<float>(heights);
        if (atStart) { line.Reverse(); hs.Reverse(); }
        if (placedLocation != null && entries.Find(e => e.placedLocation == placedLocation).road is RoadSpatialGrid.PlannedPath entry && entry.Points.Count > 0)
        {
            bool fromEnd = Vector2.Distance(entry.Points[0], line[line.Count - 1]) <= Vector2.Distance(entry.Points[entry.Points.Count - 1], line[line.Count - 1]);
            for (int k = 0; k < entry.Points.Count; k++)
            {
                int i = fromEnd ? k : entry.Points.Count - 1 - k;
                line.Add(entry.Points[i]);
                hs.Add(entry.Heights[i]);
            }
        }
        return LocationWaysIn.Of(line, hs, location);
    }

    /// <summary>Whether a road refused at <paramref name="at"/> (null or empty where its refusal does not say) may yet be built
    /// ending at the grade's stand-off instead of the MWL stand-off at <paramref name="bandedEnds"/>: refused where it does not
    /// say, or somewhere on the stretch the grade's stand-off gives up (within it of a banded end's place, and a path cell). A
    /// refusal elsewhere on the road is the same refusal searched again.</summary>
    internal static bool RefusedAtABandedEnd(IReadOnlyList<Vector2>? at, IReadOnlyList<(Vector2 centre, float graded)>? bandedEnds)
    {
        if (at == null || at.Count == 0 || bandedEnds == null) return true;
        foreach (var (centre, graded) in bandedEnds)
            foreach (Vector2 p in at)
                if ((p - centre).sqrMagnitude <= (graded + ReachPastEdge) * (graded + ReachPastEdge)) return true;
        return false;
    }

    /// <summary>The stand-off radius (<see cref="StandOffRadius"/>) at the ground along <paramref name="dir"/>: the first path cell
    /// out from the ring (<see cref="ReachPastEdge"/>) where the ground is low enough under the location for its own stand-off to
    /// fall inside it. A march, not a fixed point, so ground steeper than the grade cap still gives the nearest such cell; never
    /// past the stand-off from height 0.</summary>
    private static float StandOffOnGround(Vector2 centre, float ring, float locationLevel, Vector2 dir, WorldGenerator world)
    {
        float limit = StandOffRadius(ring, locationLevel, 0f);
        for (float r = ring; r < limit; r += ReachPastEdge)
        {
            Vector2 at = centre + dir * r;
            if (StandOffRadius(ring, locationLevel, BiomeBlendedHeight.GetBlendedHeight(at.x, at.y, world)) <= r) return r;
        }
        return limit;
    }

    /// <summary>The stand-off radius kept per location (<see cref="LocationKey"/>) for the reached test's reach (<see cref="FallbackReach"/>):
    /// the largest of the grade's round the location (eight bearings, read once) and of every road end trimmed to it
    /// (<see cref="OffLevelStandOff"/>). The grade's, the outermost a road's end may stand at, so an end at the MWL stand-off
    /// (<see cref="StandOffPastRing"/>) and one at the grade's alike are in reach.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, float), float> m_standOffRadius = new();

    private static float CompassStandOff(Vector2 centre, float ring, float locationLevel, WorldGenerator world)
    {
        float most = ring;
        for (int i = 0; i < 8; i++)
        {
            float a = i * 45f * Mathf.Deg2Rad;
            most = Mathf.Max(most, StandOffOnGround(centre, ring, locationLevel, new Vector2(Mathf.Sin(a), Mathf.Cos(a)), world));
        }
        return most;
    }

    /// <summary>
    /// How much farther out than its ring (<see cref="KeepOutEdge"/>) a road arriving from <paramref name="toward"/> (where it
    /// crosses the ring, <see cref="RingCrossing"/>) ends at a custom location that stands its road's end off
    /// (<see cref="StandOffLevel"/>): the end on that bearing at the MWL stand-off (<see cref="StandOffInBand"/>), else
    /// (none there, or the road searched again without it) its stand-off at the ground (<see cref="StandOffOnGround"/>), that
    /// one kept where nearer than the MWL stand-off (a small rise); the grade's kept for the reached test (<see cref="m_standOffRadius"/>).
    /// 0 for every other location.
    /// </summary>
    internal static float OffLevelStandOff(Vector2 centre, float radius, Vector2 toward)
    {
        var world = GenerationWorld;
        if (world == null || StandOffLevel(centre, radius) is not float locationLevel) return 0f;
        float ring = KeepOutEdge(centre, radius, RoadWidth);
        Vector2 dir = toward - centre;
        dir = dir.sqrMagnitude < 1e-4f ? new Vector2(0f, 1f) : dir.normalized;
        float graded = StandOffOnGround(centre, ring, locationLevel, dir, world), at = graded;
        if (!t_noStandOffBand && BandBefore(ring, graded) && StandOffInBand(centre, radius, ring, dir, world) is float near)
        {
            at = near;
            (t_bandedEnds ??= new()).Add((centre, graded));
        }
        m_standOffRadius.AddOrUpdate(LocationKey(centre), _ => Mathf.Max(CompassStandOff(centre, ring, locationLevel, world), graded), (_, old) => Mathf.Max(old, graded));
        return at - ring;
    }

    /// <summary>Where a road's path first stands outside the ring of radius <paramref name="ring"/> round
    /// <paramref name="centre"/>, from its start (<paramref name="fromStart"/>) or its end: the bearing the trim cuts it on. The
    /// search starts at the location's centre (or its ground), so the path's own end gives no bearing. The path's far end where
    /// none does.</summary>
    internal static Vector2 RingCrossing(List<Vector2> path, Vector2 centre, float ring, bool fromStart)
    {
        for (int k = 0; k < path.Count; k++)
        {
            Vector2 q = path[fromStart ? k : path.Count - 1 - k];
            if ((q - centre).sqrMagnitude > ring * ring) return q;
        }
        return path[fromStart ? path.Count - 1 : 0];
    }

    /// <summary>Whether network at <paramref name="q"/> (height <paramref name="h"/>) stands where a road to a custom location
    /// standing its end off (<paramref name="location"/>, its <see cref="StandOffLevel"/>) ends (<see cref="StandOffRadius"/>): within
    /// a path cell of it, or, where that lies past the MWL stand-off, at the MWL stand-off's end on its bearing
    /// (<see cref="AtBandEnd"/>, the trim's own owner). True for every other location (<paramref name="locationLevel"/> null).</summary>
    private static bool AtStandOff(Vector2 flat, float radius, float? locationLevel, Vector2 q, float h)
    {
        if (locationLevel is not float own) return true;
        float ring = KeepOutEdge(flat, radius, RoadWidth), graded = StandOffRadius(ring, own, h), d = Vector2.Distance(q, flat);
        return Mathf.Abs(d - graded) <= ReachPastEdge
            || BandBefore(ring, graded) && AtBandEnd(flat, radius, ring, q, d);
    }

    /// <summary>The one ring test of network at <paramref name="q"/> (height <paramref name="h"/>) at a location reached off its
    /// level (at a prepared ring on the first attempt too, or a custom location standing its end off, <paramref name="locationLevel"/> its
    /// <see cref="StandOffLevel"/>): at its stand-off (<see cref="AtStandOff"/>, the cheap test first), within the ring's height range
    /// (<see cref="RingLevel"/>) where not standing below a higher custom location, and not behind a ridge
    /// from it (<see cref="BehindRidge"/>), both read from the network's own height. Asked by the reached test
    /// (<see cref="Reached"/>) and by the refusal of a road's end (<see cref="EndNotLevel"/>) alike, so a road the fallback
    /// commits is never left unserved.</summary>
    private static bool OffLevelAt(Vector2 flat, float radius, float? locationLevel, Vector2 q, float h) =>
        AtStandOff(flat, radius, locationLevel, q, h) && RingLevel(flat, radius, q, h) && !BehindRidge(flat, radius, q, h);

    /// <summary>How far past the ring the network is looked for at a location on the fallback (<see cref="ReachPastEdge"/>); for a
    /// custom location standing its end off (<paramref name="locationLevel"/>, its <see cref="StandOffLevel"/>) out to its kept stand-off
    /// (<see cref="m_standOffRadius"/>) and a path cell.</summary>
    private static float FallbackReach(Vector2 flat, float radius, float? locationLevel)
    {
        float ring = KeepOutEdge(flat, radius, RoadWidth);
        var world = GenerationWorld;
        if (locationLevel is not float own || world == null) return ring + ReachPastEdge;
        return m_standOffRadius.GetOrAdd(LocationKey(flat), _ => CompassStandOff(flat, ring, own, world)) + ReachPastEdge;
    }

    /// <summary>How many locations the network reaches only off their level, on the fallback (<see cref="ReachOffLevel"/>).</summary>
    private static int m_reachedOffLevel;
    /// <summary>For tests and the summary: locations reached off level this generation.</summary>
    internal static int ReachedOffLevelCount => m_reachedOffLevel;

    /// <summary>Whether the location at <paramref name="centre"/> (<paramref name="prefab"/>, null where not known) may fall
    /// back to a road ending off its level (<see cref="ReachOffLevel"/>): not a vanilla location in the Mountains, where it is reached at
    /// height or not at all (its approach staircase or its landing), a custom location excepted (<see cref="IsModLocation"/>); not a boss's entrance (<see cref="OwnLandingOnly"/>), reached
    /// only on its own landing; not a location with a door (a crypt, <see cref="PlacedLocationShape.DoorLandings"/>), reached only
    /// at its door.</summary>
    internal static bool OffLevelAllowed(Vector2 centre, string? prefab)
    {
        if (prefab != null && (OwnLandingOnly(prefab) || PlacedLocationShape.HasDoor(prefab))) return false;
        if (PlacedLocations.TryGetReady(centre, out PlacedLocation placed)
            && (OwnLandingOnly(placed.Record.Prefab) || PlacedLocationShape.HasDoor(placed.Record.Prefab))) return false;
        var world = GenerationWorld;
        if (world == null) return false;
        return RoadRules.For(RoadBiome.OfLocation(world, centre)).KeepsOffLevel || IsModLocation(prefab);
    }

    /// <summary>A location mod's location (<see cref="IsCustomLocation"/>: registered through the API or named in the CustomLocations
    /// config, never one of the vanilla names in <see cref="LocationPriorities"/>), in any biome. It is the one Mountains location
    /// that may fall back to a road ending off its level, and wherever it is reached without its height (every biome) its
    /// road's end stands off from its own level (<see cref="StandOffLevel"/>). The fallback keeps every protection of
    /// <see cref="ReachOffLevel"/> (<see cref="BehindRidge"/>, the earthwork cap, the grade cap).</summary>
    private static bool IsModLocation(string? name) =>
        name != null && !LocationPriorities.ContainsKey(name) && IsCustomLocation(name);

    /// <summary>
    /// The fallback for a location its first attempt, at height, left unserved: tried again without height
    /// (<see cref="HeightNeeded"/> false for it alone), by <paramref name="attempt"/>, the old behaviour, a road ending at its
    /// ring within two junction steps of its level (RingLevel), or at a custom location's stand-off. Asked only of a location the network does not reach at height and that may fall back
    /// (<see cref="OffLevelAllowed"/>); first whether the network already there reaches it off level, no road needed. True
    /// where the location is reached that way: it stays without height for the rest of the generation (the count, a later
    /// road's stub), counted and logged ("reached off level"). Otherwise it needs height again, and is unserved; where only
    /// network behind a ridge from it was in reach (<see cref="BehindRidge"/>), counted and logged as that ("behind a ridge").
    /// </summary>
    internal static bool ReachOffLevel(string name, Vector3 position, float radius, Func<bool> attempt)
    {
        var centre = new Vector2(position.x, position.z);
        if (radius <= 0f || !OffLevelAllowed(centre, name) || Reached(centre, radius, name)) return false;
        (float, float) key = LocationKey(centre);
        if (m_offLevel.TryAdd(key, true)) System.Threading.Interlocked.Increment(ref s_reachedLocationsRevision);
        NoteCustomLocation(name, position);
        IslandNetworkChanged();
        if (Reached(centre, radius, name) || attempt() && Reached(centre, radius, name))
        {
            System.Threading.Interlocked.Increment(ref m_reachedOffLevel);
            string prefab = PlacedLocations.TryGetReady(centre, out PlacedLocation placed) ? placed.Record.Prefab : name;
            string off = ArrivalOffset(centre, radius) is (float locationLevel, float offset) ? FormattableString.Invariant($", the network {offset:+0.0;-0.0} m off its level ({locationLevel:F1} m)") : "";
            Log.LogInfo(FormattableString.Invariant($"  reached off level: {prefab} at ({position.x:F0}, {position.z:F0}){off}"));
            return true;
        }
        // Network behind a location entered only from its front is not in its reach (PlacedLocationShape.OnEntranceSide).
        Func<Vector2, float, bool> inReach = PlacedLocations.TryGetReady(centre, out PlacedLocation placedLocation)
            ? (q, h) => placedLocation.Shape.OnEntranceSide(q) && RingLevel(centre, radius, q, h)
            : (q, h) => RingLevel(centre, radius, q, h);
        if (NetworkNear(centre, KeepOutEdge(centre, radius, RoadWidth) + ReachPastEdge, inReach, true))
        {
            System.Threading.Interlocked.Increment(ref m_behindRidge);
            Log.LogInfo(FormattableString.Invariant($"  behind a ridge: {name} at ({position.x:F0}, {position.z:F0}), the network in reach ends behind ground over {RidgeRise:F0} m above it and the location: unserved"));
        }
        if (m_offLevel.TryRemove(key, out _)) System.Threading.Interlocked.Increment(ref s_reachedLocationsRevision);
        IslandNetworkChanged();
        return false;
    }

    /// <summary>How many locations the fallback left unserved because the network in their reach ends behind a ridge from them
    /// (<see cref="ReachOffLevel"/>, <see cref="BehindRidge"/>).</summary>
    private static int m_behindRidge;
    /// <summary>For tests and the summary: locations left unserved behind a ridge this generation.</summary>
    internal static int BehindRidgeCount => m_behindRidge;

    /// <summary>
    /// How far the ground between a road's end and the location it reaches off level may rise over both of them
    /// (<see cref="RidgeOver"/>) before the end is behind a ridge from the location and does not reach it (<see cref="BehindRidge"/>).
    /// Mistlands_DvergrTownEntrance2 at (1344, 7168) was reached by a road ending 42 m off, at 46.4 m, behind a ridge 61.6 m
    /// high, the entrance at 36.1 m beyond it: stranded on the wrong side of the ridge (15.2 m over the end, on the ground in
    /// the game). A climb up to a location higher than the end, which the player sees from the road, rises no higher than the
    /// location: 0 m over both, however high the location, and is kept. The most ground a road is cut through
    /// (<see cref="ShapedLimit"/>: the earthwork cap, 8 m, never past what the terrain moves); a rise past it between the end
    /// and the location is ground no road could have been carried through, the location on its other side. Measured offline on eight worlds' fallbacks (143 arrivals): that ridge 15.6 m,
    /// then none over 7.2 m.
    /// </summary>
    internal static float RidgeRise => ShapedLimit;
    /// <summary>The ground over the first metres from a road's end is not read for a ridge: the road's own half-width
    /// (<see cref="RoadWidth"/>) and the blend past its edge (<see cref="RoadConstants.TerrainBlendMargin"/>), where its cut or
    /// fill, not the ground, decides.</summary>
    internal static float RidgePastEnd => RoadWidth / 2f + RoadConstants.TerrainBlendMargin;

    /// <summary>
    /// How far the highest ground on the straight line from <paramref name="end"/> (a road's end, at
    /// <paramref name="endHeight"/>) to <paramref name="location"/> (the location's landing or edge, at <paramref name="locationHeight"/>)
    /// rises over the higher of the two, read every metre on <paramref name="ground"/> from <see cref="RidgePastEnd"/> on; 0 or
    /// less where nothing stands over them. The one measure of a ridge between them (<see cref="BehindRidge"/>).
    /// </summary>
    internal static float RidgeOver(Vector2 end, float endHeight, Vector2 location, float locationHeight, Func<Vector2, float> ground)
    {
        float length = Vector2.Distance(end, location), peak = float.NegativeInfinity;
        int steps = Mathf.Max(1, Mathf.CeilToInt(length));
        for (int i = 0; i <= steps; i++)
            if (length * i / steps >= RidgePastEnd) peak = Mathf.Max(peak, ground(Vector2.Lerp(end, location, (float)i / steps)));
        return float.IsNegativeInfinity(peak) ? 0f : peak - Mathf.Max(endHeight, locationHeight);
    }

    /// <summary>
    /// Whether network at <paramref name="at"/> (its height <paramref name="height"/>) is behind a ridge from the location at
    /// <paramref name="centre"/> (exterior <paramref name="radius"/>, as asked): the ground on the line to the location's landing
    /// (placed first) or to its edge on that side, at its arrival height (<see cref="ArrivalHeight"/>), rises more than
    /// <see cref="RidgeRise"/> over both (<see cref="RidgeOver"/>), on the ground as locations leave it
    /// (<see cref="PlacedLocations.GroundAt(float, float, WorldGenerator)"/>). The one test wherever a ring end
    /// off level is otherwise accepted: by the reached test (<see cref="Reached"/>) and the refusal of a road's end
    /// (<see cref="EndNotLevel"/>).
    /// </summary>
    internal static bool BehindRidge(Vector2 centre, float radius, Vector2 at, float height)
    {
        var world = GenerationWorld;
        if (world == null || radius <= 0f || ArrivalHeight(centre, radius, at) is not float level) return false;
        Vector2 to;
        if (PlacedLocations.TryGetReady(centre, out PlacedLocation placed)) to = placed.Shape.Landing;
        else
        {
            to = EdgeToward(centre, radius, at);
        }
        return RidgeOver(at, height, to, level, q => PlacedLocations.GroundAt(q.x, q.y, world)) > RidgeRise;
    }

    /// <summary>Whether a road's end at the location at <paramref name="centre"/> (exterior <paramref name="radius"/>, as
    /// asked) is already reached by other network, so the road's own stub there may be dropped (<see cref="DropStubs"/>):
    /// the one reached test (<see cref="Reached"/>), with the location's name, so a boss's entrance not placed first keeps its
    /// own stub. An end with no location (radius 0) is a junction on the network. The one owner of what counts as reached (docs/OWNERS.md).</summary>
    internal static bool EndReached(Vector2 centre, float radius) =>
        radius <= 0f || RoadSpatialGrid.IsInitialized && Reached(centre, radius, LocationPrefabLevelling.PrefabAt(centre));

    /// <summary>The location at <paramref name="centre"/> (exterior <paramref name="radius"/>) a road's search may leave or enter
    /// through its own footprint (RoadLocationProtection.Targets, by identity), or null: none for an end that is no location (radius
    /// 0), nor for a location placed first searched to its doors (<paramref name="door"/>), whose lead-ins stand outside it.</summary>
    private static Vector2? SearchedInto(Vector2 centre, float radius, bool placed, bool door) =>
        radius > 0f && !(placed && door) ? centre : null;

    /// <summary>Whether road or a staircase's walked piece (its walking surface, <see cref="RoadStairRuns.Surface"/>) lies
    /// within <paramref name="reach"/> of <paramref name="at"/>, level with <paramref name="level"/> (within
    /// <see cref="ReachRise"/>; at any height where it is not known): the network a location is reached by. The one height
    /// guard for every location, placed first or not.</summary>
    private static bool NetworkNear(Vector2 at, float reach, float? level) =>
        NetworkNear(at, reach, level.HasValue ? _ => level.Value : null);

    /// <summary><see cref="NetworkNear(Vector2, float, float?)"/> with the level the network must meet where it stands
    /// (a location's ground at its edge on that side, <see cref="ArrivalHeight"/>).</summary>
    private static bool NetworkNear(Vector2 at, float reach, Func<Vector2, float>? level)
    {
        if (level == null)
            return RoadSpatialGrid.TryGetRoadWithin(at, reach, out _)
                || StairStructures.PiecesNear(new Vector3(at.x, 0f, at.y), reach).Exists(p => p.Kind != StructurePieceKind.StairSupport);
        return NetworkNear(at, reach, (q, h) => Mathf.Abs(h - level(q)) <= ReachRise, false);
    }

    /// <summary><see cref="NetworkNear(Vector2, float, float?)"/> for network a test <paramref name="accepts"/> where it stands
    /// (its point and its height): level with the location, road only; or on the fallback not behind a ridge from it
    /// (<see cref="BehindRidge"/>), with <paramref name="paintToo"/> the same network as at any height, a ford's painted points
    /// as well (<see cref="RoadSpatialGrid.TryGetRoadWithin"/>: Bonemass and a sunken crypt, reached over a ford).</summary>
    private static bool NetworkNear(Vector2 at, float reach, Func<Vector2, float, bool> accepts, bool paintToo)
    {
        foreach (RoadSpatialGrid.RoadPoint q in RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(at.x, 0f, at.y), reach))
            if ((paintToo ? q.Walked : q.IsRoad) && (q.p - at).sqrMagnitude <= reach * reach && accepts(q.p, q.h)) return true;
        return StairStructures.PiecesNear(new Vector3(at.x, 0f, at.y), reach).Exists(p => p.Kind != StructurePieceKind.StairSupport
            && accepts(new Vector2(p.Position.x, p.Position.z), RoadStairRuns.Surface(p)));
    }

    /// <summary>For the reports (an unreached location's line): the height the location is arrived at
    /// (a placed location's landing ground, else <see cref="ArrivalHeight"/>) and how far over (+) or under (-) it the network
    /// within the location's reach comes nearest to level, road or a staircase's walked piece; offset null where no network is
    /// in reach. Null where the location's height is not known.</summary>
    internal static (float locationLevel, float? offset)? ArrivalOffset(Vector2 flat, float radius)
    {
        if (radius <= 0f || !RoadSpatialGrid.IsInitialized) return null;
        Vector2 at;
        float reach;
        float? height;
        if (PlacedLocations.TryGetReady(flat, out PlacedLocation placed))
        {
            at = placed.Shape.Landing;
            reach = LandingReach;
            height = placed.LandingHeight;
        }
        else
        {
            at = flat;
            reach = KeepOutEdge(flat, radius, RoadWidth) + ReachPastEdge;
            height = ArrivalHeight(flat, radius, flat);
        }
        if (height is not float locationLevel) return null;
        Func<Vector2, float> level = ArrivalLevelFrom(flat, radius) ?? (_ => locationLevel);
        float? best = null;
        void Offer(Vector2 q, float h) { float off = h - level(q); if (best == null || Mathf.Abs(off) < Mathf.Abs(best.Value)) best = off; }
        foreach (var (q, h) in RoadsNear(at, reach)) Offer(q, h);
        foreach (StructurePiece p in StairStructures.PiecesNear(new Vector3(at.x, 0f, at.y), reach))
            if (p.Kind != StructurePieceKind.StairSupport && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), at) <= reach)
                Offer(new Vector2(p.Position.x, p.Position.z), RoadStairRuns.Surface(p));
        return (locationLevel, best);
    }

    /// <summary>How near a placed location's landing the network must come to reach it: the road onto it keeps
    /// its own reach (half its width and its blend) off the entrance's pit, so it stops a few metres short
    /// of the shape's landing point (4.8 m on the test cave).</summary>
    public const float LandingReach = 8f;

    /// <summary>For an unreached placed location's line: how near the network comes to its landing.</summary>
    private static string NearestToLanding(PlacedLocation placedLocation)
    {
        Vector2 landing = placedLocation.Shape.Landing;
        float road = RoadSpatialGrid.IsInitialized && RoadSpatialGrid.TryGetRoadWithin(landing, 200f, out Vector2 r) ? Vector2.Distance(r, landing) : float.PositiveInfinity;
        float stair = float.PositiveInfinity;
        foreach (StructurePiece p in StairStructures.PiecesNear(new Vector3(landing.x, 0f, landing.y), 200f))
            if (p.Kind != StructurePieceKind.StairSupport) stair = Mathf.Min(stair, Vector2.Distance(new Vector2(p.Position.x, p.Position.z), landing));
        return FormattableString.Invariant($"; placed first, its landing ({landing.x:F1}, {landing.y:F1}): nearest road {road:F1} m, nearest stair piece {stair:F1} m");
    }

    /// <summary>Whether <paramref name="prefab"/> is reached only by a road onto its own landing (a boss's entrance), not by a
    /// road passing within reach of it.</summary>
    internal static bool OwnLandingOnly(string prefab) =>
        prefab.IndexOf("BossEntrance", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Whether the network comes onto a placed location's landing: a road or staircase was carried on
    /// onto it (<see cref="PlacedLocations.MarkLanded"/>: laid to its landing's level ground, <see cref="PlacedLocationShape.Aims"/>),
    /// or road or a staircase's walked piece is near the shape's landing point, level with the ground there
    /// (<see cref="NetworkNear"/>, as every other location: without a height guard network 20-45 m under a landing counted
    /// it reached). The one owner of what counts as reached (docs/OWNERS.md).</summary>
    internal static bool LandingReached(PlacedLocation placedLocation) =>
        PlacedLocations.Landed(placedLocation.Centre) || NetworkNear(placedLocation.Shape.Landing, LandingReach, placedLocation.LandingHeight);

    /// <summary>The edge of the ground a location shapes (<see cref="RoadLocationProtection.ArrivalRadiusAt"/>) on the side of
    /// <paramref name="q"/> (due +z where <paramref name="q"/> is within 1 cm of the centre).</summary>
    private static Vector2 EdgeToward(Vector2 centre, float radius, Vector2 q)
    {
        Vector2 out1 = q - centre;
        out1 = out1.sqrMagnitude < 1e-4f ? new Vector2(0f, 1f) : out1.normalized;
        return centre + out1 * RoadLocationProtection.ArrivalRadiusAt(centre, radius);
    }

    /// <summary>
    /// The height the network arriving at the location at <paramref name="centre"/> (exterior <paramref name="radius"/>, as asked)
    /// from <paramref name="from"/> must end level with to reach it (<see cref="ReachRise"/>): the one owner of a location's arrival
    /// height (the location's ground at its edge). Placed before its roads, its landing's ground
    /// (<see cref="PlacedLocationShape.Landing"/> on its placed ground). Else its single levelled platform, never below its root
    /// (<see cref="LocationLevelling.ApproachHeight"/>), the flat it is entered on. Else (no flat, several levels, or no
    /// levelling known) its ground at its edge on that side: the edge of the ground it shapes
    /// (<see cref="RoadLocationProtection.ArrivalRadiusAt"/>) toward <paramref name="from"/>, as its levelling round its centre leaves
    /// it (<see cref="PlacedLocations.ExpectedGround"/>). A cabin only smooths the ground round it: with no level known, a
    /// road 10 m under AbandonedLogCabin04 reached it; its ground at its edge on the road's side (10 m out, 185.3 m) is still 4.7
    /// m over the road's end. Its root, the ground under its centre, is not the level of a location entered from its side on a
    /// hillside (a troll cave's mouth). Null outside a generation, or for a point with no radius.
    /// </summary>
    internal static float? ArrivalHeight(Vector2 centre, float radius, Vector2 from)
    {
        if (PlacedLocations.TryGetReady(centre, out PlacedLocation placedLocation)) return placedLocation.LandingHeight;
        // Placed only to aim at (PlacedLocations.PlaceMistlandsDestinations), with a landing: its road stops at its edge, level with it.
        if (PlacedLocations.TryGetAimedLanding(centre, out float aimed)) return aimed;
        var world = GenerationWorld;
        if (world == null || radius <= 0f) return null;
        float root = LocationLevelling.CentreHeight(centre, world);
        IReadOnlyList<LevelOp>? ops = LocationLevelling.OpsAt(centre);
        if (LocationLevelling.ApproachHeight(root, ops) is float platform) return platform;
        Vector2 edge = EdgeToward(centre, radius, from);
        return PlacedLocations.ExpectedGround(centre, root, ops, world)(edge.x, edge.y);
    }

    /// <summary><see cref="ArrivalHeight"/> for network anywhere round the location, its edge read once a 5 degree bearing (a
    /// reached test asks of every road point in reach); null where the height is not known. Pure while preparation is
    /// closed (<see cref="LocationLevelling.Sealed"/>: the locations placed, their levelling and roots fixed), so kept per location
    /// for the rest of that generation's islands (<see cref="m_levelFrom"/>), its bearings shared by every worker.</summary>
    private static Func<Vector2, float>? ArrivalLevelFrom(Vector2 centre, float radius)
    {
        if (!LocationLevelling.Sealed) return BuildArrivalLevelFrom(centre, radius);
        int seal = LocationLevelling.SealCount;
        if (m_levelFromSeal != seal)
            lock (m_levelFrom)
                if (m_levelFromSeal != seal) { m_levelFrom.Clear(); m_levelFromSeal = seal; }
        return m_levelFrom.GetOrAdd((LocationKey(centre), radius), _ => BuildArrivalLevelFrom(centre, radius));
    }

    /// <summary>The locations' arrival levels (<see cref="ArrivalLevelFrom"/>) while preparation stays closed, by identity (LocationKey)
    /// and radius as asked; emptied when it is next closed (<see cref="m_levelFromSeal"/>).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<((float, float), float), Func<Vector2, float>?> m_levelFrom = new();
    private static volatile int m_levelFromSeal = -1;

    private static Func<Vector2, float>? BuildArrivalLevelFrom(Vector2 centre, float radius)
    {
        if (ArrivalHeight(centre, radius, centre) is not float level) return null;
        if (PlacedLocations.TryGetReady(centre, out _) || PlacedLocations.TryGetAimedLanding(centre, out _)) return _ => level;
        var world = GenerationWorld!;
        if (LocationLevelling.ApproachHeight(LocationLevelling.CentreHeight(centre, world), LocationLevelling.OpsAt(centre)).HasValue)
            return _ => level;
        var byBearing = new System.Collections.Concurrent.ConcurrentDictionary<int, float>();
        return q =>
        {
            int bearing = Mathf.RoundToInt(Mathf.Atan2(q.y - centre.y, q.x - centre.x) * Mathf.Rad2Deg / 5f);
            return byBearing.GetOrAdd(bearing, b =>
            {
                float a = b * 5f * Mathf.Deg2Rad;
                return ArrivalHeight(centre, radius, centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)))!.Value;
            });
        };
    }

    /// <summary>Why a road's end at one of its locations does not end level with the location (<see cref="ReachRise"/> of
    /// <see cref="ArrivalHeight"/> on its side, the one test <see cref="Reached"/> makes) and no approach staircase was laid from it onto the
    /// location (<see cref="LocationApproaches"/>) nor a way onto its landing (a placed location's, <see cref="PlanEntry"/>), or null. The
    /// end is where walking ends: the last road point, or the staircase the road ends on. Over as well as under, in every
    /// biome: such a road would not reach its location, so it is refused and the planner tries its other candidates (or the
    /// location goes unserved), but at a location on its second attempt, without height (<see cref="HeightNeeded"/>,
    /// <see cref="ReachOffLevel"/>). Before, only in the Mountains or at a boss's entrance; elsewhere an end off level was
    /// kept and counted reached, which is now that fallback.</summary>
    internal static (string refusal, string why)? EndNotLevel(List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans, List<RoadStairSpan> stairs,
        List<RoadStairSpan> approaches, Vector2 startCenter, float startRadius, Vector2 endCenter, float endRadius, bool startAtLocation,
        bool endAtLocation, bool tally = true)
    {
        (string, string)? At(bool atStart, Vector2 centre, float radius, bool atLocation)
        {
            if (!atLocation || radius <= 0f || plans.Count == 0) return null;
            if (approaches.Exists(a => a.FromIndex == (atStart ? 0 : path.Count - 1))) return null;
            float height;
            RoadStairSpan? last = stairs.Find(s => atStart ? s.FromIndex == 0 : s.ToIndex == path.Count - 1);
            if (last != null) height = atStart ? last.FromHeight : last.ToHeight;
            else
            {
                var piece = atStart ? plans[0] : plans[plans.Count - 1];
                if (piece.Heights.Count == 0) return null;
                height = atStart ? piece.Heights[0] : piece.Heights[piece.Heights.Count - 1];
            }
            Vector2 end = path[atStart ? 0 : path.Count - 1];
            // An unentered ring end, on the first attempt or the fallback, asks the reached test's own ring verdict.
            // Being within LandingReach is not a built entry; LocationEndRefusal exempts entries actually planned.
            if (!HeightNeeded(centre) || PlacedLocations.TryGetReady(centre, out PlacedLocation placedLocation) && RingEnterable(placedLocation))
            {
                float? standOff = StandOffLevel(centre, radius);
                if (OffLevelAt(centre, radius, standOff, end, height)) return null;
                if (!RingLevel(centre, radius, end, height))
                    return (RingLevelRefusal, FormattableString.Invariant($"ends at its ring off its landing level ({height:F1} m)"));
                return AtStandOff(centre, radius, standOff, end, height)
                    ? (RidgeRefusal, FormattableString.Invariant($"ends behind ground over {RidgeRise:F0} m above it and its location"))
                    : (NotLevelRefusal, FormattableString.Invariant($"ends {(standOff ?? height) - height:+0.0;-0.0} m off its location, not at its stand-off, {Vector2.Distance(end, centre):F1} m out"));
            }
            // A custom location with no flat nor landing standing above the end: at its stand-off, as the reached test.
            if (StandOffLevel(centre, radius) is float own && height < own)
                return OffLevelAt(centre, radius, own, end, height) ? null
                    : (NotLevelRefusal, FormattableString.Invariant($"ends {own - height:F1} m under its location, not at its stand-off, {Vector2.Distance(end, centre):F1} m out"));
            if (ArrivalHeight(centre, radius, end) is not float locationLevel) return null;
            float off = height - locationLevel;
            if (Mathf.Abs(off) <= ReachRise)
            {
                // Level enough to reach it, but a stair or more off (no staircase outside the Mountains): counted, kept.
                if (tally && RoadsLog && Mathf.Abs(off) > StairLayout.StairFitsFrom) System.Threading.Interlocked.Increment(ref m_closeEnds);
                return null;
            }
            return (NotLevelRefusal, FormattableString.Invariant($"ends {Mathf.Abs(off):F1} m {(off > 0f ? "over" : "under")} its location, not level with it, and no approach staircase onto it"));
        }
        return At(true, startCenter, startRadius, startAtLocation) ?? At(false, endCenter, endRadius, endAtLocation);
    }

    /// <summary>How many road ends were carried on from a location's ring to its edge (<see cref="CarryOnToEdges"/>).</summary>
    private static int m_carriedToEdge;
    /// <summary>For tests and the summary: road ends carried on to their location's edge this generation.</summary>
    internal static int CarriedToEdgeCount => m_carriedToEdge;
    /// <summary>A road end nearer its location's edge than this is not carried on.</summary>
    private const float CarryOnMin = 0.5f;

    /// <summary>
    /// The last stretch of a road whose end arrives level with a location that has no flat nor landing (not placed first, no single
    /// levelled platform, <see cref="LocationLevelling.ApproachHeight"/>): from its end at the ring (<see cref="KeepOutEdge"/>)
    /// straight on to the location's edge on its side, where its arrival height was measured (<see cref="ArrivalHeight"/>,
    /// <see cref="RoadLocationProtection.ArrivalRadiusAt"/>), from the road's height to that height, as a crypt's road is carried on
    /// to its door. In game: AbandonedLogCabin04 (-1867, 7101), the road at the right height but its end stopping
    /// short of the cabin, the road's whole blend kept outside the ground the cabin shapes. Not on the fallback (an end off
    /// level, <see cref="HeightNeeded"/>), nor from an end on stairs or with an approach staircase; none where the stretch
    /// cannot be planned (<see cref="RoadSpatialGrid.PlanLocationEntry"/>) or its end is in water: the road keeps its end. Only
    /// into a location on the preload list (<see cref="RoadLocationProtection.Enterable"/>: placed first with no landing to go onto,
    /// <see cref="PlacedLocations.FlatlessPlaced"/>), whose ground is known: the road to any other stops at its edge.
    /// </summary>
    private static List<RoadSpatialGrid.PlannedPath> CarryOnToEdges(List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans,
        List<RoadStairSpan> stairs, List<RoadStairSpan> approaches, float width, Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius, bool startAtLocation, bool endAtLocation)
    {
        var carried = new List<RoadSpatialGrid.PlannedPath>();
        var world = GenerationWorld;
        if (world == null || path.Count < 2 || plans.Count == 0) return carried;
        void At(bool atStart, Vector2 centre, float radius, bool atLocation)
        {
            // Only into a location that may be entered (on the preload list): at any other the road stops at its edge.
            if (!atLocation || radius <= 0f || PlacedLocations.TryGetReady(centre, out _) || !RoadLocationProtection.Enterable(centre) || !HeightNeeded(centre)) return;
            if (approaches.Exists(a => a.FromIndex == (atStart ? 0 : path.Count - 1))) return;
            if (RoadEnd(atStart, path, plans, stairs) is not var (end, height, onStairs) || onStairs != null) return;
            float root = LocationLevelling.CentreHeight(centre, world);
            IReadOnlyList<LevelOp>? ops = LocationLevelling.OpsAt(centre);
            if (LocationLevelling.ApproachHeight(root, ops).HasValue) return;
            // Its end stood off a custom location standing above it (StandOffLevel) stays there.
            if (StandOffLevel(centre, radius) is float own && height < own) return;
            Vector2 out1 = end - centre;
            float arrival = RoadLocationProtection.ArrivalRadiusAt(centre, radius), from = out1.magnitude;
            if (from - arrival < CarryOnMin) return;
            if (ArrivalHeight(centre, radius, end) is not float locationLevel || Mathf.Abs(height - locationLevel) > ReachRise) return;
            Vector2 edge = centre + out1 * (arrival / from);
            Func<float, float, float> expected = PlacedLocations.ExpectedGround(centre, root, ops, world);
            float Ground(Vector2 q) => expected(q.x, q.y);
            world.GetRiverWeight(edge.x, edge.y, out float river, out _);
            if (!RoadWater.BuiltDry(Ground(edge), river)) return;
            var line = new List<Vector2> { end, edge };
            RoadSpatialGrid.PlannedPath? plan = RoadGrade.Trial(() => RoadSpatialGrid.PlanLocationEntry(line, width, world, height, locationLevel, Ground,
                new RoadLocationProtection.Targets(null, centre)));
            if (plan != null && plan.Points.Count >= 2) carried.Add(plan);
        }
        At(true, startCenter, startRadius, startAtLocation);
        At(false, endCenter, endRadius, endAtLocation);
        return carried;
    }

    /// <summary>The refusal a road whose end is not level with its location is counted under (<see cref="EndNotLevel"/>).</summary>
    internal const string NotLevelRefusal = "end not level with its location";
    /// <summary>The refusal a road on the fallback whose end is behind a ridge from its location is counted under
    /// (<see cref="EndNotLevel"/>, <see cref="BehindRidge"/>).</summary>
    internal const string RidgeRefusal = "end behind a ridge from its location";

    /// <summary>Read the crossing registry under the same gate used by commits.
    /// Another island may append while this road looks for shared banks.</summary>
    internal static void SnapToExistingCrossings(IEnumerable<RoadCrossing> crossings)
    {
        lock (m_recordGate)
        {
            // Only this island's crossings and untagged ones: another island's is not this island's network (CrossIslandReads).
            int me = CrossIslandReads.Me;
            RoadCrossing? passed = null;
            bool Seen(RoadCrossing existing) { if (CrossIslandReads.Sees(me, existing.Island)) return true; passed = existing; return false; }
            // A run of back-to-back crossings (a raised pool, a wade, a bridge: one river crossed in
            // pieces) that spans the same water as a crossing another road built is replaced by that
            // one crossing (measured: a 3-piece chain ran 6 m beside a 53 m bridge; piece by piece
            // no bank matched, end to end both did).
            if (CorridorSnap > RoadSnap && crossings is List<RoadCrossing> list && list.Count > 1)
            {
                var crossingsList = list;
                crossingsList.Sort((x, y) => x.FromIndex.CompareTo(y.FromIndex));
                for (int a = 0; a < crossingsList.Count; a++)
                {
                    int b = a;
                    while (b + 1 < crossingsList.Count && Vector2.Distance(crossingsList[b].ToBank, crossingsList[b + 1].FromBank) < 1f) b++;
                    if (b == a) continue;
                    var span = RoadCrossing.Between(crossingsList[a].FromBank, crossingsList[b].ToBank, crossingsList[a].RiverbedHeight,
                        (crossingsList[a].FromBank + crossingsList[b].ToBank) * 0.5f, 0f, crossingsList[a].Kind, crossingsList[a].Style);
                    RoadCrossing? match = null; float best = float.MaxValue;
                    foreach (RoadCrossing existing in m_roadCrossings)
                        if (SameCorridorCrossing(existing, span, out float d) && d < best && Seen(existing)) { best = d; match = existing; }
                    if (match == null) { a = b; continue; }
                    var first = crossingsList[a];
                    int toIndex = crossingsList[b].ToIndex;
                    var toLand = crossingsList[b].ToLand;
                    first.SnapTo(match);
                    first.Shared = true;
                    first.ToIndex = toIndex;
                    first.ToLand = toLand;
                    crossingsList.RemoveRange(a + 1, b - a);
                    System.Threading.Interlocked.Increment(ref m_chainsShared);
                }
            }
            foreach (RoadCrossing crossing in crossings)
            {
                RoadCrossing? match = null;
                foreach (RoadCrossing existing in m_roadCrossings)
                    if (RoadCrossing.SameBanks(existing, crossing) && Seen(existing)) { match = existing; break; }
                // Two roads crossing the same water side by side share one crossing (the corridor
                // snap's rule for crossings; measured: two roads left one place through a pool,
                // each with its own ford 6.5 m apart). Nearest qualifying site wins.
                if (match == null && CorridorSnap > RoadSnap)
                {
                    float best = float.MaxValue;
                    foreach (RoadCrossing existing in m_roadCrossings)
                        if (SameCorridorCrossing(existing, crossing, out float d) && d < best && Seen(existing)) { best = d; match = existing; }
                }
                if (match != null)
                {
                    crossing.SnapTo(match);
                    crossing.Shared = true;
                }
            }
            if (passed != null) CrossIslandReads.Skipped(CrossIslandReads.Kind.Crossings, passed.Island, passed.Center);
        }
    }

    /// <summary>Both banks within <see cref="CorridorSnap"/> of the other crossing's (either order),
    /// heading within <see cref="CorridorAngle"/>, bank ground within <see cref="CorridorHeight"/>.</summary>
    internal static bool SameCorridorCrossing(RoadCrossing existing, RoadCrossing c, out float distance)
    {
        distance = float.MaxValue;
        if (Mathf.Abs(Vector2.Dot(existing.Direction, c.Direction)) < Mathf.Cos(CorridorAngle * Mathf.PI / 180f)) return false;
        bool same = (existing.FromBank - c.FromBank).sqrMagnitude <= (existing.FromBank - c.ToBank).sqrMagnitude;
        Vector2 ef = same ? existing.FromBank : existing.ToBank, et = same ? existing.ToBank : existing.FromBank;
        float df = Vector2.Distance(ef, c.FromBank), dt = Vector2.Distance(et, c.ToBank);
        if (df > CorridorSnap || dt > CorridorSnap) return false;
        var world = WorldGenerator.instance;
        if (world != null)
        {
            float H(Vector2 q) => BiomeBlendedHeight.GetBlendedHeight(q.x, q.y, world);
            if (Mathf.Abs(H(ef) - H(c.FromBank)) > CorridorHeight || Mathf.Abs(H(et) - H(c.ToBank)) > CorridorHeight) return false;
        }
        distance = df + dt;
        return true;
    }

    private static int m_roadsGeneratedCount = 0;
    private static List<(Vector2 position, string label)> m_roadStartPoints = new();
    private static readonly List<RoadCrossing> m_roadCrossings = new();
    private static readonly List<CoastalLandingPlace> m_coastalLandings = new();

    public static bool RoadsGenerated => m_roadsGenerated;
    /// <summary>
    /// Whether the world's locations are in place, so roads may be loaded or built.
    ///
    /// Not just "our event fired". Valheim 1.0 writes ZoneSystem's
    /// m_locationsGenerated straight from the save when a world is read from
    /// disk, bypassing the property setter that raises
    /// GenerateLocationsCompleted -- so on an existing world the event never
    /// arrives, no matter how early we subscribed. Before 1.0 the setter was the
    /// only writer and the event always came. Ask the game what is true rather
    /// than relying on having been told.
    /// </summary>
    public static bool IsLocationsReady =>
        m_locationsReady || (ZoneSystem.instance != null && ZoneSystem.instance.LocationsGenerated);

    /// <summary>River crossings of the generated roads (fords and bridges
    /// prototypes): empty unless the network was generated with either on.</summary>
    public static IReadOnlyList<RoadCrossing> GetRoadCrossings() => m_roadCrossings;
    /// <summary>The shore landings of the islands the network was built for (name <see cref="RoadCoastalLandings.Name"/>), by
    /// island then position, each with whether its island's destinations took it and whether the network reaches it.
    /// Saved with the network.</summary>
    public static IReadOnlyList<CoastalLandingPlace> GetCoastalLandings()
    {
        lock (m_recordGate)
            return m_coastalLandings.OrderBy(l => l.IslandId).ThenBy(l => l.Position.x).ThenBy(l => l.Position.z).ToList();
    }
    /// <summary>The crossings and the road start points in a fixed order, whichever island committed its roads first
    /// (the same rule as <see cref="StairStructures.SortCanonical"/>): generation lays islands in parallel, and the saved
    /// bytes must not depend on which finished first. Crossings by their banks, start points by position then label. No
    /// reader takes a crossing by its index or reads a road's crossings as a run (they are matched by banks, walked as
    /// sites, or counted), so the order within a road is not kept.</summary>
    internal static void SortRecordsCanonical()
    {
        lock (m_recordGate)
        {
            m_roadCrossings.Sort((a, b) =>
            {
                int c = a.FromBank.x.CompareTo(b.FromBank.x);
                if (c == 0) c = a.FromBank.y.CompareTo(b.FromBank.y);
                if (c == 0) c = a.ToBank.x.CompareTo(b.ToBank.x);
                if (c == 0) c = a.ToBank.y.CompareTo(b.ToBank.y);
                if (c == 0) c = a.Kind.CompareTo(b.Kind);
                if (c == 0) c = a.Style.CompareTo(b.Style);
                if (c == 0) c = a.ValleyJump.CompareTo(b.ValleyJump);
                return c != 0 ? c : a.RiverbedHeight.CompareTo(b.RiverbedHeight);
            });
            m_roadStartPoints.Sort((a, b) =>
            {
                int c = a.position.x.CompareTo(b.position.x);
                if (c == 0) c = a.position.y.CompareTo(b.position.y);
                return c != 0 ? c : string.CompareOrdinal(a.label, b.label);
            });
        }
    }
    /// <summary>The crossings built so far, copied under the gate commits take (another island may append meanwhile): on an
    /// island worker, its own island's and untagged ones (CrossIslandReads).</summary>
    internal static List<RoadCrossing> CrossingsSnapshot()
    {
        lock (m_recordGate) return Visible(m_roadCrossings);
    }

    /// <summary>A copy of <paramref name="all"/> holding what this thread sees of it (<see cref="CrossIslandReads.Sees(int, int)"/>).
    /// The caller holds the gate. Not counted: the whole list is filtered, near or far; the reads near a location count.</summary>
    private static List<RoadCrossing> Visible(List<RoadCrossing> all)
    {
        int me = CrossIslandReads.Me;
        if (me == 0) return new List<RoadCrossing>(all);
        var visible = new List<RoadCrossing>(all.Count);
        foreach (RoadCrossing c in all)
            if (CrossIslandReads.Sees(me, c.Island)) visible.Add(c);
        return visible;
    }
    public static bool RoadsLoadedFromZDO => m_roadsLoadedFromZDO;
    /// <summary>Whether the network must be saved in full (<see cref="m_networkDirty"/>).</summary>
    public static bool NetworkDirty => m_networkDirty;
    public static bool RoadsAvailable => !RoadLifecycleManager.WorkersOwnWorld && NetworkInMemory;
    /// <summary>Whether a network is in memory (generated, loaded, or added to), whoever may use it now: a generation that
    /// replaces it asks this, as <see cref="RoadsAvailable"/> is false while the generation's workers own the world.</summary>
    internal static bool NetworkInMemory => m_roadsGenerated || m_roadsLoadedFromZDO || m_networkDirty || m_networkReceived;

    /// <summary>Whether the network in memory is one the server sent this client (RoadNetworkSync).</summary>
    internal static bool NetworkReceived => m_networkReceived;

    /// <summary>
    /// Whether a world without persisted roads generates its network when the
    /// player spawns. Off (PROCEDURALROADS_GENERATE_ROADS_ON_LOAD=0), the world
    /// stays road-free until road_generate or road_regen_island asks; a
    /// validation loop on one location then pays seconds, not a whole-world
    /// generation. There is no config key for this: it is a switch for
    /// developing the mod, and a config key cannot be taken back once written.
    /// </summary>
    public static bool GenerateOnLoad = true;

    /// <summary>
    /// Between choosing the destinations and building the roads: the preparation steps that location the locations their
    /// landing waits for (<see cref="LocationsFirstLoad"/>), each step a stage with its own progress on the loading panel.
    /// Every generation runs them (<see cref="PrepareSteps"/>): the locations a generation will visit are the locations it
    /// places first. Placing waits for the game's frames, so every generation is the owned one, run over frames
    /// (<see cref="RoadLifecycleManager.Tick"/>); only the offline tools drain a preparation in one call, with a hook of
    /// their own that needs no frames. Settable for tests.
    /// </summary>
    internal static Func<GenerationWork, RoadPreparation, IEnumerable<bool>>? LocationsFirst;

    internal static void ApplyGeneratedNetworkToLoadedZones()
    {
        // Zones generated during the loading screen (around the login position)
        // exist before the network does; give them their roads now.
        int zones = RoadTerrainModifier.ApplyToLoadedZones();
        Log.LogDebug($"Queued road terrain for {zones} zone(s) loaded before generation");
        int bridgeZones = StructurePlacement.SpawnInLoadedZones();
        if (bridgeZones > 0)
            Log.LogDebug($"Spawned bridges into {bridgeZones} zone(s) loaded before generation");
    }

    /// <summary>
    /// Get the start points of all generated roads for visualization.
    /// </summary>
    public static IReadOnlyList<(Vector2 position, string label)> GetRoadStartPoints() => m_roadStartPoints;

    public static void Initialize() => Reset();

    /// <summary>
    /// Called when location generation is complete. Does not trigger road generation.
    /// </summary>
    public static void MarkLocationsReady()
    {
        m_locationsReady = true;
        Log.LogDebug("Locations marked ready for road generation");
    }

    /// <summary>
    /// Called when roads have been loaded from ZDO persistence (existing world).
    /// </summary>
    public static void MarkRoadsLoadedFromZDO()
    {
        m_roadsLoadedFromZDO = true;
        Log.LogDebug("Roads marked as loaded from ZDO persistence");
    }

#if ROADS_TESTS
    /// <summary>
    /// Tests: a whole-world generation run to its end as the game runs it (<see cref="RoadLifecycleManager.Begin"/>, its
    /// frames pumped): with <paramref name="force"/> as road_generate runs it (<see cref="RoadLifecycleManager.BeginForOperator"/>),
    /// without as the load does. Throws as the game's console would say it: busy, or the generation failed.
    /// </summary>
    internal static void GenerateRoads(bool force = false)
    {
        if (RoadLifecycleManager.HasNetwork && !force) return;
        string? refused = force
            ? RoadLifecycleManager.BeginForOperator(null, line => Log.LogInfo(line))
            : RoadLifecycleManager.Begin(new RoadLifecycleManager.GenerationRequest()) ? null : RoadLifecycleManager.Busy;
        if (refused == RoadLifecycleManager.Busy) throw new InvalidOperationException(refused);
        RoadLifecycleManager.RunToEndForTests();
    }

    /// <summary>Tests: road_regen_island at <paramref name="worldPos"/>, run to its end as the game runs it. Returns whether it
    /// was regenerated, and its summary or why not.</summary>
    internal static bool RegenerateIslandAt(Vector3 worldPos, out string summary)
    {
        GenerationWork? done = null;
        var told = new List<string>();
        string? refused = RoadLifecycleManager.BeginForOperator(worldPos, told.Add, work => done = work);
        if (refused == RoadLifecycleManager.Busy) throw new InvalidOperationException(refused);
        if (refused == null) RoadLifecycleManager.RunToEndForTests();
        summary = done?.Summary ?? refused ?? (told.Count > 0 ? told[told.Count - 1] : "Nothing was regenerated");
        return done != null;
    }
#endif

    /// <summary>
    /// A generation's main-thread preparation, as steps the owned generation runs over frames: the destinations chosen,
    /// the locations among them that wait for their placing placed first (<see cref="LocationsFirst"/>) and made ready, then
    /// everything the island workers read. The one way a network is prepared, for the whole world or (with
    /// <paramref name="island"/>) for the one island there, which replaces the network.
    /// </summary>
    internal static RoadPreparation BeginPreparation(bool force, Vector3? island = null)
    {
        var world=WorldGenerator.instance;
        var zones=ZoneSystem.instance;
        var options=NetworkOptions.Snapshot();
        var start=DateTime.Now;
        int percentage=IslandRoadPercentage,maxLocations=MaxLocationsPerIsland;
        return new RoadPreparation(p=>PrepareSteps(force,island,world,zones,options,start,percentage,maxLocations,p));
    }
    private static IEnumerable<bool> PrepareSteps(bool force,Vector3? only,WorldGenerator? capturedWorld,ZoneSystem? capturedZones,
        RoadNetworkOptions options,DateTime startTime,int percentage,int maxLocations,RoadPreparation preparation)
    {
        if (!RoadLifecycleManager.HoldsTheNetwork)
        {
            preparation.Refuse(RoadLifecycleManager.NotTheServer);
            yield break;
        }
        if (NetworkInMemory && !force) yield break;
        // The whole world starts afresh here; one island only once its island is found and has locations to go to
        // (a point in the ocean leaves the network as it is).
        if (only == null && force && NetworkInMemory) ResetData();
        if (capturedWorld == null || capturedZones == null)
        {
            preparation.Refuse(capturedWorld == null ? "WorldGenerator not available, cannot generate roads" : "ZoneSystem not available, cannot generate roads");
            yield break;
        }

        preparation.BeginStage("Preparing road settings"); yield return true;
        Log.LogDebug("Starting road network generation...");

        RegisterConfiguredLocations();
        var world = capturedWorld;
        // The zones the network leaves alone, captured once for the whole world and for one island alike: before a
        // destination is chosen (a protected one is not) and before any is placed (a zone its placing generates is the
        // network's). Put back with the old network if none is published (RoadLifecycleManager.BeginForOperator).
        NetworkCutoff.BeginNewNetwork();
        // Eligibility and destination selection read GenerationOptions before an island replaces the old network.
        // Use the captured options here too; ResetGenerationState installs the same snapshot after that replacement.
        m_generationOptions = options;
        if (only == null) ResetGenerationState(world, options);

        LocationData? locations=null;
        foreach(var step in GatherLocationSteps(capturedZones,preparation,value=>locations=value)) yield return step;
        if(locations==null) { preparation.Refuse("No location data available"); yield break; }

        List<Island>? islands=null;
        foreach(var step in IslandDetector.DetectRoadIslandsSteps(world,options,preparation,value=>islands=value)) yield return step;
        if (only is Vector3 at)
        {
            Island? there = islands!.FirstOrDefault(i => i.ContainsPoint(at));
            if (there == null) { preparation.Refuse($"No island at ({at.x:F0},{at.z:F0})"); yield break; }
            islands = new List<Island> { there };
        }
        preparation.BeginStage("Indexing island membership",islands!.Sum(i=>(long)i.Cells.Count),"cells"); yield return true;
        foreach(var island in islands!)
            foreach(var step in island.PrepareMembership(preparation)) yield return step;

        preparation.BeginStage("Finding island destinations",(long)islands.Count*locations.Value.AllLocations.Count,"location checks"); yield return true;
        var eligible=new Dictionary<Island,List<(string name,Vector3 position,float radius)>>();
        foreach(var island in islands)
        {
            var found=new List<(string name,Vector3 position,float radius)>();
            foreach(var loc in locations.Value.AllLocations)
            {
                if(IsIslandDestination(island,loc,world,options)) found.Add(loc);
                if(preparation.Checkpoint()) yield return true;
            }
            eligible[island]=found;
        }
        List<Island> selectedIslands;
        if (only != null)
        {
            if (eligible[islands[0]].Count == 0) { preparation.Refuse($"Island {islands[0].Id} has no road-eligible locations"); yield break; }
            selectedIslands = islands;
        }
        else
        {
            selectedIslands = RoadNetworkSelection.Islands(islands, i => eligible[i].Count, percentage);
            Log.LogDebug($"Islands: {islands.Count} total, {selectedIslands.Count} selected (target {IslandRoadPercentage}%; boss islands are not automatically included)");
        }

        preparation.BeginStage("Selecting road destinations",selectedIslands.Count,"islands"); yield return true;
        var work = new GenerationWork(startTime, world, oneIsland: only != null);
        foreach (var island in selectedIslands)
        {
            var candidates = eligible[island];
            if(candidates.Count==0) { preparation.Checkpoint(); yield return true; continue; }
            int maxLocs = RoadNetworkSelection.Quota(island.ApproxArea,maxLocations);
            var selected = SelectIslandDestinations(island, candidates, maxLocs);
            Log.LogDebug($"Island {island.Id}: {candidates.Count} candidates -> {selected.Count} selected (max {maxLocs}, area {island.ApproxArea/1_000_000:F1}km²)");
            // The spawn starts the island's roads only where the network may go (NetworkCutoff.Avoids); else its edge does.
            Vector3 spawn = locations.Value.SpawnPoint;
            work.Islands.Add(new IslandWork(island, selected,
                island.ContainsPoint(spawn) && !NetworkCutoff.Avoids(spawn) ? spawn : (Vector3?)null,
                locations.Value.SpawnRadius, candidates));
            preparation.Checkpoint(); yield return true;
        }
        if (only != null)
        {
            // One island replaces the whole network, from here as a whole-world generation does from its start.
            bool locationsWereReady = m_locationsReady;
            ResetData();
            m_locationsReady = locationsWereReady;
            ResetGenerationState(world, options);
        }
        // The locations whose landings wait for their placing are placed now, before their roads; then their ground is
        // rebuilt and their landings found, before the islands share them. Both before "Reading saved location
        // placements": it indexes the placements, these among them.
        if (LocationsFirst != null)
            foreach (var step in RoadPreparation.Own(LocationsFirst(work, preparation))) yield return step;
        foreach (var step in PlacedLocations.PrepareSteps(NaturalGround(world), preparation, world)) yield return step;
        if (RoadsLog && PlacedLocations.ReadyCount > 0) Log.LogInfo($"  {PlacedLocations.ReadyCount} location(s) placed before the roads, their roads to go onto their landings");
        // Which of the destinations get no road, their landing not known (PlacedLocations.NotPlaced): once, here on the
        // main thread, for the island workers to read.
        // With replacements on, the same verdict for every location a replacement may be drawn from (PlacedLocations.NoRoadAsReplacement).
        PlacedLocations.FindNotPlaced(work.AllSelected.Select(d => new Vector2(d.position.x, d.position.z)), world,
            ReplaceUnreached ? work.Islands.SelectMany(i => i.Candidates).Select(d => new Vector2(d.position.x, d.position.z)) : null);
        RoadParallel.IslandsRunning = Math.Min(work.Islands.Count, work.Workers);
        foreach(var step in PrepareAndSealSteps(preparation)) yield return step;
        CatalogueDoors(work);
        preparation.Result=work;
    }

    internal sealed class IslandWork
    {
        internal readonly Island Island;
        internal readonly List<(string name, Vector3 position, float radius)> Locations;
        internal readonly Vector3? Spawn;
        internal readonly float Radius;
        /// <summary>The island's eligible destinations the selection drew from (<see cref="IsIslandDestination"/>): the
        /// replacements' pool (<see cref="ReplaceUnreached"/>).</summary>
        internal readonly List<(string name, Vector3 position, float radius)> Candidates;
        internal IslandWork(Island island, List<(string name, Vector3 position, float radius)> locations, Vector3? spawn, float radius,
            List<(string name, Vector3 position, float radius)>? candidates = null)
        { Island = island; Locations = locations; Spawn = spawn; Radius = radius; Candidates = candidates ?? new(); }
    }

    internal sealed class GenerationWork
    {
        internal readonly DateTime StartTime;
        /// <summary>One island's regeneration (road_regen_island): it says how it went in <see cref="Summary"/>.</summary>
        internal readonly bool OneIsland;
        /// <summary>One island's regeneration, as it went: set once the network is complete.</summary>
        internal string? Summary;
        /// <summary>The world the islands are built in.</summary>
        internal readonly WorldGenerator World;
        internal GenerationWork(DateTime startTime, WorldGenerator world, bool oneIsland = false) { StartTime = startTime; World = world; OneIsland = oneIsland; }
        /// <summary>This generation's network replaced the one in memory (set as <see cref="CompleteGeneration"/> publishes it):
        /// a failure after this keeps it; one before discards it.</summary>
        internal bool Published;
        internal readonly List<IslandWork> Islands = new();
        internal readonly int Workers = RoadParallel.IslandWorkers;
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Finished = new();
        /// <summary>Every island's chosen destinations, in the order the islands were chosen.</summary>
        internal IEnumerable<(string name, Vector3 position, float radius)> AllSelected => Islands.SelectMany(i => i.Locations);
        internal System.Threading.CancellationToken Cancellation;
        internal RoadIslandActivity? Activity;
        internal void Compute()
        {
            // One island at a time to whichever worker is free, in the islands' chosen order (RoadNetworkSelection.Islands:
            // most eligible destinations first). The default partitioning hands each worker a run of islands up front, so
            // a long island could wait behind another on its worker while others stood idle.
            // The last island's stair ladders laid ahead on the workers no other island takes any more (RoadStairRuns.LayoutsAhead).
            RoadParallel.Islands(Islands.Count);
            Log.LogInfo(GenerationMemory.Describe());
            // Laid ahead only where the generation spends memory for speed: each of its threads keeps its own layouts' ground
            // and search sets (GenerationMemory.LaysAhead). Without it each layout is laid by its own island, the same layout.
            if (GenerationMemory.LaysAhead) RoadStairRuns.LayoutsAhead.Start();
            // While the workers run, the game's river weight is read without its shared cache (RiverWeightReads).
            WorldGenerator world = World;
            RiverWeightReads.Begin(world);
            try
            {
            // Started in StartOrder; each island keeps its number in the chosen order, which is its tag (CrossIslandReads).
            int[] order = StartOrder(world);
            // Each island's share of the world's pitch budget, by its chosen destinations (RoadPitches.Shares, BeginIsland),
            // fixed before any island starts so it does not depend on which finishes first.
            float[] pitchShares = RoadPitches.Shares(Islands.Select(i => i.Locations.Count).ToList());
            System.Threading.Tasks.Parallel.ForEach(
                System.Collections.Concurrent.Partitioner.Create(order, System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering),
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = Cancellation },
                (number, state) =>
                {
                    IslandWork island = Islands[number];
                    Cancellation.ThrowIfCancellationRequested();
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var token=Activity?.Claim((int)number+1,island.Locations.Count);
                    IslandActivity=token;
                    CrossIslandReads.Current = (int)number + 1;
                    RoadPitches.BeginIsland((int)number + 1, pitchShares[number]);
                    if(Finder!=null) Finder.Activity=token;
                    try
                    {
                    m_islandRoads = 0;
                    if (SearchStats.Enabled) SearchStats.ResetThread();
                    GenerateIslandRoads(island.Island, island.Locations, island.Spawn, island.Radius,
                        ReplaceUnreached ? (kept, tried, count) => SelectReplacements(island.Island, island.Candidates, kept, tried, count) : null);
                    Cancellation.ThrowIfCancellationRequested();
                    CountDestinationsReached(island.Island, island.Locations);
                    Finished.Enqueue(SearchStats.Enabled
                        ? $"island {number + 1} (id {island.Island.Id}): {m_islandRoads} road(s) from {island.Locations.Count} destination(s) " +
                          $"in {clock.Elapsed.TotalSeconds:F0}s ({SearchStats.BriefThread()}); so far {SearchStats.Brief()}"
                        : $"island {number + 1}: {m_islandRoads} road(s) from {island.Locations.Count} destination(s) in {clock.Elapsed.TotalSeconds:F0}s");
                    }
                    finally
                    {
                        if(Finder!=null) Finder.Activity=null; IslandActivity=null; CrossIslandReads.Current = 0; token?.Finish();
                        RoadStairRuns.LetGoOfGround();
                        RoadStairRuns.LayoutsAhead.Drop();
                        RoadParallel.IslandDone();
                        RoadStairRuns.LayoutsAhead.Wake();
                    }
                });
            }
            finally { RoadStairRuns.LayoutsAhead.Stop(); RiverWeightReads.End(world); }
        }

        /// <summary>The islands' numbers (their locations in <see cref="Islands"/>, the chosen order) in the order workers start
        /// them: the islands with Mistlands first, most Mistlands first, then the rest as chosen (ties as chosen). The Mistlands
        /// are where an island's roads cost most to price (its valley viaducts), so its longest islands start first and none is
        /// left to run alone at the end. Only when several workers build islands at once: one worker builds them in the chosen
        /// order, as it always has. Islands are separate networks (<see cref="CrossIslandReads"/>), each tagged with its own
        /// number whichever starts first, so the order they start in changes no road.</summary>
        internal int[] StartOrder(WorldGenerator world)
        {
            var order = new int[Islands.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            if (Workers <= 1 || Islands.Count <= 1) return order;
            var mistlands = new int[Islands.Count];
            System.Threading.Tasks.Parallel.For(0, Islands.Count,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = Cancellation },
                i => mistlands[i] = MistlandsCells(Islands[i].Island, world));
            // A stable sort: equal islands stay in the chosen order.
            return order.OrderByDescending(i => mistlands[i]).ThenBy(i => i).ToArray();
        }
    }

    /// <summary>How much of <paramref name="island"/> is Mistlands: its cells (each the same area) whose biome is the Mistlands,
    /// by the one owner of "this is Mistlands" (<see cref="StairKit.MistlandsKitBelongs"/>), read where the island's cell
    /// stands.</summary>
    internal static int MistlandsCells(Island island, WorldGenerator world)
    {
        int count = 0;
        foreach (Vector2Int cell in island.Cells)
        {
            Vector2 at = island.CellToWorld(cell);
            if (RoadRules.For(RoadBiome.At(world, at)).StartsIslandFirst) count++;
        }
        return count;
    }

    internal static void CompleteGeneration(GenerationWork work)
    {
        LocationLevelling.Unseal();
        ReportMissesAfterSealing();
        DropBuriedPieces();
        LogTerrainMemoCounters();

        TimeSpan elapsed = DateTime.Now - work.StartTime;
        if (work.OneIsland)
        {
            Island island = work.Islands[0].Island;
            work.Summary =
                $"Island {island.Id} ({island.ApproxArea / 1_000_000f:F1}km²): " +
                $"{work.Islands[0].Locations.Count} locations, {m_roadsGeneratedCount} roads, " +
                $"{RoadSpatialGrid.Statistics.LengthText()} in {elapsed.TotalSeconds:F1}s; " +
                $"{m_destinationsConnected}/{m_destinationsSelected} destinations connected ({m_reachedOffLevel} off level), " +
                $"{m_roadsWithNoRoute} attempt(s) found no route" +
                (m_refusalReasons.IsEmpty ? "" : "; refused when built: " + string.Join("; ",
                    System.Linq.Enumerable.Select(System.Linq.Enumerable.OrderByDescending(m_refusalReasons, kv => kv.Value), kv => $"{kv.Value} {kv.Key}")));
            // Also to the log: a console answer is lost when the command outlives
            // the caller's timeout, and a check needs a line to wait for.
            Log.LogInfo($"Island regenerated: {work.Summary}");
        }
        Log.LogInfo($"Roads done: {m_roadsGeneratedCount} road(s), " +
                    $"{RoadSpatialGrid.Statistics.LengthText("F1",1000f," km")}, connecting " +
                    $"{m_destinationsConnected} of {m_destinationsSelected} destination(s), " +
                    $"in {elapsed.TotalSeconds:F0}s.");
        int unconnected = m_destinationsSelected - m_destinationsConnected;
        Log.LogInfo($"  {m_carriedToEdge} road end(s) carried on from a location's ring to its edge, level with it (a location with no flat nor landing).");
        Log.LogInfo($"  {m_reachedOffLevel} destination(s) reached off level: no road could arrive level with them, so on the fallback a road ending off their level reaches them (outside the Mountains).");
        Log.LogInfo($"  {m_behindRidge} destination(s) left unserved behind a ridge: on the fallback the network in their reach ends behind ground over {RidgeRise:F0} m above it and the location.");
        if (m_destinationsNotPlaced > 0)
            Log.LogInfo($"  {m_destinationsNotPlaced} destination(s) given no road: their landing turns with them and they were not placed before their roads.");
        if (ReplaceUnreached)
            Log.LogInfo($"{m_replacementsTried} replacement destination(s) tried for the unreached, {m_replacementsReached} reached.");
        else
            Log.LogInfo("No replacement destinations (Roads.ReplaceUnreachableLocations off): each island kept the destinations first chosen for it.");
        if (unconnected > 0)
            // Attempts, not destinations: a location is offered several candidate
            // connections and every one of them can fail before it is given up
            // on, so these are larger than the number left unconnected.
            Log.LogInfo($"  {unconnected} destination(s) left unconnected after every candidate was tried: " +
                        $"{m_roadsWithNoRoute} attempt(s) found no route, " +
                        $"{m_roadsRefusedForGrade} could not be built (grade, turn room, footprint or water).");
        if (SearchStats.Enabled)
        {
            List<RoadSpatialGrid.Junction> junctions;
            lock (RoadSpatialGrid.RecordGate) junctions = new List<RoadSpatialGrid.Junction>(RoadSpatialGrid.Junctions);
            if (junctions.Count > 0)
                // Where a road took another's height: the largest fills are where to look for
                // ridges (at 896,4416 a junction created a large earthwork ridge that looked bad).
                // Each stretch's pull and fill are its own extremes, each at its own point.
                Log.LogInfo($"  Junctions (step {RoadPathfinder.JunctionStep:0.##} m): {junctions.Count} stretch(es) held to a road already laid; " +
                    FormattableString.Invariant($"pulled over 2 m {junctions.Count(j => Mathf.Abs(j.Pull) > 2f)}, over 4 m {junctions.Count(j => Mathf.Abs(j.Pull) > 4f)}; ") +
                    FormattableString.Invariant($"fill over 2 m {junctions.Count(j => j.Fill > 2f)}, over 4 m {junctions.Count(j => j.Fill > 4f)}; ") +
                    "pulled over 2 m by location: " + string.Join(", ", junctions.Where(j => Mathf.Abs(j.Pull) > 2f).GroupBy(j => j.Where).Select(g => $"{g.Key} {g.Count()}")) + "; " +
                    "most fill (and the stretch's greatest pull from the road's own profile): " +
                    string.Join(", ", junctions.Where(j => !float.IsNaN(j.Fill)).OrderByDescending(j => j.Fill).Take(12)
                        .Select(j => FormattableString.Invariant($"({j.FillAt.x:F0}, {j.FillAt.y:F0}) {j.Fill:+0.0;-0.0} m, pulled {j.Pull:+0.0;-0.0} at ({j.PullAt.x:F0}, {j.PullAt.y:F0})"))));
        }
        if (!m_refusalReasons.IsEmpty)
            Log.LogInfo("  Roads refused when built, by rule: " + string.Join("; ",
                System.Linq.Enumerable.Select(System.Linq.Enumerable.OrderByDescending(m_refusalReasons, kv => kv.Value), kv => $"{kv.Value} {kv.Key}")));
        if (EarthworkCapped)
        {
            Log.LogInfo($"  Earthwork cap {EarthworkCap:F0} m: {m_earthworkReroutes} road(s) re-routed round deep cuts or fills, {m_earthworkUnavoided} refused as still past it, {m_earthworkFinished} as finished (landed, levelled for a staircase or carried on) past it; {m_pullReroutes} re-routed round a stretch held over {HoldPullLimit:F0} m off its profile to a road already laid; {m_ridgeReroutes} re-routed off a trench or ridge, {m_ridgeStairs} onto stairs");
            Log.LogInfo($"  Earthwork measured across the road's width: {System.Threading.Interlocked.Read(ref s_earthworkCalls)} call(s), {System.Threading.Interlocked.Read(ref s_earthworkPoints)} point(s), {System.Threading.Interlocked.Read(ref s_earthworkFits)} writer's vertex fit(s), {System.Threading.Interlocked.Read(ref s_earthworkReads)} ground read(s), {System.Threading.Interlocked.Read(ref s_earthworkReads) - System.Threading.Interlocked.Read(ref s_earthworkKnown)} of them from the ground (the rest the 1 m lattice's, RoadStairRuns.GroundAtVertex)");
        }
        if (RoadCrossingDetector.ToWater)
            Log.LogInfo($"  Bridge ends walked in off land above the deck: {m_offLandEnds}; deck line through the ground ({RoadCrossingDetector.DeckLineStep:0} m samples, candidates included): {RoadCrossingDetector.DeckLineWalkedIn} bridge(s) ended at the first dry ground, {RoadCrossingDetector.DeckLineRefused} refused, {RoadCrossingDetector.DeckLineSpanDropped} ford span(s) laid as another style");
        if (RoadCrossingDetector.Shallows)
        {
            int sr = 0, sw = 0, ss = 0;
            lock (m_recordGate)
                foreach (var c in m_roadCrossings)
                    if (c.Shallow) { if (c.Style == FordStyle.Raise) sr++; else if (c.Style == FordStyle.Wade) sw++; else ss++; }
            Log.LogInfo($"  Shallow-water fords (pools the road walks): {sr} raised, {sw} waded, {ss} spanned");
        }
        if (CorridorSnap > RoadSnap)
            Log.LogInfo($"  Corridor snap (a road alongside another merges onto it, within {CorridorSnap:F0} m): {m_corridorSnapped} waypoint(s) moved, {m_chainsShared} chain(s) of crossings shared, candidates included; {m_reEndedOnApproach} road(s) ended on a location's approach's road instead of beside it, {m_reEndRefused} refused so");
        if (!RoadSpatialGrid.BendFallbacks.IsEmpty)
            Log.LogInfo("  Bend rounding refused with full arcs (plans, trials included): " + string.Join(", ",
                System.Linq.Enumerable.Select(System.Linq.Enumerable.OrderBy(RoadSpatialGrid.BendFallbacks, kv => kv.Key.Item1.ToString() + kv.Key.Item2),
                    kv => $"{(kv.Key.Item1 == RoadSpatialGrid.BendStage.Least ? "least arcs" : "corners kept")} after '{kv.Key.Item2}' {kv.Value}")));
        if (RoadJunctionFillet.Radius > 0f)
            Log.LogInfo($"  Junction fillets (a road's end carried onto the road it joins, its inside corners filled to a {RoadJunctionFillet.Radius:F0} m curve): {m_junctionFills} junction(s)");
        LogStairEnds();
        Log.LogInfo($"  Staircase start pads (the road levelled where a staircase starts at its end, RoadEndRing.PadUnder): {RoadStairRuns.PadsAtFeet} at feet, {RoadStairRuns.PadsAtTops} at tops ({RoadStairRuns.PadsPartial} with points over falling ground left out); not laid: "
            + (RoadStairRuns.PadsRefused.IsEmpty ? "none" : string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.OrderBy(RoadStairRuns.PadsRefused, kv => kv.Key, StringComparer.Ordinal), kv => $"{kv.Key} {kv.Value}"))));
        Log.LogInfo($"  Staircase end floors carried over by the road to the landing row next to the stair (RoadStairRuns.CarryRoadOntoStairs): {RoadStairRuns.FeetCarriedOnto} feet, {RoadStairRuns.TopsCarriedOnto} tops; ends (every lay tried) with 1/2/3/4+ floor rows {string.Join("/", RoadStairRuns.EndsByFloorRows)}, tried {RoadStairRuns.EndsTried}, carried {RoadStairRuns.EndsCarried} (one landing row kept); {RoadStairRuns.RoadOntoCalls} road(s) tried onto an end, {RoadStairRuns.RoadOntoUnplanned} refused before planning, {RoadStairRuns.CarriesReused} end(s) decided again from this generation's cache; kept (every lay tried): "
            + (RoadStairRuns.EndFloorsKept.IsEmpty ? "none" : string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.OrderBy(RoadStairRuns.EndFloorsKept, kv => kv.Key, StringComparer.Ordinal), kv => $"{kv.Key} {kv.Value}"))));
        Log.LogInfo(FormattableString.Invariant($"  One road (RoadRide): {RoadRide.RiddenMetres:F0} m of planned road riding the network left to the road laid there, {RoadRide.CrossingsRidden} crossing(s) left to the road that built them, {RoadRide.CaughtAtCommit} ridden stretch(es) caught at the commit"));
        LogDoors();
        Log.LogInfo($"  Cross-island reads (an island worker's reads that passed over a road point, ride line, staircase, crossing or viaduct another island committed; islands build as separate networks): {CrossIslandReads.Describe()}");
        Log.LogInfo($"  Structure ends met by earthwork (their landing filled over ground under the road's floor, StructureEnds.MeetEnds): {m_endEarthworks}; left from their landing pad off their line: {m_endPads}; on a landing shared with the next structure: {m_endShared}; where the road's last point stands, off their line: {m_endStands}; steps laid at them: {m_endSteps}; ends with none, one, more: {m_endFlat}/{m_endOneStep}/{m_endMoreSteps}; structures with both ends stepped, both flush, one of each (StructureEnds.Pair): {m_pairsStepped}/{m_pairsFlush}/{m_pairsOneSided}");
        Log.LogInfo($"  Staircase feet kept on the shorter flight, their road's landing raised to them (StairLayout.StairsDownTo): {RoadStairRuns.FootLandingsRaised}; roads laid again with the stair added (RoadNetworkGenerator.WithStairsAdded): planning {m_stairsAddedPlanning} ({m_stairsAddedPlanningSaved} then planned), commit {m_stairsAddedCommit} ({m_stairsAddedCommitSaved} then built)");
        Log.LogInfo($"  Staircase feet carried to a crossing's end (RoadStairRuns.EndUnderFoot): {RoadStairRuns.FeetLoweredToEnds}; of them met on a stair sunk into the road under the foot (StairLayout.FootToRoad): {RoadStairRuns.FeetSunkAtEnds}, sunk stairs laid for it: {RoadStairRuns.SunkStairsLaid}");
        Log.LogInfo($"  Staircase tops met at a crest (RoadStairRuns.JoinCrest, StairLayout.JoinTops, each try): {RoadStairRuns.TopJoinsSnapped} joined by a snapped run, {RoadStairRuns.TopJoinsClipped} by a clip ({RoadStairRuns.TopJoinsAsTheyStand} as they stand, {RoadStairRuns.TopJoinsClipSunk} on a stair sunk into the top floor under it), {RoadStairRuns.TopJoinsRefused} refused; ridge meets laid back to back (searched down from the first's top): {RoadStairRuns.RidgesSnapped}");
        if (RoadsLog)
            Log.LogInfo($"  Crossing ends: searched again round a crossing with an end no way of meeting reaches {m_crossingsUnmetSearchedRound} ({m_gridFallbacks} of them on a knight's heading, the site searched again on the grid's headings), refused with one past those searches {m_bridgeEndsUnmet}; viaducts: {m_viaductsLaid} Mistlands viaduct(s) laid over dry valleys (DvergrViaduct), searched again without one the built road did not allow {m_viaductsRefusedAtEnds}; bridges and spans the built road does not allow (road under the deck): searched again without one {m_builtSearchedRound}; crossings the built road does not allow, refused with one past those searches {m_builtRefused}; bridges and spans in a location their road does not go to: searched again without one {m_inLocationSearchedRound}, refused with one past those searches {m_inLocationRefused}; bridges and spans no kit stands in: searched again without one {m_noKitSearchedRound}, refused with one past those searches {m_noKitRefused}; the ground under another road's bridge or staircase: first planned changing it {m_keptClearSearched}, refused for it {m_keptClearRefused}, refused for writing ground not known under it {m_unknownGroundRefused}");
        if (RoadsLog)
        {
            // The kits' shares outside the Mistlands, counted over the sites: each the kit it has, or the one its first build
            // would choose among the kits installed now (BridgeStructures.KitFor).
            int rail = 0, braced = 0, stone = 0;
            List<RoadCrossing> sites;
            lock (m_recordGate) sites = BridgeLayout.DistinctSites(m_roadCrossings);
            foreach (RoadCrossing c in sites)
            {
                BridgeStructures.Kit? kit = BridgeStructures.KitFor(c, GenerationWorld);
                if (kit == BridgeStructures.Kit.WoodRail) rail++;
                else if (kit == BridgeStructures.Kit.WoodBraced) braced++;
                else if (kit == BridgeStructures.Kit.Stone) stone++;
            }
            Log.LogInfo($"  Bridge kits outside the Mistlands (as first built with the kits installed now): {rail} WoodRail, {braced} WoodBraced, {stone} Stone");
        }
        if (RoadWiggle.Enabled)
        {
            Log.LogInfo($"  Sway before crossings (searched roads through standing water, candidates included): {m_shapedFirst} shaped first, {m_shapedFirstFellBack} where the plain line was kept");
            Log.LogInfo($"  Sway (planned pieces, candidates included): kept {RoadSpatialGrid.SwayKept} ({RoadSpatialGrid.SwayKeptHeld} with it held round the stretches at fault), dropped as refused {RoadSpatialGrid.SwayRefused}, dropped for more earth {RoadSpatialGrid.SwayMoreEarth}; full sway to {RoadWiggle.Flat:P0} ground grade, {RoadWiggle.SlopeFloor:P0} of it at the {RoadGrade.Configured:P0} cap, meander fades out at {RoadPathfinder.MeanderFlat:P0}");
            Log.LogInfo($"  Sway on slopes (kept plans, over {RoadWiggle.Flat:P0} grade and more than 1 m off the plain line): {RoadSpatialGrid.SlopeSwayPlans} plan(s), metres by grade band 12-20%: {RoadSpatialGrid.SlopeSwayMillimetres[0] / 1000}, 20-28%: {RoadSpatialGrid.SlopeSwayMillimetres[1] / 1000}, over 28%: {RoadSpatialGrid.SlopeSwayMillimetres[2] / 1000}");
        }
        if (RoadPitches.Enabled)
        {
            Log.LogInfo($"  Climbs too steep for their rests under the grade cap (refused, trials too): {RoadPitches.ClimbsTooSteepForRests}");
            var (pitchCount, pitchMetres, pitchLongest) = RoadPitches.Stored();
            Log.LogInfo(FormattableString.Invariant(
                $"  Last-resort pitches: {pitchCount} stored, {pitchMetres:F1} m (budget {RoadPitches.PitchBudget:F0} m), longest {pitchLongest:F1} m; climbs planned with one (trials too) {RoadPitches.PitchClimbs}; destinations tried again {m_lastResortDestinations}, reached {m_lastResortReached}; viaduct ends tried again {m_viaductEndsTriedAgain}, planned {m_viaductEndsPlanned}"));
        }
        if (RoadPitches.JunctionLanding > 0f)
            Log.LogInfo($"  Location ends on a level landing (held at the location's ground, RoadPitches.ApplyJunctionLanding): {RoadSpatialGrid.LocationLandingsStored}");
        if (SearchStats.Enabled)
        {
            Log.LogInfo($"  Crossing reads: {CrossingReads}");
            foreach (string line in SearchStats.Describe())
                Log.LogInfo("  " + line);
        }
        if (RoadsLog) LogStairs();
        Log.LogInfo("  Staircases refused across their road: " + RoadStairRuns.AcrossCounts());
        LogSqueeze();
        LogGenerationStats(m_roadsGeneratedCount, elapsed);

        StairStructures.SortCanonical();
        SortRecordsCanonical();
        RoadSpatialGrid.FinalizeRoadNetwork();
        
        NetworkCutoff.UseNewNetwork();
        RoadNetworkPersistence.NetworkGenerated();
        m_roadsGenerated = true;
        m_networkDirty = true;
        work.Published = true;
        ReleasePathfinders();
        
        RoadNetworkPersistence.EnsureMetadataInstance();
    }

    /// <summary>Drop the generation's pathfinders and the terrain grid they shared
    /// (<see cref="SharedTerrain"/>): kept, a world's grid held tens of MB for the
    /// rest of the session, read by nothing until the next generation.</summary>
    private static void ReleasePathfinders()
    {
        m_generationOptions = null;
        m_generationWorld = null;
        m_pathfinder = null;
        m_threadPathfinders?.Dispose();
        m_threadPathfinders = null;
        SharedTerrain.Clear();
    }

    /// <summary>A worker's reusable generation scratch. Registered only when allocated, cleared after every worker drains.</summary>
    internal abstract class GenerationScratch
    {
        private static readonly List<GenerationScratch> s_kept = new();
        internal bool Released { get; private set; }

        protected GenerationScratch()
        {
            lock (s_kept) s_kept.Add(this);
        }

        internal abstract void Clear();

        internal static void ReleaseAll()
        {
            lock (s_kept)
            {
                foreach (GenerationScratch scratch in s_kept)
                {
                    scratch.Clear();
                    scratch.Released = true;
                }
                s_kept.Clear();
            }
        }
    }

    internal static void ReleaseGenerationResources()
    {
        LocationLevelling.Unseal();
        PlacedLocations.ForgetNotPlaced();
        ReleasePathfinders();
        GenerationScratch.ReleaseAll();
        m_generationWorld = null;
        m_generationOptions = null;
    }

    private static RoadNetworkOptions? m_generationOptions;
    private static RoadNetworkOptions GenerationOptions=>m_generationOptions ?? NetworkOptions;
    private static WorldGenerator? m_generationWorld;
    // As WorldGenerator.instance was read before: callers that can run without a world check it.
    internal static WorldGenerator GenerationWorld => m_generationWorld ?? WorldGenerator.instance!;

    /// <summary>
    /// Merge the config-defined custom locations into the registered set.
    /// Every generation entry point calls this first, so a location named in
    /// the config counts as road-eligible whichever entry point runs first.
    /// </summary>
    private static void RegisterConfiguredLocations()
    {
        ConfiguredLocationNames.Clear();
        foreach (string name in ProceduralRoadsPlugin.GetConfigLocationNames())
            if (!string.IsNullOrWhiteSpace(name)) ConfiguredLocationNames.Add(name.Trim());
    }

    #region Core Road Generation Primitive

    /// <summary>How many roads were dropped because no profile inside the
    /// grade cap joins their two ends. Counted so the cost of the cap is a
    /// number in the generation summary rather than a matter of opinion.</summary>
    private static int m_roadsRefusedForGrade;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> m_refusalReasons = new();
    /// <summary>Destinations chosen, and destinations that ended up with a
    /// road. These are per DESTINATION; the refusal counters below are per
    /// ATTEMPT, and a destination is offered several candidates before it is
    /// given up on, so the two do not add up to each other.</summary>
    private static int m_destinationsSelected;
    private static int m_destinationsConnected;
    /// <summary>Destinations given no road as they were not placed before their roads (<see cref="PlacedLocations.NotPlaced"/>):
    /// counted among those chosen and not reached.</summary>
    private static int m_destinationsNotPlaced;

    /// <summary>How many roads found no route at all. With a grade cap on,
    /// most of these are a destination the cap put out of reach rather than
    /// one across water, so the two are counted apart.</summary>
    private static int m_roadsWithNoRoute;

    /// <summary>PROCEDURALROADS_ROADS_LOG: say where each road was refused for crossing or running beside
    /// another, and list every destination left unconnected with how its last road attempt failed.
    /// Off by default: off, nothing is noted or logged.</summary>
    internal static readonly bool RoadsLog = DebugSwitches.Flag("ROADS_LOG", false);
    /// <summary>Per location (its identity, LocationKey), with <see cref="RoadsLog"/>: how its last road attempt failed,
    /// and whether a road was built to it.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, float), string> m_lastFailure = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, float), bool> m_builtTo = new();
    /// <summary>A location's key in every per-location store (here, RoadDoors' and PlacedLocations'): its identity, the centre it is
    /// carried by, exactly (as RoadLocationProtection.Footprint.Is compares it; never rounded, so two locations a metre apart keep
    /// their own entries). The one owner of a location's identity as a key (docs/OWNERS.md).</summary>
    internal static (float, float) LocationKey(Vector2 c) => (c.x, c.y);
    private static void NoteFailure(Vector2 a, Vector2 b, string why, Vector2? at = null)
    {
        t_capture?.Refused(why, at);
        if (!RoadsLog) return;
        m_lastFailure[LocationKey(a)] = why;
        m_lastFailure[LocationKey(b)] = why;
    }
    private static void NoteBuilt(Vector2 a, Vector2 b)
    {
        if (!RoadsLog) return;
        m_builtTo[LocationKey(a)] = true;
        m_builtTo[LocationKey(b)] = true;
    }

    /// <summary>Per location (its identity, LocationKey), with <see cref="RoadsLog"/>: the staircase that failed where a road to it gave
    /// up its stairs.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, float), string> m_stairsFailed = new();
    /// <summary>With <see cref="RoadsLog"/>: why roads were searched as a plain road, and how often.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> m_stairFallbacks = new();
    /// <summary>With <see cref="RoadsLog"/>: road ends at a location, by what became of an approach staircase there.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> m_locationApproaches = new();
    /// <summary>With <see cref="RoadsLog"/>: the generation's staircase tallies for the summary.</summary>
    private static int m_stairRuns, m_stairRoadsUnlaid, m_stretchRetries, m_stretchRetriesKept, m_tightTurnStairs, m_tightTurnStairsBuilt, m_notLevelEnds;
    /// <summary>With <see cref="RoadsLog"/>: road ends kept close to their location, within <see cref="ReachRise"/> of its level
    /// but more than <see cref="StairLayout.StairFitsFrom"/> off (<see cref="EndNotLevel"/>), or short of a placed location's landing
    /// (<see cref="PlanEntry"/>), with no staircase onto it.</summary>
    private static int m_closeEnds;
    /// <summary>Staircases laid by this thread, with <see cref="RoadsLog"/>: a retry reads it to know whether it kept the road's stairs.</summary>
    [System.ThreadStatic] private static int t_stairRunsLaidHere;

    private static void ClearStairNotes()
    {
        m_stairsFailed.Clear();
        m_stairFallbacks.Clear();
        m_locationApproaches.Clear();
        m_stairRuns = m_stairRoadsUnlaid = m_stretchRetries = m_stretchRetriesKept = m_tightTurnStairs = m_tightTurnStairsBuilt = 0;
        m_stairsAddedPlanning = m_stairsAddedPlanningSaved = m_stairsAddedCommit = m_stairsAddedCommitSaved = 0;
        m_notLevelEnds = m_closeEnds = 0;
    }

    /// <summary>The summary's staircase lines, with <see cref="RoadsLog"/>.</summary>
    internal static void LogStairs()
    {
        static string ByCount(IEnumerable<KeyValuePair<string, int>> tally) =>
            string.Join("; ", tally.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}"));
        Log.LogInfo($"  Staircases: {m_stairRuns} laid on roads; {m_stretchRetries} search(es) again with a staircase's stretch closed, " +
                    $"{m_stretchRetriesKept} of them built with stairs; " +
                    $"{m_tightTurnStairs} search(es) again with stairs open at switchback turns too close to fit, {m_tightTurnStairsBuilt} of them built with stairs; " +
                    $"{m_notLevelEnds} road(s) refused for an end not level with its location ({ReachRise:F1} m) and no approach staircase onto it; " +
                    $"{m_closeEnds} end(s) kept close to their location, within {ReachRise:F1} m of its level or short of its landing, with no approach staircase; " +
                    $"{RoadStairRuns.FeetStartedLower} started further back down their road; {RoadStairRuns.KitsRelaid} stretch(es) laid again in the kit their staircase stood in; " +
                    $"{RoadStairRuns.RidgesToFeet} start(s) onto a location's landing refused over a ridge or trench to the foot; " +
                    $"{RoadStairRuns.ApproachesNotFacingIn} approach(es) laid not facing into their location ({RoadStairRuns.ApproachFacingReruns} searched again not facing in); " +
                    $"{RoadStairRuns.AimsBuried} aim(s) whose first stair facing in was buried, {RoadStairRuns.LaidBesideTheirAim} approach(es) laid beside such an aim along the lip; " +
                    $"{RoadStairRuns.Crests} top(s) crested onto a lip; {m_humpsLevelled} crest pair(s) planned as road across their hump (built or not); {m_stairRoadsUnlaid} road(s) searched as a plain road after closing stretches to stairs" +
                    (m_stairFallbacks.IsEmpty ? "" : ": " + ByCount(m_stairFallbacks)));
        if (!m_locationApproaches.IsEmpty)
            Log.LogInfo("  Location approach staircases, by road end at a location: " + ByCount(m_locationApproaches));
        if (!RoadStairRuns.Rejections.IsEmpty)
            Log.LogInfo("  Staircase layouts, by what became of them: " + ByCount(RoadStairRuns.Rejections));
        if (!RoadStairRuns.LayFailures.IsEmpty)
            Log.LogInfo("  Roads that gave up their stairs, by the staircase that stopped them: " + ByCount(RoadStairRuns.LayFailures));
    }

    /// <summary>What the terrain memo did over a whole generation: how often a
    /// fact was already held, how often the world had to be asked, and how
    /// often a slot was evicted by a different position. Evictions are the
    /// figure that says the memo is too small for the way it is being asked,
    /// which a hit rate on its own does not.</summary>
    private static void LogTerrainMemoCounters()
    {
        if (m_threadPathfinders != null)
            foreach (var finder in m_threadPathfinders.Values) finder.FoldTerrainMemoCounters();
        long hits = RoadTerrainSamples.TotalHits, misses = RoadTerrainSamples.TotalMisses;
        long asked = hits + misses;
        if (asked == 0) return;
        Log.LogDebug($"Terrain memo: {asked} fact(s) asked, {hits} held ({100.0 * hits / asked:F1}%), " +
                     $"{misses} read from the world ({RoadTerrainSamples.TotalTerrainCalls} generator call(s)), " +
                     $"{RoadTerrainSamples.TotalReplacements} slot(s) evicted.");
        if (!SearchStats.Enabled) return;
        // Info, beside the search times it is part of: in game the generator's share
        // of a search (its river cache takes a write lock on every miss) can only be
        // read here; offline it is a dump lookup and says nothing.
        double seconds = RoadTerrainSamples.TotalTerrainTicks / (double)System.Diagnostics.Stopwatch.Frequency;
        long calls = RoadTerrainSamples.TotalTerrainCalls;
        Log.LogInfo(FormattableString.Invariant($"  World generator behind the search memo: {seconds:F0} s summed over threads, ") +
                    FormattableString.Invariant($"{calls} call(s), {(calls > 0 ? seconds * 1e6 / calls : 0):F1} us each"));
    }

    internal static float? LocationGround(Vector2 endPoint, Vector2 center, float radius)
    {
        if (radius <= 0f || GenerationWorld == null)
            return null;
        IReadOnlyList<LevelOp>? ops = LocationLevelling.OpsAt(center);
        if (ops == null)
            return null;
        float centreGround = LocationLevelling.CentreHeight(center, GenerationWorld);
        float? levelled = PlacedLocations.ExpectedLevelAt(endPoint, center, centreGround, ops, GenerationWorld);
        if (!levelled.HasValue)
            return null;
        return RoadWater.BuiltDry(levelled.Value) ? levelled : null;
    }

    /// <summary>The road's approach to the location at <paramref name="centre"/> improved (RoadLocationApproach.Improve), its
    /// candidates offered to <paramref name="builds"/> (PlanFromPath: the whole road with each, as PlanCandidate builds it).
    /// Unknown location shaping retains the old approach, which goes on to the same planning.</summary>
    internal static List<Vector2> ImproveLocationApproach(List<Vector2> path, Vector2 centre,
        float radius, float width, bool atStart, Func<List<Vector2>, bool>? builds = null)
    {
        var world = GenerationWorld;
        if (world == null || radius <= 0f) return path;
        var ops = LocationLevelling.OpsAt(centre);
        // Copy the terrain facts once: scoring must not reload the template
        // for every vertex. Unknown location shaping retains the old approach.
        if (!LocationLevelling.Levels(ops)) return path;
        float centreHeight = LocationLevelling.CentreHeight(centre, world);
        float? platform = LocationLevelling.ApproachHeight(centreHeight, ops);
        if (platform is float level && !RoadWater.BuiltDry(level)) platform = null;
        Log.LogDebug($"Location approach {centre}: keep-out edge {radius:F1}m, approach {(platform.HasValue ? platform.Value.ToString("F2") : "unknown")}m");
        Func<float, float, float> expected = PlacedLocations.ExpectedGround(centre, centreHeight, ops, world);
        float Ground(Vector2 p) => expected(p.x, p.y);
        float? Target(Vector2 p) => platform.HasValue && Mathf.Abs(Ground(p) - platform.Value) <= StairLayout.StairFitsFrom
            ? Ground(p) : PlacedLocations.ExpectedLevelAt(p, centre, centreHeight, ops, world);
        return RoadLocationApproach.Improve(path, centre, radius, width, world, atStart, Target, Ground, platform, builds);
    }

    internal static float? ApproachGround(Vector2 point, Vector2 centre, float radius)
    {
        float? local = LocationGround(point, centre, radius);
        if (local.HasValue || radius <= 0f || GenerationWorld == null) return local;
        var world = GenerationWorld;
        float centreHeight = LocationLevelling.CentreHeight(centre, world);
        float? platform = LocationLevelling.ApproachHeight(centreHeight, LocationLevelling.OpsAt(centre));
        float natural = BiomeBlendedHeight.GetBlendedHeight(point.x, point.y, world);
        // Never make an arbitrary high embankment merely to hit a platform.
        // The approach search must first find naturally nearby elevation.
        if (platform.HasValue)
            return RoadWater.BuiltDry(platform.Value) && Mathf.Abs(natural - platform.Value) <= StairLayout.StairFitsFrom ? natural : null;
        // A location with no flat (a cabin that only smooths round its centre, not placed first or placed with no landing to go
        // onto: PlacedLocations.Prepare) is arrived at its ground at its edge on the road's side (ArrivalHeight). An end whose
        // ground is level with that, as the reached test has it (ReachRise), is held there and so ends on the level landing
        // every location's end gets (RoadSpatialGrid.LocationEnds), at a road's start as at its end; from there it is carried on
        // level to the edge (CarryOnToEdges). No higher bank or deeper cut than the reached test allows. The edge is the
        // location's own (its footprint's arrival radius): the radius asked here is the road's ring, outside it.
        float arrival = RoadLocationProtection.ArrivalRadiusAt(centre, 0f);
        if (arrival > 0f && !PlacedLocations.TryGetReady(centre, out _) && ArrivalHeight(centre, arrival, point) is float edge
            && RoadWater.BuiltDry(edge) && Mathf.Abs(natural - edge) <= ReachRise)
            return edge;
        return null;
    }

    /// <summary>
    /// Core primitive: Generates a single road between two points.
    /// Handles pathfinding, radius trimming, and adding to the spatial grid.
    /// </summary>
    /// <param name="startCenter">Center of the start location</param>
    /// <param name="startRadius">Exterior radius of start location (road starts at edge)</param>
    /// <param name="endCenter">Center of the end location</param>
    /// <param name="endRadius">Exterior radius of end location (road ends at edge)</param>
    /// <param name="width">Width of the road</param>
    /// <param name="label">Optional label for logging</param>
    /// <returns>True if road was successfully generated</returns>
    public static bool GenerateRoad(
        Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius,
        float width, string? label = null) =>
        GenerateRoad(startCenter, startRadius, endCenter, endRadius, width, label, null);

    /// <summary>How many stretches whose staircase could not be laid a road closes to stairs, one at a time,
    /// searched again after each with stairs open everywhere else, before it is searched once as a plain road, so one
    /// stretch that will not lay does not cost the whole road its other staircases.</summary>
    public const int StairStretchRetries = 3;
    /// <summary>Cells round a failed stretch also closed, so the search does not lay the same
    /// staircase one column over.</summary>
    internal const int StairStretchReachCells = 1;
    /// <summary>Cells round switchback turns too close to fit their curves opened to stairs when the
    /// road is searched again (<see cref="RoadPathfinder.OpenToStairs"/>).</summary>
    internal const int TightTurnStairReachCells = 3;
#if ROADS_TESTS
    /// <summary>The meetings with the network asked on this thread (MeetingRefusal): a test's count.</summary>
    [System.ThreadStatic] internal static int t_meetingsAsked;
    internal static Action? BeforeRoadStorage;
    /// <summary>Tests: a refusal the one list (RoadRefusal) gives before its own, for a road as it would be committed.</summary>
    internal static Func<PlannedRoad, Refusal?>? RefusedFirst;
    internal static Action<RoadPathfinder>? AfterEarthworkPenalty;
    /// <summary>Generation's road as its final check read it, just before it is stored (a test's hook).</summary>
    internal static Action<WrittenRoad>? RoadChecked;
#endif
    [System.ThreadStatic] private static bool t_inRoad;
    // Once shared geometry may have changed, a failure must reach the generation owner.
    [System.ThreadStatic] private static bool t_storingRoad;
    internal const string PlanningExceptionRefusal = "planning exception";

    /// <param name="joinedThrough">Where the end is a junction a join search chose (<see cref="JoinTo"/>), the network cell that
    /// search arrived at it through: every search for this road, and each of its searches again, arrives there as that
    /// search judged (RoadPathfinder.ArrivesLevel). None for every other end.</param>
    internal static bool GenerateRoad(
        Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius,
        float width, string? label, List<Vector2>? networkHints, Vector2? joinedThrough = null)
    {
        // The cells a retry closes to stairs belong to this road alone.
        if (t_inRoad || Finder == null)
            return GenerateRoadCore(startCenter, startRadius, endCenter, endRadius, width, label, networkHints, joinedThrough, default);
        t_inRoad = true;
        t_storingRoad = false;
        int rests = RoadPitches.RestsRefusals;
        bool builtRoad = false;
        if (!RoadPitches.LastResort) RoadPitches.ForgetLastResort();
        t_bandedEnds?.Clear();
        t_refusedAt = null;
        t_reEnd = null;
        try
        {
            bool built = GenerateRoadByDoors(startCenter, startRadius, endCenter, endRadius, width, label, networkHints, joinedThrough);
            // It arrived at a location the way another way into it does (ReEndOnAWayIn): searched once more,
            // ending on that approach's road instead of at the location, as a junction (radius 0, as a location's join ends).
            if (!built && t_reEnd is var (location, onItsRoad))
            {
                t_reEnd = null;
                bool atStart = (startCenter - location).sqrMagnitude <= (endCenter - location).sqrMagnitude;
                Finder?.ClearNoStairs();
                t_doors = null;
                built = atStart
                    ? GenerateRoadByDoors(onItsRoad, 0f, endCenter, endRadius, width, label, networkHints, joinedThrough)
                    : GenerateRoadByDoors(startCenter, startRadius, onItsRoad, 0f, width, label, networkHints, null);
                if (built) System.Threading.Interlocked.Increment(ref m_reEndedOnApproach);
                else System.Threading.Interlocked.Increment(ref m_reEndRefused);
                if (label != null) Log.LogDebug(FormattableString.Invariant(
                    $"Road {label}: ended on the location's approach's road at ({onItsRoad.x:F1}, {onItsRoad.y:F1}) instead: {(built ? "built" : "refused (" + RoadSpatialGrid.LastRefusal + ")")}"));
                return builtRoad = built;
            }
            if (built || t_bandedEnds == null || t_bandedEnds.Count == 0) return builtRoad = built;
            // A road ending at a custom location's MWL stand-off (OffLevelStandOff) refused in its planning (the earthwork
            // cap, the grade cap) on the stretch the grade's stand-off gives up, or where the refusal does not say:
            // searched once more ending at the grade's stand-off. Refused anywhere else, the search again is the same refusal.
            if (!RefusedAtABandedEnd(t_refusedAt, t_bandedEnds))
            {
                if (label != null) Log.LogDebug($"Road {label}: refused ending at the MWL stand-off ({RoadSpatialGrid.LastRefusal}) away from that end; not searched again");
                return false;
            }
            if (label != null) Log.LogDebug($"Road {label}: refused ending at the MWL stand-off ({RoadSpatialGrid.LastRefusal}); searched again at the grade's stand-off");
            t_noStandOffBand = true;
            Finder?.ClearNoStairs();
            t_doors = null;
            return builtRoad = GenerateRoadByDoors(startCenter, startRadius, endCenter, endRadius, width, label, networkHints, joinedThrough);
        }
        catch (Exception error) when (!t_storingRoad && t_capture == null && error is not OperationCanceledException)
        {
            RoadLifecycleManager.ThrowIfCancellationRequested();
            RoadSpatialGrid.LastRefusal = PlanningExceptionRefusal;
            RoadSpatialGrid.RefusedAt = null;
            m_refusalReasons.AddOrUpdate(PlanningExceptionRefusal, 1, (_, n) => n + 1);
            System.Threading.Interlocked.Increment(ref m_roadsRefusedForGrade);
            NoteFailure(startCenter, endCenter, "refused: " + PlanningExceptionRefusal);
            Log.LogWarning($"Road {label ?? "(unlabelled)"} ({startCenter} -> {endCenter}) refused after a planning exception: {error}");
            return false;
        }
        finally
        {
            Finder?.EndRoad(); t_storingRoad = false; t_inRoad = false; t_doors = null; t_noStandOffBand = false; t_bandedEnds?.Clear(); t_refusedAt = null; t_reEnd = null;
            // Refused with a climb too steep for its rests among its plans: its locations may be tried as the last resort
            // (RouteIsland's last round).
            if (!builtRoad && !RoadPitches.LastResort && RoadPitches.RestsRefusals > rests && t_restsRefusedEnds is { } ends)
            {
                if (startRadius > 0f) ends.Add(LocationKey(startCenter));
                if (endRadius > 0f) ends.Add(LocationKey(endCenter));
            }
        }
    }

    /// <summary>The locations of this island worker's roads refused, outside the last resort, with a climb too steep for its
    /// rests among their plans (GenerateRoad): the ones its last round tries (RouteIsland). Null off an island worker.</summary>
    [System.ThreadStatic] internal static HashSet<(float, float)>? t_restsRefusedEnds;

    /// <summary>How far out from a door its road's search ends, the road then running straight in to the door
    /// (<see cref="RoadDoor.LeadIn"/>): a search cell.</summary>
    public const float LeadInLength = RoadPathfinder.CellSize;

    /// <summary>
    /// Where a road to the location at <paramref name="centre"/> (exterior <paramref name="radius"/>) stops, from its
    /// centre: the one owner for the trim (<see cref="TrimPathToRadii"/>) and for whether the network reaches the
    /// location (<see cref="Reached"/>). Enough room for the road's whole terrain blend outside the
    /// ground the location shapes (<see cref="RoadLocationProtection.ArrivalRadiusAt"/>): over ground the location only
    /// paints the road is carried on, stored into the location so it levels and paints there only along
    /// itself (<see cref="RoadSpatialGrid.Commit"/>). A location placed first keeps its whole footprint: its road is
    /// carried on onto its landing by its own owner (<see cref="PlanEntry"/>). A location not on the preload list
    /// (<see cref="RoadLocationProtection.Enterable"/>) keeps its whole footprint too: its road stops at its edge.
    /// The one owner of A location's ring, doors and keep-out edge (docs/OWNERS.md).
    /// </summary>
    internal static float KeepOutEdge(Vector2 centre, float radius, float width) =>
        KeepOutEdge(centre, radius, width, PlacedLocations.TryGetReady(centre, out _));

    /// <summary><see cref="KeepOutEdge(Vector2, float, float)"/> for a location known to be placed first or not (the
    /// preparation that is making it ready asks before it is).</summary>
    internal static float KeepOutEdge(Vector2 centre, float radius, float width, bool placed) =>
        (placed || !RoadLocationProtection.Enterable(centre) ? RoadLocationProtection.RadiusAt(centre, radius) : RoadLocationProtection.ArrivalRadiusAt(centre, radius))
        + RoadLocationProtection.Clearance(width);

    /// <summary>A road's searches again, each against its own budget: closing a stretch to stairs (StairStretchRetries), the
    /// one tight-turn search with stairs opened, and banning a crossing (CrossingRetries). One spent never spends another.</summary>
    internal readonly struct RoadRetries
    {
        public readonly int StairStretches;
        public readonly bool TightTurn;
        public readonly int CrossingBans;
        /// <summary>Sites searched again on the grid's headings after a knight's crossing there had an end not met
        /// (RoadPathfinder.KeepToGridHeadingsAt), each before the failure counts as a crossing ban: at most CrossingRetries.</summary>
        public readonly int GridFallbacks;
        /// <summary>The route the search found before the last crossing was banned: a search that finds it again took no
        /// notice of the ban, and is not searched again.</summary>
        public readonly IReadOnlyList<Vector2>? BeforeBan;
        private RoadRetries(int stairStretches, bool tightTurn, int crossingBans, IReadOnlyList<Vector2>? beforeBan, int gridFallbacks = 0)
        { StairStretches = stairStretches; TightTurn = tightTurn; CrossingBans = crossingBans; BeforeBan = beforeBan; GridFallbacks = gridFallbacks; }
        public RoadRetries NextStairStretch() => new(StairStretches + 1, TightTurn, CrossingBans, BeforeBan, GridFallbacks);
        public RoadRetries WithTightTurn() => new(StairStretches, true, CrossingBans, BeforeBan, GridFallbacks);
        public RoadRetries NextCrossingBan(IReadOnlyList<Vector2> searched) => new(StairStretches, TightTurn, CrossingBans + 1, searched, GridFallbacks);
        public RoadRetries NextGridFallback(IReadOnlyList<Vector2> searched) => new(StairStretches, TightTurn, CrossingBans, searched, GridFallbacks + 1);
        /// <summary>Whether <paramref name="searched"/> is the route found before the last ban.</summary>
        public bool SameAsBeforeBan(IReadOnlyList<Vector2> searched)
        {
            if (BeforeBan == null || BeforeBan.Count != searched.Count) return false;
            for (int i = 0; i < searched.Count; i++)
                if ((BeforeBan[i] - searched[i]).sqrMagnitude > 0.01f) return false;
            return true;
        }
    }

    private static int m_gridFallbacks;

    /// <summary>How the next search of a road goes round the crossings it was refused with (GenerateRoadCore): a crossing on a
    /// knight's heading with an end not met (<paramref name="unmet"/>, while <paramref name="fallbackLeft"/>) keeps its site to
    /// the grid's headings (RoadPathfinder.KeepToGridHeadingsAt) and is not banned, so the site's square crossing stays open;
    /// else every crossing in <paramref name="banned"/> is banned as the search used it (its banks, and every move it lies on).
    /// True where the site was kept to the grid.</summary>
    internal static bool BanForTheNextSearch(RoadPathfinder finder, List<RoadCrossing> banned, RoadCrossing? unmet, bool fallbackLeft)
    {
        if (unmet != null && !unmet.ValleyJump && fallbackLeft && OnAKnightsHeading(unmet))
        {
            finder.KeepToGridHeadingsAt(unmet.Center, unmet.Width * 0.5f + RoadPathfinder.CellSize);
            System.Threading.Interlocked.Increment(ref m_gridFallbacks);
            return true;
        }
        foreach (RoadCrossing c in banned)
        {
            // Mistlands viaducts only: a valley viaduct is banned as the one jump it is (RoadPathfinder.BanViaduct), its
            // neighbours left open; its searched moves too, as any crossing's are.
            if (c.ValleyJump)
            {
                finder.BanViaduct(c.FromBank, c.ToBank);
                if (c.Searched != null) foreach (var (from, to) in c.Searched) finder.BanViaduct(from, to);
                continue;
            }
            finder.BanJump(c.FromBank, c.ToBank);
            if (c.Searched != null) foreach (var (from, to) in c.Searched) finder.BanJump(from, to);
        }
        return false;
    }
    /// <summary>Roads searched again on a site's grid headings after a knight's crossing there had an end not met, this
    /// generation (GenerateRoadCore).</summary>
    public static int GridFallbacks => m_gridFallbacks;

    /// <summary>Whether a crossing the search bought lies on a knight's heading: its jump (the longest move it was searched
    /// on, else its banks' line) is not one of the grid's eight headings, the hammer's even steps (BridgeLayout.HeadingIsPlaceable
    /// at its own tolerance; a knight's jump turned onto the hammer's nearest heading stands on an odd step).</summary>
    internal static bool OnAKnightsHeading(RoadCrossing c)
    {
        // The jump itself: the longest move the crossing lies on (its approaches' moves may run on any heading).
        Vector2 jump = c.ToBank - c.FromBank;
        if (c.Searched != null)
            foreach (var (from, to) in c.Searched)
                if ((to - from).sqrMagnitude > jump.sqrMagnitude || c.Searched.Count == 1) jump = to - from;
        if (jump.sqrMagnitude < 1f) return false;
        // The grid's eight headings are the hammer's even steps (BridgeLayout: a placeable heading, an even multiple of its step):
        // a knight's jump is any other, turned onto an odd step or not placeable at all.
        float yaw = BridgeLayout.YawDegrees(jump.normalized);
        if (!BridgeLayout.HeadingIsPlaceable(yaw)) return true;
        return Mathf.RoundToInt(BridgeLayout.SnapHeadingDegrees(yaw) / BridgeLayout.PlaceableHeadingStep) % 2 != 0;
    }

    private static bool GenerateRoadCore(
        Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius,
        float width, string? label, List<Vector2>? networkHints, Vector2? joinedThrough, RoadRetries retries)
    {
        // The road's approach trials, remembered from its first search to its last (its searches again are calls of this one)
        // and forgotten as it ends (OneRoad).
        using var trials = OneRoad();
        if (Finder == null)
        {
            Log.LogWarning("GenerateRoad called without active pathfinder");
            return false;
        }
        // From here, anything this road asks for that preparation did not
        // provide marks the road rather than only the log.
        LocationLevelling.BeginRoad();
        // Where this road is refused, where its refusal says (RefusedAtABandedEnd): left null by a refusal that does not.
        t_refusedAt = null;
        var refusedAt = new List<Vector2>();
        // A staircase that could not be laid: its stretch is closed to stairs and the road searched again with them open
        // everywhere else, each failing stretch in turn, up to StairStretchRetries; then once as a plain road, else refused.
        // The road keeps every staircase that lays (no collateral).
        bool WithoutThatStaircase(RoadPathfinder finder, List<Vector2> laidOn)
        {
            if (retries.StairStretches >= StairStretchRetries)
            {
                // Last resort: one plain road search before the road is refused.
                // A routing fallback, not a stairs switch: there is no setting for stairs, and the plain road keeps every grade
                // cap. Refused outright after the closings, the roads lost 35 destinations over RoadSeedA-E
                // against 12 with this search.
                if (RoadsLog)
                {
                    System.Threading.Interlocked.Increment(ref m_stairRoadsUnlaid);
                    m_stairFallbacks.AddOrUpdate("a staircase could not be laid", 1, (_, n) => n + 1);
                    m_stairsFailed[LocationKey(startCenter)] = m_stairsFailed[LocationKey(endCenter)] = "a staircase could not be laid";
                    if (label != null) Log.LogDebug($"Road {label}: a staircase could not be laid on {StairStretchRetries} stretch(es) closed in turn; searched once as a plain road");
                }
                bool stairs = finder.Stairs;
                finder.Stairs = false;
                try { return GenerateRoadCore(startCenter, startRadius, endCenter, endRadius, width, label, networkHints, joinedThrough, retries); }
                finally { finder.Stairs = stairs; }
            }
            var (from, to) = RoadStairRuns.LastFailedStretch;
            from = Mathf.Clamp(from, 0, laidOn.Count - 1);
            to = Mathf.Clamp(to, from, laidOn.Count - 1);
            var stretch = laidOn.GetRange(from, to - from + 1);
            // The search's own steps on that stretch too: the path may have been moved onto a road there.
            foreach (var (a, b) in finder.LastStairSteps)
                if (stretch.Exists(p => (p - a).sqrMagnitude < 144f || (p - b).sqrMagnitude < 144f)) { stretch.Add(a); stretch.Add(b); }
            finder.CloseToStairs(stretch, StairStretchReachCells);
            if (!RoadsLog) return GenerateRoadCore(startCenter, startRadius, endCenter, endRadius, width, label, networkHints, joinedThrough, retries.NextStairStretch());
            System.Threading.Interlocked.Increment(ref m_stretchRetries);
            if (label != null)
                Log.LogDebug($"Road {label}: staircase on path {from}..{to} could not be laid ({RoadStairRuns.Said(RoadStairRuns.LastRefusal)}); searched again with {finder.NoStairCells} cell(s) closed to stairs");
            int before = t_stairRunsLaidHere;
            bool built = GenerateRoadCore(startCenter, startRadius, endCenter, endRadius, width, label, networkHints, joinedThrough, retries.NextStairStretch());
            if (built && t_stairRunsLaidHere > before) System.Threading.Interlocked.Increment(ref m_stretchRetriesKept);
            return built;
        }

        // A road to a location is searched to its doors (DoorsOf); one with none, to the ground at its centre.
        PlacedLocation? startLocation = startRadius > 0f && PlacedLocations.TryGetReady(startCenter, out PlacedLocation s0) ? s0 : null;
        PlacedLocation? endLocation = endRadius > 0f && PlacedLocations.TryGetReady(endCenter, out PlacedLocation s1) ? s1 : null;
        Vector2? startAim = null, endAim = null;
        Vector2 pathStart = RoadEndpoint.FindGround(GenerationWorld, startCenter, startRadius);
        Vector2 pathEnd = RoadEndpoint.FindGround(GenerationWorld, endCenter, endRadius);
        IReadOnlyList<RoadDoor> startDoors = DoorsLeft(startCenter, startRadius, width);
        IReadOnlyList<RoadDoor> endDoors = DoorsLeft(endCenter, endRadius, width);
        bool startDoor = startDoors.Count > 0, endDoor = endDoors.Count > 0;
        var searchLocations = new RoadLocationProtection.Targets(SearchedInto(startCenter, startRadius, startLocation != null, startDoor),
            SearchedInto(endCenter, endRadius, endLocation != null, endDoor));
        List<Vector2>? path;
        if (startDoor || endDoor)
        {
            // To every door at once (DoorsOf): the road ends at the cheapest it gets to, its ring point then joined head on
            // from its lead-in. A location placed first is exempt from nothing, as its lead-in is not; a location not placed
            // first lets the search through its own footprint, as its centre's ground does.
            if (startDoor) pathStart = startDoors[0].LeadIn;
            if (endDoor) pathEnd = endDoors[0].LeadIn;
            path = Finder.FindPathBetween(SearchEnds(startDoors, pathStart), SearchEnds(endDoors, pathEnd),
                startDoor && startLocation == null ? startCenter : pathStart, endDoor && endLocation == null ? endCenter : pathEnd, searchLocations, joinedThrough);
            if (RoadsLog)
            {
                System.Threading.Interlocked.Increment(ref m_doorRoads);
                if (path != null && path.Count >= 2) System.Threading.Interlocked.Increment(ref m_doorRoadsFound);
            }
            if (startDoor) { pathStart = startDoors[Finder.LastStartIndex].LeadIn; startAim = startDoors[Finder.LastStartIndex].At; }
            if (endDoor) { pathEnd = endDoors[Finder.LastEndIndex].LeadIn; endAim = endDoors[Finder.LastEndIndex].At; }
            NoteDoorsTaken(path != null && path.Count >= 2, startDoor ? startAim : null, endDoor ? endAim : null);
        }
        else
        {
            if (RoadsLog && (startRadius > 0f || endRadius > 0f)) System.Threading.Interlocked.Increment(ref m_doorFallbacks);
            path = Finder.FindPath(pathStart, pathEnd, searchLocations, joinedThrough);
        }
        // In to a placed location's ring point head on from its lead-in, so the road onto its landing
        // carries straight on: arriving along the ring, its staircase ran beside the road's own end
        // and was refused "on its road" (14 of the 36 placed locations on one test world).
        if (path != null && path.Count >= 2)
        {
            if (startAim is Vector2 s2) path.Insert(0, s2);
            if (endAim is Vector2 e2) path.Add(e2);
        }

        if (path == null || path.Count < 2)
        {
            if (label != null)
                Log.LogDebug($"Could not find path: {label}");
            NoteFailure(startCenter, endCenter, "no route");
            System.Threading.Interlocked.Increment(ref m_roadsWithNoRoute);
            return false;
        }

        // Whether other network already reaches each end's location, by the one reached test on the radius as asked; the
        // network does not change while this road is planned and re-routed.
        bool startReached = EndReached(startCenter, startRadius), endReached = EndReached(endCenter, endRadius);
        // The one entry from a searched path to a planned road (PlanSearchedPath), as the re-routes plan too.
        var finder = Finder!;
        // Read after the plan (refusedAt): never one an earlier plan on this thread left.
        RoadSpatialGrid.RefusedAt = null;
        RoadPlan planned = PlanSearchedPath(finder, path, startCenter, startRadius, endCenter, endRadius,
            startLocation != null, endLocation != null, startReached, endReached, width, searchLocations);
        var searched = planned.Snapped;
        float startEdge = planned.StartEdge, endEdge = planned.EndEdge;
        switch (planned.Stop)
        {
            case PlanStop.NothingBetween:
                // The two locations are close enough that what is left between their
                // protected footprints is shorter than a road. A failed attempt
                // like any other -- the planner goes on to its next candidate, as
                // it always did. Whether the destination ends up reached is not
                // decided here: it is measured once the island is built.
                if (label != null)
                    Log.LogDebug($"Nothing to lay between the two locations once their footprints were cleared: {label}");
                NoteFailure(startCenter, endCenter, "nothing between the two footprints");
                System.Threading.Interlocked.Increment(ref m_roadsRefusedForGrade);
                return false;
            case PlanStop.Staircase:
                return WithoutThatStaircase(finder, planned.Path);
            case PlanStop.Preparation:
                if (label != null)
                    Log.LogDebug($"Road refused: preparation was incomplete for it, destination left unconnected: {label}");
                NoteFailure(startCenter, endCenter, "refused: preparation incomplete");
                System.Threading.Interlocked.Increment(ref m_roadsRefusedForGrade);
                return false;
        }
        path = planned.Path;
        if (planned.Plans == null && planned.Unmet == null && RoadSpatialGrid.RefusedAt is Vector2 planRefused) refusedAt.Add(planRefused);
        List<RoadCrossing> crossings = planned.Crossings;
        List<RoadStairSpan> stairs = planned.Stairs;
        var plans = planned.Plans;
        bool startAtLocation = planned.StartAtLocation, endAtLocation = planned.EndAtLocation;
        // The plan the road keeps (its first, or a re-route's): laid again from it with stairs added (WithStairsAdded).
        RoadPlan kept = planned;
        // Switchback turns too close to fit their curves, even tightened: the search climbs that
        // stretch on stairs instead, once, and carries on as road beyond it. On the five offline
        // worlds it was tried 11 times and built none with stairs (one road was built without them):
        // their staircases there could not be laid. One test world refused 22 roads for these turns.
        if (plans == null && !retries.TightTurn
            && RoadSpatialGrid.LastRefusal == SwitchbackRefusal.TurnsTooClose && RoadSwitchbacks.LastRefusalAt is Vector2 tight)
        {
            finder.OpenToStairs(new[] { tight }, TightTurnStairReachCells);
            if (!RoadsLog) return GenerateRoadCore(startCenter, startRadius, endCenter, endRadius, width, label, networkHints, joinedThrough, retries.WithTightTurn());
            System.Threading.Interlocked.Increment(ref m_tightTurnStairs);
            int before = t_stairRunsLaidHere;
            bool built = GenerateRoadCore(startCenter, startRadius, endCenter, endRadius, width, label, networkHints, joinedThrough, retries.WithTightTurn());
            if (built && t_stairRunsLaidHere > before) System.Threading.Interlocked.Increment(ref m_tightTurnStairsBuilt);
            if (label != null) Log.LogDebug($"Road {label}: switchback turns at {tight} too close to fit; searched again with {finder.OpenStairCells} cell(s) open to stairs: {(built ? "built" : "not built")}");
            return built;
        }
        // A climb too steep for its rests within the grade cap (RoadPitches.PlacePitches) refused the road above: "if a climb
        // is too steep we shouldn't climb there". It is not searched again here (a search round it was tried: on the worlds
        // it built almost none and cost most of the generation time); the planner's other candidates are the way round.
        if (plans != null && (EarthworkCapped || NetworkCutoff.AvoidsAny))
        {
            // A road the game cannot build as planned -- a cut or fill past the
            // terrain limit is clamped, so the surface tilts across the road
            // and jumps between the clamp and the plan -- is
            // searched again with the cells round the offending points priced
            // up, up to EarthworkRetries times; a plan with none past the
            // limit is kept, and if none is found the road is refused.
            // So is one whose writer's reach touches a zone the network goes round (IntoProtected): searched again round it,
            // never refused here; the final check of its whole footprint (WritesProtected) judges it.
            // So is one whose write would change the ground under a structure another road planned (NearAPlannedStructure):
            // searched again round it; the final check (UnderAPlannedStructure) refuses what no way round clears.
            var where = new List<Vector2>();
            ReRouteFaults faults = SafetyFaults(plans, crossings, stairs, where);
            int past = faults.Total;
            if (RoadsLog && faults.NearAStructure > 0) System.Threading.Interlocked.Increment(ref m_keptClearSearched);
            // Stretches held to a road already laid far off this road's own profile are re-routed
            // round too, but never refuse a road: the fill or cut they build is a ridge beside the
            // network (at -2486,1716 a stretch held +8.8 m was a large earthwork ridge).
            int pulled = EarthworkCapped ? HeldOffProfile(plans, where) : 0;
            Log.LogDebug($"Road {label}: {past} planned point(s) past the {EarthworkCap:F0} m earthwork cap in {plans.Count} piece(s), {pulled} stretch(es) held over {HoldPullLimit:F0} m off its profile");
            finder.ClearEarthworkPenalty();
            for (int attempt = 0; (past > 0 || pulled > 0) && attempt < EarthworkRetries; attempt++)
            {
                foreach (var w in where) finder.PenalizeAround(w, EarthworkPenaltyRadius);
#if ROADS_TESTS
                AfterEarthworkPenalty?.Invoke(finder);
#endif
                var retry = Replan(finder, pathStart, pathEnd, startCenter, startRadius, endCenter, endRadius, width, startAim, endAim, startReached, endReached, joinedThrough);
                if (retry == null)
                { Log.LogDebug($"Road {label}: earthwork re-route {attempt + 1} did not route or build ({RoadSpatialGrid.LastRefusal ?? "no route"}), {finder.EarthworkPenaltyCells} cell(s) priced up"); continue; }
                var whereRetry = new List<Vector2>();
                ReRouteFaults faultsRetry = SafetyFaults(retry.Value.plans, retry.Value.crossings, retry.Value.stairs, whereRetry);
                int pastRetry = faultsRetry.Total;
                int pulledRetry = EarthworkCapped ? HeldOffProfile(retry.Value.plans, whereRetry) : 0;
                Log.LogDebug($"Road {label}: earthwork re-route {attempt + 1}: {pastRetry} point(s) past the cap (was {past}), {retry.Value.crossings.Count} crossing(s), {finder.EarthworkPenaltyCells} cell(s) priced up, {finder.PenalizedWaypoints(retry.Value.path)} of {retry.Value.path.Count} waypoints in them; first offending point {(whereRetry.Count > 0 ? whereRetry[0].ToString() : "-")}, crossing {(retry.Value.crossings.Count > 0 ? retry.Value.crossings[0].FromBank + "->" + retry.Value.crossings[0].ToBank : "-")}");
                // Only a re-route the network takes as it meets it is offered: one that then crosses another road or runs
                // beside one was refused for it after it had replaced a first plan that would have been built.
                if ((pastRetry < past || (pastRetry == past && pulledRetry < pulled)) && MeetingRefusal(retry.Value.plans, out Vector2 meets) is string meeting)
                {
                    Log.LogDebug(FormattableString.Invariant($"Road {label}: earthwork re-route {attempt + 1} not taken: it {meeting} at ({meets.x:F1}, {meets.y:F1})"));
                    // Priced up there too: the next attempt goes round where this one met the network, not the same way again.
                    where.Add(meets);
                    continue;
                }
                if (pastRetry < past || (pastRetry == past && pulledRetry < pulled))
                {
                    Log.LogDebug($"Road {label}: re-routed round deep earthwork, {past} -> {pastRetry} points past {EarthworkCap:F0} m, {pulled} -> {pulledRetry} held off its profile");
                    System.Threading.Interlocked.Increment(ref pastRetry < past ? ref m_earthworkReroutes : ref m_pullReroutes);
                    pulled = pulledRetry;
                    faults = faultsRetry;
                    (path, crossings, plans, past, stairs, kept) = (retry.Value.path, retry.Value.crossings, retry.Value.plans, pastRetry, retry.Value.stairs, retry.Value.plan);
                    (startAtLocation, endAtLocation) = (retry.Value.startAtLocation, retry.Value.endAtLocation);
                    where = whereRetry;
                }
                else where = whereRetry.Count > 0 ? whereRetry : where;
            }
            finder.ClearEarthworkPenalty();
            // What is left past the cap refuses the road; what is left in a protected zone's reach, or at a structure another
            // road planned, is the final checks' (RoadRefusal). The counts are the kept plan's own (SafetyFaults).
            if (faults.Earthwork > 0)
            {
                // A road the game cannot build as planned is not a road: it is
                // refused, even if that leaves a destination unserved.
                bool water = crossings.Exists(c => c.Invalid && c.Shallow);
                bool throughDeck = !water && crossings.Exists(c => c.Invalid && !c.Shallow);
                bool ridge = !water && !throughDeck && HeldRidges(plans, null) > 0 && PastTerrainLimit(plans, EarthworkLimit, new List<Vector2>()) == 0;
                if (!water && !throughDeck) System.Threading.Interlocked.Increment(ref m_earthworkUnavoided);
                plans = null;
                refusedAt.AddRange(where);
                RoadSpatialGrid.LastRefusal = water ? ShallowWaterRefusal : throughDeck ? DeckThroughGroundRefusal : ridge ? JunctionRidgeRefusal : EarthworkRefusal;
            }
            else if (EarthworkCapped && !crossings.Exists(c => c.Invalid))
                (path, crossings, plans, stairs, startAtLocation, endAtLocation, kept) = OffRidges(finder, (path, crossings, plans, stairs, startAtLocation, endAtLocation, kept), pulled,
                    pathStart, pathEnd, startCenter, startRadius, endCenter, endRadius, width, startAim, endAim, startReached, endReached, joinedThrough);
        }
        // A crossing with an end no way of meeting reaches (StructureEnds.MeetEnds) is not built: the road is searched again
        // round it. A crossing the built road does not allow (BuiltRefusal), of every kind, is not built either, nor a valley
        // viaduct a road refused outright had on it: the road is searched again without it.
        // Each is banned as the search used it (RoadCrossing.Searched): its banks, and every move it lies on. A search that
        // finds the same route again is not searched again.
        bool bansSpent = retries.CrossingBans >= CrossingRetries || retries.SameAsBeforeBan(searched);
        if (!bansSpent)
        {
            // Every crossing ban reads the ground the final check reads it on (RoadRefusal): what the road writes as it would
            // be stored, of the road as it stands here (Written, its location ends not laid yet), its own earthwork at its decks'
            // ends in it; one assembly for them all, made only for a road with a deck of its own (none read anything else).
            ManualAppend? banWritten = plans != null && HasOwnDeck(crossings) ? BanWritten(plans, crossings, stairs) : null;
            string builtWhy = "";
            // Of the crossings it would store of its own (StoredCrossings, as the final check reads them): one another road
            // laid before it carries is never built by this road.
            RoadCrossing? unmet = plans != null ? BuiltRefusal(plans, StoredCrossings(crossings), out _, out builtWhy, banWritten) : null;
            // A crossing whose structure would stand in a location the road does not go to is searched round as one the built
            // road does not allow is (InAnotherLocation).
            bool inLocationBan = false, noKitBan = false;
            if (plans != null && planned.Unmet == null && unmet == null
                && InAnotherLocation(plans, crossings, out _, out string inLocation, banWritten) is RoadCrossing inAnother)
                (unmet, builtWhy, inLocationBan) = (inAnother, inLocation, true);
            // So is a crossing with a deck built in no kit (WithNoKit): a road is never stored over a deck with no structure.
            else if (banWritten != null && planned.Unmet == null && unmet == null
                && WithNoKit(crossings, out _, out string noKit, banWritten) is RoadCrossing kitless)
                (unmet, builtWhy, noKitBan) = (kitless, noKit, true);
            var banned = planned.Unmet != null ? new List<RoadCrossing> { planned.Unmet }
                : unmet != null ? new List<RoadCrossing> { unmet }
                : plans == null ? (LastStubBetween is RoadCrossing stubbed && crossings.Contains(stubbed) ? new List<RoadCrossing> { stubbed }
                    : crossings.FindAll(c => c.ValleyJump && !c.Shared))
                : new List<RoadCrossing>();
            if (banned.Count > 0)
            {
                // A crossing on a knight's heading with an end not met: the same site is searched again on the grid's headings
                // (RoadPathfinder.KeepToGridHeadingsAt), and only that, before the failure counts as a crossing ban. The site
                // is not banned first: the ban matches any crossing within a cell of its banks, whatever its heading, so it
                // would forbid the square crossing the retry is for. Where the square crossing fails too, it is banned so.
                bool gridFallback = BanForTheNextSearch(finder, banned, planned.Unmet, retries.GridFallbacks < CrossingRetries);
                if (planned.Unmet != null) System.Threading.Interlocked.Increment(ref m_crossingsUnmetSearchedRound);
                else if (inLocationBan) System.Threading.Interlocked.Increment(ref m_inLocationSearchedRound);
                else if (noKitBan) System.Threading.Interlocked.Increment(ref m_noKitSearchedRound);
                else if (unmet != null && !unmet.ValleyJump) System.Threading.Interlocked.Increment(ref m_builtSearchedRound);
                else System.Threading.Interlocked.Increment(ref m_viaductsRefusedAtEnds);
                if (label != null && RoadsLog)
                    Log.LogInfo(FormattableString.Invariant($"[BRIDGES] road {label}: {(planned.Unmet != null ? "an end not met of " + planned.Unmet : unmet != null ? builtWhy + " at " + unmet : "refused with a viaduct")} ({banned[0].FromBank} -> {banned[0].ToBank}, {banned[0].Searched?.Count ?? 0} move(s)); searched again without it{(gridFallback ? ", the site on the grid's headings" : "")}"));
                return GenerateRoadCore(startCenter, startRadius, endCenter, endRadius, width, label, networkHints, joinedThrough,
                    gridFallback ? retries.NextGridFallback(searched) : retries.NextCrossingBan(searched));
            }
        }
        if (planned.Unmet != null) System.Threading.Interlocked.Increment(ref m_bridgeEndsUnmet);
        // Past the searches again, a crossing the built road does not allow, a crossing whose structure would stand in
        // another location or a deck built in no kit refuses the road with it, as a bridge with no road at one end does: the one
        // list's (RoadRefusal, below), asked of the road as it is finished.
        // A location placed before its roads: the road carried on onto its landing, on its placed ground, as
        // a road or else a staircase, as part of this road. Where neither gets there the road is refused
        // and the planner tries its other candidates: there is no approach left to lay later. Outside the
        // Mountains (ApproachStairsAt) there is no staircase but at a boss's entrance, and a road that gets no further
        // keeps its end at the ring.
        // Its location ends as every road judged with them plans them (PlanLocationEnds: generation's and an approach trial's alike):
        // its ways onto placed locations' landings, the landing it lands on a flight with, its approaches and its ends carried
        // into its locations. Whether it may be built so is the one list's (RoadRefusal), its location ends' refusals first.
        PlannedRoad? road = null;
        if (plans != null)
        {
            road = new PlannedRoad
            {
                StartCenter = startCenter, EndCenter = endCenter, Path = path, Plans = plans, Crossings = crossings, Stairs = stairs,
                Ends = new LocationEndContext(startRadius, endRadius, startAtLocation, endAtLocation, startLocation, endLocation, width, startEdge, endEdge),
            };
            PlanLocationEnds(road, label, tally: true, landOnFlight: t_capture == null);
        }
        WrittenRoad? written = null;
        // Every refusal of the one list keeps its effects (its marks, its re-end, its counts, its line), the first's too
        // where the road is then laid again with stairs added (WithStairsAdded) and refused another way.
        void Refused(Refusal refused)
        {
            plans = null;
            refusedAt.AddRange(refused.Marks);
            RoadSpatialGrid.LastRefusal = refused.Reason;
            // Its way onto a location arriving as another way into it does: searched again ending on that way's road
            // (GenerateRoad, ReEndOnAWayIn).
            if (refused.ReEnd is (Vector2 location, Vector2 onItsRoad))
            {
                t_reEnd ??= (location, onItsRoad);
                if (RoadsLog) Log.LogDebug(FormattableString.Invariant(
                    $"Road {label ?? "(unlabelled)"}: arrives at the location at {location} the way another way into it does; searched again to that way's road at ({onItsRoad.x:F1}, {onItsRoad.y:F1})"));
            }
            if (refused.Reason == NotLevelRefusal && RoadsLog) System.Threading.Interlocked.Increment(ref m_notLevelEnds);
            // The zones it would have written into, for an operator's road (road_connect: ManualRoads.ConnectRefusal).
            if (refused.Zones.Count > 0) t_capture?.Protected.UnionWith(refused.Zones);
            if (refused.Reason == EarthworkRefusal) System.Threading.Interlocked.Increment(ref m_earthworkFinished);
            else if (refused.Reason == GroundUnderAStructure) System.Threading.Interlocked.Increment(ref m_keptClearRefused);
            else if (refused.Reason == UnknownGroundUnderAStructure) System.Threading.Interlocked.Increment(ref m_unknownGroundRefused);
            else if (refused.Reason == StructureInAnotherLocation) System.Threading.Interlocked.Increment(ref m_inLocationRefused);
            else if (refused.Reason == DeckWithNoKit) System.Threading.Interlocked.Increment(ref m_noKitRefused);
            else if (refused.Reason.StartsWith(BuiltRoadRefusalReason, StringComparison.Ordinal)) System.Threading.Interlocked.Increment(ref m_builtRefused);
            // A bridge must have road at both ends; one was found stopping in 3 m of water.
            if (refused.Reason == DanglingRefusal)
                Log.LogWarning($"Bridge with no road beyond it on {label}: {refused.Marks[0]} -> {refused.Marks[refused.Marks.Length - 1]}; road refused");
            if (RoadsLog)
            {
                string line = FormattableString.Invariant($"{(refused.Tag != null ? refused.Tag + " road" : "Road")} {label ?? "(unlabelled)"}: {refused.Said} at ({refused.At.x:F1}, {refused.At.y:F1}); refused");
                if (refused.Tag == null) Log.LogDebug(line); else Log.LogInfo(line);
            }
        }
        Refusal? refusal = road != null ? RoadRefusal(road, out written) : null;
        // Refused with a foot kept on a raised landing: this road laid once more from the plan it keeps, those feet with their
        // stair added (WithStairsAdded), and asked again; built so where it passes, before the road is searched for again.
        if (refusal != null && NeedsStairsAdded(road!.Stairs, refusal) && kept.LayAgain is Func<IReadOnlyCollection<Vector2>, RoadPlan> layAgain)
        {
            Refused(refusal);
            var again = WithStairsAdded(road.Stairs, kept.StairsAddedAt, set =>
            {
                RoadPlan replanned = layAgain(set);
                if (replanned.Stop != PlanStop.Planned || replanned.Plans == null) return (replanned, (PlannedRoad?)null, (Refusal?)null, (WrittenRoad?)null);
                var relaid = new PlannedRoad
                {
                    StartCenter = startCenter, EndCenter = endCenter, Path = replanned.Path, Plans = replanned.Plans, Crossings = replanned.Crossings, Stairs = replanned.Stairs,
                    Ends = new LocationEndContext(startRadius, endRadius, replanned.StartAtLocation, replanned.EndAtLocation, startLocation, endLocation, width, startEdge, endEdge),
                };
                PlanLocationEnds(relaid, label, tally: true, landOnFlight: t_capture == null);
                Refusal? refusedAgain = RoadRefusal(relaid, out WrittenRoad? writtenAgain);
                return (replanned, relaid, refusedAgain, writtenAgain);
            }, r => r.Item2 == null || r.Item3 != null, StairsAddedAt.Commit, refusal);
            if (RoadsLog)
                Log.LogInfo(FormattableString.Invariant(
                    $"[FOOT] road {label ?? "(unlabelled)"}: refused at its commit ({refusal.Reason} at ({refusal.At.x:F1}, {refusal.At.y:F1})) with a foot on a raised landing; laid again with its stair added: {(again.Item2 == null ? "not planned (" + (RoadSpatialGrid.LastRefusal ?? "no reason") + ")" : again.Item3 is Refusal still ? "refused (" + still.Reason + ")" : "built")}"));
            if (again.Item2 != null && again.Item3 == null)
            {
                (kept, road, written, refusal) = (again.Item1, again.Item2, again.Item4, null);
                RoadSpatialGrid.LastRefusal = null;
                (path, plans, crossings, stairs, startAtLocation, endAtLocation) = (road.Path, road.Plans, road.Crossings, road.Stairs, again.Item1.StartAtLocation, again.Item1.EndAtLocation);
            }
            else
            {
                // Refused again another way, or not planned so: the road is refused, every refusal's effects kept.
                if (again.Item3 is Refusal other) Refused(other);
                else RoadSpatialGrid.LastRefusal = refusal.Reason;
                plans = null;
                refusal = null;
            }
        }
        if (refusal != null) Refused(refusal);
        var entries = road?.Entries ?? new List<(PlacedLocation placedLocation, RoadSpatialGrid.PlannedPath? road, RoadStairSpan? stairs)>();
        RoadStairRuns.FlightLanding? landing = road?.Landing;
        var approaches = road?.Approaches ?? new List<RoadStairSpan>();
        var carried = road?.Carried ?? new List<RoadSpatialGrid.PlannedPath>();
        // Refused with stairs on it, it is refused: stairs are always on, and a road is never searched again without them
        // (there is no flag for them, nor code to work
        // around them). The planner tries its other candidates.
        if (plans == null)
        {
            t_refusedAt = refusedAt.Count > 0 ? refusedAt : null;
            string why = RoadSpatialGrid.LastRefusal ?? "other (crossing or footprint)";
            m_refusalReasons.AddOrUpdate(why, 1, (_, n) => n + 1);
            if (label != null)
                Log.LogDebug($"Road could not be built ({why}), destination left unconnected: {label}");
            NoteFailure(startCenter, endCenter, "refused: " + why, refusedAt.Count > 0 ? refusedAt[0] : null);
            System.Threading.Interlocked.Increment(ref m_roadsRefusedForGrade);
            return false;
        }
        // Its ends arriving at their locations as road (no staircase there): the ways into those locations it adds (LocationWaysIn),
        // recorded once it is stored, here or (planned outside generation) where its plan is committed (ManualRoads.Commit).
        var waysIn = new List<(Vector2 location, LocationWaysIn.Way way)>();
        foreach (var (atStart, centre, asked, atLocation, placedLocation) in new[]
            { (true, startCenter, startRadius, startAtLocation, startLocation), (false, endCenter, endRadius, endAtLocation, endLocation) })
        {
            if (!atLocation || asked <= 0f || !LocationWaysIn.On) continue;
            Vector2 location = placedLocation?.Centre ?? centre;
            if (StairsAt(atStart, entries, approaches, placedLocation) == null && ArrivalAt(location, atStart, plans, entries, approaches, placedLocation) is LocationWaysIn.Way way)
                waysIn.Add((location, way));
        }
        // Planned outside generation (PlanningWith): handed over as it would be stored, nothing stored. A road onto a
        // staircase's flight is not planned so (MeetingRefusal above): it changes a staircase already laid.
        if (t_capture != null)
        {
            t_capture.Road = new PlannedRoad
            {
                StartCenter = startCenter, EndCenter = endCenter, Path = path, Plans = plans, Crossings = crossings, Stairs = stairs,
                Approaches = approaches, Entries = entries, Carried = carried, WaysIn = waysIn, Written = written,
            };
            return true;
        }

#if ROADS_TESTS
        BeforeRoadStorage?.Invoke();
        RoadChecked?.Invoke(written!);
#endif
        // One road where two would ride along each other (RoadRide, the one owner): where this road rides the network --
        // within a road's width of a road laid before it, or of a structure that road crosses or climbs on, heading its way
        // -- it stores nothing: not its points, nor a crossing of its own there (the road that built it carries it). Asked
        // of the network as it stands before this road is stored. Stored as its final check read it (WrittenRoad): those
        // crossings, its own pieces and what its crossings change, the network unchanged since.
        var plannedCrossings = crossings;
        int crossingsBefore = crossings.Count;
        crossings = written!.Stored;
        if (crossings.Count < crossingsBefore) System.Threading.Interlocked.Add(ref RoadRide.m_crossingsRidden, crossingsBefore - crossings.Count);
        // Its own decks too: a structure replaces the road over its gap, so no piece of it runs on under its own
        // span or bridge (a neighbouring ford's piece in a pool chain did, under a deck turned onto a buildable heading).
        // A bridge whose ends the owner brings the road to (MeetsTheRoad) is left to StructureEnds' own trimming.
        // The other roads on a structure end's landing ground are raised to it with the road (StructureEnds.MeetEnds).
        CrossingGround crossingGround = written.Ground;
        crossingGround.SayMounds(GenerationWorld);
        t_storingRoad = true;
        crossingGround.RaiseOthers();
        foreach (RoadCrossing c in crossings)
            if (!c.Shared)
            {
                System.Threading.Interlocked.Add(ref m_endPads, (c.FromPad ? 1 : 0) + (c.ToPad ? 1 : 0));
                System.Threading.Interlocked.Add(ref m_endShared, (c.FromShared ? 1 : 0) + (c.ToShared ? 1 : 0));
                System.Threading.Interlocked.Add(ref m_endStands, (c.FromStands ? 1 : 0) + (c.ToStands ? 1 : 0));
                foreach (var end in new[] { c.FromEnd, c.ToEnd })
                    if (end is StructureEnds.End { IsStructure: true } e)
                    {
                        System.Threading.Interlocked.Add(ref m_endSteps, e.Steps);
                        System.Threading.Interlocked.Increment(ref e.Steps == 0 ? ref m_endFlat : ref e.Steps == 1 ? ref m_endOneStep : ref m_endMoreSteps);
                    }
                // A structure's two ends as a pair (StructureEnds.Pair): both stepped, both flush, or one of each.
                if (c.FromEnd is StructureEnds.End { IsStructure: true } from && c.ToEnd is StructureEnds.End { IsStructure: true } to)
                    System.Threading.Interlocked.Increment(ref from.Steps > 0 != to.Steps > 0 ? ref m_pairsOneSided : ref from.Steps > 0 ? ref m_pairsStepped : ref m_pairsFlush);
                // An end met by earthwork (StructureEnds.MeetEnds): its landing filled over ground under the road's floor.
                int earthworks = (c.FromEarthwork ? 1 : 0) + (c.ToEarthwork ? 1 : 0);
                if (earthworks == 0) continue;
                System.Threading.Interlocked.Add(ref m_endEarthworks, earthworks);
                if (RoadsLog) Log.LogInfo(FormattableString.Invariant($"[BRIDGES] road {label}: {earthworks} end(s) of the bridge {c.FromBank} -> {c.ToBank} met by earthwork, {c.FromEnd?.Steps.ToString() ?? "-"}/{c.ToEnd?.Steps.ToString() ?? "-"} steps laid"));
            }
        // Its own road raised to it with the crossings' raises, in their order (RaiseOthers): not again here.
        if (landing != null && RoadStairRuns.LayLanding(landing, raiseOwn: false) && RoadsLog)
            Log.LogDebug(FormattableString.Invariant($"[STAIRS] road {label ?? "(unlabelled)"}: landing at {landing.Height:F2} onto the staircase {landing.Run.From} -> {landing.Run.To}, {landing.Removed.Count} walked piece(s) under it taken away, its foot now ({landing.Foot.x:F1}, {landing.Foot.y:F1})"));
        List<RoadSpatialGrid.PlannedPath> own = written.Own;
        var decks = written.Decks;
        if (written.Ridden > 0f) System.Threading.Interlocked.Add(ref RoadRide.m_riddenCentimetres, (long)(written.Ridden * 100f));
        // The staircase a landing cut back is met at its new foot by this road (RoadStairRuns.MeetLanding).
        if (landing != null) RoadStairRuns.MeetLanding(landing, own);
        // Where its ends meet the network, the junctions' fill (RoadJunctionFillet), and where its staircases start at its
        // ends, a small level pad (RoadEndRing.PadUnder, the ring's): stored as its final check read them (Written), read
        // there on the network as it stands now, with the other roads it raises raised and the staircase it lands on cut back.
        foreach (Action<string?> say in written.PadsSaid) say(label);
        var fills = written.Fills;
        RoadSpatialGrid.CommitRoad(own, committed: Committed(label, "road"), decks: decks, ownChecked: true);
        foreach (var fill in fills) { RoadSpatialGrid.CommitLevelled(fill.Points, fill.Heights, fill.Width); RoadOwners.Record(label, "junction fill", fill.Points); }
        foreach (var pad in written.Pads) { RoadSpatialGrid.CommitLevelled(pad.Points, pad.Heights, pad.Width, unpainted: true); RoadOwners.Record(label, "start pad", pad.Points); }
        if (fills.Count > 0) System.Threading.Interlocked.Add(ref m_junctionFills, fills.Count);
        // Its last-resort pitches, as the one list judged them (RoadRefusal: its pieces, ends carried on and ways onto landings).
        foreach (RoadPitches.Pitch p in RoadPitches.Record(written.Footprint.Pitches))
            Log.LogInfo(FormattableString.Invariant(
                $"[PITCH] road {label ?? "(unlabelled)"}: {p.Length:F1} m at up to {p.Steepest:P0} from ({p.Points[0].x:F1}, {p.Points[0].y:F1}) to ({p.Points[p.Points.Count - 1].x:F1}, {p.Points[p.Points.Count - 1].y:F1}), for {RoadPitches.LastResortRunFor ?? "-"}; island {CrossIslandReads.Me}"));
        if (IslandActivity != null)
        {
            // A crossing snapped to a site an earlier road built is that bridge, not a new one.
            int bridges = 0;
            foreach (var c in crossings) if (c.Kind == CrossingKind.Bridge && !c.Shared && !c.Invalid) bridges++;
            IslandActivity.BuiltBridges(bridges);
        }
        // Its ways onto its locations' landings, its ends carried into them and its staircases, as its final check read them
        // (written.Ends, written.RunOf): the road's own pieces into the location owned before anything of it was stored.
        for (int e = 0; e < entries.Count; e++)
        {
            var (entryLocation, entryRoad, entryStairs) = entries[e];
            if (written.Ends.EntryRoads[e] is List<RoadSpatialGrid.PlannedPath> entryPieces)
            {
                RoadSpatialGrid.CommitRoad(entryPieces, intoLocation: true, committed: Committed(label, "onto a landing"), ownChecked: true);
                PlacedLocations.MarkLanded(entryLocation.Centre);
                if (RoadsLog && label != null) Log.LogDebug($"Road {label}: carried on onto the landing, {entryRoad!.TotalLength:F0} m of road into the location");
            }
            if (entryStairs != null)
            {
                JoinNetwork(written.RunOf(entryStairs.Plan), entryStairs.Plan, entryStairs.Kit, atLocation: true);
                RoadStairRuns.TallyRing(entryStairs.Plan);
                PlacedLocations.MarkLanded(entryLocation.Centre);
                if (RoadsLog && label != null) Log.LogDebug($"Road {label}: carried on onto the landing, {entryStairs.Kit.Name} staircase {entryStairs.Plan.Rise:F0} m {(entryStairs.Plan.Reversed ? "down" : "up")}");
            }
        }
        // Its ends arriving at their locations as road (no staircase there): ways into those locations from now on (LocationWaysIn).
        foreach (var (location, way) in waysIn) LocationWaysIn.Record(location, way);
        for (int e = 0; e < carried.Count; e++)
        {
            RoadSpatialGrid.CommitRoad(written.Ends.CarriedRoads[e], intoLocation: true, committed: Committed(label, "carried into its location"), ownChecked: true);
            System.Threading.Interlocked.Increment(ref m_carriedToEdge);
            if (RoadsLog && label != null) Log.LogDebug($"Road {label}: carried on to its location's edge, {carried[e].TotalLength:F1} m into the location");
        }
        foreach (RoadStairSpan approach in approaches)
        {
            // An approach crosses its location's footprint by design: it is a staircase at its location, as an entry is.
            JoinNetwork(written.RunOf(approach.Plan), approach.Plan, approach.Kit, atLocation: true);
            RoadStairRuns.TallyRing(approach.Plan);
            if (RoadsLog && label != null) Log.LogDebug($"Road {label}: location approach staircase, {approach.Kit.Name} {approach.Plan.Rise:F0} m {(approach.Plan.Reversed ? "down" : "up")}");
        }
        foreach (RoadStairSpan stair in stairs)
        {
            StairStructures.MarkLaid(path.GetRange(stair.FromIndex, stair.ToIndex - stair.FromIndex + 1));
            if (stair.Existing) continue;
            RoadStairRuns.TallyRing(stair, plans);
            JoinNetwork(written.RunOf(stair.Plan), stair.Plan, stair.Kit, atLocation: false);
        }
        crossingGround.CommitGround();
        if (RoadCrossingDetector.ToWater)
        {
            foreach (var c in crossings)
            {
                if (c.FromLand is Vector2 f) { System.Threading.Interlocked.Increment(ref m_offLandEnds); Log.LogDebug($"Bridge end off land on {label}: {f} -> {c.FromBank}"); }
                if (c.ToLand is Vector2 t) { System.Threading.Interlocked.Increment(ref m_offLandEnds); Log.LogDebug($"Bridge end off land on {label}: {t} -> {c.ToBank}"); }
            }
        }

        if (networkHints != null)
            for (int i = 0; i < path.Count; i += 8)
            {
                // Joins aim at these hints, so none may lie on a crossing: a
                // later road joined an earlier bridge's waypoint in mid-river
                // and built a second bridge out to it, ending mid-river.
                bool onCrossing = false;
                foreach (var crossing in plannedCrossings)
                    if (i >= crossing.FromIndex && i <= crossing.ToIndex) { onCrossing = true; break; }
                // Nor on a staircase: there is no road there to join.
                foreach (var stair in stairs)
                    if (i >= stair.FromIndex && i <= stair.ToIndex) { onCrossing = true; break; }
                if (!onCrossing) { networkHints.Add(path[i]); IslandNetworkChanged(); }
            }
        m_islandRoads++;
        int roadNumber;
        // Each crossing's kind decided once, as it is stored (BridgeStructures.DecideKind): a viaduct, or a deck whose kit
        // is chosen when it is first built. Decided before the lock every island records its roads under: a viaduct's
        // test samples its piers and ends.
        foreach (RoadCrossing c in crossings) BridgeStructures.DecideKind(c, GenerationWorld);
        foreach (RoadCrossing c in crossings) c.Island = CrossIslandReads.Tag;
        lock (m_recordGate)
        {
            m_roadCrossings.AddRange(crossings);
            roadNumber = ++m_roadsGeneratedCount;
        }
        IslandNetworkChanged();
        LayViaducts(crossings, label);
        foreach (var c in crossings)
            if (c.Shallow && c.ShallowNote != null) Log.LogDebug("  " + c.ShallowNote);

        if (path.Count > 0)
        {
            string pinLabel = label ?? $"Road {roadNumber}";
            lock (m_recordGate) m_roadStartPoints.Add((path[0], pinLabel));
        }
        if (crossings.Count > 0 && label != null)
            Log.LogDebug($"Road {label}: {crossings.Count} river crossing(s)");
        if (RoadsLog && stairs.Count > 0 && label != null)
            Log.LogDebug($"Road {label}: {stairs.Count} staircase(s), {string.Join(", ", stairs.ConvertAll(st => $"{st.Kit.Name} {st.Plan.Rise:F0} m up"))}");

        if (label != null)
            Log.LogDebug($"Generated road: {label} ({path.Count} waypoints)");
        NoteBuilt(startCenter, endCenter);

        return true;
    }

    /// <summary>Why a road whose footprint reaches a zone the network goes round is refused (<see cref="WritesProtected"/>).</summary>
    internal const string ProtectedZoneRefusal = "it would write into a zone the network leaves as it is";

    /// <summary>The crossings a road stores of its own: none another road laid before it carries (RoadRide.RidesCrossing, the
    /// one owner of where two roads ride along each other), read off the network as it stands.</summary>
    private static List<RoadCrossing> StoredCrossings(List<RoadCrossing> crossings)
    {
        var crossingLines = RoadRide.Around(crossings.SelectMany(c => new[] { c.FromBank, c.Center, c.ToBank }).ToList(), RoadRide.Reach);
        return crossings.FindAll(c => !RoadRide.RidesCrossing(crossingLines, c));
    }

    /// <summary>
    /// What a road writes, as it would be stored (<see cref="Footprint"/>, a manual road's append's assembly), off the network as
    /// it stands before this road changes it, and the crossings it stores of its own (<see cref="StoredCrossings"/>): its own
    /// pieces (<see cref="OwnPieces"/>), its junctions' fill (RoadJunctionFillet), its staircases' start pads
    /// (RoadStairRuns.StartPads), what its crossings change (<see cref="GroundOf"/>: the other roads it raises, the cuts under its
    /// decks, its mounds), its staircases and their cuts, and with <paramref name="entries"/> its ways onto its locations' landings,
    /// its ends carried into them and its approaches. Read by the one list of refusals (<see cref="RoadRefusal"/>:
    /// <see cref="UnderAPlannedStructure"/>, <see cref="WritesProtected"/>) and stored as it was read (<see cref="WrittenRoad"/>:
    /// generation's commit and a manual road's append); nothing said or counted (it is, as the road is stored: its pads'
    /// counts and lines kept for its commit, <see cref="WrittenRoad.PadsSaid"/>). <paramref name="path"/>: its search path,
    /// where known, whose points its own staircases are recorded at (<see cref="RunOf"/>, as generation lays them).
    /// <paramref name="landing"/>: the landing it is laid onto a staircase's flight with: the staircase's own road it raises
    /// is among the roads it raises, and its fills and pads are read on the network as it will stand once those roads are
    /// raised and that staircase is cut back (<see cref="Proposed"/>), so the commit stores what was checked.
    /// </summary>
    private static WrittenRoad Written(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings,
        IReadOnlyList<RoadStairSpan> stairs, List<RoadStairSpan> approaches,
        List<(PlacedLocation placedLocation, RoadSpatialGrid.PlannedPath? road, RoadStairSpan? stairs)> entries, List<RoadSpatialGrid.PlannedPath> carried,
        IReadOnlyList<Vector2>? path = null, RoadStairRuns.FlightLanding? landing = null)
    {
        List<RoadCrossing> stored = StoredCrossings(crossings);
        var own = OwnPieces(plans, stored, out float ridden, out var decks);
        var laid = stairs.Where(st => !st.Existing).ToList();
        var ownStairs = new List<RoadStairSpan>(laid);
        ownStairs.AddRange(approaches);
        foreach (var entry in entries) if (entry.stairs != null) ownStairs.Add(entry.stairs);
        LocationEnds ends = LocationEnds.Of(new PlannedRoad { Entries = entries, Carried = carried, Approaches = approaches });
        CrossingGround ground = GroundOf(stored, say: false);
        if (landing != null) ground.AddLanding(landing.Others);
        var proposed = new Proposed(ground.Raises(), landing);
        var fills = RoadJunctionFillet.For(own, GenerationWorld, crossings, ownStairs, proposed);
        var said = new List<Action<string?>>();
        var pads = RoadStairRuns.StartPads(own, ownStairs, GenerationWorld, null, false, proposed, said);
        ManualAppend footprint = Footprint(own, laid, ends, ground, fills, pads, path);
        footprint.Landing = landing;
        return new WrittenRoad(footprint, stored, own, ridden, decks, fills, pads, said, ends);
    }

    /// <summary>
    /// The network as it will stand once a road's changes besides its own points are made, as its checks read it before they
    /// are (<see cref="Written"/>): the other roads it raises at their new heights (by the commit's own rule,
    /// RoadSpatialGrid.Raises), and the staircase it lands on (<paramref name="landing"/>, RoadStairRuns.LayLanding) cut back.
    /// What its junctions' fill (RoadJunctionFillet.For, through RoadRide.Around) and its staircases' start pads
    /// (RoadStairRuns.StartPads) read of the network.
    /// </summary>
    internal sealed class Proposed
    {
        private readonly RoadSpatialGrid.Raises m_raised;
        private readonly StairRun? m_standing, m_trimmed;
        internal Proposed(RoadSpatialGrid.Raises raised, RoadStairRuns.FlightLanding? landing)
        {
            m_raised = raised;
            (m_standing, m_trimmed) = (landing?.Run, landing?.Trimmed);
        }

        /// <summary>A stored point, read by island worker <paramref name="me"/>, at the height it is raised to.</summary>
        internal RoadSpatialGrid.RoadPoint AsRaised(RoadSpatialGrid.RoadPoint rp, int me)
        {
            if (m_raised.To(rp, me, out float to)) rp.h = to;
            return rp;
        }

        /// <summary>The staircases as they will stand: the one landed on cut back.</summary>
        internal IEnumerable<StairRun> Runs(IEnumerable<StairRun> runs) =>
            m_standing == null ? runs : runs.Select(r => ReferenceEquals(r, m_standing) ? m_trimmed! : r);
    }

    /// <summary>
    /// What a road writes as the refusals read it (<see cref="Written"/>, <see cref="RoadRefusal"/>), and what its commit
    /// stores from it, so it stores what was checked: its footprint (<see cref="Footprint"/>, a manual road's append), the
    /// crossings it stores of its own (<see cref="StoredCrossings"/>), its own pieces (<see cref="OwnPieces"/>) with the
    /// metres it rides and its decks kept clear, and what its crossings change (<see cref="Ground"/>: the roads it raises, the
    /// cuts and the mounds).
    /// </summary>
    internal sealed class WrittenRoad
    {
        public readonly ManualAppend Footprint;
        public readonly List<RoadCrossing> Stored;
        public readonly List<RoadSpatialGrid.PlannedPath> Own;
        public readonly float Ridden;
        public readonly List<RoadRide.Segment> Decks;
        /// <summary>Its junctions' fill and its staircases' start pads, as its checks read them (in <see cref="Footprint"/> too).</summary>
        public readonly List<RoadSpatialGrid.PlannedPath> Fills, Pads;
        /// <summary>The counts and lines its start pads make, made as they are stored, with the road's label.</summary>
        public readonly List<Action<string?>> PadsSaid;
        /// <summary>What it lays at its locations' ends: its ways onto placed locations' landings and its ends carried into them,
        /// each as it is stored (<see cref="LocationEnds.EntryRoads"/>, <see cref="LocationEnds.CarriedRoads"/>).</summary>
        public readonly LocationEnds Ends;
        public CrossingGround Ground => Footprint.Ground;
        public WrittenRoad(ManualAppend footprint, List<RoadCrossing> stored, List<RoadSpatialGrid.PlannedPath> own, float ridden, List<RoadRide.Segment> decks,
            List<RoadSpatialGrid.PlannedPath> fills, List<RoadSpatialGrid.PlannedPath> pads, List<Action<string?>> padsSaid, LocationEnds ends) =>
            (Footprint, Stored, Own, Ridden, Decks, Fills, Pads, PadsSaid, Ends) = (footprint, stored, own, ridden, decks, fills, pads, padsSaid, ends);

        /// <summary>The staircase it stores for <paramref name="plan"/>, as its checks read it (<see cref="ManualAppend.Runs"/>).</summary>
        public StairRun RunOf(StairPlan plan)
        {
            foreach (var (run, p, _) in Footprint.Runs) if (ReferenceEquals(p, plan)) return run;
            throw new InvalidOperationException("a staircase stored that its checks did not read");
        }
    }

    /// <summary>
    /// Where a generated road's whole footprint reaches a zone the network goes round (<see cref="NetworkCutoff.Protected"/>):
    /// the road point nearest the first such zone, or null where it reaches none; <paramref name="reached"/>: every such zone.
    /// The footprint is the road as a manual road's
    /// append reads it (<see cref="Written"/>, ManualRoads.ZonesReached), and its bridges' pieces.
    /// </summary>
    private static Vector2? WritesProtected(List<RoadSpatialGrid.PlannedPath> plans, WrittenRoad written, out List<Vector2s> reached)
    {
        reached = NetworkCutoff.Protected(ManualRoads.ZonesReached(written.Footprint, written.Stored));
        if (reached.Count == 0) return null;
        Vector3 centre = ZoneSystem.GetZonePos(reached[0]);
        Vector2 zone = new(centre.x, centre.z), nearest = plans[0].Points[0];
        foreach (RoadSpatialGrid.PlannedPath plan in plans)
            foreach (Vector2 p in plan.Points)
                if ((p - zone).sqrMagnitude < (nearest - zone).sqrMagnitude) nearest = p;
        return nearest;
    }

    /// <summary>
    /// What a road's crossings change besides the road itself, the one list a generated road and a manual road both store
    /// with it: the other roads on an end's landing raised to it (StructureEnds.MeetEnds), the ground cut under each
    /// deck (<see cref="CrossingGround.Cuts"/>) and under each viaduct's end stairs (<see cref="CrossingGround.StairCuts"/>),
    /// and the mounds under its structures' limiting piers and footings (<see cref="CrossingGround.Mounds"/>).
    /// </summary>
    internal sealed class CrossingGround
    {
        /// <summary>The other roads' points on the landings of the road's own crossings' ends, each to its new height.</summary>
        public readonly List<(RoadSpatialGrid.RoadPoint at, float to)> Raised = new();
        /// <summary>The ground cut under every deck to its underside (BridgeStructures.GroundUnderDeck), <see cref="CutWidth"/>
        /// wide.</summary>
        public readonly List<Vector3> Cuts = new();
        public static float CutWidth => BridgeLayout.EndStairsWidth;
        /// <summary>The ground cut under a viaduct's end stairs, as a staircase's stairs are cut (StairLayout.AddStepCuts, at
        /// the steps the end lays, BridgeLayout.EndStep, decided from the road's arrival: StructureEnds), committed
        /// <see cref="StairLayout.CutWidth"/> wide as a staircase's cuts are.</summary>
        public readonly List<Vector3> StairCuts = new();
        /// <summary>The mounds under its own structures' limiting piers and stair footings (StructureGround.Mound): a
        /// viaduct's (DvergrViaduct.Mounds), a stone bridge's (BridgeStructures.StoneMounds), each stored as mound points,
        /// ground and not road (<see cref="MoundPlans"/>, RoadSpatialGrid.RoadPoint.mound).</summary>
        public readonly List<StructureGround.Mound> Mounds = new();
        /// <summary>Each crossing's mounds, as <see cref="SayMounds"/> says them.</summary>
        internal readonly List<(RoadCrossing c, bool viaduct, List<StructureGround.Mound> mounds)> Mounded = new();

        /// <summary>One <c>[MOUND]</c> line per crossing with mounds (<see cref="MoundLine"/>): said as the road is stored, of
        /// the ground its checks read (<see cref="GroundOf"/> with say false).</summary>
        public void SayMounds(WorldGenerator world)
        {
            foreach (var (c, viaduct, mounds) in Mounded) Log.LogInfo(MoundLine(c, viaduct, mounds, world));
        }

        /// <summary>The mounds as the road pieces they are stored as (StructureGround.Mound.Plan).</summary>
        public List<RoadSpatialGrid.PlannedPath> MoundPlans() => Mounds.ConvertAll(m => m.Plan());

        /// <summary>Where in <see cref="Raised"/> the landing's raises begin (<see cref="AddLanding"/>), or -1: none.</summary>
        private int m_landingFrom = -1;

        /// <summary>A landing's raises (RoadStairRuns.FlightLanding.Others) after the crossings' (RoadStairRuns.LayLanding writes them
        /// after the crossings' are written): in <see cref="Raised"/>, the second stage of <see cref="Raises"/>.</summary>
        public void AddLanding(List<(RoadSpatialGrid.RoadPoint at, float to)> others)
        {
            m_landingFrom = Raised.Count;
            Raised.AddRange(others);
        }

        /// <summary>The raises as one ordered transformation (RoadSpatialGrid.Raises): the crossings', then a landing's on the
        /// heights they leave. What its checks predict (RoadTerrainModifier.AsStored) and its commit writes, once.</summary>
        public RoadSpatialGrid.Raises Raises() => m_landingFrom < 0 ? new RoadSpatialGrid.Raises(Raised)
            : new RoadSpatialGrid.Raises(new IReadOnlyList<(RoadSpatialGrid.RoadPoint at, float to)>[]
                { Raised.GetRange(0, m_landingFrom), Raised.GetRange(m_landingFrom, Raised.Count - m_landingFrom) });

        /// <summary>The other roads raised (generation: before the road is stored, so its junctions meet them as raised), a
        /// landing's after the crossings', once (<see cref="Raises"/>).</summary>
        public void RaiseOthers() => RoadSpatialGrid.SetRoadHeights(Raises());

        /// <summary>The cuts stored (generation: once the road and its staircases are).</summary>
        public void CommitGround()
        {
            if (Cuts.Count > 0) RoadSpatialGrid.CommitCuts(Cuts, CutWidth);
            if (StairCuts.Count > 0) RoadSpatialGrid.CommitCuts(StairCuts, StairLayout.CutWidth);
            foreach (RoadSpatialGrid.PlannedPath mound in MoundPlans())
                RoadSpatialGrid.CommitLevelled(mound.Points, mound.Heights, mound.Width, mound: true);
        }
    }

    /// <summary><see cref="CrossingGround"/> of <paramref name="crossings"/>: the raises of those it builds (not of one shared
    /// with a road laid before, which that road raised), and where crossings are built over water
    /// (<see cref="RoadCrossingDetector.ToWater"/>) the ground cut under every deck and under a viaduct's end stairs. Each
    /// crossing with mounds says them (<see cref="MoundLine"/>) but where <paramref name="say"/> is false (a check reading what
    /// the road would write: it says them as it is stored).</summary>
    internal static CrossingGround GroundOf(List<RoadCrossing> crossings, bool say = true)
    {
        var ground = new CrossingGround();
        foreach (RoadCrossing c in crossings)
            if (!c.Shared)
                foreach (var raised in new[] { c.FromRaised, c.ToRaised })
                    if (raised != null) ground.Raised.AddRange(raised);
        var world = GenerationWorld;
        // The mounds under its own structures' limiting piers and footings, decided now its route is chosen and its ends met.
        foreach (RoadCrossing c in crossings)
            if (!c.Shared && BridgeStructures.HasDeck(c))
            {
                bool viaduct = IsViaduct(c);
                List<StructureGround.Mound> mounds = viaduct ? DvergrViaduct.Mounds(c, world) : BridgeStructures.StoneMounds(c, world);
                if (mounds.Count == 0) continue;
                ground.Mounds.AddRange(mounds);
                ground.Mounded.Add((c, viaduct, mounds));
                if (say) Log.LogInfo(MoundLine(c, viaduct, mounds, world));
            }
        if (!RoadCrossingDetector.ToWater) return ground;
        foreach (var c in crossings)
        {
            if (!BridgeStructures.HasDeck(c)) continue;
            float span = Vector2.Distance(c.FromBank, c.ToBank);
            if (span < 2f) continue;
            // Under a viaduct's end stairs the ground is cut as a staircase's is: each step of both lanes, where the end lays
            // it, to expose its walking surface (StairLayout.AddStepCuts), on the generator's ground the ends are decided on.
            if (IsViaduct(c))
            {
                var (near, far) = DvergrViaduct.Ends(c, world);
                Vector2 side = new(-c.Direction.y, c.Direction.x);
                float Natural(float x, float z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world);
                foreach (StructureEnds.End end in new[] { near, far })
                    for (int k = 0; k < end.Steps; k++)
                        foreach (int lane in new[] { BridgeLayout.Ruin.Left, BridgeLayout.Ruin.Right })
                            StairLayout.AddStepCuts(ground.StairCuts, BridgeLayout.EndStep(end.Edge, end.Up, side, end.Height, k, lane),
                                BridgeLayout.EndStepYaw(end.Up), end.Meet, end.Edge, Natural);
            }
            // Under every deck, of every kit, to its underside: no ground pokes up through it, at an end met level (its road
            // run on under the deck's end at the walking surface, StructureEnds.JoinOverlap) or one with stairs.
            ground.Cuts.AddRange(BridgeStructures.GroundUnderDeck(c, world, CrossingGround.CutWidth * 0.5f));
        }
        return ground;
    }

    /// <summary>One <c>[MOUND]</c> line for a crossing whose road stores mounds under its structure (<see cref="GroundOf"/>):
    /// where, the kit, and each mound's raise over the lowest natural ground under it, and whether it stands in water. It
    /// decides nothing.</summary>
    private static string MoundLine(RoadCrossing c, bool viaduct, List<StructureGround.Mound> mounds, WorldGenerator world)
    {
        var raises = new List<string>();
        foreach (StructureGround.Mound m in mounds)
        {
            float low = float.PositiveInfinity;
            foreach (Vector2 p in m.Points) low = Mathf.Min(low, BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world));
            raises.Add(FormattableString.Invariant($"{m.Top - low:F2}{(RoadWater.Wet(low) ? " in water" : "")}"));
        }
        return FormattableString.Invariant($"[MOUND] {(viaduct ? "viaduct" : "stone bridge")} ({c.Center.x:F1}, {c.Center.y:F1}) from ({c.FromBank.x:F1}, {c.FromBank.y:F1}) to ({c.ToBank.x:F1}, {c.ToBank.y:F1}): {mounds.Count} mound(s) raised {string.Join(", ", raises)} m");
    }

    /// <summary>
    /// The ground the planner sees, with each strip a deck
    /// end was walked in from (<see cref="RoadCrossing.FromLand"/>) lowered to
    /// a straight ramp between the old and new banks. Cut only: ground below
    /// the ramp is left as it is. The earthwork cap still measures against the
    /// real ground, so a cut past the terrain limit is refused as any other.
    /// Null when no crossing moved an end.
    /// </summary>
    private static System.Func<Vector2, float>? StripGround(List<RoadCrossing> crossings, float width)
    {
        var world = GenerationWorld;
        float H(Vector2 q) => BiomeBlendedHeight.GetBlendedHeight(q.x, q.y, world);
        var strips = new List<(Vector2 a, Vector2 b, float ha, float hb)>();
        foreach (var c in crossings)
        {
            if (c.FromLand is Vector2 f) strips.Add((f, c.FromBank, H(f), H(c.FromBank)));
            if (c.ToLand is Vector2 t) strips.Add((c.ToBank, t, H(c.ToBank), H(t)));
        }
        if (strips.Count == 0) return null;
        float reach = RoadLocationProtection.Clearance(width);
        return q =>
        {
            float g = H(q);
            foreach (var (a, b, ha, hb) in strips)
            {
                Vector2 ab = b - a;
                float len2 = ab.sqrMagnitude;
                if (len2 < 1e-4f) continue;
                float t = Vector2.Dot(q - a, ab) / len2;
                if (t < 0f || t > 1f || Vector2.Distance(q, a + ab * t) > reach) continue;
                g = Mathf.Min(g, Mathf.Lerp(ha, hb, t));
            }
            return g;
        };
    }

    /// <summary>
    /// After the search, a waypoint within this many metres of a road already
    /// built is moved onto it, so a road that runs alongside another merges
    /// with it and branches off where it leaves, instead of following the
    /// same path beside it. The end waypoints stay. Settable for tests; 0 is off.
    /// </summary>
    internal static float RoadSnap = 6f;

    /// <summary>
    /// A road's own stub is dropped where it merges into a road already built
    /// within <see cref="StubLength"/> of its end: two roads leaving one place
    /// the same way made two branches a few metres apart that joined just
    /// after, and the road now starts at the junction. A road heading another
    /// way never merges that soon and keeps its own branch, so a location can
    /// still have several where the network heads different directions. Works
    /// on snapped waypoints, so only with <see cref="RoadSnap"/>.
    /// </summary>
    internal static List<Vector2>? DropStubs(List<Vector2>? path, bool startReached = true, bool endReached = true)
    {
        if (path == null || RoadSnap <= 0f || path.Count < 3 || !RoadSpatialGrid.IsInitialized) return path;
        // Only a location another road already reaches loses this road's stub:
        // cutting the stub of a location nothing else reaches would cut its only
        // access.
        int first = startReached ? MergeWithin(path, +1) : -1, last = endReached ? MergeWithin(path, -1) : -1;
        // Running alongside the road already serving the location instead, never touching it: the two
        // share one trunk, this one branching off where the trunk stands at its ground's height.
        Vector2? firstOn = null, lastOn = null;
        if (first < 0 && startReached && TrunkBranch(path, +1, out Vector2 a) is int fa && fa >= 0) { first = fa; firstOn = a; }
        if (last < 0 && endReached && TrunkBranch(path, -1, out Vector2 b) is int lb && lb >= 0) { last = lb; lastOn = b; }
        if (first < 0 && last < 0) return path;
        int from = first < 0 ? 0 : first, to = last < 0 ? path.Count - 1 : last;
        if (to - from < 1) return path;
        var kept = path.GetRange(from, to - from + 1);
        if (firstOn is Vector2 fo && Vector2.Distance(fo, kept[0]) > 0.5f) kept.Insert(0, fo);
        if (lastOn is Vector2 lo && Vector2.Distance(lo, kept[kept.Count - 1]) > 0.5f) kept.Add(lo);
        return kept;
    }

    public const float StubLength = 80f;

    /// <summary>The first waypoint from one end that lies on a road already
    /// built, within <see cref="StubLength"/> along the path; -1 if none, or
    /// if the end itself is on a road.</summary>
    private static int MergeWithin(List<Vector2> path, int step)
    {
        int start = step > 0 ? 0 : path.Count - 1;
        if (RoadSpatialGrid.TryGetRoadWithin(path[start], RoadBeside.OnRoad, out _)) return -1;
        float along = 0f;
        for (int i = start + step; i >= 0 && i < path.Count; i += step)
        {
            along += Vector2.Distance(path[i - step], path[i]);
            if (along > StubLength) return -1;
            if (RoadSpatialGrid.TryGetRoadWithin(path[i], RoadBeside.OnRoad, out _)) return i;
        }
        return -1;
    }

    /// <summary>How far in from a location a road may branch off a trunk serving it.</summary>
    public const float TrunkReach = 120f;
    /// <summary>The trunk must come alongside within this far of the location.</summary>
    public const float TrunkNear = 30f;

    /// <summary>
    /// Where a road, near its <paramref name="step"/> end at a location another road already serves,
    /// runs within that road's terrain reach (the band RoadSpatialGrid.HoldToNetwork holds to its
    /// height) without touching it: the waypoint furthest from the location, along that stretch, where
    /// the other road stands within <see cref="RoadPathfinder.JunctionStep"/> of the ground, and the
    /// road point there (<paramref name="onTrunk"/>); else -1. The road then ends on the trunk there,
    /// at grade, instead of running beside it to the location at another height: two roads leaving one
    /// place side by side were the largest junction earthworks (so they share a trunk).
    /// </summary>
    private static int TrunkBranch(List<Vector2> path, int step, out Vector2 onTrunk)
    {
        onTrunk = default;
        var world = GenerationWorld;
        if (RoadPathfinder.JunctionStep <= 0f || world == null) return -1;
        float band = RoadProfile.Footprint(RoadWidth);
        int start = step > 0 ? 0 : path.Count - 1, best = -1;
        bool alongside = false;
        float along = 0f;
        for (int i = start; i >= 0 && i < path.Count; i += step)
        {
            if (i != start) along += Vector2.Distance(path[i - step], path[i]);
            if (along > TrunkReach) break;
            if (RoadSpatialGrid.TryGetRoadHeightWithin(path[i], band, out float h))
            {
                if (!alongside && along > TrunkNear) break;
                alongside = true;
                if (i != start && Mathf.Abs(h - RoadSpatialGrid.JunctionGround(path[i], world)) <= RoadPathfinder.JunctionStep
                    && RoadSpatialGrid.TryGetRoadWithin(path[i], band, out Vector2 q)) { best = i; onTrunk = q; }
            }
            else if (alongside) break;
        }
        return best;
    }

    /// <summary>A road's ends pulled back to road ground: the trim at a location's
    /// edge can fall in a river, and the road then ended in the water.</summary>
    internal static List<Vector2>? TrimWetEnds(List<Vector2>? path)
    {
        if (path == null || path.Count < 2) return path;
        var world = GenerationWorld;
        int from = 0, to = path.Count - 1;
        while (from < to && !RoadCrossingDetector.IsRoadGround(path[from], world)) from++;
        while (to > from && !RoadCrossingDetector.IsRoadGround(path[to], world)) to--;
        if (from == 0 && to == path.Count - 1) return path;
        return to - from >= 1 ? path.GetRange(from, to - from + 1) : null;
    }

    /// <summary>A crossing with a deck (BridgeStructures.HasDeck) whose bank has no planned road piece starting or ending at it.</summary>
    private static bool DanglingBridge(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings,
        Vector2 roadStart, Vector2 roadEnd, out RoadCrossing dangling)
    {
        // A bank at the road's own start or end is the location itself (a coast
        // landing starts on its bridge): only a bank in between needs road.
        bool AtRoadEnd(Vector2 bank) => Vector2.Distance(bank, roadStart) < 8f || Vector2.Distance(bank, roadEnd) < 8f;
        dangling = null!;
        foreach (var c in crossings)
        {
            if (!BridgeStructures.HasDeck(c)) continue;
            bool from = false, to = false;
            // The road ends at a bank, or where the one owner meets the bridge's end (StructureEnds: its stairs' foot).
            var (near, far) = MeetsTheRoad(c) ? BridgeStructures.Ends(c, GenerationWorld) : default;
            foreach (var plan in plans)
            {
                if (plan.Points.Count == 0) continue;
                Vector2 a = plan.Points[0], b = plan.Points[plan.Points.Count - 1];
                from |= Vector2.Distance(a, c.FromBank) < 3f || Vector2.Distance(b, c.FromBank) < 3f
                    || (near.IsSet && (Vector2.Distance(a, near.Joint) < 1f || Vector2.Distance(b, near.Joint) < 1f));
                to |= Vector2.Distance(a, c.ToBank) < 3f || Vector2.Distance(b, c.ToBank) < 3f
                    || (far.IsSet && (Vector2.Distance(a, far.Joint) < 1f || Vector2.Distance(b, far.Joint) < 1f));
            }
            from |= AtRoadEnd(c.FromBank);
            to |= AtRoadEnd(c.ToBank);
            if (!from || !to) { dangling = c; return true; }
        }
        return false;
    }

    /// <summary>
    /// A road running alongside another in the same corridor merges onto it even when it is
    /// further off than <see cref="RoadSnap"/>: a generated world was measured with ~400 m of
    /// road with a second road 8-14 m beside it on the same heading at nearly the same height,
    /// beyond the snap and beyond the reuse discount (which only prices cells the other road
    /// covers). A waypoint is moved onto the other road when that road is within this reach,
    /// heads the same way (within <see cref="CorridorAngle"/>) and stands within
    /// <see cref="CorridorHeight"/> of the ground here, over a run of at least
    /// <see cref="CorridorRun"/> metres; the waypoints either side of a run move halfway, so the
    /// road leans into the join instead of jogging. Settable for tests; 0 is off.
    /// </summary>
    internal static float CorridorSnap = 14f;
    internal const float CorridorAngle = RoadRide.Angle;
    internal const float CorridorHeight = 3f;
    internal const float CorridorRun = 12f;

    /// <summary>Why a planned road may not meet the network as it would: it crosses another road (<see cref="CrossesNetwork"/>;
    /// it meets one at a junction, at its ends or along a stretch it shares, or it is not built, even if that rejects a POI), or it runs beside one at its height (<see cref="BraidsNetwork"/>;
    /// where its search did not put it on the other road it braids beside it: roads at 3354,6308 and -5487,5808 were a
    /// mess), or it runs on or under a staircase's flight laid before it (<see cref="RoadStairRuns.OnALaidFlight"/>: it meets
    /// a staircase only at its foot or top, straight on). Null where it does none of these; <paramref name="at"/>: where. The
    /// one test of a road against the network it meets, for the road, its earthwork re-routes and the ways onto a placed
    /// location's landing.</summary>
    internal static string? MeetingRefusal(List<RoadSpatialGrid.PlannedPath> plans, out Vector2 at) => MeetingRefusal(plans, out at, out _, landOnFlight: false);

    /// <summary>
    /// <see cref="MeetingRefusal(List{RoadSpatialGrid.PlannedPath}, out Vector2)"/>; with <paramref name="landOnFlight"/>, a road
    /// on or under a staircase's flight is not refused where a landing level with the row it reaches fits
    /// (RoadStairRuns.LandingOnto, its fill within the earthwork cap): <paramref name="landed"/>, which the road is then laid
    /// with (<see cref="LandOnTheFlight"/>). It meets the foot straight on, else it lands on the row it reaches, else it is
    /// refused. Only the road itself is laid with one: its re-routes and its ways onto a location's landing are refused as before.
    /// <paramref name="laidWith"/>: the landing the road was already laid with (<see cref="LandOnTheFlight"/>, as
    /// <see cref="RoadRefusal"/> reads the road as finished): the flight it lands on is met by it, as this found.
    /// </summary>
    internal static string? MeetingRefusal(List<RoadSpatialGrid.PlannedPath> plans, out Vector2 at, out RoadStairRuns.FlightLanding? landed, bool landOnFlight,
        RoadStairRuns.FlightLanding? laidWith = null)
    {
#if ROADS_TESTS
        t_meetingsAsked++;
#endif
        at = default;
        landed = null;
        if (CrossesNetwork(plans, out at)) return "crosses another road";
        if (BraidsNetwork(plans, out at)) return "runs beside another road";
        if (MeetsOverCap(plans, out at)) return JunctionOverCapRefusal;
        if (laidWith != null) { at = default; return null; }
        var (points, heights, _) = Flatten(plans);
        if (!RoadStairRuns.OnALaidFlight(points, heights, out at)) return null;
        var widths = new List<float>(points.Count);
        foreach (RoadSpatialGrid.PlannedPath plan in plans) for (int i = 0; i < plan.Points.Count; i++) widths.Add(plan.WidthAt(i));
        if (landOnFlight && RoadStairRuns.LandingOnto(points, heights, widths, (x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, GenerationWorld),
                GroundBeforeRoads(GenerationWorld), EarthworkLimit) is RoadStairRuns.FlightLanding fits)
        {
            landed = fits;
            return null;
        }
        return OnAFlightRefusal;
    }

    /// <summary>Why a road that meets the network on a stretch steeper than the grade cap is refused (<see cref="MeetsOverCap"/>).</summary>
    internal const string JunctionOverCapRefusal = "meets another road on a stretch over the grade cap";

    /// <summary>Whether an end of a piece of <paramref name="plans"/>, or of what of them is stored as the road's own
    /// (RoadRide.Own: where it comes onto another road it rides and where it leaves it), meets a road already laid (within the
    /// reach a road's end takes that road's height at, RoadSpatialGrid.JoinReach) where the network is past the grade cap
    /// across the junction (RoadPathfinder.MeetsWithinCap): on another road's pitch. A road that rides another from its
    /// location and leaves it mid-pitch makes its junction where it leaves, not at its plan's end. <paramref name="at"/>: the network
    /// point it meets.</summary>
    private static bool MeetsOverCap(List<RoadSpatialGrid.PlannedPath> plans, out Vector2 at)
    {
        at = default;
        foreach (RoadSpatialGrid.PlannedPath plan in plans.Concat(RoadRide.Own(plans, out _)))
        {
            if (plan.FollowTerrain || plan.Points.Count < 2) continue;
            float reach = RoadSpatialGrid.JoinReach(plan.Width);
            foreach (Vector2 end in new[] { plan.Points[0], plan.Points[plan.Points.Count - 1] })
                if (RoadSpatialGrid.TryGetRoadWithin(end, reach, out Vector2 met) && !RoadPathfinder.MeetsWithinCap(met))
                { at = met; return true; }
        }
        return false;
    }

    /// <summary>
    /// Why a way onto a placed location's landing from the road's end at <paramref name="end"/> (a road or a staircase) may not
    /// meet the road and the network as it would (<see cref="PlanEntry"/>), or null: a staircase that doubles back beside its
    /// own road (<see cref="DoublesBackBesideItsRoad"/>), and, with <paramref name="network"/>, the road with it against the
    /// network (<see cref="MeetingRefusal(List{RoadSpatialGrid.PlannedPath}, out Vector2)"/>).
    /// </summary>
    /// <param name="own">The road's own earlier stretch (<see cref="OwnRoadOf"/>), built once for every candidate way from the
    /// same road; built here where not given.</param>
    internal static string? EntryMeetingRefusal(bool atStart, List<RoadSpatialGrid.PlannedPath> plans, Vector2 end,
        RoadSpatialGrid.PlannedPath? road, RoadStairSpan? span, bool network, Lazy<OwnRoad?>? own = null)
    {
        var all = WithEntry(atStart, plans, end, road, span);
        if (span != null && all.Count > plans.Count
            && DoublesBackBesideItsRoad((own ?? new Lazy<OwnRoad?>(() => OwnRoadOf(plans, atStart), false)).Value, all[atStart ? 0 : all.Count - 1], out Vector2 back))
            return FormattableString.Invariant($"{DoublesBackRefusal} at ({back.x:F1}, {back.y:F1})");
        return network && MeetingRefusal(all, out Vector2 at) is string meeting ? FormattableString.Invariant($"{meeting} at ({at.x:F1}, {at.y:F1})") : null;
    }

    /// <summary>The refusal of a way onto a landing that doubles back beside its own road (<see cref="DoublesBackBesideItsRoad"/>).</summary>
    internal const string DoublesBackRefusal = "doubles back beside its own road";

    /// <summary>
    /// Whether a way onto a placed location's landing (<paramref name="entry"/>, on the road's end at the location: its start where
    /// <paramref name="atStart"/>) ends beside the road's own earlier stretch at that road's height, as a road beside a road
    /// is judged (<see cref="RoadBeside"/>: within its reach across, within its height), away from the junction where the
    /// two meet (<see cref="JunctionEnd"/> back along the road from its end). The road was already at that height there: the
    /// staircase climbed or came down to it beside itself (a road coming in level with a landing, down a dip to its end, and
    /// a staircase from there doubling back up beside it onto the landing, its top a few metres from the road at the road's
    /// height), and is laid from further back along the road instead. A staircase that doubles back to end well above or
    /// below its road is not beside it. The network's roads are <see cref="BraidsNetwork"/>'s.
    /// </summary>
    internal static bool DoublesBackBesideItsRoad(OwnRoad? own, RoadSpatialGrid.PlannedPath entry, out Vector2 at)
    {
        at = default;
        if (own == null || entry.Points.Count == 0) return false;
        // Where it ends: its end away from the road's.
        int first = 0, last = entry.Points.Count - 1;
        int far = Vector2.Distance(entry.Points[first], own.End) >= Vector2.Distance(entry.Points[last], own.End) ? first : last;
        if (!own.Lines.Nearest(entry.Points[far], RoadBeside.Current, out _, height: entry.Heights[far], heightTolerance: RoadBeside.Height)) return false;
        at = entry.Points[far];
        return true;
    }

    /// <summary>A road's own line short of its end at a location (<see cref="DoublesBackBesideItsRoad"/>), and that end.</summary>
    internal sealed class OwnRoad
    {
        internal readonly RoadRide.Lines Lines;
        internal readonly Vector2 End;
        internal OwnRoad(RoadRide.Lines lines, Vector2 end) { Lines = lines; End = end; }
    }

    /// <summary>The road <paramref name="plans"/>' own line, its junction with the way onto the landing at its end (its
    /// start where <paramref name="atStart"/>) left out (<see cref="JunctionEnd"/>); null where nothing is left.</summary>
    internal static OwnRoad? OwnRoadOf(List<RoadSpatialGrid.PlannedPath> plans, bool atStart)
    {
        var (points, heights, along) = Flatten(plans);
        if (points.Count < 2) return null;
        float total = along[points.Count - 1];
        var ownPoints = new List<Vector2>();
        var ownHeights = new List<float>();
        for (int i = 0; i < points.Count; i++)
            if ((atStart ? along[i] : total - along[i]) >= JunctionEnd) { ownPoints.Add(points[i]); ownHeights.Add(heights[i]); }
        if (ownPoints.Count == 0) return null;
        RoadRide.Lines lines = RoadRide.Around(Array.Empty<Vector2>(), 0f);
        lines.AddRoad(ownPoints, ownHeights);
        return new OwnRoad(lines, points[atStart ? 0 : points.Count - 1]);
    }

    /// <summary>A road laid with a landing onto a staircase's lower stairs (<see cref="MeetingRefusal(List{RoadSpatialGrid.PlannedPath}, out Vector2, out RoadStairRuns.FlightLanding?, bool)"/>):
    /// its heights levelled over the landing, as the landing found them.</summary>
    internal static void LandOnTheFlight(List<RoadSpatialGrid.PlannedPath> plans, RoadStairRuns.FlightLanding landing)
    {
        int k = 0;
        foreach (RoadSpatialGrid.PlannedPath plan in plans)
            for (int i = 0; i < plan.Heights.Count; i++) plan.Heights[i] = landing.Heights[k++];
    }

    /// <summary>The refusal of a road that runs on or under a staircase's flight (<see cref="MeetingRefusal"/>).</summary>
    internal const string OnAFlightRefusal = "runs under a staircase's flight";

    /// <summary>A stretch of a road this long or longer running on another road shares it (a trunk both walk); shorter, it
    /// passes through.</summary>
    public const float SharedRun = 12f;
    /// <summary>A road's own ends meet the network within this of them: a junction, not a crossing.</summary>
    public const float JunctionEnd = 6f;

    /// <summary>A road's planned pieces end to end: their points, heights, and how far along the road each point lies.</summary>
    private static (List<Vector2> points, List<float> heights, float[] along) Flatten(List<RoadSpatialGrid.PlannedPath> plans)
    {
        var points = new List<Vector2>();
        var heights = new List<float>();
        foreach (RoadSpatialGrid.PlannedPath plan in plans) { points.AddRange(plan.Points); heights.AddRange(plan.Heights); }
        var along = new float[points.Count];
        for (int i = 1; i < points.Count; i++) along[i] = along[i - 1] + Vector2.Distance(points[i - 1], points[i]);
        return (points, heights, along);
    }

    /// <summary>
    /// Whether a planned road passes through another already built: away from its own ends, a stretch on that road
    /// shorter than <see cref="SharedRun"/>, or one as long not within a step of it all along (StairLayout.Walkable), that comes
    /// in on one side of it and leaves on the other (an X at grade, or one road over or under another). A road may still meet the network at its ends (a T or Y) and share a trunk along a
    /// longer stretch. <paramref name="at"/>: where.
    /// </summary>
    internal static bool CrossesNetwork(List<RoadSpatialGrid.PlannedPath> plans, out Vector2 at)
    {
        at = default;
        if (!RoadSpatialGrid.IsInitialized) return false;
        var (points, heights, along) = Flatten(plans);
        if (points.Count < 3) return false;
        float total = along[points.Count - 1], touch = RoadWidth * 0.5f;
        // The network's line (RoadRide): a crossing's or a staircase's line is road too, so a road through a structure's gap
        // in another road crosses it as it crosses the road.
        var lines = RoadRide.Around(points, RoadRide.Reach);
        // Only where every touching point meets the other road within a walker's step of its surface is a long touch a trunk the
        // two share. A road touching another for as long at another height anywhere along it (a shallow X, one road over or
        // under the other, a stretch of it apart) shares nothing there and is judged as a crossing.
        int k = 0;
        while (k < points.Count)
        {
            if (!lines.Nearest(points[k], touch, out _)) { k++; continue; }
            int first = k;
            while (k + 1 < points.Count && lines.Nearest(points[k + 1], touch, out _)) k++;
            int last = k++;
            if (along[first] < JunctionEnd || total - along[last] < JunctionEnd) continue;
            if (along[last] - along[first] >= SharedRun && WalkableAll(lines, points, heights, first, last, touch)) continue;
            // The other road's line here: its heading at the middle of the touch (RoadRide: from its road points 1.5-4 m
            // apart, never a staircase's cut-only points, which near a staircase's end turned the line).
            Vector2 mid = points[(first + last) / 2];
            if (!lines.Nearest(mid, 4f, out RoadRide.Hit line) || !line.HasHeading) continue;
            Vector2 centre = line.At, dir = line.Dir, normal = new Vector2(-dir.y, dir.x);
            // Its own points a few metres before and after the touch, either side of the other road's line.
            int before = first, after = last;
            while (before > 0 && along[first] - along[before] < 4f) before--;
            while (after < points.Count - 1 && along[after] - along[last] < 4f) after++;
            float sideIn = Vector2.Dot(points[before] - centre, normal), sideOut = Vector2.Dot(points[after] - centre, normal);
            if (Mathf.Abs(sideIn) > touch && Mathf.Abs(sideOut) > touch && (sideIn > 0f) != (sideOut > 0f))
            {
                at = mid;
                return true;
            }
        }
        return false;
    }


    /// <summary>Whether every point from <paramref name="first"/> to <paramref name="last"/> meets the other road it touches
    /// within a walker's step of its surface (StairLayout.Walkable, the walk verdict; <see cref="CrossesNetwork"/>): a stretch the
    /// two share, walked from one onto the other anywhere along it.</summary>
    private static bool WalkableAll(RoadRide.Lines lines, List<Vector2> points, List<float> heights, int first, int last, float touch)
    {
        for (int i = first; i <= last; i++)
            if (!lines.Nearest(points[i], touch, out _, height: heights[i], walk: true)) return false;
        return true;
    }

    /// <summary>
    /// Whether a planned road runs beside another already built, at its height, for longer than the cap
    /// (<see cref="RoadBeside"/>: within its reach across and its height, off the other road), away from its own ends
    /// (<see cref="JunctionEnd"/>: its junctions). A lens (off the other road and back onto it), a duplicate (beside it all
    /// the way) and a road that comes in beside the trunk and ends on it, or starts on it and runs out beside it (a Y at its
    /// own end), are judged alike, by how long they run beside it (the length is capped like every
    /// other parallel check; such a Y once passed however long it ran beside the trunk). A road that shares a trunk
    /// along its middle and comes onto it or leaves it there (onto a shared bridge and off again) is not judged here: its run beside the trunk is the fork where two roads part.
    /// </summary>
    internal static bool BraidsNetwork(List<RoadSpatialGrid.PlannedPath> plans, out Vector2 at)
    {
        at = default;
        if (!RoadSpatialGrid.IsInitialized) return false;
        var (points, heights, along) = Flatten(plans);
        if (points.Count < 3) return false;
        float total = along[points.Count - 1];
        // Where it rides the network (RoadRide: within a road's width, its way, at its height) it is on the other road, the
        // trunk the two share, whose road it then stores none of; beside it is past that, within RoadBeside's reach.
        var lines = RoadRide.Around(points, RoadBeside.Current);
        bool[] rides = RoadRide.Rides(lines, points, heights);
        var place = new RoadBeside.Place[points.Count];
        for (int i = 0; i < points.Count; i++) place[i] = rides[i] ? RoadBeside.Place.On : RoadBeside.At(lines, points[i], heights[i]);
        // Whether the road is on the other road from point i to its end (step +1) or from its start to i (step -1), its
        // junction's mouth aside.
        bool OnToItsEnd(int i, int step)
        {
            for (int j = i; j >= 0 && j < points.Count; j += step)
                if (place[j] != RoadBeside.Place.On && (step > 0 ? total - along[j] : along[j]) >= JunctionEnd) return false;
            return true;
        }
        int k = 0;
        while (k < points.Count)
        {
            if (place[k] != RoadBeside.Place.Beside) { k++; continue; }
            int first = k;
            while (k + 1 < points.Count && place[k + 1] == RoadBeside.Place.Beside) k++;
            int last = k++;
            bool fromTrunk = first > 0 && place[first - 1] == RoadBeside.Place.On, toTrunk = last + 1 < points.Count && place[last + 1] == RoadBeside.Place.On;
            // A fork off a trunk shared along the road's middle: not judged (see the summary).
            if (fromTrunk != toTrunk && !(toTrunk ? OnToItsEnd(last + 1, +1) : OnToItsEnd(first - 1, -1))) continue;
            // Its own ends left out: the stretch between them.
            while (first <= last && along[first] < JunctionEnd) first++;
            while (last >= first && total - along[last] < JunctionEnd) last--;
            if (first <= last && RoadBeside.TooLong(along[last] - along[first])) { at = points[first]; return true; }
        }
        return false;
    }

    /// <summary>Waypoints moved onto the network's line nearby. A held waypoint (<paramref name="held"/>: a structure's end
    /// the road was brought to) stays where it is and in the path, neither snapped nor eased toward a corridor.</summary>
    internal static List<Vector2>? SnapToNetwork(List<Vector2>? path, float? width = null, System.Func<Vector2, bool>? held = null,
        RoadLocationProtection.Targets locations = default)
    {
        if (path == null || RoadSnap <= 0f || path.Count < 3 || !RoadSpatialGrid.IsInitialized) return path;
        var moved = new Vector2[path.Count];
        var fixedPoint = new bool[path.Count];
        var keep = new bool[path.Count];
        for (int i = 0; i < path.Count; i++) { moved[i] = path[i]; keep[i] = held != null && held(path[i]); }
        // Onto the network's line (RoadRide): a crossing's or a staircase's line too, so a road coming along another over
        // its bridge or ford snaps onto the deck's line, not to the banks either side of the gap. Never onto a cell a structure
        // closes to its search (RoadPathfinder.ClosedToARoad: a location's approach no road climbed, a viaduct's deck the road
        // does not jump), which it went beside.
        var lines = RoadRide.Around(path, Mathf.Max(RoadSnap, CorridorSnap * 1.5f));
        // A structure's line only about the ground's height here (never a flight or deck far over or under the waypoint).
        for (int i = 1; i < path.Count - 1; i++)
        {
            if (keep[i]) { fixedPoint[i] = true; continue; }
            bool road = lines.Nearest(path[i], RoadSnap, out RoadRide.Hit onRoad, structures: false);
            if (GenerationWorld != null && lines.Nearest(path[i], road ? onRoad.Distance : RoadSnap, out RoadRide.Hit onDeck, height: BiomeBlendedHeight.GetBlendedHeight(path[i].x, path[i].y, GenerationWorld),
                    heightTolerance: RoadRide.StructureHeight, roads: false))
            { onRoad = onDeck; road = true; }
            if (road && !RoadPathfinder.ClosedToARoad(onRoad.At)) { moved[i] = onRoad.At; fixedPoint[i] = true; }
        }
        if (CorridorSnap > RoadSnap && GenerationWorld != null)
        {
            // The nearest of the network's line within the corridor's reach heading this way, at about this ground's height.
            var corridor = new Vector2?[path.Count];
            for (int i = 1; i < path.Count - 1; i++)
                if (!fixedPoint[i] && lines.Nearest(path[i], CorridorSnap, out RoadRide.Hit same, path[i + 1] - path[i - 1],
                        BiomeBlendedHeight.GetBlendedHeight(path[i].x, path[i].y, GenerationWorld), CorridorHeight, paintOnly: false)
                    && !RoadPathfinder.ClosedToARoad(same.At))
                    corridor[i] = same.At;
            int a = 1;
            while (a < path.Count - 1)
            {
                if (corridor[a] == null) { a++; continue; }
                int b = a;
                while (b + 1 < path.Count - 1 && (corridor[b + 1] != null || fixedPoint[b + 1])) b++;
                float run = 0f;
                for (int k = a; k < b; k++) run += Vector2.Distance(path[k], path[k + 1]);
                if (run >= CorridorRun)
                {
                    for (int k = a; k <= b; k++) if (corridor[k] is Vector2 q) moved[k] = q;
                    if (a - 1 > 0 && !fixedPoint[a - 1] && lines.Nearest(path[a - 1], CorridorSnap * 1.5f, out RoadRide.Hit qa)
                        && Vector2.Lerp(path[a - 1], qa.At, 0.5f) is var ea && !RoadPathfinder.ClosedToARoad(ea))
                        moved[a - 1] = ea;
                    if (b + 1 < path.Count - 1 && !fixedPoint[b + 1] && lines.Nearest(path[b + 1], CorridorSnap * 1.5f, out RoadRide.Hit qb)
                        && Vector2.Lerp(path[b + 1], qb.At, 0.5f) is var eb && !RoadPathfinder.ClosedToARoad(eb))
                        moved[b + 1] = eb;
                    System.Threading.Interlocked.Add(ref m_corridorSnapped, b - a + 1);
                }
                a = b + 1;
            }
        }
        // A river jump the search priced (a leg over river water, RoadCrossingDetector.SegmentCrossesRiver: the detector's own
        // test of a crossing on a path) is never made longer by the snap: where moving its banks would lengthen it, both stay
        // where the search put them, so the crossing is built at the length the search priced and judged by the one ford cap
        // (RoadCrossingDetector.WithinFordCap) with no slack. Its banks also stay in the path: a waypoint snapped next to one
        // never stands in for it. Only legs a move reaches are read (a leg whose end moved, or the one beyond a moved waypoint).
        var bank = new bool[path.Count];
        var world = GenerationWorld;
        if (world != null)
            for (int i = 0; i + 1 < path.Count; i++)
            {
                bool touched = false;
                for (int k = Mathf.Max(0, i - 1); k <= Mathf.Min(path.Count - 1, i + 2) && !touched; k++) touched = moved[k] != path[k];
                if (!touched || !RoadCrossingDetector.SegmentCrossesRiver(path[i], path[i + 1], world)) continue;
                bank[i] = bank[i + 1] = true;
                if (Vector2.Distance(moved[i], moved[i + 1]) > Vector2.Distance(path[i], path[i + 1]))
                    (moved[i], moved[i + 1]) = (path[i], path[i + 1]);
            }
        var result = new List<Vector2>(path.Count) { path[0] };
        bool lastKept = true;
        for (int i = 1; i < path.Count - 1; i++)
        {
            bool stays = keep[i] || bank[i];
            bool near = Vector2.Distance(result[result.Count - 1], moved[i]) <= 0.5f;
            if (!near) { result.Add(moved[i]); lastKept = stays; }
            // A held waypoint or a river jump's bank takes the location of a snapped one it landed on.
            else if (stays && !lastKept) { result[result.Count - 1] = moved[i]; lastKept = true; }
        }
        result.Add(path[path.Count - 1]);
        // Snapping is cosmetic. Its new connecting legs have not been checked
        // by the route search, so keep the original path when the merge cuts
        // a protected location. Before trimming, the route's own locations'
        // footprints (locations) are crossed only as its departure and arrival,
        // just as during the search (RoadLocationProtection.BlocksRoute).
        // Nor where it enters a zone the network goes round (NetworkCutoff.AvoidsSegment, the search's own rule).
        float clearance = RoadLocationProtection.Clearance(width ?? RoadWidth);
        for (int i = 1; i < result.Count; i++)
            if (NetworkGround.BlocksRoute(result[i - 1], result[i], clearance, locations, path[0], path[path.Count - 1]))
                return path;
        return result;
    }

    private static int m_corridorSnapped;
    /// <summary>Roads ended on a location's approach's road instead of beside it (<see cref="ReEndOnAWayIn"/>), and those still refused so.</summary>
    private static int m_reEndedOnApproach, m_reEndRefused;
    /// <summary>The last resort (RoadPitches.AsLastResort): destinations tried once more (RouteIsland) and those reached so;
    /// Mistlands viaducts' ends planned once more (PlanCandidate) and those planned so.</summary>
    private static int m_lastResortDestinations, m_lastResortReached, m_viaductEndsTriedAgain, m_viaductEndsPlanned;
    private static int m_junctionFills;
    private static int m_chainsShared;

    /// <summary>
    /// One refusal of a road as planned (<see cref="RoadRefusal"/>): the reason it is counted and logged by
    /// (<see cref="Reason"/>, RoadSpatialGrid.LastRefusal's), what an operator is told (<see cref="Said"/>: the reason with
    /// what it is refused for), where (<see cref="At"/>), the points generation records it at (<see cref="Marks"/>, for
    /// RefusedAtABandedEnd), and the roads log's tag it is said under (<see cref="Tag"/>: a staircase's, the keep-clear's, or
    /// none for a debug line).
    /// </summary>
    internal sealed class Refusal
    {
        public readonly string Reason, Said;
        public readonly Vector2 At;
        public readonly Vector2[] Marks;
        public string? Tag;
        /// <summary>The zones the network goes round that the road writes into (<see cref="ProtectedZoneRefusal"/>, read by
        /// <see cref="WritesProtected"/>); none for every other refusal. What an operator is told to mark or force.</summary>
        public IReadOnlyList<Vector2s> Zones = Array.Empty<Vector2s>();
        /// <summary>A way onto a location arriving as another way into it does (<see cref="BesideItsApproachRefusal"/>): the
        /// location, and where that way's road is, which generation searches the road again to end on (<see cref="ReEndOnAWayIn"/>).</summary>
        public (Vector2 location, Vector2 onItsRoad)? ReEnd;
        public Refusal(string reason, string said, Vector2 at, params Vector2[] marks)
        {
            (Reason, Said, At) = (reason, said, at);
            Marks = marks.Length > 0 ? marks : new[] { at };
        }
        public Refusal(string reason, Vector2 at) : this(reason, reason, at) { }
    }

    /// <summary>Why a road with shallow water no ford style may cross is refused (<see cref="InvalidCrossings"/>).</summary>
    internal const string ShallowWaterRefusal = "shallow water too long or deep to cross";
    /// <summary>Why a road with a bridge deck through the ground between its banks is refused (<see cref="InvalidCrossings"/>).</summary>
    internal const string DeckThroughGroundRefusal = "a bridge deck through the ground between its banks";
    /// <summary>Why a road held to a road already laid on more than <see cref="JunctionFillLimit"/> of fill is refused (<see cref="HeldRidges"/>).</summary>
    internal static string JunctionRidgeRefusal => $"a junction ridge: held to a road on more than {JunctionFillLimit:F0} m of fill";
    /// <summary>Why a road along a ridge is refused (<see cref="AlongARidge"/>).</summary>
    internal const string RidgeRoadRefusal = "along a ridge";
    /// <summary>Why a road in a trench is refused (<see cref="AlongARidge"/>).</summary>
    internal const string TrenchRoadRefusal = "in a trench";

    /// <summary>
    /// A road along a ridge or in a trench (<see cref="Ridges"/>, read on the natural ground as generation's ridge re-route
    /// reads it, <see cref="NaturalGround"/>): the ground lower (a ridge) or higher (a trench) on both sides by more than
    /// RoadRidges.Depth over RoadRidges.MinLength or more of it (in a biome with its own values, RoadRules.For(...).Ridge, a
    /// stretch not long by more than its override's depth). Said with how deep and how long, and where it starts, its
    /// reason (<see cref="RidgeRoadRefusal"/> or <see cref="TrenchRoadRefusal"/>) in <paramref name="reason"/>; null where
    /// none. A shorter stretch is a blip RoadRidges does not find, and is built. Judged on every road, with an earthwork cap
    /// or without: the re-route round it (<see cref="OffRidges"/>) runs only with one, the verdict always.
    /// </summary>
    internal static string? AlongARidge(List<RoadSpatialGrid.PlannedPath> plans, IReadOnlyList<RoadCrossing> crossings, out Vector2 at, out string reason)
    {
        at = default;
        reason = RidgeRoadRefusal;
        foreach (RoadRidges.Stretch stretch in Ridges(plans, crossings))
        {
            at = stretch.Points[0];
            bool ridge = stretch.Kind == RoadRidges.Kind.Ridge;
            reason = ridge ? RidgeRoadRefusal : TrenchRoadRefusal;
            return FormattableString.Invariant($"{reason}: {stretch.Worst:F1} m {(ridge ? "over" : "under")} the ground on both sides for {stretch.Length:F0} m");
        }
        return null;
    }

    /// <summary>Why a road with a step no one walks up between two of its stretches is refused (<see cref="WalkStep"/>).</summary>
    internal const string WalkStepRefusal = "a step between two of its stretches no one walks up";
    /// <summary>How far apart two points of a road's stretches are read for a step between them (<see cref="WalkStep"/>), metres:
    /// a planned point's spacing and a half (planned points stand about a metre apart), so only points side by side are
    /// compared, never two a stretch of road apart. Two points of one piece no further apart than this along it are a step
    /// of that piece, not two stretches side by side.</summary>
    internal const float WalkStepReach = 1.5f;

    /// <summary>
    /// A step a player cannot walk up between two points of the road's stretches (its pieces, its ends carried into their
    /// locations and its ways onto their landings; a ford painted on its bed is ground, not judged) standing within
    /// <see cref="WalkStepReach"/> of each other: two pieces of road that do not meet (<see cref="PiecesMeet"/>, the one
    /// verdict: within the grade cap over the distance between them, or within a step, never the two summed). Two stretches
    /// side by side (a sharp corner where the road's end meets its way onto a landing, climbing out of it on one and down on
    /// the other, two legs at a fold) can stand a step apart. Two points of one piece are two stretches only where the piece
    /// does not join them within <see cref="WalkStepReach"/> along it (a fold); nearer along it they are steps of that piece,
    /// whose grade has one judge on the schedule it was limited on (RoadGrade.Limit, RoadSpatialGrid.WorstOverLimit: each
    /// edge at RoadGrade.EdgeLimit of <see cref="RoadSpatialGrid.PlannedPath.Grades"/>, a last-resort pitch's edges at their
    /// own grade), never judged here again. Said with the step and where; null where none.
    /// </summary>
    internal static string? WalkStep(List<RoadSpatialGrid.PlannedPath> plans, out Vector2 at)
    {
        at = default;
        var points = new List<(Vector2 p, float h, int piece, float along)>();
        for (int k = 0; k < plans.Count; k++)
        {
            RoadSpatialGrid.PlannedPath plan = plans[k];
            if (plan.FollowTerrain) continue;
            int n = Mathf.Min(plan.Points.Count, plan.Heights.Count);
            float along = 0f;
            for (int i = 0; i < n; i++)
            {
                if (i > 0) along += Vector2.Distance(plan.Points[i - 1], plan.Points[i]);
                points.Add((plan.Points[i], plan.Heights[i], k, along));
            }
        }
        var bins = new Dictionary<(int, int), List<int>>();
        (int, int) Bin(Vector2 p) => (Mathf.FloorToInt(p.x / WalkStepReach), Mathf.FloorToInt(p.y / WalkStepReach));
        for (int i = 0; i < points.Count; i++)
        {
            var (bx, bz) = Bin(points[i].p);
            for (int x = -1; x <= 1; x++) for (int z = -1; z <= 1; z++)
            {
                if (!bins.TryGetValue((bx + x, bz + z), out var list)) continue;
                foreach (int j in list)
                {
                    float d = Vector2.Distance(points[i].p, points[j].p);
                    if (d > WalkStepReach) continue;
                    // Steps of one piece: its grade judge's.
                    if (points[i].piece == points[j].piece && points[i].along - points[j].along <= WalkStepReach) continue;
                    float rise = Mathf.Abs(points[i].h - points[j].h);
                    if (PiecesMeet(rise, d)) continue;
                    at = points[i].p;
                    return FormattableString.Invariant($"{WalkStepRefusal}: {rise:F2} m over {d:F1} m");
                }
            }
            if (!bins.TryGetValue(Bin(points[i].p), out var own)) bins[Bin(points[i].p)] = own = new List<int>();
            own.Add(i);
        }
        return null;
    }

    /// <summary>Why a road with a crossing the built road does not allow is refused (<see cref="BuiltRefusal"/>), of every kind.</summary>
    internal const string BuiltRoadRefusalReason = "a crossing the built road does not allow";

    /// <summary>Why a road with a bridge with no road beyond one of its banks is refused (<see cref="DanglingBridge"/>).</summary>
    internal const string DanglingRefusal = "a bridge with no road at one end";

    /// <summary>
    /// Why a planned road may not be built as it stands, and where; null when it may. The one list of the refusals of a road
    /// as planned (decisions 6a and 6e: one hard acceptance), for every path that judges one before anything of it is stored:
    /// generation's final check (GenerateRoadCore, a road_connect's plan with it), an approach trial (<see cref="PlanFromPath"/>),
    /// a marked manual road (ManualRoads.PrepareCore) and a staircase's end floor (RoadStairRuns.EndFloors). Each is asked of
    /// its own owner, in this order: earthwork past the terrain limit, over the road and its ends carried on into their
    /// locations (<see cref="PastTerrainLimit(List{RoadSpatialGrid.PlannedPath}, float, List{Vector2})"/>); a crossing no style
    /// may take or a deck through the ground (<see cref="InvalidCrossings"/>); a junction ridge (<see cref="HeldRidges"/>);
    /// a road along a ridge or in a trench (<see cref="AlongARidge"/>, over the road, its ends carried on into their locations and its ways onto their landings); a step no one walks up between two of its stretches (<see cref="WalkStep"/>, over the same); running under the deck of a viaduct in the network (<see cref="UnderALaidDeck"/>); crossing or running beside another
    /// road or on a staircase's flight (<see cref="MeetingRefusal(List{RoadSpatialGrid.PlannedPath}, out Vector2, out RoadStairRuns.FlightLanding?, bool, RoadStairRuns.FlightLanding?)"/>);
    /// the ground under a structure another road planned changed by what it writes (<see cref="UnderAPlannedStructure"/>, over
    /// <see cref="Written"/>); a staircase its road does not meet where it ends (RoadStairRuns.ConnectionRefusal); a crossing
    /// of any kind with road under its deck, or a valley viaduct's pier under the floor (<see cref="BuiltRefusal"/>); a crossing's structure in
    /// another location (<see cref="InAnotherLocation"/>) or built in no kit (<see cref="WithNoKit"/>); a bridge with no road beyond
    /// it (<see cref="DanglingBridge"/>); a staircase its kit would refuse (<see cref="StairsRefusal"/>); and, where the
    /// network goes round any zone, a footprint that writes into one (<see cref="WritesProtected"/>).
    /// What a path knows of the road beyond its plans, crossings and staircases is in <paramref name="road"/> and is only ever
    /// more context, never a check left out: its search path's ends (<see cref="PlannedRoad.Path"/>, where a bank may be the
    /// location itself), its ways onto placed locations' landings, its ends carried into their locations and its approach staircases
    /// (generation's location ends), and the landing it was laid onto a flight with (<see cref="PlannedRoad.Landing"/>: that
    /// flight is met by it, and its staircase is not another road's to keep clear of). <paramref name="written"/>: what the
    /// road writes, as the checks read it (null where refused before it is read), the one footprint its commit stores.
    /// Generation searches again round most of these before it asks; a manual road, which has no other route, is refused
    /// with the reason.
    /// </summary>
    internal static Refusal? RoadRefusal(PlannedRoad road, out WrittenRoad? written)
    {
#if ROADS_TESTS
        written = null;
        if (RefusedFirst?.Invoke(road) is Refusal first) return first;
#endif
        written = null;
        List<RoadSpatialGrid.PlannedPath> plans = road.Plans;
        List<RoadCrossing> crossings = road.Crossings;
        List<RoadStairSpan> stairs = road.Stairs;
        Vector2 at;
        var where = new List<Vector2>();
        // Its ends at its locations first, in the order generation plans them, the meeting with the network among them.
        if (LocationEndRefusal(road) is Refusal locationEnd) return locationEnd;
        var finished = road.Carried.Count == 0 ? plans : plans.Concat(road.Carried).ToList();
        if (PastTerrainLimit(finished, EarthworkLimit, where) > 0) return new Refusal(EarthworkRefusal, where[0]);
        if (InvalidCrossings(crossings, where) > 0)
            return new Refusal(crossings.Exists(c => c.Invalid && c.Shallow) ? ShallowWaterRefusal : DeckThroughGroundRefusal,
                where.Count > 0 ? where[where.Count - 1] : default);
        if (HeldRidges(plans, where) > 0) return new Refusal(JunctionRidgeRefusal, where[where.Count - 1]);
        // A ridge or trench over all the road it stores: its pieces, its ends carried into their locations and its ways onto
        // their landings (each its own leg, joined to the road where it starts at its end).
        var walked = finished;
        foreach (var entry in road.Entries) if (entry.road != null) { if (ReferenceEquals(walked, finished)) walked = new List<RoadSpatialGrid.PlannedPath>(finished); walked.Add(entry.road); }
        if (AlongARidge(walked, road.RidgeCrossings ?? crossings, out at, out string alongWhy) is string ridge) return new Refusal(alongWhy, ridge, at);
        // A last-resort pitch (RoadPitches.AsLastResort) within its island's share of the world's pitch budget (off an island
        // worker, the world's) and spaced from every other: over everything it stores, as the ridge is read; its pitches kept
        // for its commit to record as judged (ManualAppend.Pitches).
        if (RoadPitches.PitchRefusal(walked, RoadGrade.Configured, out at, out List<RoadPitches.Pitch> pitches) is string pitch) return new Refusal(pitch, at);
        // A step no one walks up between two of its stretches side by side (a sharp corner where its end meets its way onto
        // a landing, two legs at a fold): over everything it stores, as the ridge is read.
        if (WalkStep(walked, out at) is string step) return new Refusal(WalkStepRefusal, step, at);
        if (UnderALaidDeck(plans, out at) is string underDeck) return new Refusal(underDeck, at);
        // Met again as finished, but where its location ends' meeting was asked of these very plans (no landing nor approach
        // changed them since): the same verdict.
        if ((!road.Ends.MeetingAsked || road.Ends.ChangedSinceMeeting)
            && MeetingRefusal(plans, out at, out _, landOnFlight: false, laidWith: road.Landing) is string meeting) return new Refusal(meeting, at);
        foreach (RoadSpatialGrid.PlannedPath edge in road.Carried)
            if (MeetingRefusal(new List<RoadSpatialGrid.PlannedPath> { edge }, out at) is string carriedMeeting)
                return new Refusal(carriedMeeting, at);
        // Nothing it writes changes the ground under a structure another road planned (the one keep-clear), of the road as it
        // would be stored, the staircase it lands on its own.
        written = Written(plans, crossings, stairs, road.Approaches, road.Entries, road.Carried, road.Path.Count > 1 ? road.Path : null, road.Landing);
        written.Footprint.Pitches = pitches;
        List<Vector2> roadEnds = road.Path.Count > 1 ? road.Path : plans.SelectMany(p => p.Points).ToList();
        string? named = roadEnds.Count > 1 ? FormattableString.Invariant(
            $"({roadEnds[0].x:F1}, {roadEnds[0].y:F1}) -> ({roadEnds[roadEnds.Count - 1].x:F1}, {roadEnds[roadEnds.Count - 1].y:F1})") : null;
        if (UnderAPlannedStructure(written.Footprint, crossings, stairs, out at, out string keptClear, road.Landing?.Run, named) is string underStructure)
            return new Refusal(keptClear, underStructure, at) { Tag = "[KEEPCLEAR]" };
        // Its staircases met by the road where they end: the one finished-connection verdict, every end of every staircase it
        // lays read by the one end reader on the finished ground as the road will stand once stored (AsWritten, StairEnds.GroundOf:
        // the ground the staircase-end audit reads them on once they are).
        var laid = stairs.FindAll(st => !st.Existing);
        var laying = new List<RoadStairSpan>(laid);
        laying.AddRange(road.Approaches);
        foreach (var entry in road.Entries) if (entry.stairs != null) laying.Add(entry.stairs);
        string? unconnected;
        using (AsWritten(written.Footprint))
            unconnected = RoadStairRuns.ConnectionRefusal(laying, plans, out at, written.Footprint.Runs.ConvertAll(r => r.run),
                run => StairEnds.GroundOf(run, GenerationWorld));
        if (unconnected != null) return new Refusal(unconnected, at) { Tag = "[STAIRS]" };
        // A crossing the built road does not allow refuses it, of every kind alike (a viaduct, a river bridge, a span): none
        // is built with road under its deck. Of the crossings it stores of its own (WrittenRoad.Stored: none a road laid before
        // it carries, which it never builds), each structure read on the ground as the road would be stored (its Written): the
        // crossing bans' ground.
        if (BuiltRefusal(plans, written.Stored, out at, out string built, written.Footprint) is RoadCrossing unmet)
            return new Refusal(BuiltRoadRefusalReason + " (" + built + ")", built, at, unmet.FromBank, unmet.ToBank);
        if (InAnotherLocation(plans, crossings, out at, out string inLocation, written.Footprint) != null) return new Refusal(StructureInAnotherLocation, inLocation, at);
        if (WithNoKit(crossings, out at, out string noKit, written.Footprint) != null) return new Refusal(DeckWithNoKit, noKit, at);
        // A bank at the road's own start or end is the location itself: its search path's ends where known, else its plans'.
        List<Vector2> ends = road.Path.Count > 1 ? road.Path : plans.SelectMany(p => p.Points).ToList();
        if (ends.Count > 1 && DanglingBridge(plans, crossings, ends[0], ends[ends.Count - 1], out RoadCrossing dangling))
            return new Refusal(DanglingRefusal, DanglingRefusal, dangling.Center, dangling.FromBank, dangling.ToBank);
        // Each staircase it lays, as what it writes lays them, read by its kit's support response on the ground as the road
        // would be stored (decision 6p).
        if (StairsRefusal(written.Footprint, out at) is string unheld) return new Refusal(unheld, at) { Tag = "[STAIRS]" };
        // The one final check of its whole footprint against the zones the network goes round, where it goes round any.
        if (NetworkCutoff.AvoidsAny && WritesProtected(plans, written, out List<Vector2s> into) is Vector2 protectedAt)
            return new Refusal(ProtectedZoneRefusal, protectedAt) { Zones = into };
        return null;
    }

    /// <summary>
    /// <see cref="RoadRefusal"/>'s reason, said with what it is refused for, of a road known only by its plans, crossings and
    /// <paramref name="stairs"/> (a test's, a staircase's end floor): the one list with no more context; its ridges judged from
    /// <paramref name="ridgeCrossings"/> where given (the road's own crossings, a piece judged apart from them: PlannedRoad.RidgeCrossings).
    /// </summary>
    internal static string? BuiltRoadRefusal(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings, out Vector2 at,
        IReadOnlyList<RoadStairSpan>? stairs = null, IReadOnlyList<RoadCrossing>? ridgeCrossings = null)
    {
        Refusal? refused = RoadRefusal(new PlannedRoad { Plans = plans, Crossings = crossings, Stairs = stairs?.ToList() ?? new(), RidgeCrossings = ridgeCrossings }, out _);
        at = refused?.At ?? default;
        return refused?.Said;
    }

    /// <summary>
    /// What generation searches a road again round, where it can, and where: a stretch held to a road already laid more than
    /// <see cref="HoldPullLimit"/> off its own profile (HeldOffProfile, the earthwork re-route), then a trench or ridge
    /// (RoadRidges, read on the ground as <see cref="OffRidges"/> reads it); asked only as GenerateRoadCore asks them, with an
    /// earthwork cap and its retries. Null where none. Generation builds a road anyway where no way round clears these, but for
    /// a trench or ridge, which the one list refuses (<see cref="AlongARidge"/>); a road
    /// with no other way to search (a staircase's end carried on to its first stair, tried once) is refused for them instead:
    /// the consequence of its one try, not another rule. Its ridges judged from <paramref name="crossings"/> (the road's own
    /// viaducts among them, <see cref="Ridges"/>), none where not given.
    /// </summary>
    internal static string? SearchedRound(List<RoadSpatialGrid.PlannedPath> plans, out Vector2 at, IReadOnlyList<RoadCrossing>? crossings = null)
    {
        at = default;
        if (!EarthworkCapped) return null;
        var where = new List<Vector2>();
        if (EarthworkRetries > 0 && HeldOffProfile(plans, where) > 0)
        { at = where[0]; return $"held to a road more than {HoldPullLimit:F0} m off its profile"; }
        if (RidgeRetries > 0 && Ridges(plans, crossings ?? Array.Empty<RoadCrossing>()) is { Count: > 0 } ridges)
        {
            at = ridges[0].Points[0];
            return FormattableString.Invariant($"a {(ridges[0].Kind == RoadRidges.Kind.Ridge ? "ridge" : "trench")}: {ridges[0].Worst:F1} m for {ridges[0].Length:F0} m");
        }
        return null;
    }

    /// <summary>What a road stores of its planned pieces: none of what rides the network (RoadRide.Own), with its own decks
    /// (a spanned ford's, a bridge's whose ends are not met) kept clear.</summary>
    internal static List<RoadSpatialGrid.PlannedPath> OwnPieces(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings,
        out float ridden, out List<RoadRide.Segment> decks)
    {
        decks = RoadRide.DeckLines(plans, crossings.FindAll(c => (c.Kind == CrossingKind.Ford && c.Style == FordStyle.Span) || (c.Kind == CrossingKind.Bridge && !MeetsTheRoad(c))));
        return RoadRide.Own(plans, out ridden, decks);
    }

    /// <summary>The search a road is planned with: generation's finders and a manual road's alike.</summary>
    internal static RoadPathfinder NewFinder(WorldGenerator world, float locationClearance)
    {
        var finder = new RoadPathfinder(world) { LocationClearance = locationClearance };
        ConfigureFinder?.Invoke(finder);
        return finder;
    }

    /// <summary>Tests' settings for every search this thread makes (NewFinder), generated and manual alike.</summary>
    [ThreadStatic] private static Action<RoadPathfinder>? ConfigureFinder;
    internal static void ConfigureFinders(Action<RoadPathfinder>? configure) => ConfigureFinder = configure;

    /// <summary>The terrain limit a planned road is held to, metres of cut or
    /// fill (vanilla TerrainComp clamps level deltas to +/-8 m). A road still
    /// past it after the re-route retries is refused. Settable for tests; 0 is
    /// off. The one owner of the earthwork cap (docs/OWNERS.md).</summary>
    internal static float EarthworkCap = DefaultEarthworkCap;

    /// <summary>The cap a game runs with: the level-delta clamp the game itself applies (<see cref="RoadConstants.TerrainDeltaMax"/>).</summary>
    internal const float DefaultEarthworkCap = RoadConstants.TerrainDeltaMax;

    // ---- The earthwork cap as each kind of rule reads it: the one place its unset case (EarthworkCap 0) is decided ----
    // Three readings, each meant, none a copy of another:
    //  - EarthworkCapped: the searches that exist only to keep a road within the cap (the re-routes round deep earthwork,
    //    held stretches and ridges; the humps laid as road) run only with one.
    //  - EarthworkLimit: what a road, a landing or a structure's end is judged by; without a cap there is no limit. A test
    //    skipped and a limit no height passes are the same reading.
    //  - Shaped: how far ground is cut or filled to a height (a start pad, an ease's reach, ground no road is cut through):
    //    within the limit, and never past what the terrain itself moves (RoadConstants.TerrainDeltaMax, where the game
    //    clamps a level delta). Without a cap, the terrain's own.

    /// <summary>Whether a cap is set (<see cref="EarthworkCap"/>): the searches that keep a road within it run only then.</summary>
    internal static bool EarthworkCapped => EarthworkCap > 0f;

    /// <summary>The limit a road, a landing or an end is judged by: <see cref="EarthworkCap"/>, or none (infinity) without one. The one owner of the earthwork cap (docs/OWNERS.md).</summary>
    internal static float EarthworkLimit => EarthworkCap > 0f ? EarthworkCap : float.PositiveInfinity;

    /// <summary>How far ground is shaped within <paramref name="limit"/>: never past what the terrain moves
    /// (<see cref="RoadConstants.TerrainDeltaMax"/>).</summary>
    internal static float Shaped(float limit) => Mathf.Min(limit, RoadConstants.TerrainDeltaMax);

    /// <summary><see cref="Shaped(float)"/> of <see cref="EarthworkLimit"/>: the cap, or without one the terrain's own limit. The one owner of the earthwork cap (docs/OWNERS.md).</summary>
    internal static float ShapedLimit => Shaped(EarthworkLimit);

    /// <summary>Crossings that may not be built, counted into the earthwork re-route so the search goes round them:
    /// shallow water no ford style may cross (too long or deep), and a bridge whose deck would meet ground between its
    /// banks (RoadCrossingDetector.ClearOfGround). Each adds its banks and the line between to
    /// <paramref name="where"/> (the cells priced up).</summary>
    internal static int InvalidCrossings(List<RoadCrossing> crossings, List<Vector2>? where)
    {
        int n = 0;
        foreach (var c in crossings)
        {
            if (!c.Invalid) continue;
            n++;
            if (where == null) continue;
            float len = Vector2.Distance(c.FromBank, c.ToBank);
            int k = Mathf.Max(1, Mathf.CeilToInt(len / 16f));
            for (int i = 0; i <= k; i++) where.Add(Vector2.Lerp(c.FromBank, c.ToBank, (float)i / k));
        }
        return n;
    }
    internal static int EarthworkRetries = 2;
    /// <summary>Searches round a road's trenches and ridges (<see cref="RoadRidges"/>) per road; 0 is none. Settable for tests.</summary>
    internal static int RidgeRetries = 2;
    private static int m_ridgeReroutes;
    /// <summary>Roads re-routed off a trench or ridge this generation.</summary>
    internal static int RidgeReroutes => m_ridgeReroutes;

    private static float NaturalGround(Vector2 p) => BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, GenerationWorld);

    /// <summary>A road's trenches and ridges as generation judges them, on every path that asks (the one list's
    /// <see cref="AlongARidge"/>, the re-route round them, the searched-round verdict, the hump check): RoadRidges.Find of its
    /// planned pieces on the natural ground, a stretch in a biome with its own ridge values judged by them
    /// (RoadRules.For(...).Ridge: the Mistlands', the owner's decision of 13 Nov 2026), where its viaduct reach lets it
    /// (RoadRidges.Judged): from the bank ends of the road's own viaducts among <paramref name="crossings"/> (one it rides
    /// included) and of the viaducts laid in the network this island sees (<see cref="KeepClearOfViaducts"/>). A viaduct
    /// another road lays later is not known yet.</summary>
    internal static List<RoadRidges.Stretch> Ridges(List<RoadSpatialGrid.PlannedPath> plans, IReadOnlyList<RoadCrossing> crossings) =>
        RoadRidges.Find(plans, NaturalGround, BiomeAt, ViaductEnds(crossings));

    /// <summary>The viaduct ends a road's stretches are measured from (<see cref="Ridges"/>): its own viaducts' and the laid ones'.</summary>
    private static RoadRidges.Viaducts ViaductEnds(IReadOnlyList<RoadCrossing> crossings)
    {
        var own = new List<Vector2>();
        foreach (RoadCrossing c in crossings)
            if (IsViaduct(c)) { var (near, far) = BridgeStructures.Ends(c, GenerationWorld); own.Add(near.Edge); own.Add(far.Edge); }
        var laid = new List<Vector2>();
        int me = CrossIslandReads.Me;
        lock (m_laidDecks)
            foreach (var d in m_laidDecks)
                if (d.Viaduct && CrossIslandReads.Sees(me, d.Island)) { laid.Add(d.Near.Edge); laid.Add(d.Far.Edge); }
        return new RoadRidges.Viaducts(own, laid);
    }

    /// <summary>The generation world's biome at a point of a road (None with no world).</summary>
    private static Heightmap.Biome BiomeAt(Vector2 p) => GenerationWorld is { } world ? RoadBiome.At(world, p) : Heightmap.Biome.None;

    /// <summary>
    /// A road within the earthwork cap laid in a trench or along a ridge (<see cref="RoadRidges"/>) is searched again round
    /// it, up to <see cref="RidgeRetries"/> times, the cells round each such stretch priced up as the earthwork re-route
    /// prices them. A re-route is kept only where it lays a third fewer metres of trench and ridge, no more stretches held off
    /// its profile, and passes the safety the earthwork re-route is held to (<see cref="SafetyFaults"/>: nothing past the cap,
    /// no held ridge, no invalid crossing, no write into a zone the network goes round nor under a structure another road
    /// planned) and the network takes it as it meets it (<see cref="KeepsOffRidges"/>). Otherwise the road keeps its plan: a
    /// trench or ridge left refuses the road (<see cref="AlongARidge"/>, in the one list) and the planner tries its
    /// other candidates.
    /// </summary>
    private static (List<Vector2> path, List<RoadCrossing> crossings, List<RoadSpatialGrid.PlannedPath> plans, List<RoadStairSpan> stairs, bool startAtLocation, bool endAtLocation, RoadPlan plan) OffRidges(
        RoadPathfinder finder, (List<Vector2> path, List<RoadCrossing> crossings, List<RoadSpatialGrid.PlannedPath> plans, List<RoadStairSpan> stairs, bool startAtLocation, bool endAtLocation, RoadPlan plan) road,
        int pulled, Vector2 pathStart, Vector2 pathEnd, Vector2 startCenter, float startRadius, Vector2 endCenter, float endRadius, float width,
        Vector2? startAim, Vector2? endAim, bool startReached, bool endReached, Vector2? joinedThrough)
    {
        var stretches = Ridges(road.plans, road.crossings);
        float metres = RoadRidges.Metres(stretches);
        for (int attempt = 0; attempt < RidgeRetries && stretches.Count > 0; attempt++)
        {
            foreach (var stretch in stretches) PenalizeStretch(finder, stretch);
            var retry = Replan(finder, pathStart, pathEnd, startCenter, startRadius, endCenter, endRadius, width, startAim, endAim, startReached, endReached, joinedThrough);
            if (retry == null) { SayRidgeReRoute(stretches, attempt, "did not route or build (" + (RoadSpatialGrid.LastRefusal ?? "no route") + ")"); continue; }
            var after = Ridges(retry.Value.plans, retry.Value.crossings);
            if (!KeepsOffRidges(retry.Value.plans, retry.Value.crossings, retry.Value.stairs, metres, after, pulled))
            {
                SayRidgeReRoute(stretches, attempt, FormattableString.Invariant($"not taken: {metres:F0} -> {RoadRidges.Metres(after):F0} m of trench and ridge{(RoadRidges.Metres(after) * 3f > metres * 2f ? "" : ", its safety or meeting refused")}"));
                stretches = after; continue;
            }
            System.Threading.Interlocked.Increment(ref m_ridgeReroutes);
            road = retry.Value;
            stretches = after;
            metres = RoadRidges.Metres(after);
        }
        // What no way round clears goes up or down on stairs: the stretches left are opened to stairs, their road priced
        // up as before and their staircases not, and searched once more.
        if (stretches.Count > 0 && finder.Stairs && RidgeRetries > 0)
        {
            foreach (var stretch in stretches) PenalizeStretch(finder, stretch);
            var opened = finder.OpenToStairs(stretches.SelectMany(st => st.Points), RidgeStairReachCells);
            var retry = Replan(finder, pathStart, pathEnd, startCenter, startRadius, endCenter, endRadius, width, startAim, endAim, startReached, endReached, joinedThrough);
            finder.ShutToStairs(opened);
            if (retry != null && KeepsOffRidges(retry.Value.plans, retry.Value.crossings, retry.Value.stairs, metres, Ridges(retry.Value.plans, retry.Value.crossings), pulled))
            {
                System.Threading.Interlocked.Increment(ref m_ridgeStairs);
                road = retry.Value;
            }
            else SayRidgeReRoute(stretches, RidgeRetries, retry == null ? "onto stairs: did not route or build" : "onto stairs: not taken");
        }
        finder.ClearEarthworkPenalty();
        return road;
    }

    /// <summary>One roads-log line for a ridge re-route attempt not taken, at the first stretch it searched round.</summary>
    private static void SayRidgeReRoute(List<RoadRidges.Stretch> stretches, int attempt, string why)
    {
        if (!RoadsLog || stretches.Count == 0) return;
        RoadRidges.Stretch first = stretches[0];
        Log.LogDebug(FormattableString.Invariant(
            $"Ridge re-route {attempt + 1} round a {(first.Kind == RoadRidges.Kind.Ridge ? "ridge" : "trench")} of {first.Length:F1} m at ({first.Points[0].x:F1}, {first.Points[0].y:F1}): {why}"));
    }

    /// <summary>Cells round a trench or ridge opened to stairs where no re-route clears it.</summary>
    internal const int RidgeStairReachCells = 1;
    private static int m_ridgeStairs;

    /// <summary>The cells round a trench or ridge priced up, a point every <see cref="EarthworkPenaltyRadius"/> along it.</summary>
    private static void PenalizeStretch(RoadPathfinder finder, RoadRidges.Stretch stretch)
    {
        Vector2? last = null;
        foreach (var p in stretch.Points)
        {
            if (last is Vector2 l && Vector2.Distance(l, p) < EarthworkPenaltyRadius * 0.5f && p != stretch.Points[stretch.Points.Count - 1]) continue;
            finder.PenalizeAround(p, EarthworkPenaltyRadius);
            last = p;
        }
    }

    /// <summary>Whether a re-planned road replaces one with <paramref name="before"/> metres of trench and ridge and
    /// <paramref name="pulled"/> stretches held off its profile: its own objective, a third fewer metres (<paramref name="after"/>)
    /// and no more held stretches, and the safety both re-route acceptances share (<see cref="SafetyFaults"/>: none, and the
    /// network takes it as it meets it, <see cref="MeetingRefusal(List{RoadSpatialGrid.PlannedPath}, out Vector2)"/>).</summary>
    private static bool KeepsOffRidges(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings, List<RoadStairSpan> stairs,
        float before, List<RoadRidges.Stretch> after, int pulled)
    {
        if (RoadRidges.Metres(after) * 3f > before * 2f) return false;
        var none = new List<Vector2>();
        return SafetyFaults(plans, crossings, stairs, none).Total == 0 && HeldOffProfile(plans, none) <= pulled && MeetingRefusal(plans, out _) == null;
    }

    /// <summary>A planned road's faults by kind (<see cref="SafetyFaults"/>).</summary>
    internal readonly struct ReRouteFaults
    {
        /// <summary>Points past the earthwork cap, crossings that may not be built, and junction ridges (with a cap only).</summary>
        public readonly int Earthwork;
        /// <summary>Points whose writer's reach touches a zone the network goes round (<see cref="IntoProtected"/>).</summary>
        public readonly int IntoAZone;
        /// <summary>Points whose write would change the ground under a structure another road planned (<see cref="NearAPlannedStructure"/>).</summary>
        public readonly int NearAStructure;
        public int Total => Earthwork + IntoAZone + NearAStructure;
        public ReRouteFaults(int earthwork, int intoAZone, int nearAStructure) => (Earthwork, IntoAZone, NearAStructure) = (earthwork, intoAZone, nearAStructure);
    }

    /// <summary>
    /// The one safety check of a re-route (X2): what generation searches a road again round and what a re-route of it may
    /// not bring, asked by both acceptances alike, the earthwork re-route (GenerateRoadCore: fewer faults than the plan it
    /// replaces) and the ridge re-route (<see cref="KeepsOffRidges"/>: none). Earthwork past the cap, a crossing that may not
    /// be built and a junction ridge (<see cref="PastTerrainLimit(List{RoadSpatialGrid.PlannedPath}, float, List{Vector2})"/>,
    /// <see cref="InvalidCrossings"/>, <see cref="HeldRidges"/>, with a cap only), a writer's reach into a zone the network goes
    /// round (<see cref="IntoProtected"/>) and a write under a structure another road planned (<see cref="NearAPlannedStructure"/>);
    /// each offending point goes in <paramref name="where"/>, the cells priced up. Each acceptance keeps its own objective.
    /// </summary>
    private static ReRouteFaults SafetyFaults(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings, List<RoadStairSpan> stairs,
        List<Vector2> where) =>
        new(EarthworkCapped ? PastTerrainLimit(plans, EarthworkLimit, where) + InvalidCrossings(crossings, where) + HeldRidges(plans, where) : 0,
            IntoProtected(plans, where), NearAPlannedStructure(plans, crossings, stairs, where));
    /// <summary>Cells within this of an offending point are priced up.</summary>
    internal const float EarthworkPenaltyRadius = 12f;
    private static int m_earthworkReroutes, m_earthworkUnavoided;
    /// <summary>Roads refused as finished past the earthwork cap (GenerateRoadCore's last check, after its landings and
    /// carried ends), within it when first judged.</summary>
    private static int m_earthworkFinished;
    /// <summary>Where the squeeze between a location and the water refused the
    /// search: one Info line, then one Debug line per location, named from the
    /// location list, busiest first.</summary>
    private static void LogSqueeze()
    {
        var hits = RoadPathfinder.SqueezeHits.ToArray();
        int steps = 0, landings = 0;
        foreach (var kv in hits) { steps += kv.Value.steps; landings += kv.Value.landings; }
        Log.LogInfo($"  Squeeze between a location and the water: refused {steps} search step(s) and {landings} crossing landing(s) beside {hits.Length} location(s)");
        int fills = 0;
        foreach (var kv in RoadPathfinder.ShoreFillCells) fills += kv.Value;
        Log.LogInfo($"  Shallows built out beside a location: the search went through {fills} cell(s) beside {RoadPathfinder.ShoreFillCells.Count} location(s)");
        var locations = ZoneSystem.instance != null ? ZoneSystem.instance.GetLocationList() : null;
        foreach (var kv in System.Linq.Enumerable.OrderByDescending(hits, h => h.Value.steps + h.Value.landings))
        {
            var c = new Vector2(kv.Key.x, kv.Key.z);
            string? name = "?";
            if (locations != null)
                foreach (var loc in locations)
                    if (Vector2.Distance(new Vector2(loc.m_position.x, loc.m_position.z), c) < 1.5f) { name = loc.m_location.m_prefab.Name ?? ""; break; }
            Log.LogDebug($"Squeeze: {name} at ({kv.Key.x},{kv.Key.z}): {kv.Value.steps} step(s), {kv.Value.landings} landing(s) refused");
        }
    }

    /// <summary>Deck ends walked in off land on built roads.</summary>
    private static int m_offLandEnds;

    /// <summary>Why a road past the earthwork cap is refused (<see cref="RoadRefusal"/>). The one owner of where a road may go (docs/OWNERS.md).</summary>
    internal static string EarthworkRefusal => $"earthwork past the {EarthworkCap:F0} m terrain limit";

    /// <summary>Whether a road point at <paramref name="p"/> stored at <paramref name="height"/> is past <paramref name="cap"/>
    /// of earthwork at its centre, over the ground before roads (<see cref="GroundBeforeRoads"/>): a point known before its
    /// road is planned (RoadStairRuns.RoadOnto), whose width is not yet known. A planned road is measured across its width
    /// (<see cref="EarthworkAcross"/>). The one owner of the earthwork cap (docs/OWNERS.md).</summary>
    internal static bool PastTerrainLimit(Vector2 p, float height, float cap, WorldGenerator? world = null) =>
        PastTerrainLimit(height, GroundBeforeRoads(world ?? GenerationWorld)(p), cap);

    /// <summary>Whether ground cut or filled to <paramref name="height"/> from <paramref name="ground"/> is past <paramref name="cap"/>.</summary>
    internal static bool PastTerrainLimit(float height, float ground, float cap) => Mathf.Abs(height - ground) > cap;

    /// <summary>Whether every road leaving <paramref name="bank"/> at the ground it meets there (no lower than the dry-land floor
    /// a land piece keeps) is past the earthwork cap at <paramref name="at"/>, however it is graded: the height nearest the
    /// ground there that a profile reaches at its steepest stored step (the grade cap,
    /// <see cref="RoadGrade.Configured"/>), judged by
    /// <see cref="PastTerrainLimit(float, float, float)"/> on the ground the cap is measured from (<see cref="GroundBeforeRoads"/>)
    /// against <see cref="EarthworkLimit"/>. The planner's measure of the piece it would plan there
    /// (<see cref="PastTerrainLimit(List{RoadSpatialGrid.PlannedPath}, float, List{Vector2})"/>) then refuses it too; the search
    /// asks it of a standing-water crossing's banks (RoadPathfinder.CrossingTerrainOf). Without a cap nothing is past it.</summary>
    internal static bool PastTerrainLimitOffBank(Vector2 bank, Vector2 at, WorldGenerator world)
    {
        if (!EarthworkCapped) return false;
        var ground = GroundBeforeRoads(world);
        float from = Mathf.Max(ground(bank), LandFloor), there = ground(at);
        float reach = RoadGrade.Floored * Vector2.Distance(bank, at);
        return PastTerrainLimit(Mathf.Clamp(there, from - reach, from + reach), there, EarthworkLimit);
    }

    /// <summary>Whether ground cut or filled to <paramref name="height"/> from <paramref name="ground"/> stays within <paramref name="cap"/>.
    /// Not <c>!PastTerrainLimit</c>: an unknown (NaN) height or ground is neither. The one owner of the earthwork cap (docs/OWNERS.md).</summary>
    internal static bool WithinTerrainLimit(float height, float ground, float cap) => Mathf.Abs(height - ground) <= cap;

    /// <summary>Points of the plans (not wades) cut or filled past <paramref name="cap"/> across their width
    /// (<see cref="EarthworkAcross"/>, over the ground before roads: <see cref="GroundBeforeRoads"/>), listed in <paramref name="where"/>.</summary>
    internal static int PastTerrainLimit(List<RoadSpatialGrid.PlannedPath> plans, float cap, List<Vector2> where)
    {
        int past = 0;
        var ground = GroundBeforeRoads(GenerationWorld);
        foreach (var plan in plans)
        {
            if (plan.FollowTerrain) continue;
            past += EarthworkAcross(plan, ground, cap, p =>
            {
                if (where.Count == 0 || Vector2.Distance(where[where.Count - 1], p) > 6f) where.Add(p);
            }).Past;
        }
        return past;
    }

    /// <summary>The ground a road's earthwork is measured from: the ground as the locations placed before roads leave it,
    /// else the natural ground (<see cref="PlacedLocations.GroundAt(float, float, WorldGenerator)"/>). Never a road's own
    /// finished ground, which would hide the earthwork being measured.</summary>
    /// <remarks>At a whole metre (a heightmap vertex, where the earthwork cap reads it: <see cref="EarthworkAcross"/>) it is
    /// read through the planning lattice (RoadStairRuns.GroundAtVertex): the same ground to the bit, read once a thread until
    /// the ground next changes.</remarks>
    internal static Func<Vector2, float> GroundBeforeRoads(WorldGenerator world) => p =>
    {
        int x = (int)p.x, z = (int)p.y;
        return x == p.x && z == p.y ? RoadStairRuns.GroundAtVertex(x, z, world) : PlacedLocations.GroundAt(p.x, p.y, world);
    };

    /// <summary>A planned piece's earthwork as <see cref="EarthworkAcross"/> measures it: its deepest cut or fill
    /// (<see cref="Worst"/>), its most fill (<see cref="Fill"/>, 0 where it fills nothing), and how many of its points have a
    /// sample past the cap they were measured against (<see cref="Past"/>).</summary>
    internal readonly struct Earthwork
    {
        public readonly float Worst, Fill;
        public readonly int Past;
        public Earthwork(float worst, float fill, int past) { Worst = worst; Fill = fill; Past = past; }
    }

    /// <summary>Points measured by <see cref="EarthworkAcross"/>, its calls, the writer's vertices it fitted and the ground
    /// reads it made, since the generation began (the summary's cost line).</summary>
    private static long s_earthworkPoints, s_earthworkReads, s_earthworkCalls, s_earthworkFits, s_earthworkKnown;

    /// <summary>Calls of <see cref="EarthworkAcross"/>, the vertices' ground reads it asked for and those answered from the
    /// 1 m lattice (RoadStairRuns.GroundAtVertex), since the generation began.</summary>
    internal static (long calls, long reads, long known) EarthworkReads =>
        (System.Threading.Interlocked.Read(ref s_earthworkCalls), System.Threading.Interlocked.Read(ref s_earthworkReads), System.Threading.Interlocked.Read(ref s_earthworkKnown));

    /// <summary>Each thread's working sets for <see cref="EarthworkAcross"/>: the plan's points, by cell, and the vertices
    /// fitted, cleared for every call (the hot path allocates nothing).</summary>
    private sealed class EarthworkScratch : GenerationScratch
    {
        public List<RoadSpatialGrid.RoadPoint>? Points;
        /// <summary>The piece's points laid out once for the writer's fit, and the indices of those near a vertex.</summary>
        public RoadTerrainModifier.FitPoints? Fit;
        public int[]? Near;
        public Dictionary<Vector2i, List<int>>? Cells;
        public Stack<List<int>>? Spare;
        public Dictionary<Vector2i, (float written, float ground)>? Vertices;
        internal override void Clear()
        {
            Points = null;
            Fit = null;
            Near = null;
            Cells = null;
            Spare = null;
            Vertices = null;
        }
    }

    [ThreadStatic] private static EarthworkScratch? t_earthwork;

    /// <summary>
    /// The one measurement of a planned piece's earthwork, for every rule that judges it (the earthwork cap on ordinary
    /// roads, <see cref="PastTerrainLimit(List{RoadSpatialGrid.PlannedPath}, float, List{Vector2})"/>, and on the way onto a
    /// location's landing, RoadLocationEntry.PlanTo; the sway's no-more-earth comparison, RoadWiggle.NoMoreEarth): what the terrain
    /// writer will write against <paramref name="ground"/>. The writer writes the game's heightmap vertices (whole metres),
    /// each to the road's surface as it fits it from the piece's points (RoadTerrainModifier.WrittenAt: the line along the
    /// road, past an end its last stretch, blended into the ground past the flat core; before the game's clamp), and the
    /// ground between vertices is the mesh joining them (BuriedPieces.BetweenVertices). At each point the road's surface is
    /// read there: at every vertex within half its own width (<see cref="RoadSpatialGrid.PlannedPath.WidthAt"/>), short of the
    /// piece's ends (the vertices past its first or last point are the ground the writer blends it into, not its surface),
    /// and on the mesh at its centre and at both its edges, half its width to each side across its heading. A point is past
    /// <paramref name="cap"/> where any of them is (<see cref="PastTerrainLimit(float, float, float)"/>); <paramref name="past"/>
    /// is told each such point. Each vertex is fitted and its ground read once a call.
    /// </summary>
    internal static Earthwork EarthworkAcross(RoadSpatialGrid.PlannedPath plan, Func<Vector2, float> ground, float cap, Action<Vector2>? past = null)
    {
        float worst = 0f, fill = 0f;
        int over = 0;
        List<Vector2> points = plan.Points;
        if (t_earthwork == null || t_earthwork.Released) t_earthwork = new EarthworkScratch();
        EarthworkScratch scratch = t_earthwork;
        var road = scratch.Points ??= new List<RoadSpatialGrid.RoadPoint>();
        var cells = scratch.Cells ??= new Dictionary<Vector2i, List<int>>(RoadGridComparer.Instance);
        var spare = scratch.Spare ??= new Stack<List<int>>();
        var vertices = scratch.Vertices ??= new Dictionary<Vector2i, (float written, float ground)>(RoadGridComparer.Instance);
        road.Clear();
        vertices.Clear();
        foreach (List<int> list in cells.Values) { list.Clear(); spare.Push(list); }
        cells.Clear();
        float widest = 0f;
        for (int i = 0; i < points.Count; i++)
        {
            float w = plan.WidthAt(i);
            road.Add(new RoadSpatialGrid.RoadPoint(points[i], w, plan.Heights[i]));
            widest = Mathf.Max(widest, w);
        }
        // The piece's points laid out once (RoadTerrainModifier.FitPoints): each vertex's fit reads those near it by index.
        RoadTerrainModifier.FitPoints fit = (scratch.Fit ??= new RoadTerrainModifier.FitPoints()).Set(road, null);
        int[] near = scratch.Near != null && scratch.Near.Length >= road.Count ? scratch.Near : scratch.Near = new int[System.Math.Max(road.Count, 2 * (scratch.Near?.Length ?? 0))];
        // Every point that can enter a vertex's fit is within the writer's reach of it (RoadTerrainModifier.FitReach of the
        // widest point: a superset, the fit itself leaves out those that do not reach); filed by cells that wide.
        float reach = RoadTerrainModifier.FitReach(widest), cell = Mathf.Max(1f, reach);
        for (int i = 0; i < points.Count; i++)
        {
            var key = new Vector2i(Mathf.FloorToInt(points[i].x / cell), Mathf.FloorToInt(points[i].y / cell));
            if (!cells.TryGetValue(key, out List<int>? list)) cells[key] = list = spare.Count > 0 ? spare.Pop() : new List<int>();
            list.Add(i);
        }
        long fits = 0, known = RoadStairRuns.VertexReadsKnown;
        (float written, float ground) Vertex(int x, int z)
        {
            var at = new Vector2i(x, z);
            if (vertices.TryGetValue(at, out var known)) return known;
            var v = new Vector2(x, z);
            int nearCount = 0;
            int cx = Mathf.FloorToInt(x / cell), cz = Mathf.FloorToInt(z / cell);
            for (int kx = cx - 1; kx <= cx + 1; kx++)
                for (int kz = cz - 1; kz <= cz + 1; kz++)
                    if (cells.TryGetValue(new Vector2i(kx, kz), out List<int>? list))
                        foreach (int k in list)
                            if ((points[k] - v).sqrMagnitude <= reach * reach) near[nearCount++] = k;
            float under = ground(v);
            fits++;
            return vertices[at] = (RoadTerrainModifier.WrittenAt(fit, near, nearCount, v, under), under);
        }
        // Past a piece's end the road's surface ends: a vertex beyond its first or last point (across the line through it
        // square to the piece there), within the road's width of it, is the ground the writer blends it into.
        Vector2 first = points.Count > 0 ? points[0] : default, last = points.Count > 0 ? points[points.Count - 1] : default;
        Vector2 back = points.Count > 1 ? first - points[1] : default, on = points.Count > 1 ? last - points[points.Count - 2] : default;
        float firstWidth = points.Count > 0 ? plan.WidthAt(0) : 0f, lastWidth = points.Count > 0 ? plan.WidthAt(points.Count - 1) : 0f;
        bool PastAnEnd(int x, int z)
        {
            var v = new Vector2(x, z);
            return (Vector2.Dot(v - first, back) > 0f && (v - first).sqrMagnitude < firstWidth * firstWidth)
                || (Vector2.Dot(v - last, on) > 0f && (v - last).sqrMagnitude < lastWidth * lastWidth);
        }
        bool Judge(float written, float under)
        {
            worst = Mathf.Max(worst, Mathf.Abs(written - under));
            fill = Mathf.Max(fill, written - under);
            return PastTerrainLimit(written, under, cap);
        }
        for (int i = 0; i < points.Count; i++)
        {
            Vector2 p = points[i];
            float half = plan.WidthAt(i) * 0.5f;
            bool isPast = false;
            // Every vertex under its surface.
            for (int x = Mathf.CeilToInt(p.x - half); x <= Mathf.FloorToInt(p.x + half); x++)
                for (int z = Mathf.CeilToInt(p.y - half); z <= Mathf.FloorToInt(p.y + half); z++)
                {
                    float dx = x - p.x, dz = z - p.y;
                    if (dx * dx + dz * dz > half * half || PastAnEnd(x, z)) continue;
                    var (written, under) = Vertex(x, z);
                    if (Judge(written, under)) isPast = true;
                }
            // Its centre and both edges, on the mesh between the vertices round each.
            Vector2 direction = points[Mathf.Min(i + 1, points.Count - 1)] - points[Mathf.Max(i - 1, 0)];
            Vector2 side = new Vector2(-direction.y, direction.x).normalized * half;
            for (int j = -1; j <= 1; j++)
            {
                Vector2 q = p + side * j;
                int x0 = Mathf.FloorToInt(q.x), z0 = Mathf.FloorToInt(q.y);
                float fx = q.x - x0, fz = q.y - z0;
                var c00 = Vertex(x0, z0);
                var c10 = Vertex(x0 + 1, z0);
                var c01 = Vertex(x0, z0 + 1);
                var c11 = Vertex(x0 + 1, z0 + 1);
                float written = BuriedPieces.BetweenVertices(fx, fz, c00.written, c10.written, c01.written, c11.written);
                float under = BuriedPieces.BetweenVertices(fx, fz, c00.ground, c10.ground, c01.ground, c11.ground);
                if (Judge(written, under)) isPast = true;
            }
            if (isPast) { over++; past?.Invoke(p); }
        }
        System.Threading.Interlocked.Add(ref s_earthworkPoints, points.Count);
        System.Threading.Interlocked.Add(ref s_earthworkReads, fits);
        System.Threading.Interlocked.Add(ref s_earthworkKnown, RoadStairRuns.VertexReadsKnown - known);
        System.Threading.Interlocked.Add(ref s_earthworkFits, fits);
        System.Threading.Interlocked.Increment(ref s_earthworkCalls);
        return new Earthwork(worst, fill, over);
    }

    /// <summary>Furthest a stretch may be held to a road already laid from its own profile before the
    /// road is re-routed round it (<see cref="HeldOffProfile"/>): two junction steps.</summary>
    public const float HoldPullLimit = 3f;
    private static int m_pullReroutes;

    /// <summary>Most fill a stretch held to a road already laid may stand on: past it the junction is a
    /// ridge across the slope, refused like earthwork past the cap (re-routed first). At 832,4359 a stretch
    /// held 4.5-4.7 m over the ground was an awkward junction earthwork ridge.</summary>
    public const float JunctionFillLimit = 4f;

    /// <summary>Points of <paramref name="plans"/> whose writer's reach (ManualRoads.ReachedBy) touches a zone the network goes
    /// round (<see cref="NetworkCutoff.Protected"/>); each goes in <paramref name="where"/>. What the re-routes round deep
    /// earthwork go round too, so a road the search led along a protected border at its reach, and its shaping moved closer,
    /// is searched again round it rather than refused by the final check (<see cref="WritesProtected"/>), which judges the
    /// whole footprint once the road is planned.</summary>
    private static int IntoProtected(List<RoadSpatialGrid.PlannedPath> plans, List<Vector2>? where)
    {
        if (!NetworkCutoff.AvoidsAny) return 0;
        int count = 0;
        var zones = new HashSet<Vector2s>();
        foreach (RoadSpatialGrid.PlannedPath plan in plans)
            for (int i = 0; i < plan.Points.Count; i++)
            {
                zones.Clear();
                ManualRoads.ReachedBy(plan.Points[i], plan.WidthAt(i), zones);
                if (NetworkCutoff.Protected(zones).Count == 0) continue;
                count++;
                where?.Add(plan.Points[i]);
            }
        return count;
    }

    private static int HeldRidges(List<RoadSpatialGrid.PlannedPath> plans, List<Vector2>? where) =>
        CountJunctions(plans, j => j.Fill > JunctionFillLimit, j => j.FillAt, where);

    /// <summary>Stretches of <paramref name="plans"/> held to a road already laid more than
    /// <see cref="HoldPullLimit"/> off their own profile (<see cref="RoadSpatialGrid.Junction.Pull"/>); each one's point of
    /// greatest pull goes in <paramref name="where"/>.</summary>
    private static int HeldOffProfile(List<RoadSpatialGrid.PlannedPath> plans, List<Vector2> where) =>
        CountJunctions(plans, j => Mathf.Abs(j.Pull) > HoldPullLimit, j => j.PullAt, where);

    /// <summary>The junctions of <paramref name="plans"/> that are <paramref name="past"/> a limit; each one's point of that
    /// measurement (<paramref name="at"/>) goes in <paramref name="where"/>.</summary>
    private static int CountJunctions(List<RoadSpatialGrid.PlannedPath> plans, Func<RoadSpatialGrid.Junction, bool> past,
        Func<RoadSpatialGrid.Junction, Vector2> at, List<Vector2>? where)
    {
        int count = 0;
        foreach (var plan in plans)
            if (plan.Junctions != null)
                foreach (var j in plan.Junctions)
                    if (past(j)) { count++; where?.Add(at(j)); }
        return count;
    }

    /// <summary>The road searched and planned again, with the pathfinder's
    /// current penalties; null when it does not route or build. <paramref name="startRadius"/> and <paramref name="endRadius"/>:
    /// the locations' radii as the road asked for them; the keep-out edges are derived from this search's own snapped path
    /// (<see cref="PlanSearchedPath"/>). <paramref name="startAim"/> and <paramref name="endAim"/>:
    /// the doors the road's own search ended at (<see cref="DoorsOf"/>; null where it searched to the location), which the
    /// re-route comes in to head on as the road did. <paramref name="joinedThrough"/>: as the road's own search
    /// (<see cref="GenerateRoad(Vector2, float, Vector2, float, float, string?, List{Vector2}?, Vector2?)"/>).</summary>
    internal static (List<Vector2> path, List<RoadCrossing> crossings, List<RoadSpatialGrid.PlannedPath> plans, List<RoadStairSpan> stairs, bool startAtLocation, bool endAtLocation, RoadPlan plan)? Replan(
        RoadPathfinder finder, Vector2 pathStart, Vector2 pathEnd, Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius, float width, Vector2? startAim, Vector2? endAim, bool startReached, bool endReached,
        Vector2? joinedThrough = null)
    {
        PlacedLocation? startLocation = startRadius > 0f && PlacedLocations.TryGetReady(startCenter, out PlacedLocation s0) ? s0 : null;
        PlacedLocation? endLocation = endRadius > 0f && PlacedLocations.TryGetReady(endCenter, out PlacedLocation s1) ? s1 : null;
        bool startPlaced = startLocation != null, endPlaced = endLocation != null;
        var searchLocations = new RoadLocationProtection.Targets(SearchedInto(startCenter, startRadius, startPlaced, startAim.HasValue),
            SearchedInto(endCenter, endRadius, endPlaced, endAim.HasValue));
        var path = finder.FindPath(pathStart, pathEnd, searchLocations, joinedThrough);
        if (path == null || path.Count < 2) return null;
        // In head on from the lead-in to the ring point the road's search was aimed at, as GenerateRoadCore does.
        if (startAim is Vector2 sa) path.Insert(0, sa);
        if (endAim is Vector2 ea) path.Add(ea);
        RoadPlan planned = PlanSearchedPath(finder, path, startCenter, startRadius, endCenter, endRadius,
            startPlaced, endPlaced, startReached, endReached, width, searchLocations);
        return planned.Stop != PlanStop.Planned || planned.Plans == null ? null
            : (planned.Path, planned.Crossings, planned.Plans, planned.Stairs, planned.StartAtLocation, planned.EndAtLocation, planned);
    }

    /// <summary>Where <see cref="PlanFromPath"/> stopped.</summary>
    internal enum PlanStop
    {
        /// <summary>Planned, or refused by the planner (<see cref="RoadPlan.Plans"/> null; its refusal in RoadSpatialGrid.LastRefusal).</summary>
        Planned,
        /// <summary>Nothing left between the two footprints once trimmed.</summary>
        NothingBetween,
        /// <summary>A staircase could not be laid on <see cref="RoadPlan.Path"/>.</summary>
        Staircase,
        /// <summary>Something the road needed was not prepared.</summary>
        Preparation,
    }

    /// <summary>A searched path made a road: what <see cref="PlanFromPath"/> returns.</summary>
    internal sealed class RoadPlan
    {
        public PlanStop Stop;
        /// <summary>The searched path once snapped onto the network (<see cref="PlanSearchedPath"/>), before any trim: the route
        /// a search again is compared with (RoadRetries.SameAsBeforeBan).</summary>
        public List<Vector2> Snapped = new();
        /// <summary>The keep-out edges the road was trimmed to (<see cref="KeepOutEdge"/> and an off-level stand-off,
        /// on the snapped path's bearing), derived from the radii asked for; an end that is no location keeps its radius.</summary>
        public float StartEdge, EndEdge;
        public List<Vector2> Path = new();
        public List<RoadCrossing> Crossings = new();
        public List<RoadStairSpan> Stairs = new();
        public List<RoadSpatialGrid.PlannedPath>? Plans;
        public bool StartAtLocation, EndAtLocation;
        /// <summary>A crossing whose end cannot be met (StructureEnds.MeetEnds), or whose treatment or adjoining road piece
        /// cannot be planned, with its searched moves (<see cref="RoadCrossing.Searched"/>). Kept through treatment exhaustion
        /// so the search can go round it; <see cref="Plans"/> then null.</summary>
        public RoadCrossing? Unmet;
        /// <summary>The feet its staircases were laid with their stair added, by their foot's path point (StairPlan.FootRaisedAt;
        /// <see cref="WithStairsAdded{T}"/>); null for none.</summary>
        public IReadOnlyCollection<Vector2>? StairsAddedAt;
        /// <summary>This road planned again from the same inputs, no search, with the feet at the given path points laid with
        /// their stair added (<see cref="WithStairsAdded{T}"/>): set by <see cref="PlanCandidate"/>, wrapped by
        /// <see cref="PlanSearchedPath"/>.</summary>
        public Func<IReadOnlyCollection<Vector2>, RoadPlan>? LayAgain;
    }

    /// <summary>Why a road is refused where a crossing on it has an end no way of meeting reaches.</summary>
    internal const string EndNotMet = "a bridge end the road does not meet";

    /// <summary>
    /// The one entry from a searched path to a planned road, for every road whose route a search found: the generator's
    /// (GenerateRoadCore), its re-routes (<see cref="Replan"/>) and a manual road's marked legs (ManualRoads.Prepare). Its
    /// stages, in order: the path snapped onto the network nearby (<see cref="SnapToNetwork"/>, with the
    /// <paramref name="locations"/> the search left and entered), before any crossing, staircase or junction is planned on it;
    /// the keep-out edges derived from the radii asked for (<paramref name="startRadius"/>, <paramref name="endRadius"/>) on
    /// the snapped path's bearing (<see cref="KeepOutEdge"/>, <see cref="OffLevelStandOff"/>); the planning pipeline
    /// (<see cref="PlanFromPath"/>); and, for a road through <paramref name="marks"/>, whether the planned road keeps to them
    /// (<see cref="WaypointRefusal"/>), else it is refused with the reason (<see cref="RoadPlan.Plans"/> null). The one owner of
    /// the searched path's stage order (docs/OWNERS.md).
    /// </summary>
    internal static RoadPlan PlanSearchedPath(RoadPathfinder finder, List<Vector2> path, Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius, bool startPlaced, bool endPlaced, bool startReached, bool endReached, float width,
        RoadLocationProtection.Targets locations, IReadOnlyList<Vector2>? marks = null)
    {
        path = SnapToNetwork(path, width, locations: locations)!;
        var snapped = new List<Vector2>(path);
        // On the bearing the trim cuts the path on, where it crosses the ring (not the search's own end, the centre).
        float startOut = startRadius > 0f ? OffLevelStandOff(startCenter, startRadius, RingCrossing(path, startCenter, KeepOutEdge(startCenter, startRadius, width), true)) : 0f;
        float endOut = endRadius > 0f ? OffLevelStandOff(endCenter, endRadius, RingCrossing(path, endCenter, KeepOutEdge(endCenter, endRadius, width), false)) : 0f;
        float startEdge = startRadius, endEdge = endRadius;
        if (startRadius > 0f) startEdge = KeepOutEdge(startCenter, startRadius, width) + startOut;
        if (endRadius > 0f) endEdge = KeepOutEdge(endCenter, endRadius, width) + endOut;
        RoadPlan Finished(RoadPlan planned)
        {
            (planned.Snapped, planned.StartEdge, planned.EndEdge) = (snapped, startEdge, endEdge);
            if (marks != null && planned.Plans != null && WaypointRefusal(planned.Plans, marks, width, out Vector2 mark) is string why)
            {
                planned.Plans = null;
                RoadSpatialGrid.LastRefusal = why;
                RoadSpatialGrid.RefusedAt = mark;
            }
            // Laid again (WithStairsAdded) as it was planned here: snapped, its edges and its marks kept to.
            if (planned.LayAgain is Func<IReadOnlyCollection<Vector2>, RoadPlan> again) planned.LayAgain = set => Finished(again(set));
            return planned;
        }
        return Finished(PlanFromPath(finder, path, startCenter, startEdge, endCenter, endEdge,
            startPlaced, endPlaced, startReached, endReached, width, startRadius, endRadius));
    }

    /// <summary>Why a road planned through <paramref name="marks"/> (a manual road's waypoints) does not keep to them, and
    /// the mark it left (<paramref name="at"/>); null where it keeps to every one. A mark is a constraint on the shared
    /// pipeline, not a held point: the network snap or a turning landing may move the road off it, but a planned point
    /// stays within twice the road's width of every mark, else the road is refused. Read on the whole planned road, the
    /// network it shares included. The one owner of a marked road's waypoint tolerance (docs/OWNERS.md).</summary>
    internal static string? WaypointRefusal(List<RoadSpatialGrid.PlannedPath> plans, IReadOnlyList<Vector2> marks, float width, out Vector2 at)
    {
        foreach (var point in marks)
            if (!plans.Any(p => p.Points.Any(q => Vector2.Distance(point, q) <= width * 2f)))
            {
                at = point;
                return $"the road cannot stay within {width * 2f:F0} m of waypoint {point}; move that mark";
            }
        at = default;
        return null;
    }

    /// <summary>
    /// The verdicts of the approach trials (<see cref="PlanFromPath"/>'s: the whole road with an approach a candidate proposes,
    /// <see cref="PlanCandidate"/> then <see cref="RoadRefusal"/>) made while the road being generated on this thread is
    /// planned, by what each reads (<see cref="ApproachTrial"/>). Valid for one road: nothing is stored while it is planned
    /// (<see cref="GenerateRoadCore"/> stores nothing before its last checks pass, and each search again returns the one it
    /// makes; a trial stores nothing), and the key holds the island's network version besides, which every store moves on.
    /// Null outside a road (<see cref="OneRoad"/>).
    /// </summary>
    [System.ThreadStatic] private static Dictionary<ApproachTrial, bool>? t_trials;

    private static int m_trialsRepeated;
    /// <summary>Approach trials decided as they were from the road's own (<see cref="t_trials"/>) this generation.</summary>
    internal static int TrialsRepeated => m_trialsRepeated;

    /// <summary>The trials of one road: begun by its outermost <see cref="GenerateRoadCore"/> (a search again is another call
    /// of it, within), forgotten as that call ends.</summary>
    internal static RoadTrials OneRoad()
    {
        if (t_trials != null) return default;
        t_trials = new Dictionary<ApproachTrial, bool>();
        return new RoadTrials(true);
    }

    internal readonly struct RoadTrials : IDisposable
    {
        private readonly bool m_began;
        public RoadTrials(bool began) => m_began = began;
        public void Dispose()
        {
            if (!m_began) return;
            t_trials = null;
            RoadStairRuns.LetGoOfGround();
        }
    }

    /// <summary>
    /// What one approach trial reads, compared bit for bit: its key among its road's trials (<see cref="t_trials"/>). The road
    /// it plans and every other argument of <see cref="PlanCandidate"/>; of its search (the finder), the stair steps it bought
    /// and whether it lays fords, bridges and stairs; and what else the planning reads as it stands: the world, the island's
    /// network (<see cref="IslandNetworkVersion"/>), the staircases' ground (RoadStairRuns.GroundGeneration), the ledge price
    /// setting (StairLayout.UnpricedLedge) and the road shaper's switches (RoadSpatialGrid.SuppressWiggle, RoadSwitchbacks'
    /// SuppressFillets, SuppressSwitchbacks, SuppressBends, LeastBends: off at every trial, set and put back within a plan).
    /// The settings a generation keeps (the caps, the width) do not change within one road.
    /// </summary>
    internal sealed class ApproachTrial : IEquatable<ApproachTrial>
    {
        public readonly RoadPathfinder Finder;
        public readonly Vector2[] Road;
        public readonly (Vector2 from, Vector2 to)[] Steps;
        public readonly bool Fords, Bridges, Stairs;
        public readonly Vector2 StartCenter;
        public readonly float StartRadius;
        public readonly Vector2 EndCenter;
        public readonly float EndRadius;
        public readonly bool StartPlaced, EndPlaced, StartReached, EndReached;
        public readonly float Width;
        public readonly WorldGenerator? World;
        public readonly int Network, Ground;
        public readonly bool UnpricedLedge;
        public readonly int Shaping;
        private readonly int m_hash;

        internal ApproachTrial(RoadPathfinder finder, IReadOnlyList<Vector2> road, IReadOnlyList<(Vector2 from, Vector2 to)> steps,
            bool fords, bool bridges, bool stairs, Vector2 startCenter, float startRadius, Vector2 endCenter, float endRadius,
            bool startPlaced, bool endPlaced, bool startReached, bool endReached, float width, WorldGenerator? world, int network,
            int ground, bool unpricedLedge, int shaping)
        {
            (Finder, Road, Steps, Fords, Bridges, Stairs) = (finder, road.ToArray(), steps.ToArray(), fords, bridges, stairs);
            (StartCenter, StartRadius, EndCenter, EndRadius) = (startCenter, startRadius, endCenter, endRadius);
            (StartPlaced, EndPlaced, StartReached, EndReached, Width) = (startPlaced, endPlaced, startReached, endReached, width);
            (World, Network, Ground, UnpricedLedge, Shaping) = (world, network, ground, unpricedLedge, shaping);
            unchecked
            {
                int h = Road.Length * 31 + Steps.Length;
                foreach (Vector2 p in Road) h = (h * 31 + Bits(p.x)) * 31 + Bits(p.y);
                m_hash = (h * 31 + Network) * 31 + Bits(StartRadius) + Bits(EndRadius) * 7;
            }
        }

        /// <summary>The trial of <paramref name="road"/> as <see cref="PlanFromPath"/> makes it now.</summary>
        public static ApproachTrial Of(RoadPathfinder finder, List<Vector2> road, Vector2 startCenter, float startRadius,
            Vector2 endCenter, float endRadius, bool startPlaced, bool endPlaced, bool startReached, bool endReached, float width) =>
            new(finder, road, finder.LastStairSteps, finder.Fords, finder.Bridges, finder.Stairs, startCenter, startRadius, endCenter, endRadius,
                startPlaced, endPlaced, startReached, endReached, width, GenerationWorld, IslandNetworkVersion, RoadStairRuns.GroundGeneration,
                StairLayout.UnpricedLedge,
                (RoadSpatialGrid.SuppressWiggle ? 1 : 0) | (RoadSwitchbacks.SuppressFillets ? 2 : 0) | (RoadSwitchbacks.SuppressSwitchbacks ? 4 : 0)
                | (RoadSwitchbacks.SuppressBends ? 8 : 0) | (RoadSwitchbacks.LeastBends ? 16 : 0));

        private static int Bits(float v) => TerrainFingerprint.Bits(v);
        private static bool Same(float a, float b) => Bits(a) == Bits(b);
        private static bool Same(Vector2 a, Vector2 b) => Same(a.x, b.x) && Same(a.y, b.y);

        public bool Equals(ApproachTrial? o)
        {
            if (o is null) return false;
            if (ReferenceEquals(this, o)) return true;
            if (m_hash != o.m_hash || !ReferenceEquals(Finder, o.Finder) || !ReferenceEquals(World, o.World)) return false;
            if (Fords != o.Fords || Bridges != o.Bridges || Stairs != o.Stairs) return false;
            if (!Same(StartCenter, o.StartCenter) || !Same(StartRadius, o.StartRadius) || !Same(EndCenter, o.EndCenter) || !Same(EndRadius, o.EndRadius)) return false;
            if (StartPlaced != o.StartPlaced || EndPlaced != o.EndPlaced || StartReached != o.StartReached || EndReached != o.EndReached) return false;
            if (!Same(Width, o.Width) || Network != o.Network || Ground != o.Ground || UnpricedLedge != o.UnpricedLedge || Shaping != o.Shaping) return false;
            if (Road.Length != o.Road.Length || Steps.Length != o.Steps.Length) return false;
            for (int i = 0; i < Road.Length; i++) if (!Same(Road[i], o.Road[i])) return false;
            for (int i = 0; i < Steps.Length; i++) if (!Same(Steps[i].from, o.Steps[i].from) || !Same(Steps[i].to, o.Steps[i].to)) return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is ApproachTrial o && Equals(o);
        public override int GetHashCode() => m_hash;
    }

    /// <summary>
    /// The one pipeline from a searched (and snapped) path to a planned road: trim to the keep-out edges
    /// (<paramref name="startRadius"/>, <paramref name="endRadius"/> are those edges) and off wet ends, improve the
    /// approach to a location not placed first, drop the stubs where other network reaches the location
    /// (<paramref name="startReached"/>, <paramref name="endReached"/>: <see cref="EndReached"/>), detect and snap the
    /// crossings, lay the staircases, read the ground to meet at each end, refuse what preparation missed, plan; a road
    /// refused without its stubs is planned once with them. Every road reaches it through <see cref="PlanSearchedPath"/>, which
    /// snaps the path and derives the edges first.
    /// </summary>
    internal static RoadPlan PlanFromPath(RoadPathfinder finder, List<Vector2> path, Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius, bool startPlaced, bool endPlaced, bool startReached, bool endReached, float width,
        float? startAskedRadius = null, float? endAskedRadius = null)
    {
        // The radius asked of each location (its keep-out edges are startRadius and endRadius): what its location ends are judged at.
        float startAsked = startAskedRadius ?? startRadius, endAsked = endAskedRadius ?? endRadius;
        List<Vector2>? trimmed = TrimWetEnds(TrimPathToRadii(path, startCenter, startRadius, endCenter, endRadius));
        if (trimmed == null || trimmed.Count < 2) return new RoadPlan { Path = path, Stop = PlanStop.NothingBetween };
        path = trimmed;
        // Whether the whole road with an approach a candidate proposes is built: planned by the one continuation
        // (PlanCandidate) and refused by none of the one list of refusals of a road as planned (RoadRefusal). A trial: no
        // grade statistic is kept (RoadGrade.Trial) and the planner's last refusal is left as it was; nothing is stored.
        // A trial the road being generated made already, exactly (ApproachTrial: everything the trial reads), is decided as
        // it was (OneRoad): nothing is stored while one road is planned. Not where this road found preparation missing
        // (LocationLevelling.PreparationMissedHere): a trial made then is refused for it, and one that finds it marks the road.
        bool Builds(List<Vector2> road)
        {
            Dictionary<ApproachTrial, bool>? made = t_trials;
            ApproachTrial? key = made != null && !LocationLevelling.PreparationMissedHere
                ? ApproachTrial.Of(finder, road, startCenter, startRadius, endCenter, endRadius, startPlaced, endPlaced, startReached, endReached, width)
                : null;
            if (key != null && made!.TryGetValue(key, out bool known))
            {
                System.Threading.Interlocked.Increment(ref m_trialsRepeated);
                return known;
            }
            string? why = RoadSpatialGrid.LastRefusal;
            Vector2? at = RoadSpatialGrid.RefusedAt;
            bool built;
            try
            {
                built = TrialBuilds(finder, road, startCenter, startRadius, endCenter, endRadius, startPlaced, endPlaced, startReached, endReached,
                    width, startAsked, endAsked);
            }
            finally { RoadSpatialGrid.LastRefusal = why; RoadSpatialGrid.RefusedAt = at; }
            if (key != null && !LocationLevelling.PreparationMissedHere) made![key] = built;
            return built;
        }
        // A placed location's end is already on its landing's side, and is carried on onto the landing.
        var asSearched = path;
        if (!startPlaced) path = ImproveLocationApproach(path, startCenter, startRadius, width, true, Builds);
        bool startKept = ReferenceEquals(path, asSearched);
        if (!endPlaced)
        {
            var before = path;
            path = ImproveLocationApproach(path, endCenter, endRadius, width, false, Builds);
            // The start's candidates were judged with the end's old approach: with that improved, they are judged again.
            if (!startPlaced && startKept && !ReferenceEquals(path, before)) path = ImproveLocationApproach(path, startCenter, startRadius, width, true, Builds);
        }
        return PlanCandidate(finder, path, startCenter, startRadius, endCenter, endRadius, startPlaced, endPlaced, startReached, endReached, width);
    }

    /// <summary>
    /// Whether the whole road with an approach a candidate proposes (<paramref name="road"/>, PlanFromPath's trial) is built:
    /// planned by the one continuation (<see cref="PlanCandidate"/>), its location ends planned as generation plans them
    /// (<see cref="PlanLocationEnds"/>: the end it proposes an approach for, and the other unless it is a placed location's, whose
    /// way onto its landing the road itself plans), and refused by none of the one list (<see cref="RoadRefusal"/>, its location
    /// ends' refusals among them). <paramref name="startRadius"/>, <paramref name="endRadius"/>: the keep-out edges;
    /// <paramref name="startAsked"/>, <paramref name="endAsked"/>: the radii asked of the locations, which its location ends are
    /// judged at. A trial: no grade statistic kept (RoadGrade.Trial), nothing counted, said or stored.
    /// </summary>
    internal static bool TrialBuilds(RoadPathfinder finder, List<Vector2> road, Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius, bool startPlaced, bool endPlaced, bool startReached, bool endReached, float width,
        float startAsked, float endAsked) =>
        RoadGrade.Trial(() =>
        {
            List<RoadStairSpan>? laid = null;
            IReadOnlyCollection<Vector2>? laidAdded = null;
            Refusal? refusedFor = null;
            bool Builds(IReadOnlyCollection<Vector2>? stairsAddedAt)
            {
                RoadPlan trial = PlanCandidate(finder, road, startCenter, startRadius, endCenter, endRadius,
                    startPlaced, endPlaced, startReached, endReached, width, stairsAddedAt);
                (laid, laidAdded) = (trial.Stairs, trial.StairsAddedAt);
                if (trial.Stop != PlanStop.Planned || trial.Plans == null) return false;
                var planned = new PlannedRoad
                {
                    StartCenter = startCenter, EndCenter = endCenter, Path = trial.Path, Plans = trial.Plans, Crossings = trial.Crossings, Stairs = trial.Stairs,
                    Ends = new LocationEndContext(startAsked, endAsked, trial.StartAtLocation, trial.EndAtLocation,
                        startAsked > 0f && PlacedLocations.TryGetReady(startCenter, out PlacedLocation s0) ? s0 : null,
                        endAsked > 0f && PlacedLocations.TryGetReady(endCenter, out PlacedLocation s1) ? s1 : null,
                        width, startRadius, endRadius),
                };
                PlanLocationEnds(planned, null, tally: false, landOnFlight: t_capture == null);
                refusedFor = RoadRefusal(planned, out _);
                return refusedFor == null;
            }
            // Refused with a foot kept on a raised landing: tried once more with its stair added, as generation builds it.
            bool builds = Builds(null);
            // Refused for a raised landing it names (refusedFor; a trial not planned at all is not one: its planning asked already).
            return builds || refusedFor == null || !NeedsStairsAdded(laid, refusedFor) ? builds
                : WithStairsAdded(laid!, laidAdded, set => Builds(set), b => !b, StairsAddedAt.Trial, refusedFor);
        });

    /// <summary>
    /// The continuation of <see cref="PlanFromPath"/> once the approaches are chosen, for the road as it is planned and for
    /// each approach a candidate proposes (RoadLocationApproach.Improve's evaluator): drop the stubs where other network reaches
    /// the location, detect and snap the crossings, meet their ends, lay the staircases, read the ground to meet at each end,
    /// refuse what preparation missed, plan; a road refused without its stubs is planned once with them. Never improves an
    /// approach itself. The one owner of an approach candidate's construction (docs/OWNERS.md).
    /// </summary>
    internal static RoadPlan PlanCandidate(RoadPathfinder finder, List<Vector2> path, Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius, bool startPlaced, bool endPlaced, bool startReached, bool endReached, float width,
        IReadOnlyCollection<Vector2>? stairsAddedAt = null)
    {
        var result = new RoadPlan { Path = path };
        // The search's own line while a shaped candidate is planned (PlanShapedOrPlain): its crossings' searched moves are read on it.
        List<Vector2>? searchedLine = null;
        var given = new List<Vector2>(path);
        // The feet laid with their stair added (WithStairsAdded); this road planned again so, from the same inputs.
        IReadOnlyCollection<Vector2>? added = stairsAddedAt;
        result.LayAgain = set => PlanCandidate(finder, new List<Vector2>(given), startCenter, startRadius, endCenter, endRadius,
            startPlaced, endPlaced, startReached, endReached, width, set);
        Vector2 beforeStart = path[0], beforeEnd = path[path.Count - 1];
        var withStubs = path;
        path = DropStubs(path, startReached, endReached) ?? path;
        // An end moved to a junction takes its height from the road it joins,
        // not from the location it no longer reaches: aiming at the location's edge
        // from the junction refused short roads as "ends too far apart for
        // the cap".
        bool startAtLocation = path[0] == beforeStart, endAtLocation = path[path.Count - 1] == beforeEnd;
        // Detected, snapped to a crossing an earlier road made a few metres off (it takes that site's banks and ends, so this
        // road is painted up to the one bridge built there), each crossing's ends met (StructureEnds.MeetEnds, the road
        // brought to each end's foot), stairs laid, the ground to meet at each end read, and planned.
        // A bridge's end refused for a climb too steep for its rests is given up as any other unmet end (GenerateRoadCore's
        // ban and search again); it takes a pitch only if nothing else reaches its destination (RouteIsland's last round).
        // A Mistlands viaduct's end so refused, on a generated road (an island worker's: a manual road never plans as the
        // last resort), is planned once more as the last resort (RoadPitches.AsLastResort) through the same flow, every
        // re-plan of this candidate alike: the ban would drop the viaduct, and nothing but the pitch reaches its end once
        // every way of meeting it (MeetEnds, its stairs, its approaches) was refused for its rests. Judged and budgeted
        // as it is committed (RoadRefusal); not planned so, it is refused as the normal flow refused it, round the same end.
        // A road through standing water outside the Mistlands is also shaped (pulled and swayed, RoadSpatialGrid.PullAndSway) before its crossings are
        // found, on the stretches between the structures the search bought (each kept as it was: a stair step's or a viaduct's
        // jump one consecutive pair, a river crossing's points), so a pool's crossing is detected on the swayed line, whose wet
        // points have moved onto drier ground where the sway finds it. The shaped and the plain candidate are both planned, and
        // the shaped one is kept only as the sway's own rule keeps a swayed plan (it plans, and moves no more earth than the
        // plain one: RoadWiggle.NoMoreEarth); where neither plans, the plain one's refusal stands. A crossing's searched moves
        // are the search's own line's. The last resort for a viaduct's end (Plan) is asked of the two together, once neither planned
        // without it: a pitch is never taken by the shaped line where the plain one planned without one.
        bool Plan(List<Vector2> on, bool atStart, bool atEnd, IReadOnlyList<(Vector2 from, Vector2 to)>? steps = null)
        {
            int rests = RoadPitches.RestsRefusals;
            bool planned = PlanShapedOrPlain(on, atStart, atEnd, steps);
            if (planned && result.Plans == null && !RoadPitches.LastResort && RoadPitches.RestsRefusals > rests && CrossIslandReads.Me > 0
                && result.Unmet is RoadCrossing end && !end.Shared && IsViaduct(end))
            {
                System.Threading.Interlocked.Increment(ref m_viaductEndsTriedAgain);
                string what = FormattableString.Invariant($"a viaduct's end, ({end.FromBank.x:F0}, {end.FromBank.y:F0}) -> ({end.ToBank.x:F0}, {end.ToBank.y:F0})");
                planned = RoadPitches.AsLastResort(what, () => PlanShapedOrPlain(on, atStart, atEnd, steps));
                if (planned && result.Plans != null) System.Threading.Interlocked.Increment(ref m_viaductEndsPlanned);
                else if (planned && result.Plans == null && result.Unmet == null) result.Unmet = end;
            }
            return planned;
        }
        bool PlanShapedOrPlain(List<Vector2> on, bool atStart, bool atEnd, IReadOnlyList<(Vector2 from, Vector2 to)>? steps)
        {
            if (ShapeBeforeCrossings(on, atStart, atEnd, steps) is not List<Vector2> shaped)
                return PlanTreatments(on, atStart, atEnd, steps);
            bool was = RoadSpatialGrid.ShapedBeforeCrossings;
            var searchedWas = searchedLine;
            bool shapedPlanned;
            RoadPlanState? shapedState = null;
            RoadSpatialGrid.ShapedBeforeCrossings = true;
            searchedLine = on;
            try
            {
                shapedPlanned = PlanTreatments(shaped, atStart, atEnd, steps);
                if (shapedPlanned && result.Plans != null) shapedState = RoadPlanState.Of(result);
            }
            finally { RoadSpatialGrid.ShapedBeforeCrossings = was; searchedLine = searchedWas; }
            bool plainPlanned = PlanTreatments(on, atStart, atEnd, steps);
            if (shapedState != null && (!plainPlanned || result.Plans == null
                || RoadWiggle.NoMoreEarth(shapedState.Plans!, result.Plans, GroundBeforeRoads(GenerationWorld!))))
            {
                shapedState.Into(result);
                return true;
            }
            System.Threading.Interlocked.Increment(ref m_shapedFirstFellBack);
            return plainPlanned;
        }
        List<Vector2>? ShapeBeforeCrossings(List<Vector2> on, bool atStart, bool atEnd, IReadOnlyList<(Vector2 from, Vector2 to)>? steps)
        {
            if (!RoadWiggle.Enabled || RoadSpatialGrid.SuppressWiggle || RoadSpatialGrid.ShapedBeforeCrossings || GenerationWorld == null
                || on.Count < 3)
                return null;
            WorldGenerator world = GenerationWorld;
            // Shaping first is the swamp's: a road with any point where a biome says no (RoadRules.ShapedFirst: the Mistlands, any
            // point, RoadBiome.AnyIn) is planned on the search's own line alone, as every road was before shaping first. The
            // Mistlands' ground bends its roads on its own (the sway's biome factor there is none), and its valley roads are
            // planned once, not twice.
            if (RoadBiome.AnyIn(on, q => RoadBiome.At(world, q), b => !RoadRules.For(b).ShapedFirst)) return null;
            // Only a line that runs over standing water anywhere (the search's own probe, RoadCrossingDetector.SegmentCrossesRiver
            // on standing water) is detected in full: most roads have none, and are not detected twice.
            bool wet = false;
            for (int k = 1; k < on.Count && !wet; k++) wet = RoadCrossingDetector.SegmentCrossesRiver(on[k - 1], on[k], world, standing: true);
            if (!wet) return null;
            List<RoadCrossing> found = DetectCrossings(finder, on);
            if (!found.Exists(c => c.Shallow)) return null;
            // Kept where they are, as spans of the path's own points: the stair steps and viaduct jumps the search bought (each a
            // consecutive pair of points), every point of a crossing that is not a pool's (a river's jump, a viaduct), and the held
            // structure ends. One pass over the path, its points looked up by where they stand.
            var keep = new bool[on.Count];
            var at = new Dictionary<(int, int), int>();
            for (int k = 0; k < on.Count; k++) at[(Mathf.RoundToInt(on[k].x), Mathf.RoundToInt(on[k].y))] = k;
            int Index(Vector2 p)
            {
                int bx = Mathf.RoundToInt(p.x), bz = Mathf.RoundToInt(p.y);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (at.TryGetValue((bx + dx, bz + dz), out int k) && (on[k] - p).sqrMagnitude < 1f) return k;
                return -1;
            }
            void KeepJump(Vector2 a, Vector2 b)
            {
                int ia = Index(a), ib = Index(b);
                if (ia >= 0) keep[ia] = true;
                if (ib >= 0) keep[ib] = true;
                if (ia >= 0 && ib >= 0) for (int k = Mathf.Min(ia, ib); k <= Mathf.Max(ia, ib); k++) keep[k] = true;
            }
            foreach (var (a, b) in steps ?? finder.LastStairSteps) KeepJump(a, b);
            foreach (var (a, b) in finder.LastViaductJumps) KeepJump(a, b);
            foreach (RoadCrossing c in found)
                if (!c.Shallow)
                    for (int k = Mathf.Max(0, c.FromIndex); k <= Mathf.Min(on.Count - 1, c.ToIndex); k++) keep[k] = true;
            if (RoadSpatialGrid.HeldPoints is List<Vector2> held) foreach (Vector2 h in held) { int k = Index(h); if (k >= 0) keep[k] = true; }
            keep[0] = keep[on.Count - 1] = true;
            var arriving = new RoadLocationProtection.Targets(atStart && startRadius > 0f ? startCenter : null, atEnd && endRadius > 0f ? endCenter : null);
            float Ground(Vector2 q) => BiomeBlendedHeight.GetBlendedHeight(q.x, q.y, world);
            // Each stretch between kept points shaped on its own, its two ends where they are: a kept span is copied as it is,
            // so a bought jump stays one consecutive pair (DvergrViaduct.AddJumps, the stair steps' match) and no point of a
            // river's crossing is swayed.
            var shaped = ShapedBetweenKept(on, keep, stretch => RoadSpatialGrid.PullAndSway(stretch, width, world, Ground, null, arriving, sway: true));
            System.Threading.Interlocked.Increment(ref m_shapedFirst);
            return shaped;
        }
        bool PlanTreatments(List<Vector2> on, bool atStart, bool atEnd, IReadOnlyList<(Vector2 from, Vector2 to)>? steps)
        {
            // A failed pool treatment is not a failed pool. Ask its detector for another eligible treatment,
            // then meet its ends and plan every piece again. Each attempt starts with the searched path:
            // the previous treatment's aprons and moved banks must not become the next one's input.
            var searched = new List<Vector2>(on);
            var refused = new List<(Vector2 site, FordStyle style)>();
            var failedCrossings = new List<RoadCrossing>();
            for (int attempt = 0; ; attempt++)
            {
                failedCrossings.Clear();
                bool planned = PlanTreatment(new List<Vector2>(searched), atStart, atEnd, steps, refused, failedCrossings);
                if (!planned || result.Plans != null || attempt >= CrossingRetries) return planned;
                int before = refused.Count;
                foreach (var c in failedCrossings)
                {
                    if (!c.Shallow || c.Shared || c.Invalid || c.Kind != CrossingKind.Ford || c.ShallowSite is not Vector2 site) continue;
                    var choice = (site, c.Style);
                    if (!refused.Contains(choice)) refused.Add(choice);
                }
                if (refused.Count == before) return planned;
            }
        }
        bool PlanTreatment(List<Vector2> on, bool atStart, bool atEnd, IReadOnlyList<(Vector2 from, Vector2 to)>? steps,
            IReadOnlyList<(Vector2 site, FordStyle style)> refused, List<RoadCrossing> failedCrossings)
        {
            var treated = new List<Vector2>(on);
            result.Stop = PlanStop.Planned;
            result.Path = on;
            result.StartAtLocation = atStart; result.EndAtLocation = atEnd;
            result.Crossings = DetectCrossings(finder, on, refused);
            SnapToExistingCrossings(result.Crossings);
            foreach (RoadCrossing c in result.Crossings) c.Searched = searchedLine != null ? SearchedMovesOn(searchedLine, c) : SearchedMoves(on, c);
            if (refused.Count > 0 && result.Crossings.Exists(c => c.Shallow && c.Invalid))
            {
                result.Plans = null;
                // Keep the last failing crossing and its searched moves for route-around recovery.
                return true;
            }
            result.Unmet = null;
            if (MeetEnds(finder, on, result.Crossings, width) is RoadCrossing unmet)
            {
                // No way of meeting one of its ends reaches the road: the road is searched round that crossing
                // (GenerateRoadCore), or refused.
                (result.Unmet, result.Plans) = (unmet, null);
                failedCrossings.Add(unmet);
                RoadSpatialGrid.LastRefusal = EndNotMet;
                return true;
            }
            // The locations its ends still arrive at (a stub dropped onto a junction arrives at none): the only footprints whose
            // clearance a trimmed end may graze (RoadLocationProtection.Targets), its staircases' carried ends' roads too.
            var arriving = new RoadLocationProtection.Targets(atStart && startRadius > 0f ? startCenter : null, atEnd && endRadius > 0f ? endCenter : null);
            if (!TryLayStairs(steps ?? finder.LastStairSteps, on, result.Crossings, arriving, added, out List<RoadStairSpan> stairs)) { result.Stop = PlanStop.Staircase; return false; }
            result.Stairs = stairs;
            result.StairsAddedAt = added;
            // The heights to meet are asked for at the road's OWN ends, after
            // trimming - not at the locations' centres. A road stops at the
            // exterior radius, which on a hillside is metres below the middle of
            // the location it is going to, and aiming at the middle builds that
            // difference as a rim around the doorstep.
            float? startGround = atStart ? ApproachGround(on[0], startCenter, startRadius) : null;
            float? endGround = atEnd ? ApproachGround(on[on.Count - 1], endCenter, endRadius) : null;
            // Something this road needed was not prepared. Building it anyway
            // would put it through a footprint nothing could see, or land it on a
            // height nothing could read. Leaving the destination unconnected is
            // the recoverable half of that choice.
            if (LocationLevelling.PreparationMissedHere) { result.Stop = PlanStop.Preparation; return false; }
            RoadSpatialGrid.LastRefusal = null;
            // An end at its location that does not come level with it is where an approach staircase may be laid up from
            // (LocationApproaches, once the road is planned): no tight switchback narrows the road into it (RoadSpatialGrid.StairStarts).
            bool ApproachFrom(bool at, bool placed, float? ground, Vector2 centre, float radius) =>
                at && !placed && ground == null && radius > 0f && ApproachStairsAt(centre, LocationPrefabLevelling.PrefabAt(centre));
            int failedBefore = failedCrossings.Count;
            result.Plans = PlanRoadPathWithCrossings(on, result.Crossings, width, startGround, endGround, stairs,
                (ApproachFrom(atStart, startPlaced, startGround, startCenter, startRadius), ApproachFrom(atEnd, endPlaced, endGround, endCenter, endRadius)),
                arriving, failedCrossings);
            // A foot kept on the shorter flight whose raised landing the road cannot be planned with (its grade into the
            // foot, its held ends): planned once more with the stair added (WithStairsAdded).
            if (result.Plans == null && NeedsStairsAdded(stairs))
            {
                failedCrossings.RemoveRange(failedBefore, failedCrossings.Count - failedBefore);
                return WithStairsAdded(stairs, added, set =>
                {
                    var was = added;
                    added = set;
                    try { return PlanTreatment(new List<Vector2>(treated), atStart, atEnd, steps, refused, failedCrossings); }
                    finally { added = was; }
                }, planned => planned && result.Plans == null, StairsAddedAt.Planning);
            }
            if (result.Plans == null) result.Unmet = failedCrossings.FirstOrDefault();
            // A staircase across the road as planned at either end (RoadStairRuns.SpanAcrossItsRoad): its stretch closed to stairs
            // and the road searched again, as for one that could not be laid.
            if (result.Plans != null)
                foreach (RoadStairSpan stair in stairs)
                {
                    if (stair.Existing || RoadStairRuns.SpanAcrossItsRoad(stair, result.Plans, out string piece) is not string end) continue;
                    RoadStairRuns.Refuse(stair, end, piece);
                    result.Plans = null;
                    result.Stop = PlanStop.Staircase;
                    return false;
                }
            return true;
        }
        // Over a hump the search climbed on stairs and came down again close by, the road straight across it on earthwork
        // first: both staircases dropped where that plans within the caps and meets the network as any road must.
        if (AcrossTheHumps(path, startAtLocation, endAtLocation)) return result;
        if (!Plan(path, startAtLocation, endAtLocation)) return result;
        // Dropping the stubs is cosmetic: a road refused without them is
        // planned with them. With both ends at locations other roads reach,
        // the 40-60 m connector left between two parts of the network can
        // be too steep where the full road is not.
        if (result.Plans == null && !ReferenceEquals(withStubs, path)) Plan(withStubs, true, true);
        return result;

        // Each crest pair whose ends stand within RoadStairRuns.HumpReach (RoadStairRuns.Humps), last first: its stretch
        // laid as a straight road between its two ends, its stair steps dropped, and the road planned so. Kept where it
        // plans, its earthwork within the cap, no trench or ridge (RoadRidges) and no crossing, and it meets the network as any
        // road must (MeetingRefusal) near the stretch; a hump the caps, a trench or the network refuses stays stairs, and with no cap none is tried. True where the
        // road is planned so.
        bool AcrossTheHumps(List<Vector2> on, bool atStart, bool atEnd)
        {
            // Only within an earthwork cap: with none (0), nothing bounds the cut a hump's road would take.
            if (!EarthworkCapped || finder.LastStairSteps.Count == 0 || GenerationWorld == null) return false;
            List<(int from, int to)> humps = RoadStairRuns.Humps(on, finder.LastStairSteps, GenerationWorld);
            if (humps.Count == 0) return false;
            IReadOnlyList<(Vector2 from, Vector2 to)> steps = finder.LastStairSteps;
            bool across = false;
            for (int k = humps.Count - 1; k >= 0; k--)
            {
                var (from, to) = humps[k];
                Vector2 a = on[from], b = on[to];
                var straight = new List<Vector2>(on.GetRange(0, from + 1));
                int cells = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / RoadPathfinder.CellSize));
                for (int c = 1; c < cells; c++) straight.Add(Vector2.Lerp(a, b, c / (float)cells));
                straight.AddRange(on.GetRange(to, on.Count - to));
                var kept = RoadStairRuns.StepsOff(on, from, to, steps);
                if (!Plan(straight, atStart, atEnd, kept) || result.Plans == null) continue;
                bool Near(Vector2 p)
                {
                    Vector2 ab = b - a;
                    float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                    return Vector2.Distance(p, a + ab * t) <= HumpCheckReach;
                }
                var past = new List<Vector2>();
                PastTerrainLimit(result.Plans, EarthworkLimit, past);
                if (past.Exists(Near)
                    || Ridges(result.Plans, result.Crossings).Exists(st => st.Points.Exists(Near))
                    || result.Crossings.Exists(c => c.FromIndex <= from + cells && c.ToIndex >= from)
                    || MeetingRefusal(result.Plans, out Vector2 meets) != null && Near(meets)) continue;
                (on, steps, across) = (straight, kept, true);
                System.Threading.Interlocked.Increment(ref m_humpsLevelled);
                if (RoadsLog) Log.LogDebug(FormattableString.Invariant($"[STAIRS] hump between ({a.x:F1}, {a.y:F1}) and ({b.x:F1}, {b.y:F1}): laid as road on earthwork, both staircases dropped"));
            }
            return across && Plan(on, atStart, atEnd, steps) && result.Plans != null;
        }
    }

    /// <summary>How far from a hump's straight road its plan is read for earthwork past the cap and the network it meets
    /// (PlanFromPath): the stretch and the blend either side.</summary>
    private const float HumpCheckReach = 16f;

    private static int m_humpsLevelled;
    private static int m_shapedFirst, m_shapedFirstFellBack;
    /// <summary>Searched roads through standing water shaped before their crossings were found this generation, and of them
    /// those planned in the old order because the shaped line did not plan (PlanCandidate).</summary>
    public static (int shaped, int fellBack) ShapedBeforeCrossings => (m_shapedFirst, m_shapedFirstFellBack);
    /// <summary>Crest pairs planned as road straight across their hump this generation, built or not (PlanFromPath).</summary>
    public static int HumpsLevelled => m_humpsLevelled;

    internal static List<RoadSpatialGrid.PlannedPath>? PlanRoadPathWithCrossings(
        List<Vector2> path, List<RoadCrossing> crossings, float width, float? startGround, float? endGround,
        List<RoadStairSpan>? stairs = null, (bool start, bool end) approaches = default, RoadLocationProtection.Targets locations = default,
        ICollection<RoadCrossing>? failedCrossings = null)
    {
        using var fitting = new RoadFittingActivity(IslandActivity);
        return PlanPieces(path, crossings, width, startGround, endGround, stairs, approaches, locations, failedCrossings);
    }

    /// <summary>The crossings of a finished path: its river crossings (RoadCrossingDetector) and
    /// the Mistlands viaducts its search bought over dry valleys (DvergrViaduct.AddJumps), in path
    /// order.</summary>
    private static List<RoadCrossing> DetectCrossings(RoadPathfinder finder, List<Vector2> path,
        IReadOnlyList<(Vector2 site, FordStyle style)>? refusedShallows = null)
    {
        List<RoadCrossing> found = finder.Fords || finder.Bridges
            ? RoadCrossingDetector.Detect(path, GenerationWorld, finder.Bridges, finder.Fords, refusedShallows)
            : new List<RoadCrossing>();
        if (finder.Viaducts && finder.Bridges)
            DvergrViaduct.AddJumps(path, finder.LastViaductJumps, GenerationWorld, found);
        return found;
    }

    /// <summary>
    /// Every crossing's ends met on <paramref name="path"/> (StructureEnds.MeetEnds, the one owner, every kind alike), the
    /// road brought to each end's foot; the first crossing with an end no way of meeting reaches, with the search's jump it
    /// lies on, or null. A stair step the search bought is never cut; each end's landing is within the earthwork cap across
    /// the road's <paramref name="width"/>.
    /// </summary>
    internal static RoadCrossing? MeetEnds(RoadPathfinder finder, List<Vector2> path, List<RoadCrossing> crossings, float width)
    {
        var steps = finder.LastStairSteps;
        bool IsStep(Vector2 a, Vector2 b)
        {
            foreach (var (from, to) in steps)
                if ((Vector2.Distance(a, from) <= 1f && Vector2.Distance(b, to) <= 1f) || (Vector2.Distance(a, to) <= 1f && Vector2.Distance(b, from) <= 1f))
                    return true;
            return false;
        }
        // A valley viaduct's moved banks only where the search would have bought its line, on the ground its jump was bought on
        // (RoadPathfinder.MovedViaductAllowed, the search's own test of a viaduct's line), remembered with what that test reads.
        return StructureEnds.MeetEndsRemembered(path, crossings, GenerationWorld!, EarthworkLimit, steps, IsStep, width,
            finder.MovedViaductAllowed, finder.MoveAsked());
    }

    /// <summary>The moves of the search's <paramref name="path"/> that <paramref name="c"/> lies on, from its
    /// <see cref="RoadCrossing.FromIndex"/> to its <see cref="RoadCrossing.ToIndex"/>: each step of a ford walked cell by
    /// cell, of a bank walked out to dry ground or moved back to a higher one, and the jump of a bridge or viaduct.</summary>
    /// <summary>A line shaped only on its stretches between kept points (<paramref name="keep"/>, its two ends always kept): each
    /// stretch of two moves or more handed to <paramref name="shape"/> with its two ends (which it keeps where they are), each run
    /// of kept points copied as it is, so a kept consecutive pair (a bought jump) stays one consecutive pair (PlanCandidate's
    /// shaped candidate).</summary>
    internal static List<Vector2> ShapedBetweenKept(List<Vector2> on, bool[] keep, Func<List<Vector2>, List<Vector2>> shape)
    {
        var shaped = new List<Vector2> { on[0] };
        int from = 0;
        for (int k = 1; k < on.Count; k++)
        {
            if (!keep[k] && k < on.Count - 1) continue;
            if (k - from >= 2)
            {
                var stretch = shape(on.GetRange(from, k - from + 1));
                for (int q = 1; q < stretch.Count - 1; q++) shaped.Add(stretch[q]);
            }
            shaped.Add(on[k]);
            from = k;
        }
        return shaped;
    }

    /// <summary>The moves of the search's own <paramref name="searched"/> line that a crossing found on a line shaped from it
    /// lies on (<see cref="SearchedMoves"/> for the line it was found on): every move within a cell (RoadPathfinder.CellSize, the
    /// bans' own reach) of the crossing's bank-to-bank line, else its banks.</summary>
    internal static List<(Vector2 from, Vector2 to)> SearchedMovesOn(List<Vector2> searched, RoadCrossing c)
    {
        var moves = new List<(Vector2 from, Vector2 to)>();
        for (int i = 1; i < searched.Count; i++)
            if (SegmentsWithin(searched[i - 1], searched[i], c.FromBank, c.ToBank, RoadPathfinder.CellSize)) moves.Add((searched[i - 1], searched[i]));
        if (moves.Count == 0) moves.Add((c.FromBank, c.ToBank));
        return moves;
    }

    private static bool SegmentsWithin(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float reach)
    {
        static float ToSegment(Vector2 p, Vector2 s0, Vector2 s1)
        {
            Vector2 sd = s1 - s0;
            float t = sd.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - s0, sd) / sd.sqrMagnitude);
            return Vector2.Distance(p, s0 + sd * t);
        }
        // Within reach where an end of one is near the other, or the two cross.
        if (ToSegment(a, c, d) <= reach || ToSegment(b, c, d) <= reach || ToSegment(c, a, b) <= reach || ToSegment(d, a, b) <= reach) return true;
        static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;
        float d1 = Cross(b - a, c - a), d2 = Cross(b - a, d - a), d3 = Cross(d - c, a - c), d4 = Cross(d - c, b - c);
        return (d1 > 0f) != (d2 > 0f) && (d3 > 0f) != (d4 > 0f);
    }

    /// <summary>What a candidate's planning left in its <see cref="RoadPlan"/> (PlanCandidate's Plan, which plans a shaped
    /// and a plain candidate in turn and keeps one).</summary>
    private sealed class RoadPlanState
    {
        public PlanStop Stop;
        public List<Vector2> Path = null!;
        public List<RoadCrossing> Crossings = null!;
        public List<RoadStairSpan> Stairs = null!;
        public List<RoadSpatialGrid.PlannedPath>? Plans;
        public bool StartAtLocation, EndAtLocation;
        public RoadCrossing? Unmet;
        public IReadOnlyCollection<Vector2>? StairsAddedAt;
        public static RoadPlanState Of(RoadPlan r) => new()
        {
            Stop = r.Stop, Path = r.Path, Crossings = r.Crossings, Stairs = r.Stairs, Plans = r.Plans, StartAtLocation = r.StartAtLocation,
            EndAtLocation = r.EndAtLocation, Unmet = r.Unmet, StairsAddedAt = r.StairsAddedAt,
        };
        public void Into(RoadPlan r)
        {
            r.Stop = Stop; r.Path = Path; r.Crossings = Crossings; r.Stairs = Stairs; r.Plans = Plans; r.StartAtLocation = StartAtLocation;
            r.EndAtLocation = EndAtLocation; r.Unmet = Unmet; r.StairsAddedAt = StairsAddedAt;
        }
    }

    internal static List<(Vector2 from, Vector2 to)> SearchedMoves(List<Vector2> path, RoadCrossing c)
    {
        var moves = new List<(Vector2 from, Vector2 to)>();
        int from = Mathf.Clamp(c.FromIndex, 0, path.Count - 1), to = Mathf.Clamp(c.ToIndex, 0, path.Count - 1);
        for (int i = from; i < to; i++) moves.Add((path[i], path[i + 1]));
        if (moves.Count == 0) moves.Add((c.FromBank, c.ToBank));
        return moves;
    }

    /// <summary>
    /// A staircase joins the network: its earthwork goes in with the road's (at a location, only outside every footprint: the
    /// terrain writer leaves the location's own ground alone, and its clearance keeps the location's own rocks), its pieces are a
    /// structure source's, standing from now on for later roads. The one way a road's, an entry's and an approach's
    /// staircase is laid: its cuts, then the run.
    /// Generation lays the run its road's checks read (<see cref="WrittenRoad.RunOf"/>, made by <see cref="RunOf"/>). The one
    /// owner of joining a network (docs/OWNERS.md).
    /// </summary>
    private static void JoinNetwork(StairRun run, StairPlan plan, StairKit kit, bool atLocation)
    {
        RoadSpatialGrid.CommitCuts(CutsOf(plan, atLocation), StairLayout.CutWidth);
        AddToNetwork(run, plan, kit);
    }

    /// <summary>A staircase's earthwork as it is laid (<see cref="JoinNetwork"/>, and a manual road's append): at a location, only
    /// outside every footprint.</summary>
    internal static IReadOnlyList<Vector3> CutsOf(StairPlan plan, bool atLocation) =>
        atLocation ? RoadStairRuns.CutsOutsideFootprints(plan) : plan.Cuts;

    /// <summary>A staircase's run as it is laid (<see cref="JoinNetwork"/>, and a manual road's append). <paramref name="roads"/>:
    /// the road's pieces as it stores them; the one planned to each end of <paramref name="span"/>
    /// (RoadSpatialGrid.PlannedPath.StairAtStart, StairAtEnd) is the road that meets it there (StairRun.FromRoad,
    /// StairRun.ToRoad), never a road found near it.</summary>
    internal static StairRun RunOf(Vector2 from, Vector2 to, StairPlan plan, StairKit kit, bool atLocation, Vector2? fromMeet = null,
        Vector2? toMeet = null, (Vector2 Centre, float Ground)? expectedLocation = null, RoadStairSpan? span = null,
        IReadOnlyList<RoadSpatialGrid.PlannedPath>? roads = null)
    {
        var run = StairRun.FromPlan(from, to, RoadStairRuns.Width, plan, kit);
        run.KeepsLocationRocks = atLocation;
        run.ExpectedLocation = expectedLocation;
        run.FromMeet = fromMeet;
        // At a location, where its ground meets a floor laid out onto it from the foot (StairPlan.LocationMeet): the location's end.
        run.ToMeet = toMeet ?? (atLocation ? plan.LocationMeet : null);
        if (span != null && roads != null)
            foreach (RoadSpatialGrid.PlannedPath piece in roads)
                foreach (var (mark, atStart) in new[] { (piece.StairAtStart, true), (piece.StairAtEnd, false) })
                    if (mark is { } m && ReferenceEquals(m.Stair, span))
                    {
                        if (m.AtTo) run.ToRoad = StairRoad.Of(piece, atStart); else run.FromRoad = StairRoad.Of(piece, atStart);
                    }
        return run;
    }

    /// <summary>A laid staircase's run becomes the network's: a structure source's pieces, standing from now on for later
    /// roads, its layout committed. <see cref="JoinNetwork"/>'s second half, and a manual road's, whose cuts go in its own
    /// append.</summary>
    private static void AddToNetwork(StairRun run, StairPlan plan, StairKit kit)
    {
        StairStructures.Add(run);
        StairStructures.MarkStanding(run);
        RoadStairRuns.Laid(run, plan, kit);
        if (RoadsLog)
        {
            System.Threading.Interlocked.Increment(ref m_stairRuns);
            t_stairRunsLaidHere++;
            // One line per end as laid, read on the ground its layout was decided on (StairEnds, the one end reader).
            foreach (StairEnds.End end in StairEnds.Read(run, RoadStairRuns.PlanningGround(run, GenerationWorld), StairStructures.MetAtEnds(run)))
                Log.LogInfo(StairEnds.Line(end) + " (as laid)");
        }
    }

    /// <summary>Every staircase end of the network as generated, read on its finished ground (StairEnds.AuditAll): the counts,
    /// and a line for each end with no way off it or not verified. The one invariant every gate reads ("ends: N ok, M miss,
    /// K gap"; beside it "U not verified", a foot whose road is not known, StairEnds.OntoItsRoad); and
    /// beside it the network's twin ways into locations ("twins: N").</summary>
    private static void LogStairEnds()
    {
        var world = GenerationWorld;
        var (lines, ok, miss, gap, unverified, unfinished) = StairEnds.AuditAll(run => StairEnds.GroundOf(run, world));
        Log.LogInfo($"  Staircase ends (StairEnds, on the finished ground): ends: {ok} ok, {miss} miss, {gap} gap, {unverified} not verified, {unfinished} unfinished; approaches laid with a bare top, no layout with a way off it: {RoadStairRuns.BareTopsKept}; merges' free layout not laid, both held ones out of budget: {RoadStairRuns.MergesFreeSkipped}");
        foreach (var (verdict, line) in lines)
            if (verdict != StairEnds.Verdict.Ok || line.Contains(" UNFINISHED: ")) Log.LogInfo("  " + line);
        // Beside it, the ways into locations (LocationWaysIn.Twins, the rule's own test over the whole network): no location has two
        // ways in arriving the same way ("twins: N", each named with its location and both ways' ends).
        var twins = LocationWaysIn.Twins();
        Log.LogInfo($"  Ways into locations (LocationWaysIn, the whole network): twins: {twins.Count}");
        foreach (var twin in twins) Log.LogInfo("  " + twin.line);
    }

    /// <summary>Whether a crossing is built as a Dvergr viaduct (BridgeStructures.IsViaduct, the one owner; the kind stored
    /// with it once the road is recorded).</summary>
    private static bool IsViaduct(RoadCrossing c) => BridgeStructures.IsViaduct(c, GenerationWorld);

    /// <summary>A crossing with a structure at each end the road is held to (StructureEnds.Of): a viaduct, a bridge or a
    /// ford's spanned footbridge in any kit. The road ends on each end's foot at its height, come along its line or from its
    /// landing pad, as the one owner decided (StructureEnds.MeetEnds). A waded or raised ford is road.</summary>
    internal static bool MeetsTheRoad(RoadCrossing c) => BridgeStructures.Ends(c, GenerationWorld).near.IsSet;

    /// <summary>
    /// The final check of a road's structures, on the road as BUILT: the road this road plans together
    /// with the network laid before it. Judging only the ends of this road's own planned pieces passed viaducts with road
    /// under their deck ends (another road, and the 3 m every bridge's road was painted in under its deck at the
    /// ground's height after the check), and a floor raised by a road's fill. The first
    /// crossing on the road that fails, with where and why, or null:
    /// <list type="bullet">
    /// <item>road under its deck (StructureEnds.UnderTheDeck), this road's or the network's;</item>
    /// <item>a valley viaduct under the floor over the finished ground (UnderTheFloor).</item>
    /// </list>
    /// Where what the road writes is known (<paramref name="written"/>, <see cref="Written"/>), the built road is read as it
    /// will stand once the road is stored (RoadTerrainModifier.AsStored: its own fill, pads and mounds, and the other roads it
    /// raises): the final check's and the crossing bans' alike, one reader (<see cref="WithNoKit"/>).
    /// </summary>
    internal static RoadCrossing? BuiltRefusal(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings, out Vector2 where, out string why,
        ManualAppend? written = null)
    {
        // The first crossing on the road that fails, of every kind alike: each is searched round or refuses the road.
        why = "";
        where = default;
        using System.IDisposable? stored = CountedAsStored(written, crossings, ref s_builtReads, ref s_builtReadsAsStored);
        foreach (RoadCrossing c in crossings)
        {
            if (!MeetsTheRoad(c) || c.Shared) continue;
            var (near, far) = BridgeStructures.Ends(c, GenerationWorld);
            float deck = BridgeStructures.DeckHeight(c, GenerationWorld), half = BridgeStructures.DeckHalfWidth(c, GenerationWorld);
            foreach (var (p, h, w) in StructureEnds.BuiltRoadNear(c.Center, c.Width * 0.5f + half + 4f, plans))
                if (StructureEnds.UnderTheDeck(p, h, w, near, far, deck, half))
                {
                    where = p; why = FormattableString.Invariant($"road under the deck at ({p.x:F1}, {p.y:F1}), {deck - h:F1} m below it");
                    return c;
                }
        }
        if (UnderTheFloor(plans, crossings) is RoadCrossing low) { where = low.Center; why = "a pier under the floor over the finished ground"; return low; }
        return null;
    }

    /// <summary>
    /// The first crossing of a road whose structure would stand on ground the network keeps out of, with where and why; null
    /// where none would. Each crossing with a deck that is its own (not one it shares with a road laid before it) is read by
    /// every piece it may be laid with (<see cref="BridgeStructures.EveryPieceItMayLay"/>) through the one ground owner
    /// (<see cref="NetworkGround.StructureRefusal"/>), the road's targets (its pieces' <see cref="RoadSpatialGrid.PlannedPath.Locations"/>)
    /// its own: a structure keeps out of every other location and every zone the network goes round as its road's line does,
    /// never laid on nor holding their ground (its railing, posts, piers and end steps reach past the road's width). Generation searches the road again without it
    /// (GenerateRoadCore's crossing bans) and refuses the road past them (<see cref="RoadRefusal"/>, which refuses every other road).
    /// Where what the road writes is known (<paramref name="written"/>, <see cref="Written"/>), each structure's pieces are
    /// solved on the ground as it will stand once the road is stored (RoadTerrainModifier.AsStored), as <see cref="WithNoKit"/>
    /// reads it: the final check's and the crossing bans' alike, one reader.
    /// </summary>
    internal static RoadCrossing? InAnotherLocation(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings, out Vector2 at, out string why,
        ManualAppend? written = null)
    {
        at = default; why = "";
        WorldGenerator? world = GenerationWorld ?? WorldGenerator.instance;
        if (world == null) return null;
        using System.IDisposable? stored = CountedAsStored(written, crossings, ref s_inLocationReads, ref s_inLocationReadsAsStored);
        var own = plans.ConvertAll(p => p.Locations);
        foreach (RoadCrossing c in crossings)
        {
            if (c.Shared || !BridgeStructures.HasDeck(c)) continue;
            if (NetworkGround.StructureRefusal(BridgeStructures.EveryPieceItMayLay(c, world), own, out at) is not string refused) continue;
            why = $"{StructureInAnotherLocation}: {refused}";
            return c;
        }
        return null;
    }

    /// <summary>The refusal of a road whose crossing's structure would stand in another location (<see cref="InAnotherLocation"/>).</summary>
    internal const string StructureInAnotherLocation = "a bridge or span on ground the network keeps out of";

    /// <summary>
    /// The first crossing of a road with a deck that is its own (not one it shares with a road laid before it) and is built
    /// in no kit (<see cref="BridgeStructures.BuiltInNoKit"/>: the support rule breaks the bridge of every kit that fits it),
    /// with where and why; null where none is. A road is never stored over a deck with no structure: generation searches the
    /// road again without it (GenerateRoadCore's crossing bans, as a crossing in another location, <see cref="InAnotherLocation"/>)
    /// and refuses the road past them (<see cref="RoadRefusal"/>, which refuses every other road). Where what the road writes
    /// is known (<paramref name="written"/>, the one list's own assembly, <see cref="Written"/>), each deck is read on the
    /// ground as it will stand once the road is stored (RoadTerrainModifier.AsStored: its earthwork at the deck's ends in
    /// it), the ground its first build reads: the final check's (its <see cref="Written"/>) and the crossing bans' (the same
    /// assembly of the road as it stands when they ask, its location ends not laid yet) alike, one reader.
    /// </summary>
    internal static RoadCrossing? WithNoKit(List<RoadCrossing> crossings, out Vector2 at, out string why, ManualAppend? written = null)
    {
        at = default; why = "";
        WorldGenerator? world = GenerationWorld ?? WorldGenerator.instance;
        if (world == null || !HasOwnDeck(crossings)) return null;
        using System.IDisposable? stored = CountedAsStored(written, crossings, ref s_noKitReads, ref s_noKitReadsAsStored);
        foreach (RoadCrossing c in crossings)
        {
            if (c.Shared || !BridgeStructures.BuiltInNoKit(c, world)) continue;
            at = c.Center;
            why = FormattableString.Invariant($"{DeckWithNoKit}: ({c.FromBank.x:F1}, {c.FromBank.y:F1}) -> ({c.ToBank.x:F1}, {c.ToBank.y:F1})");
            return c;
        }
        return null;
    }

    /// <summary>Whether a road has a crossing with a deck that is its own (not one it shares with a road laid before it): what
    /// <see cref="WithNoKit"/> reads, and nothing to read without one.</summary>
    private static bool HasOwnDeck(List<RoadCrossing> crossings) => crossings.Exists(c => !c.Shared && BridgeStructures.HasDeck(c));

    /// <summary>What a road writes as the crossing bans read it (GenerateRoadCore: <see cref="BuiltRefusal"/>,
    /// <see cref="InAnotherLocation"/>, <see cref="WithNoKit"/>): the one assembly (<see cref="Written"/>) of the road as it stands
    /// when they ask, its location ends not laid yet; counted.</summary>
    private static ManualAppend BanWritten(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings, IReadOnlyList<RoadStairSpan> stairs)
    {
        System.Threading.Interlocked.Increment(ref s_noKitBanWritten);
        return Written(plans, crossings, stairs, new(), new(), new()).Footprint;
    }

    /// <summary>The ground a crossing check reads, for a road with a deck of its own (<see cref="HasOwnDeck"/>, counted in
    /// <paramref name="asked"/>): with <paramref name="written"/>, as the road would be stored (RoadTerrainModifier.AsStored
    /// over its written points and the other roads it raises, counted in <paramref name="asStored"/>), until disposed; else
    /// the network as it stands (null).</summary>
    private static System.IDisposable? CountedAsStored(ManualAppend? written, List<RoadCrossing> crossings, ref int asked, ref int asStored)
    {
        if (!HasOwnDeck(crossings)) return null;
        System.Threading.Interlocked.Increment(ref asked);
        if (written == null) return null;
        System.Threading.Interlocked.Increment(ref asStored);
        return AsWritten(written);
    }

    /// <summary>The ground as the road <paramref name="written"/> would leave it once stored, until disposed
    /// (RoadTerrainModifier.AsStored): its points, the roads it raises in their order, its staircases' walks, and its landing's
    /// edits (the cuts it takes back, the staircase it cuts back). What every check of a road before it is stored reads.</summary>
    internal static System.IDisposable AsWritten(ManualAppend written) =>
        RoadTerrainModifier.AsStored(WrittenPoints(written), written.Ground.Raises(), WrittenRuns(written), written.Landing);

    /// <summary>The staircases a road would store (<paramref name="written"/>, <see cref="Footprint"/>), whose walks floor its
    /// cuts once they are (StairStructures.CutFloor): what the ground as the road would be stored reads with its points.</summary>
    private static IEnumerable<StairRun> WrittenRuns(ManualAppend written) => written.Runs.Select(r => r.run);

    private static int s_noKitReads, s_noKitReadsAsStored, s_noKitBanWritten, s_builtReads, s_builtReadsAsStored, s_inLocationReads, s_inLocationReadsAsStored;
    /// <summary>Crossing verdict reads, including those on the road's written ground and the assemblies made by bans.
    /// Shared by the timing summary and the checks of those callers.</summary>
    internal readonly struct CrossingReadCounts
    {
        internal readonly (int asked, int asStored) NoKit, Built, InLocation;
        internal readonly int BanAssemblies;
        internal CrossingReadCounts((int, int) noKit, (int, int) built, (int, int) inLocation, int banAssemblies)
        { NoKit = noKit; Built = built; InLocation = inLocation; BanAssemblies = banAssemblies; }
        public override string ToString() =>
            $"no-kit verdicts asked {NoKit.asked} ({NoKit.asStored} on the road's written ground), built-road verdicts {Built.asked} ({Built.asStored}), another-location verdicts {InLocation.asked} ({InLocation.asStored}), {BanAssemblies} assemblies of what a road writes the crossing bans made";
    }

    internal static CrossingReadCounts CrossingReads => new(
        (s_noKitReads, s_noKitReadsAsStored), (s_builtReads, s_builtReadsAsStored), (s_inLocationReads, s_inLocationReadsAsStored), s_noKitBanWritten);

    /// <summary>The refusal of a road whose crossing's deck is built in no kit (<see cref="WithNoKit"/>).</summary>
    internal const string DeckWithNoKit = "a bridge or span no kit stands in (the support rule breaks every kit that fits)";

    /// <summary>The refusal of a road whose write would change the ground under a structure another road planned
    /// (<see cref="UnderAPlannedStructure"/>).</summary>
    internal const string GroundUnderAStructure = "it would change the ground under another road's bridge or staircase";

    /// <summary>The refusal of a road whose write would reach ground not known under a structure another road planned
    /// (<see cref="UnderAPlannedStructure"/>): a location's ground not read yet, which a road never writes (roads stop at the edge
    /// of locations not placed first), so said as an error.</summary>
    internal const string UnknownGroundUnderAStructure = "it would write ground not known under another road's bridge or staircase";

    /// <summary>
    /// Why a road may not be stored for the ground it would change under a structure another road planned, and where: the
    /// keep-clear of the ground under the network's structures (docs/OWNERS.md). A structure is planned and its kit saved when
    /// its road is stored, but laid only when its first zone is built; a later road that cut or filled under it meanwhile
    /// left it to be built on changed ground (refused at build, its road ending at a gap). So no later road changes a vertex
    /// one of them stands on (<see cref="PlannedGround"/>): every bridge, ford span and viaduct of the network in every kit it
    /// may be laid with, and every staircase. What the road writes is what it would store (<paramref name="written"/>, the
    /// one assembly of it, <see cref="Footprint"/>: its roads, junction fills, staircases' start pads, mounds and ways into its
    /// locations, its staircases' and crossings' cuts and the other roads it raises), read by the terrain writer's own verdict
    /// on those points alone (RoadTerrainModifier.Unwritten.Changes) over the ground as it stands. Not another road's: a crossing
    /// the road shares or rides (one of its <paramref name="crossings"/> on the same banks, RoadCrossing.SameBanks), a
    /// staircase it walks (an existing one of its <paramref name="stairs"/>) or lands on (<paramref name="landsOn"/>). Null where
    /// it changes none; else the vertex in <paramref name="at"/>. Generation searches a road again round it
    /// (<see cref="NearAPlannedStructure"/>, with the earthwork) and refuses what is left, as every other road is refused, by the
    /// one list (<see cref="RoadRefusal"/>).
    /// </summary>
    internal static string? UnderAPlannedStructure(ManualAppend written, IReadOnlyList<RoadCrossing> crossings, IReadOnlyList<RoadStairSpan> stairs,
        out Vector2 at, StairRun? landsOn = null) => UnderAPlannedStructure(written, crossings, stairs, out at, out _, landsOn, null);

    /// <summary><see cref="UnderAPlannedStructure(ManualAppend, IReadOnlyList{RoadCrossing}, IReadOnlyList{RoadStairSpan}, out Vector2, StairRun?)"/>,
    /// with why (<paramref name="reason"/>): <see cref="GroundUnderAStructure"/>, or where the road writes ground not known
    /// under one (a location's ground not read yet: RoadTerrainModifier.Unwritten.Verdict null)
    /// <see cref="UnknownGroundUnderAStructure"/>, refused as a road is refused for that structure, and said as an error
    /// naming the structure, the location and the road (<paramref name="road"/>): roads stop at the edge of locations not placed
    /// first, so it never writes there.</summary>
    internal static string? UnderAPlannedStructure(ManualAppend written, IReadOnlyList<RoadCrossing> crossings, IReadOnlyList<RoadStairSpan> stairs,
        out Vector2 at, out string reason, StairRun? landsOn, string? road)
    {
        at = default;
        reason = GroundUnderAStructure;
        var unresolved = new List<(Vector2 vertex, string what)>();
        foreach (var (vertex, what) in ChangedUnderStructures(WrittenPoints(written), written.Ground.Raises(), crossings, stairs, landsOn, all: false,
                     runs: WrittenRuns(written), unresolved: unresolved, landing: written.Landing))
        {
            at = vertex;
            return $"{GroundUnderAStructure}: the {what}";
        }
        if (unresolved.Count == 0) return null;
        var (unknownAt, structure) = unresolved[0];
        at = unknownAt;
        reason = UnknownGroundUnderAStructure;
        string location = RoadLocationProtection.NearestFootprint(unknownAt, 0f, out RoadLocationProtection.Footprint f)
            ? FormattableString.Invariant($"({f.Centre.x:F1}, {f.Centre.y:F1})") : "(none found)";
        Log.LogError(FormattableString.Invariant(
            $"[KEEPCLEAR] road {road ?? "(unknown)"} would write ground not known at ({unknownAt.x:F0}, {unknownAt.y:F0}) under the {structure}, in the location at {location}, its ground not read: refused; a road stops at the edge of a location not placed first, so this should not happen"));
        return FormattableString.Invariant($"{UnknownGroundUnderAStructure}: the {structure}, in the location at {location}");
    }

    /// <summary>Points of <paramref name="plans"/> whose write would change the ground under a structure another road planned
    /// (<see cref="UnderAPlannedStructure"/>, over the road's own pieces alone, the staircase it would land on left out as
    /// the final check leaves it): the point nearest each such vertex goes in
    /// <paramref name="where"/>, once. What generation's re-routes round deep earthwork go round too, so a road the search led
    /// past a structure at its write's reach is searched again round it rather than refused by the final check.</summary>
    private static int NearAPlannedStructure(List<RoadSpatialGrid.PlannedPath> plans, IReadOnlyList<RoadCrossing> crossings,
        IReadOnlyList<RoadStairSpan> stairs, List<Vector2>? where)
    {
        // Nothing to go round where no structure another road planned stands within the plans' write (the box
        // ChangedUnderStructures reads first), before the network's ride is read.
        WorldGenerator? world = GenerationWorld ?? WorldGenerator.instance;
        if (world == null) return 0;
        Vector2 low = new(float.PositiveInfinity, float.PositiveInfinity), high = new(float.NegativeInfinity, float.NegativeInfinity);
        foreach (RoadSpatialGrid.PlannedPath plan in plans)
            for (int i = 0; i < plan.Points.Count; i++)
            {
                float r = RoadSpatialGrid.ZoneReach(plan.WidthAt(i));
                Vector2 p = plan.Points[i];
                low = new Vector2(Mathf.Min(low.x, p.x - r), Mathf.Min(low.y, p.y - r));
                high = new Vector2(Mathf.Max(high.x, p.x + r), Mathf.Max(high.y, p.y + r));
            }
        bool any = false;
        foreach (var (ground, _) in PlannedStructures(world, crossings, stairs, null))
            any |= ground.Low.x <= high.x && ground.High.x >= low.x && ground.Low.y <= high.y && ground.High.y >= low.y;
        if (!any) return 0;
        // The staircase it would land on, as its final check reads it (MeetingRefusal, as GenerateRoadCore lands a road on a
        // flight): that one is not another road's to keep clear of.
        MeetingRefusal(plans, out _, out RoadStairRuns.FlightLanding? landing, landOnFlight: t_capture == null);
        // Its own pieces, as it would store them (OwnPieces, as Written reads them): a stretch riding a road already laid is
        // not stored, so it writes nothing.
        var points = new List<RoadSpatialGrid.RoadPoint>();
        foreach (RoadSpatialGrid.PlannedPath plan in OwnPieces(plans, StoredCrossings(crossings.ToList()), out _, out _))
            points.AddRange(RoadSpatialGrid.PointsOf(plan, intoLocation: false));
        var nearest = new HashSet<Vector2>();
        foreach (var (vertex, _) in ChangedUnderStructures(points, new RoadSpatialGrid.Raises(new List<(RoadSpatialGrid.RoadPoint, float)>()), crossings, stairs, landing?.Run, all: true))
        {
            Vector2 best = default;
            float bestSq = float.PositiveInfinity;
            foreach (RoadSpatialGrid.PlannedPath plan in plans)
                foreach (Vector2 p in plan.Points)
                    if ((p - vertex).sqrMagnitude < bestSq) { bestSq = (p - vertex).sqrMagnitude; best = p; }
            if (nearest.Add(best)) where?.Add(best);
        }
        return nearest.Count;
    }

    /// <summary>What a road would store as points (<paramref name="written"/>, <see cref="Footprint"/>), each as the terrain
    /// writer reads it once stored: its roads, fills, pads and mounds (RoadSpatialGrid.PointsOf), its ways into its locations as
    /// points into them, its staircases' and crossings' cuts at the widths they are committed at (RoadSpatialGrid.CutPoint,
    /// as <see cref="AppendManualRoad"/> and <see cref="CrossingGround.CommitGround"/> commit them). The other roads' points it
    /// raises are its CrossingGround.Raised.</summary>
    internal static List<RoadSpatialGrid.RoadPoint> WrittenPoints(ManualAppend written)
    {
        var points = new List<RoadSpatialGrid.RoadPoint>();
        foreach (RoadSpatialGrid.PlannedPath plan in written.WithFills) points.AddRange(RoadSpatialGrid.PointsOf(plan, intoLocation: false));
        foreach (RoadSpatialGrid.PlannedPath plan in written.IntoLocation) points.AddRange(RoadSpatialGrid.PointsOf(plan, intoLocation: true));
        foreach (Vector3 cut in written.StairCuts) points.Add(RoadSpatialGrid.CutPoint(cut, StairLayout.CutWidth));
        foreach (Vector3 cut in written.Ground.StairCuts) points.Add(RoadSpatialGrid.CutPoint(cut, StairLayout.CutWidth));
        foreach (Vector3 cut in written.Ground.Cuts) points.Add(RoadSpatialGrid.CutPoint(cut, CrossingGround.CutWidth));
        return points;
    }

    /// <summary>
    /// The vertices of the ground the network's structures stand on (<see cref="PlannedGround"/>) that a road's write would
    /// change (RoadTerrainModifier.Unwritten.Changes: <paramref name="points"/> stored, and the stored points of
    /// <paramref name="raised"/> raised), each with what stands there, in the network's order: the
    /// structures another road planned (<see cref="UnderAPlannedStructure"/>), those the road shares, rides, walks or lands on
    /// left out. Only the first unless <paramref name="all"/>.
    /// </summary>
    private static IEnumerable<(Vector2 vertex, string what)> ChangedUnderStructures(List<RoadSpatialGrid.RoadPoint> points,
        RoadSpatialGrid.Raises raised, IReadOnlyList<RoadCrossing> crossings, IReadOnlyList<RoadStairSpan> stairs,
        StairRun? landsOn, bool all, IEnumerable<StairRun>? runs = null, List<(Vector2 vertex, string what)>? unresolved = null,
        RoadStairRuns.FlightLanding? landing = null)
    {
        WorldGenerator? world = GenerationWorld ?? WorldGenerator.instance;
        // How far each point may write: a road point as far as a zone gathers it (RoadSpatialGrid.ZoneReach, the writer's
        // widest reach), a cut its cut and batter (RoadTerrainModifier.CutReach). A box over them all, then per structure.
        float Reach(in RoadSpatialGrid.RoadPoint rp) => rp.cutOnly ? RoadTerrainModifier.CutReach(rp.w) : RoadSpatialGrid.ZoneReach(rp.w);
        // What its landing changes besides: the stored cuts it takes back (RoadStairRuns.CutsTakenBack), as far as their cut and
        // batter reach, and the walk floor under the pieces it takes away (StairLayout.PieceRun of each, as its take-back reads them).
        var landed = new List<(Vector2 p, float r)>();
        if (landing != null)
        {
            RoadSpatialGrid.CutsTakenBack back = RoadStairRuns.CutsTakenBack(landing);
            int me = CrossIslandReads.Me;
            foreach (Vector2 q in landing.Removed)
            {
                landed.Add((q, StairLayout.PieceRun));
                foreach (RoadSpatialGrid.RoadPoint rp in RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(q.x, 0f, q.y), StairLayout.PieceRun))
                    if (back.Takes(rp, me)) landed.Add((rp.p, Reach(rp)));
            }
        }
        if (world == null || points.Count + raised.Count + landed.Count == 0) yield break;
        Vector2 low = new(float.PositiveInfinity, float.PositiveInfinity), high = new(float.NegativeInfinity, float.NegativeInfinity);
        foreach (var (p, r) in points.Concat(raised.Sources).Select(rp => (rp.p, Reach(rp))).Concat(landed))
        {
            low = new Vector2(Mathf.Min(low.x, p.x - r), Mathf.Min(low.y, p.y - r));
            high = new Vector2(Mathf.Max(high.x, p.x + r), Mathf.Max(high.y, p.y + r));
        }
        var near = new List<(PlannedGround ground, string what)>();
        foreach (var (ground, what) in PlannedStructures(world, crossings, stairs, landsOn))
            if (ground.Low.x <= high.x && ground.High.x >= low.x && ground.Low.y <= high.y && ground.High.y >= low.y) near.Add((ground, what));
        if (near.Count == 0) yield break;
        // The points that may reach one of them, read by the writer as it would read them stored.
        var reaching = new List<RoadSpatialGrid.RoadPoint>();
        foreach (RoadSpatialGrid.RoadPoint rp in points)
        {
            float r = Reach(rp);
            if (near.Exists(n => n.ground.Low.x <= rp.p.x + r && n.ground.High.x >= rp.p.x - r && n.ground.Low.y <= rp.p.y + r && n.ground.High.y >= rp.p.y - r))
                reaching.Add(rp);
        }
        if (reaching.Count + raised.Count + landed.Count == 0) yield break;
        var unwritten = new RoadTerrainModifier.Unwritten(reaching, raised, world, runs, landing);
        Func<int, int, float> natural = (x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world);
        foreach (var (ground, what) in near)
            foreach ((int x, int z) in ground.Vertices)
            {
                if (x < low.x || x > high.x || z < low.y || z > high.y || !unwritten.Changes(x, z, natural)) continue;
                yield return (new Vector2(x, z), what);
                if (!all) yield break;
            }
        // Ground not known under a structure (a location's not read yet) is no verdict, never "no change": where the road
        // writes there, it is unresolved, and the road is refused for it (UnknownGroundUnderAStructure; the structure lays
        // nothing at its build, StructureGround.NotLaid).
        if (unresolved == null) yield break;
        foreach (var (ground, what) in near)
            foreach ((int x, int z) in ground.Unknown)
                if (x >= low.x && x <= high.x && z >= low.y && z <= high.y && unwritten.Verdict(x, z, natural) == null)
                {
                    unresolved.Add((new Vector2(x, z), what));
                    break;
                }
    }

    /// <summary>
    /// The ground one structure of the network stands on: the whole-metre vertices its pieces' ground is saved from
    /// (StructureGround.Measure: within StructureGround.HeldReach across and StructureGround.HeldDepth of each piece but its
    /// debris and flair), on the finished ground as the network stood when it was first asked of (generation asks of each
    /// before any later road on its island is stored), and the box round them. A bridge's over every piece it may be laid with
    /// (BridgeStructures.EveryPieceItMayLay: its kit is chosen only as it is first built), a staircase's over its pieces.
    /// Whether it is <see cref="Complete"/>: no vertex under its pieces not known (StructureGround.FirstOnUnknownGround, a
    /// location's ground not read yet, which Measure leaves out and under which a kit lays nothing).
    /// </summary>
    private sealed class PlannedGround
    {
        public readonly List<(int x, int z)> Vertices = new();
        /// <summary>The vertices under its pieces whose ground is not known (StructureGround.UnknownUnder): no verdict there
        /// (<see cref="ChangedUnderStructures"/>); the structure lays nothing at its build (StructureGround.NotLaid).</summary>
        public readonly List<(int x, int z)> Unknown = new();
        public Vector2 Low = new(float.PositiveInfinity, float.PositiveInfinity), High = new(float.NegativeInfinity, float.NegativeInfinity);
        /// <summary>Measured on ground all known: kept (<see cref="GroundOf"/>). One that is not leaves out what it could not
        /// read, so it is measured again until it is.</summary>
        public readonly bool Complete;

        public PlannedGround(IEnumerable<StructurePiece> pieces, WorldGenerator world)
        {
            var laid = new List<StructurePiece>(pieces);
            Func<int, int, float> finished = StructureGround.Vertices(world);
            Complete = StructureGround.FirstOnUnknownGround(laid, finished) == null;
            void Bound(int x, int z)
            {
                Low = new Vector2(Mathf.Min(Low.x, x), Mathf.Min(Low.y, z));
                High = new Vector2(Mathf.Max(High.x, x), Mathf.Max(High.y, z));
            }
            foreach (GroundPin pin in StructureGround.Measure(laid, finished))
            {
                Vertices.Add((pin.X, pin.Z));
                Bound(pin.X, pin.Z);
            }
            if (Complete) return;
            var unknown = new HashSet<(int, int)>();
            foreach ((_, int x, int z) in StructureGround.UnknownUnder(laid, finished))
                if (unknown.Add((x, z))) { Unknown.Add((x, z)); Bound(x, z); }
        }
    }

    /// <summary>Each structure's ground (<see cref="PlannedGround"/>), measured once it is complete: by the crossing or staircase
    /// run itself, so the network as it now stands (a generation's, a loaded one's after a reload, a staircase cut back) is
    /// what is read, and a structure gone from it is forgotten with it.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, PlannedGround> s_plannedGround = new();

    /// <summary>A structure's ground (<see cref="PlannedGround"/>): the one kept for it, else measured now
    /// (<paramref name="measure"/>) and kept only where it is complete. One measured on ground not known (a location's not read
    /// yet) stays unresolved: it protects what it read, and is measured again at the next ask, until that ground is read.</summary>
    private static PlannedGround GroundOf(object structure, Func<PlannedGround> measure)
    {
        if (s_plannedGround.TryGetValue(structure, out PlannedGround? kept)) return kept;
        PlannedGround now = measure();
        return now.Complete ? s_plannedGround.GetValue(structure, _ => now) : now;
    }

    /// <summary>The network's structures this thread sees (CrossingsSnapshot, StairStructures.Runs: on an island worker its own
    /// island's) with their ground, but those the road shares, rides, walks or lands on (<see cref="UnderAPlannedStructure"/>).</summary>
    private static IEnumerable<(PlannedGround ground, string what)> PlannedStructures(WorldGenerator world, IReadOnlyList<RoadCrossing> crossings,
        IReadOnlyList<RoadStairSpan> stairs, StairRun? landsOn)
    {
        foreach (RoadCrossing c in CrossingsSnapshot())
        {
            if (!BridgeStructures.HasDeck(c)) continue;
            bool own = false;
            foreach (RoadCrossing mine in crossings) own |= RoadCrossing.SameBanks(c, mine);
            if (own) continue;
            PlannedGround ground = GroundOf(c, () => new PlannedGround(BridgeStructures.EveryPieceItMayLay(c, world), world));
            yield return (ground, FormattableString.Invariant($"{(IsViaduct(c) ? "viaduct" : c.Kind == CrossingKind.Ford ? "ford span" : "bridge")} ({c.FromBank.x:F1}, {c.FromBank.y:F1}) -> ({c.ToBank.x:F1}, {c.ToBank.y:F1})"));
        }
        foreach (StairRun run in StairStructures.Runs)
        {
            if (ReferenceEquals(run, landsOn)) continue;
            StairPlan? laid = RoadStairRuns.LaidPlan(run);
            bool walked = false;
            foreach (RoadStairSpan st in stairs) walked |= st.Existing && laid != null && ReferenceEquals(st.Plan, laid);
            if (walked) continue;
            PlannedGround ground = GroundOf(run, () => new PlannedGround(run.Pieces, world));
            yield return (ground, FormattableString.Invariant($"staircase ({run.From.x:F1}, {run.From.y:F1}) -> ({run.To.x:F1}, {run.To.y:F1})"));
        }
    }

    /// <summary>
    /// Whether a road, as planned, runs under the deck of a structure laid before it (StructureEnds.UnderTheDeck), of every
    /// kind alike (a viaduct, a river bridge, a ford span): the other half of the built-result check (<see cref="BuiltRefusal"/>,
    /// which reads only the road's own crossings). The search keeps out from under a valley viaduct's deck
    /// (DvergrViaduct.Standing) but for its two bank cells, where a deck's end overhangs; a road through there, or under any
    /// other laid deck, is refused.
    /// </summary>
    internal static string? UnderALaidDeck(List<RoadSpatialGrid.PlannedPath> plans, out Vector2 at)
    {
        at = default;
        List<LaidDeck> decks;
        lock (m_laidDecks) { if (m_laidDecks.Count == 0) return null; decks = new(m_laidDecks); }
        int me = CrossIslandReads.Me;
        foreach (var d in decks)
            foreach (var (p, h, w) in PlanPointsNear(plans, d.Centre, d.Reach))
                if (StructureEnds.UnderTheDeck(p, h, w, d.Near, d.Far, d.Deck, d.Half))
                {
                    // Another island's structure is not this island's network (CrossIslandReads).
                    if (!CrossIslandReads.Sees(me, d.Island))
                    { CrossIslandReads.Skipped(d.Viaduct ? CrossIslandReads.Kind.Viaducts : CrossIslandReads.Kind.Crossings, d.Island, p); break; }
                    at = p; return d.Viaduct ? "passes under a viaduct's deck" : d.Kind == CrossingKind.Ford ? "passes under a ford span's deck" : "passes under a bridge's deck";
                }
        return null;
    }

    private static IEnumerable<(Vector2 p, float h, float w)> PlanPointsNear(List<RoadSpatialGrid.PlannedPath> plans, Vector2 at, float reach)
    {
        float r2 = reach * reach;
        foreach (var plan in plans)
            for (int i = 0; i < plan.Points.Count; i++)
                if ((plan.Points[i] - at).sqrMagnitude <= r2) yield return (plan.Points[i], plan.Heights[i], plan.Width);
    }

    /// <summary>
    /// What later roads keep clear of, for each structure in the network (a shared one is the other road's): every deck that
    /// meets the road (<see cref="MeetsTheRoad"/>: a viaduct, a bridge or a ford's span in any kit), read by its own kit's
    /// ends, height and half width as <see cref="BuiltRefusal"/> reads it (<see cref="UnderALaidDeck"/>); and for a viaduct its
    /// ends and clearance (DvergrViaduct.MarkLaid) and, for a valley's, the cells under it (DvergrViaduct.MarkStanding). A generated road and a manual one as each is laid, and a saved network's crossings as it
    /// loads (<see cref="TryLoadGlobalRoadData"/>), alike: a reload keeps every road out of where the session that laid them
    /// kept it.
    /// </summary>
    private static void KeepClearOfViaducts(IEnumerable<RoadCrossing> crossings)
    {
        foreach (var c in crossings)
        {
            if (c.Shared) continue;
            bool viaduct = IsViaduct(c);
            if (viaduct) DvergrViaduct.MarkLaid(c);
            else if (!MeetsTheRoad(c)) continue;
            var (near, far) = BridgeStructures.Ends(c, GenerationWorld);
            float half = BridgeStructures.DeckHalfWidth(c, GenerationWorld);
            lock (m_laidDecks) m_laidDecks.Add(new LaidDeck(near, far, BridgeStructures.DeckHeight(c, GenerationWorld), half, c.Center,
                c.Width * 0.5f + half + 4f, CrossIslandReads.Tag, viaduct, c.Kind));
            IslandNetworkChanged();
        }
        foreach (var c in crossings)
            if (c.ValleyJump && !c.Shared) DvergrViaduct.MarkStanding(c);
    }

    /// <summary>A road's viaducts as it is laid: kept clear of (<see cref="KeepClearOfViaducts"/>), and its valley viaducts
    /// counted and said.</summary>
    private static void LayViaducts(List<RoadCrossing> crossings, string? label)
    {
        KeepClearOfViaducts(crossings);
        foreach (var c in crossings)
        {
            if (!c.ValleyJump || c.Shared) continue;
            System.Threading.Interlocked.Increment(ref m_viaductsLaid);
            if (RoadsLog) Log.LogInfo($"[VIADUCT] road {label ?? "(unlabelled)"}: {DvergrViaduct.Describe(c, GenerationWorld)}");
        }
    }

    /// <summary>Nothing kept clear of: no viaduct's ends, deck or cells (<see cref="KeepClearOfViaducts"/>), for a new
    /// network and before a saved one's are put back.</summary>
    private static void ForgetViaducts()
    {
        DvergrViaduct.ResetStanding();
        lock (m_laidDecks) m_laidDecks.Clear();
    }

    /// <summary>A deck in the network, of any kind, as <see cref="UnderALaidDeck"/> reads it: its ends, height and half width
    /// in its own kit, where to look for road under it, the island that laid it, and what it is (for the refusal's words).</summary>
    private readonly record struct LaidDeck(StructureEnds.End Near, StructureEnds.End Far, float Deck, float Half, Vector2 Centre, float Reach,
        int Island, bool Viaduct, CrossingKind Kind);

    /// <summary>The decks in the network, every kind, for <see cref="UnderALaidDeck"/>.</summary>
    private static readonly List<LaidDeck> m_laidDecks = new();

    /// <summary>A valley viaduct on the road whose tallest pier over the finished ground (the road built under it, this
    /// road's plans and the network so far) is under the floor (DvergrViaduct.MinViaductPier), or null.</summary>
    internal static RoadCrossing? UnderTheFloor(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings)
    {
        foreach (RoadCrossing c in crossings)
        {
            if (!c.ValleyJump || c.Shared || !IsViaduct(c)) continue;
            if (DvergrViaduct.TallestPierOnFinishedGround(c, GenerationWorld, plans) < DvergrViaduct.MinViaductPier - 1e-3f) return c;
        }
        return null;
    }

    private static int m_inLocationSearchedRound, m_inLocationRefused, m_noKitSearchedRound, m_noKitRefused, m_builtSearchedRound, m_builtRefused;
    /// <summary>Bridges and spans (no valley viaduct) the built road did not allow, searched again without, and roads refused
    /// with a crossing of any kind the built road does not allow past those searches (<see cref="BuiltRefusal"/>).</summary>
    public static (int searchedRound, int refused) CrossingsTheBuiltRoadRefused => (m_builtSearchedRound, m_builtRefused);
    /// <summary>Roads first planned changing the ground under a structure another road planned (<see cref="NearAPlannedStructure"/>,
    /// counted with the roads log), and roads refused for it as finished (<see cref="UnderAPlannedStructure"/>).</summary>
    private static int m_keptClearSearched, m_keptClearRefused, m_unknownGroundRefused;
    private static int m_viaductsRefusedAtEnds, m_crossingsUnmetSearchedRound, m_bridgeEndsUnmet, m_endEarthworks, m_endPads, m_endShared, m_endStands, m_endSteps, m_endFlat, m_endOneStep, m_endMoreSteps,
        m_pairsStepped, m_pairsFlush, m_pairsOneSided;
    /// <summary>Road searches made again without a crossing with a deck built in no kit (<see cref="WithNoKit"/>), and road
    /// attempts refused with one, their searches round it spent.</summary>
    public static (int searchedRound, int refused) DecksWithNoKit => (m_noKitSearchedRound, m_noKitRefused);
    /// <summary>Road attempts refused with a crossing with an end no way of meeting reaches, its searches round it spent.</summary>
    public static int BridgeEndsUnmet => m_bridgeEndsUnmet;
    /// <summary>Searches again round a crossing, past the staircase retries.</summary>
    internal const int CrossingRetries = 3;
    /// <summary>Told of every road piece a generated road stores, with the road's label: for offline maps and the
    /// viaduct tests. Called from island workers.</summary>
    public static Action<RoadSpatialGrid.PlannedPath, string?>? PieceCommitted;

    /// <summary>What a road's committed piece is handed to: the debug record of its owner (<see cref="RoadOwners"/>), then
    /// <see cref="PieceCommitted"/>.</summary>
    private static Action<RoadSpatialGrid.PlannedPath> Committed(string? label, string kind) => plan =>
    {
        RoadOwners.Record(label, kind, plan.Points);
        PieceCommitted?.Invoke(plan, label);
    };
    private static int m_viaductsLaid;


    /// <summary>The viaduct the last plan on this thread was refused with for a stub of road between it and another
    /// structure (StructureEnds.IsStub), or null.</summary>
    [System.ThreadStatic] internal static RoadCrossing? LastStubBetween;

    /// <summary>Land pieces stand no lower than <see cref="RoadConstants.DryRoadFloor"/>.</summary>
    private const float LandFloor = RoadConstants.DryRoadFloor;

    /// <summary>The floor a raised ford's road stands on: dry land's over standing water, a landing's over a river.</summary>
    internal static float RaisedFordFloor(RoadCrossing c) => c.Shallow ? RoadConstants.DryRoadFloor : RoadWater.LandingFloor;

    /// <summary>The height a raised ford (<paramref name="c"/>, a road's own, not shared) and the road on land meet at on its
    /// bank <paramref name="bank"/>: the ground there, no lower than the ford's raised floor (<see cref="RaisedFordFloor"/>).
    /// Both pieces are held there, so the road on land climbs to the raised floor within the cap or is refused, never left
    /// a step below it at its own floor (BRIEF-RULES 17). Null for any other crossing.</summary>
    internal static float? RaisedBankHeight(RoadCrossing? c, Vector2 bank) =>
        c != null && c.Kind == CrossingKind.Ford && c.Style == FordStyle.Raise && !c.Shared
            ? Mathf.Max(BiomeBlendedHeight.GetBlendedHeight(bank.x, bank.y, GenerationWorld), RaisedFordFloor(c))
            : null;

    /// <summary>Whether <paramref name="c"/> is a raised ford of this road with a bank on the levelled stretch of one of
    /// <paramref name="ends"/> (within <see cref="StructureEnds.ApronLength"/> of its foot): that stretch is planned over it
    /// (PlanPieces).</summary>
    internal static bool RaisedOnALevelledEnd(RoadCrossing c, IEnumerable<(StructureEnds.End near, StructureEnds.End far)> ends)
    {
        if (c.Kind != CrossingKind.Ford || c.Style != FordStyle.Raise || c.Shared) return false;
        foreach (var (near, far) in ends)
            if (OnALevelledEnd(c, near) || OnALevelledEnd(c, far)) return true;
        return false;
    }

    /// <summary>Whether a bank of <paramref name="c"/>, a crossing that is road, stands on the levelled stretch of
    /// <paramref name="end"/>: within <see cref="StructureEnds.ApronLength"/> of its foot (RoadSpatialGrid.LevelledEnds).</summary>
    private static bool OnALevelledEnd(RoadCrossing c, StructureEnds.End end) =>
        Vector2.Distance(end.Meet, c.FromEnd?.Meet ?? c.FromBank) < StructureEnds.ApronLength
        || Vector2.Distance(end.Meet, c.ToEnd?.Meet ?? c.ToBank) < StructureEnds.ApronLength;

    /// <summary>The refusal of a road with a waded ford on a structure end's levelled stretch (PlanPieces).</summary>
    internal const string WadeOnALevelledEnd = "a wade on a structure's levelled end";

    /// <summary>A staircase's end this far or further from the junction its road ends at is met by road laid to it.</summary>
    internal const float StairEndReach = 0.75f;

    /// <param name="approaches">Whether an approach staircase may be laid up from the road's own start, and its end
    /// (LocationApproaches): those ends, as every staircase's foot the road is held at, are where a staircase starts
    /// (RoadSpatialGrid.StairStarts).</param>
    /// <param name="locations">The locations the road's own ends arrive at (RoadLocationProtection.Targets): its first piece's start
    /// and its last piece's end.</param>
    private static List<RoadSpatialGrid.PlannedPath>? PlanPieces(
        List<Vector2> path, List<RoadCrossing> crossings, float width, float? startGround, float? endGround,
        List<RoadStairSpan>? stairs, (bool start, bool end) approaches, RoadLocationProtection.Targets locations,
        ICollection<RoadCrossing>? failedCrossings)
    {
        // Every piece is worked out before any of it is stored. A piece can be
        // refused - too steep to build - and a road stored up to the river it
        // cannot come back from is a paved stretch ending in open ground.
        // A piece's ends may be held at a height: the road's own ends where they
        // meet a location, and the ends of a staircase, which the road meets level.
        var pieces = new List<(List<Vector2> points, bool followTerrain, float minHeight, float? startHeight, float? endHeight)>();
        var pieceCrossings = new Dictionary<int, (RoadCrossing? from, RoadCrossing? to)>();
        var between = new List<(List<Vector2> points, float from, float to, RoadCrossing? fromCrossing, RoadCrossing? toCrossing)>();
        // A stretch of road to or from a structure's end is planned as two pieces, split where its levelled stretch starts
        // (StructureEnds.ApronLength along the road from the foot, the joining piece beyond it): the road up to it, held there
        // at the meeting height, and the levelled stretch itself (the stairs' rule: the road runs to the structure at its
        // height). One piece, the grade limiter spread the climb to a rim into the apron and tilted it (most viaduct ends a
        // little off level, seven a step or more).
        // The staircase each piece starts from (at its To end) and ends at (its From end), by the piece's index: marked on
        // its plan (RoadSpatialGrid.PlannedPath.StairAtStart, StairAtEnd), the road that meets that staircase's end.
        var startsAtStair = new Dictionary<int, RoadStairSpan>();
        var endsAtStair = new Dictionary<int, RoadStairSpan>();
        // A piece whose start is the end of the piece before it, split from it at a raised ford's far bank (AddLandPieces):
        // held at the height that piece ends at, so the two meet.
        var continues = new HashSet<int>();
        // The raised fords planned as a structure end's land (below), by their banks' path points, at their floors.
        var raisedOnAnEnd = new List<(Vector2 from, Vector2 to, float floor)>();
        void AddLand(List<Vector2> land, float? startH, float? endH, bool startEnd, bool endEnd, RoadStairSpan? fromStair = null, RoadStairSpan? toStair = null,
            RoadCrossing? fromCrossing = null, RoadCrossing? toCrossing = null)
        {
            int first = pieces.Count;
            AddLandPieces(land, startH, endH, startEnd, endEnd, fromCrossing, toCrossing);
            if (pieces.Count == first) return;
            if (fromStair != null) startsAtStair[first] = fromStair;
            if (toStair != null) endsAtStair[pieces.Count - 1] = toStair;
        }
        // A land stretch is planned as up to three pieces: the levelled stretch at a structure's end it starts from (held at
        // its height), the road between, and the levelled stretch at a structure's end it ends at. A raised ford planned
        // with the land (raisedOnAnEnd) keeps its own floor over its banks' range and the levelled stretch it stands on
        // (StructureEnds.ApronLength + JoinOverlap from that end), and only there: the middle piece is split at the ford's
        // far bank, the part past it at the land's floor and held where the part before it ends. The rest of the land, which
        // may run hundreds of metres on, is at the land's floor: a dip there is no causeway for a ford far off.
        void AddLandPieces(List<Vector2> land, float? startH, float? endH, bool startEnd, bool endEnd,
            RoadCrossing? fromCrossing, RoadCrossing? toCrossing)
        {
            int i0 = startEnd && startH.HasValue ? SplitAt(land, fromEnd: false) : -1;
            int i1 = endEnd && endH.HasValue ? SplitAt(land, fromEnd: true) : -1;
            if (i0 >= 0 && i1 >= 0 && i1 <= i0) i0 = i1 = -1;
            int last = land.Count - 1, from = 0, to = last;
            bool levelHead = i0 > 0 && i0 < last;
            if (levelHead) from = i0;
            bool levelTail = i1 > from && i1 < last;
            if (levelTail) to = i1;
            // Each raised ford on this land: the range of land indices at its floor.
            var raised = new List<(int a, int b, float floor)>();
            foreach (var (fordFrom, fordTo, floor) in raisedOnAnEnd)
            {
                int a = land.IndexOf(fordFrom), b = land.IndexOf(fordTo);
                if (a < 0 && b < 0) continue;
                if (a < 0) a = b;
                if (b < 0) b = a;
                if (a > b) (a, b) = (b, a);
                bool onHead = levelHead ? a <= i0 : startEnd && Vector2.Distance(land[0], land[a]) <= StructureEnds.ApronLength + StructureEnds.JoinOverlap;
                bool onTail = levelTail ? b >= i1 : endEnd && Vector2.Distance(land[last], land[b]) <= StructureEnds.ApronLength + StructureEnds.JoinOverlap;
                raised.Add((onHead ? 0 : a, onTail ? last : b, floor));
            }
            float FloorOver(int s, int e)
            {
                float floor = LandFloor;
                foreach (var (a, b, f) in raised)
                    if (a < e && b > s) floor = Mathf.Max(floor, f);
                return floor;
            }
            if (levelHead)
            {
                pieceCrossings[pieces.Count] = (fromCrossing, null);
                pieces.Add((land.GetRange(0, i0 + 1), false, FloorOver(0, i0), startH, startH));
            }
            if (to > from)
            {
                // The middle cut where a raised range ends or starts inside it.
                var cuts = new SortedSet<int> { from, to };
                foreach (var (a, b, _) in raised)
                {
                    if (a > from && a < to) cuts.Add(a);
                    if (b > from && b < to) cuts.Add(b);
                }
                int[] at = cuts.ToArray();
                for (int k = 0; k + 1 < at.Length; k++)
                {
                    int s = at[k], e = at[k + 1];
                    if (k > 0) continues.Add(pieces.Count);
                    pieceCrossings[pieces.Count] = (k == 0 ? fromCrossing : null, k + 2 == at.Length ? toCrossing : null);
                    pieces.Add((land.GetRange(s, e - s + 1), false, FloorOver(s, e), k == 0 ? startH : null, k + 2 == at.Length ? endH : null));
                }
            }
            if (levelTail)
            {
                pieceCrossings[pieces.Count] = (null, toCrossing);
                pieces.Add((land.GetRange(i1, last - i1 + 1), false, FloorOver(i1, last), endH, endH));
            }
        }
        // The index in land of the point StructureEnds.ApronLength + JoinOverlap along it from its start or end, put in where
        // no point stands within 0.3 m of it; -1 where the land is no longer.
        static int SplitAt(List<Vector2> land, bool fromEnd)
        {
            float want = StructureEnds.ApronLength + StructureEnds.JoinOverlap, along = 0f;
            for (int n = 1; n < land.Count; n++)
            {
                int a = fromEnd ? land.Count - n : n - 1, b = fromEnd ? a - 1 : a + 1;
                float run = Vector2.Distance(land[a], land[b]);
                if (along + run < want - 0.3f) { along += run; continue; }
                if (along + run <= want + 0.3f) return b;
                Vector2 at = Vector2.Lerp(land[a], land[b], (want - along) / run);
                int insert = fromEnd ? a : b;
                land.Insert(insert, at);
                return insert;
            }
            return -1;
        }
        LastStubBetween = null;

        // What the road is cut at, in path order: river crossings and staircases. Every crossing's ends are the one owner's
        // (StructureEnds.MeetEnds), which brought the path to them: a structure's at its crossing's FromIndex and ToIndex,
        // where the road ends at the end's foot at its height, its last ApronLength levelled to it
        // (RoadSpatialGrid.LevelledEnds), and runs on under the joining piece (StructureEnds.JoinOverlap), unpainted under stairs; a
        // crossing that is road at its banks.
        var gaps = new List<(int from, int to, Vector2 fromPoint, Vector2 toPoint, Vector2? fromLand, Vector2? toLand, float? fromHeight, float? toHeight, RoadCrossing? crossing, RoadStairSpan? stair)>();
        var levelled = new List<(Vector2 at, float height)>();
        var ups = new Dictionary<RoadCrossing, (StructureEnds.End near, StructureEnds.End far)>();
        foreach (RoadCrossing c in crossings)
        {
            if (MeetsTheRoad(c))
            {
                var (near, far) = BridgeStructures.Ends(c, GenerationWorld);
                ups[c] = (near, far);
                levelled.Add((near.Joint, near.MeetHeight));
                levelled.Add((far.Joint, far.MeetHeight));
                gaps.Add((c.FromIndex, c.ToIndex, near.Meet, far.Meet, null, null, near.MeetHeight, far.MeetHeight, c, null));
            }
            else gaps.Add((c.FromIndex, c.ToIndex, c.FromEnd?.Meet ?? c.FromBank, c.ToEnd?.Meet ?? c.ToBank, c.FromLand, c.ToLand, null, null, c, null));
        }
        if (stairs != null)
            foreach (RoadStairSpan st in stairs)
                gaps.Add((st.FromIndex, st.ToIndex, st.FromPoint, st.ToPoint, null, null, st.FromHeight, st.ToHeight, null, st));
        // A raised ford is road (BridgeStructures.Ends): one with a bank on a structure end's levelled stretch (its last
        // ApronLength to the foot, RoadSpatialGrid.LevelledEnds) is planned as that stretch's land, at its own floor where
        // that is over the land's, so the stretch reaches over it as the end was met (StructureEnds.MeetEnds: a ford that is
        // road at the bound of an end's approach carries it). A piece of its own, its road stood at the raise's floor right
        // under the end, a step or more off it with no stairs.
        // Each by its banks' path points (the land keeps them), at its floor (AddLandPieces).
        foreach (RoadCrossing c in crossings)
            if (RaisedOnALevelledEnd(c, ups.Values))
                raisedOnAnEnd.Add((path[Mathf.Clamp(c.FromIndex, 0, path.Count - 1)], path[Mathf.Clamp(c.ToIndex, 0, path.Count - 1)], RaisedFordFloor(c)));
        if (raisedOnAnEnd.Count > 0) gaps.RemoveAll(g => g.crossing != null && RaisedOnALevelledEnd(g.crossing, ups.Values));
        // A waded ford is the bed's own surface: an end whose levelled stretch reaches one cannot be met level, nor by its
        // stairs. The structure is not built with this road and the road is searched again without it, as with a stub of
        // road between two structures (below); one another road built stands, and the road is refused.
        foreach (RoadCrossing c in crossings)
        {
            if (c.Kind != CrossingKind.Ford || c.Style != FordStyle.Wade || c.Shared) continue;
            foreach (var up in ups)
            {
                RoadCrossing structure = up.Key;
                if (!OnALevelledEnd(c, up.Value.near) && !OnALevelledEnd(c, up.Value.far)) continue;
                RoadSpatialGrid.LastRefusal = WadeOnALevelledEnd;
                if (!structure.Shared) failedCrossings?.Add(structure);
                if (RoadsLog) Log.LogDebug(FormattableString.Invariant($"[BRIDGES] a wade {c.FromBank} -> {c.ToBank} on the levelled end of the crossing {structure.FromBank} -> {structure.ToBank}, which {(structure.Shared ? "stands: the road is refused" : "is not built with this road")}"));
                return null;
            }
        }
        // Stable by start: crossings keep the order Detect gave them.
        gaps = gaps.Select((g, i) => (g, i)).OrderBy(x => x.g.from).ThenBy(x => x.i).Select(x => x.g).ToList();

        if (gaps.Count == 0)
        {
            pieces.Add((path, false, LandFloor, null, null));
        }
        else
        {
            int cursor = 0;
            Vector2? resumeAt = null, resumeLand = null, resumeJoint = null;
            // What a piece runs from and to, where both are structures (a staircase, or a bridge end the owner meets).
            RoadCrossing? resumeCrossing = null, resumeTreatment = null;
            StructureEnds.End? resumeEnd = null;
            float? resumeHeight = null;
            // A staircase ends where its layout ended, up to a few metres from the
            // path point it aimed at: the road ends and starts there instead of
            // running on to that point and doubling back (a hairpin was refused).
            bool resumeOnStair = false;
            RoadStairSpan? resumeStair = null;
            foreach (var gap in gaps)
            {
                // Two crossings on one road can overlap on the path: a bridge's
                // banks walk out to the bank tops and a swamp bridge's on to dry
                // ground, so one crossing's span can reach past the start of the
                // next. A crossing the previous one already spans has nothing left
                // to paint, and one that merely starts inside it has no land in
                // front of it.
                if (gap.to <= cursor)
                    continue;

                List<Vector2> land = gap.from > cursor
                    ? path.GetRange(cursor, gap.from - cursor + 1)
                    : new List<Vector2>();
                // A staircase starts at its own first point, not at the path point it was laid from: that point is dropped
                // where it is still the stretch's last (never a meeting point or apron the stretch was brought to).
                Vector2? stepFrom = gap.crossing == null && gap.from > cursor ? path[gap.from] : null;
                if (resumeLand.HasValue && (land.Count == 0 || Vector2.Distance(resumeLand.Value, land[0]) > 0.5f))
                    land.Insert(0, resumeLand.Value);
                if (resumeOnStair && land.Count > 1) land.RemoveAt(0);
                if (resumeAt.HasValue && (land.Count == 0 || Vector2.Distance(resumeAt.Value, land[0]) > 0.5f))
                    land.Insert(0, resumeAt.Value);
                if (resumeJoint is Vector2 rj) land.Insert(0, rj);
                // A deck end walked in off land: the road runs on over the strip.
                if (gap.fromLand is Vector2 fromLand && land.Count > 0 && Vector2.Distance(fromLand, land[land.Count - 1]) > 0.5f)
                    land.Add(fromLand);
                if (stepFrom is Vector2 sf && land.Count > 1 && land[land.Count - 1] == sf) land.RemoveAt(land.Count - 1);
                Vector2 endAt = gap.fromPoint;
                float? endHeight = gap.fromHeight ?? RaisedBankHeight(gap.crossing, gap.fromPoint);
                if (land.Count > 0 && Vector2.Distance(endAt, land[land.Count - 1]) > 0.5f)
                    land.Add(endAt);
                // On under the joining piece (StructureEnds.JoinOverlap): the road ends under the deck's end module or the
                // bottom step, levelled at the meeting height, so the structure stands on it.
                if (endHeight.HasValue && gap.crossing != null && ups.TryGetValue(gap.crossing, out var joinEnds) && land.Count > 0)
                    land.Add(joinEnds.near.Joint);
                if (land.Count >= 2)
                {
                    RoadCrossing? toCrossing = gap.crossing != null && ups.ContainsKey(gap.crossing) ? gap.crossing : null;
                    if (resumeHeight.HasValue && endHeight.HasValue)
                        between.Add((land, resumeHeight.Value, endHeight.Value, resumeCrossing, toCrossing));
                    AddLand(land, resumeHeight, endHeight, resumeJoint.HasValue, toCrossing != null, resumeStair, gap.stair, resumeTreatment, gap.crossing);
                }

                // A bridge or a spanned ford is left to its pieces: nothing is
                // leveled or painted over the water. Nor on a staircase.
                // A shared crossing is carried by the road that built it: only the land up to its banks is ours.
                RoadCrossing? crossing = gap.crossing;
                if (crossing != null && crossing.Kind == CrossingKind.Ford && crossing.Style != FordStyle.Span && !crossing.Shared)
                {
                    pieceCrossings[pieces.Count] = (crossing, crossing);
                    List<Vector2> ford = new() { gap.fromPoint };
                    for (int k = Mathf.Max(crossing.FromIndex + 1, cursor + 1); k < crossing.ToIndex; k++)
                        ford.Add(path[k]);
                    ford.Add(gap.toPoint);
                    if (ford.Count >= 2 && crossing.Style == FordStyle.Wade)
                    {
                        // The crossing owner supplies the waded surface, including a standing pool's submerged floor.
                        var (followTerrain, floor) = RoadCrossingDetector.WadeSurface(crossing.Shallow, RoadConstants.SeaLevel - crossing.RiverbedHeight);
                        pieces.Add((ford, followTerrain, floor, null, null));
                    }
                    else if (ford.Count >= 2)
                        pieces.Add((ford, false, RaisedFordFloor(crossing), RaisedBankHeight(crossing, gap.fromPoint), RaisedBankHeight(crossing, gap.toPoint)));
                }

                resumeTreatment = gap.crossing;
                resumeAt = gap.toPoint;
                resumeLand = gap.toLand;
                resumeHeight = gap.toHeight ?? RaisedBankHeight(gap.crossing, gap.toPoint);
                resumeCrossing = gap.crossing != null && ups.ContainsKey(gap.crossing) ? gap.crossing : null;
                resumeOnStair = gap.crossing == null;
                resumeStair = gap.stair;
                resumeEnd = gap.crossing != null && ups.TryGetValue(gap.crossing, out var farEnds) ? farEnds.far : null;
                resumeJoint = resumeEnd?.Joint;
                cursor = gap.to;
            }

            int tailStart = Mathf.Min(cursor, path.Count - 1);
            List<Vector2> tail = path.GetRange(tailStart, path.Count - tailStart);
            // A staircase at the path's last index ends the road there: no stub of road from its end back to the path point
            // (short, possibly steep or doubled back, it could cost the road its stairs). Kept only where it
            // is all the road there is.
            bool endsOnStair = resumeOnStair && tail.Count == 1 && pieces.Count > 0;
            // But where the road ends on another road (a junction) and the staircase's layout ended short of it, the road is
            // laid from the staircase's end to the junction: the staircase meets the road it joins. Laid like any road, it
            // is held to the junction's height (HoldToNetwork).
            if (endsOnStair && resumeAt is Vector2 stairEnd && Vector2.Distance(stairEnd, tail[0]) > StairEndReach
                && RoadSpatialGrid.TryGetRoadHeightWithin(tail[0], RoadSnap, out _))
                endsOnStair = false;
            if (resumeOnStair && tail.Count > 1) tail.RemoveAt(0);
            if (resumeLand.HasValue && Vector2.Distance(resumeLand.Value, tail[0]) > 0.5f)
                tail.Insert(0, resumeLand.Value);
            if (resumeAt.HasValue && Vector2.Distance(resumeAt.Value, tail[0]) > 0.5f)
                tail.Insert(0, resumeAt.Value);
            if (resumeJoint is Vector2 tj) tail.Insert(0, tj);
            if (tail.Count >= 2 && !endsOnStair)
                AddLand(tail, resumeHeight, null, resumeJoint.HasValue, false, resumeStair, fromCrossing: resumeTreatment);
        }

        if (pieces.Count == 0)
            return null;

        // Between two structures (a crossing's end and a staircase, or two crossings' ends), no stub of road. A stretch
        // shorter than StructureEnds.StubLength is one level landing at the joining height: where both share it
        // (StructureEnds.SharesLanding) it is that; further apart the road is not planned so (the stretch is levelled to the crossing's end all the
        // way, and a staircase's foot is always at the road's height where it starts). A crossing this road builds is not
        // built with it, and the road is searched again without it (GenerateRoadCore); another road's is met as it stands.
        // Two staircases of one road keep their own meeting (StairChain).
        foreach (var (points, fromH, toH, from, to) in between)
        {
            RoadCrossing? structure = from ?? to;
            if (structure == null) continue;
            if (StructureEnds.IsStub(points, fromH, toH) is string stub)
            {
                RoadSpatialGrid.LastRefusal = "a stub of road between two structures";
                RoadCrossing? own = from != null && !from.Shared ? from : to != null && !to.Shared ? to : null;
                LastStubBetween = own;
                if (from != null && !from.Shared) failedCrossings?.Add(from);
                if (to != null && !to.Shared && to != from) failedCrossings?.Add(to);
                if (RoadsLog) Log.LogDebug(FormattableString.Invariant($"[BRIDGES] {stub} at ({points[0].x:F1}, {points[0].y:F1}); the crossing {structure.FromBank} -> {structure.ToBank} {(own != null ? "is not built with this road" : "stands: the road is refused")}"));
                return null;
            }
        }

        // Only the road's own two ends meet a location. Every join in between
        // is a river bank, which meets the natural ground as it always did, or
        // the end of a staircase, which the road meets at the stair's height.
        var plans = new List<RoadSpatialGrid.PlannedPath>(pieces.Count);
        System.Func<Vector2, float>? ground = StripGround(crossings, width);
        // Each structure's end the road was brought to (StructureEnds.MeetEnds) stays on it: the point under its joining
        // piece, its foot and its apron, on its line; and its last ApronLength to each is levelled to it.
        List<Vector2>? feet = null;
        var ends = new List<StructureEnds.End>();
        foreach (var (near, far) in ups.Values)
            foreach (StructureEnds.End end in new[] { near, far })
            {
                (feet ??= new List<Vector2>()).AddRange(new[] { end.Joint, end.Meet, end.Meet - end.Up * StructureEnds.PadTurn, end.Meet - end.Up * (StructureEnds.ApronLength * 0.5f), end.Apron });
                ends.Add(end);
            }
        RoadSpatialGrid.HeldPoints = feet;
        RoadSpatialGrid.LevelledEnds = levelled.Count > 0 ? levelled : null;
        // Where a staircase starts: each staircase's foot (its lower end), which a piece is held at, and the top of each it lays.
        var stairFeet = new List<Vector2>();
        if (stairs != null)
            foreach (RoadStairSpan st in stairs)
            {
                stairFeet.Add(st.FromHeight <= st.ToHeight ? st.FromPoint : st.ToPoint);
                // And its top where it lays its own (a pad is laid there, level at the top's height, RoadStairRuns.StartPads):
                // the road starting again there lands on it as at a foot.
                if (!st.Existing) stairFeet.Add(st.FromHeight <= st.ToHeight ? st.ToPoint : st.FromPoint);
            }
        bool AtAFoot(Vector2 p) => stairFeet.Exists(f => (f - p).sqrMagnitude < 0.25f);
        try
        {
            for (int i = 0; i < pieces.Count; i++)
            {
                // The road's own ends held at a location's ground end on a level landing (RoadSpatialGrid.LocationEnds).
                RoadSpatialGrid.LocationEnds = (i == 0 && pieces[i].startHeight == null && startGround.HasValue,
                    i == pieces.Count - 1 && pieces[i].endHeight == null && endGround.HasValue);
                // And so does an end held at a staircase's foot, where the landing fits (else within the cap into it); there, and
                // at an end an approach may start from, no tight switchback narrows the road (RoadSpatialGrid.StairStarts).
                var points = pieces[i].points;
                RoadSpatialGrid.StairStarts = (
                    pieces[i].startHeight != null && AtAFoot(points[0]) || i == 0 && pieces[i].startHeight == null && approaches.start,
                    pieces[i].endHeight != null && AtAFoot(points[points.Count - 1]) || i == pieces.Count - 1 && pieces[i].endHeight == null && approaches.end);
                RoadSpatialGrid.PlannedPath? plan = RoadSpatialGrid.PlanRoadPath(
                    pieces[i].points, width, GenerationWorld,
                    pieces[i].startHeight ?? (continues.Contains(i) ? plans[plans.Count - 1].Heights[plans[plans.Count - 1].Heights.Count - 1] : i == 0 ? startGround : null),
                    pieces[i].endHeight ?? (i == pieces.Count - 1 ? endGround : null),
                    pieces[i].followTerrain, pieces[i].minHeight, ground,
                    new RoadLocationProtection.Targets(i == 0 ? locations.Start : null, i == pieces.Count - 1 ? locations.End : null));
                RoadSpatialGrid.LocationEnds = default;
                RoadSpatialGrid.StairStarts = default;
                if (plan == null)
                {
                    if (pieceCrossings.TryGetValue(i, out var failed))
                    {
                        if (failed.from != null) failedCrossings?.Add(failed.from);
                        if (failed.to != null && failed.to != failed.from) failedCrossings?.Add(failed.to);
                    }
                    return null;
                }
                // A waded ford is painted on the bed, which is never levelled (RoadSpatialGrid.PlanRoadPath skips the grade there):
                // its profile as planned, every route point between its banks, meets the cap through the one grade judge
                // (RoadSpatialGrid.WorstOverLimit), or the crossing is not waded and the pool gives it its next treatment
                // (failedCrossings: BRIEF-RULES 17). The bank-to-bank chord (RoadCrossingDetector.WadeBanksWithinGrade) did not see
                // a curve or a rise in the bed between.
                if (pieces[i].followTerrain && RoadSpatialGrid.WorstOverLimit(plan.Points, plan.Heights, null, RoadGrade.Configured,
                        RoadSpatialGrid.RoadStep(width)) is int steep && steep >= 0)
                {
                    if (pieceCrossings.TryGetValue(i, out var wade))
                    {
                        if (wade.from != null) failedCrossings?.Add(wade.from);
                        if (wade.to != null && wade.to != wade.from) failedCrossings?.Add(wade.to);
                    }
                    RoadSpatialGrid.LastRefusal = WadeOverCapRefusal;
                    RoadSpatialGrid.RefusedAt = plan.Points[steep];
                    Log.LogDebug(FormattableString.Invariant($"Road refused: a waded ford's bed climbs past the {RoadGrade.Configured:P0} cap at ({plan.Points[steep].x:F1}, {plan.Points[steep].y:F1})"));
                    return null;
                }
                // Past an end's stairs' foot the road is levelled under them but not painted; under a deck met level it is painted on.
                plan.Unpainted = StructureEnds.Unpainted(plan.Points, ends, width * 0.5f);
                plan.StairAtStart = startsAtStair.TryGetValue(i, out RoadStairSpan? startStair) ? (startStair!, true) : null;
                plan.StairAtEnd = endsAtStair.TryGetValue(i, out RoadStairSpan? endStair) ? (endStair!, false) : null;
                plans.Add(plan);
            }
        }
        finally
        {
            RoadSpatialGrid.HeldPoints = null;
            RoadSpatialGrid.LevelledEnds = null;
            RoadSpatialGrid.LocationEnds = default;
            RoadSpatialGrid.StairStarts = default;
        }

        // Where two pieces meet (a ford's waded or raised stretch and the road either side of it), they meet within the grade
        // cap over the gap between them, or within a step (StairLayout.StepUp): a ford's end a step no one walks up is not
        // built. The road is refused with that ford, which is searched round (failedCrossings: BRIEF-RULES 17).
        for (int i = 1; i < plans.Count; i++)
        {
            RoadSpatialGrid.PlannedPath a = plans[i - 1], b = plans[i];
            if (a.Points.Count == 0 || b.Points.Count == 0) continue;
            // Only where a ford's waded or raised stretch is one of the two: past a bridge or a staircase the structure meets them.
            bool fordPiece(int k) => pieceCrossings.TryGetValue(k, out var on) && on.from != null && on.from == on.to
                && on.from.Kind == CrossingKind.Ford && on.from.Style != FordStyle.Span;
            if (!fordPiece(i - 1) && !fordPiece(i)) continue;
            float gap = Vector2.Distance(a.Points[a.Points.Count - 1], b.Points[0]);
            float rise = Mathf.Abs(a.Heights[a.Heights.Count - 1] - b.Heights[0]);
            if (PiecesMeet(rise, gap)) continue;
            foreach (int k in new[] { i - 1, i })
                if (pieceCrossings.TryGetValue(k, out var met))
                {
                    if (met.from != null) failedCrossings?.Add(met.from);
                    if (met.to != null && met.to != met.from) failedCrossings?.Add(met.to);
                }
            RoadSpatialGrid.LastRefusal = FordEndStepRefusal;
            RoadSpatialGrid.RefusedAt = b.Points[0];
            Log.LogDebug(FormattableString.Invariant($"Road refused: two of its pieces meet {rise:F2} m apart at ({b.Points[0].x:F1}, {b.Points[0].y:F1}), more than a step"));
            return null;
        }
        return plans;
    }

    /// <summary>Why a road is refused where a ford's waded or raised stretch and the road beside it meet more than a step apart
    /// (neither within the grade cap over the gap nor within StairLayout.StepUp), PlanPieces.</summary>
    internal const string FordEndStepRefusal = "a ford's end more than a step from its road";

    /// <summary>Whether two pieces of a road <paramref name="gap"/> apart and <paramref name="rise"/> apart in height meet: within
    /// the grade cap over the gap (float slack only), or within a step (StairLayout.Walkable). Each rule on its own, never the
    /// two allowances summed (BRIEF-RULES 17): summed, a 1.1 m gap passed a 0.585 m rise, a ramp at the cap and a step on top.
    /// The one verdict on a ford's end seam (PlanPieces) and on two of a road's stretches side by side (<see cref="WalkStep"/>).</summary>
    internal static bool PiecesMeet(float rise, float gap) =>
        rise <= RoadGrade.Configured * gap + RoadSpatialGrid.HeldCapMargin || StairLayout.Walkable(rise, 0f);

    /// <summary>Why a road is refused where a waded ford's bed, as planned between its banks, climbs past the grade cap
    /// (PlanPieces): the wade is not built and its crossing gets its next treatment.</summary>
    internal const string WadeOverCapRefusal = "a waded ford's bed over the grade cap";

    /// <summary>
    /// The network's staircases settled on the ground the terrain writer will leave (natural, a placed location's own
    /// levelling, the roads' earthwork and the staircases' cuts): columns carried down where it lies lower than their layout
    /// read, then pieces wholly under it not placed, which the game breaks once it weighs them and drops their resources
    /// (<see cref="BuriedPieces"/>).
    /// Once, after every island is built.
    /// </summary>
    private static void DropBuriedPieces()
    {
        WorldGenerator? world = WorldGenerator.instance;
        if (world == null || StairStructures.Runs.Count == 0) return;
        var byPrefab = RoadsLog ? new SortedDictionary<string, int>(StringComparer.Ordinal) : null;
        int dropped = StairStructures.SettleOnFinishedGround(StructureGround.Vertices(world), byPrefab);
        if (byPrefab != null)
            Log.LogInfo($"  Staircases on the finished ground: pieces wholly under it, not placed: {dropped}; pieces the search left with nothing within reach to hold them: {StairLayout.SupportRefusals}" +
                (byPrefab.Count == 0 ? "" : " (" + string.Join(", ", byPrefab.Select(kv => $"{kv.Value} {kv.Key}")) + ")"));
    }

    /// <summary>
    /// The road from <paramref name="path"/>'s end at a location placed before its roads onto the location's
    /// landing, on its placed ground: a road where one gets there (<see cref="RoadLocationEntry"/>), else, in the Mountains
    /// or at a boss's entrance (<see cref="ApproachStairsAt"/>), a staircase (<see cref="RoadStairRuns.PlanPlacedLocationApproach"/>), carried on from the top row of the
    /// staircase the road ends on where it ends on one. False, with <paramref name="why"/>, where neither does.
    /// <paramref name="farGround"/>: the ground the road's other end was planned to meet (PlanFromPath).
    /// </summary>
    internal static bool PlanEntry(PlacedLocation placedLocation, bool atStart, List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans,
        List<RoadStairSpan> stairs, List<RoadCrossing> crossings, float width, out RoadSpatialGrid.PlannedPath? road, out RoadStairSpan? span, out string why,
        float? farGround = null)
    {
        why = "";
        long began = SearchStats.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        int tries = 0;
        try
        {
        // From the road's end, else from further back along its last stretch with the road cut there: the
        // way onto the landing then has more length to climb, and a staircase from there takes the road's
        // place instead of running beside it. From its end alone, 111 roads were refused at their
        // landings, "grade: ends too far apart for the cap" and "stairs: on its road" (5568,3904: 29 times).
        // The way onto the landing is part of the road: it may not cross another road nor run beside one at its height
        // either (MeetingRefusal), and is planned again (as a staircase where a road onto it fails, from further back) where
        // it does. A road that fails on its own is left to the road's own check, which names it. A staircase is held off the
        // road's own earlier stretch too (EntryMeetingRefusal): one that doubles back to end beside it at its height is
        // refused, and is laid from further back along the road, where the road is already at that height.
        bool checkMeeting = MeetingRefusal(plans, out _) == null;
        // The road's own line is built once for every candidate from the same road (OwnRoadOf), when one is first asked.
        Func<RoadSpatialGrid.PlannedPath?, RoadStairSpan?, string?> MeetsFrom(List<RoadSpatialGrid.PlannedPath> withPlans, Vector2 end)
        {
            var own = new Lazy<OwnRoad?>(() => OwnRoadOf(withPlans, atStart), false);
            return (entryRoad, entryStairs) => EntryMeetingRefusal(atStart, withPlans, end, entryRoad, entryStairs, checkMeeting, own);
        }
        // A start refused because the road to its foot is built as a ridge or a trench (RoadStairRuns.RidgeToFoot).
        bool ridged = false;
        foreach (float back in EntryCutBacks)
        {
            List<Vector2> p = path;
            RoadSpatialGrid.PlannedPath piece = atStart ? plans[0] : plans[plans.Count - 1];
            int shift = 0;
            if (back > 0f && !CutBack(atStart, back, path, piece, stairs, crossings, out p, out piece, out shift)) break;
            var cutPlans = new List<RoadSpatialGrid.PlannedPath>(plans);
            cutPlans[atStart ? 0 : plans.Count - 1] = piece;
            tries++;
            Vector2 end = p[atStart ? 0 : p.Count - 1];
            if (!PlanEntryFrom(placedLocation, atStart, p, cutPlans, stairs, width, shift, MeetsFrom(cutPlans, end), out road, out span, out string whyHere))
            {
                if (back == 0f) why = whyHere;
                ridged |= whyHere.Contains(RoadStairRuns.RidgeToFootRefusal);
                continue;
            }
            if (back > 0f)
            {
                ApplyCutBack(atStart, path, p, plans, piece, stairs, crossings, shift);
                if (RoadsLog) m_locationApproaches.AddOrUpdate(FormattableString.Invariant($"placed first: onto its landing from {back:F0} m back along its road"), 1, (_, c) => c + 1);
            }
            why = "";
            return true;
        }
        // The road to the foot built as a ridge or a trench: the staircase starts lower, from where the road climbs to it on
        // its ground, the road's heights planned again to meet the ground there (EntryFromLower), as a road staircase's foot
        // is started lower (RoadStairRuns.StartLower).
        if (ridged)
        {
            tries++;
            string lower = EntryFromLower(placedLocation, atStart, path, plans, stairs, crossings, width, farGround, MeetsFrom, out road, out span);
            if (lower == "") { why = ""; return true; }
            why += "; from lower: " + lower;
        }
        road = null;
        span = null;
        why += FormattableString.Invariant($" (also from up to {EntryCutBacks[EntryCutBacks.Length - 1]:F0} m back along the road)");
        return false;
        }
        finally { if (SearchStats.Enabled) SearchStats.Record(SearchStats.LandingEntry, why == "", false, tries, began); }
    }

    /// <summary>The ground a placed location's road is judged on (RoadStairRuns.RidgeToFoot, LowerEnd): the location's placed ground,
    /// the natural ground past its grid (<see cref="PlacedLocation.Ground"/> already falls back to it).</summary>
    private static Func<Vector2, float> PlacedGround(PlacedLocation placedLocation) => q => placedLocation.Ground(q.x, q.y);

    /// <summary>
    /// The way onto a placed location's landing from lower down the road, where the road to its end is built as a ridge or a
    /// trench (RoadStairRuns.RidgeToFoot): the road's end moved back along it to the nearest point it climbs to on its ground
    /// (RoadStairRuns.LowerEnd, within <see cref="PlacedLocationReach"/>), the road planned again to meet its ground there (its
    /// other end to meet <paramref name="farGround"/>, as it was planned), and the way onto the landing laid from there; taken
    /// (the path, its plans, and the staircases' and crossings' indices moved with it) where it is, its road within the
    /// earthwork cap and held to no ridge. Why not, else "". The new end meets its own ground, not a target: a target holds the
    /// end ramp toward it for its whole length (RoadEndpointRamp.BaseHeight), a level shelf on fill down the slope.
    /// </summary>
    private static string EntryFromLower(PlacedLocation placedLocation, bool atStart, List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans,
        List<RoadStairSpan> stairs, List<RoadCrossing> crossings, float width, float? farGround,
        Func<List<RoadSpatialGrid.PlannedPath>, Vector2, Func<RoadSpatialGrid.PlannedPath?, RoadStairSpan?, string?>> meetsFrom,
        out RoadSpatialGrid.PlannedPath? road, out RoadStairSpan? span)
    {
        road = null;
        span = null;
        // The structure nearest the end: the road is not cut back past it.
        int structures = -1;
        foreach (RoadStairSpan s in stairs) structures = atStart ? (structures < 0 ? s.FromIndex : Mathf.Min(structures, s.FromIndex)) : Mathf.Max(structures, s.ToIndex);
        foreach (RoadCrossing c in crossings) structures = atStart ? (structures < 0 ? c.FromIndex : Mathf.Min(structures, c.FromIndex)) : Mathf.Max(structures, c.ToIndex);
        Func<Vector2, float> ground = PlacedGround(placedLocation);
        int j = RoadStairRuns.LowerEnd(path, atStart, structures, ground, PlacedLocationReach);
        if (j < 0) return "no point back along the road it climbs to on its ground";
        var lowPath = atStart ? path.GetRange(j, path.Count - j) : path.GetRange(0, j + 1);
        int shift = atStart ? j : 0;
        var lowCrossings = new List<RoadCrossing>(crossings);
        void Shift(int by) { foreach (RoadStairSpan s in stairs) { s.FromIndex -= by; s.ToIndex -= by; } foreach (RoadCrossing c in crossings) { c.FromIndex -= by; c.ToIndex -= by; } }
        Shift(shift);
        List<RoadSpatialGrid.PlannedPath>? lowPlans = PlanRoadPathWithCrossings(lowPath, lowCrossings, width,
            atStart ? null : farGround, atStart ? farGround : null, stairs,
            locations: new RoadLocationProtection.Targets(atStart ? placedLocation.Centre : null, atStart ? null : placedLocation.Centre));
        Shift(-shift);
        if (lowPlans == null) return "the road to it not planned: " + (RoadSpatialGrid.LastRefusal ?? "refused");
        var none = new List<Vector2>();
        if (PastTerrainLimit(lowPlans, EarthworkLimit, none) > 0 || HeldRidges(lowPlans, none) > 0)
            return "the road to it past the earthwork cap or held on a ridge";
        Vector2 end = lowPath[atStart ? 0 : lowPath.Count - 1];
        if (!PlanEntryFrom(placedLocation, atStart, lowPath, lowPlans, stairs, width, shift, meetsFrom(lowPlans, end), out road, out span, out string why))
            return why;
        path.Clear(); path.AddRange(lowPath);
        plans.Clear(); plans.AddRange(lowPlans);
        Shift(shift);
        crossings.Clear(); crossings.AddRange(lowCrossings);
        System.Threading.Interlocked.Increment(ref RoadStairRuns.FeetStartedLower);
        if (RoadsLog) m_locationApproaches.AddOrUpdate("placed first: onto its landing from lower down its road, off a ridge to its foot", 1, (_, c) => c + 1);
        return "";
    }

    /// <summary>The road's planned pieces with the way onto a placed location's landing (a road, or a staircase's walked pieces)
    /// on its end at the location, running from <paramref name="end"/>, the road's end, onto the landing: the road as it meets the
    /// network (<see cref="MeetingRefusal"/>).</summary>
    internal static List<RoadSpatialGrid.PlannedPath> WithEntry(bool atStart, List<RoadSpatialGrid.PlannedPath> plans, Vector2 end,
        RoadSpatialGrid.PlannedPath? road, RoadStairSpan? span)
    {
        var points = new List<Vector2>();
        var heights = new List<float>();
        if (road != null) { points.AddRange(road.Points); heights.AddRange(road.Heights); }
        else if (span != null)
        {
            // Its walk: the pieces are laid a row at a time, side by side across the width, so each row (consecutive
            // walked pieces at one height) is one point at its middle, on its walking surface (RoadStairRuns.Surface).
            var row = new List<StructurePiece>();
            void EndRow()
            {
                if (row.Count == 0) return;
                points.Add(new Vector2(row.Average(q => q.Position.x), row.Average(q => q.Position.z)));
                heights.Add(RoadStairRuns.Surface(row[0]));
                row.Clear();
            }
            foreach (StructurePiece piece in StairRun.FromPlan(span.FromPoint, span.ToPoint, RoadStairRuns.Width, span.Plan).Pieces)
            {
                if (piece.Kind != StructurePieceKind.StairStep && piece.Kind != StructurePieceKind.StairFloor) continue;
                if (row.Count > 0 && (Mathf.Abs(piece.Position.y - row[0].Position.y) > 0.05f || piece.Kind != row[0].Kind
                    || Vector2.Distance(new Vector2(piece.Position.x, piece.Position.z), new Vector2(row[0].Position.x, row[0].Position.z)) > RoadStairRuns.Width + 0.5f))
                    EndRow();
                row.Add(piece);
            }
            EndRow();
        }
        var all = new List<RoadSpatialGrid.PlannedPath>(plans);
        if (points.Count == 0) return all;
        // From the road's end onto the landing; before the road where the road starts at the location.
        if ((points[0] - end).sqrMagnitude > (points[points.Count - 1] - end).sqrMagnitude) { points.Reverse(); heights.Reverse(); }
        if (atStart) { points.Reverse(); heights.Reverse(); }
        var entry = new RoadSpatialGrid.PlannedPath(points, heights, points.ConvertAll(_ => default(RoadPointDebugInfo)), RoadWidth, 0f, false);
        if (atStart) all.Insert(0, entry); else all.Add(entry);
        return all;
    }

    /// <summary>How far back along a road's last stretch the way onto a placed location's landing may start,
    /// the road cut there, tried in turn (<see cref="PlanEntry"/>); the first is the road's own end. Settable for tests.</summary>
    internal static float[] EntryCutBacks = { 0f, 12f, 24f };

    /// <summary>The stair layout's allowance for a way onto a placed location's landing, per landing and start
    /// tried: a road's staircase's (<see cref="RoadStairRuns.GenerationTotalExpansions"/>).</summary>
    internal static int EntryTotalExpansions = RoadStairRuns.GenerationTotalExpansions, EntryStall = RoadStairRuns.GenerationStall;

    /// <summary>Starts within a cell of this size (and a metre of height) lay the same staircase onto a landing.</summary>
    private const float EntryStartCell = 2f;
    /// <summary>How many starts' staircases onto one placed location's landing are laid out before no more are. Settable for tests.</summary>
    internal static int EntryStairsPerLocation = 4;
    /// <summary>Staircases onto placed locations' landings laid out this generation (tests).</summary>
    internal static int EntryStairLayouts;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, float), int> m_entryStairsTried = new();
    /// <summary>Staircases onto placed locations' landings that could not be laid, by location, start and height:
    /// read and written by the island worker that owns the location (TryGetValue and the indexer only).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<((float, float) location, int x, int z, int h, bool onTop), string> m_entryStairsFailed = new();

    /// <summary>How far from where a road ends a placed location's landing may be for the road to be carried on
    /// onto it: from the ring round a Dvergr entrance (41 m out, its landing 24 m) anywhere but its back. A
    /// landing facing the sea is reached along the shore from the side or behind (at -6016,4736: water
    /// 6-14 m out to the N, NE, E and SE of its landing, dry ground only to the S, SW and W; refused "too far"
    /// at the old 40 m, <see cref="RoadStairRuns.LocationReach"/>).</summary>
    public const float PlacedLocationReach = 64f;

    /// <summary>
    /// The road with its end at a location pulled <paramref name="back"/> metres back along its last planned
    /// stretch: that stretch cut at the planned point there, and the waypoints past it replaced by it.
    /// False where the stretch is too short, or a staircase or a river crossing lies in the part cut away.
    /// <paramref name="shift"/> is how many waypoints the start lost: everything indexed along the path (its
    /// staircases and its crossings) moves down by it when the cut is taken (<see cref="ApplyCutBack"/>). The
    /// one owner of cutting a road back: the crossings' indices were not moved with the staircases', and the
    /// network hints then read stale crossing ranges (a hint on a bridge or ford waypoint).
    /// </summary>
    internal static bool CutBack(bool atStart, float back, List<Vector2> path, RoadSpatialGrid.PlannedPath piece,
        List<RoadStairSpan> stairs, List<RoadCrossing> crossings, out List<Vector2> cutPath, out RoadSpatialGrid.PlannedPath cutPiece, out int shift)
    {
        cutPath = path;
        cutPiece = piece;
        shift = 0;
        int n = piece.Points.Count;
        if (n < 3 || path.Count < 2) return false;
        // The planned point `back` metres in from the location's end, leaving at least a few metres of stretch.
        int k = -1;
        float walked = 0f;
        for (int i = 1; i < n - 1; i++)
        {
            int a = atStart ? i - 1 : n - i, b2 = atStart ? i : n - 1 - i;
            walked += Vector2.Distance(piece.Points[a], piece.Points[b2]);
            if (walked >= back) { k = b2; break; }
        }
        if (k < 0) return false;
        int from = atStart ? k : 0, count = atStart ? n - k : k + 1;
        float rest = 0f;
        for (int i = from + 1; i < from + count; i++) rest += Vector2.Distance(piece.Points[i - 1], piece.Points[i]);
        if (rest < 4f) return false;
        Vector2 cut = piece.Points[k];
        // The waypoints within `back` of the location's end go; the cut point takes their location.
        int drop = 0;
        float along = 0f;
        for (int i = 1; i < path.Count - 1; i++)
        {
            int a = atStart ? i - 1 : path.Count - i, b2 = atStart ? i : path.Count - 1 - i;
            along += Vector2.Distance(path[a], path[b2]);
            if (along >= back) break;
            drop = i;
        }
        int dropped = drop + 1;
        foreach (RoadStairSpan s in stairs)
            if (atStart ? s.FromIndex < dropped : s.ToIndex > path.Count - 1 - dropped) return false;
        foreach (RoadCrossing c in crossings)
            if (atStart ? c.FromIndex < dropped : c.ToIndex > path.Count - 1 - dropped) return false;
        var trimmed = atStart ? path.GetRange(dropped, path.Count - dropped) : path.GetRange(0, path.Count - dropped);
        if (atStart) trimmed.Insert(0, cut); else trimmed.Add(cut);
        if (trimmed.Count < 2) return false;
        shift = atStart ? dropped - 1 : 0;
        var points = piece.Points.GetRange(from, count);
        cutPiece = new RoadSpatialGrid.PlannedPath(points, piece.Heights.GetRange(from, count), piece.DebugInfos.GetRange(from, count),
            piece.Width, rest, piece.FollowTerrain)
        {
            Widths = piece.Widths?.GetRange(from, count),
            Junctions = RoadSpatialGrid.Junction.On(piece.Junctions, at => points.Exists(q => (q - at).sqrMagnitude <= 1f)),
            Unpainted = piece.Unpainted?.GetRange(from, count),
            Grades = piece.Range(piece.Grades, from, count), Held = piece.Range(piece.Held, from, count),
            // The cut end is no longer the one planned: only the other keeps its landing, and the staircase planned to meet
            // it there (RoadSpatialGrid.PlannedPath.StairAtStart, StairAtEnd: the road that meets that staircase's end).
            Landed = (!atStart && piece.Landed.start, atStart && piece.Landed.end),
            StairAtStart = atStart ? null : piece.StairAtStart, StairAtEnd = atStart ? piece.StairAtEnd : null,
            Locations = piece.Locations,
        };
        cutPath = trimmed;
        return true;
    }

    /// <summary>
    /// Where a road ends at one of its ends, and at what height: its last road point, or, where its last stretch is a
    /// staircase, that staircase's end (an approach or an entry carries on from there). Null where neither is at the end.
    /// <paramref name="shift"/>: how many path points were cut back from the start since the staircases were laid.
    /// </summary>
    private static (Vector2 end, float height, RoadStairSpan? onStairs)? RoadEnd(bool atStart, List<Vector2> path,
        List<RoadSpatialGrid.PlannedPath> plans, List<RoadStairSpan> stairs, int shift = 0)
    {
        Vector2 end = path[atStart ? 0 : path.Count - 1];
        RoadSpatialGrid.PlannedPath piece = atStart ? plans[0] : plans[plans.Count - 1];
        int at = atStart ? 0 : piece.Points.Count - 1;
        if (piece.Points.Count > 0 && Vector2.Distance(piece.Points[at], end) <= 0.5f) return (end, piece.Heights[at], null);
        RoadStairSpan? onStairs = stairs.Find(s => atStart ? s.FromIndex - shift == 0 : s.ToIndex == path.Count - 1);
        if (onStairs == null) return null;
        return atStart ? (onStairs.FromPoint, onStairs.FromHeight, onStairs) : (onStairs.ToPoint, onStairs.ToHeight, onStairs);
    }

    /// <summary>Take a cut made by <see cref="CutBack"/>: the path and its end piece replaced, and the staircases' and the
    /// crossings' indices moved down by <paramref name="shift"/>.</summary>
    internal static void ApplyCutBack(bool atStart, List<Vector2> path, List<Vector2> cutPath, List<RoadSpatialGrid.PlannedPath> plans,
        RoadSpatialGrid.PlannedPath cutPiece, List<RoadStairSpan> stairs, List<RoadCrossing> crossings, int shift)
    {
        path.Clear(); path.AddRange(cutPath);
        plans[atStart ? 0 : plans.Count - 1] = cutPiece;
        foreach (RoadStairSpan s in stairs) { s.FromIndex -= shift; s.ToIndex -= shift; }
        foreach (RoadCrossing c in crossings) { c.FromIndex -= shift; c.ToIndex -= shift; }
    }

    /// <param name="meets">Why the road with this way onto the landing would not meet the network as the rules allow, or null
    /// (<see cref="PlanEntry"/>): a road onto it that fails gives way to a staircase, a staircase that fails is not laid out
    /// again from the same start.</param>
    private static bool PlanEntryFrom(PlacedLocation placedLocation, bool atStart, List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans,
        List<RoadStairSpan> stairs, float width, int shift, Func<RoadSpatialGrid.PlannedPath?, RoadStairSpan?, string?> meets,
        out RoadSpatialGrid.PlannedPath? road, out RoadStairSpan? span, out string why)
    {
        road = null;
        span = null;
        var world = GenerationWorld;
        int index = atStart ? 0 : path.Count - 1;
        if (RoadEnd(atStart, path, plans, stairs, shift) is not var (end, height, onStairs)) { why = "no road at the end"; return false; }
        var tail = new List<Vector2>();
        if (atStart) for (int i = Mathf.Min(LocationApproachRequest.TailPoints, path.Count - 1); i >= 0; i--) tail.Add(path[i]);
        else for (int i = Mathf.Max(0, path.Count - 1 - LocationApproachRequest.TailPoints); i < path.Count; i++) tail.Add(path[i]);
        tail[tail.Count - 1] = end;
        var request = new LocationApproachRequest
        {
            RoadEnd = end, RoadHeight = height, Arriving = RoadStairRuns.ArrivingTravel(path, atStart ? plans[0] : plans[plans.Count - 1], end, atStart), Centre = placedLocation.Centre, RoadTail = tail,
            Reach = PlacedLocationReach, TotalExpansions = EntryTotalExpansions, Stall = EntryStall,
            Leg = RoadStairRuns.GenerationExpansions,
        };
        PlacedLocationRecord r = placedLocation.Record;
        string noRoad = "road: arrives on a staircase";
        if (onStairs == null)
        {
            road = RoadLocationEntry.Plan(request, placedLocation.Shape, width, placedLocation.Ground, world, out noRoad, RoadsNear);
            if (road != null && meets(road, null) is string roadMeets) { noRoad = "road: " + roadMeets; road = null; }
            if (road != null) { why = ""; return true; }
        }
        // Outside the Mountains a road onto the landing or nothing, but at a boss's entrance (ApproachStairsAt).
        // Nor down onto a custom location standing below the end: its fallback ends the road at its ring.
        if (!ApproachStairsAt(placedLocation.Centre, placedLocation.Record.Prefab, height)) { why = noRoad + "; stairs: none outside the mountains nor down to a custom location"; return false; }
        // Nor from a road built as a ridge or a trench to its foot (RoadStairRuns.RidgeToFoot): PlanEntry starts it lower.
        RoadSpatialGrid.PlannedPath endPiece = atStart ? plans[0] : plans[plans.Count - 1];
        if (onStairs == null && RoadStairRuns.RidgeToFoot(endPiece.Points, endPiece.Heights, atStart, PlacedGround(placedLocation)) is { } ridge)
        {
            System.Threading.Interlocked.Increment(ref RoadStairRuns.RidgesToFeet);
            why = noRoad + "; stairs: " + RoadStairRuns.RidgeRefusal(ridge);
            return false;
        }
        // Where it starts, the road's end is a level landing (RoadPitches.LandEnd), as from an end at a location not placed first
        // (LocationApproaches): limited again with that end held; where that does not fit, no staircase from it.
        List<float>? landed = onStairs == null ? new List<float>(endPiece.Heights) : null;
        List<float>? landedGrades = endPiece.Grades;
        (bool start, bool end) landedEnds = endPiece.Landed;
        if (landed != null && !RoadPitches.LandEnd(endPiece.Points, landed, atStart, RoadGrade.Configured, out landedGrades,
                out landedEnds, endPiece.Grades, endPiece.Held, endPiece.Landed))
        {
            why = noRoad + "; stairs: " + ApproachLandingRefusal;
            return false;
        }
        StairContinuation? top = onStairs == null ? null
            : RoadStairRuns.TopAt(end, height, StairRun.FromPlan(onStairs.FromPoint, onStairs.ToPoint, RoadStairRuns.Width, onStairs.Plan).Pieces);
        // The same start onto the same landing lays the same staircase: a failed one is not laid out again.
        // Roads to a location come in on its one aimed ring point, and each failing layout cost seconds: one
        // location took 36 of them, 34 s (the game's slowest islands 570 s).
        var failedKey = (LocationKey(placedLocation.Centre), Mathf.RoundToInt(end.x / EntryStartCell), Mathf.RoundToInt(end.y / EntryStartCell),
            Mathf.RoundToInt(height), top.HasValue);
        string noStairs;
        // Laid out on the ground its kit will hold it up on (RoadStairRuns.PlanningGround, one owner): the lattice, the location's
        // preload in its footprint. On the location's own ground out to its grid's edge, a flight up the slope just past the
        // footprint was priced on the grid's metres and held up on the continuous ground between them.
        RoadStairRuns.LayoutGround planning = RoadStairRuns.LayoutGround.Lattice(world);
        Func<float, float, float> stairGround = planning.Read;
        // And a location whose landing no staircase reached from EntryStairsPerLocation starts gets no more: its
        // roads may still go on as road. Three locations took 84 layouts and 65 s there.
        m_entryStairsTried.TryGetValue(failedKey.Item1, out int triedHere);
        if (m_entryStairsFailed.TryGetValue(failedKey, out string? failed)) { span = null; noStairs = failed; }
        else if (triedHere >= EntryStairsPerLocation) { span = null; noStairs = FormattableString.Invariant($"no staircase from {triedHere} other starts, none laid out again"); }
        else
        {
            System.Threading.Interlocked.Increment(ref EntryStairLayouts);
            span = RoadStairRuns.PlanPlacedLocationApproach(request, r.Yaw, r.Ops, planning, (x, z) => RoadBiome.At(world, x, z), world.GetSeed(),
                out noStairs, r.RockRadius, RoadLocationProtection.Contains, r.Rocks, r.KeepSmallRocks, r.Root, top, placed: placedLocation.Shape);
            if (span == null)
            {
                m_entryStairsFailed[failedKey] = noStairs;
                m_entryStairsTried[failedKey.Item1] = triedHere + 1;
            }
        }
        if (span != null)
        {
            // It may touch the road's own staircase where the road ends on it, or just past its top: a road
            // that climbed to the ring by a staircase and ran its last metres on was refused "on another
            // staircase" at a frost cave.
            string conflict = RoadStairRuns.LocationConflict(span.Plan, placedLocation.Centre, path, index, stairGround, world, stairs,
                onStairs != null || RoadStairRuns.EndsOnStairs(end, stairs) ? end : null, request.Arriving, request.RoadHeight);
            // Laid out and clear of the location, but meeting the network as the rules forbid: as a failed layout, not laid
            // out again from this start, and counted toward the location's EntryStairsPerLocation.
            if (conflict == "" && meets(null, span) is string stairsMeet)
            {
                conflict = stairsMeet;
                m_entryStairsFailed[failedKey] = stairsMeet;
                m_entryStairsTried[failedKey.Item1] = triedHere + 1;
            }
            if (conflict == "")
            {
                if (landed != null)
                {
                    for (int i = 0; i < landed.Count; i++) endPiece.Heights[i] = landed[i];
                    (endPiece.Grades, endPiece.Landed) = (landedGrades, landedEnds);
                }
                MarkFromTheEnd(endPiece, atStart, span, onStairs);
                why = "";
                return true;
            }
            noStairs = conflict;
            span = null;
        }
        why = noRoad + "; stairs: " + noStairs;
        return false;
    }

    /// <summary>The network's road points within <paramref name="within"/> of a point (any thread: the grid is read under its lock).</summary>
    internal static IEnumerable<(Vector2 at, float height)> RoadsNear(Vector2 p, float within)
    {
        foreach (RoadSpatialGrid.RoadPoint q in RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(p.x, 0f, p.y), within))
            if (q.IsRoad && (q.p - p).sqrMagnitude <= within * within) yield return (q.p, q.h);
    }

    /// <summary>Why no approach is laid from a road's end (<see cref="LocationApproaches"/>, <see cref="PlanEntryFrom"/>): its last
    /// metres cannot be levelled for its foot within the grade cap (RoadPitches.LandEnd).</summary>
    internal const string ApproachLandingRefusal = "the road's end cannot be levelled for the approach's foot within the grade cap";

    /// <summary>
    /// Staircases from the road's ends that stop at a location up to the location's
    /// flat ground (<see cref="RoadStairRuns.LayLocationApproach"/>). An end is at
    /// its location unless it was moved to a junction (<paramref name="startAtLocation"/>,
    /// <paramref name="endAtLocation"/>); a location with no radius (an island's edge) has none, nor one outside the Mountains
    /// but a boss's entrance (<see cref="ApproachStairsAt"/>).
    /// Judged by distance from the trimmed ring, an end the approach bend or the contour
    /// search had moved a few metres out (by design) counted as moved off its location, and
    /// its approach was never tried (at the frost cave -2560,1536 the road did
    /// not meet the front cave landing; 51 such ends on one world).
    /// </summary>
    private static List<RoadStairSpan> LocationApproaches(List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans,
        List<RoadStairSpan> stairs, Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius, bool startAtLocation, bool endAtLocation, bool tally = true)
    {
        bool said = tally && RoadsLog;
        var laid = new List<RoadStairSpan>();
        var world = GenerationWorld;
        if (world == null || path.Count < 2) return laid;
        void At(bool atStart, Vector2 centre, float asked, bool atLocation)
        {
            if (asked <= 0f || !atLocation) return;
            if (RoadEnd(atStart, path, plans, stairs) is not var (end, height, onStairs)) return;
            // Nor down to a custom location standing below the end: its fallback ends the road at its ring.
            if (!ApproachStairsAt(centre, LocationPrefabLevelling.PrefabAt(centre), height))
            {
                if (said) m_locationApproaches.AddOrUpdate("none outside the mountains nor down to a custom location", 1, (_, n) => n + 1);
                return;
            }
            int index = atStart ? 0 : path.Count - 1;
            // Not from a road built as a ridge or a trench to its foot (RoadStairRuns.RidgeToFoot): none, and the road's end
            // is then not level with its location (EndNotLevel).
            RoadSpatialGrid.PlannedPath piece = atStart ? plans[0] : plans[plans.Count - 1];
            if (onStairs == null && RoadStairRuns.RidgeToFoot(piece.Points, piece.Heights, atStart, NaturalGround) != null)
            {
                if (tally) System.Threading.Interlocked.Increment(ref RoadStairRuns.RidgesToFeet);
                if (said) m_locationApproaches.AddOrUpdate(RoadStairRuns.RidgeToFootRefusal, 1, (_, n) => n + 1);
                return;
            }
            // Where it starts, the road's end is a level landing, as at every staircase's foot the road is held at
            // (RoadSpatialGrid.StairStarts): planned before the approach, the road's last metres are limited again with that
            // end held (RoadPitches.LandEnd); where they cannot be, the road is left as it was if it keeps within the cap into
            // that end, else no approach is laid from it, and the road is not built so (EndNotLevel; the planner tries its other
            // candidates). Its last 8 m at a pitch's grade ran into an approach's foot.
            List<float>? landed = null, landedGrades = null;
            (bool start, bool end) landedEnds = default;
            if (onStairs == null)
            {
                landed = new List<float>(piece.Heights);
                if (!RoadPitches.LandEnd(piece.Points, landed, atStart, RoadGrade.Configured, out landedGrades, out landedEnds,
                        piece.Grades, piece.Held, piece.Landed))
                {
                    if (said) m_locationApproaches.AddOrUpdate(ApproachLandingRefusal, 1, (_, n) => n + 1);
                    return;
                }
            }
            var both = new List<RoadStairSpan>(stairs);
            both.AddRange(laid);
            RoadStairSpan? span = RoadStairRuns.LayLocationApproach(end, height, RoadStairRuns.ArrivingTravel(path, piece, end, atStart), centre,
                LocationLevelling.CentreHeight(centre, world), LocationLevelling.OpsAt(centre), LocationLevelling.FacesDownhill(centre),
                path, index, both, world, out string why, tally);
            // A location placed before its road was carried on onto its landing (PlanEntry); one that could not be
            // placed before its roads has no road (PlacedLocations.NotPlaced), so none ends here.
            if (span != null)
            {
                laid.Add(span);
                if (landed != null)
                {
                    for (int i = 0; i < landed.Count; i++) piece.Heights[i] = landed[i];
                    (piece.Grades, piece.Landed) = (landedGrades, landedEnds);
                }
                MarkFromTheEnd(piece, atStart, span, onStairs);
            }
            if (said) m_locationApproaches.AddOrUpdate(span != null ? "laid" : why, 1, (_, n) => n + 1);
        }
        At(true, startCenter, startRadius, startAtLocation);
        At(false, endCenter, endRadius, endAtLocation);
        return laid;
    }

    /// <summary>A staircase laid from the road's own end (<see cref="LocationApproaches"/>, <see cref="PlanEntryFrom"/>, its From end
    /// the road's end) marked on the road's end piece there (RoadSpatialGrid.PlannedPath.StairAtStart, StairAtEnd): the
    /// road that meets it. Not where the road ends on a staircase (<paramref name="onStairs"/>): no road meets it.</summary>
    private static void MarkFromTheEnd(RoadSpatialGrid.PlannedPath piece, bool atStart, RoadStairSpan span, RoadStairSpan? onStairs)
    {
        if (onStairs != null) return;
        if (atStart) piece.StairAtStart = (span, false); else piece.StairAtEnd = (span, false);
    }

    /// <summary>Where a road was laid again with its raised feet's stairs added (<see cref="WithStairsAdded{T}"/>).</summary>
    internal enum StairsAddedAt { Planning, Commit, Trial, Manual }

    /// <summary>Roads laid again with their raised feet's stairs added, and of them those then planned or built, by where
    /// (<see cref="StairsAddedAt"/>: planning, generation's commit; trials and manual roads are not counted).</summary>
    internal static int m_stairsAddedPlanning, m_stairsAddedPlanningSaved, m_stairsAddedCommit, m_stairsAddedCommitSaved;

    /// <summary>Whether a foot of <paramref name="st"/> stands on a raised landing (StairPlan.FootRaised,
    /// StairLayout.StairsDownTo) that <paramref name="refused"/> names: at or round its landing, within the reach its landing is
    /// judged over (RoadStairRuns.FootBackReach, RoadStairRuns.LandingRaised) of its foot's path point or its road's end there,
    /// by where it was refused or any point it marks; any raised foot where no refusal is given (the road's planning, whose
    /// grade refusals name no point).</summary>
    private static bool RaisedFootNamed(RoadStairSpan st, Refusal? refused)
    {
        if (st.Existing || st.Plan.FootRaised <= 0f || st.Plan.FootRaisedAt is not Vector2 at) return false;
        if (refused == null) return true;
        Vector2 end = st.Plan.Foot is Vector3 f ? RoadStairRuns.RoadEndAt(st.Plan, new Vector2(f.x, f.z)) : at;
        bool Near(Vector2 p) => Vector2.Distance(p, at) <= RoadStairRuns.FootBackReach || Vector2.Distance(p, end) <= RoadStairRuns.FootBackReach;
        return Near(refused.At) || refused.Marks.Any(Near);
    }

    /// <summary>Whether a road refused with <paramref name="stairs"/> (for <paramref name="refused"/>, where it names a point)
    /// is laid once more with stairs added (<see cref="WithStairsAdded{T}"/>): a foot of its own kept on the shorter flight on
    /// a raised landing that the refusal names (<see cref="RaisedFootNamed"/>).</summary>
    internal static bool NeedsStairsAdded(IEnumerable<RoadStairSpan>? stairs, Refusal? refused = null) =>
        stairs != null && stairs.Any(st => RaisedFootNamed(st, refused));

    /// <summary>
    /// The one owner of a road whose raised foot landing cannot be built (the owner's rule, StairLayout.StairsDownTo: where the
    /// earthwork can't, the stair is added): <paramref name="again"/>, this road's own lay once more (never a search again),
    /// with only the feet of <paramref name="stairs"/> on a raised landing that <paramref name="refusal"/> names
    /// (<see cref="RaisedFootNamed"/>) laid with their stair added, by their foot's path point (StairPlan.FootRaisedAt), with
    /// those <paramref name="already"/> added; every other foot as it was. Asked once where a road was refused so
    /// (<see cref="NeedsStairsAdded"/>): where it is planned (PlanCandidate: the one landing acceptance refused it), at
    /// generation's commit (GenerateRoadCore: the one list, RoadRefusal), for a marked manual road (ManualRoads) and an
    /// approach trial (TrialBuilds) alike. <paramref name="refused"/> reads whether it was refused again; counted, but for
    /// trials and manual roads, in the generation log.
    /// </summary>
    internal static T WithStairsAdded<T>(IEnumerable<RoadStairSpan> stairs, IReadOnlyCollection<Vector2>? already,
        Func<IReadOnlyCollection<Vector2>, T> again, Func<T, bool> refused, StairsAddedAt at, Refusal? refusal = null)
    {
        var set = already != null ? new HashSet<Vector2>(already) : new HashSet<Vector2>();
        foreach (RoadStairSpan st in stairs)
            if (RaisedFootNamed(st, refusal)) set.Add(st.Plan.FootRaisedAt!.Value);
        if (at == StairsAddedAt.Planning) System.Threading.Interlocked.Increment(ref m_stairsAddedPlanning);
        else if (at == StairsAddedAt.Commit) System.Threading.Interlocked.Increment(ref m_stairsAddedCommit);
        T result = again(set);
        if (!refused(result))
        {
            if (at == StairsAddedAt.Planning) System.Threading.Interlocked.Increment(ref m_stairsAddedPlanningSaved);
            else if (at == StairsAddedAt.Commit) System.Threading.Interlocked.Increment(ref m_stairsAddedCommitSaved);
        }
        return result;
    }

    /// <summary>The staircases on <paramref name="path"/> laid out from the stair
    /// steps the last search bought; false when one cannot be laid.</summary>
    private static bool TryLayStairs(IReadOnlyList<(Vector2 from, Vector2 to)> steps, List<Vector2> path, List<RoadCrossing> crossings,
        RoadLocationProtection.Targets locations, IReadOnlyCollection<Vector2>? stairsAddedAt, out List<RoadStairSpan> stairs)
    {
        stairs = new List<RoadStairSpan>();
        if (steps.Count == 0) return true;
        List<RoadStairSpan>? laid = RoadStairRuns.Lay(path, steps, crossings, GenerationWorld, locations, stairsAddedAt);
        if (laid == null) return false;
        stairs = laid;
        return true;
    }

    /// <summary>
    /// Convenience overload using Vector3 positions (extracts X/Z as Vector2).
    /// </summary>
    public static bool GenerateRoad(
        Vector3 startPos, float startRadius,
        Vector3 endPos, float endRadius,
        float width, string? label = null) =>
        GenerateRoad(startPos, startRadius, endPos, endRadius, width, label, null);

    internal static bool GenerateRoad(
        Vector3 startPos, float startRadius,
        Vector3 endPos, float endRadius,
        float width, string? label, List<Vector2>? networkHints)
    {
        return GenerateRoad(
            new Vector2(startPos.x, startPos.z), startRadius,
            new Vector2(endPos.x, endPos.z), endRadius,
            width, label, networkHints);
    }

    #endregion

    #region Location Data

    public struct LocationData
    {
        public Vector3 SpawnPoint;
        public float SpawnRadius;
        public List<(string name, Vector3 position, float radius)> AllLocations;
    }

    private static IEnumerable<bool> GatherLocationSteps(ZoneSystem zones,RoadPreparation preparation,Action<LocationData?> complete)
    {
        // Snapshot the live ValueCollection in one main-thread operation; no live enumerator crosses a frame.
        var source=zones.GetLocationList()?.ToArray();
        preparation.BeginStage("Copying world locations",source?.Length ?? 0,"locations"); yield return true;
        if(source==null || source.Length==0) { Log.LogWarning("No location instances found"); complete(null); yield break; }
        Vector3? spawn=null; float radius=0;
        var all=new List<(string name,Vector3 position,float radius)>();
        foreach(var location in source)
        {
            var item=(location.m_location.m_prefab.Name!,location.m_position,location.m_location.m_exteriorRadius);
            if(item.Item1=="StartTemple") { spawn=item.Item2; radius=item.Item3; all.Add(item); }
            // HARD RULE: a location the game leaves for a player to find is never a destination (PlacedLocations.PlayerDecides).
            else if(PlacedLocations.PlayerDecides(item.Item1,location.m_location.m_unique,location.m_location.m_iconPlaced,location.m_location.m_iconAlways)) { }
            else all.Add(item);
            if(preparation.Checkpoint()) yield return true;
        }
        if(!spawn.HasValue) { Log.LogWarning("Could not find spawn point (StartTemple)"); spawn=Vector3.zero; }
        Log.LogDebug($"Found spawn at {spawn.Value}, {all.Count} total locations");
        complete(new LocationData {SpawnPoint=spawn.Value,SpawnRadius=radius,AllLocations=all});
    }

    /// <summary>Whether a location is a destination on <paramref name="island"/>: on it, a road location, in a biome the
    /// network allows, and not in a zone the network goes round (NetworkCutoff.Avoids: it is not chosen, so it neither
    /// ranks its island nor takes a location in its quota).</summary>
    private static bool IsIslandDestination(Island island, (string name, Vector3 position, float radius) location,
        WorldGenerator world, RoadNetworkOptions options) =>
        island.ContainsPoint(location.position) && IsRoadLocation(location.name)
        && options.Allows(RoadBiome.OfLocation(world, new Vector2(location.position.x, location.position.z)))
        && !NetworkCutoff.Avoids(location.position);

    /// <summary>Hildir's quest locations: her cave, crypt and tower. Not her camp (unique, a vendor).</summary>
    internal static bool IsHildirQuestLocation(string name) => name == "Hildir_cave" || name == "Hildir_crypt" || name == "Hildir_plainsfortress";

    /// <summary>The one rule for Hildir's quest locations: roads go to them only with their setting on (config
    /// Locations.AttemptRoadsToHildirQuestLocations). Then they are required destinations, placed before their roads; off, they
    /// are left for a player, as the game leaves them.</summary>
    internal static bool HildirRoads(string name) => IsHildirQuestLocation(name) && GenerationOptions.HildirQuestLocations;

    private static bool IsRoadLocation(string locationName)
    {
        if (IsHildirQuestLocation(locationName)) return HildirRoads(locationName);
        return LocationPriorities.ContainsKey(locationName) ||
               IsCustomLocation(locationName);
    }

    private static bool IsCustomLocation(string name) => RegisteredLocationNames.Contains(name)
        || ConfiguredLocationNames.Contains(name);

    private static bool IsRequiredLocation(string name) => IsBossLocation(name)
        || HildirRoads(name)
        || name == RoadCoastalLandings.Name;

    private static List<(string name, Vector3 position, float radius)> SelectIslandDestinations(
        Island island, List<(string name, Vector3 position, float radius)> locations, int quota)
    {
        var candidates = new List<(string name, Vector3 position, float radius)>(locations);
        // Island ranking has already happened using real POIs only. Landings
        // consume quota slots like required locations; they never select an island.
        // A landing in a zone the network goes round is not one (NetworkCutoff.Avoids), as a location there is not.
        candidates.AddRange(island.CoastalLandings.Where(p => !NetworkCutoff.Avoids(p))
            .Select(p => (RoadCoastalLandings.Name, p, RoadCoastalLandings.Radius)));
        var selected = SelectLocations(candidates, quota, island.ApproxArea);
        Log.LogDebug($"Coastal landings on island {island.Id}: {selected.Count(p => p.name == RoadCoastalLandings.Name)} selected; connection and boat access are not guaranteed");
        return selected;
    }

    /// <summary>Up to <paramref name="count"/> replacements on <paramref name="island"/> for destinations that ended unreached
    /// (<see cref="ReplaceUnreached"/>), by the selection's own rule (<see cref="RoadNetworkSelection.Replacements"/>) over the
    /// same candidates as <see cref="SelectIslandDestinations"/>: never one already chosen or tried
    /// (<paramref name="tried"/>), one the network already reaches, nor one that would get no road
    /// (<see cref="PlacedLocations.NoRoadAsReplacement"/>).</summary>
    private static List<(string name, Vector3 position, float radius)> SelectReplacements(Island island,
        List<(string name, Vector3 position, float radius)> locations, IReadOnlyList<(string name, Vector3 position, float radius)> kept,
        ISet<(float, float)> tried, int count)
    {
        var candidates = new List<(string name, Vector3 position, float radius)>(locations);
        candidates.AddRange(island.CoastalLandings.Where(p => !NetworkCutoff.Avoids(p))
            .Select(p => (RoadCoastalLandings.Name, p, RoadCoastalLandings.Radius)));
        bool Excluded((string name, Vector3 position, float radius) c)
        {
            var centre = new Vector2(c.position.x, c.position.z);
            return tried.Contains(LocationKey(centre)) || PlacedLocations.NoRoadAsReplacement(centre) || Reached(c.position, c.radius, c.name);
        }
        return RoadNetworkSelection.Replacements(candidates, kept, Excluded, count, island.ApproxArea, GetLocationPriority,
            GenerationWorld?.GetSeed() ?? 0, GenerationOptions);
    }

    private static List<(string name, Vector3 position, float radius)> SelectLocations(
        List<(string name, Vector3 position, float radius)> candidates, int maxCount, float area = 0) =>
        RoadNetworkSelection.Destinations(candidates, maxCount, area, GetLocationPriority,
            IsRequiredLocation, GenerationWorld?.GetSeed() ?? 0, GenerationOptions);

    private static int GetLocationPriority(string locationName)
    {
        if (locationName == RoadCoastalLandings.Name) return RoadCoastalLandings.Priority;
        if (LocationPriorities.TryGetValue(locationName, out int priority))
            return priority;

        if (IsCustomLocation(locationName))
            return CustomLocationPriority;

        return DefaultPriority;
    }

    #endregion

    #region Island Road Strategies

    private static void GenerateIslandRoads(
        Island island,
        List<(string name, Vector3 position, float radius)> islandLocations,
        Vector3? overrideStart = null,
        float overrideStartRadius = 0f,
        Replacer? replace = null)
    {
        RoadLifecycleManager.ThrowIfCancellationRequested();
        if (islandLocations.Count == 0) return;
        
        Vector3 startPos;
        float startRadius;
        if (overrideStart.HasValue)
        {
            startPos = overrideStart.Value;
            startRadius = overrideStartRadius;
        }
        else
        {
            Vector2 edge = island.GetEdgePoint();
            startPos = new Vector3(edge.x, 0, edge.y);
            startRadius = 0f;
        }
        
        Log.LogDebug($"Island {island.Id}: {islandLocations.Count} locations");
        
        IslandActivity?.Stage("Connecting");
        GenerateRoutedRoads(startPos, startRadius, islandLocations, replace, island.Id);
    }

    #endregion

    #region Utility Methods

    /// <summary>
    /// Everything that reads Unity (location prefabs out of AssetBundles, the
    /// location footprint index, saved placement heights) done here, on the main
    /// thread, and then preparation sealed, so a worker that misses names the
    /// miss instead of loading an asset: Unity refuses asset loads off the
    /// main thread. Throws, having logged why, when the footprint index could
    /// not be built (PrepareAndSeal returns false instead). The caller must
    /// Unseal() in a finally.
    /// </summary>
    private static IEnumerable<bool> PrepareAndSealSteps(RoadPreparation preparation)
    {
        if(LocationLevelling.PrimeIncremental!=null && (LocationLevelling.CanPrimeIncrementally?.Invoke() ?? true))
            foreach(var step in RoadPreparation.Own(LocationLevelling.PrimeIncremental(preparation))) yield return step;
        else
        {
            preparation.BeginStage("Preparing location footprints"); yield return true;
            foreach(var step in RoadLocationProtection.PrimeSteps(preparation)) yield return step;
            preparation.BeginStage("Preparing saved placements"); yield return true;
            LocationLevelling.Prime?.Invoke();
        }
        if(RoadLocationProtection.InstalledButUnprepared)
        {
            Log.LogError("the location footprint index could not be built, so no road could avoid a POI. " +
                         "No roads were generated. This is a gap in preparation, not in the world.");
            throw new InvalidOperationException("Road location preparation failed; no network was published.");
        }
        preparation.BeginStage("Finishing road preparation"); yield return true;
        LocationLevelling.Seal();
    }

    private static void ReportMissesAfterSealing()
    {
        if (LocationLevelling.MissesAfterSealing > 0)
            Log.LogError($"{LocationLevelling.MissesAfterSealing} lookup(s) arrived after preparation closed; " +
                         "roads near those locations meet natural terrain. See the errors above for what was missing.");
    }

    /// <summary>Told when the network is reset (the world unloaded, or regenerated).</summary>
    public static Action? OnReset;

    public static void Reset()
    {
        bool cancelled = RoadLifecycleManager.IsGenerating;
        RoadLifecycleManager.CancelAndDrain();
        RoadLifecycleManager.AllowInitialRetry();
        if (!cancelled) ResetData();
        RoadNetworkPersistence.Reset();
    }

    /// <summary>
    /// Everything one generation starts afresh: its world and options, its searches (one per worker), and every counter and
    /// per-location note it keeps. The one reset a whole-world generation and an island's regeneration share.
    /// </summary>
    private static void ResetGenerationState(WorldGenerator world, RoadNetworkOptions options)
    {
        m_generationOptions = options;
        m_generationWorld = world;
        float clearance = RoadLocationProtection.Clearance(RoadWidth);
        m_pathfinder = NewFinder(world, clearance);
        m_threadPathfinders?.Dispose();
        m_threadPathfinders = new System.Threading.ThreadLocal<RoadPathfinder>(() => NewFinder(world, clearance), true);
        ResetGenerationCounters();
        ResetDoors();
        RoadLocationProtection.Reset();
        LocationWaysIn.Reset();
    }

    /// <summary>The counters and per-location notes of a generation (logs, reports, retry budgets per location).</summary>
    private static void ResetGenerationCounters()
    {
        CrossIslandReads.Reset();
        RoadTerrainSamples.ResetTotals();
        SearchStats.Reset();
        m_destinationsSelected = 0;
        m_destinationsConnected = 0;
        m_destinationsNotPlaced = 0;
        m_replacementsTried = m_replacementsReached = 0;
        RestoreReachedLocations();
        m_reachedOffLevel = 0;
        m_behindRidge = 0;
        m_carriedToEdge = 0;
        m_roadsGeneratedCount = 0;
        m_roadsRefusedForGrade = 0;
        m_refusalReasons.Clear();
        m_lastFailure.Clear();
        ClearStairNotes();
        m_builtTo.Clear();
        m_entryStairsFailed.Clear();
        m_entryStairsTried.Clear();
        EntryStairLayouts = 0;
        m_roadsWithNoRoute = 0;
        m_earthworkReroutes = 0;
        s_earthworkPoints = s_earthworkReads = s_earthworkCalls = s_earthworkFits = s_earthworkKnown = 0;
        m_ridgeReroutes = 0;
        m_ridgeStairs = 0;
        m_pullReroutes = 0;
        m_offLandEnds = 0;
        RoadCrossingDetector.DeckLineWalkedIn = RoadCrossingDetector.DeckLineRefused = RoadCrossingDetector.DeckLineSpanDropped = 0;
        RoadPitches.ResetCounters();
        RoadSpatialGrid.ResetLocationLandings();
        m_corridorSnapped = 0;
        m_reEndedOnApproach = 0;
        m_lastResortDestinations = m_lastResortReached = m_viaductEndsTriedAgain = m_viaductEndsPlanned = 0;
        m_reEndRefused = 0;
        m_junctionFills = 0;
        RoadSpatialGrid.BendFallbacks.Clear();
        m_chainsShared = 0;
        RoadRide.Reset();
        m_humpsLevelled = 0; m_gridFallbacks = 0; m_shapedFirst = 0; m_shapedFirstFellBack = 0;
        m_trialsRepeated = 0;
        RoadSpatialGrid.SwayKept = RoadSpatialGrid.SwayRefused = RoadSpatialGrid.SwayMoreEarth = RoadSpatialGrid.SwayKeptHeld = 0;
        RoadSpatialGrid.SlopeSwayPlans = 0; System.Array.Clear(RoadSpatialGrid.SlopeSwayMillimetres, 0, 3);
        RoadPathfinder.SqueezeHits.Clear(); RoadPathfinder.ShoreFillCells.Clear();
        m_earthworkUnavoided = m_earthworkFinished = 0;
        RoadGrade.SteepestPlanned = 0f;
        m_inLocationSearchedRound = m_inLocationRefused = m_noKitSearchedRound = m_noKitRefused = m_builtSearchedRound = m_builtRefused = m_keptClearSearched = m_keptClearRefused = m_unknownGroundRefused = 0;
        s_noKitReads = s_noKitReadsAsStored = s_noKitBanWritten = s_builtReads = s_builtReadsAsStored = s_inLocationReads = s_inLocationReadsAsStored = 0;
        m_viaductsLaid = m_viaductsRefusedAtEnds = m_crossingsUnmetSearchedRound = m_bridgeEndsUnmet = m_endEarthworks = m_endPads = m_endShared = m_endStands = m_endSteps = m_endFlat = m_endOneStep = m_endMoreSteps = m_pairsStepped = m_pairsFlush = m_pairsOneSided = 0;
    }

    internal static void ResetData()
    {
        ResetGenerationCounters();
        ResetDoors();
        PlacedLocations.ForgetLanded();
        RoadOwners.Clear();
        OnReset?.Invoke();
        ManualRoads.Reset();
        LocationLevelling.ResetPlacements?.Invoke();
        m_roadsGenerated = false;
        m_locationsReady = false;
        m_roadsLoadedFromZDO = false;
        m_networkDirty = false;
        m_networkReceived = false;
        m_kitsChosen = false;
        ReleasePathfinders();
        RoadLocationProtection.Reset();
        m_roadStartPoints.Clear();
        m_roadCrossings.Clear();
        lock (m_recordGate) m_coastalLandings.Clear();
        StairStructures.Reset();
        LocationWaysIn.Reset();
        BuiltBridges.Reset();
        ForgetViaducts();
        RoadStairRuns.ResetCache();
        StructurePlans.Reset();
        VegetationClearing.Reset();
        RoadSpatialGrid.Clear();
    }

    private static void LogGenerationStats(int roadsGenerated, TimeSpan elapsed)
    {
        var log = Log;

        log.LogDebug("=== Road Generation Summary ===");
        log.LogDebug($"  Roads generated: {roadsGenerated}");
        log.LogDebug($"  Stored road-point records: {RoadSpatialGrid.StoredPointRecords}");
        log.LogDebug($"  Committed planned-path length: {RoadSpatialGrid.Statistics.LengthText()}");
        log.LogDebug($"  Grid cells with roads: {RoadSpatialGrid.GridCellsWithRoads}");

        if (roadsGenerated > 0)
        {
            log.LogDebug($"  Avg stored records/road: {RoadSpatialGrid.StoredPointRecords / (float)roadsGenerated:F0}");
            log.LogDebug($"  Avg committed length/road: {RoadSpatialGrid.Statistics.LengthText(divisor:roadsGenerated)}");
        }

        log.LogDebug($"  Generation time: {elapsed.TotalSeconds:F2}s");
        log.LogDebug($"  Road width: {RoadWidth}m");
        log.LogDebug($"  Max grade: {RoadGrade.Configured.ToString("P0")}");
        log.LogDebug($"  Destinations dropped: {m_roadsWithNoRoute} with no route, {m_roadsRefusedForGrade} refused when the profile was planned (grade, turn room, footprint or water)");
        log.LogDebug($"  Steepest road built: {RoadGrade.SteepestPlanned:P1}");
        log.LogDebug("===============================");
    }

    /// <summary>
    /// Trims a path so it stops at the exterior radius of both endpoints.
    /// </summary>
    private static List<Vector2>? TrimPathToRadii(List<Vector2> path, Vector2 startCenter, float startRadius, Vector2 endCenter, float endRadius)
    {
        if (path == null || path.Count < 2)
            return null;

        int startIndex = 0;
        float startRadiusSq = startRadius * startRadius;
        for (int i = 0; i < path.Count; i++)
        {
            if ((path[i] - startCenter).sqrMagnitude > startRadiusSq)
            {
                startIndex = i;
                break;
            }
        }

        int endIndex = path.Count - 1;
        float endRadiusSq = endRadius * endRadius;
        for (int i = path.Count - 1; i >= 0; i--)
        {
            if ((path[i] - endCenter).sqrMagnitude > endRadiusSq)
            {
                endIndex = i;
                break;
            }
        }

        if (endIndex <= startIndex)
            return null;

        var trimmedPath = new List<Vector2>();

        if (startIndex > 0 && startIndex < path.Count)
        {
            Vector2 edgePoint = CalculateRadiusIntersection(path[startIndex], startCenter, startRadius);
            trimmedPath.Add(edgePoint);
        }

        for (int i = startIndex; i <= endIndex; i++)
        {
            trimmedPath.Add(path[i]);
        }

        if (endIndex < path.Count - 1 && endIndex >= 0)
        {
            Vector2 edgePoint = CalculateRadiusIntersection(path[endIndex], endCenter, endRadius);
            trimmedPath.Add(edgePoint);
        }

        return trimmedPath.Count >= 2 ? trimmedPath : null;
    }

    /// <summary>
    /// Calculates the point on the radius circle in the direction from center to the given point.
    /// </summary>
    private static Vector2 CalculateRadiusIntersection(Vector2 outsidePoint, Vector2 center, float radius)
    {
        Vector2 direction = (outsidePoint - center).normalized;
        return center + direction * radius;
    }

    #endregion

    #region Persistence

    /// <summary>
    /// Unique prefab name for our metadata ZDO. Must not conflict with any game prefabs.
    /// This is public so Plugin.cs can register the prefab with Jotunn.
    /// </summary>
    public const string MetadataPrefabName = RoadNetworkPersistence.MetadataPrefabName;

    /// <summary>
    /// Save the entire road network to a dedicated ZDO for persistence across world reloads.
    /// Call this on world save.
    /// </summary>
    public static void SaveGlobalRoadData()
    {
        if (IsGenerating || !RoadLifecycleManager.HoldsTheNetwork) return;
        if (!m_networkDirty)
        {
            Log.LogDebug("[SAVE] No roads generated or appended, skipping global save");
            return;
        }
        RoadNetworkPersistence.SaveGlobalRoadData(m_roadStartPoints, m_roadCrossings, StructurePlans.SpawnedZones, GetCoastalLandings());
    }

    /// <summary>
    /// Save only which zones have their bridge pieces (bridges prototype).
    /// For a session whose network was loaded, not generated: the network
    /// itself is unchanged, but zones spawned this session must be
    /// remembered, or a bridge whose pieces were all destroyed comes back.
    /// </summary>
    public static void SaveBridgeZones()
    {
        if (IsGenerating) return;
        if (!RoadLifecycleManager.WritesTheNetwork)
            return;
        // The crossings too once a kit was chosen (whether they may be rewritten is the persistence's to say).
        RoadNetworkPersistence.SaveBridgeZones(StructurePlans.SpawnedZones, m_kitsChosen ? m_roadCrossings : null);
    }

    /// <summary>Whether a crossing's kit was chosen this session (<see cref="ChooseKitsAsBuilt"/>): the crossings are saved
    /// with the zones that have their pieces.</summary>
    private static bool m_kitsChosen;

    /// <summary>
    /// The kits of the bridges these pieces belong to chosen as the server builds them (BridgeStructures.ChooseKits), on
    /// the network's records, and saved at the next save: a bridge built keeps its kit, one not built yet takes its kit
    /// from the kits installed when it is.
    /// </summary>
    internal static void ChooseKitsAsBuilt(IEnumerable<StructurePiece> pieces)
    {
        if (WorldGenerator.instance is not WorldGenerator world) return;
        lock (m_recordGate)
            if (BridgeStructures.ChooseKits(pieces, world, m_roadCrossings)) m_kitsChosen = true;
    }

    /// <summary>The natural ground locations placed before their roads are made ready on (<see cref="PlacedLocations.Prepare"/>), at
    /// a generation and at a load alike.</summary>
    private static Func<float, float, float> NaturalGround(WorldGenerator world) => (x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world);

    /// <summary>
    /// Try to load the entire road network from persisted ZDO.
    /// Call this on world load before road generation would trigger.
    /// </summary>
    /// <returns>True if road data was found and loaded</returns>
    public static bool TryLoadGlobalRoadData()
    {
        if (IsGenerating) return false;
        var bridgeZones = new HashSet<Vector2s>();
        bool loaded = RoadNetworkPersistence.TryLoadGlobalRoadData(m_roadStartPoints, m_roadCrossings, bridgeZones, m_coastalLandings);
        if (loaded) UseReadNetwork(bridgeZones);
        return loaded;
    }

    /// <summary>
    /// A client: read the network the server sent (<paramref name="records"/>, RoadNetworkSync) through the one reader
    /// (RoadNetworkPersistence.TryLoadReceived), in place of whatever this client held. True when it was read; refused, this
    /// client holds no network (never one it generates: a client generates none, RoadLifecycleManager.HoldsTheNetwork).
    /// </summary>
    internal static bool LoadReceivedNetwork(IReadOnlyDictionary<int, byte[]> records)
    {
        if (RoadLifecycleManager.HoldsTheNetwork || IsGenerating) return false;
        ForgetReceivedNetwork();
        var bridgeZones = new HashSet<Vector2s>();
        if (!RoadNetworkPersistence.TryLoadReceived(records, m_roadStartPoints, m_roadCrossings, bridgeZones, m_coastalLandings))
        {
            ForgetReceivedNetwork();
            return false;
        }
        UseReadNetwork(bridgeZones);
        m_networkReceived = true;
        return true;
    }

    /// <summary>The server: the records a client is sent (RoadNetworkPersistence.SentRecords), of the network in memory.</summary>
    internal static List<(string name, byte[] bytes)> SentRecords()
    {
        var records = new List<(string name, byte[] bytes)>();
        foreach (var (name, key) in RoadNetworkPersistence.SentRecords)
            if (RoadNetworkPersistence.CurrentRecord(key, m_roadStartPoints) is byte[] bytes) records.Add((name, bytes));
        return records;
    }

    /// <summary>A client: the server's network is gone (a generation started there) or its package was refused: none here.</summary>
    internal static void ForgetReceivedNetwork()
    {
        if (RoadLifecycleManager.HoldsTheNetwork) return;
        // The world's locations are as ready as they were: only the network goes.
        bool locationsReady = m_locationsReady;
        ResetData();
        m_locationsReady = locationsReady;
    }

    /// <summary>What a network read (saved, or received) needs in memory before anything reads it.</summary>
    private static void UseReadNetwork(HashSet<Vector2s> bridgeZones)
    {
        {
            // The locations placed before the roads ready again, on the ground the generation made them ready on: a staircase
            // whose first zone is built this session is dressed as it would have been in the session that generated it.
            if (WorldGenerator.instance != null) PlacedLocations.Prepare(NaturalGround(WorldGenerator.instance));
            else if (PlacedLocations.Count > 0)
                Log.LogWarning($"[LOAD] {PlacedLocations.Count} location(s) placed before their roads not made ready: no world generator yet; their staircases are dressed on the natural ground");
            // Before anything may write: which zones this network must leave alone. Only on the peer that holds the network:
            // a client writes nothing, and reads only the package (the metadata ZDO it happens to see is not the server's).
            if (RoadLifecycleManager.HoldsTheNetwork)
                NetworkCutoff.UseLoadedNetwork(RoadSpatialGrid.RoadNetworkVersion, RoadNetworkPersistence.LoadZonesWithNetwork());
            // Zones whose pieces stand keep them, whatever layout made them.
            StructurePlans.MarkSpawned(bridgeZones);
            // What later roads (a manual one) keep clear of the saved viaducts, as the session that laid them did: their
            // ends and clearance, their decks and the cells under them.
            ForgetViaducts();
            KeepClearOfViaducts(m_roadCrossings);
        }
    }

    /// <summary>
    /// Save which zones were generated with this network in place
    /// (NetworkCutoff), so the next session knows which zones it may write.
    /// </summary>
    public static void SaveNetworkCutoff()
    {
        if (IsGenerating || !RoadLifecycleManager.WritesTheNetwork || ZoneSystem.instance == null) return;
        byte[]? record = NetworkCutoff.Record(RoadSpatialGrid.RoadNetworkVersion, ZoneSystem.instance.m_generatedZones);
        if (record != null) RoadNetworkPersistence.SaveZonesWithNetwork(record);
    }

    /// <summary>
    /// Save which zones' vegetation matches the network (VegetationClearing),
    /// so a zone is cleared once per network and not again on every start.
    /// </summary>
    public static void SaveClearedZones()
    {
        if (IsGenerating) return;
        // Written as it is: an append moved it as it was published (VegetationClearing.AppendPublished), and a record
        // still of another network is not this one's to write.
        if (!RoadLifecycleManager.WritesTheNetwork || VegetationClearing.Version == 0 ||
            VegetationClearing.Version != RoadSpatialGrid.RoadNetworkVersion)
            return;
        RoadNetworkPersistence.SaveClearedZones(VegetationClearing.Version, VegetationClearing.ClearedZones, VegetationClearing.ClearedThrough);
    }

    #endregion
}
