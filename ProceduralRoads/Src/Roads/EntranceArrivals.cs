using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// For the roads log: which landing of a location with entrances (<see cref="PlacedLocationShape.DoorLandings"/>) its road arrived
/// on, an entrance or its fallback (<see cref="PlacedLocation.Fallback"/>), and why its entrances were not reached. Written as the
/// ways onto landings are planned (<see cref="RoadNetworkGenerator.PlanEntry"/>: its <see cref="Arrival"/>, carried on the entry) and laid, read once an island's destinations are
/// counted: one <c>[ENTRANCE]</c> line per destination with entrances. Decides nothing. Any thread.
/// </summary>
internal static class EntranceArrivals
{
    /// <summary>By location: the entrance index its laid way arrived at (-1 its fallback), why its entrances were not reached, and
    /// the rung of the arrival ladder the road that laid it arrived on.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, float), (int entrance, string why, string? rung)> s_laid = new();

    /// <summary>By location: why its last way onto a landing was planned onto none of its targets.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, float), string> s_failed = new();

    public static void Reset()
    {
        s_laid.Clear();
        s_failed.Clear();
        s_rung.Clear();
    }

    /// <summary>The landing a way onto a placed location's landing was planned onto (<see cref="Target"/>), and why the entrances
    /// tried before it were not reached (<see cref="Before"/>): carried on the way itself, the road's entry
    /// (RoadNetworkGenerator.PlannedRoad.Entries), from where it is planned (RoadNetworkGenerator.PlanEntry) to where it is laid,
    /// generated or manual: the one record the label reads.</summary>
    internal sealed class Arrival
    {
        public readonly PlacedLocationShape Target;
        public readonly string Before;
        public Arrival(PlacedLocationShape target, string before) { Target = target; Before = before; }
    }

    /// <summary>How a target is named in the log: "entrance i", or "fallback".</summary>
    public static string Name(PlacedLocationShape target) =>
        target.Entrance >= 0 ? FormattableString.Invariant($"entrance {target.Entrance}") : target.LevelledArea ? "fallback (levelled area)" : "fallback";

    public static void Failed(PlacedLocation location, string why) => s_failed[RoadNetworkGenerator.LocationKey(location.Centre)] = why;

    /// <summary>Why the last way onto the landing of the location at <paramref name="centre"/> was planned onto none of its
    /// targets (<see cref="Failed"/>), null where none was recorded: what its [ENTRANCE] line prints.</summary>
    internal static string? FailedAt(Vector2 centre) => s_failed.TryGetValue(RoadNetworkGenerator.LocationKey(centre), out string? why) ? why : null;

    /// <summary>By location: the arrival ladder's rung a laid road's end was kept at its ring on (RoadNetworkGenerator.PlanArrival),
    /// for the log (the last laid wins).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, float), string> s_rung = new();

    /// <summary>A road laid with its end kept at <paramref name="location"/>'s ring on <paramref name="rung"/> (none of its ways onto a
    /// landing laid there). A road planned but not laid records nothing: the log reads what was built.</summary>
    public static void Ring(PlacedLocation location, string rung)
    {
        if (rung != "unreached") s_rung[RoadNetworkGenerator.LocationKey(location.Centre)] = rung;
    }

    /// <summary>A way onto <paramref name="location"/>'s landing laid and reaching it as built (RoadNetworkGenerator.LandedAsBuilt), as
    /// it was planned (<paramref name="arrival"/>, its entry's own record; none for a location without entrances), its road arriving
    /// on <paramref name="rung"/>.</summary>
    public static void Laid(PlacedLocation location, Arrival? arrival, string? rung = null)
    {
        if (arrival == null) return;
        var key = RoadNetworkGenerator.LocationKey(location.Centre);
        // An entrance once laid stays the location's arrival; a fallback is noted only where no entrance was.
        if (arrival.Target.Entrance >= 0 || !s_laid.TryGetValue(key, out var was) || was.entrance < 0)
            s_laid[key] = (arrival.Target.Entrance, arrival.Before, rung);
    }

