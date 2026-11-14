using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>A tree of priced candidate edges, with branches joining existing roads.</summary>
public static class RoadConnectionPlanner
{
    public readonly struct Location
    {
        public readonly string Name;
        public readonly Vector3 Position;
        public readonly float Radius;
        public Location(string name, Vector3 position, float radius)
        { Name=name; Position=position; Radius=radius; }
    }

    /// <summary>How many nearest locations each location is paired with for pricing.</summary>
    internal const int PairNeighbours = 8;
    /// <summary>The farthest two locations are paired for pricing, in metres.</summary>
    internal const float PairReach = 2200f;

    /// <summary>The one owner of which candidate edges are tried, each once, and what each outcome reports (docs/OWNERS.md).</summary>
    public static void Connect(IReadOnlyList<Location> locations,
        Func<Location,Location,float?> price, Func<Location,Location,bool> build,
        Func<Location,bool> joinNetwork, Func<string,int> priority, Action<Location,string> report) =>
        Connect(locations,price,build,joinNetwork,priority,report,null,null,null,null);

    internal static void Connect(IReadOnlyList<Location> locations,
        Func<Location,Location,float?> price, Func<Location,Location,bool> build,
        Func<Location,bool> joinNetwork, Func<string,int> priority,
        Action<Location,string> report, Action<int>? settled = null, Action<int>? pricing = null, Action? pricedOne = null, Action? connecting = null,
        Action<int,bool>? attempted = null)
    {
        if (locations.Count < 2) return;
        static (int,int) Key(int a,int b) => a<b ? (a,b) : (b,a);
        var pairs = new HashSet<(int,int)>();
        for (int a=0; a<locations.Count; a++)
            foreach (int b in Enumerable.Range(0,locations.Count).Where(b => b!=a)
                .OrderBy(b => Vector3.SqrMagnitude(locations[a].Position-locations[b].Position)).ThenBy(b=>b).Take(PairNeighbours))
                if (Vector3.Distance(locations[a].Position,locations[b].Position)<=PairReach)
                    pairs.Add(Key(a,b));
        // Pricing is the one pass that reads no committed road: it is a search
        // over raw terrain between two locations, so the answer does not depend on
        // what has been built or on the order the pairs are asked in. It is
        // also the larger half of the search work. So price every candidate on
        // worker threads, then fill `costs` serially in the same sorted order
        // as before -- the prices are identical and so is the insertion order,
        // which is what the planner's tie-breaking reads.
        //
        // Build and join stay strictly serial: both DO read the network as it
        // grows, and reordering them would change the roads.
        var ordered = pairs.OrderBy(p=>p.Item1).ThenBy(p=>p.Item2).ToList();
        var priced = new float?[ordered.Count];
        pricing?.Invoke(ordered.Count);
        float? PriceOne(int i) { try { return price(locations[ordered[i].Item1],locations[ordered[i].Item2]); } finally { pricedOne?.Invoke(); } }
        int workers = RoadParallel.PricingWorkers;
        if (workers > 1 && ordered.Count > 1)
            System.Threading.Tasks.Parallel.For(0, ordered.Count,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = workers },
                i => priced[i] = PriceOne(i));
        else
            for (int i=0;i<ordered.Count;i++) priced[i] = PriceOne(i);

        connecting?.Invoke();
        var costs = new Dictionary<(int,int),float>();
        for (int i=0;i<ordered.Count;i++)
        {
            float? cost = priced[i];
            if (cost.HasValue && !float.IsNaN(cost.Value) && !float.IsInfinity(cost.Value))
                costs.Add(ordered[i],cost.Value);
        }
        var tree = new SortedSet<int> { 0 };
        var remaining = new SortedSet<int>(Enumerable.Range(1,locations.Count-1));
        // After pricing, pairs is the set of candidates not yet offered to the
        // full builder. Removing a failed price below must not recreate one.
        bool BuildOnce(int from,int to) => pairs.Remove(Key(from,to))
            && build(locations[from],locations[to]);
        // Candidate edges that never received a price, nearest first. Locations
        // already in the tree come first because reaching one of those joins
        // the network outright; a location still waiting its turn is tried after,
        // since a boss is ordered ahead of the very neighbour it needs and
        // would otherwise be left with nothing to build to.
        IEnumerable<int> Unpriced(int location) => tree.Concat(remaining)
            .Where(t => t != location && pairs.Contains(Key(location,t)) && !costs.ContainsKey(Key(location,t)))
            .OrderBy(t => tree.Contains(t) ? 0 : 1)
            .ThenBy(t => Vector3.SqrMagnitude(locations[location].Position-locations[t].Position))
            .ThenBy(t => t)
            .ToList();
        // A Location's failed attempts (a priced edge's join and build, a join with no priced edge, an unpriced edge) are
        // reported to `attempted` (the destination log); every remaining edge is still tried.
        while (remaining.Count>0)
        {
            RoadLifecycleManager.ThrowIfCancellationRequested();
            int from=-1,to=-1; float best=float.MaxValue;
            foreach (int a in tree)
                foreach (int b in remaining)
                    if (costs.TryGetValue(Key(a,b),out float cost) && cost<best)
                    { from=a; to=b; best=cost; }
            if (to<0)
            {
                int next = remaining.OrderByDescending(i=>priority(locations[i].Name)).ThenBy(i=>i).First();
                bool joined = joinNetwork(locations[next]);
                if (!joined)
                {
                    attempted?.Invoke(next,false);
                    // A pricing probe runs at a reduced budget and the open-sea
                    // test is a straight line. Neither may be the thing that
                    // decides a location cannot be reached, so the candidate edges
                    // that were never priced are tried once at the build budget.
                    foreach (int other in Unpriced(next))
                        if (!BuildOnce(other,next)) attempted?.Invoke(next,false);
                        else
                        {
                            joined=true;
                            // This road serves both ends even if neither end
                            // previously belonged to a built component. Do not
                            // schedule the waiting partner to build it again.
                            if (remaining.Remove(other))
                            {
                                settled?.Invoke(other);
                                tree.Add(other);
                                report(locations[other],"connected by an unpriced candidate edge");
                            }
                            break;
                        }
                }
                if (joined) attempted?.Invoke(next,true);
                report(locations[next],joined ? "joined without a priced edge" : "new component; no priced connection to network");
                tree.Add(next); remaining.Remove(next); settled?.Invoke(next);
                continue;
            }
            if (joinNetwork(locations[to]) || BuildOnce(from,to))
            {
                attempted?.Invoke(to,true);
                tree.Add(to); remaining.Remove(to); settled?.Invoke(to);
                report(locations[to],"connected");
            }
            else
            {
                // Remove this failed edge, not the destination. Other candidates
                // can still connect it; every edge is attempted at most once.
                costs.Remove(Key(from,to));
                report(locations[to],$"build from {locations[from].Name} failed; trying remaining candidates");
                attempted?.Invoke(to,false);
            }
        }
    }
}
