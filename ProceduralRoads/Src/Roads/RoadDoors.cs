using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A place's door: a point on the ring a road stops at (<see cref="RoadNetworkGenerator.KeepOutEdge(Vector2, float, float)"/>,
/// out by <see cref="PlacedSites.AimOut"/>) where it can meet the place, with the point the search ends at a search cell
/// further out (<see cref="LeadIn"/>, the road then running straight in), the ground's height there, the bearing in, and
/// what arriving there costs on top of the road (<see cref="Price"/>).
/// </summary>
internal readonly struct RoadDoor
{
    public readonly Vector2 At, LeadIn;
    public readonly float Height, Heading, Price;
    /// <summary>Level with the place (<see cref="RoadNetworkGenerator.ReachRise"/>): a road ending here reaches it.</summary>
    public readonly bool Level;
    public RoadDoor(Vector2 at, Vector2 leadIn, float height, float heading, float price, bool level)
    { At = at; LeadIn = leadIn; Height = height; Heading = heading; Price = price; Level = level; }
}

public static partial class RoadNetworkGenerator
{
    /// <summary>Each place's doors, built once a generation: at preparation for every destination chosen
    /// (<see cref="CatalogueDoors"/>), else the first time a road asks (a manual road, an island made again). Kept apart
    /// for a place placed first and the same place not yet placed; forgotten with the network (ResetData).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<((int, int) place, int radius, int width, bool placed), RoadDoor[]> s_doors = new();
    private static int m_doorPlaces, m_doorCount, m_doorlessPlaces, m_levelDoorPlaces, m_unlevelPlaces;
    private static int m_doorRoads, m_doorRoadsFound, m_doorFallbacks;

    /// <summary>
    /// Where a road may end at the place at <paramref name="centre"/> (exterior <paramref name="radius"/>, as asked): the
    /// one owner of that. Every <see cref="PlacedSites.AimStep"/> round the ring a road to it stops at, a point on road
    /// ground with road ground a search cell further out. A placed place's are priced by the way round from its landing's
    /// bearing and <see cref="PlacedSites.AimRise"/> a metre of height off its landing's level; a place not placed first by its height off the place's own arrival height
    /// (<see cref="ArrivalHeight"/> on the door's side), the ground as its levelling leaves it (<see cref="ApproachGround"/>). None for a point.
    /// </summary>
    internal static IReadOnlyList<RoadDoor> DoorsOf(Vector2 centre, float radius, float width)
    {
        if (radius <= 0f || GenerationWorld == null) return Array.Empty<RoadDoor>();
        var key = (PlaceKey(centre), Mathf.RoundToInt(radius * 10f), Mathf.RoundToInt(width * 10f), PlacedSites.TryGetReady(centre, out _));
        return s_doors.GetOrAdd(key, _ => BuildDoors(centre, radius, width));
    }

    /// <summary>PROCEDURALROADS_DOOR_WEIGHT: what a metre of a door's price (<see cref="RoadDoor.Price"/>) costs the search
    /// against a metre of road, 1 to 20; 3 by default (at 1 a road to a placed place came in round from behind its
    /// landing, as aimed at its centre it did).</summary>
    internal static readonly int DoorWeight = DebugSwitches.Count("DOOR_WEIGHT", 3, 1, 20);

