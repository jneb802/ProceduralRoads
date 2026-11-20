using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A location's door: a point on the ring a road stops at (<see cref="RoadNetworkGenerator.KeepOutEdge(Vector2, float, float)"/>,
/// out by <see cref="PlacedLocations.AimOut"/>) where it can meet the location, with the point the search ends at a search cell
/// further out (<see cref="LeadIn"/>, the road then running straight in: at a placed location, back along its way in,
/// <see cref="RoadNetworkGenerator.LeadInOnTheWayIn"/>), the ground's height there, the bearing in, and
/// what arriving there costs on top of the road (<see cref="Price"/>).
/// </summary>
internal readonly struct RoadDoor
{
    public readonly Vector2 At, LeadIn;
    public readonly float Height, Heading, Price;
    /// <summary>Level with the location (<see cref="RoadNetworkGenerator.ReachRise"/>): a road ending here reaches it.</summary>
    public readonly bool Level;
    public RoadDoor(Vector2 at, Vector2 leadIn, float height, float heading, float price, bool level)
    { At = at; LeadIn = leadIn; Height = height; Heading = heading; Price = price; Level = level; }
}

public static partial class RoadNetworkGenerator
{
    /// <summary>Each location's doors, built once a generation: at preparation for every destination chosen
    /// (<see cref="CatalogueDoors"/>), else the first time a road asks (a manual road, an island made again). Kept apart
    /// for a location placed first and the same location not yet placed; forgotten with the network (ResetData).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<((float, float) location, int radius, int width, bool placed), RoadDoor[]> s_doors = new();
    private static int m_doorLocations, m_doorCount, m_doorlessLocations, m_levelDoorLocations, m_unlevelLocations;
    private static int m_doorRoads, m_doorRoadsFound, m_doorFallbacks;

    /// <summary>
    /// Where a road may end at the location at <paramref name="centre"/> (exterior <paramref name="radius"/>, as asked): the
    /// one owner of that. Every <see cref="PlacedLocations.AimStep"/> round the ring a road to it stops at, a point on road
    /// ground with road ground a search cell further out. A placed location's are priced by the way round from its landing's
    /// bearing and <see cref="PlacedLocations.AimRise"/> a metre of height off its landing's level; a location not placed first by its height off the location's own arrival height
    /// (<see cref="ArrivalHeight"/> on the door's side), the ground as its levelling leaves it (<see cref="ApproachGround"/>). None for a point.
    /// </summary>
    internal static IReadOnlyList<RoadDoor> DoorsOf(Vector2 centre, float radius, float width)
    {
        if (radius <= 0f || GenerationWorld == null) return Array.Empty<RoadDoor>();
        var key = (LocationKey(centre), Mathf.RoundToInt(radius * 10f), Mathf.RoundToInt(width * 10f), PlacedLocations.TryGetReady(centre, out _));
        return s_doors.GetOrAdd(key, _ => BuildDoors(centre, radius, width));
    }

    /// <summary>What a metre of a door's price (<see cref="RoadDoor.Price"/>) costs the search
    /// against a metre of road: 3 (at 1 a road to a placed location came in round from behind its
    /// landing, as aimed at its centre it did).</summary>
    internal static readonly int DoorWeight = 3;

    /// <summary>The ring a location's doors stand on: where the road's trim leaves it (<see cref="KeepOutEdge(Vector2, float, float, bool)"/>),
    /// out by <see cref="PlacedLocations.AimOut"/>. The record of a location placed first covers it (LocationsFirstLoad.Read). The one owner of A location's ring, doors and keep-out edge (docs/OWNERS.md).</summary>
    internal static float DoorRing(Vector2 centre, float radius, float width, bool placed) =>
        KeepOutEdge(centre, radius, width, placed) + PlacedLocations.AimOut;

