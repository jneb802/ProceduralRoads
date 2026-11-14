using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

public static partial class RoadNetworkGenerator
{
    /// <summary>Up to a count of replacements for destinations that ended unreached: given the destinations still standing and
    /// the locations already chosen or tried (by <see cref="LocationKey"/>).</summary>
    internal delegate List<(string name, Vector3 position, float radius)> Replacer(
        IReadOnlyList<(string name, Vector3 position, float radius)> kept, ISet<(float, float)> tried, int count);

    /// <summary>The shipped Roads.ReplaceUnreachableLocations: on.</summary>
    internal const bool DefaultReplaceUnreached = true;
    /// <summary>On by default (the setting Roads.ReplaceUnreachableLocations): a chosen destination that ends unreached is
    /// replaced by another on its island, drawn by the selection's own rule (<see cref="SelectReplacements"/>), at most one for
    /// each unreached one and one more round for replacements that end unreached. Off, no replacement round runs and each
    /// island keeps its first picks; the last resort still runs over them. Set between generations only
    /// (ProceduralRoadsPlugin.ApplyConfiguration waits for a generation to end), so one generation reads one value.</summary>
    internal static bool ReplaceUnreached = DefaultReplaceUnreached;
    /// <summary>Debug switch (off): one log line per destination with its attempts, failures and time.</summary>
    internal static bool LogDestinationAttempts = false;
    /// <summary>How many rounds of replacements an island takes: the first for its unreached destinations, one more for
    /// replacements that end unreached.</summary>
    internal const int ReplacementRounds = 2;

    /// <summary>One destination's attempts on its island (<see cref="LogDestinationAttempts"/>).</summary>
    private sealed class DestinationAttempts
    {
        internal int Tries, Failed, Round;
        internal long Ticks;
    }

    internal static int m_replacementsTried, m_replacementsReached;

    internal static void GenerateRoutedRoads(Vector3 start, float radius,
        List<(string name,Vector3 position,float radius)> locations, Replacer? replace = null, int islandId = 0)
    {
        // This thread's own: build and join both run inside the parallel
        // island loop, and a pathfinder's AlreadyOnNetwork and LastPathCost are
        // read straight after the call that sets them.
        var finder = Finder;
        if (finder == null) return;
        // The island's searches remembered while it is planned (RoadPathfinder.RemembersSearches, where the generation keeps
        // them: GenerationMemory.KeepsSearches): one asked again on the network as it stands is answered as it was made.
        finder.RemembersSearches = GenerationMemory.KeepsSearches;
        // And its pieces of road planned (RoadSpatialGrid.RemembersPlans, GenerationMemory.KeepsPlans), as its searches are.
        RoadSpatialGrid.RemembersPlans = GenerationMemory.KeepsPlans;
        t_restsRefusedEnds = new HashSet<(float, float)>();
        try { RouteIsland(finder, start, radius, locations, replace, islandId); }
        finally { finder.RemembersSearches = false; RoadSpatialGrid.RemembersPlans = false; t_restsRefusedEnds = null; }
    }

