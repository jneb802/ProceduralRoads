using System;
using System.Collections.Generic;
using System.IO;

namespace ProceduralRoads;

/// <summary>What the server does the first time a world players have explored loads with no road network.</summary>
public enum ExistingWorldRoads
{
    /// <summary>Ask on screen, with IncludeExplored recommended; where nobody can be asked (a dedicated server), IncludeExplored.</summary>
    Ask,
    /// <summary>Build no roads: the world stays as it is.</summary>
    Skip,
    /// <summary>Build the network, and write it only into land explored from now on.</summary>
    NewZonesOnly,
    /// <summary>Build the network into the explored land too, except the zones players built in or dug.</summary>
    IncludeExplored,
}

/// <summary>
/// The first load of an explored world with no road network (Roads.ExistingWorldFirstLoadRoads, or the choice on the
/// loading screen). A fresh world, and a world that has a network, never ask: they load or generate as they always
/// did. Only a world's first load decides; a world once skipped stays without roads until road_generate.
/// </summary>
public static class ExistingWorldFirstLoad
{
    /// <summary>The setting (the plugin keeps it in step with the config).</summary>
    public static ExistingWorldRoads Setting = ExistingWorldRoads.Ask;

    /// <summary>Whether this peer has a screen to ask on: false on a dedicated server (the plugin sets it).</summary>
    public static Func<bool> CanAsk = () => false;

    /// <summary>
    /// Makes the game's own backup of the world as it is on disk (the plugin sets it): its name, shown and restored
    /// from the game's Manage saves, or null when it could not be made. Asked before the network writes any explored
    /// zone.
    /// </summary>
    public static Func<string?>? Backup;

    /// <summary>Tells the players something on screen (the plugin sets it); the log has it either way.</summary>
    public static Action<string>? Tell;

    /// <summary>What a world's first load does: load or generate as always, ask, or one of the answers.</summary>
    public enum Decision { AsAlways, Ask, Skip, NewZonesOnly, IncludeExplored }

    /// <summary>
    /// The one decision. <paramref name="explored"/>: zones the save had generated before this session;
    /// <paramref name="skippedBefore"/>: an earlier first load skipped the roads; <paramref name="playerInWorld"/>:
    /// the host is already in the world, past the loading screen that asks.
    /// </summary>
    public static Decision Decide(int explored, bool skippedBefore, ExistingWorldRoads setting, bool canAsk, bool playerInWorld)
    {
        if (explored == 0) return Decision.AsAlways;
        if (skippedBefore) return Decision.Skip;
        return setting switch
        {
            ExistingWorldRoads.Skip => Decision.Skip,
            ExistingWorldRoads.NewZonesOnly => Decision.NewZonesOnly,
            ExistingWorldRoads.IncludeExplored => Decision.IncludeExplored,
            // What most players want: roads through their world, round what they made.
            _ => canAsk && !playerInWorld ? Decision.Ask : Decision.IncludeExplored,
        };
    }

    /// <summary>What players are told about the zones an IncludeExplored network left as they are.</summary>
    public static string KeptNote(int kept) =>
        kept == 0
            ? "Roads were built through the explored land: no zone held player buildings or terrain edits."
            : $"Roads go round the {kept} explored zone(s) with your buildings or terrain edits, which are left as they are. " +
              "Use road_connect where you want a road to your own builds.";

    private const int RecordFormat = 1;

    /// <summary>The record saved when a world's first load skipped the roads. Format: [format=1][ExistingWorldRoads.Skip].</summary>
    internal static byte[] SkipRecord()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(RecordFormat);
        writer.Write((int)ExistingWorldRoads.Skip);
        return ms.ToArray();
    }

    /// <summary>Whether a saved record says the world's first load skipped the roads. One this build can't read does
    /// not: the world asks again, which writes nothing until answered.</summary>
    internal static bool SaysSkipped(byte[]? record)
    {
        if (record == null || record.Length != 8) return false;
        try
        {
            using var reader = new BinaryReader(new MemoryStream(record));
            return reader.ReadInt32() == RecordFormat && reader.ReadInt32() == (int)ExistingWorldRoads.Skip;
        }
        catch (Exception) { return false; }
    }

    /// <summary>The explored zones players built in or dug (<see cref="PlayerChanges.Zones"/>): before any network every
    /// terrain compiler is a player's.</summary>
    public static HashSet<Vector2s> PlayerMarkedZones(IReadOnlyCollection<Vector2s> explored) =>
        explored.Count == 0 ? new HashSet<Vector2s>() : PlayerChanges.Zones(explored, _ => true);
}

/// <summary>
/// The one answer to "did players change this zone?": it holds something a player made (vanilla stamps every piece,
/// planted crop and placed ship with its creator; the mod's own pieces carry none) or a terrain compiler a player's
/// edit made (vanilla makes one only for a hoe, pickaxe or cultivator edit, or another terrain operation). Read from
/// every ZDO the server holds, so zones not loaded count too. Where the network has written a zone, its compiler is
/// the network's, and a player's dig there is not told apart from the road's.
/// </summary>
public static class PlayerChanges
{
    private static readonly int TerrainCompilerPrefabHash = "_TerrainCompiler".GetStableHashCode();

    /// <summary>
    /// The zones players changed, among <paramref name="within"/> (null: anywhere). <paramref name="compilerIsPlayers"/>
    /// says where a terrain compiler can only be a player's.
    /// </summary>
    public static HashSet<Vector2s> Zones(IReadOnlyCollection<Vector2s>? within, Func<Vector2s, bool> compilerIsPlayers)
    {
        var zones = new HashSet<Vector2s>();
        if (ZDOMan.instance == null) return zones;
        HashSet<Vector2s>? only = within == null ? null : within as HashSet<Vector2s> ?? new HashSet<Vector2s>(within);
        foreach (ZDO zdo in ZDOMan.instance.m_objectsByID.Values)
        {
            bool compiler = zdo.GetPrefab() == TerrainCompilerPrefabHash;
            if (!compiler && zdo.GetLong(ZDOVars.s_creator, 0L) == 0L) continue;
            Vector2s zone = ZoneSystem.GetZone(zdo.GetPosition());
            if (only != null && !only.Contains(zone)) continue;
            if (compiler && !compilerIsPlayers(zone)) continue;
            zones.Add(zone);
        }
        return zones;
    }
}
