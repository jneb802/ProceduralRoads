using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Host-side append planning. All engine-facing command/sync work is in ManualRoadCommands.</summary>
public static class ManualRoads
{
    private static readonly Dictionary<string, ManualRoadDraft> drafts = new();
    private static List<Island>? islands;
    private static int worldEpoch;
    public static bool Busy { get; private set; }
    public static ManualRoadDraft Draft(string actor)
    {
        if (!drafts.TryGetValue(actor, out var draft)) drafts.Add(actor, draft = new ManualRoadDraft());
        return draft;
    }
    public static void Reset()
    {
        drafts.Clear(); islands = null; worldEpoch++; Busy = false; s_confirm = null; s_locationConfirm = null;
        ManualRoadCommands.ResetPending();
        StructureAppendQueue.Reset();
    }

    public sealed class Plan
    {
        internal int Epoch, Version;
        internal List<RoadSpatialGrid.PlannedPath> Paths = new();
        internal List<RoadCrossing> Crossings = new();
        /// <summary>The staircases the road's search bought, laid by the generator's own rule.</summary>
        internal List<RoadStairSpan> Stairs = new();
        /// <summary>Its append as planned (RoadNetworkGenerator.AppendOf), with the zones it writes: committed as it is, on
        /// the network it was planned on.</summary>
        internal RoadNetworkGenerator.ManualAppend? Append;
        public IReadOnlyList<RoadCrossing> NewCrossings => Crossings.AsReadOnly();
        public float Length => Paths.Sum(p => p.TotalLength);
        public bool AlreadyConnected { get; internal set; }
        public Vector2 Start { get; internal set; }
        /// <summary>After Commit: the zones explored before the network that this road was built through.</summary>
        public IReadOnlyList<Vector2s> ExploredZonesWritten { get; internal set; } = Array.Empty<Vector2s>();
        /// <summary>The zones players changed (<see cref="PlayerChanges"/>) that this road writes: only those of the
        /// operator's own points, unless forced. Built only once confirmed (<see cref="ConfirmPlayerZones"/>).</summary>
        public IReadOnlyList<Vector2s> PlayerZones { get; internal set; } = Array.Empty<Vector2s>();
        /// <summary>Planned with force: through whatever stands, no confirmation asked.</summary>
        public bool Forced { get; internal set; }
        /// <summary>Where it meets the network (road_connect), or its last mark.</summary>
        public Vector2 End { get; internal set; }
        /// <summary>road_connect from a location (<see cref="LocationAt"/>): the location it was planned from.</summary>
        internal Location? From { get; set; }
        /// <summary>road_connect: the roads its planning refused before this one (RoadNetworkGenerator.RoadCapture).</summary>
        internal IReadOnlyList<(string why, Vector2? at)> Refused { get; set; } = Array.Empty<(string, Vector2?)>();
        /// <summary>road_connect: its ends arriving at their locations as road, ways into them once committed (LocationWaysIn.Record),
        /// as a generated road's are once stored.</summary>
        internal IReadOnlyList<(Vector2 location, LocationWaysIn.Way way)> WaysIn { get; set; } = Array.Empty<(Vector2, LocationWaysIn.Way)>();
    }

    /// <summary>A location road_connect was run inside (<see cref="LocationAt"/>): the network is joined from it, as the
    /// generator joins a location (RoadNetworkGenerator.JoinLocation).</summary>
    internal sealed class Location
    {
        public readonly string Name;
        public readonly Vector2 Centre;
        /// <summary>Its exterior radius, as generation's destinations carry it.</summary>
        public readonly float Radius;
        public Location(string name, Vector2 centre, float radius) { Name = name; Centre = centre; Radius = radius; }
        public override string ToString() => FormattableString.Invariant($"{Name} at ({Centre.x:F0}, {Centre.y:F0})");
    }

    /// <summary>How long a warning about zones players changed waits for the same command again.</summary>
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromMinutes(2);
    private static (string key, DateTime until)? s_confirm;

    /// <summary>
    /// Whether this road may be built into the zones players changed that it writes: none, forced, or the operator
    /// ran the same command (same actor, same operation, same zones) again within <see cref="ConfirmWindow"/> of the
    /// warning. A first run is remembered and answered false.
    /// </summary>
    public static bool ConfirmPlayerZones(string actor, string operation, Plan plan, DateTime now)
    {
        if (plan.Forced || plan.PlayerZones.Count == 0 || plan.AlreadyConnected) return true;
        return ConfirmAgain($"{actor}|{operation}|" + string.Join(";", plan.PlayerZones.Select(z => $"{z.x},{z.y}")), now);
    }

    /// <summary>The one confirm: true when <paramref name="key"/> (the same actor, operation and subject) was asked within
    /// <see cref="ConfirmWindow"/> before, and forgets it; else remembers it and answers false. Also asked by
    /// road_generate and road_regen_island (<see cref="RoadLifecycleManager.BeginForOperator"/>).</summary>
    internal static bool ConfirmAgain(string key, DateTime now)
    {
        if (s_confirm is { } pending && pending.key == key && now <= pending.until) { s_confirm = null; return true; }
        s_confirm = (key, now + ConfirmWindow);
        return false;
    }

    /// <summary>Whether a command's words end in force (the last word): the one reading of it, for the manual roads and
    /// road_generate and road_regen_island.</summary>
    internal static bool EndsInForce(IReadOnlyList<string> words) => words.Count > 0 && words[words.Count - 1] == "force";

    /// <summary>The road island at <paramref name="p"/>, as the manual roads read the islands (detected once per world), or
    /// null: what road_regen_island's confirm is keyed on.</summary>
    internal static Island? IslandAt(Vector3 p) => (islands ??= IslandDetector.DetectRoadIslands()).FirstOrDefault(i => i.ContainsPoint(p));

    private static (string key, DateTime until)? s_locationConfirm;

    /// <summary>
    /// Whether road_connect may join the network from <paramref name="location"/>: the same actor ran it inside the same location
    /// within <see cref="ConfirmWindow"/> of the warning (<see cref="InsideLocationWarning"/>). A first run is remembered and
    /// answered false. Kept, not spent, while the window lasts: a run held back for the zones players changed
    /// (<see cref="ConfirmPlayerZones"/>) and run again is still from the location; forgotten once its road is built
    /// (<see cref="ForgetLocation"/>).
    /// </summary>
    internal static bool ConfirmLocation(string actor, Location location, DateTime now)
    {
        string key = FormattableString.Invariant($"{actor}|connect|{location.Name}|{location.Centre.x:F1},{location.Centre.y:F1}");
        bool again = s_locationConfirm is { } pending && pending.key == key && now <= pending.until;
        s_locationConfirm = (key, now + ConfirmWindow);
        return again;
    }

