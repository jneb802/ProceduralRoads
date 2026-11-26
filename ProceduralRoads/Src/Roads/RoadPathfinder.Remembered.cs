using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The searches a pathfinder remembers while an island's roads are planned (<see cref="RemembersSearches"/>): a road searched
/// again after a refusal, its earthwork re-route priced up round points already priced up, a join asked again by the second
/// attempt, each asked a search it made already, on the same network, and searched it all again. A search reads its asking
/// (its ends, their costs, the points exempt from the zones it goes round, its locations, its budget, the junction it is
/// joined through), this pathfinder's settings and its closings, openings, price-ups and bans, and the island's network
/// (<see cref="RoadNetworkGenerator.IslandNetworkVersion"/>: its roads, staircases, viaducts, crossings and locations);
/// the terrain it reads does not change in a generation. Asked again with all of those the same, bit for bit, it is answered
/// as it was: the path, its cost, its stair steps and viaduct jumps, which start and end it took, its iterations, the
/// squeezes it met and its failure line. Forgotten as the network changes and as the island ends.
/// </summary>
public partial class RoadPathfinder
{
    /// <summary>On while an island's roads are planned on this pathfinder (RoadNetworkGenerator.GenerateRoutedRoads); off, every
    /// search is made afresh. Turning it off forgets what was remembered.</summary>
    internal bool RemembersSearches
    {
        get => m_remembers;
        set { m_remembers = value; m_remembered.Clear(); }
    }

    private bool m_remembers;
    private readonly List<SearchAnswered> m_remembered = new();
    private int m_rememberedNetwork;
    /// <summary>The failure line of this pathfinder's last search, or null where it found a path.</summary>
    private string? m_lastFailure;
    /// <summary>The most searches remembered on one network: a road's searches again and its re-routes between two writes.</summary>
    private const int MostRemembered = 64;

    private static int s_recalled;
    /// <summary>Searches answered from memory this process (tests, the timing summary).</summary>
    internal static int Recalled => s_recalled;

    /// <summary>What a search reads besides the terrain, held to compare bit for bit (<see cref="Same"/>).</summary>
    private sealed class SearchAsked
    {
        public (Vector2 at, float cost)[] Starts = null!, Ends = null!;
        public Vector2? ExemptStart, ExemptEnd, LocationStart, LocationEnd, JoinedThrough;
        public int? IterationLimit;
        public int[] Settings = null!;
        public WorldGenerator World = null!;
        public Vector2i[] NoStairs = null!, StairOpen = null!, Earthwork = null!;
        public (Vector2 from, Vector2 to)[] NoJumps = null!, NoViaducts = null!;
        public (Vector2 at, float radius)[] GridOnly = null!;
    }

    private sealed class SearchAnswered
    {
        public SearchAsked Asked = null!;
        public List<Vector2>? Path;
        public float Cost;
        public (Vector2 from, Vector2 to)[] StairSteps = null!, ViaductJumps = null!;
        public int StartIndex, EndIndex, Iterations;
        public KeyValuePair<(int x, int z), (int steps, int landings)>[] Squeezes = null!;
        public string? Failure;
    }

    private static int Bits(float v) => TerrainFingerprint.Bits(v);

    /// <summary>The settings a search reads: this pathfinder's prices, switches and caps, and the generation's search
    /// settings (its budget, the junction step, the squeeze corridor, whether shallows are crossings).</summary>
    private int[] SettingsNow() => new[]
    {
        Bits(SlopeMultiplier), Bits(RiverPenalty), Bits(WaterPenalty), Bits(SteepSlopePenalty), Bits(SteepSlopeThreshold),
        Bits(TerrainVariancePenalty), Bits(TerrainVarianceThreshold), Bits(BaseCost), Bits(StandingWaterPenalty),
        Fords ? 1 : 0, Bridges ? 1 : 0, Bits(BridgeCostFixed), Bits(BridgeCostPerMeter), Bits(BridgeHeightShare), Viaducts ? 1 : 0, Bits(ViaductCostFixed),
        Bits(ViaductCostPerMeter), Bits(ViaductEndStairCostPerMeter), Bits(ViaductApproachReach), Bits(ViaductApproachFactor), Stairs ? 1 : 0, Bits(StairCostFixed), Bits(StairCostPerMeterClimbed),
        Bits(MaxGrade), Bits(ReuseFactor), Bits(LocationClearance), QuadVariance ? 1 : 0, Bits(TurnLimitDegrees), Bits(Meander),
        Bits(TurnWeight), MaxIterations, Bits(JunctionStep), Bits(SqueezeCorridor), RoadCrossingDetector.Shallows ? 1 : 0,
        StopsAtItsGoal ? 1 : 0,
    };

