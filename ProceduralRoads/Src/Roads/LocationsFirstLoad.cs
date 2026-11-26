using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// In every generation (at world load, road_generate, road_regen_island), between choosing the network's destinations
/// and building it: the server settles the landing of each destination whose landing only its placing settles
/// (<see cref="PlacedLocations.NeedsPlacing(ZoneSystem.LocationInstance, WorldGenerator)"/>), and only then builds the roads,
/// all under the loading panel. No zone is generated for it: the game places such a location when a player first comes near
/// its zone, as it always does, and turns it then with its own turn code, seeded for it (<see cref="SeededLocationTurn"/>). So
/// the turn, root, levelling and parts the game will give it are predicted here (<see cref="PlacedLocationReader.Predict"/>)
/// and remembered as its record (<see cref="PlacedLocations.Remember"/>). One already placed (its zone generated with this
/// network in place) is read as the game left it (<see cref="Read"/>), never predicted. It runs as steps of the generation's
/// preparation (<see cref="RoadNetworkGenerator.LocationsFirst"/>, <see cref="RoadPreparation"/>), each with its own stage and
/// count on the loading panel: <see cref="ListingStage"/> and <see cref="PredictingStage"/>; then the generation rebuilds
/// their ground (<see cref="PlacedLocations.PrepareSteps"/>). A location that is unique or shows a map icon once placed is
/// never a destination (<see cref="PlacedLocations.PlayerDecides"/>).
/// </summary>
public static class LocationsFirstLoad
{
    /// <summary>The loading panel's stages, in order.</summary>
    public const string ListingStage = "Finding locations to predict before their roads";
    public const string PredictingStage = "Predicting how the game will place locations";

    /// <summary>Destinations not placed yet: predicted.</summary>
    private static readonly List<(Vector2 centre, Vector2s zone)> s_pending = new();
    /// <summary>Destinations already placed: read.</summary>
    private static readonly List<(Vector2 centre, Vector2s zone)> s_placed = new();

    public static void Install()
    {
        RoadNetworkGenerator.LocationsFirst = Steps;
        RoadNetworkGenerator.OnReset += Reset;
    }

    /// <summary>
    /// The generation's chosen destinations whose landing turns with them listed, then each predicted (not placed yet) or read
    /// (already placed), before the roads are built. One that can be neither gets no road (<see cref="PlacedLocations.NotPlaced"/>,
    /// with its reason).
    /// </summary>
    internal static IEnumerable<bool> Steps(RoadNetworkGenerator.GenerationWork work, RoadPreparation preparation)
    {
        Reset();
        if (!RoadLifecycleManager.HoldsTheNetwork || ZoneSystem.instance == null || WorldGenerator.instance == null) yield break;
        var selected = work.AllSelected.ToList();
        preparation.BeginStage(ListingStage, selected.Count, "destinations"); yield return true;
        foreach (var (_, position, _) in selected)
        {
            List(position);
            if (preparation.Checkpoint()) yield return true;
        }
        if (RoadNetworkGenerator.RoadsLog) ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(
            $"[LOCATIONS] before the roads: {s_pending.Count} location(s) to predict, {s_placed.Count} already placed");
        int count = s_pending.Count + s_placed.Count;
        if (count == 0) yield break;

        // Predicting and reading a location ask the location footprint index (its keep-out edge): built here over frames, not in
        // the first one's frame.
        foreach (var step in RoadPreparation.Own(LocationPrefabLevelling.PrimeFootprintSteps(preparation))) yield return step;
        preparation.BeginStage(PredictingStage, count, "locations"); yield return true;
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        int predicted = 0;
        foreach (var (centre, zone) in s_pending)
        {
            if (Predict(centre, zone)) predicted++;
            else PlacedLocations.NoteWhyNotPlaced(centre, "how the game will place it could not be predicted");
            preparation.CompleteUnit();
            preparation.Checkpoint(); yield return true;
        }
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(FormattableString.Invariant(
            $"[SEEDEDTURN] before the roads: predicted {predicted} of {s_pending.Count} location(s) without placing them, in {Ms(began):F0} ms"));
        began = System.Diagnostics.Stopwatch.GetTimestamp();
        int read = 0;
        foreach (var (centre, zone) in s_placed)
        {
            if (Read(centre, zone)) read++;
            else PlacedLocations.NoteWhyNotPlaced(centre, "placed, but what its placing settled could not be read");
            preparation.CompleteUnit();
            preparation.Checkpoint(); yield return true;
        }
        if (s_placed.Count > 0 && RoadNetworkGenerator.RoadsLog) ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(FormattableString.Invariant(
            $"[LOCATIONS] before the roads: read {read} of {s_placed.Count} placed location(s) (turn, root, parts, rocks) in {Ms(began):F0} ms"));
        Reset();
    }

