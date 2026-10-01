using System.Linq;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A* pathfinding for road generation with 16-direction movement, slope-based cost, and river avoidance.
/// </summary>
public class RoadPathfinder
{
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;
    
    public const float CellSize = RoadConstants.PathfindingCellSize;
    public static int MaxIterations = RoadConstants.PathfindingMaxIterations;

    /// <summary>
    /// Price of steepness per step, times the step's grade squared. At 10 an
    /// 8 m step at 0.33 cost about 1.1 over its 8 for length, so a straight
    /// climb up the fall line was nearly as cheap as flat ground and a
    /// traverse was never worth its extra length: roads went straight up on
    /// dirt walls instead of traversing the slope and switching back.
    /// </summary>
    public float SlopeMultiplier = DefaultSlopeMultiplier;
    /// <summary>What a new pathfinder prices slope at. Settable for tests.</summary>
    internal static float DefaultSlopeMultiplier = RoadConstants.DefaultSlopeMultiplier;
    public float RiverPenalty = RoadConstants.DefaultRiverPenalty;
    public float WaterPenalty = RoadConstants.DefaultWaterPenalty;
    public float SteepSlopePenalty = RoadConstants.DefaultSteepSlopePenalty;
    public float SteepSlopeThreshold = RoadConstants.DefaultSteepSlopeThreshold;
    public float TerrainVariancePenalty = RoadConstants.DefaultTerrainVariancePenalty;
    public float TerrainVarianceThreshold = RoadConstants.DefaultTerrainVarianceThreshold;
    public float BaseCost = RoadConstants.DefaultBaseCost;
    public float SwampShallowWaterPenalty = RoadConstants.DefaultSwampShallowWaterPenalty;

    /// <summary>
    /// Whether a road may jump a knee-deep river, and wade swamp shallows.
    /// On: a road that meets a river it can ford does so rather than going
    /// around. Held per instance so a test can pathfind without it.
    /// </summary>
    public bool Fords = true;

    /// <summary>
    /// Whether a road may jump a river too long or too deep to ford, on a
    /// bridge. The cost levers are "Bridges/CostFixed" and
    /// "Bridges/CostPerMeter"; how dear a bridge is decides how often one
    /// appears, which is the lever a player actually wants.
    /// </summary>
    public bool Bridges = true;

    public static float ConfiguredBridgeCostFixed = RoadConstants.DefaultBridgeCostFixed;
    public static float ConfiguredBridgeCostPerMeter = RoadConstants.DefaultBridgeCostPerMeter;

    public float BridgeCostFixed = ConfiguredBridgeCostFixed;
    public float BridgeCostPerMeter = ConfiguredBridgeCostPerMeter;

    /// <summary>Ground a crossing may land on: the shallow-water line plus the bank clearance.</summary>
    public const float LandingFloor = RoadConstants.ShallowWaterHeight + RoadConstants.BankClearance;

    /// <summary>The floor for road ground in a biome: swamps wade down to DeepWaterHeight.</summary>
    public static float FloorFor(Heightmap.Biome biome) =>
        biome == Heightmap.Biome.Swamp ? RoadConstants.DeepWaterHeight : LandingFloor;

    /// <summary>
    /// The steepest ground a road may climb, as rise over run; 0 for no cap.
    /// A step over it is refused outright rather than priced, so the search
    /// has to cross the slope - which the 16 directions already allow - or
    /// fail and leave the destination unconnected. Every other steep case
    /// here is a large but finite price and so is never a refusal, which is
    /// why a destination on a cliff got the direct climb: it was the cheapest
    /// of the expensive lines. Held per instance so a test can pathfind
    /// without it. The lever is "Roads/MaxGrade".
    /// </summary>
    public float MaxGrade = RoadGrade.Configured;

    /// <summary>
    /// A step from existing road onto existing road costs this share of its
    /// price, so a later road merges onto a road already built and branches
    /// off where it must, instead of running beside it: a place-to-place
    /// search had no reason to prefer the road over the ground beside it, and
    /// with the meander and the sway, road with another 3.5-10 m beside it on
    /// the same heading grew by half. The search heuristic still assumes full
    /// price, so it may settle for a slightly dearer route.
    /// </summary>
    public float ReuseFactor = DefaultReuseFactor;
    /// <summary>Settable for tests; 1 prices road already built like new ground.</summary>
    internal static float DefaultReuseFactor = 0.5f;
    /// <summary>Returned by GetMoveCost for a step no road may take, as
    /// against the water and river penalties, which say only that a step is
    /// dear. Nothing may buy its way past this one.</summary>
    public const float Impassable = float.PositiveInfinity;

