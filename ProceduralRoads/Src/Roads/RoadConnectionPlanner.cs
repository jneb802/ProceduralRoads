using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>A tree of priced candidate edges, with branches joining existing roads.</summary>
public static class RoadConnectionPlanner
{
    public readonly struct Place
    {
        public readonly string Name;
        public readonly Vector3 Position;
        public readonly float Radius;
        public Place(string name, Vector3 position, float radius)
        { Name=name; Position=position; Radius=radius; }
    }

    public static void Connect(IReadOnlyList<Place> places,
        Func<Place,Place,float?> price, Func<Place,Place,bool> build,
        Func<Place,bool> joinNetwork, Func<string,int> priority,
        Action<Place,string> report)
    {
        if (places.Count < 2) return;
        static (int,int) Key(int a,int b) => a<b ? (a,b) : (b,a);
        var pairs = new HashSet<(int,int)>();
        for (int a=0; a<places.Count; a++)
            foreach (int b in Enumerable.Range(0,places.Count).Where(b => b!=a)
                .OrderBy(b => Vector3.SqrMagnitude(places[a].Position-places[b].Position)).ThenBy(b=>b).Take(8))
                if (Vector3.Distance(places[a].Position,places[b].Position)<=2200)
                    pairs.Add(Key(a,b));
        // Pricing is the one pass that reads no committed road: it is a search
        // over raw terrain between two places, so the answer does not depend on
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
        int workers = RoadParallel.PricingWorkers;
        if (workers > 1 && ordered.Count > 1)
            System.Threading.Tasks.Parallel.For(0, ordered.Count,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = workers },
                i => priced[i] = price(places[ordered[i].Item1],places[ordered[i].Item2]));
        else
            for (int i=0;i<ordered.Count;i++) priced[i] = price(places[ordered[i].Item1],places[ordered[i].Item2]);

        var costs = new Dictionary<(int,int),float>();
        for (int i=0;i<ordered.Count;i++)
        {
            float? cost = priced[i];
            if (cost.HasValue && !float.IsNaN(cost.Value) && !float.IsInfinity(cost.Value))
                costs.Add(ordered[i],cost.Value);
        }
        var tree = new SortedSet<int> { 0 };
        var remaining = new SortedSet<int>(Enumerable.Range(1,places.Count-1));
        // After pricing, pairs is the set of candidates not yet offered to the
        // full builder. Removing a failed price below must not recreate one.
        bool BuildOnce(int from,int to) => pairs.Remove(Key(from,to))
            && build(places[from],places[to]);
        // Candidate edges that never received a price, nearest first. Places
        // already in the tree come first because reaching one of those joins
        // the network outright; a place still waiting its turn is tried after,
        // since a boss is ordered ahead of the very neighbour it needs and
        // would otherwise be left with nothing to build to.
        IEnumerable<int> Unpriced(int place) => tree.Concat(remaining)
            .Where(t => t != place && pairs.Contains(Key(place,t)) && !costs.ContainsKey(Key(place,t)))
            .OrderBy(t => tree.Contains(t) ? 0 : 1)
            .ThenBy(t => Vector3.SqrMagnitude(places[place].Position-places[t].Position))
            .ThenBy(t => t)
            .ToList();
        while (remaining.Count>0)
        {
            int from=-1,to=-1; float best=float.MaxValue;
            foreach (int a in tree)
                foreach (int b in remaining)
                    if (costs.TryGetValue(Key(a,b),out float cost) && cost<best)
                    { from=a; to=b; best=cost; }
            if (to<0)
            {
                int next = remaining.OrderByDescending(i=>priority(places[i].Name)).ThenBy(i=>i).First();
                bool joined = joinNetwork(places[next]);
                if (!joined)
                {
                    // A pricing probe runs at a reduced budget and the open-sea
                    // test is a straight line. Neither may be the thing that
                    // decides a place cannot be reached, so the candidate edges
                    // that were never priced are tried once at the build budget.
                    foreach (int other in Unpriced(next))
                        if (BuildOnce(other,next))
                        {
                            joined=true;
                            // This road serves both ends even if neither end
                            // previously belonged to a built component. Do not
                            // schedule the waiting partner to build it again.
                            if (remaining.Remove(other))
                            {
                                tree.Add(other);
                                report(places[other],"connected by an unpriced candidate edge");
                            }
                            break;
                        }
                }
                report(places[next],joined ? "joined without a priced edge" : "new component; no priced connection to network");
                tree.Add(next); remaining.Remove(next);
                continue;
            }
            if (joinNetwork(places[to]) || BuildOnce(from,to))
            {
                tree.Add(to); remaining.Remove(to);
                report(places[to],"connected");
            }
            else
            {
                // Remove this failed edge, not the destination. Other candidates
                // can still connect it; every edge is attempted at most once.
                costs.Remove(Key(from,to));
                report(places[to],$"build from {places[from].Name} failed; trying remaining candidates");
            }
        }
    }
}