    /// <summary>One destination: predicted (not placed yet), read (already placed), or neither. Its location is resolved once by
    /// the one resolver (LocationPrefabLevelling.LocationAt: none, or two there, is no location; PlacedLocations.FindNotPlaced
    /// gives the second its reason) and carried from then on by its identity and its own zone.</summary>
    private static void List(Vector3 position)
    {
        if (!LocationPrefabLevelling.LocationAt(new Vector2(position.x, position.z), out ZoneSystem.LocationInstance instance, out _)) return;
        Vector2 centre = LocationPrefabLevelling.Centre(instance);
        Vector2s zone = ZoneSystem.GetZone(instance.m_position);
        // A zone explored before the road network is left exactly as it is (NetworkCutoff): nothing is laid
        // onto a landing there, so its location gets no road (PlacedLocations.NotPlaced).
        if (NetworkCutoff.Excludes(zone)) { PlacedLocations.NoteWhyNotPlaced(centre, PlacedLocations.ZoneLeftAlone); return; }
        if (!PlacedLocations.PlacesFirst(instance, WorldGenerator.instance)) return;
        // Already placed: read; not placed yet: predicted (SeededLocationTurn.Settle, the one decision).
        bool found = PlacedLocationReader.Placed(zone, centre, out _);
        switch (SeededLocationTurn.Settle(found, instance.m_placed || ZoneSystem.instance.IsZoneGenerated(zone)))
        {
            case SeededLocationTurn.Settling.Read: s_placed.Add((centre, zone)); return;
            case SeededLocationTurn.Settling.Predicted: s_pending.Add((centre, zone)); return;
            default: PlacedLocations.NoteWhyNotPlaced(centre, "its zone is generated, but no placing of it was found there to read"); return;
        }
    }

    /// <summary>The record of the destination at <paramref name="centre"/> predicted, not placed (PlacedLocationReader.Predict on
    /// the ground the game's raycast hits on a freshly built zone, <see cref="SeededLocationTurn.MeshGround"/>), and remembered for
    /// the roads as a placed one's is.</summary>
    private static bool Predict(Vector2 centre, Vector2s zone)
    {
        WorldGenerator world = WorldGenerator.instance;
        if (!LocationPrefabLevelling.LocationOf(centre, out ZoneSystem.LocationInstance instance)) return false;
        PlacedLocationRecord? record = PlacedLocationReader.Predict(centre, zone, instance, (x, z) => SeededLocationTurn.MeshGround(x, z, world));
        if (record == null) return false;
        PlacedLocations.Remember(record);
        return true;
    }

    /// <summary>What an already placed location's placing settled, remembered for the roads (<see cref="PlacedLocations"/>), by the
    /// one record builder (PlacedLocationReader.Record). Main thread.</summary>
    private static bool Read(Vector2 centre, Vector2s zone)
    {
        if (!PlacedLocationReader.Placed(zone, centre, out ZDO? proxy)) return false;
        float yaw = proxy!.GetRotation().eulerAngles.y, root = proxy.GetPosition().y;
        // A location that levels nothing (no terrain modifier) is read too: it has no flat, and is reached at its ring at the
        // height the game stood it (PlacedLocations.FlatlessPlaced).
        IReadOnlyList<LevelOp>? ops = PlacedLocationReader.KeptOps(centre, proxy);
        if (ops == null) return false;
        string prefab = LocationPrefabLevelling.LocationOf(centre, out ZoneSystem.LocationInstance placed) ? placed.m_location.m_prefab.name : "?";
        // Its own built pieces are read with its boulders: every approach keeps clear of them (PlacedLocationShape.Refusal: the walker's own body).
        PlacedLocations.Remember(PlacedLocationReader.Record(centre, prefab, yaw, root, ops,
            (x0, z0, w) => PlacedLocationReader.LocationRocks(x0, z0, w, w, centre, root, yaw, out _)));
        return true;
    }

    public static void Reset()
    {
        s_pending.Clear();
        s_placed.Clear();
    }

    private static double Ms(long began) => (System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
}
