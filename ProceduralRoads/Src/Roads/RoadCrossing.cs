using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Bridge: long, deep or sailable water, pieces span it. Ford:
/// knee-deep, unsailable water the road goes through in one of the
/// <see cref="FordStyle"/> ways.</summary>
public enum CrossingKind { Ford, Bridge } // Ford first: the value a fords-only save stores

/// <summary>How a ford treats the shallows: WADE paints the ground and
/// leaves it at its height (the road goes through the water), RAISE levels
/// the road up through the shallows, SPAN builds a short low footbridge
/// with a step at each end. Chosen per site for variety.</summary>
public enum FordStyle { None, Wade, Raise, Span }

/// <summary>
/// One river crossing on a generated road (fords and bridges prototypes):
/// where the road leaves each bank, the river profile between the banks,
/// and the fairway, the deepest contiguous stretch, which a bridge leaves
/// open so boats can still sail through.
/// </summary>
public sealed class RoadCrossing
{
    public CrossingKind Kind = CrossingKind.Ford;
    public FordStyle Style = FordStyle.None;

    /// <summary>Path vertices bracketing the water: the crossing is the path
    /// segment [FromIndex, ToIndex]. Not persisted; only the generation pass
    /// that paints the crossing needs them.</summary>
    public int FromIndex;
    public int ToIndex;

    /// <summary>Last road ground on each side: where the crossing starts and ends.</summary>
    public Vector2 FromBank;
    public Vector2 ToBank;

    /// <summary>Midpoint between the banks.</summary>
    public Vector2 Center;

    /// <summary>Normalized FromBank -> ToBank.</summary>
    public Vector2 Direction;

    /// <summary>Bank-to-bank distance in metres.</summary>
    public float Width;

    public float WaterLevel = RoadConstants.SeaLevel;

    /// <summary>Lowest terrain height between the banks.</summary>
    public float RiverbedHeight;

    /// <summary>Centre of the deepest contiguous stretch (depth at least
    /// <see cref="RoadCrossingDetector.FairwayMinDepth"/>), or the single
    /// deepest sample when no stretch qualifies.</summary>
    public Vector2 FairwayCenter;

    /// <summary>Length of that stretch in metres; 0 when the river is too shallow to sail.</summary>
    public float FairwayWidth;

    /// <summary>Two routes over the same jump each record a crossing; a
    /// crossing whose banks both lie within this distance of another's (in
    /// either order) is the same site.</summary>
    public const float SharedBankRadius = 4f;

    public static bool SameBanks(RoadCrossing a, RoadCrossing b) => SameBanks(a, b, SharedBankRadius);

    public static bool SameBanks(RoadCrossing a, RoadCrossing b, float radius)
    {
        float r2 = radius * radius;
        return ((a.FromBank - b.FromBank).sqrMagnitude <= r2 && (a.ToBank - b.ToBank).sqrMagnitude <= r2)
            || ((a.FromBank - b.ToBank).sqrMagnitude <= r2 && (a.ToBank - b.FromBank).sqrMagnitude <= r2);
    }

    /// <summary>
    /// Makes this crossing the same site as <paramref name="existing"/>: its
    /// banks (in this crossing's own order), profile, kind and style. The
    /// road that recorded this crossing is then painted up to the shared
    /// banks, so it meets the one bridge built there.
    /// </summary>
    public void SnapTo(RoadCrossing existing)
    {
        bool sameOrder = (FromBank - existing.FromBank).sqrMagnitude <= (FromBank - existing.ToBank).sqrMagnitude;
        FromBank = sameOrder ? existing.FromBank : existing.ToBank;
        ToBank = sameOrder ? existing.ToBank : existing.FromBank;
        Center = existing.Center;
        Direction = ToBank - FromBank;
        Direction.Normalize();
        Width = existing.Width;
        RiverbedHeight = existing.RiverbedHeight;
        FairwayCenter = existing.FairwayCenter;
        FairwayWidth = existing.FairwayWidth;
        Kind = existing.Kind;
        Style = existing.Style;
    }

    /// <summary>A crossing between two banks with its derived fields filled in.</summary>
    public static RoadCrossing Between(Vector2 fromBank, Vector2 toBank, float riverbedHeight,
        Vector2 fairwayCenter, float fairwayWidth, CrossingKind kind = CrossingKind.Ford, FordStyle style = FordStyle.None)
    {
        Vector2 direction = toBank - fromBank;
        direction.Normalize();
        return new RoadCrossing
        {
            Kind = kind,
            Style = style,
            FromBank = fromBank,
            ToBank = toBank,
            Center = (fromBank + toBank) * 0.5f,
            Direction = direction,
            Width = Vector2.Distance(fromBank, toBank),
            RiverbedHeight = riverbedHeight,
            FairwayCenter = fairwayCenter,
            FairwayWidth = fairwayWidth,
        };
    }

