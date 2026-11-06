using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

public static partial class RoadNetworkGenerator
{
    internal static void GenerateRoutedRoads(Vector3 start, float radius,
        List<(string name,Vector3 position,float radius)> locations)
    {
        // This thread's own: build and join both run inside the parallel
        // island loop, and a pathfinder's AlreadyOnNetwork and LastPathCost are
        // read straight after the call that sets them.
        var finder = Finder;
        if (finder == null) return;
        var world = GenerationWorld;
        var places = new List<RoadConnectionPlanner.Place> { new("Start",start,radius) };
        // A place whose landing turns with it goes onto the roads only placed before them (PlacedSites.NotPlaced):
        // one not placed is not planned for, and is counted a destination not reached (Reached). Planned places keep
        // their index among the destinations for the loading panel.
        var planned = new List<int>();
        for (int i = 0; i < locations.Count; i++)
        {
            var place = locations[i];
            if (PlacedSites.NotPlacedWhy(new Vector2(place.position.x, place.position.z)) is string why)
            {
                System.Threading.Interlocked.Increment(ref m_destinationsNotPlaced);
                // By name and why, so a check in game can tell a place not placed before its roads from one no road could reach.
                Log.LogInfo(System.FormattableString.Invariant($"  given no road, its landing not known ({why}): {place.name} at ({place.position.x:F0}, {place.position.z:F0})"));
                IslandActivity?.Settled(i, false);
                continue;
            }
            planned.Add(i);
            places.Add(new(place.name,place.position,place.radius));
            NoteCustomPlace(place.name,place.position);
        }
        // Generation-local samples, released after this island. No route-history
        // recorder or study instrumentation is shipped.
        var hints = new List<Vector2>();
        Vector2 Ground(RoadConnectionPlanner.Place p) => RoadEndpoint.FindGround(world,
            new Vector2(p.Position.x,p.Position.z),p.Radius);
        // A pathfinder carries its own terrain cache and LastPathCost, so a
        // shared one cannot be priced on several threads. Each worker gets its
        // own, built exactly as the serial one is; the cost of a pair does not
        // depend on which finder asked.
        var activity=IslandActivity;
        using var pricers = new System.Threading.ThreadLocal<RoadPathfinder>(
            () => { var f = NewFinder(world, RoadSiteProtection.Clearance(RoadWidth)); f.Activity = activity; return f; }, trackAllValues: true);
        // Every pair's price, kept for a place's second attempt (ReachOffLevel): its cheapest road from a place already
        // reached, without searching the pairs again.
        var prices = new System.Collections.Concurrent.ConcurrentDictionary<(Vector3, Vector3), float>();
        // The pricing workers run as this island's worker (CrossIslandReads), so what they read is what the island sees.
        int island=CrossIslandReads.Current;
        float? Price(RoadConnectionPlanner.Place a,RoadConnectionPlanner.Place b) => CrossIslandReads.On(island,()=>PriceAs(a,b));
        float? PriceAs(RoadConnectionPlanner.Place a,RoadConnectionPlanner.Place b)
        {
            RoadLifecycleManager.ThrowIfCancellationRequested();
            Vector2 from=Ground(a),to=Ground(b);
            if (OpenSeaOnLine(world,from,to)) return null;
            var pricer = RoadParallel.PricingWorkers > 1 ? pricers.Value! : finder;
            var path=pricer.FindPath(from,to,RoadPathfinder.PricingIterations);
            float? cost = path != null && path.Count>=2 ? pricer.LastPathCost : null;
            if (cost is float c && !float.IsNaN(c) && !float.IsInfinity(c)) prices[(a.Position, b.Position)] = c;
            return cost;
        }
        bool Build(RoadConnectionPlanner.Place a,RoadConnectionPlanner.Place b)
        {
            activity?.Connecting(b.Name,Vector3.Distance(a.Position,b.Position));
            return GenerateRoad(a.Position,a.Radius,b.Position,b.Radius,RoadWidth,$"{a.Name} -> {b.Name}",hints);
        }
        bool Join(RoadConnectionPlanner.Place place) => JoinPlace(finder,world,hints,place);
        // The planner asks a place's join again with each of its candidate edges; where it failed and nothing was
        // written to the island's network since, it fails again the same way (FailedJoins), so it is not run again.
        var joins = new FailedJoins(Join);
        RoadConnectionPlanner.Connect(places,Price,Build,joins.Join,GetLocationPriority,
            (place,result)=>Log.LogDebug($"Road destination {place.Name} at {place.Position}: {result}"),
            index => { if (index > 0) SettleDestination(locations,planned[index-1]); RefreshDestinationsReached(locations); },
            total => activity?.Pricing(total), () => activity?.Priced(), () => activity?.Stage("Connecting"));
        // The second attempt: a place outside the Mountains that no road reaches at height is tried once more
        // without it (ReachOffLevel), only once every place had its first: a road to another may yet pass it level. As the
        // planner tries a place, cheaply: the join to the network (its own retries), then its one cheapest priced road from a
        // place reached, or the start, never searched again.
        for (int k = 1; k < places.Count; k++)
        {
            RoadLifecycleManager.ThrowIfCancellationRequested();
            var place = places[k];
            ReachOffLevel(place.Name, place.Position, place.Radius, () =>
            {
                if (Join(place)) return true;
                int from = -1; float best = float.MaxValue;
                for (int o = 0; o < places.Count; o++)
                {
                    var other = places[o];
                    if (o == k || !(prices.TryGetValue((other.Position, place.Position), out float c) || prices.TryGetValue((place.Position, other.Position), out c))
                        || c >= best || o > 0 && !Reached(other.Position, other.Radius, other.Name)) continue;
                    best = c; from = o;
                }
                return from >= 0 && Build(places[from], place);
            });
        }
        // These are thrown away at the end of this island, so their memo
        // counters have to be collected before they go.
        foreach (var pricer in pricers.Values) pricer.FoldTerrainMemoCounters();
    }