    internal static void ForgetLocation() => s_locationConfirm = null;

    /// <summary>What the operator is told the first time road_connect is run inside a location.</summary>
    internal static string InsideLocationWarning(Vector2 at, Location location) => FormattableString.Invariant(
        $"Not built yet: {at.x:F0}, {at.y:F0} is inside {location} or its road clearance. Run the same command again within {ConfirmWindow.TotalMinutes:F0} minutes to connect {location.Name} to the network from its landing or approach, as the generator would.");

    /// <summary>
    /// The location whose footprint or road clearance (RoadLocationProtection) holds <paramref name="point"/>, as generation's
    /// destinations carry it (its location's name and exterior radius); null where none does. A point in a footprint
    /// that is no location a road may lead to is refused: a location the game leaves for a player to find
    /// (PlacedLocations.PlayerDecides), one whose landing is not known (PlacedLocations.NotPlacedWhy), or a footprint the one
    /// resolver (LocationPrefabLevelling.LocationAt) finds no single location at (where two stand there, the refusal says
    /// so: LocationPrefabLevelling.TwoLocations). The location is carried by its footprint's
    /// centre from here on (its identity, RoadLocationProtection.Footprint.Is).
    /// </summary>
    internal static Location? LocationAt(Vector2 point)
    {
        float clearance = RoadLocationProtection.Clearance(RoadNetworkGenerator.RoadWidth);
        if (!RoadLocationProtection.BlocksSegment(point, point, clearance, default)) return null;
        string outside = $"Waypoint {point} is inside a protected POI or its road clearance. Mark a point outside its edge.";
        if (!RoadLocationProtection.NearestFootprint(point, clearance, out RoadLocationProtection.Footprint footprint))
            throw new ArgumentException(outside);
        if (!LocationPrefabLevelling.LocationAt(footprint.Centre, out ZoneSystem.LocationInstance instance, out string? two))
            throw new ArgumentException(two == null ? outside : FormattableString.Invariant(
                $"Waypoint {point} is inside the location at {footprint.Centre.x:F1}, {footprint.Centre.y:F1}, where {two}: no road leads to it. Mark a point outside its edge."));
        if (instance.m_location?.m_prefab == null) throw new ArgumentException(outside);
        var zoneLocation = instance.m_location;
        var location = new Location(zoneLocation.m_prefab.Name, footprint.Centre, zoneLocation.m_exteriorRadius);
        if (PlacedLocations.PlayerDecides(location.Name, zoneLocation.m_unique, zoneLocation.m_iconPlaced, zoneLocation.m_iconAlways))
            throw new ArgumentException($"{location} is a location the game leaves for players to find: no road leads to it. Mark a point outside its edge.");
        if (PlacedLocations.NotPlacedWhy(location.Centre) is string why)
            throw new ArgumentException($"{location} is given no road: its landing is not known ({why}). Mark a point outside its edge.");
        return location;
    }

    /// <summary>What the operator is told as road_connect starts its search.</summary>
    internal static string ConnectStarting(Vector2 at) => FormattableString.Invariant(
        $"Trying to build a road from {at.x:F0}, {at.y:F0} to the network \u2013 this may take some time and the game may pause while we search.");