    /// <summary>Signed distance of a point along the crossing line, from FromBank toward ToBank.</summary>
    public float Along(Vector2 p) => Vector2.Dot(p - FromBank, Direction);
}

/// <summary>
/// Finds the river crossings on a finished path. With fords or bridges on,
/// the pathfinder jumps a river in one straight segment from dry cell to
/// dry cell; this walks each such segment to the water's edge on both
/// sides, profiles the river between them and decides whether it is a ford
/// (and in which style) or, with bridges on, a bridge. Pure logic: the same
/// code runs in the game and in the headless test harness.
/// </summary>
public static class RoadCrossingDetector
{
    /// <summary>Minimum water depth that counts as sailable fairway.</summary>
    public const float FairwayMinDepth = 1.2f;

    private const float SampleSpacing = 2f;
    private const float ShoreStep = 0.5f;

    /// <summary>Player-facing levers (config "Fords/WadeWeight", "RaiseWeight",
    /// "SpanWeight"): relative odds of each ford style among the styles a
    /// site allows. 0 removes a style; when every allowed style is 0 the site
    /// raises the road (always allowed). Set at config read.</summary>
    public static float ConfiguredWadeWeight = RoadConstants.DefaultFordStyleWeight;
    public static float ConfiguredRaiseWeight = RoadConstants.DefaultFordStyleWeight;
    public static float ConfiguredSpanWeight = RoadConstants.DefaultFordStyleWeight;

    public static void SetFordStyleWeights(float wade, float raise) => SetFordStyleWeights(wade, raise, ConfiguredSpanWeight);

    public static void SetFordStyleWeights(float wade, float raise, float span)
    {
        ConfiguredWadeWeight = Mathf.Max(0f, wade);
        ConfiguredRaiseWeight = Mathf.Max(0f, raise);
        ConfiguredSpanWeight = Mathf.Max(0f, span);
    }

    /// <summary>The crossings on a path, classified as the pathfinder
    /// accepted them: with <paramref name="bridges"/> a jump longer than a
    /// ford, or over water too deep or sailable for one, is a bridge and a
    /// ford may be spanned; without <paramref name="fords"/> every crossing
    /// is a bridge; without bridges every crossing is a ford.</summary>
    public static List<RoadCrossing> Detect(List<Vector2> path, WorldGenerator world, bool bridges = false, bool fords = true)
    {
        List<RoadCrossing> crossings = new();
        if (path == null || path.Count < 2 || world == null)
            return crossings;

        for (int i = 1; i < path.Count; i++)
        {
            if (!SegmentCrossesRiver(path[i - 1], path[i], world))
                continue;
            RoadCrossing? crossing = Build(path, i - 1, i, world, bridges, fords);
            if (crossing != null)
                crossings.Add(crossing);
        }
        return crossings;
    }