    private static RoadDoor[] BuildDoors(Vector2 centre, float radius, float width)
    {
        var world = GenerationWorld!;
        bool placed = PlacedSites.TryGetReady(centre, out PlacedSite site);
        float ring = KeepOutEdge(centre, radius, width, placed) + PlacedSites.AimOut;
        float bearing = 0f, level;
        if (placed)
        {
            Vector2 toward = site.Shape.Landing - site.Centre;
            if (toward.sqrMagnitude < 1e-4f) toward = site.Shape.Front;
            bearing = Mathf.Atan2(toward.x, toward.y) * Mathf.Rad2Deg;
            level = site.Ground(site.Shape.Landing.x, site.Shape.Landing.y);
        }
        else level = float.NaN;
        int count = Mathf.RoundToInt(360f / PlacedSites.AimStep);
        var doors = new List<RoadDoor>(count);
        for (int i = 0; i < count; i++)
        {
            float b = bearing + i * PlacedSites.AimStep;
            Vector2 out1 = new Vector2(Mathf.Sin(b * Mathf.Deg2Rad), Mathf.Cos(b * Mathf.Deg2Rad));
            Vector2 at = centre + out1 * ring, leadIn = at + out1 * LeadInLength;
            if (!RoadCrossingDetector.IsRoadGround(at, world) || !RoadCrossingDetector.IsRoadGround(leadIn, world)) continue;
            // None behind a place entered only from its front (PlacedSiteShape.OnEntranceSide): a road ending there does not reach it.
            if (placed && !site.Shape.OnEntranceSide(at)) continue;
            float height = placed ? site.Ground(at.x, at.y)
                : ApproachGround(at, centre, ring) ?? BiomeBlendedHeight.GetBlendedHeight(at.x, at.y, world);
            float arrive = placed ? level : ArrivalHeight(centre, radius, at) ?? float.NaN;
            float off = float.IsNaN(arrive) ? 0f : Mathf.Abs(height - arrive);
            float round = placed ? Mathf.Min(i, count - i) * PlacedSites.AimStep * Mathf.Deg2Rad * ring : 0f;
            doors.Add(new RoadDoor(at, leadIn, height, (b + 180f) % 360f, DoorWeight * (round + PlacedSites.AimRise * off), off <= ReachRise));
        }
        if (RoadsLog)
        {
            System.Threading.Interlocked.Increment(ref m_doorPlaces);
            System.Threading.Interlocked.Add(ref m_doorCount, doors.Count);
            if (doors.Count == 0) System.Threading.Interlocked.Increment(ref m_doorlessPlaces);
            if (!placed && ApproachStairsAt(centre, PrefabAt(centre)))
            {
                if (doors.Exists(d => d.Level)) System.Threading.Interlocked.Increment(ref m_levelDoorPlaces);
                else System.Threading.Interlocked.Increment(ref m_unlevelPlaces);
            }
        }
        return doors.ToArray();
    }

    /// <summary>The door catalogue for every destination chosen, built at preparation.</summary>
    private static void CatalogueDoors(GenerationWork work)
    {
        foreach (IslandWork island in work.Islands)
        {
            foreach (var loc in island.Locations) DoorsOf(new Vector2(loc.position.x, loc.position.z), loc.radius, RoadWidth);
            if (island.Spawn is Vector3 spawn) DoorsOf(new Vector2(spawn.x, spawn.z), island.Radius, RoadWidth);
        }
    }

    private static void ResetDoors()
    {
        s_doors.Clear();
        m_doorPlaces = m_doorCount = m_doorlessPlaces = m_levelDoorPlaces = m_unlevelPlaces = 0;
        m_doorRoads = m_doorRoadsFound = m_doorFallbacks = 0;
        m_doorRetries = m_doorRetriesBuilt = 0;
    }

    /// <summary>
    /// Whether no road can reach the place at <paramref name="centre"/> (exterior <paramref name="radius"/>, as asked): it has
    /// doors, none level with it (<see cref="RoadDoor.Level"/>, <see cref="ReachRise"/>), and nothing can climb from one to its
    /// level: it is not placed first (no way onto a landing, <see cref="PlanEntry"/>) and no approach staircase can be laid
    /// onto it (outside the Mountains, <see cref="ApproachStairsAt"/>, or no single platform to climb onto,
    /// <see cref="LocationLevelling.PlatformHeight"/>). A road to it would end off its level and be refused
    /// (<see cref="EndNotLevel"/>) after every search: on a steep slope, a test cabin's roads took a minute of searches
    /// with stairs to be refused. Refused before searching instead; the place goes unserved.
    /// </summary>
    internal static bool NoLevelDoor(Vector2 centre, float radius, float width)
    {
        if (!OnlyLevelDoors(centre, radius)) return false;
        IReadOnlyList<RoadDoor> doors = DoorsOf(centre, radius, width);
        return doors.Count > 0 && !doors.Any(d => d.Level);
    }