    private static RoadDoor[] BuildDoors(Vector2 centre, float radius, float width)
    {
        var world = GenerationWorld!;
        bool placed = PlacedLocations.TryGetReady(centre, out PlacedLocation location);
        float ring = DoorRing(centre, radius, width, placed);
        float bearing = 0f, level;
        if (placed)
        {
            // Priced from the landing its road aims for first (PlacedLocation.EntranceBearing, EntranceArc: the entrance preference).
            bearing = location.EntranceBearing;
            level = location.LandingHeight;
        }
        else level = float.NaN;
        int count = Mathf.RoundToInt(360f / PlacedLocations.AimStep);
        var doors = new List<RoadDoor>(count);
        for (int i = 0; i < count; i++)
        {
            float b = bearing + i * PlacedLocations.AimStep;
            Vector2 out1 = new Vector2(Mathf.Sin(b * Mathf.Deg2Rad), Mathf.Cos(b * Mathf.Deg2Rad));
            Vector2 at = centre + out1 * ring, leadIn = at + out1 * LeadInLength;
            // A placed location's road comes in head on to its way in (LeadInOnTheWayIn), not to its centre.
            if (placed && LeadInOnTheWayIn(at, location, width) is Vector2 onTheWayIn) leadIn = onTheWayIn;
            if (!RoadCrossingDetector.IsRoadGround(at, world) || !RoadCrossingDetector.IsRoadGround(leadIn, world)) continue;
            // None on a side a placed Location may not be come to from at all (PlacedLocation.OnEntranceSide: a blocked back). Every
            // other side is offered, priced by its arc from the entrance (PlacedLocation.EntranceArc): a road ends nearer the entrance
            // where it can, and on the Location's levelled ground on another side where the entrance side is blocked.
            if (placed && !location.OnEntranceSide(at)) continue;
            float height = placed ? location.Ground(at.x, at.y)
                : ApproachGround(at, centre, ring) ?? GroundBeforeRoads(at.x, at.y, world);
            float arrive = placed ? level : ArrivalHeight(centre, radius, at) ?? float.NaN;
            float off = float.IsNaN(arrive) ? 0f : FloatBits.Abs(height - arrive);
            float round = placed ? PlacedLocation.EntranceArc(Mathf.Min(i, count - i) * PlacedLocations.AimStep, ring) : 0f;
            Vector2 inward = at - leadIn;
            float heading = (Mathf.Atan2(inward.x, inward.y) * Mathf.Rad2Deg + 360f) % 360f;
            doors.Add(new RoadDoor(at, leadIn, height, heading, DoorWeight * (round + PlacedLocations.AimRise * off), off <= ReachRise));
        }
        if (RoadsLog)
        {
            System.Threading.Interlocked.Increment(ref m_doorLocations);
            System.Threading.Interlocked.Add(ref m_doorCount, doors.Count);
            if (doors.Count == 0) System.Threading.Interlocked.Increment(ref m_doorlessLocations);
            if (!placed && ApproachStairsAt(centre, LocationPrefabLevelling.PrefabAt(centre)))
            {
                if (doors.Exists(d => d.Level)) System.Threading.Interlocked.Increment(ref m_levelDoorLocations);
                else System.Threading.Interlocked.Increment(ref m_unlevelLocations);
            }
        }
        return doors.ToArray();
    }

    /// <summary>
    /// A placed location's door's lead-in: a search cell (<see cref="LeadInLength"/>) back along the line its way onto the landing
    /// leaves the door (<see cref="RoadLocationEntry.Route"/> onto the landing its road aims for first, <see cref="PlacedLocation.Aim"/>,
    /// the landing the door's price is read from), so the road comes in head on to its way in and carries straight on. Null where
    /// that line would put the lead-in no further from the centre than the door (the way in leaves it outward, round a lead-round
    /// ring wider than the door's): the door's radial lead-in then. Come in radially to a door off the landing's bearing, the road
    /// turned at the door onto the way in's lead round the location, and again onto the front: two tight turns back to back (a
    /// SunkenCrypt4's and a TrollCave02's roads folded back on themselves so on open ground).
    /// </summary>
    internal static Vector2? LeadInOnTheWayIn(Vector2 at, PlacedLocation location, float width)
    {
        PlacedLocationShape aim = location.Aim;
        PlacedLocationShape.Walker walker = PlacedLocationShape.Walker.Road(width);
        List<Vector2> line = RoadLocationEntry.Route(at, aim, RoadLocationEntry.WayInEnd(aim, at, walker), walker);
        Vector2 leaves = line[1] - at;
        if (leaves.sqrMagnitude < 1e-4f) return null;
        Vector2 leadIn = at - leaves.normalized * LeadInLength;
        return (leadIn - location.Centre).sqrMagnitude > (at - location.Centre).sqrMagnitude ? leadIn : null;
    }