    private static Vector2i[] Cells(HashSet<Vector2i> cells)
    {
        var all = new Vector2i[cells.Count];
        cells.CopyTo(all);
        return all;
    }

    private SearchAsked Asking(IReadOnlyList<(Vector2 at, float cost)> starts, IReadOnlyList<(Vector2 at, float cost)> ends,
        Vector2? exemptStart, Vector2? exemptEnd, RoadLocationProtection.Targets locations, int? iterationLimit, Vector2? joinedThrough)
    {
        var s = new (Vector2 at, float cost)[starts.Count];
        for (int i = 0; i < s.Length; i++) s[i] = starts[i];
        var e = new (Vector2 at, float cost)[ends.Count];
        for (int i = 0; i < e.Length; i++) e[i] = ends[i];
        return new SearchAsked
        {
            Starts = s, Ends = e, ExemptStart = exemptStart, ExemptEnd = exemptEnd, LocationStart = locations.Start, LocationEnd = locations.End,
            JoinedThrough = joinedThrough, IterationLimit = iterationLimit, Settings = SettingsNow(), World = m_worldGen,
            NoStairs = Cells(m_noStairs), StairOpen = Cells(m_stairOpen), Earthwork = Cells(m_earthworkPenalty), NoJumps = m_noJumps.ToArray(),
            GridOnly = m_gridOnly.ToArray(), NoViaducts = m_noViaducts.ToArray(),
        };
    }

    private static bool Same(float a, float b) => Bits(a) == Bits(b);
    private static bool Same(Vector2 a, Vector2 b) => Same(a.x, b.x) && Same(a.y, b.y);
    private static bool Same(Vector2? a, Vector2? b) => a.HasValue == b.HasValue && (!a.HasValue || Same(a.Value, b!.Value));

    private static bool Same((Vector2 at, float cost)[] a, (Vector2 at, float cost)[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!Same(a[i].at, b[i].at) || !Same(a[i].cost, b[i].cost)) return false;
        return true;
    }

    private static bool Same((Vector2 from, Vector2 to)[] a, (Vector2 from, Vector2 to)[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!Same(a[i].from, b[i].from) || !Same(a[i].to, b[i].to)) return false;
        return true;
    }

    /// <summary>The same cells, in any order (a set's).</summary>
    private static bool Same(Vector2i[] a, Vector2i[] b)
    {
        if (a.Length != b.Length) return false;
        if (a.Length == 0) return true;
        var set = new HashSet<Vector2i>(a, RoadGridComparer.Instance);
        foreach (Vector2i c in b) if (!set.Contains(c)) return false;
        return true;
    }

    private static bool Same(SearchAsked a, SearchAsked b)
    {
        if (!ReferenceEquals(a.World, b.World) || a.IterationLimit != b.IterationLimit) return false;
        if (!Same(a.Starts, b.Starts) || !Same(a.Ends, b.Ends)) return false;
        if (!Same(a.ExemptStart, b.ExemptStart) || !Same(a.ExemptEnd, b.ExemptEnd) || !Same(a.LocationStart, b.LocationStart)
            || !Same(a.LocationEnd, b.LocationEnd) || !Same(a.JoinedThrough, b.JoinedThrough)) return false;
        if (a.Settings.Length != b.Settings.Length) return false;
        for (int i = 0; i < a.Settings.Length; i++) if (a.Settings[i] != b.Settings[i]) return false;
        return Same(a.NoJumps, b.NoJumps) && Same(a.NoViaducts, b.NoViaducts) && Same(a.GridOnly, b.GridOnly) && Same(a.NoStairs, b.NoStairs) && Same(a.StairOpen, b.StairOpen) && Same(a.Earthwork, b.Earthwork);
    }

