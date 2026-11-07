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
    /// <summary>Changes whenever the world does: work planned in one world must not land in another.</summary>
    public static int WorldEpoch => worldEpoch;
    public static bool Busy { get; private set; }
    public static ManualRoadDraft Draft(string actor)
    {
        if (!drafts.TryGetValue(actor, out var draft)) drafts.Add(actor, draft = new ManualRoadDraft());
        return draft;
    }
    public static void Reset()
    {
        drafts.Clear(); islands = null; worldEpoch++; Busy = false; s_confirm = null; s_placeConfirm = null;
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
        /// <summary>road_connect from a place (<see cref="PlaceAt"/>): the place it was planned from.</summary>
        internal Place? From { get; set; }
        /// <summary>road_connect: the roads its planning refused before this one (RoadNetworkGenerator.RoadCapture).</summary>
        internal IReadOnlyList<(string why, Vector2? at)> Refused { get; set; } = Array.Empty<(string, Vector2?)>();
        /// <summary>road_connect: its ends arriving at their places as road, ways into them once committed (PlaceWaysIn.Record),
        /// as a generated road's are once stored.</summary>
        internal IReadOnlyList<(Vector2 place, PlaceWaysIn.Way way)> WaysIn { get; set; } = Array.Empty<(Vector2, PlaceWaysIn.Way)>();
    }

    /// <summary>A place road_connect was run inside (<see cref="PlaceAt"/>): the network is joined from it, as the
    /// generator joins a place (RoadNetworkGenerator.JoinPlace).</summary>
    internal sealed class Place
    {
        public readonly string Name;
        public readonly Vector2 Centre;
        /// <summary>Its exterior radius, as generation's destinations carry it.</summary>
        public readonly float Radius;
        public Place(string name, Vector2 centre, float radius) { Name = name; Centre = centre; Radius = radius; }
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

    private static (string key, DateTime until)? s_placeConfirm;

    /// <summary>
    /// Whether road_connect may join the network from <paramref name="place"/>: the same actor ran it inside the same place
    /// within <see cref="ConfirmWindow"/> of the warning (<see cref="InsidePlaceWarning"/>). A first run is remembered and
    /// answered false. Kept, not spent, while the window lasts: a run held back for the zones players changed
    /// (<see cref="ConfirmPlayerZones"/>) and run again is still from the place; forgotten once its road is built
    /// (<see cref="ForgetPlace"/>).
    /// </summary>
    internal static bool ConfirmPlace(string actor, Place place, DateTime now)
    {
        string key = FormattableString.Invariant($"{actor}|connect|{place.Name}|{place.Centre.x:F1},{place.Centre.y:F1}");
        bool again = s_placeConfirm is { } pending && pending.key == key && now <= pending.until;
        s_placeConfirm = (key, now + ConfirmWindow);
        return again;
    }

    internal static void ForgetPlace() => s_placeConfirm = null;

    /// <summary>What the operator is told the first time road_connect is run inside a place.</summary>
    internal static string InsidePlaceWarning(Vector2 at, Place place) => FormattableString.Invariant(
        $"Not built yet: {at.x:F0}, {at.y:F0} is inside {place} or its road clearance. Run the same command again within {ConfirmWindow.TotalMinutes:F0} minutes to connect {place.Name} to the network from its landing or approach, as the generator would.");

    /// <summary>
    /// The place whose footprint or road clearance (RoadSiteProtection) holds <paramref name="point"/>, as generation's
    /// destinations carry it (its location's name and exterior radius); null where none does. A point in a footprint
    /// that is no place a road may lead to is refused: a place the game leaves for a player to find
    /// (PlacedSites.PlayerDecides), one whose landing is not known (PlacedSites.NotPlacedWhy), or a footprint with no
    /// location.
    /// </summary>
    internal static Place? PlaceAt(Vector2 point)
    {
        float clearance = RoadSiteProtection.Clearance(RoadNetworkGenerator.RoadWidth);
        if (!RoadSiteProtection.BlocksSegment(point, point, clearance, null, null)) return null;
        string outside = $"Waypoint {point} is inside a protected POI or its road clearance. Mark a point outside its edge.";
        if (!RoadSiteProtection.NearestFootprint(point, clearance, out RoadSiteProtection.Footprint site)
            || ZoneSystem.instance?.m_locationInstances is not { } placed
            || !placed.TryGetValue(ZoneSystem.GetZone(new Vector3(site.Centre.x, 0f, site.Centre.y)), out ZoneSystem.LocationInstance instance)
            || instance.m_location?.m_prefab == null
            || (new Vector2(instance.m_position.x, instance.m_position.z) - site.Centre).sqrMagnitude > 0.25f)
            throw new ArgumentException(outside);
        var location = instance.m_location;
        var place = new Place(location.m_prefab.Name, site.Centre, location.m_exteriorRadius);
        if (PlacedSites.PlayerDecides(place.Name, location.m_unique, location.m_iconPlaced, location.m_iconAlways))
            throw new ArgumentException($"{place} is a place the game leaves for players to find: no road leads to it. Mark a point outside its edge.");
        if (PlacedSites.NotPlacedWhy(place.Centre) is string why)
            throw new ArgumentException($"{place} is given no road: its landing is not known ({why}). Mark a point outside its edge.");
        return place;
    }

    /// <summary>What the operator is told as road_connect starts its search.</summary>
    internal static string ConnectStarting(Vector2 at) => FormattableString.Invariant(
        $"Trying to build a road from {at.x:F0}, {at.y:F0} to the network \u2013 this may take some time and the game may pause while we search.");

    /// <summary>road_connect's one line where no road was planned: the main reason and where, with how many roads were
    /// tried and why each was refused (RoadNetworkGenerator.RoadCapture).</summary>
    internal static string ConnectRefusal(Vector2 from, IReadOnlyList<(string why, Vector2? at)> refused)
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
        return text + ". Nothing added.";
    }

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
        void Reach(Vector2 p, float width) => ReachBy(p, RoadSpatialGrid.ZoneReach(width));
        void ReachBy(Vector2 p, float reach)
        {
            // As far as a zone gathers the point (RoadSpatialGrid.ZoneReach at the width the append stores; a cut-only point's
            // cut and batter, RoadTerrainModifier.CutReach).
            Vector2s low = ZoneSystem.GetZone(new Vector3(p.x - reach, 0f, p.y - reach));
            Vector2s high = ZoneSystem.GetZone(new Vector3(p.x + reach, 0f, p.y + reach));
            for (int x = low.x; x <= high.x; x++)
                for (int y = low.y; y <= high.y; y++)
                    zones.Add(new Vector2s((short)x, (short)y));
        }
        foreach (StairRun run in append.Runs.Select(r => r.run))
        {
            foreach (StructurePiece piece in run.Pieces) zones.Add(ZoneSystem.GetZone(piece.Position));
            foreach (StairClearance box in run.Clearances) zones.Add(ZoneSystem.GetZone(box.Centre));
        }
        foreach (RoadSpatialGrid.PlannedPath path in append.WithFills.Concat(append.IntoSite))
            for (int i = 0; i < path.Points.Count; i++)
                Reach(path.Points[i], path.Widths != null && i < path.Widths.Count ? path.Widths[i] : path.Width);
        foreach (Vector3 cut in append.StairCuts) ReachBy(new Vector2(cut.x, cut.z), RoadTerrainModifier.StairCutReach + RoadTerrainModifier.CutGatherPad);
        foreach (Vector3 cut in append.Ground.Cuts) Reach(new Vector2(cut.x, cut.z), RoadNetworkGenerator.CrossingGround.CutWidth);
        foreach (var (at, _) in append.Ground.Raised) Reach(at.p, at.w);
        var world = WorldGenerator.instance;
        if (world != null)
            foreach (RoadCrossing crossing in BridgeLayout.DistinctSites(crossings.ToList()))
                foreach (StructurePiece piece in BridgeStructures.PiecesOf(crossing, world, world.GetSeed()))
                    zones.Add(ZoneSystem.GetZone(piece.Position));
        // Read once: the plan's guard and the commit's admission ask the same append.
        append.Zones = zones;
        return zones;
    }

    /// <summary>What the operator is told when a manual road is built through zones explored before the network.</summary>
    public static string ExploredZonesWarning(IReadOnlyList<Vector2s> zones) =>
        NetworkCutoff.AdmissionWarning("this road crosses", zones);

    /// <summary>
    /// Plan a manual road. It goes round every zone players changed (<see cref="PlayerChanges"/>), keeping its
    /// levelling out of them, except the zones of its own points (road_connect's position, the marks): the operator
    /// asked for those, and they are built through once confirmed (<see cref="Plan.PlayerZones"/>).
    /// <paramref name="force"/>: through whatever stands, as asked. road_connect is the generator's join
    /// (RoadNetworkGenerator.JoinTo), with every search again it makes; from <paramref name="fromPlace"/>, the place the
    /// point is in (<see cref="PlaceAt"/>), as the generator joins that place.
    /// </summary>
    public static Plan Prepare(IReadOnlyList<Vector2> points, bool connect,
        IReadOnlyList<Island>? knownIslands = null, bool force = false) => Prepare(points, connect, knownIslands, force, null);

    internal static Plan Prepare(IReadOnlyList<Vector2> points, bool connect,
        IReadOnlyList<Island>? knownIslands, bool force, Place? fromPlace)
    {
        if (Busy || RoadNetworkGenerator.IsGenerating) throw new InvalidOperationException("Road generation is busy.");
        Busy = true;
        try { return PrepareCore(points, connect, knownIslands, force, fromPlace); }
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
        if (RoadNetworkPersistence.LastLoadUnreadable)
            throw new InvalidOperationException("This world's saved road network could not be read, so no road is added to it. " +
                "Use a build that reads it, or road_generate to replace it on purpose.");
        if (RoadSpatialGrid.LoadedFromReleasedFormat)
            throw new InvalidOperationException("This world's roads were saved by ProceduralRoads 1.4 or earlier. " +
                "Adding a road would make them unreadable to that version, so roads cannot be added to this world.");
        // A record kept unread (RoadNetworkPersistence.ReadOrKeep) is written back as it was: what a road added to it would be lost.
        string kept = string.Join(", ", RoadNetworkPersistence.KeptRecords);
        if (kept.Length > 0)
            throw new InvalidOperationException($"This world's saved {kept} could not be read, so no road is added this session.");
        // A generation that failed or was stopped left none in memory: the road would be saved over the world's network.
        if (!RoadNetworkGenerator.RoadsAvailable && RoadNetworkPersistence.SavedNetworkExists)
            throw new InvalidOperationException("This world's saved road network is not loaded (a road generation failed or was " +
                "stopped), so no road is added over it. It loads again on the next spawn or world load, or road_generate replaces it.");
    }

    private static Plan PrepareCore(IReadOnlyList<Vector2> points, bool connect, IReadOnlyList<Island>? knownIslands, bool force, Place? fromPlace)
    {
        RefuseOnReleasedNetwork();
        var world = WorldGenerator.instance;
        if (world == null || !RoadNetworkGenerator.IsLocationsReady)
            throw new InvalidOperationException("Wait for the world and its locations to finish loading.");
        if (connect)
        {
            if (points.Count != 1 || !ManualRoadDraft.ValidPoint(points[0])) throw new ArgumentException("Connect needs one valid X,Z position.");
            if (!RoadNetworkGenerator.RoadsAvailable || RoadSpatialGrid.StoredPointRecords == 0)
                throw new InvalidOperationException("No road network exists on this island.");
        }
        else ManualRoadDraft.Validate(points);
        float width = RoadNetworkGenerator.RoadWidth;
        foreach (var point in points)
        {
            // From a place, its point is in it (PlaceAt): the road leaves from the place, as the generator's does.
            if (fromPlace == null && RoadSiteProtection.BlocksSegment(point, point, RoadSiteProtection.Clearance(width), null, null))
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
            End = points[points.Count - 1], From = fromPlace };
        // Where the network left a zone as it is, a terrain compiler there is a player's; elsewhere it is the roads'.
        HashSet<Vector2s> changed = PlayerChanges.Zones(null, NetworkCutoff.Excludes);
        var asked = new HashSet<Vector2s>(points.Select(p => ZoneSystem.GetZone(new Vector3(p.x, 0f, p.y))));
        asked.IntersectWith(changed);
        var avoided = new HashSet<Vector2s>(changed);
        avoided.ExceptWith(asked);
        if (force) avoided.Clear();
        using IDisposable avoiding = NetworkCutoff.AvoidWhilePlanning(avoided, RoadSpatialGrid.ZoneReach(width));
        // The generator's search, as it searches for one road: it may buy stair steps and Mistlands viaducts; only where
        // the route comes from (the marks, or the network to connect to) is the operator's.
        var finder = RoadNetworkGenerator.NewFinder(world, RoadSiteProtection.Clearance(width));
        List<RoadSpatialGrid.PlannedPath> paths;
        List<RoadCrossing> crossings;
        List<RoadStairSpan> stairs;
        RoadNetworkGenerator.PlaceEnds? ends = null;
        if (connect)
        {
            var targets = RoadNetworkGenerator.JoinTargets(OnIsland);
            if (targets.Count == 0) throw new InvalidOperationException("No road network exists on this island.");
            if (fromPlace == null && RoadSpatialGrid.TryGetRoadWithin(points[0], width * 0.5f, out _, OnIsland))
            { plan.AlreadyConnected = true; return plan; }
            // Sample hints bound heuristic cost. The success predicate checks ALL
            // actual road points and filters them by island, not just the hints.
            int stride = Math.Max(1, targets.Count / 128);
            var hints = targets.Where((_, i) => i % stride == 0).Take(129).ToList();
            // The generator's join, as it joins a place to the network: from a place its ground and exterior radius, else the
            // point as a place of none.
            Vector2 from = fromPlace == null ? points[0] : RoadEndpoint.FindGround(world, fromPlace.Centre, fromPlace.Radius);
            Vector2 centre = fromPlace?.Centre ?? points[0];
            float radius = fromPlace?.Radius ?? 0f;
            Func<bool>? served = fromPlace == null ? null
                : () => RoadNetworkGenerator.Reached(new Vector3(centre.x, 0f, centre.y), radius, fromPlace.Name);
            var capture = new RoadNetworkGenerator.RoadCapture();
            bool joined;
            using (RoadNetworkGenerator.PlanningWith(finder, capture))
                joined = RoadNetworkGenerator.JoinTo(finder, from, hints, fromPlace?.Name ?? "road_connect", new Vector3(centre.x, 0f, centre.y),
                    radius, OnIsland, served, null);
            if (joined && capture.Road == null) { plan.AlreadyConnected = true; return plan; }
            if (!joined || capture.Road is not RoadNetworkGenerator.PlannedRoad road)
                throw new InvalidOperationException(ConnectRefusal(from, capture.Refusals));
            plan.Refused = capture.Refusals;
            plan.WaysIn = road.WaysIn;
            plan.End = road.EndCenter;
            paths = road.Plans;
            crossings = road.Crossings;
            stairs = road.Stairs;
            ends = RoadNetworkGenerator.PlaceEnds.Of(road);
        }
        else
        {
            var path = new List<Vector2>();
            var boughtSteps = new List<(Vector2 from, Vector2 to)>();
            var boughtJumps = new List<(Vector2 from, Vector2 to)>();
            for (int i = 1; i < points.Count; i++)
            {
                var leg = finder.FindPath(points[i - 1], points[i]);
                if (leg == null || leg.Count < 2)
                    throw new InvalidOperationException($"Leg {i} ({points[i - 1]} → {points[i]}) has no usable route within the routing limits. Nothing added.");
                boughtSteps.AddRange(finder.LastStairSteps); boughtJumps.AddRange(finder.LastViaductJumps);
                if (path.Count > 0) leg.RemoveAt(0);
                path.AddRange(leg);
            }
            // Check the complete polyline.
            for (int i = 1; i < path.Count; i++)
                if (RoadSiteProtection.BlocksSegment(path[i - 1], path[i], finder.SiteClearance, null, null))
                    throw new InvalidOperationException("The requested road would cross a protected POI.");
            // One road through every leg: the steps and jumps each leg's search bought, as one search would have.
            finder.LastStairSteps.Clear(); finder.LastStairSteps.AddRange(boughtSteps);
            finder.LastViaductJumps.Clear(); finder.LastViaductJumps.AddRange(boughtJumps);
            // The generator's one planning pipeline for a road (crossings, staircases, structure ends, profile), then its
            // refusals of the road as planned: a marked road has no other route, so it is refused with the reason.
            LocationLevelling.BeginRoad();
            var planned = RoadNetworkGenerator.PlanFromPath(finder, path, path[0], 0f, path[path.Count - 1], 0f,
                false, false, false, false, width);
            switch (planned.Stop)
            {
                case RoadNetworkGenerator.PlanStop.Staircase:
                    throw new InvalidOperationException("A staircase the road needs could not be laid. Move a mark. Nothing added.");
                case RoadNetworkGenerator.PlanStop.Preparation:
                    throw new InvalidOperationException("A place near the road was not prepared for roads; try again once the world has loaded. Nothing added.");
                case RoadNetworkGenerator.PlanStop.NothingBetween:
                    throw new InvalidOperationException("There is no road to lay between the marks. Nothing added.");
            }
            paths = planned.Plans
                ?? throw new InvalidOperationException($"The complete road cannot be built ({RoadSpatialGrid.LastRefusal ?? "grade, turn-space, water or POI limits"}). Nothing added.");
            if (RoadNetworkGenerator.BuiltRoadRefusal(paths, planned.Crossings, out Vector2 refusedAt) is string why)
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"The road cannot be built: {why} at ({refusedAt.x:F0}, {refusedAt.y:F0}). Move a mark. Nothing added."));
            crossings = planned.Crossings;
            stairs = planned.Stairs;
            // Turning landings may move a marked pivot slightly. Large deviations
            // need a new mark instead of silently ignoring the user's requested route.
            foreach (var point in points)
                if (!paths.Any(p => p.Points.Any(q => Vector2.Distance(point, q) <= width * 2f)))
                    throw new InvalidOperationException($"The road cannot stay within {width * 2f:F0} m of waypoint {point}. Move that mark.");
        }
        plan.Paths = RoadNetworkGenerator.OwnPieces(paths, crossings, out _, out _);
        plan.Crossings = crossings.Where(c => !RoadNetworkGenerator.GetRoadCrossings().Any(old => RoadCrossing.SameBanks(old, c))).ToList();
        plan.Stairs = stairs.FindAll(st => !st.Existing);
        // What it writes, as AppendManualRoad will: a zone players changed only where the operator asked (or forced).
        plan.Append = RoadNetworkGenerator.AppendOf(plan.Paths, plan.Crossings, plan.Stairs, ends);
        HashSet<Vector2s> written = new HashSet<Vector2s>(ZonesReached(plan.Append, plan.Crossings));
        written.IntersectWith(changed);
        var unasked = new HashSet<Vector2s>(written);
        unasked.ExceptWith(asked);
        if (!force && unasked.Count > 0)
            throw new InvalidOperationException("The road would reach zone(s) players changed (buildings or terrain edits): " +
                string.Join(", ", unasked.OrderBy(z => z.x).ThenBy(z => z.y).Select(z => $"({z.x}, {z.y})")) +
                ". Mark a point in a zone to build into it, or add force. Nothing added.");
        plan.PlayerZones = written.OrderBy(z => z.x).ThenBy(z => z.y).ToList();
        return plan;
    }

    public static void Commit(Plan plan)
    {
        if (Busy || RoadNetworkGenerator.IsGenerating) throw new InvalidOperationException("Road generation is busy.");
        if (plan.Epoch != worldEpoch || plan.Version != RoadSpatialGrid.RoadNetworkVersion)
            throw new InvalidOperationException("The world or road network changed; plan the road again.");
        if (plan.AlreadyConnected) return;
        if (plan.From != null) ForgetPlace();
        Busy = true;
        try { plan.ExploredZonesWritten = RoadNetworkGenerator.AppendManualRoad(plan.Paths, plan.Crossings, plan.Stairs, plan.Start, plan.Version, plan.Append); }
        finally { Busy = false; }
        // Its arrivals as road are ways into their places from now on, as a generated road's (its approaches are, as laid).
        foreach (var (place, way) in plan.WaysIn) PlaceWaysIn.Record(place, way);
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
        /// <summary>Its ways onto its place's landing and its ends carried on into its place (<see cref="PlaceEnds"/>):
        /// stored into the site, as generation stores them.</summary>
        public readonly List<RoadSpatialGrid.PlannedPath> IntoSite = new();
        /// <summary>The places it lands on (PlacedSites.MarkLanded).</summary>
        public readonly List<Vector2> Landed = new();
        /// <summary>The zones it writes, once read (ManualRoads.ZonesReached).</summary>
        public HashSet<Vector2s>? Zones;
    }

    /// <summary>What a planned road (<see cref="PlannedRoad"/>) lays at its places' ends, as generation lays it
    /// (GenerateRoadCore): its ways onto a placed place's landing (a road or a staircase), its ends carried on into their
    /// places, and its approach staircases.</summary>
    internal sealed class PlaceEnds
    {
        public readonly List<RoadSpatialGrid.PlannedPath> IntoSite = new();
        public readonly List<RoadStairSpan> Stairs = new();
        public readonly List<Vector2> Landed = new();

        public static PlaceEnds Of(PlannedRoad road)
        {
            var ends = new PlaceEnds();
            foreach (var (site, entryRoad, entryStairs) in road.Entries)
            {
                if (entryRoad != null) ends.IntoSite.AddRange(RoadRide.Own(new List<RoadSpatialGrid.PlannedPath> { entryRoad }, out _));
                if (entryStairs != null) ends.Stairs.Add(entryStairs);
                ends.Landed.Add(site.Centre);
            }
            foreach (RoadSpatialGrid.PlannedPath edge in road.Carried)
                ends.IntoSite.AddRange(RoadRide.Own(new List<RoadSpatialGrid.PlannedPath> { edge }, out _));
            ends.Stairs.AddRange(road.Approaches);
            return ends;
        }
    }

    internal static ManualAppend AppendOf(IReadOnlyList<RoadSpatialGrid.PlannedPath> paths, List<RoadCrossing> crossings,
        IReadOnlyList<RoadStairSpan> stairs, PlaceEnds? ends = null)
    {
        var append = new ManualAppend();
        foreach (RoadStairSpan stair in stairs)
        {
            append.Runs.Add((StairRun.FromPlan(stair.FromPoint, stair.ToPoint, RoadStairRuns.Width, stair.Plan, stair.Kit), stair.Plan, stair.Kit));
            append.StairCuts.AddRange(stair.Plan.Cuts);
        }
        // At its places, laid as generation lays an entry's and an approach's staircase (JoinNetwork).
        var own = new List<RoadStairSpan>(stairs);
        if (ends != null)
        {
            foreach (RoadStairSpan stair in ends.Stairs)
            {
                append.Runs.Add((RunOf(stair.FromPoint, stair.ToPoint, stair.Plan, stair.Kit, atPlace: true, expectedPlace: stair.ExpectedPlace), stair.Plan, stair.Kit));
                append.StairCuts.AddRange(CutsOf(stair.Plan, atPlace: true));
            }
            own.AddRange(ends.Stairs);
            append.IntoSite.AddRange(ends.IntoSite);
            append.Landed.AddRange(ends.Landed);
        }
        append.Ground = GroundOf(crossings, paths.Count > 0 ? paths[0].Width : RoadWidth);
        append.WithFills.AddRange(paths);
        append.WithFills.AddRange(RoadJunctionFillet.For(paths, WorldGenerator.instance, crossings, own));
        append.WithFills.AddRange(RoadStairRuns.StartPads(paths, own, WorldGenerator.instance));
        if (append.Ground.UnderDecks != null) append.WithFills.Add(append.Ground.UnderDecks);
        return append;
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
        var runs = append.Runs;
        // An explicit manual road is built in full, explored zones included; before the bridge plans are read, so
        // a bridge this makes whole is built whole.
        List<Vector2s> explored = NetworkCutoff.Admit(ManualRoads.ZonesReached(append, crossings));
        try
        {
            RoadNetworkPersistence.EnsureMetadataInstance();
            // All or none: the road, its fills and pads, its cuts and the other roads it raises.
            RoadSpatialGrid.CommitAppend(append.WithFills, expectedVersion, intoSitePlans: append.IntoSite,
                cuts: new (IReadOnlyList<Vector3>, float)[] { (append.StairCuts, StairLayout.CutWidth), (append.Ground.Cuts, CrossingGround.CutWidth) },
                raised: append.Ground.Raised);
            // In the network from here on: saved with the world even if a step below throws.
            m_networkDirty = true;
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
        Dictionary<Vector2s, List<StructurePiece>> bridgeAdditions = StructureAppendQueue.Prepare(crossings);
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
        foreach (Vector2 landed in append.Landed) PlacedSites.MarkLanded(landed);
        foreach (RoadCrossing c in crossings) BridgeStructures.DecideKind(c, GenerationWorld);
        m_roadCrossings.AddRange(crossings);
        // Its bridges are built now, through the append queue: their kits are chosen with them.
        ChooseKitsAsBuilt(bridgeAdditions.Values.SelectMany(pieces => pieces));
        string label = $"Manual road {RoadSpatialGrid.AppendCount}";
        // Later roads keep clear of its viaducts as they do of a generated road's: none under a deck or through a pier.
        KeepClearOfViaducts(crossings, label);
        m_roadStartPoints.Add((start, label));
        m_networkDirty = true; // Saved in full; a loaded network stays loaded.
        StructurePlans.InvalidatePlans();
        RoadClearAreaManager.ClearCache();
        return explored;
    }
}