    /// <summary>road_connect's one line where no road was planned: the main reason and where, with how many roads were
    /// tried and why each was refused (RoadNetworkGenerator.RoadCapture), and where any was refused for writing into zones
    /// players changed (<paramref name="protectedZones"/>, RoadNetworkGenerator.RoadCapture.Protected), those zones and how
    /// to build into them (<see cref="ProtectedZonesSaid"/>).</summary>
    internal static string ConnectRefusal(Vector2 from, IReadOnlyList<(string why, Vector2? at)> refused,
        IReadOnlyCollection<Vector2s>? protectedZones = null)
    {
        string Said(string why) => why.StartsWith("refused: ", StringComparison.Ordinal) ? why.Substring("refused: ".Length) : why;
        var text = FormattableString.Invariant($"no road could be planned from {from.x:F0}, {from.y:F0} to the network");
        if (refused.Count == 0) return text + " within the routing limits. Nothing added.";
        var reasons = refused.GroupBy(r => Said(r.why)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        var main = reasons[0];
        text += ": " + main.Key;
        if (main.Select(r => r.at).FirstOrDefault(a => a.HasValue) is Vector2 where) text += FormattableString.Invariant($" at ({where.x:F0}, {where.y:F0})");
        if (refused.Count > 1)
            text += $". Tried {refused.Count} roads: " + string.Join(", ", reasons.Select(g => g.Count() > 1 ? $"{g.Key} ({g.Count()})" : g.Key));
        text += ".";
        if (protectedZones != null && protectedZones.Count > 0) text += " " + ProtectedZonesSaid(protectedZones);
        return text + " Nothing added.";
    }

    /// <summary>What the operator is told of a road refused for writing into zones players changed
    /// (RoadNetworkGenerator.ProtectedZoneRefusal, its zones RoadNetworkGenerator.Refusal.Zones): the zones, and how to build
    /// into them. A marked road and road_connect alike.</summary>
    internal static string ProtectedZonesSaid(IEnumerable<Vector2s> zones) =>
        "The road would reach zone(s) players changed (buildings or terrain edits): " +
        string.Join(", ", zones.OrderBy(z => z.x).ThenBy(z => z.y).Select(z => $"({z.x}, {z.y})")) +
        ". Mark a point in a zone to build into it, or add force.";

    /// <summary>
    /// Whether a manual road command's exception is one of its refusals, said in one line: the plain InvalidOperationException
    /// or ArgumentException the mod throws on purpose. Anything else, or one the runtime threw (a collection changed under
    /// an enumeration), is a defect, logged with its stack.
    /// </summary>
    internal static bool IsRefusal(Exception ex) =>
        (ex.GetType() == typeof(InvalidOperationException) || ex.GetType() == typeof(ArgumentException))
        && ex.TargetSite?.DeclaringType?.Namespace is string space && (space == "ProceduralRoads" || space.StartsWith("ProceduralRoads.", StringComparison.Ordinal));

    /// <summary>What the operator is told to confirm a command held back.</summary>
    internal static string RepeatHint(string does = "build it") =>
        $" Run the same command again within {ConfirmWindow.TotalMinutes:F0} minutes to {does}, or add force.";

    /// <summary>What the operator is told about the zones players changed that a road writes.</summary>
    public static string PlayerZonesWarning(IReadOnlyList<Vector2s> zones, bool built) =>
        (built ? $"Warning: this road was built through {zones.Count} zone(s) players changed (buildings or terrain edits), "
               : $"Not built yet: your position or a mark is in {zones.Count} zone(s) players changed (buildings or terrain edits). ")
        + (built ? "over whatever stood on its line. " : "The road would be built there in full, over whatever stands on its line. ")
        + "Zones: " + string.Join(", ", zones.Select(z => $"({z.x}, {z.y})")) + "."
        + (built ? "" : RepeatHint());

    /// <summary>
    /// Every zone a manual road writes, read off the append it commits (<see cref="RoadNetworkGenerator.AppendOf"/>): each
    /// zone the levelling of its points, junction fills, stair start pads and the ground under its decks reaches (as
    /// RoadSpatialGrid.GetRoadPointsInZone gathers points for a zone), each zone its cuts and the other roads' points it
    /// raises reach, and each zone its new staircases and bridges put pieces in.
    /// </summary>
    internal static HashSet<Vector2s> ZonesReached(IReadOnlyList<RoadSpatialGrid.PlannedPath> paths, List<RoadCrossing> crossings,
        IReadOnlyList<RoadStairSpan>? stairs = null) =>
        ZonesReached(RoadNetworkGenerator.AppendOf(paths, crossings, stairs ?? Array.Empty<RoadStairSpan>()), crossings);

    internal static HashSet<Vector2s> ZonesReached(RoadNetworkGenerator.ManualAppend append, IEnumerable<RoadCrossing> crossings)
    {
        if (append.Zones != null) return append.Zones;
        var zones = new HashSet<Vector2s>();
        void Reach(Vector2 p, float width) => ReachedBy(p, width, zones);
        // A cut-only point as far as its cut and batter reach (RoadTerrainModifier.CutReach).
        void ReachBy(Vector2 p, float reach) => NetworkCutoff.ZonesOver(p - new Vector2(reach, reach), p + new Vector2(reach, reach), zones);
        // Its staircases and bridges by their pieces' bodies (StructurePlans.ZonesOf, as they are held back by), a staircase's
        // walking boxes by their boxes.
        foreach (StairRun run in append.Runs.Select(r => r.run))
        {
            zones.UnionWith(StructurePlans.ZonesOf(run.Pieces, debris: true));
            foreach (StairClearance box in run.Clearances)
            {
                Vector2 c = new(box.Centre.x, box.Centre.z), right = StairLayout.Right(box.YawDegrees), forward = StairLayout.Forward(box.YawDegrees);
                Vector2 half = new(Mathf.Abs(right.x) * box.Half.x + Mathf.Abs(forward.x) * box.Half.z, Mathf.Abs(right.y) * box.Half.x + Mathf.Abs(forward.y) * box.Half.z);
                NetworkCutoff.ZonesOver(c - half, c + half, zones);
            }
        }
        foreach (RoadSpatialGrid.PlannedPath path in append.WithFills.Concat(append.IntoLocation))
            for (int i = 0; i < path.Points.Count; i++)
                Reach(path.Points[i], path.WidthAt(i));
        foreach (Vector3 cut in append.StairCuts) ReachBy(new Vector2(cut.x, cut.z), RoadTerrainModifier.StairCutReach + RoadTerrainModifier.CutGatherPad);
        foreach (Vector3 cut in append.Ground.Cuts) Reach(new Vector2(cut.x, cut.z), RoadNetworkGenerator.CrossingGround.CutWidth);
        foreach (Vector3 cut in append.Ground.StairCuts) ReachBy(new Vector2(cut.x, cut.z), RoadTerrainModifier.StairCutReach + RoadTerrainModifier.CutGatherPad);
        foreach (var (at, _) in append.Ground.Raised) Reach(at.p, at.w);
        var world = WorldGenerator.instance;
        if (world != null)
            foreach (RoadCrossing crossing in BridgeLayout.DistinctSites(crossings.ToList()))
                zones.UnionWith(StructurePlans.ZonesOf(BridgeStructures.PiecesOf(crossing, world, world.GetSeed()), debris: true));
        // Read once: the plan's guard and the commit's admission ask the same append.
        append.Zones = zones;
        return zones;
    }

    /// <summary>The zones a road point stored <paramref name="width"/> wide writes, into <paramref name="zones"/>: as far as a zone
    /// gathers it (RoadSpatialGrid.ZoneReach, the writer's reach). For <see cref="ZonesReached"/> and the generator's re-routes
    /// round a protected zone alike.</summary>
    internal static void ReachedBy(Vector2 p, float width, ISet<Vector2s> zones)
    {
        float reach = RoadSpatialGrid.ZoneReach(width);
        NetworkCutoff.ZonesOver(p - new Vector2(reach, reach), p + new Vector2(reach, reach), zones);
    }

    /// <summary>What the operator is told when a manual road is built through zones explored before the network.</summary>
    public static string ExploredZonesWarning(IReadOnlyList<Vector2s> zones) =>
        NetworkCutoff.AdmissionWarning("this road crosses", zones);

    /// <summary>
    /// Plan a manual road. It goes round every zone players changed (<see cref="PlayerChanges"/>), keeping its
    /// levelling out of them, except the zones of its own points (road_connect's position, the marks): the operator
    /// asked for those, and they are built through once confirmed (<see cref="Plan.PlayerZones"/>).
    /// <paramref name="force"/>: through whatever stands, as asked. road_connect is the generator's join
    /// (RoadNetworkGenerator.JoinTo), with every search again it makes; from <paramref name="fromLocation"/>, the location the
    /// point is in (<see cref="LocationAt"/>), as the generator joins that location.
    /// </summary>
    public static Plan Prepare(IReadOnlyList<Vector2> points, bool connect,
        IReadOnlyList<Island>? knownIslands = null, bool force = false) => Prepare(points, connect, knownIslands, force, null);

    internal static Plan Prepare(IReadOnlyList<Vector2> points, bool connect,
        IReadOnlyList<Island>? knownIslands, bool force, Location? fromLocation)
    {
        if (Busy || RoadNetworkGenerator.IsGenerating) throw new InvalidOperationException("Road generation is busy.");
        Busy = true;
        try { return PrepareCore(points, connect, knownIslands, force, fromLocation); }
        finally { Busy = false; }
    }

    /// <summary>
    /// A network a released build saved (format 1) takes no additions: saving
    /// one would rewrite it in a format that build cannot read, and that build
    /// would then throw the network away and generate another over the world.
    /// Nor does a saved network this build could not read (the road would be
    /// saved over it), one a failed generation left out of memory, or one with a saved record this build could not read.
    /// </summary>
    internal static void RefuseOnReleasedNetwork()
    {
        // Only the peer that holds the network adds to it: a client's received network is the server's to change.
        if (!RoadLifecycleManager.HoldsTheNetwork) throw new InvalidOperationException(RoadLifecycleManager.NotTheServer);
        if (RoadNetworkPersistence.LastLoadUnreadable)
            throw new InvalidOperationException("This world's saved road network could not be read, so no road is added to it. " +
                "Use a build that reads it, or road_generate to replace it on purpose.");
        if (!RoadNetworkPersistence.AllowsNetworkChanges)
            throw new InvalidOperationException("This world's roads were saved by ProceduralRoads 1.4 or earlier. " +
                "Adding a road would make them unreadable to that version, so roads cannot be added to this world.");
        // A record kept unread (RoadNetworkPersistence.ReadOrKeep) is written back as it was: what a road added to it would be lost.
        string kept = string.Join(", ", RoadNetworkPersistence.KeptRecords);
        if (kept.Length > 0)
            throw new InvalidOperationException($"This world's saved {kept} could not be read, so no road is added this session.");
        // A generation that failed or was stopped left none in memory: the road would be saved over the world's network.
        if (!RoadLifecycleManager.HasNetwork && RoadNetworkPersistence.SavedNetworkExists)
            throw new InvalidOperationException("This world's saved road network is not loaded (a road generation failed or was " +
                "stopped), so no road is added over it. It loads again on the next spawn or world load, or road_generate replaces it.");
    }

    private static Plan PrepareCore(IReadOnlyList<Vector2> points, bool connect, IReadOnlyList<Island>? knownIslands, bool force, Location? fromLocation)
    {
        RefuseOnReleasedNetwork();
        var world = WorldGenerator.instance;
        if (world == null || !RoadNetworkGenerator.IsLocationsReady)
            throw new InvalidOperationException("Wait for the world and its locations to finish loading.");
        if (connect)
        {
            if (points.Count != 1 || !RoadConstants.InWorld(points[0])) throw new ArgumentException("Connect needs one valid X,Z position.");
            if (!RoadLifecycleManager.HasNetwork || RoadSpatialGrid.StoredPointRecords == 0)
                throw new InvalidOperationException("No road network exists on this island.");
        }
        else ManualRoadDraft.Validate(points);
        float width = RoadNetworkGenerator.RoadWidth;
        foreach (var point in points)
        {
            // From a location, its point is in it (LocationAt): the road leaves from the location, as the generator's does.
            if (fromLocation == null && RoadLocationProtection.BlocksSegment(point, point, RoadLocationProtection.Clearance(width), default))
                throw new ArgumentException($"Waypoint {point} is inside a protected POI or its road clearance. Mark a point outside its edge.");
            if (!RoadWater.OnLand(world, point))
                throw new ArgumentException($"Waypoint {point} is under water; mark usable ground instead.");
        }
        var groups = knownIslands ?? (islands ??= IslandDetector.DetectRoadIslands());
        var island = groups.FirstOrDefault(i => i.ContainsPoint(points[0].x, points[0].y));
        if (island == null) throw new InvalidOperationException("No road island was detected at the first waypoint.");
        bool OnIsland(Vector2 p) => island.ContainsPoint(p.x, p.y);
        if (points.Any(p => !OnIsland(p))) throw new ArgumentException("All waypoints must be on the same island.");
        var plan = new Plan { Epoch = worldEpoch, Version = RoadSpatialGrid.RoadNetworkVersion, Start = points[0], Forced = force,
            End = points[points.Count - 1], From = fromLocation };
        // Where the network left a zone as it is, a terrain compiler there is a player's; elsewhere it is the roads'.
        HashSet<Vector2s> changed = PlayerChanges.Zones(null, PlayerChanges.CompilerIsPlayers);
        var asked = new HashSet<Vector2s>(points.Select(p => ZoneSystem.GetZone(new Vector3(p.x, 0f, p.y))));
        asked.IntersectWith(changed);
        var avoided = new HashSet<Vector2s>(changed);
        avoided.ExceptWith(asked);
        if (force) avoided.Clear();
        using IDisposable avoiding = NetworkCutoff.AvoidWhilePlanning(avoided, RoadSpatialGrid.ZoneReach(width));
        // The generator's search, as it searches for one road: it may buy stair steps and Mistlands viaducts; only where
        // the route comes from (the marks, or the network to connect to) is the operator's.
        var finder = RoadNetworkGenerator.NewFinder(world, RoadLocationProtection.Clearance(width));
        List<RoadStairSpan> stairs;
        // What it writes, as the one list of refusals read it (RoadNetworkGenerator.RoadRefusal): what it stores.
        RoadNetworkGenerator.WrittenRoad written;
        if (connect)
        {
            var targets = RoadNetworkGenerator.JoinTargets(OnIsland);
            if (targets.Count == 0) throw new InvalidOperationException("No road network exists on this island.");
            // Sample hints bound heuristic cost. The success predicate checks ALL
            // actual road points and filters them by island, not just the hints.
            int stride = Math.Max(1, targets.Count / 128);
            var hints = targets.Where((_, i) => i % stride == 0).Take(129).ToList();
            // The generator's join, as it joins a location to the network: from a location its ground and exterior radius, else the
            // point as a location of none, served only where it walks onto the network (the join's own test for a point).
            Vector2 from = fromLocation == null ? points[0] : RoadEndpoint.FindGround(world, fromLocation.Centre, fromLocation.Radius);
            Vector2 centre = fromLocation?.Centre ?? points[0];
            float radius = fromLocation?.Radius ?? 0f;
            Func<bool>? served = fromLocation == null ? null
                : () => RoadNetworkGenerator.Reached(new Vector3(centre.x, 0f, centre.y), radius, fromLocation.Name);
            var capture = new RoadNetworkGenerator.RoadCapture();
            bool joined;
            using (RoadNetworkGenerator.PlanningWith(finder, capture))
                joined = RoadNetworkGenerator.JoinTo(finder, from, hints, fromLocation?.Name ?? "road_connect", new Vector3(centre.x, 0f, centre.y),
                    radius, OnIsland, served, null);
            if (joined && capture.Road == null) { plan.AlreadyConnected = true; return plan; }
            if (!joined || capture.Road is not RoadNetworkGenerator.PlannedRoad road)
                throw new InvalidOperationException(ConnectRefusal(from, capture.Refusals, capture.Protected));
            plan.Refused = capture.Refusals;
            plan.WaysIn = road.WaysIn;
            plan.End = road.EndCenter;
            // Judged by generation's final check as it planned it (the one list, the zones it goes round included).
            written = road.Written!;
            stairs = road.Stairs;
        }
        else
        {
            var path = new List<Vector2>();
            var boughtSteps = new List<(Vector2 from, Vector2 to)>();
            var boughtJumps = new List<(Vector2 from, Vector2 to)>();
            var boughtGrounds = new List<RoadPathfinder.SearchGround>();
            // A marked road ends at its marks: each leg's search stops at its mark (RoadPathfinder.StopsAtItsGoal).
            finder.StopsAtItsGoal = true;
            for (int i = 1; i < points.Count; i++)
            {
                var leg = finder.FindPath(points[i - 1], points[i]);
                if (leg == null || leg.Count < 2)
                    throw new InvalidOperationException($"Leg {i} ({points[i - 1]} → {points[i]}) has no usable route within the routing limits. Nothing added.");
                boughtSteps.AddRange(finder.LastStairSteps); boughtJumps.AddRange(finder.LastViaductJumps); boughtGrounds.AddRange(finder.LastViaductGrounds);
                if (path.Count > 0) leg.RemoveAt(0);
                path.AddRange(leg);
            }
            // One road through every leg: the steps and jumps each leg's search bought, as one search would have, each jump with
            // the ground its leg bought it on (a moved viaduct is asked on its own leg's, RoadPathfinder.MovedViaductAllowed).
            finder.LastStairSteps.Clear(); finder.LastStairSteps.AddRange(boughtSteps);
            finder.LastViaductJumps.Clear(); finder.LastViaductJumps.AddRange(boughtJumps);
            finder.LastViaductGrounds.Clear(); finder.LastViaductGrounds.AddRange(boughtGrounds);
            // The generator's one entry for a searched road (the network snap, then crossings, staircases, structure ends,
            // profile, and its marks kept to), then its refusals of the road as planned: a marked road has no other route,
            // so it is refused with the reason.
            LocationLevelling.BeginRoad();
            var steps = new List<(Vector2 from, Vector2 to)>(finder.LastStairSteps);
            var jumps = new List<(Vector2 from, Vector2 to)>(finder.LastViaductJumps);
            (RoadNetworkGenerator.RoadPlan planned, RoadNetworkGenerator.Refusal? refused, RoadNetworkGenerator.WrittenRoad? written) Check(RoadNetworkGenerator.RoadPlan p)
            {
                if (p.Stop != RoadNetworkGenerator.PlanStop.Planned || p.Plans == null) return (p, null, null);
                // The one list of refusals of a road as planned (RoadNetworkGenerator.RoadRefusal), as generation and an approach
                // trial ask it: the zones it goes round are the players' it was not asked into (none with force).
                var refusal = RoadNetworkGenerator.RoadRefusal(new RoadNetworkGenerator.PlannedRoad { Path = p.Path, Plans = p.Plans, Crossings = p.Crossings, Stairs = p.Stairs },
                    out RoadNetworkGenerator.WrittenRoad? w);
                return (p, refusal, w);
            }
            var check = Check(RoadNetworkGenerator.PlanSearchedPath(finder, path, path[0], 0f, path[path.Count - 1], 0f,
                false, false, false, false, width, default, points));
            // Refused by the one list with a foot kept on a raised landing: this road laid once more, those feet with their stair
            // added, and checked again, as generation's commit does (RoadNetworkGenerator.WithStairsAdded).
            if (check.refused != null && RoadNetworkGenerator.NeedsStairsAdded(check.planned.Stairs, check.refused)
                && check.planned.LayAgain is Func<IReadOnlyCollection<Vector2>, RoadNetworkGenerator.RoadPlan> layAgain)
                check = RoadNetworkGenerator.WithStairsAdded(check.planned.Stairs, check.planned.StairsAddedAt, set =>
                {
                    finder.LastStairSteps.Clear(); finder.LastStairSteps.AddRange(steps);
                    finder.LastViaductJumps.Clear(); finder.LastViaductJumps.AddRange(jumps);
                    finder.LastViaductGrounds.Clear(); finder.LastViaductGrounds.AddRange(boughtGrounds);
                    return Check(layAgain(set));
                }, c => c.refused != null || c.planned.Plans == null, RoadNetworkGenerator.StairsAddedAt.Manual, check.refused);
            var (planned, refusedHere, checkedWrite) = check;
            switch (planned.Stop)
            {
                case RoadNetworkGenerator.PlanStop.Staircase:
                    throw new InvalidOperationException("A staircase the road needs could not be laid. Move a mark. Nothing added.");
                case RoadNetworkGenerator.PlanStop.Preparation:
                    throw new InvalidOperationException("A location near the road was not prepared for roads; try again once the world has loaded. Nothing added.");
                case RoadNetworkGenerator.PlanStop.NothingBetween:
                    throw new InvalidOperationException("There is no road to lay between the marks. Nothing added.");
            }
            _ = planned.Plans
                ?? throw new InvalidOperationException($"The complete road cannot be built ({RoadSpatialGrid.LastRefusal ?? "grade, turn-space, water or POI limits"}). Nothing added.");
            stairs = planned.Stairs;
            if (refusedHere is RoadNetworkGenerator.Refusal refused)
            {
                if (refused.Zones.Count > 0)
                    throw new InvalidOperationException(ProtectedZonesSaid(refused.Zones) + " Nothing added.");
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"The road cannot be built: {refused.Said} at ({refused.At.x:F0}, {refused.At.y:F0}). Move a mark. Nothing added."));
            }
            written = checkedWrite!;
        }
        // Stored as it was checked (RoadNetworkGenerator.WrittenRoad): its own pieces, the crossings it stores of its own as a
        // generated road stores them (none another road laid before it carries), and its append, all as the refusals read them.
        plan.Paths = written.Own;
        plan.Crossings = written.Stored;
        plan.Stairs = stairs.FindAll(st => !st.Existing);
        plan.Append = written.Footprint;
        // A zone players changed is written only where the operator asked (or forced).
        var reached = new HashSet<Vector2s>(ZonesReached(plan.Append, plan.Crossings));
        reached.IntersectWith(changed);
        plan.PlayerZones = reached.OrderBy(z => z.x).ThenBy(z => z.y).ToList();
        return plan;
    }

    public static void Commit(Plan plan)
    {
        if (Busy || RoadNetworkGenerator.IsGenerating) throw new InvalidOperationException("Road generation is busy.");
        if (plan.Epoch != worldEpoch || plan.Version != RoadSpatialGrid.RoadNetworkVersion)
            throw new InvalidOperationException("The world or road network changed; plan the road again.");
        if (plan.AlreadyConnected) return;
        if (plan.From != null) ForgetLocation();
        Busy = true;
        try { plan.ExploredZonesWritten = RoadNetworkGenerator.AppendManualRoad(plan.Paths, plan.Crossings, plan.Stairs, plan.Start, plan.Version, plan.Append); }
        finally { Busy = false; RoadNetworkSync.Publish(); }
        // Its arrivals as road are ways into their locations from now on, as a generated road's (its approaches are, as laid).
        foreach (var (location, way) in plan.WaysIn) LocationWaysIn.Record(location, way);
    }
}