    private static void RouteIsland(RoadPathfinder finder, Vector3 start, float radius,
        List<(string name,Vector3 position,float radius)> locations, Replacer? replace, int islandId)
    {
        var world = GenerationWorld;
        var plannerLocations = new List<RoadConnectionPlanner.Location> { new("Start",start,radius) };
        int target = locations.Count;
        var attempts = new Dictionary<(float, float), DestinationAttempts>();
        DestinationAttempts Of(Vector3 p)
        {
            var key = LocationKey(new Vector2(p.x, p.z));
            if (!attempts.TryGetValue(key, out var a)) attempts[key] = a = new DestinationAttempts();
            return a;
        }
        T Timed<T>(Vector3 p, System.Func<T> run)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return run(); } finally { Of(p).Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; }
        }
        // A location whose landing turns with it goes onto the roads only placed before them (PlacedLocations.NotPlaced):
        // one not placed is not planned for, and is counted a destination not reached (Reached). Planned locations keep
        // their index among the destinations for the loading panel.
        var planned = new List<int>();
        for (int i = 0; i < locations.Count; i++)
        {
            var location = locations[i];
            if (PlacedLocations.NotPlacedWhy(new Vector2(location.position.x, location.position.z)) is string why)
            {
                System.Threading.Interlocked.Increment(ref m_destinationsNotPlaced);
                // By name and why, so a check in game can tell a location not placed before its roads from one no road could reach.
                Log.LogInfo(System.FormattableString.Invariant($"  given no road, its landing not known ({why}): {location.name} at ({location.position.x:F0}, {location.position.z:F0})"));
                IslandActivity?.Settled(i, false);
                continue;
            }
            planned.Add(i);
            plannerLocations.Add(new(location.name,location.position,location.radius));
            NoteCustomLocation(location.name,location.position);
        }
        // Generation-local samples, released after this island. No route-history
        // recorder or study instrumentation is shipped.
        var hints = new List<Vector2>();
        Vector2 Ground(RoadConnectionPlanner.Location p) => RoadEndpoint.FindGround(world,
            new Vector2(p.Position.x,p.Position.z),p.Radius);
        // A location by its identity (its location's centre, RoadLocationProtection.Footprint.Is): the one footprint its road's search may leave or enter.
        Vector2? Location(RoadConnectionPlanner.Location p) => p.Radius>0f ? new Vector2(p.Position.x,p.Position.z) : null;
        // A pathfinder carries its own terrain cache and LastPathCost, so a
        // shared one cannot be priced on several threads. Each worker gets its
        // own, built exactly as the serial one is; the cost of a pair does not
        // depend on which finder asked.
        var activity=IslandActivity;
        using var pricers = new System.Threading.ThreadLocal<RoadPathfinder>(
            () => { var f = NewFinder(world, RoadLocationProtection.Clearance(RoadWidth)); f.Activity = activity; return f; }, trackAllValues: true);
        // Every pair's price, kept for a location's second attempt (ReachOffLevel): its cheapest road from a location already
        // reached, without searching the pairs again.
        var prices = new System.Collections.Concurrent.ConcurrentDictionary<(Vector3, Vector3), float>();
        // The pricing workers run as this island's worker (CrossIslandReads), so what they read is what the island sees.
        int island=CrossIslandReads.Current;
        // A pair asked again by a replacement round is not searched again: the price does not depend on the network.
        var asked = new System.Collections.Concurrent.ConcurrentDictionary<(Vector3, Vector3), float?>();
        float? Price(RoadConnectionPlanner.Location a,RoadConnectionPlanner.Location b)
        {
            if (asked.TryGetValue((a.Position, b.Position), out float? known) || asked.TryGetValue((b.Position, a.Position), out known)) return known;
            float? cost = CrossIslandReads.On(island,()=>PriceAs(a,b));
            asked[(a.Position, b.Position)] = cost;
            return cost;
        }
        float? PriceAs(RoadConnectionPlanner.Location a,RoadConnectionPlanner.Location b)
        {
            RoadLifecycleManager.ThrowIfCancellationRequested();
            Vector2 from=Ground(a),to=Ground(b);
            if (OpenSeaOnLine(world,from,to)) return null;
            var pricer = RoadParallel.PricingWorkers > 1 ? pricers.Value! : finder;
            var path=pricer.FindPath(from,to,RoadPathfinder.PricingIterations,new RoadLocationProtection.Targets(Location(a),Location(b)));
            float? cost = path != null && path.Count>=2 ? pricer.LastPathCost : null;
            if (cost is float c && !float.IsNaN(c) && !float.IsInfinity(c)) prices[(a.Position, b.Position)] = c;
            return cost;
        }
        bool Build(RoadConnectionPlanner.Location a,RoadConnectionPlanner.Location b)
        {
            activity?.Connecting(b.Name,Vector3.Distance(a.Position,b.Position));
            return Timed(b.Position, () => GenerateRoad(a.Position,a.Radius,b.Position,b.Radius,RoadWidth,$"{a.Name} -> {b.Name}",hints));
        }
        bool Join(RoadConnectionPlanner.Location location) => Timed(location.Position, () => JoinLocation(finder,world,hints,location));
        // The planner asks a location's join again with each of its candidate edges; where it failed and nothing was
        // written to the island's network since, it fails again the same way (FailedJoins), so it is not run again.
        var joins = new FailedJoins(Join);
        // One round of the planner over these locations (the start first), then the second attempt for each Location from
        // index `from` on.
        void Plan(List<RoadConnectionPlanner.Location> round, int from, System.Action<int> settled, int number)
        {
            RoadConnectionPlanner.Connect(round,Price,Build,joins.Join,GetLocationPriority,
                (location,result)=>Log.LogDebug($"Road destination {location.Name} at {location.Position}: {result}"),
                settled, total => activity?.Pricing(total), () => activity?.Priced(), () => activity?.Stage("Connecting"),
                (index, ok) => { if (index >= from) { var a = Of(round[index].Position); a.Tries++; if (!ok) a.Failed++; } });
            // The second attempt: a location outside the Mountains that no road reaches at height is tried once more
            // without it (ReachOffLevel), only once every location had its first: a road to another may yet pass it level. As the
            // planner tries a location, cheaply: the join to the network (its own retries), then its one cheapest priced road from a
            // location reached, or the start, never searched again.
            for (int k = Math.Max(1, from); k < round.Count; k++)
            {
                RoadLifecycleManager.ThrowIfCancellationRequested();
                var location = round[k];
                Of(location.Position).Round = number;
                ReachOffLevel(location.Name, location.Position, location.Radius, () =>
                {
                    Of(location.Position).Tries++;
                    if (Join(location)) return true;
                    int best_from = -1; float best = float.MaxValue;
                    for (int o = 0; o < round.Count; o++)
                    {
                        var other = round[o];
                        if (o == k || !(prices.TryGetValue((other.Position, location.Position), out float c) || prices.TryGetValue((location.Position, other.Position), out c))
                            || c >= best || o > 0 && !Reached(other.Position, other.Radius, other.Name)) continue;
                        best = c; best_from = o;
                    }
                    return best_from >= 0 && Build(round[best_from], location);
                });
            }
        }
        Plan(plannerLocations, 1, index => { if (index > 0) SettleDestination(locations,planned[index-1]); RefreshDestinationsReached(locations); }, 0);
        // Replacements (ReplaceUnreached): as many as the destinations left unreached, drawn by the selection's rule, planned as
        // the island's own were; then once more for the replacements left unreached.
        int unreachedFirst = CountUnreached(locations, 0, locations.Count);
        int[] tried = new int[ReplacementRounds], reached = new int[ReplacementRounds];
        if (replace != null)
            (tried, reached) = ReplaceUnreachedRounds(locations, d => Reached(d.position, d.radius, d.name), replace, (picks, kept, number) =>
            {
                foreach (var d in picks) NoteCustomLocation(d.name, d.position);
                var round = ReplacementRound(plannerLocations[0], kept, picks);
                Plan(round, round.Count - picks.Count, _ => RefreshDestinationsReached(locations), number);
            });
        // The last resort (RoadPitches.AsLastResort), once every other attempt had its turn: each destination still unreached
        // whose roads were refused with a climb too steep for its rests (t_restsRefusedEnds), in the island's order, tried once
        // more as the planner tries a location (its join, then its one cheapest priced road from a location reached or the
        // start), where such a climb may take one pitch within the island's budget and spacing (RoadRefusal).
        var restless = t_restsRefusedEnds;
        if (restless != null && restless.Count > 0)
            foreach (var d in locations.ToList())
            {
                RoadLifecycleManager.ThrowIfCancellationRequested();
                var centre = new Vector2(d.position.x, d.position.z);
                if (!restless.Contains(LocationKey(centre)) || Reached(d.position, d.radius, d.name)) continue;
                var location = new RoadConnectionPlanner.Location(d.name, d.position, d.radius);
                System.Threading.Interlocked.Increment(ref m_lastResortDestinations);
                bool reachedNow = RoadPitches.AsLastResort("the destination " + d.name + FormattableString.Invariant($" ({d.position.x:F0}, {d.position.z:F0})"), () =>
                {
                    Of(d.position).Tries++;
                    if (Timed(d.position, () => JoinLocation(finder, world, hints, location)) && Reached(d.position, d.radius, d.name)) return true;
                    RoadConnectionPlanner.Location? from = null; float best = float.MaxValue;
                    var anchors = new List<RoadConnectionPlanner.Location> { plannerLocations[0] };
                    foreach (var o in locations)
                        if (!o.position.Equals(d.position) && Reached(o.position, o.radius, o.name)) anchors.Add(new(o.name, o.position, o.radius));
                    for (int o = 0; o < anchors.Count; o++)
                    {
                        var other = anchors[o];
                        if (!(prices.TryGetValue((other.Position, d.position), out float c) || prices.TryGetValue((d.position, other.Position), out c)) || c >= best) continue;
                        best = c; from = other;
                    }
                    return from is RoadConnectionPlanner.Location anchor && Build(anchor, location) && Reached(d.position, d.radius, d.name);
                });
                if (reachedNow) System.Threading.Interlocked.Increment(ref m_lastResortReached);
            }
        System.Threading.Interlocked.Add(ref m_replacementsTried, tried.Sum());
        System.Threading.Interlocked.Add(ref m_replacementsReached, reached.Sum());
        if (replace != null || LogDestinationAttempts)
        {
            int reachedNow = locations.Count - CountUnreached(locations, 0, locations.Count);
            Log.LogInfo(FormattableString.Invariant(
                $"Replacements, island {islandId}: target {target}, reached first {target - unreachedFirst}, replacements {tried[0]}/{reached[0]} then {tried[1]}/{reached[1]} (tried/reached), chosen reached {reachedNow} of target {target} ({locations.Count} chosen)"));
        }
        if (LogDestinationAttempts)
            for (int i = 0; i < locations.Count; i++)
            {
                var d = locations[i];
                var a = Of(d.position);
                bool r = Reached(d.position, d.radius, d.name);
                Log.LogInfo(FormattableString.Invariant(
                    $"Destination, island {islandId} {d.name} at ({d.position.x:F0}, {d.position.z:F0}): round {(i < target ? 0 : a.Round)}, tries {a.Tries}, failed {a.Failed}, {a.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms, {(r ? "reached" : "unreached: " + UnreachedWhy(d.position))}"));
            }
        // These are thrown away at the end of this island, so their memo
        // counters have to be collected before they go.
        foreach (var pricer in pricers.Values) pricer.FoldTerrainMemoCounters();
    }

    /// <summary>
    /// The replacement rounds (<see cref="ReplaceUnreached"/>): as many replacements as <paramref name="locations"/> left
    /// unreached, drawn by <paramref name="replace"/> (never one chosen or tried before) and planned by <paramref name="plan"/>
    /// (the replacements, the destinations still standing, the round's number), appended to <paramref name="locations"/>; then,
    /// up to <see cref="ReplacementRounds"/>, as many again as that round's replacements left unreached. Returns, per round,
    /// the replacements tried and reached.
    /// </summary>
    internal static (int[] tried, int[] reached) ReplaceUnreachedRounds(List<(string name, Vector3 position, float radius)> locations,
        Func<(string name, Vector3 position, float radius), bool> isReached, Replacer replace,
        Action<List<(string name, Vector3 position, float radius)>, List<(string name, Vector3 position, float radius)>, int> plan)
    {
        int[] tried = new int[ReplacementRounds], reached = new int[ReplacementRounds];
        var chosen = new HashSet<(float, float)>();
        foreach (var d in locations) chosen.Add(LocationKey(new Vector2(d.position.x, d.position.z)));
        int Unreached(int from) { int n = 0; for (int i = from; i < locations.Count; i++) if (!isReached(locations[i])) n++; return n; }
        int need = Unreached(0);
        for (int number = 1; number <= ReplacementRounds && need > 0; number++)
        {
            RoadLifecycleManager.ThrowIfCancellationRequested();
            var kept = locations.Where(isReached).ToList();
            var picks = replace(kept, chosen, need);
            // Never more than asked for, never one chosen or tried before.
            picks = picks.Where(d => chosen.Add(LocationKey(new Vector2(d.position.x, d.position.z)))).Take(need).ToList();
            if (picks.Count == 0) break;
            int first = locations.Count;
            locations.AddRange(picks);
            plan(picks, kept, number);
            need = Unreached(first);
            tried[number - 1] = picks.Count;
            reached[number - 1] = picks.Count - need;
        }
        return (tried, reached);
    }

    /// <summary>A replacement round's locations (<see cref="ReplaceUnreachedRounds"/>): the start, every destination still reached, then
    /// the replacements. Which of them a replacement is paired with is the planner's one rule (<see cref="RoadConnectionPlanner.Connect"/>:
    /// each location with its nearest within reach, a pair made from either end), so no anchor is picked here: a reached destination
    /// farther off that has the replacement among its own nearest stays a candidate, as it would be among the island's own.
    /// Already reached, each of them joins at once (its join finds it served) and builds nothing.</summary>
    internal static List<RoadConnectionPlanner.Location> ReplacementRound(RoadConnectionPlanner.Location start,
        IReadOnlyList<(string name, Vector3 position, float radius)> kept, IReadOnlyList<(string name, Vector3 position, float radius)> picks)
    {
        var round = new List<RoadConnectionPlanner.Location>(1 + kept.Count + picks.Count) { start };
        foreach (var d in kept) round.Add(new(d.name, d.position, d.radius));
        foreach (var d in picks) round.Add(new(d.name, d.position, d.radius));
        return round;
    }

    private static int CountUnreached(List<(string name, Vector3 position, float radius)> locations, int from, int to)
    {
        int n = 0;
        for (int i = from; i < to; i++) if (!Reached(locations[i].position, locations[i].radius, locations[i].name)) n++;
        return n;
    }

    /// <summary>The planner's join: the location already served (<see cref="Reached"/>, as the count of locations reached), else a road from it to the
    /// nearest junction its search finds; where that road is refused or not found, to the next junction the search finds
    /// away from it, up to <see cref="JoinRetries"/> more times. The search returns only its cheapest level junction: a road
    /// there that could not be built (its ends too steep once its turns were flattened, a ridge at the junction, the junction
    /// point itself out of a road search's reach) would cut the location off where another level junction would take it.</summary>
    internal static bool JoinLocation(RoadPathfinder finder, WorldGenerator world, List<Vector2> hints, RoadConnectionPlanner.Location location)
    {
        if (hints.Count==0) return false;
        Vector2 from=RoadEndpoint.FindGround(world,new Vector2(location.Position.x,location.Position.z),location.Radius);
        return JoinTo(finder,from,hints,location.Name,location.Position,location.Radius,null,
            () => Reached(location.Position, location.Radius, location.Name),hints);
    }

    /// <summary>
    /// The join (<see cref="JoinLocation"/>, and road_connect's: ManualRoads, planning it with <see cref="PlanningWith"/>): from
    /// <paramref name="from"/> (the ground of the location at <paramref name="position"/>, exterior <paramref name="radius"/>, or
    /// a point with radius 0) a road to the nearest junction the search finds among the network's points
    /// <paramref name="allowed"/> admits; where that road is refused or not found, to the next junction the search finds away
    /// from it, up to <see cref="JoinRetries"/> more times. True where one is built, or where the network already serves it:
    /// a location by <paramref name="served"/> (its <see cref="Reached"/>), a plain point (none given) by
    /// <see cref="Reached(Vector2, System.Func{Vector2, bool})"/>, a way onto the network it walks, never the search's own
    /// junction at its first cell.
    /// The one owner of joining a network (docs/OWNERS.md).
    /// </summary>
    internal static bool JoinTo(RoadPathfinder finder, Vector2 from, IReadOnlyList<Vector2> hints, string name,
        Vector3 position, float radius, System.Func<Vector2, bool>? allowed, System.Func<bool>? served, List<Vector2>? networkHints)
    {
        var tried=new List<Vector2>();
        bool Untried(Vector2 p) { foreach (var t in tried) if ((p-t).sqrMagnitude<JoinRetryApart*JoinRetryApart) return false; return true; }
        bool Allowed(Vector2 p) => (allowed==null || allowed(p)) && Untried(p);
        var point=new Vector2(position.x,position.z);
        System.Func<bool> isServed=served ?? (() => Reached(point,allowed));
        for (int attempt=0; attempt<=JoinRetries; attempt++)
        {
            // Served already: the search says so at its start and goes no further.
            var path=finder.FindPathToNetwork(from,RoadPathfinder.CellSize,isServed,hints,tried.Count==0 ? allowed : Allowed,
                location: radius>0f ? new Vector2(position.x,position.z) : null);
            if (finder.AlreadyOnNetwork) return true;
            if (path==null || path.Count<2) { t_capture?.Refused("no route to the network",from); return false; }
            Vector2 end=path[path.Count-1];
            // The network cell the search arrived at the junction through: the road built there arrives as the search judged it.
            Vector2 through=path[path.Count-2];
            IslandActivity?.Connecting(name,Vector2.Distance(point,end));
            int noted=t_capture?.Refusals.Count ?? 0;
            if (GenerateRoad(point,radius,end,0,RoadWidth,$"{name} -> road",networkHints,through)) return true;
            // A refusal that does not say where: on the road to this junction.
            if (t_capture is RoadCapture capture)
                for (int i=noted; i<capture.Refusals.Count; i++)
                    if (capture.Refusals[i].at==null) capture.Refusals[i]=(capture.Refusals[i].why,end);
            tried.Add(end);
        }
        return false;
    }

    /// <summary>
    /// The planner's joins of one island (<see cref="GenerateRoutedRoads"/>), each failed one remembered while the island's
    /// network stays as it was (<see cref="IslandNetworkVersion"/>). <see cref="JoinLocation"/> reads only its location (name,
    /// position, radius, and whether a road must arrive level with it, <see cref="HeightNeeded"/>) and that network: asked
    /// again of a location with neither changed, it would search and build the same roads and fail the same way, so it is not
    /// run again. Any write to the network forgets them all. One per island, used on its own worker only.
    /// </summary>
    internal sealed class FailedJoins
    {
        private readonly System.Func<RoadConnectionPlanner.Location, bool> m_join;
        private readonly HashSet<(string name, Vector3 position, float radius, bool height)> m_failed = new();
        private int m_version;

        public FailedJoins(System.Func<RoadConnectionPlanner.Location, bool> join) { m_join = join; m_version = IslandNetworkVersion; }

        public bool Join(RoadConnectionPlanner.Location location)
        {
            int version = IslandNetworkVersion;
            if (version != m_version) { m_failed.Clear(); m_version = version; }
            var key = (location.Name, location.Position, location.Radius, HeightNeeded(new Vector2(location.Position.x, location.Position.z)));
            if (m_failed.Contains(key)) return false;
            if (m_join(location)) return true;
            // A join that wrote to the network before it failed leaves a different network: nothing to remember.
            if (IslandNetworkVersion == version) m_failed.Add(key);
            return false;
        }
    }

    /// <summary>How many more junctions a location's join tries once the road to its first is refused or not found. Settable for tests.</summary>
    internal static int JoinRetries = 2;
    /// <summary>A junction tried again is at least this far from every junction already tried.</summary>
    internal const float JoinRetryApart = 24f;

    // A cheap pricing heuristic, not proof that a land detour cannot exist.
    // Used only for probes; it never forbids a build/reverse search.
    private static bool OpenSeaOnLine(WorldGenerator world,Vector2 from,Vector2 to)
    {
        float span=Vector2.Distance(from,to);
        int samples=Mathf.Max(1,Mathf.CeilToInt(span/RoadPathfinder.CellSize));
        float run=0; bool river=false;
        float reach=RoadConstants.MaxBridgeSpan;
        bool Blocked() => run>reach && !river;
        for (int i=0;i<=samples;i++)
        {
            Vector2 p=Vector2.Lerp(from,to,i/(float)samples);
            if (RoadWater.OnLand(world, p))
            {
                if (Blocked()) return true;
                run=0; river=false;
            }
            else
            {
                world.GetRiverWeight(p.x,p.y,out float weight,out _);
                river|=RoadWater.River(weight);
                run+=span/samples;
            }
        }
        return Blocked();
    }
}