    /// <summary>
    /// Ground a road may stand on, so also where a crossing ends. Outside
    /// swamps: the shallow-water line plus the bank clearance, the same
    /// ground a ford jump may land on. In swamps the road wades down to
    /// DeepWaterHeight, so everything shallower is road, not crossing.
    /// </summary>
    public static bool IsRoadGround(Vector2 p, WorldGenerator world) =>
        BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world) >= RoadPathfinder.FloorFor(world.GetBiome(p.x, p.y));

    /// <summary>A segment with river water under it somewhere: the jump the
    /// pathfinder made. A splined road dipping into a puddle between two dry
    /// cells is not a crossing.</summary>
    private static bool SegmentCrossesRiver(Vector2 a, Vector2 b, WorldGenerator world)
    {
        float length = Vector2.Distance(a, b);
        int samples = Mathf.Max(1, Mathf.CeilToInt(length / SampleSpacing));
        for (int s = 0; s <= samples; s++)
        {
            float t = (float)s / samples;
            float x = a.x + (b.x - a.x) * t;
            float y = a.y + (b.y - a.y) * t;
            world.GetRiverWeight(x, y, out float weight, out _);
            if (weight > RoadConstants.RiverImpassableThreshold
                && BiomeBlendedHeight.GetBlendedHeight(x, y, world) < RoadConstants.SeaLevel)
                return true;
        }
        return false;
    }

    private static RoadCrossing? Build(List<Vector2> path, int fromIndex, int toIndex, WorldGenerator world, bool bridges, bool fords)
    {
        Vector2 a = path[fromIndex], b = path[toIndex];
        float jumpLength = Vector2.Distance(a, b);
        // The crossing spans the water, not the dry approaches: each bank is
        // the last road ground along the jump before the water, so the road
        // runs down to the water's edge and the crossing lies on the road.
        Vector2 from = Shore(a, b, world);
        Vector2 to = Shore(b, a, world);

        // High bridge: when the road climbs a cliff on both sides of the
        // water, the deck springs from the bank tops instead of the water's
        // edge. Both banks move together, and only when the tops are level
        // enough for one deck.
        //
        // This placement is OPTIONAL -- the crossing is sound either way -- and
        // it lengthens the deck, so it is taken only while the result still fits
        // the span the search is allowed to accept. Springing from the tops of a
        // gorge whose rims stand back from the water can add metres at each end:
        // a 128 m jump came back as a 136.05 m bridge, past a cap the router had
        // already checked and priced against. When the tops do not fit, the
        // water's-edge banks stand, and those are the ones routing measured.
        if (bridges)
        {
            float fromH = BiomeBlendedHeight.GetBlendedHeight(from.x, from.y, world);
            float toH = BiomeBlendedHeight.GetBlendedHeight(to.x, to.y, world);
            (Vector2 topFrom, int topFromIndex, float topFromH) = BankTop(path, from, fromIndex, -1, world);
            (Vector2 topTo, int topToIndex, float topToH) = BankTop(path, to, toIndex, +1, world);
            float cap = RoadConstants.MaxBridgeCrossingCells * RoadPathfinder.CellSize;
            if (topFromH >= fromH + RoadConstants.HighBankRise && topToH >= toH + RoadConstants.HighBankRise
                && Mathf.Abs(topFromH - topToH) <= RoadConstants.MaxBridgeBankDelta
                && Vector2.Distance(topFrom, topTo) <= cap)
            {
                from = topFrom;
                to = topTo;
                fromIndex = topFromIndex;
                toIndex = topToIndex;
            }
        }

        float width = Vector2.Distance(from, to);
        if (width < 1f)
            return null;

        Vector2 direction = to - from;
        direction.Normalize();
        (float riverbed, Vector2 fairwayCenter, float fairwayWidth) = Profile(from, to, world);

        // A crossing needs water under it; a dry river valley is ordinary road.
        if (riverbed >= RoadConstants.SeaLevel)
            return null;

        Vector2 center = (from + to) * 0.5f;
        bool swamp = world.GetBiome(center.x, center.y) == Heightmap.Biome.Swamp;

        // Knee-deep and unsailable: a FORD, in one of the styles the site
        // allows, chosen per site so roads vary: wading only where the water
        // is ankle deep (always in a swamp), raising always, a span only
        // where there is room for a deck. Swamps wade deeper: a swamp channel
        // whose bed stays at wading depth (what the pathfinder already wades)
        // is a ford in the same style mix, unless a stretch of it is sailable
        // for at least a boat's length, which keeps it a bridge. Without
        // bridges every crossing is a ford.
        // The class the pathfinder accepted is kept: a jump beyond the ford
        // cap was a bridge whatever its depth, and a feature that is off
        // never produces its kind.
        bool ford = jumpLength <= RoadConstants.MaxRiverCrossingCells * RoadPathfinder.CellSize + 0.5f && (swamp
            ? fairwayWidth < RoadConstants.SwampFordMaxFairway && riverbed >= RoadConstants.DeepWaterHeight
            : fairwayWidth <= 0f && riverbed >= RoadConstants.SeaLevel - RoadConstants.FordWadeDepth);
        if (!fords) ford = false;
        // Without bridges, water a ford may not cross is not a crossing: a
        // segment dipping into a deeper channel is left as it is today.
        if (!ford && !bridges)
            return null;
        FordStyle style = FordStyle.None;
        if (ford)
        {
            float depth = RoadConstants.SeaLevel - riverbed;
            List<FordStyle> eligible = new() { FordStyle.Raise };
            if (swamp || depth <= RoadConstants.FordWadeMaxDepth) eligible.Add(FordStyle.Wade);
            if (bridges && width >= RoadConstants.FordSpanMinWidth) eligible.Add(FordStyle.Span);
            style = PickFordStyle(eligible, SiteHash(center));
        }
        else if (swamp)
        {
            // The profile keeps its riverbed and fairway: the added deck is
            // over the shelf, which is shallower than both.
            (from, to) = ExtendOverSwampShelf(from, to, world);
        }

        RoadCrossing crossing = RoadCrossing.Between(from, to, riverbed, fairwayCenter, fairwayWidth,
            ford ? CrossingKind.Ford : CrossingKind.Bridge, style);
        crossing.FromIndex = fromIndex;
        crossing.ToIndex = toIndex;
        return crossing;
    }

    /// <summary>The river along the bank-to-bank line at about 1 m spacing:
    /// the lowest bed, and the longest sailable stretch.</summary>
    private static (float riverbed, Vector2 fairwayCenter, float fairwayWidth) Profile(Vector2 from, Vector2 to, WorldGenerator world)
    {
        float width = Vector2.Distance(from, to);
        int samples = Mathf.Max(2, Mathf.CeilToInt(width));
        float riverbed = float.MaxValue;
        Vector2 deepestPoint = from;
        int bestRunStart = -1, bestRunLength = 0, runStart = -1;
        for (int s = 0; s <= samples; s++)
        {
            float t = (float)s / samples;
            Vector2 p = new(from.x + (to.x - from.x) * t, from.y + (to.y - from.y) * t);
            float h = BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
            if (h < riverbed)
            {
                riverbed = h;
                deepestPoint = p;
            }

            bool sailable = h <= RoadConstants.SeaLevel - FairwayMinDepth;
            if (sailable && runStart < 0)
                runStart = s;
            if ((!sailable || s == samples) && runStart >= 0)
            {
                int runEnd = sailable ? s : s - 1;
                int runLength = runEnd - runStart + 1;
                if (runLength > bestRunLength)
                {
                    bestRunLength = runLength;
                    bestRunStart = runStart;
                }
                runStart = -1;
            }
        }

        Vector2 fairwayCenter = deepestPoint;
        float fairwayWidth = 0f;
        if (bestRunLength > 0)
        {
            float t = (bestRunStart + (bestRunLength - 1) / 2f) / samples;
            fairwayCenter = new Vector2(from.x + (to.x - from.x) * t, from.y + (to.y - from.y) * t);
            fairwayWidth = bestRunLength * (width / samples);
        }
        return (riverbed, fairwayCenter, fairwayWidth);
    }

    private static float WeightOf(FordStyle style) => style switch
    {
        FordStyle.Wade => ConfiguredWadeWeight,
        FordStyle.Raise => ConfiguredRaiseWeight,
        FordStyle.Span => ConfiguredSpanWeight,
        _ => 0f,
    };

    /// <summary>Weighted pick driven by the site hash, so a world regenerates
    /// with the same fords. Equal weights reduce to a plain modulo pick.</summary>
    internal static FordStyle PickFordStyle(List<FordStyle> eligible, int hash)
    {
        float total = 0f, first = -1f;
        bool equal = true;
        foreach (FordStyle s in eligible)
        {
            float w = Mathf.Max(0f, WeightOf(s));
            total += w;
            if (first < 0f) first = w;
            else if (Mathf.Abs(w - first) > 1e-6f) equal = false;
        }
        if (total <= 0f)
            return FordStyle.Raise;
        if (equal)
            return eligible[hash % eligible.Count];

        float r = (hash & 0xFFFF) / 65536f * total;
        foreach (FordStyle s in eligible)
        {
            float w = Mathf.Max(0f, WeightOf(s));
            if (w <= 0f) continue;
            if (r < w) return s;
            r -= w;
        }
        return FordStyle.Raise; // float tail
    }

    /// <summary>Deterministic per-site hash so a ford keeps its style across
    /// loads and worlds regenerate identically.</summary>
    private static int SiteHash(Vector2 center)
    {
        unchecked
        {
            int x = Mathf.RoundToInt(center.x), y = Mathf.RoundToInt(center.y);
            uint h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return (int)((h ^ (h >> 16)) & 0x7FFFFFFF);
        }
    }

    /// <summary>
    /// The water's edge walking from <paramref name="start"/> toward
    /// <paramref name="end"/>: the last point that stands on road ground
    /// before the ground turns wet and stays wet. A wet start is its own
    /// bank; a walk that never meets water keeps the start as its bank.
    /// </summary>
    private static Vector2 Shore(Vector2 start, Vector2 end, WorldGenerator world)
    {
        float length = Vector2.Distance(start, end);
        if (length < 0.01f || !IsRoadGround(start, world))
            return start;

        Vector2 dir = (end - start) * (1f / length);
        Vector2 last = start;
        for (float d = ShoreStep; d <= length; d += ShoreStep)
        {
            Vector2 p = start + dir * d;
            if (IsRoadGround(p, world))
            {
                last = p;
                continue;
            }
            // A 1-2 m pothole on the approach is not the shore: the ground
            // must still be wet 1 m and 2 m further on.
            bool wetAhead = !IsRoadGround(start + dir * Mathf.Min(length, d + 1f), world)
                && !IsRoadGround(start + dir * Mathf.Min(length, d + 2f), world);
            if (wetAhead)
                return last;
        }
        return start;
    }

    /// <summary>Walks straight out from a bank along <paramref name="outward"/>
    /// and returns the first point whose ground is at least <paramref name="floor"/>
    /// high, or the bank itself when none lies within reach.</summary>
    /// <summary>
    /// A BRIDGE starts and ends on land above the water, but in a swamp the
    /// wade shelf is road-legal, so the banks the search landed on can sit
    /// under the waterline. Walk each wet bank straight out along the crossing
    /// line until the ground clears the waterline, even though that makes the
    /// deck longer. The painted lead still reaches the abutment.
    ///
    /// The pathfinder calls this too, BEFORE it accepts the jump. It has to:
    /// moving a bank afterwards would change a span the search had already
    /// measured against its limit and already priced, and a 64 m jump can come
    /// out over 150 m long once a long shelf is walked. Both sides must see the
    /// same geometry, and the side that can still choose a different route is
    /// the one that has to see it first.
    /// </summary>
    public static (Vector2 from, Vector2 to) ExtendOverSwampShelf(Vector2 from, Vector2 to, WorldGenerator world)
    {
        Vector2 direction = (to - from).normalized;
        float dryFloor = RoadPathfinder.LandingFloor;
        if (BiomeBlendedHeight.GetBlendedHeight(from.x, from.y, world) < dryFloor)
            from = FirstDryAlongLine(from, -direction, RoadConstants.SwampBridgeDryReach, world, dryFloor);
        if (BiomeBlendedHeight.GetBlendedHeight(to.x, to.y, world) < dryFloor)
            to = FirstDryAlongLine(to, direction, RoadConstants.SwampBridgeDryReach, world, dryFloor);
        return (from, to);
    }

    private static Vector2 FirstDryAlongLine(Vector2 bank, Vector2 outward, float reach, WorldGenerator world, float floor)
    {
        for (float d = 0.5f; d <= reach; d += 0.5f)
        {
            Vector2 p = bank + outward * d;
            if (BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world) >= floor)
                return p;
        }
        return bank;
    }

    /// <summary>
    /// The highest ground within HighBankReach of a bank, walking outward
    /// from it along the path (step -1 toward the path start from the from
    /// bank, +1 toward the path end from the to bank). Returns the nearest
    /// point where that height is reached, the path index that brackets it
    /// on the water side (FromIndex / ToIndex semantics), and its height.
    /// </summary>
    private static (Vector2 top, int index, float height) BankTop(List<Vector2> path, Vector2 bank, int bankIndex, int step, WorldGenerator world)
    {
        List<(Vector2 p, int index)> samples = new() { (bank, bankIndex) };
        Vector2 pos = bank;
        int next = bankIndex;
        float budget = RoadConstants.HighBankReach;
        while (budget > 0f && next >= 0 && next < path.Count)
        {
            Vector2 target = path[next];
            float length = Vector2.Distance(pos, target);
            if (length > 0.01f)
            {
                Vector2 dir = (target - pos) * (1f / length);
                for (float d = 0.5f; d < length && d <= budget; d += 0.5f)
                    samples.Add((pos + dir * d, next));
                if (length <= budget)
                    samples.Add((target, next));
                budget -= length;
            }
            pos = target;
            next += step;
        }

        float best = float.MinValue;
        foreach ((Vector2 p, int _) in samples)
            best = Mathf.Max(best, BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world));
        foreach ((Vector2 p, int index) in samples)
        {
            float h = BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
            if (h >= best - 0.05f)
                return (p, index, h);
        }
        return (bank, bankIndex, BiomeBlendedHeight.GetBlendedHeight(bank.x, bank.y, world));
    }
}