public static partial class RoadNetworkGenerator
{
    /// <summary>A manual road's append, as it is committed (<see cref="AppendManualRoad"/>) and as the zones it writes are
    /// read off it (ManualRoads.ZonesReached): one list of both.</summary>
    internal sealed class ManualAppend
    {
        /// <summary>Its roads with the junctions' fill where its ends meet the network (RoadJunctionFillet), its staircases'
        /// start pads and the ground under its decks' ends.</summary>
        public readonly List<RoadSpatialGrid.PlannedPath> WithFills = new();
        /// <summary>What its crossings change as a generated road's do: the other roads raised to its structures' landings,
        /// the cuts under its viaducts' ends.</summary>
        public CrossingGround Ground = new();
        /// <summary>Its staircases as the generator lays them (<see cref="RunOf"/>), and their cuts.</summary>
        public readonly List<(StairRun run, StairPlan plan, StairKit kit)> Runs = new();
        public readonly List<Vector3> StairCuts = new();
        /// <summary>Its ways onto its location's landing and its ends carried on into its location (<see cref="LocationEnds"/>):
        /// stored into the location, as generation stores them.</summary>
        public readonly List<RoadSpatialGrid.PlannedPath> IntoLocation = new();
        /// <summary>The locations it lands on (PlacedLocations.MarkLanded).</summary>
        public readonly List<Vector2> Landed = new();
        /// <summary>Its last-resort pitches as the one list of refusals judged them (RoadNetworkGenerator.RoadRefusal,
        /// RoadPitches.PitchRefusal): recorded in the world's ledger as it is stored, generated or manual alike (RoadPitches.Record).</summary>
        public IReadOnlyList<RoadPitches.Pitch> Pitches = System.Array.Empty<RoadPitches.Pitch>();
        /// <summary>The zones it writes, once read (ManualRoads.ZonesReached).</summary>
        public HashSet<Vector2s>? Zones;
        /// <summary>The landing generation's road lands on a staircase's flight with (RoadStairRuns.LayLanding): the cuts it takes
        /// back and the staircase it cuts back, which the checks read as done (RoadTerrainModifier.AsStored); null for none.</summary>
        public RoadStairRuns.FlightLanding? Landing;
    }