    /// <summary>Whether a road to the place at <paramref name="centre"/> may end only at a door level with it
    /// (<see cref="RoadDoor.Level"/>): nothing can climb from any other to its level (see <see cref="NoLevelDoor"/>). The
    /// search goes to its level doors alone (<see cref="DoorsLeft"/>): to all of them, a road on a slope came in at the
    /// cheaper door under the place and was refused, searched again with stairs, for a minute.</summary>
    private static bool OnlyLevelDoors(Vector2 centre, float radius)
    {
        var world = GenerationWorld;
        if (radius <= 0f || world == null || PlacedSites.TryGetReady(centre, out _) || !HeightNeeded(centre)) return false;
        return !(ApproachStairsAt(centre, PrefabAt(centre))
            && LocationLevelling.PlatformHeight(LocationLevelling.CentreHeight(centre, world), LocationLevelling.OpsAt(centre)) != null);
    }

    /// <summary>The refusal a road to a place with no door level with it is counted under (<see cref="NoLevelDoor"/>).</summary>
    internal const string NoLevelDoorRefusal = "no door level with its place";

    /// <summary>PROCEDURALROADS_DOOR_TRIES: how many times a road is searched to its doors before its last resort
    /// (<see cref="GenerateRoadByDoors"/>), 1 to 8; 2 by default (3 found a few more destinations for a fifth more
    /// generation).</summary>
    internal static readonly int DoorTries = DebugSwitches.Count("DOOR_TRIES", 2, 1, 8);
    /// <summary>One road's doors tried so far (<see cref="GenerateRoadByDoors"/>): null outside it.</summary>
    private sealed class DoorTrial
    {
        public readonly List<Vector2> Tried = new();
        public readonly List<Vector2> Taken = new();
        public bool Found;
    }
    [ThreadStatic] private static DoorTrial? t_doors;
    private static int m_doorRetries, m_doorRetriesBuilt;

    /// <summary>
    /// A road between two places, by their doors: the one owner of the order they are tried in. The search ends at the
    /// cheapest door it gets to (<see cref="RoadPathfinder.FindPathBetween"/>); a road built from there that is refused
    /// anywhere in its planning is searched again without the doors it took and their neighbours round the ring
    /// (<see cref="DoorsLeft"/>), so it arrives somewhere else, up to <see cref="DoorTries"/> searches in all, or
    /// until a search to the doors found no route at all. (A last resort searched as before the catalogue, aimed at a
    /// placed place's ring point or at a place's centre, built 5 of 492 such roads.)
    /// </summary>
    private static bool GenerateRoadByDoors(Vector2 startCenter, float startRadius, Vector2 endCenter, float endRadius,
        float width, string? label, List<Vector2>? networkHints)
    {
        if (NoLevelDoor(startCenter, startRadius, width) || NoLevelDoor(endCenter, endRadius, width))
        {
            RoadSpatialGrid.LastRefusal = NoLevelDoorRefusal;
            m_refusalReasons.AddOrUpdate(NoLevelDoorRefusal, 1, (_, n) => n + 1);
            if (label != null) Log.LogDebug($"Road could not be built ({NoLevelDoorRefusal}), destination left unconnected: {label}");
            NoteFailure(startCenter, endCenter, "refused: " + NoLevelDoorRefusal);
            return false;
        }
        var trial = t_doors = new DoorTrial();
        for (int n = 0; ; n++)
        {
            Finder!.ClearNoStairs();
            trial.Taken.Clear();
            trial.Found = false;
            bool built = GenerateRoadCore(startCenter, startRadius, endCenter, endRadius, width, label, networkHints, default);
            if (RoadsLog && built && n > 0) System.Threading.Interlocked.Increment(ref m_doorRetriesBuilt);
            if (built || trial.Taken.Count == 0 || !trial.Found || n + 1 >= DoorTries) return built;
            // It arrived the way another way into the place does (ReEndOnAWayIn): it ends on that way's road (GenerateRoad),
            // not on another door of its own.
            if (t_reEnd != null) return false;
            trial.Tried.AddRange(trial.Taken);
            if (RoadsLog) System.Threading.Interlocked.Increment(ref m_doorRetries);
            if (RoadsLog && label != null) Log.LogDebug($"Road {label}: refused from the door{(trial.Taken.Count > 1 ? "s" : "")} it took ({string.Join(", ", trial.Taken)}: {RoadSpatialGrid.LastRefusal}); searched again to the others");
        }
    }