    /// <summary>The door catalogue for every destination chosen, built at preparation.</summary>
    /// <remarks>A step per location, so the preparation's frame budget slices it (RoadPreparation.Advance): a placed Location's
    /// doors each lead in on its way in (LeadInOnTheWayIn: its route and its end, walked against its body's rocks), and in game,
    /// with the prefabs' own rocks read, the whole catalogue in one step held the main thread 3.6 s.</remarks>
    internal static IEnumerable<bool> CatalogueDoors(GenerationWork work, RoadPreparation preparation)
    {
        preparation.BeginStage("Cataloguing doors", work.Islands.Sum(i => i.Locations.Count + (i.Spawn.HasValue ? 1 : 0)), "locations");
        foreach (IslandWork island in work.Islands)
        {
            foreach (var loc in island.Locations)
            {
                DoorsOf(new Vector2(loc.position.x, loc.position.z), loc.radius, RoadWidth);
                preparation.CompleteUnit();
                yield return true;
            }
            if (island.Spawn is Vector3 spawn)
            {
                DoorsOf(new Vector2(spawn.x, spawn.z), island.Radius, RoadWidth);
                preparation.CompleteUnit();
                yield return true;
            }
        }
    }

    private static void ResetDoors()
    {
        s_doors.Clear();
        m_doorLocations = m_doorCount = m_doorlessLocations = m_levelDoorLocations = m_unlevelLocations = 0;
        m_doorRoads = m_doorRoadsFound = m_doorFallbacks = 0;
        m_doorRetries = m_doorRetriesBuilt = 0;
    }

    /// <summary>
    /// Whether no road can reach the location at <paramref name="centre"/> (exterior <paramref name="radius"/>, as asked): it has
    /// doors, none level with it (<see cref="RoadDoor.Level"/>, <see cref="ReachRise"/>), and nothing can climb from one to its
    /// level: it is not placed first (no way onto a landing, <see cref="PlanEntry"/>) and no approach staircase can be laid
    /// onto it (outside the Mountains, <see cref="ApproachStairsAt"/>, or no single platform to climb onto,
    /// <see cref="LocationLevelling.PlatformHeight"/>). A road to it would end off its level and be refused
    /// (<see cref="EndNotLevel"/>) after every search: on a steep slope, a test cabin's roads took a minute of searches
    /// with stairs to be refused. Refused before searching instead; the location goes unserved.
    /// </summary>
    internal static bool NoLevelDoor(Vector2 centre, float radius, float width)
    {
        if (!OnlyLevelDoors(centre, radius)) return false;
        IReadOnlyList<RoadDoor> doors = DoorsOf(centre, radius, width);
        return doors.Count > 0 && !doors.Any(d => d.Level);
    }

    /// <summary>Whether a road to the location at <paramref name="centre"/> may end only at a door level with it
    /// (<see cref="RoadDoor.Level"/>): nothing can climb from any other to its level (see <see cref="NoLevelDoor"/>). The
    /// search goes to its level doors alone (<see cref="DoorsLeft"/>): to all of them, a road on a slope came in at the
    /// cheaper door under the location and was refused, searched again with stairs, for a minute.</summary>
    private static bool OnlyLevelDoors(Vector2 centre, float radius)
    {
        var world = GenerationWorld;
        if (radius <= 0f || world == null || PlacedLocations.TryGetReady(centre, out _) || !HeightNeeded(centre)) return false;
        return !(ApproachStairsAt(centre, LocationPrefabLevelling.PrefabAt(centre))
            && LocationLevelling.PlatformHeight(LocationLevelling.CentreHeight(centre, world), LocationLevelling.OpsAt(centre)) != null);
    }

    /// <summary>The refusal a road to a location with no door level with it is counted under (<see cref="NoLevelDoor"/>).</summary>
    internal const string NoLevelDoorRefusal = "no door level with its location";