    /// <summary>What a planned road (<see cref="PlannedRoad"/>) lays at its locations' ends, as generation lays it
    /// (GenerateRoadCore): its ways onto a placed location's landing (a road or a staircase), its ends carried on into their
    /// locations, and its approach staircases.</summary>
    internal sealed class LocationEnds
    {
        public readonly List<RoadSpatialGrid.PlannedPath> IntoLocation = new();
        public readonly List<RoadStairSpan> Stairs = new();
        public readonly List<Vector2> Landed = new();
        /// <summary>Each way onto a placed location's landing as road, as it is stored (RoadRide.Own, before anything of the
        /// road is), in the order of its entries; null for an entry by staircase.</summary>
        public readonly List<List<RoadSpatialGrid.PlannedPath>?> EntryRoads = new();
        /// <summary>Each end carried on into its location, as it is stored, in the order of its carried ends.</summary>
        public readonly List<List<RoadSpatialGrid.PlannedPath>> CarriedRoads = new();

        public static LocationEnds Of(PlannedRoad road)
        {
            var ends = new LocationEnds();
            foreach (var (location, entryRoad, entryStairs) in road.Entries)
            {
                List<RoadSpatialGrid.PlannedPath>? owned = entryRoad != null ? RoadRide.Own(new List<RoadSpatialGrid.PlannedPath> { entryRoad }, out _) : null;
                if (owned != null) ends.IntoLocation.AddRange(owned);
                ends.EntryRoads.Add(owned);
                if (entryStairs != null) ends.Stairs.Add(entryStairs);
                ends.Landed.Add(location.Centre);
            }
            foreach (RoadSpatialGrid.PlannedPath edge in road.Carried)
            {
                List<RoadSpatialGrid.PlannedPath> owned = RoadRide.Own(new List<RoadSpatialGrid.PlannedPath> { edge }, out _);
                ends.IntoLocation.AddRange(owned);
                ends.CarriedRoads.Add(owned);
            }
            ends.Stairs.AddRange(road.Approaches);
            return ends;
        }
    }