    /// <summary>The doors of the place at <paramref name="centre"/> a road may still be searched to: those not within <see cref="PlacedSites.AimStep"/>
    /// round the ring of one it has taken: the door next to a refused one meets the place at much the same height and
    /// heading, and was refused the same way. Where only a level door will do (<see cref="OnlyLevelDoors"/>), only those.</summary>
    private static IReadOnlyList<RoadDoor> DoorsLeft(Vector2 centre, float radius, float width)
    {
        DoorTrial? trial = t_doors;
        IReadOnlyList<RoadDoor> doors = DoorsOf(centre, radius, width);
        if (OnlyLevelDoors(centre, radius) && doors.Any(d => d.Level)) doors = doors.Where(d => d.Level).ToList();
        if (trial == null || trial.Tried.Count == 0) return doors;
        var left = new List<RoadDoor>(doors.Count);
        float near = Mathf.Cos((PlacedSites.AimStep + 1f) * Mathf.Deg2Rad);
        foreach (RoadDoor d in doors)
        {
            Vector2 out1 = d.At - centre;
            bool taken = false;
            foreach (Vector2 t in trial.Tried)
            {
                Vector2 tOut = t - centre;
                // A door of this place (on its ring), not of the road's other end.
                if (Mathf.Abs(tOut.magnitude - out1.magnitude) > 1f) continue;
                if (Vector2.Dot(tOut, out1) >= near * tOut.magnitude * out1.magnitude) { taken = true; break; }
            }
            if (!taken) left.Add(d);
        }
        return left;
    }

    /// <summary>The doors a search to them took (null at an end with none), and whether it found a route.</summary>
    private static void NoteDoorsTaken(bool found, Vector2? start, Vector2? end)
    {
        if (t_doors is not DoorTrial trial) return;
        trial.Found = found;
        if (start is Vector2 s) trial.Taken.Add(s);
        if (end is Vector2 e) trial.Taken.Add(e);
    }

    /// <summary>The searches' ends at <paramref name="doors"/> (their lead-ins, priced), or the one point the search goes to
    /// at a place with none.</summary>
    private static (Vector2 at, float cost)[] SearchEnds(IReadOnlyList<RoadDoor>? doors, Vector2 point)
    {
        if (doors == null || doors.Count == 0) return new[] { (point, 0f) };
        var ends = new (Vector2 at, float cost)[doors.Count];
        for (int i = 0; i < doors.Count; i++) ends[i] = (doors[i].LeadIn, doors[i].Price);
        return ends;
    }

    private static void LogDoors()
    {
        if (!RoadsLog) return;
        Log.LogInfo($"  Door catalogue: {m_doorPlaces} place(s), {m_doorCount} door(s), {m_doorlessPlaces} with none; " +
                    $"level arrival asked: {m_levelDoorPlaces} with a level door, {m_unlevelPlaces} with none; " +
                    $"roads searched to doors {m_doorRoads}, found {m_doorRoadsFound}; searched to a doorless place's centre {m_doorFallbacks}; " +
                    $"searched again to the doors left after a refusal {m_doorRetries}, built {m_doorRetriesBuilt}");
    }
}
