using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

public static partial class RoadNetworkGenerator
{
    private static void GenerateRoutedRoads(Vector3 start, float radius,
        List<(string name,Vector3 position,float radius)> locations)
    {
        // This thread's own: build and join both run inside the parallel
        // island loop, and a pathfinder's AlreadyOnNetwork and LastPathCost are
        // read straight after the call that sets them.
        var finder = Finder;
        if (finder == null) return;
        var world = GenerationWorld;
        var places = new List<RoadConnectionPlanner.Place> { new("Start",start,radius) };
        foreach (var place in locations) places.Add(new(place.name,place.position,place.radius));
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
        float? Price(RoadConnectionPlanner.Place a,RoadConnectionPlanner.Place b)
        {
            RoadLifecycleManager.ThrowIfCancellationRequested();
            Vector2 from=Ground(a),to=Ground(b);
            if (OpenSeaOnLine(world,from,to)) return null;
            var pricer = RoadParallel.PricingWorkers > 1 ? pricers.Value! : finder;
            var path=pricer.FindPath(from,to,System.Math.Max(1000,RoadPathfinder.MaxIterations/2));
            return path != null && path.Count>=2 ? pricer.LastPathCost : null;
        }
        bool Build(RoadConnectionPlanner.Place a,RoadConnectionPlanner.Place b)
        {
            activity?.Connecting(b.Name,Vector3.Distance(a.Position,b.Position));
            return GenerateRoad(a.Position,a.Radius,b.Position,b.Radius,RoadWidth,$"{a.Name} -> {b.Name}",hints);
        }
        bool Join(RoadConnectionPlanner.Place place) => JoinPlace(finder,world,hints,place);
        RoadConnectionPlanner.Connect(places,Price,Build,Join,GetLocationPriority,
            (place,result)=>Log.LogDebug($"Road destination {place.Name} at {place.Position}: {result}"),
            index => { SettleDestination(locations,index-1); RefreshDestinationsReached(locations); },
            total => activity?.Pricing(total), () => activity?.Priced(), () => activity?.Stage("Connecting"));
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
        var tried=new List<Vector2>();
        bool Untried(Vector2 p) { foreach (var t in tried) if ((p-t).sqrMagnitude<JoinRetryApart*JoinRetryApart) return false; return true; }
        for (int attempt=0; attempt<=JoinRetries; attempt++)
        {
            // Served already: the search says so at its start and goes no further.
            var path=finder.FindPathToNetwork(from,8,hints,tried.Count==0 ? null : Untried,
                served: () => PlaceServed(place.Name, place.Position, place.Radius));
            if (finder.AlreadyOnNetwork) return true;
            if (path==null || path.Count<2) return false;
            Vector2 end=path[path.Count-1];
            IslandActivity?.Connecting(place.Name,Vector2.Distance(new Vector2(place.Position.x,place.Position.z),end));
            if (GenerateRoad(place.Position,place.Radius,new Vector3(end.x,0,end.y),0,RoadWidth,$"{place.Name} -> road",hints)) return true;
            tried.Add(end);
        }
        return false;
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
        float reach=RoadConstants.MaxBridgeCrossingCells*RoadPathfinder.CellSize;
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