    internal static ManualAppend AppendOf(IReadOnlyList<RoadSpatialGrid.PlannedPath> paths, List<RoadCrossing> crossings,
        IReadOnlyList<RoadStairSpan> stairs, LocationEnds? ends = null)
    {
        var own = new List<RoadStairSpan>(stairs);
        if (ends != null) own.AddRange(ends.Stairs);
        return Footprint(paths, stairs, ends, GroundOf(crossings), RoadJunctionFillet.For(paths, WorldGenerator.instance, crossings, own),
            RoadStairRuns.StartPads(paths, own, WorldGenerator.instance));
    }

    /// <summary>A road's append from the pieces it stores: its roads (<paramref name="paths"/>), what its crossings change
    /// (<paramref name="ground"/>), its junctions' fill and its staircases' start pads, its staircases laid as the generator
    /// lays them, and its ends at its locations (<paramref name="ends"/>). The one assembly of what a road writes, for a manual
    /// road's append (<see cref="AppendOf"/>) and a generated road's final check (GenerateRoadCore) alike: the zones are read
    /// off it (ManualRoads.ZonesReached).</summary>
    internal static ManualAppend Footprint(IReadOnlyList<RoadSpatialGrid.PlannedPath> paths, IReadOnlyList<RoadStairSpan> stairs,
        LocationEnds? ends, CrossingGround ground, IEnumerable<RoadSpatialGrid.PlannedPath> fills, IEnumerable<RoadSpatialGrid.PlannedPath> pads,
        IReadOnlyList<Vector2>? path = null)
    {
        var append = new ManualAppend();
        AddStairs(append, stairs, ends?.Stairs, paths, path);
        if (ends != null)
        {
            append.IntoLocation.AddRange(ends.IntoLocation);
            append.Landed.AddRange(ends.Landed);
        }
        append.Ground = ground;
        append.WithFills.AddRange(paths);
        append.WithFills.AddRange(fills);
        append.WithFills.AddRange(pads);
        // The mounds under its structures' limiting piers and footings, stored with it as mound points (ground, not road).
        append.WithFills.AddRange(append.Ground.MoundPlans());
        return append;
    }