    /// <summary>How many times a road is searched to its doors before its last resort
    /// (<see cref="GenerateRoadByDoors"/>): 2 (3 found a few more destinations for a fifth more
    /// generation).</summary>
    internal static readonly int DoorTries = 2;
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
    /// A road between two locations, by their doors: the one owner of the order they are tried in. The search ends at the
    /// cheapest door it gets to (<see cref="RoadPathfinder.FindPathBetween"/>); a road built from there that is refused
    /// anywhere in its planning is searched again without the doors it took and their neighbours round the ring
    /// (<see cref="DoorsLeft"/>), so it arrives somewhere else, up to <see cref="DoorTries"/> searches in all, or
    /// until a search to the doors found no route at all. (A last resort searched as before the catalogue, aimed at a
    /// placed location's ring point or at a location's centre, built 5 of 492 such roads.)
    /// </summary>
    private static bool GenerateRoadByDoors(Vector2 startCenter, float startRadius, Vector2 endCenter, float endRadius,
        float width, string? label, List<Vector2>? networkHints, Vector2? joinedThrough)
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
            bool built = GenerateRoadCore(startCenter, startRadius, endCenter, endRadius, width, label, networkHints, joinedThrough, default);
            if (RoadsLog && built && n > 0) System.Threading.Interlocked.Increment(ref m_doorRetriesBuilt);
            if (built || trial.Taken.Count == 0 || !trial.Found || n + 1 >= DoorTries) return built;
            // A duplicate of a way into the location (ReEndOnAWayIn, PlanStop.Duplicate): dropped or searched again from that way's road (GenerateRoad), never tried
            // at another door of its own.
            if (t_reEnd != null) return false;
            trial.Tried.AddRange(trial.Taken);
            if (RoadsLog) System.Threading.Interlocked.Increment(ref m_doorRetries);
            if (RoadsLog && label != null)
            {
                // Each door once: every sub-search within the road notes the doors it took (NoteDoorsTaken).
                var taken = trial.Taken.Distinct().ToList();
                Log.LogDebug($"Road {label}: refused from the door{(taken.Count > 1 ? "s" : "")} it took ({string.Join(", ", taken)}: {RoadSpatialGrid.LastRefusal}); searched again to the others");
            }
        }
    }

    /// <summary>The doors of the location at <paramref name="centre"/> a road may still be searched to: those not within <see cref="PlacedLocations.AimStep"/>
    /// round the ring of one it has taken: the door next to a refused one meets the location at much the same height and
    /// heading, and was refused the same way. Where only a level door will do (<see cref="OnlyLevelDoors"/>), only those.</summary>
    private static IReadOnlyList<RoadDoor> DoorsLeft(Vector2 centre, float radius, float width)
    {
        DoorTrial? trial = t_doors;
        IReadOnlyList<RoadDoor> doors = DoorsOf(centre, radius, width);
        if (OnlyLevelDoors(centre, radius) && doors.Any(d => d.Level)) doors = doors.Where(d => d.Level).ToList();
        if (trial == null || trial.Tried.Count == 0) return doors;
        var left = new List<RoadDoor>(doors.Count);
        float near = Mathf.Cos((PlacedLocations.AimStep + 1f) * Mathf.Deg2Rad);
        foreach (RoadDoor d in doors)
        {
            Vector2 out1 = d.At - centre;
            bool taken = false;
            foreach (Vector2 t in trial.Tried)
            {
                Vector2 tOut = t - centre;
                // A door of this location (on its ring), not of the road's other end.
                if (FloatBits.Abs(tOut.magnitude - out1.magnitude) > 1f) continue;
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
    /// at a location with none.</summary>
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
        Log.LogInfo($"  Door catalogue: {m_doorLocations} location(s), {m_doorCount} door(s), {m_doorlessLocations} with none; " +
                    $"level arrival asked: {m_levelDoorLocations} with a level door, {m_unlevelLocations} with none; " +
                    $"roads searched to doors {m_doorRoads}, found {m_doorRoadsFound}; searched to a doorless location's centre {m_doorFallbacks}; " +
                    $"searched again to the doors left after a refusal {m_doorRetries}, built {m_doorRetriesBuilt}");
    }
}