    private static readonly Vector2Int[] Directions = new Vector2Int[]
    {
        new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1),
        new Vector2Int(1, 1), new Vector2Int(-1, 1), new Vector2Int(1, -1), new Vector2Int(-1, -1),
        new Vector2Int(2, 1), new Vector2Int(2, -1), new Vector2Int(-2, 1), new Vector2Int(-2, -1),
        new Vector2Int(1, 2), new Vector2Int(-1, 2), new Vector2Int(1, -2), new Vector2Int(-1, -2),
    };

    private static readonly float[] DirectionCosts;

    static RoadPathfinder()
    {
        DirectionCosts = new float[Directions.Length];
        for (int i = 0; i < Directions.Length; i++)
            DirectionCosts[i] = Mathf.Sqrt(Directions[i].x * Directions[i].x + Directions[i].y * Directions[i].y);
    }

    private WorldGenerator m_worldGen;
    private readonly RoadTerrainSamples m_terrain;
    private Vector2? m_searchStart, m_searchEnd;
    public float SiteClearance = 4f;

    /// <summary>Roughness about a quadratic surface; see RoadTerrainSamples.QuadVariance.</summary>
    internal bool QuadVariance { get => m_terrain.QuadVariance; set => m_terrain.QuadVariance = value; }

    public RoadPathfinder(WorldGenerator worldGen)
    {
        m_worldGen = worldGen;
        m_terrain = new RoadTerrainSamples(worldGen);
    }

    /// <summary>Hand this pathfinder's terrain-memo counters to the
    /// generation totals and clear them. Called when the pathfinder is
    /// finished with, so a discarded one does not take its numbers with it.</summary>
    internal void FoldTerrainMemoCounters() => m_terrain.FoldIntoTotals();

    public float LastPathCost { get; private set; }

    /// <summary>Set by FindPathToNetwork when the start was already within
    /// reach of a road, so a null result can be told apart from a failure.</summary>
    public bool AlreadyOnNetwork { get; private set; }

    public List<Vector2>? FindPath(Vector2 start, Vector2 end) => FindPath(start, end, null);

    public List<Vector2>? FindPath(Vector2 start, Vector2 end, int? iterationLimit)
    {
        // The memo is NOT blanked here. It holds world-generator facts, and
        // the generator a pathfinder was built for does not change underneath
        // it: a pathfinder is built at the start of a generation and thrown
        // away at the end of it. Blanking between searches threw away answers
        // that were still true and bought nothing.
        m_searchStart = start;
        m_searchEnd = end;

        LastPathCost = 0;
        Vector2i startGrid = WorldToGrid(start);
        Vector2i endGrid = WorldToGrid(end);

        if (startGrid == endGrid)
            return new List<Vector2> { start, end };

        SortedSet<(float priority, Vector2i pos)> openSet = new SortedSet<(float, Vector2i)>(
            Comparer<(float priority, Vector2i pos)>.Create((a, b) =>
            {
                int cmp = a.priority.CompareTo(b.priority);
                if (cmp != 0) return cmp;
                cmp = a.pos.x.CompareTo(b.pos.x);
                if (cmp != 0) return cmp;
                return a.pos.y.CompareTo(b.pos.y);
            }));

        Dictionary<Vector2i, float> gCosts = new Dictionary<Vector2i, float>(RoadGridComparer.Instance);
        Dictionary<Vector2i, Vector2i> cameFrom = new Dictionary<Vector2i, Vector2i>(RoadGridComparer.Instance);
        HashSet<Vector2i> closedSet = new HashSet<Vector2i>(RoadGridComparer.Instance);

        openSet.Add((Heuristic(startGrid, endGrid), startGrid));
        gCosts[startGrid] = 0;

        int iterations = 0;

        while (openSet.Count > 0 && iterations < (iterationLimit ?? MaxIterations))
        {
            iterations++;

            var current = openSet.Min;
            openSet.Remove(current);
            Vector2i currentPos = current.pos;

            if (currentPos == endGrid)
            {
                LastPathCost = gCosts[currentPos];
                return ReconstructPath(cameFrom, currentPos, start, end);
            }

            closedSet.Add(currentPos);

            for (int i = 0; i < Directions.Length; i++)
            {
                Vector2i neighborPos = new Vector2i(currentPos.x + Directions[i].x, currentPos.y + Directions[i].y);

                if (closedSet.Contains(neighborPos))
                    continue;

                float moveCost = GetMoveCost(currentPos, neighborPos, i);
                if (!float.IsPositiveInfinity(moveCost) && moveCost < RiverPenalty)
                    moveCost += TurnCost(cameFrom, currentPos, i);
                // Refused outright, not merely dear: kept apart from the price
                // test below because the two mean different things to a reader
                // and, once there are crossings, to the search.
                if (float.IsPositiveInfinity(moveCost))
                    continue;
                if (moveCost >= RiverPenalty)
                {
                    // A blocked neighbour may be the near edge of a river:
                    // with fords on, look for dry ground on the far side
                    // and take the whole crossing as one move.
                    if (!(Fords || Bridges) || !TryGetRiverCrossing(currentPos, Directions[i], out Vector2i landing, out float crossingCost))
                        continue;
                    if (closedSet.Contains(landing))
                        continue;
                    neighborPos = landing;
                    moveCost = crossingCost;
                }

                float tentativeG = gCosts[currentPos] + moveCost;

                if (!gCosts.TryGetValue(neighborPos, out float existingG) || tentativeG < existingG)
                {
                    cameFrom[neighborPos] = currentPos;
                    gCosts[neighborPos] = tentativeG;
                    float h = Heuristic(neighborPos, endGrid);
                    openSet.Remove((existingG + h, neighborPos));
                    openSet.Add((tentativeG + h, neighborPos));
                }
            }
        }

        string reason = openSet.Count == 0 ? "no reachable path" : "max iterations reached";
        // Not a fault. A probe declining an expensive pair, or an island with
        // water in the way, is how screening works; roughly one destination in
        // five is unreachable on a normal world. Warning-level here meant a
        // clean generation printed hundreds of warnings, which teaches an
        // operator to ignore them. The totals are in the closing summary.
        Log.LogDebug($"Pathfinding failed: {reason} after {iterations} iterations");
        return null;
    }

    /// <summary>Search from a destination towards any existing road. Sampled hints
    /// guide the search; they do not promise a globally cheapest junction.</summary>
    public List<Vector2>? FindPathToNetwork(Vector2 start, float reach, IReadOnlyList<Vector2>? hints = null)
    {
        LastPathCost = 0;
        // Kept between searches, for the reason given in FindPath.
        m_searchStart = start;
        m_searchEnd = null;
        AlreadyOnNetwork = false;
        Vector2i startGrid = WorldToGrid(start);

        float NetworkHeuristic(Vector2i cell)
        {
            if (hints == null || hints.Count == 0) return 0f;
            Vector2 world = GridToWorld(cell);
            float best = float.MaxValue;
            for (int i = 0; i < hints.Count; i++)
            {
                float dx = hints[i].x - world.x, dy = hints[i].y - world.y;
                float d = dx * dx + dy * dy;
                if (d < best) best = d;
            }
            return Mathf.Max(0f, Mathf.Sqrt(best) - reach);
        }

        bool AtNetwork(Vector2i cell) => RoadSpatialGrid.TryGetRoadWithin(GridToWorld(cell), reach, out _);

        if (AtNetwork(startGrid))
        {
            // Already within reach. This is not a failure, and returning null
            // for it made the caller build a road to a place the network
            // already passes within a cell of: the caller's own check uses the
            // exact point, this one uses the cell centre, up to 5.66 m away.
            LastPathCost = 0f;
            AlreadyOnNetwork = true;
            return null;
        }

        SortedSet<(float priority, Vector2i pos)> openSet = new SortedSet<(float, Vector2i)>(
            Comparer<(float priority, Vector2i pos)>.Create((a, b) =>
            {
                int cmp = a.priority.CompareTo(b.priority);
                if (cmp != 0) return cmp;
                cmp = a.pos.x.CompareTo(b.pos.x);
                if (cmp != 0) return cmp;
                return a.pos.y.CompareTo(b.pos.y);
            }));
        Dictionary<Vector2i, float> gCosts = new(RoadGridComparer.Instance);
        Dictionary<Vector2i, Vector2i> cameFrom = new(RoadGridComparer.Instance);
        HashSet<Vector2i> closedSet = new(RoadGridComparer.Instance);

        openSet.Add((NetworkHeuristic(startGrid), startGrid));
        gCosts[startGrid] = 0f;
        int iterations = 0;

        while (openSet.Count > 0 && iterations < MaxIterations)
        {
            iterations++;
            var current = openSet.Min;
            openSet.Remove(current);
            Vector2i currentPos = current.pos;

            if (AtNetwork(currentPos))
            {
                LastPathCost = gCosts.TryGetValue(currentPos, out float cost) ? cost : 0f;
                RoadSpatialGrid.TryGetRoadWithin(GridToWorld(currentPos), reach, out Vector2 junction);
                return ReconstructPath(cameFrom, currentPos, start, junction);
            }

            closedSet.Add(currentPos);

            for (int i = 0; i < Directions.Length; i++)
            {
                Vector2i neighborPos = new Vector2i(currentPos.x + Directions[i].x, currentPos.y + Directions[i].y);
                if (closedSet.Contains(neighborPos))
                    continue;

                float moveCost = GetMoveCost(currentPos, neighborPos, i);
                if (!float.IsPositiveInfinity(moveCost) && moveCost < RiverPenalty)
                    moveCost += TurnCost(cameFrom, currentPos, i);
                if (moveCost >= RiverPenalty)
                {
                    if (!(Fords || Bridges))
                    {
                        continue;
                    }
                    if (!TryGetRiverCrossing(currentPos, Directions[i], out Vector2i landing, out float crossingCost))
                        continue;
                    if (closedSet.Contains(landing))
                    {
                        continue;
                    }
                    neighborPos = landing;
                    moveCost = crossingCost;
                }

                float tentativeG = gCosts[currentPos] + moveCost;
                if (!gCosts.TryGetValue(neighborPos, out float existingG) || tentativeG < existingG)
                {
                    cameFrom[neighborPos] = currentPos;
                    gCosts[neighborPos] = tentativeG;
                    float h = NetworkHeuristic(neighborPos);
                    openSet.Remove((existingG + h, neighborPos));
                    openSet.Add((tentativeG + h, neighborPos));
                }
            }
        }

        LastPathCost = 0f;
        return null;
    }

    private Vector2i WorldToGrid(Vector2 world)
    {
        return new Vector2i(Mathf.RoundToInt(world.x / CellSize), Mathf.RoundToInt(world.y / CellSize));
    }

    private Vector2 GridToWorld(Vector2i grid)
    {
        return new Vector2(grid.x * CellSize, grid.y * CellSize);
    }

    private float Heuristic(Vector2i from, Vector2i to)
    {
        float dx = (to.x - from.x) * CellSize;
        float dy = (to.y - from.y) * CellSize;
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// The sharpest single-step turn the search may make, in degrees against
    /// the step it arrived by; 0 is no limit.
    ///
    /// Traced with the steepness price raised: the search climbed by
    /// sawtoothing, back and
    /// forth on 11-18 m legs with 162 degree turns, because the price is per
    /// step and a weave of gentle steps undercuts one steep step. No road
    /// follows that, and no turn radius fits it. A real switchback turns its
    /// 180 degrees over several steps, each within the limit.
    /// </summary>
    public float TurnLimitDegrees = DefaultTurnLimit;
    /// <summary>Settable for tests; 0 is no limit.</summary>
    internal static float DefaultTurnLimit = 100f;

    /// <summary>
    /// Cells the road being built could not cross without cutting or filling
    /// past the game's terrain limit (see RoadNetworkGenerator.EarthworkCap):
    /// stepping into one costs <see cref="EarthworkPenalty"/>, so the next
    /// search goes round or takes another crossing. Cleared for every road.
    /// </summary>
    private readonly HashSet<Vector2i> m_earthworkPenalty = new(RoadGridComparer.Instance);
    public static float EarthworkPenalty = 1500f;
    internal void ClearEarthworkPenalty() => m_earthworkPenalty.Clear();
    internal void PenalizeAround(Vector2 world, float radius)
    {
        int r = Mathf.CeilToInt(radius / CellSize);
        var centre = WorldToGrid(world);
        for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                var cell = new Vector2i(centre.x + dx, centre.y + dz);
                if (Vector2.Distance(GridToWorld(cell), world) <= radius + CellSize * 0.5f) m_earthworkPenalty.Add(cell);
            }
    }
    internal int EarthworkPenaltyCells => m_earthworkPenalty.Count;
    internal int PenalizedWaypoints(List<Vector2> path) { int n = 0; foreach (var p in path) if (m_earthworkPenalty.Contains(WorldToGrid(p))) n++; return n; }

    /// <summary>
    /// On gentle ground, a smooth low-frequency cost field adds up to this
    /// share of the base step price, so long flat runs bend instead of running
    /// dead straight. It fades out by <see cref="MeanderFlat"/> so climbs,
    /// contouring and switchbacks are priced as before. It only adds, so the
    /// heuristic's BaseCost-per-metre bound still holds. 0 is off.
    /// </summary>
    public float Meander = DefaultMeander;
    /// <summary>Settable for tests; 0 is no meander.</summary>
    internal static float DefaultMeander = 3f;
    /// <summary>Slope at which the meander field has faded out. Validation switch MEANDER_FLAT.</summary>
    public static readonly float MeanderFlat = DebugSwitches.Number("MEANDER_FLAT", 0.12f, 0.02f, 1f);
    internal float MeanderCost(Vector2 from, Vector2 to, Vector2i toCell, float slope, float dist)
    {
        if (slope >= MeanderFlat || Meander <= 0f) return 0f;
        float fade = 1f - slope / MeanderFlat;
        var mid = (from + to) * 0.5f;
        return BaseCost * dist * Meander * fade * MeanderBiome(m_terrain.Biome(toCell)) * MeanderField(mid.x, mid.y);
    }

    /// <summary>How much a biome's roads wander: open country most, none where
    /// the ground already bends every road.</summary>
    internal static float MeanderBiome(Heightmap.Biome biome) => biome switch
    {
        Heightmap.Biome.Meadows => 1f,
        Heightmap.Biome.Plains => 1f,
        Heightmap.Biome.BlackForest => 0.7f,
        Heightmap.Biome.Swamp => 0.5f,
        Heightmap.Biome.AshLands => 0.5f,
        Heightmap.Biome.DeepNorth => 0.5f,
        _ => 0f,   // Mountain, Mistlands, Ocean
    };

    // Octaves of the meander field: wavelength (m) and weight. The lengths are
    // not multiples of each other and each octave is turned, so no period or
    // grid direction shows along a long road: a repeating period is noticed.
    private static readonly (float wave, float weight, float cos, float sin, float ox, float oz)[] MeanderOctaves =
    {
        (171f, 0.55f, 0.906f, 0.423f, 1013.7f, -2027.3f),
        (67f, 0.30f, -0.342f, 0.940f, -331.1f, 877.9f),
        (29f, 0.15f, 0.643f, -0.766f, 4561.3f, 12.7f),
    };

    /// <summary>The meander field at a world position, in [0,1].</summary>
    internal static float MeanderField(float worldX, float worldZ)
    {
        float sum = 0f;
        for (int i = 0; i < MeanderOctaves.Length; i++)
        {
            var o = MeanderOctaves[i];
            float weight = o.weight;
            float u = (o.cos * worldX - o.sin * worldZ + o.ox) / o.wave;
            float v = (o.sin * worldX + o.cos * worldZ + o.oz) / o.wave;
            sum += weight * ValueNoise(u, v);
        }
        return sum;
    }

    /// <summary>Smooth value noise in [0,1]: hashed lattice values, smoothstep
    /// between them. Managed and stateless, so the parallel island workers can
    /// share it (Unity's Perlin is a native call).</summary>
    internal static float ValueNoise(float x, float y)
    {
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
        float a = Lattice(x0, y0), b = Lattice(x0 + 1, y0), c = Lattice(x0, y0 + 1), d = Lattice(x0 + 1, y0 + 1);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
    }

    private static float Lattice(int x, int y)
    {
        unchecked
        {
            uint h = (uint)x * 374761393u + (uint)y * 668265263u;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0xFFFFFF;
        }
    }

    /// <summary>Price per radian turned between consecutive steps (Galin et
    /// al. 2010 price curvature for the same reason).</summary>
    public float TurnWeight = DefaultTurnWeight;
    /// <summary>Settable for tests; 0 is no turn price.</summary>
    internal static float DefaultTurnWeight = 4f;

    /// <summary>
    /// What turning into step <paramref name="direction"/> costs, given the
    /// step the search arrived at <paramref name="at"/> by. The search state is
    /// the cell alone, so this reads the arriving step from cameFrom: a cost
    /// that depends on history, which makes A* no longer exact (a cell reached
    /// by a worse-heading route first may keep it). Measured, not assumed.
    /// </summary>
    private float TurnCost(Dictionary<Vector2i, Vector2i> cameFrom, Vector2i at, int direction)
    {
        if (TurnLimitDegrees <= 0f && TurnWeight <= 0f) return 0f;
        if (!cameFrom.TryGetValue(at, out Vector2i from)) return 0f;
        Vector2 incoming = new Vector2(at.x - from.x, at.y - from.y);
        if (incoming.sqrMagnitude < 1e-6f) return 0f;
        Vector2 outgoing = new Vector2(Directions[direction].x, Directions[direction].y);
        float cos = Mathf.Clamp((incoming.x * outgoing.x + incoming.y * outgoing.y) /
                                (Mathf.Sqrt(incoming.sqrMagnitude) * Mathf.Sqrt(outgoing.sqrMagnitude)), -1f, 1f);
        float angle = (float)System.Math.Acos(cos);
        if (TurnLimitDegrees > 0f && angle * 57.29578f > TurnLimitDegrees + 0.01f) return Impassable;
        return TurnWeight * angle;
    }

    /// <summary>
    /// The narrowest gap, in metres, left between a place's footprint and the
    /// water for a road to pass through. A road forced along a strip of shore
    /// beside a place comes out strange: squeezed against the footprint on one
    /// side and the water on the other, tilted down the bank or run out on a
    /// causeway. Such ground is not a road at all, for any place, the road's
    /// own destination included; the search goes round, or the place goes
    /// unserved. 12 m is the site clearance (4 m to the road's centre), half a
    /// 4 m road and 6 m of bank. Settable for tests; 0 is off.
    /// </summary>
    internal static float SqueezeCorridor = 12f;

    /// <summary>Whether <paramref name="p"/> lies between a place's footprint
    /// and water less than <see cref="SqueezeCorridor"/> apart. Inside a
    /// footprint it is not: the search's own ends reach their centres there,
    /// and every other footprint is refused anyway.</summary>
    internal bool SiteShoreSqueeze(Vector2 p)
    {
        if (SqueezeCorridor <= 0f) return false;
        float gap = RoadSiteProtection.EdgeDistance(p, SqueezeCorridor);
        if (gap < 0f || gap >= SqueezeCorridor) return false;
        float reach = SqueezeCorridor - gap;
        for (int i = 0; i < 8; i++)
        {
            float a = i * Mathf.PI / 4f;
            var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            for (float r = reach * 0.5f; r <= reach + 0.01f; r += reach * 0.5f)
                if (Wet(p + dir * r)) return true;
        }
        return false;
    }

    /// <summary>Whether a crossing's line runs over water within <see cref="SqueezeCorridor"/> of a
    /// place's footprint (sampled every 4 m, 8 m clear of each bank).</summary>
    internal bool DeckSqueezed(Vector2 from, Vector2 to, out Vector2 at)
    {
        at = default;
        if (SqueezeCorridor <= 0f) return false;
        float length = Vector2.Distance(from, to);
        int n = Mathf.Max(1, Mathf.CeilToInt(length / 4f));
        for (int i = 1; i < n; i++)
        {
            float along = length * i / n;
            // The banks are the landing test's; the deck test is for the water it runs over.
            if (along < 8f || length - along < 8f) continue;
            Vector2 p = Vector2.Lerp(from, to, (float)i / n);
            if (!Wet(p)) continue;
            float gap = RoadSiteProtection.EdgeDistance(p, SqueezeCorridor);
            if (gap >= 0f && gap < SqueezeCorridor) { at = p; return true; }
        }
        return false;
    }

    /// <summary>The squeeze, once per cell for this pathfinder's life: the
    /// places and the ground do not change during a generation, and the test
    /// samples the world sixteen times.</summary>
    private readonly Dictionary<Vector2i, bool> m_squeezed = new(RoadGridComparer.Instance);
    private bool SqueezedCell(Vector2i cell)
    {
        if (SqueezeCorridor <= 0f) return false;
        if (!m_squeezed.TryGetValue(cell, out bool squeezed))
            m_squeezed[cell] = squeezed = SiteShoreSqueeze(GridToWorld(cell));
        return squeezed;
    }

    /// <summary>Where the squeeze refused something this generation, by the
    /// place it squeezed against: (centre x, centre z) -> steps, landings.</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<(int x, int z), (int steps, int landings)> SqueezeHits = new();
    private static void RecordSqueeze(Vector2 p, bool landing)
    {
        if (!RoadSiteProtection.NearestFootprint(p, SqueezeCorridor, out var f)) return;
        SqueezeHits.AddOrUpdate((Mathf.RoundToInt(f.Centre.x), Mathf.RoundToInt(f.Centre.y)),
            landing ? (0, 1) : (1, 0), (_, n) => landing ? (n.steps, n.landings + 1) : (n.steps + 1, n.landings));
    }

    /// <summary>Water the search would not walk: a river, or ground under the
    /// shallows (swamp shallows are waded, so there the deep line).</summary>
    private bool Wet(Vector2 p)
    {
        m_worldGen.GetRiverWeight(p.x, p.y, out float river, out _);
        if (river > RoadConstants.RiverImpassableThreshold) return true;
        float h = m_worldGen.GetHeight(p.x, p.y);
        return h < (m_worldGen.GetBiome(p.x, p.y) == Heightmap.Biome.Swamp ? RoadConstants.DeepWaterHeight : RoadConstants.ShallowWaterHeight);
    }

    private float GetMoveCost(Vector2i from, Vector2i to, int directionIndex)
    {
        Vector2 fromWorld = GridToWorld(from);
        Vector2 toWorld = GridToWorld(to);
        if (RoadSiteProtection.BlocksSegment(fromWorld, toWorld, SiteClearance, m_searchStart, m_searchEnd))
            return Impassable;

        float dist = DirectionCosts[directionIndex] * CellSize;
        float h1 = m_terrain.Height(from);
        float h2 = m_terrain.Height(to);
        float slope = Mathf.Abs(h2 - h1) / dist;

        float riverWeight = m_terrain.River(to);
        if (riverWeight > RoadConstants.RiverImpassableThreshold)
            return RiverPenalty;

        float biomeHeight = h2;
        if (biomeHeight < RoadConstants.DeepWaterHeight)
            return WaterPenalty * 2f;
        // With fords on, swamp shallows are waded at a price.
        float wadeCost = 0f;
        if (biomeHeight < RoadConstants.ShallowWaterHeight)
        {
            if (!Fords || m_worldGen.GetBiome(toWorld.x, toWorld.y) != Heightmap.Biome.Swamp)
                return WaterPenalty;
            wadeCost = SwampShallowWaterPenalty;
        }

        if (SqueezedCell(to))
        {
            RecordSqueeze(GridToWorld(to), landing: false);
            return Impassable;
        }

        // A grade cap is a refusal, not a price: above it there is no road, so
        // the search crosses the slope or gives the destination up. The slope
        // terms below still price everything under the cap.
        // The margin is the search's, not the road's: RoadGrade.Limit still
        // holds the finished profile to MaxGrade, so a route admitted here can
        // still be refused when its profile is built. A cap switched off
        // (MaxGrade <= 0) stays off - the margin never becomes a cap.
        if (MaxGrade > 0f && slope > MaxGrade + RoadConstants.SearchGradeMargin)
            return Impassable;

        if (slope > SteepSlopeThreshold)
            return SteepSlopePenalty;

        if (TerrainVariancePenalty > 0f && m_terrain.Variance(to) > TerrainVarianceThreshold)
            return TerrainVariancePenalty;

        Heightmap.Biome biome = m_terrain.Biome(to);
        if (biome == Heightmap.Biome.Mountain && slope > RoadConstants.MountainSlopeThreshold)
            return WaterPenalty;

        float riverCost = riverWeight > 0 ? WaterPenalty * riverWeight : 0f;
        bool reuse = ReuseFactor < 1f && OnExistingRoad(toWorld) && OnExistingRoad(fromWorld);
        float cost = BaseCost * dist + (slope * slope * SlopeMultiplier) + riverCost + wadeCost
             + RoadPitches.OverSustainedPrice(slope, dist)
             + (reuse ? 0f : MeanderCost(fromWorld, toWorld, to, slope, dist));
        if (reuse) cost *= ReuseFactor;
        return cost + (m_earthworkPenalty.Count > 0 && m_earthworkPenalty.Contains(to) ? EarthworkPenalty : 0f);
    }

    /// <summary>
    /// Scans cell by cell from a dry cell across river water in one of the
    /// eight principal directions and lands on the first dry ground outside
    /// the river band. A jump within the ford cap over water no deeper than
    /// wading is a FORD at a small cost (with fords on); anything longer or
    /// deeper is a BRIDGE at the bridge cost (with bridges on), held to
    /// near-level banks. Water without a
    /// river core under it (a lake, the sea) is not crossed; neither is a dry
    /// river valley.
    /// </summary>
    private bool TryGetRiverCrossing(Vector2i from, Vector2Int direction, out Vector2i landing, out float crossingCost)
    {
        landing = from;
        crossingCost = 0f;

        // The scan walks whole cells, so a knight move would skip cells it
        // never checked and could start the crossing one cell short of the bank.
        if (Mathf.Abs(direction.x) > 1 || Mathf.Abs(direction.y) > 1)
            return false;

        Vector2 fromWorld = GridToWorld(from);

        float fromHeight = m_worldGen.GetHeight(fromWorld.x, fromWorld.y);
        bool sawRiverWater = false;
        float deepest = float.MaxValue;
        int maxCells = Bridges ? RoadConstants.MaxBridgeCrossingCells : RoadConstants.MaxRiverCrossingCells;

        for (int step = 1; step <= maxCells; step++)
        {
            Vector2i check = new Vector2i(from.x + direction.x * step, from.y + direction.y * step);
            Vector2 world = GridToWorld(check);
            float height = m_worldGen.GetHeight(world.x, world.y);
            m_worldGen.GetRiverWeight(world.x, world.y, out float riverWeight, out _);
            bool water = height < LandingFloor;
            bool riverCore = riverWeight > RoadConstants.RiverImpassableThreshold;

            // Keep scanning over water and over the river band (its dry
            // shores included: a road cannot stand there either).
            if (water || riverCore)
            {
                sawRiverWater |= water && riverCore;
                deepest = Mathf.Min(deepest, height);
                continue;
            }

            // Dry ground: the far bank, if a river lay between.
            if (!sawRiverWater)
                return false;

            bool bridgesAllowed = Bridges;
            Vector2 fromBank = fromWorld;
            Vector2 toBank = world;
            // In a swamp the detector walks a wet bank out over the wade shelf
            // until it finds dry ground, which can add a great deal of deck.
            // Do it HERE, before the span is measured against the cap and
            // before the crossing is priced, so the search judges the bridge it
            // will actually get. Deciding afterwards would leave a route
            // accepted at one length and built at another.
            if (bridgesAllowed && m_worldGen.GetBiome(
                    (fromWorld.x + world.x) * 0.5f, (fromWorld.y + world.y) * 0.5f) == Heightmap.Biome.Swamp)
                (fromBank, toBank) = RoadCrossingDetector.ExtendOverSwampShelf(fromWorld, world, m_worldGen);

            float distance = Vector2.Distance(fromBank, toBank);
            // The cells are 8 m apart and a channel can hide between them:
            // the whole jump is sampled every 2 m before its depth is trusted.
            deepest = Mathf.Min(deepest, DeepestAlong(fromWorld, world));
            bool bridge = distance > RoadConstants.MaxRiverCrossingCells * CellSize
                || deepest < RoadConstants.SeaLevel - RoadConstants.FordWadeDepth;
            if (bridge ? !Bridges : !Fords)
                return false;
            if (distance > maxCells * CellSize)
                return false;

            float bankDelta = Mathf.Abs(
                BiomeBlendedHeight.GetBlendedHeight(toBank.x, toBank.y, m_worldGen)
                - BiomeBlendedHeight.GetBlendedHeight(fromBank.x, fromBank.y, m_worldGen));
            if (bankDelta > (bridge ? RoadConstants.MaxBridgeBankDelta : RoadConstants.MaxFordBankDelta))
                return false;

            // A landing squeezed between a place and the water is not a road
            // either: the road off the bridge would run along the shore there.
            if (SqueezedCell(check))
            {
                RecordSqueeze(GridToWorld(check), landing: true);
                return false;
            }
            // Nor is a deck that runs along the shore past a place: a 104 m bridge was measured
            // flying past a troll cave 7 m from its edge, over the very strip the squeeze
            // keeps a road out of, because only steps and landings were tested. Counted with the
            // landings.
            if (DeckSqueezed(fromBank, toBank, out Vector2 at))
            {
                RecordSqueeze(at, landing: true);
                return false;
            }

            landing = check;
            // Both ends already on road (a finished road's banks): the
            // crossing exists, so using it costs a fraction of its price,
            // and a later road detours to share it rather than build
            // another beside it, up to what that saving buys.
            bool shared = OnExistingRoad(fromWorld) && OnExistingRoad(world);
            float price = bridge ? BridgeCostFixed + BridgeCostPerMeter * distance : RoadConstants.RiverCrossingPenalty;
            if (shared)
                price *= RoadConstants.SharedCrossingCostFraction;
            crossingCost = price + BaseCost * distance
                + RoadConstants.BankDeltaPenalty * bankDelta * bankDelta;
            return true;
        }

        return false;
    }

    /// <summary>The lowest ground on the line between two points, sampled
    /// every 2 m at the biome-blended height the game renders (a biome edge
    /// can move the ground metres from the raw generator height), so the
    /// pathfinder and the crossing detector judge the same water.</summary>
    private float DeepestAlong(Vector2 a, Vector2 b)
    {
        float length = Vector2.Distance(a, b);
        int samples = Mathf.Max(1, Mathf.CeilToInt(length / 2f));
        float deepest = float.MaxValue;
        for (int i = 0; i <= samples; i++)
        {
            float t = (float)i / samples;
            deepest = Mathf.Min(deepest, BiomeBlendedHeight.GetBlendedHeight(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, m_worldGen));
        }
        return deepest;
    }

    /// <summary>Whether a finished road already runs through this point.</summary>
    private static bool OnExistingRoad(Vector2 world)
    {
        if (!RoadSpatialGrid.IsInitialized)
            return false;
        RoadSpatialGrid.GetRoadWeight(world.x, world.y, out float weight, out _);
        return weight > 0f;
    }

    private List<Vector2> ReconstructPath(Dictionary<Vector2i, Vector2i> cameFrom, Vector2i current, Vector2 start, Vector2 end)
    {
        List<Vector2> path = new List<Vector2> { end };

        while (cameFrom.ContainsKey(current))
        {
            path.Add(GridToWorld(current));
            current = cameFrom[current];
        }

        path.Add(start);
        path.Reverse();
        return path;
    }
}