    /// <summary>
    /// The one verdict both log lines of a destination print (<see cref="Log"/>): reached on a laid way onto an entrance's landing or
    /// its fallback landing (its rung that road's), else at its ring (the rung a laid road was kept there on), else unreached; read
    /// from what was laid and from whether the network reaches it (RoadNetworkGenerator.Reached), never from a road only planned.
    /// </summary>
    internal static string ArrivalRung(Vector2 centre, bool reached, bool laid, (int entrance, string why, string? rung) way)
    {
        if (!reached) return "unreached";
        if (laid && way.rung != null) return way.rung;
        return s_rung.TryGetValue(RoadNetworkGenerator.LocationKey(centre), out string? ring) ? ring : "ring";
    }

    /// <summary>The entrance the way laid onto the location at <paramref name="centre"/> arrived at (-1 its fallback), if one was.</summary>
    internal static int? LaidAt(Vector2 centre) => s_laid.TryGetValue(RoadNetworkGenerator.LocationKey(centre), out var laid) ? laid.entrance : null;

    /// <summary>One line per destination of <paramref name="selected"/> with entrances: reached at an entrance, at its fallback, not
    /// placed first (its entrances not aimed for), or unreached.</summary>
    public static void Log(List<(string name, Vector3 position, float radius)> selected)
    {
        foreach (var d in selected)
        {
            var centre = new Vector2(d.position.x, d.position.z);
            bool ready = PlacedLocations.TryGetReady(centre, out PlacedLocation placed);
            string? prefab = ready ? placed.Record.Prefab : LocationPrefabLevelling.PrefabAt(centre);
            // One [ARRIVAL] line per destination: the rung its road arrived on (placed first), else whether it is reached; the same
            // verdict the [ENTRANCE] line prints (ArrivalRung, from what was laid).
            bool reached = RoadNetworkGenerator.Reached(d.position, d.radius, d.name);
            var key = RoadNetworkGenerator.LocationKey(centre);
            bool wasLaid = s_laid.TryGetValue(key, out var laid);
            string rungAt = !ready ? (reached ? "notplaced-reached" : "notplaced-unreached") : ArrivalRung(centre, reached, wasLaid, laid);
            Say(FormattableString.Invariant($"[ARRIVAL] {rungAt} {prefab ?? d.name} at ({centre.x:F1}, {centre.y:F1})"));
            if (!PlacedLocationShape.HasDoor(prefab)) continue;
            string at = FormattableString.Invariant($"at ({centre.x:F1}, {centre.y:F1})");
            if (!ready || placed.Entrances.Count == 0)
            {
                Say(FormattableString.Invariant($"[ENTRANCE] not placed {prefab} {at}: {(reached ? "reached" : "unreached")} as a location not placed first"));
                continue;
            }
            PlacedLocationShape? near = RoadNetworkGenerator.ReachedTarget(placed);
            int entrance = wasLaid && laid.entrance >= 0 ? laid.entrance : near is { Entrance: >= 0 } n ? n.Entrance : -1;
            if (reached && entrance >= 0)
            {
                Vector2 landing = placed.Entrances[entrance].Landing;
                string how = wasLaid && laid.entrance == entrance ? "its way onto the landing laid" : "network at its landing";
                Say(FormattableString.Invariant($"[ENTRANCE] reached {prefab} entrance {entrance} {at}: landing ({landing.x:F1}, {landing.y:F1}) at {placed.Ground(landing.x, landing.y):F2}, {how}{(wasLaid && laid.entrance == entrance && laid.why != "" ? "; refused before it: " + laid.why : "")}"));
                continue;
            }
            string why = wasLaid && laid.why != "" ? laid.why : s_failed.TryGetValue(key, out string? failed) ? failed : "no way onto an entrance planned";
            if (reached)
            {
                string onto = near != null && near.Entrance < 0 && placed.Fallback != null
                    ? FormattableString.Invariant($"its fallback landing ({near.Landing.x:F1}, {near.Landing.y:F1})")
                    : PlacedLocations.Landed(centre) ? "its fallback landing" : "its ring";
                Say(FormattableString.Invariant($"[ENTRANCE] fallback {prefab} {at}: reached at {onto}; {why}"));
            }
            else Say(FormattableString.Invariant($"[ENTRANCE] unreached {prefab} {at}: {why}"));
        }
    }

    private static void Say(string line) => ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(line);
}