    /// <summary>A road's staircases as it lays them, runs and cuts (<see cref="Footprint"/>'s): its own (<paramref name="stairs"/>)
    /// and, at its locations, laid as generation lays an entry's and an approach's staircase (<paramref name="atLocations"/>,
    /// JoinNetwork). <paramref name="roads"/>: its pieces, the roads that meet each run (RunOf). <paramref name="path"/>: its
    /// search path, where known: a road's own staircase is recorded at the path points it climbs, met at its span's ends,
    /// as generation lays it.</summary>
    private static void AddStairs(ManualAppend append, IReadOnlyList<RoadStairSpan> stairs, IReadOnlyList<RoadStairSpan>? atLocations,
        IReadOnlyList<RoadSpatialGrid.PlannedPath>? roads, IReadOnlyList<Vector2>? path = null)
    {
        foreach (RoadStairSpan stair in stairs)
        {
            bool onPath = path != null && stair.FromIndex >= 0 && stair.ToIndex < path.Count && stair.FromIndex <= stair.ToIndex;
            append.Runs.Add((onPath
                ? RunOf(path![stair.FromIndex], path[stair.ToIndex], stair.Plan, stair.Kit, atLocation: false, stair.FromPoint, stair.ToPoint, span: stair, roads: roads)
                : RunOf(stair.FromPoint, stair.ToPoint, stair.Plan, stair.Kit, atLocation: false, span: stair, roads: roads), stair.Plan, stair.Kit));
            append.StairCuts.AddRange(stair.Plan.Cuts);
        }
        if (atLocations == null) return;
        foreach (RoadStairSpan stair in atLocations)
        {
            append.Runs.Add((RunOf(stair.FromPoint, stair.ToPoint, stair.Plan, stair.Kit, atLocation: true, expectedLocation: stair.ExpectedLocation, span: stair, roads: roads), stair.Plan, stair.Kit));
            append.StairCuts.AddRange(CutsOf(stair.Plan, atLocation: true));
        }
    }

    /// <summary>
    /// Why a road may not be stored for one of its staircases, and where; null when it may (decision 6p, docs/OWNERS.md, a
    /// structure's final support): each staircase it lays, as the one assembly of what it writes lays them
    /// (<paramref name="written"/>, <see cref="Written"/>: its own and those at its locations), read by its kit's support
    /// response before anything of the road is (<see cref="StairStructures.Refusal"/>), on the ground as it will stand once
    /// the road is stored (RoadTerrainModifier.AsStored over its written points: its own fill, cuts, pads and mounds, its
    /// staircases' cuts among them, the other roads it raises, and its staircases' walks flooring those cuts as once they are
    /// stored: StairStructures.CutFloor), the ground its first build reads (StairStructures.Dresser).
    /// One its kit would refuse refuses the road with it, as any refusal of a road as planned: no road and no cut without its
    /// staircase. Asked by generation in the one list of refusals (RoadNetworkGenerator.RoadRefusal): generation, a
    /// road_connect's plan, an approach trial and a marked manual road alike. A staircase on ground not known under its
    /// walking pieces (a bug under the preload-list rule: it would lay nothing at its build) refuses its road too, said as an
    /// error naming the staircase, the location and the zone (StairStructures.UnknownGroundRefusal); nothing waits for it.
    /// </summary>
    internal static string? StairsRefusal(ManualAppend written, out Vector2 at)
    {
        at = default;
        if (written.Runs.Count == 0) return null;
        using System.IDisposable stored = AsWritten(written);
        foreach (var (run, _, _) in written.Runs)
            if (StairStructures.Refusal(run, GenerationWorld, out at) is string why)
            {
                if (why.StartsWith(StairStructures.UnknownGroundRefusal, StringComparison.Ordinal))
                    Log.LogError(FormattableString.Invariant(
                        $"[STAIRS] a road refused for its staircase ({run.From.x:F1}, {run.From.y:F1}) -> ({run.To.x:F1}, {run.To.y:F1}): {why}; a structure stands only in a location on the preload list"));
                return why;
            }
        at = default;
        return null;
    }