    /// <summary>
    /// Whether the network serves a place: the one test for the planner's join and its search's start
    /// (<see cref="JoinPlace"/>), and the same as the count of places reached (<see cref="Reached"/>).
    /// A boss's entrance is served only by a road onto its own landing, never by a road passing within reach of it.
    /// </summary>
    internal static bool PlaceServed(string name, Vector3 position, float radius) =>
        Reached(position, radius, name);

    /// <summary>The planner's join: the place already served (<see cref="PlaceServed"/>), else a road from it to the
    /// nearest junction its search finds; where that road is refused or not found, to the next junction the search finds
    /// away from it, up to <see cref="JoinRetries"/> more times. The search returns only its cheapest level junction: a road
    /// there that could not be built (its ends too steep once its turns were flattened, a ridge at the junction, the junction
    /// point itself out of a road search's reach) would cut the place off where another level junction would take it.</summary>
    internal static bool JoinPlace(RoadPathfinder finder, WorldGenerator world, List<Vector2> hints, RoadConnectionPlanner.Place place)
    {
        if (hints.Count==0) return false;
        Vector2 from=RoadEndpoint.FindGround(world,new Vector2(place.Position.x,place.Position.z),place.Radius);
        return JoinTo(finder,from,hints,place.Name,place.Position,place.Radius,null,
            () => PlaceServed(place.Name, place.Position, place.Radius),hints);
    }

    /// <summary>
    /// The join (<see cref="JoinPlace"/>, and road_connect's: ManualRoads, planning it with <see cref="PlanningWith"/>): from
    /// <paramref name="from"/> (the ground of the place at <paramref name="position"/>, exterior <paramref name="radius"/>, or
    /// a point with radius 0) a road to the nearest junction the search finds among the network's points
    /// <paramref name="allowed"/> admits; where that road is refused or not found, to the next junction the search finds away
    /// from it, up to <see cref="JoinRetries"/> more times. True where one is built, or where the network already serves it
    /// (<paramref name="served"/>, or within the search's reach of a road where none is given).
    /// </summary>
    internal static bool JoinTo(RoadPathfinder finder, Vector2 from, IReadOnlyList<Vector2> hints, string name,
        Vector3 position, float radius, System.Func<Vector2, bool>? allowed, System.Func<bool>? served, List<Vector2>? networkHints)
    {
        var tried=new List<Vector2>();
        bool Untried(Vector2 p) { foreach (var t in tried) if ((p-t).sqrMagnitude<JoinRetryApart*JoinRetryApart) return false; return true; }
        bool Allowed(Vector2 p) => (allowed==null || allowed(p)) && Untried(p);
        for (int attempt=0; attempt<=JoinRetries; attempt++)
        {
            // Served already: the search says so at its start and goes no further.
            var path=finder.FindPathToNetwork(from,RoadPathfinder.CellSize,hints,tried.Count==0 ? allowed : Allowed,served: served);
            if (finder.AlreadyOnNetwork) return true;
            if (path==null || path.Count<2) { t_capture?.Refused("no route to the network",from); return false; }
            Vector2 end=path[path.Count-1];
            IslandActivity?.Connecting(name,Vector2.Distance(new Vector2(position.x,position.z),end));
            int noted=t_capture?.Refusals.Count ?? 0;
            if (GenerateRoad(position,radius,new Vector3(end.x,0,end.y),0,RoadWidth,$"{name} -> road",networkHints)) return true;
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
    /// network stays as it was (<see cref="IslandNetworkVersion"/>). <see cref="JoinPlace"/> reads only its place (name,
    /// position, radius, and whether a road must arrive level with it, <see cref="HeightNeeded"/>) and that network: asked
    /// again of a place with neither changed, it would search and build the same roads and fail the same way, so it is not
    /// run again. Any write to the network forgets them all. One per island, used on its own worker only.
    /// </summary>
    internal sealed class FailedJoins
    {
        private readonly System.Func<RoadConnectionPlanner.Place, bool> m_join;
        private readonly HashSet<(string name, Vector3 position, float radius, bool height)> m_failed = new();
        private int m_version;

        public FailedJoins(System.Func<RoadConnectionPlanner.Place, bool> join) { m_join = join; m_version = IslandNetworkVersion; }

        public bool Join(RoadConnectionPlanner.Place place)
        {
            int version = IslandNetworkVersion;
            if (version != m_version) { m_failed.Clear(); m_version = version; }
            var key = (place.Name, place.Position, place.Radius, HeightNeeded(new Vector2(place.Position.x, place.Position.z)));
            if (m_failed.Contains(key)) return false;
            if (m_join(place)) return true;
            // A join that wrote to the network before it failed leaves a different network: nothing to remember.
            if (IslandNetworkVersion == version) m_failed.Add(key);
            return false;
        }
    }

    /// <summary>How many more junctions a place's join tries once the road to its first is refused or not found. Settable for tests.</summary>
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