    /// <summary>The search asked so on the network as it stands, or null; what was remembered on another network is forgotten.</summary>
    private SearchAnswered? Recall(SearchAsked asked)
    {
        int network = RoadNetworkGenerator.IslandNetworkVersion;
        if (network != m_rememberedNetwork) { m_remembered.Clear(); m_rememberedNetwork = network; return null; }
        foreach (SearchAnswered known in m_remembered)
            if (Same(known.Asked, asked)) return known;
        return null;
    }

    private void Remember(SearchAsked asked, List<Vector2>? path, Dictionary<(int x, int z), (int steps, int landings)>? squeezedBefore)
    {
        // A search that wrote to the network (none does) or met another network is not remembered.
        if (RoadNetworkGenerator.IslandNetworkVersion != m_rememberedNetwork) return;
        var squeezes = new List<KeyValuePair<(int x, int z), (int steps, int landings)>>();
        foreach (var hit in m_squeezeHits)
        {
            var (steps, landings) = hit.Value;
            if (squeezedBefore != null && squeezedBefore.TryGetValue(hit.Key, out var had)) (steps, landings) = (steps - had.steps, landings - had.landings);
            if (steps != 0 || landings != 0) squeezes.Add(new KeyValuePair<(int x, int z), (int steps, int landings)>(hit.Key, (steps, landings)));
        }
        if (m_remembered.Count >= MostRemembered) m_remembered.RemoveAt(0);
        m_remembered.Add(new SearchAnswered
        {
            Asked = asked, Path = path == null ? null : new List<Vector2>(path), Cost = LastPathCost, StairSteps = LastStairSteps.ToArray(),
            ViaductJumps = LastViaductJumps.ToArray(), StartIndex = LastStartIndex, EndIndex = LastEndIndex, Iterations = m_lastIterations,
            Squeezes = squeezes.ToArray(), Failure = m_lastFailure,
        });
    }

    /// <summary>A remembered search, answered as it was made: what it leaves on this pathfinder set as it left them.</summary>
    private List<Vector2>? Replay(SearchAnswered known, Vector2? exemptStart, Vector2? exemptEnd, RoadLocationProtection.Targets locations)
    {
        RoadLifecycleManager.ThrowIfCancellationRequested();
        System.Threading.Interlocked.Increment(ref s_recalled);
        m_searchStart = exemptStart;
        m_searchEnd = exemptEnd;
        m_locations = locations;
        LastPathCost = known.Cost;
        LastStairSteps.Clear();
        LastStairSteps.AddRange(known.StairSteps);
        LastViaductJumps.Clear();
        LastViaductJumps.AddRange(known.ViaductJumps);
        // Each jump on the ground of the search asked (its ends and locations, set above), as the search made it.
        LastViaductGrounds.Clear();
        for (int k = 0; k < known.ViaductJumps.Length; k++) LastViaductGrounds.Add(Ground);
        LastStartIndex = known.StartIndex;
        LastEndIndex = known.EndIndex;
        m_lastIterations = known.Iterations;
        foreach (var hit in known.Squeezes)
        {
            m_squeezeHits.TryGetValue(hit.Key, out var n);
            m_squeezeHits[hit.Key] = (n.steps + hit.Value.steps, n.landings + hit.Value.landings);
        }
        m_lastFailure = known.Failure;
        if (known.Failure != null) Log.LogDebug(known.Failure);
        return known.Path == null ? null : new List<Vector2>(known.Path);
    }
}