    /// <summary>Returns the zones explored before the network that the road is built through (see NetworkCutoff.Admit).</summary>
    internal static List<Vector2s> AppendManualRoad(IReadOnlyList<RoadSpatialGrid.PlannedPath> paths,
        List<RoadCrossing> crossings, IReadOnlyList<RoadStairSpan> stairs, Vector2 start, int expectedVersion,
        ManualAppend? planned = null)
    {
        if (IsGenerating) throw new InvalidOperationException("Road generation is busy.");
        ManualRoads.RefuseOnReleasedNetwork();
        // The road and its earthwork, as one append (AppendOf). Its staircases are laid as the generator lays them, settled
        // on the ground their cuts and the roads leave (BuriedPieces).
        ManualAppend append = planned ?? AppendOf(paths, crossings, stairs);
        // A plan's append was read by its checks, its mounds unsaid (RoadNetworkGenerator.Written): said as it is stored.
        if (planned != null && WorldGenerator.instance is WorldGenerator sayingWorld) planned.Ground.SayMounds(sayingWorld);
        var runs = append.Runs;
        // An explicit manual road is built in full, explored zones included, its own bridges' and staircases' pieces
        // there too (StructurePlacement.SpawnAppend asks NetworkCutoff.Excludes). A zone it makes writable builds no
        // other structure for it (StructureAppendQueue.Prepare).
        List<Vector2s> explored = NetworkCutoff.Admit(ManualRoads.ZonesReached(append, crossings));
        try
        {
            RoadNetworkPersistence.EnsureMetadataInstance();
            // All or none: the road, its fills and pads, its cuts and the other roads it raises.
            // Published with the zones it owes work, its structures' included (ManualRoads.ZonesReached): the bake and the
            // clearing record take its scope from there (RoadSpatialGrid.ZonesChangedSince).
            RoadSpatialGrid.CommitAppend(append.WithFills, expectedVersion, intoLocationPlans: append.IntoLocation,
                cuts: new (IReadOnlyList<Vector3>, float)[]
                {
                    (append.StairCuts, StairLayout.CutWidth), (append.Ground.StairCuts, StairLayout.CutWidth), (append.Ground.Cuts, CrossingGround.CutWidth),
                },
                raised: append.Ground.Raised, reached: ManualRoads.ZonesReached(append, crossings));
            // In the network from here on: saved with the world even if a step below throws.
            m_networkDirty = true;
            // Its pitches in the world's ledger, as its refusals judged them: later roads, manual or generated, keep to the
            // budget and spacing with them (RoadPitches.Record), and a save keeps them.
            foreach (RoadPitches.Pitch p in RoadPitches.Record(append.Pitches))
                Log.LogInfo(FormattableString.Invariant(
                    $"[PITCH] manual road {RoadSpatialGrid.AppendCount}: {p.Length:F1} m at up to {p.Steepest:P0} from ({p.Points[0].x:F1}, {p.Points[0].y:F1}) to ({p.Points[p.Points.Count - 1].x:F1}, {p.Points[p.Points.Count - 1].y:F1})"));
        }
        catch
        {
            NetworkCutoff.Readmit(explored);
            throw;
        }
        if (explored.Count > 0)
            Log.LogWarning(ManualRoads.ExploredZonesWarning(explored));
        // The bridges' pieces once the road is in the network: they are laid on the ground its levelling leaves
        // (StructureGround), as the plans read them from now on.
        // Every structure the append builds, and every ground it saves, its bridges', released ones' and its staircases', on
        // the ground as it stands with every one of its staircases in the network: their walks floor its cuts
        // (StairStructures.CutFloor, with the walks not added yet), as their checks read it (StairsRefusal, AsWritten) and as a
        // generated road's structures' first builds read it.
        string label = $"Manual road {RoadSpatialGrid.AppendCount}";
        foreach (RoadSpatialGrid.PlannedPath path in append.WithFills) RoadOwners.Record(label, "manual road", path.Points);
        foreach (RoadSpatialGrid.PlannedPath path in append.IntoLocation) RoadOwners.Record(label, "manual road into its location", path.Points);
        List<StructurePiece> released;
        using (RoadTerrainModifier.AsStored(new List<RoadSpatialGrid.RoadPoint>(), new List<(RoadSpatialGrid.RoadPoint at, float to)>(), WrittenRuns(append)))
        {
            Dictionary<Vector2s, List<StructurePiece>> bridgeAdditions = StructureAppendQueue.Prepare(crossings);
            // A structure held back by the zones this road opens, and by no other (StructurePlans.ReleasedBy), is built whole now:
            // saved as its first zone's build saves it, its kit chosen, its pieces owed through the queue to the zones built while
            // it was held back (a zone not generated yet lays them with its plan). Never half of it.
            released = StructurePlans.ReleasedBy(explored);
            if (released.Count > 0 && WorldGenerator.instance is WorldGenerator releasedWorld)
            {
                StructureSites.KeepFirstBuilt(released, releasedWorld);
                ChooseKitsAsBuilt(released);
                StructureAppendQueue.Enqueue(StructureAppendQueue.Prepare(released));
            }
            // Its staircases settled and built on the ground as it stands with every one of them in the network.
            if (runs.Count > 0 && WorldGenerator.instance is WorldGenerator world)
            {
                Func<int, int, float> finished = StructureGround.Vertices(world);
                foreach (var (run, _, _) in runs) StairStructures.Settle(run, finished);
            }
            StructureAppendQueue.Enqueue(bridgeAdditions);
            foreach (var (run, plan, kit) in runs)
            {
                // The pieces of zones already spawned are owed through the append queue, as a manual road's bridges are.
                // Its kit's pieces laid now and saved, as a generated staircase's are as its first zone is built.
                StructureAppendQueue.Enqueue(StructureAppendQueue.Prepare(StairStructures.Build(run, GenerationWorld)));
                AddToNetwork(run, plan, kit);
            }
            foreach (Vector2 landed in append.Landed) PlacedLocations.MarkLanded(landed);
            foreach (RoadCrossing c in crossings) BridgeStructures.DecideKind(c, GenerationWorld);
            m_roadCrossings.AddRange(crossings);
            // Its bridges are built now, through the append queue: their kits are chosen with them.
            ChooseKitsAsBuilt(bridgeAdditions.Values.SelectMany(pieces => pieces));
            // Later roads keep clear of its viaducts as they do of a generated road's: none under a deck or through a pier.
            LayViaducts(crossings, label);
        }
        m_roadStartPoints.Add((start, label));
        m_networkDirty = true; // Saved in full; a loaded network stays loaded.
        StructurePlans.InvalidatePlans();
        RoadClearAreaManager.ClearCache();
        // Its structures in the plans: the zones their walks reach as generation reads them (StructurePlans.ZonesWithWalks,
        // the full bake's reach limited to them; a neighbour of a deck or a stair, a tree there reaching over it) are owed
        // their clearing (VegetationClearing.Reached).
        if (WorldGenerator.instance is WorldGenerator walked && (crossings.Count > 0 || runs.Count > 0 || released.Count > 0))
        {
            var pieces = new List<StructurePiece>(released);
            foreach (RoadCrossing c in BridgeLayout.DistinctSites(crossings))
                pieces.AddRange(BridgeStructures.PiecesOf(c, walked, walked.GetSeed()));
            VegetationClearing.Reached(StructurePlans.ZonesWithWalks(pieces,
                runs.Select(r => r.run).Where(run => BuiltStairs.Of(run) != null)));
        }
        return explored;
    }
}
