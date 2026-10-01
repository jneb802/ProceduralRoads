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
        var world = WorldGenerator.instance;
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
        using var pricers = new System.Threading.ThreadLocal<RoadPathfinder>(
            () => new RoadPathfinder(world) { SiteClearance = RoadWidth * 0.5f + 2f }, trackAllValues: true);
        float? Price(RoadConnectionPlanner.Place a,RoadConnectionPlanner.Place b)
        {
            Vector2 from=Ground(a),to=Ground(b);
            if (OpenSeaOnLine(world,from,to)) return null;
            var pricer = RoadParallel.PricingWorkers > 1 ? pricers.Value! : finder;
            var path=pricer.FindPath(from,to,System.Math.Max(1000,RoadPathfinder.MaxIterations/2));
            return path != null && path.Count>=2 ? pricer.LastPathCost : null;
        }
        bool Build(RoadConnectionPlanner.Place a,RoadConnectionPlanner.Place b) =>
            GenerateRoad(a.Position,a.Radius,b.Position,b.Radius,RoadWidth,$"{a.Name} -> {b.Name}",hints);
        bool Join(RoadConnectionPlanner.Place place)
        {
            if (hints.Count==0) return false;
            Vector2 from=Ground(place);
            // Reached when the network comes within the place's own trim
            // radius, where a road to it would stop anyway (not 8 m: a road
            // ending 20 m from a place left it "unreached" and a second road
            // bridged the river towards it).
            float reached = RoadSiteProtection.RadiusAt(new Vector2(place.Position.x,place.Position.z),place.Radius) + RoadWidth*0.5f + 4f;
            if (RoadSpatialGrid.TryGetRoadWithin(new Vector2(place.Position.x,place.Position.z),System.Math.Max(8f,reached),out _)) return true;
            if (RoadSpatialGrid.TryGetRoadWithin(from,8,out _)) return true;
            var path=finder.FindPathToNetwork(from,8,hints);
            if (finder.AlreadyOnNetwork) return true;
            if (path==null || path.Count<2) return false;
            Vector2 end=path[path.Count-1];
            return GenerateRoad(place.Position,place.Radius,new Vector3(end.x,0,end.y),0,
                RoadWidth,$"{place.Name} -> road",hints);
        }
        RoadConnectionPlanner.Connect(places,Price,Build,Join,GetLocationPriority,
            (place,result)=>Log.LogDebug($"Road destination {place.Name} at {place.Position}: {result}"));
        // These are thrown away at the end of this island, so their memo
        // counters have to be collected before they go.
        foreach (var pricer in pricers.Values) pricer.FoldTerrainMemoCounters();
    }

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
            if (world.GetHeight(p.x,p.y)>=RoadPathfinder.FloorFor(world.GetBiome(p.x,p.y)))
            {
                if (Blocked()) return true;
                run=0; river=false;
            }
            else
            {
                world.GetRiverWeight(p.x,p.y,out float weight,out _);
                river|=weight>RoadConstants.RiverImpassableThreshold;
                run+=span/samples;
            }
        }
        return Blocked();
    }
}
