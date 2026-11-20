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
    /// <summary>The shipped Roads.ExistingWorldFirstLoadRoads.</summary>
    public const ExistingWorldRoads DefaultSetting = ExistingWorldRoads.Ask;
    public static ExistingWorldRoads Setting = DefaultSetting;

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
    /// The one owner of the explored-world question at first load (docs/OWNERS.md).
    /// </summary>
    public static Decision Decide(int explored, bool skippedBefore, ExistingWorldRoads setting, bool canAsk, bool playerInWorld)
    {
        if (explored == 0) return Decision.AsAlways;
        if (skippedBefore) return Decision.Skip;
        return setting == ExistingWorldRoads.Ask
            ? canAsk && !playerInWorld ? Decision.Ask : DecisionFor(Recommended)
            : DecisionFor(setting);
    }

    /// <summary>What most players want, and what a dedicated server and the loading screen's first choice take: roads
    /// through their world, round what they made.</summary>
    public const ExistingWorldRoads Recommended = ExistingWorldRoads.IncludeExplored;

    /// <summary>The decision an answer (never Ask) comes to: the setting's, or the host's choice on the loading screen.</summary>
    public static Decision DecisionFor(ExistingWorldRoads answer) => answer switch
    {
        ExistingWorldRoads.Skip => Decision.Skip,
        ExistingWorldRoads.IncludeExplored => Decision.IncludeExplored,
        _ => Decision.NewZonesOnly,
    };

    /// <summary>
    /// The answer road_generate and road_regen_island take when none is typed: over a road network (one in memory, or a
    /// saved one this build could not read), new land only; on a world with none, the first load's own
    /// (<see cref="Recommended"/>). An operator writes explored land under an existing network only by naming
    /// IncludeExplored.
    /// </summary>
    public static ExistingWorldRoads CommandDefault(bool overNetwork) => overNetwork ? ExistingWorldRoads.NewZonesOnly : Recommended;

    /// <summary>An answer's heading: the loading screen's button, and the line road_generate lists it on.</summary>
    public static string Label(ExistingWorldRoads answer) => answer switch
    {
        ExistingWorldRoads.IncludeExplored => "Build roads through explored land too",
        ExistingWorldRoads.NewZonesOnly => "Build roads in new land only",
        _ => "Skip road building",
    };

    /// <summary>What an answer does, as the loading screen explains it under its button.</summary>
    public static string Detail(ExistingWorldRoads answer) => answer switch
    {
        ExistingWorldRoads.IncludeExplored =>
            "Roads are built everywhere except the zones with your buildings or terrain edits, which they go round. " +
            "The world is backed up first. Use road_connect afterwards to join your builds to the network.",
        ExistingWorldRoads.NewZonesOnly =>
            "Roads are built, but only in land explored from now on. Everything explored so far stays as it is.",
        _ => "No road network. The world stays exactly as it is. You can still build one later with road_generate.",
    };

    /// <summary>What an answer adds when it replaces a road network: what becomes of the old roads in explored land.</summary>
    public static string OverNetworkNote(ExistingWorldRoads answer) => answer switch
    {
        ExistingWorldRoads.IncludeExplored =>
            "The old network's ground in explored land cannot be undone: where the new roads do not run, the old roads stay " +
            "as levelled, painted ground. The backup is the undo.",
        ExistingWorldRoads.NewZonesOnly => "The old roads there stay as they are, but are no longer part of the network.",
        _ => "Nothing is run, and the roads stay as they are.",
    };

    /// <summary>The words road_generate and road_regen_island take after their own (the point, for road_regen_island).</summary>
    public const string CommandWords = "[NewZonesOnly|IncludeExplored|Skip] [force]";

    /// <summary>
    /// Reads a command's words (after its name): force last (ManualRoads.EndsInForce), then one answer word
    /// (<see cref="ExistingWorldRoads"/> by name, any case; not Ask), the words before them returned in
    /// <paramref name="rest"/>. Null when they are read, else why not. The one reading of the answer a command is given.
    /// </summary>
    public static string? ReadCommand(IReadOnlyList<string> words, out ExistingWorldRoads? answer, out bool force, out List<string> rest)
    {
        rest = new List<string>(words);
        answer = null;
        force = ManualRoads.EndsInForce(rest);
        if (force) rest.RemoveAt(rest.Count - 1);
        if (rest.Count > 0 && !float.TryParse(rest[rest.Count - 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            string word = rest[rest.Count - 1];
            // By name only: Enum.TryParse would also read numbers and lists ("Skip,NewZonesOnly").
            string? name = Array.Find(Enum.GetNames(typeof(ExistingWorldRoads)), n => string.Equals(n, word, StringComparison.OrdinalIgnoreCase));
            if (name == null || name == nameof(ExistingWorldRoads.Ask)) return $"unknown word '{word}'";
            answer = (ExistingWorldRoads)Enum.Parse(typeof(ExistingWorldRoads), name);
            rest.RemoveAt(rest.Count - 1);
        }
        return null;
    }

    /// <summary>
    /// Saves the world now, the game's own save, and waits for it (the plugin sets it): before a command's backup, so the
    /// backup is the world as it stands, the network it replaces in it. False when the game did not save it.
    /// </summary>
    public static Func<bool>? SaveNow;

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

    /// <summary>The explored zones players built in or dug (<see cref="PlayerChanges.Zones"/>, a terrain compiler counted as
    /// <see cref="PlayerChanges.CompilerIsPlayers"/> says).</summary>
    public static HashSet<Vector2s> PlayerMarkedZones(IReadOnlyCollection<Vector2s> explored) =>
        explored.Count == 0 ? new HashSet<Vector2s>() : PlayerChanges.Zones(explored, PlayerChanges.CompilerIsPlayers);
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

    /// <summary>
    /// Whether a terrain compiler in this zone can only be a player's: with no road network in memory, everywhere; with
    /// one, in the zones it leaves as they are (NetworkCutoff.Excludes). Where the network writes, the compiler is its own.
    /// The one rule for manual roads and for the explored land a new network goes round.
    /// </summary>
    public static bool CompilerIsPlayers(Vector2s zone) => !RoadNetworkGenerator.NetworkInMemory || NetworkCutoff.Excludes(zone);

    /// <summary>Whether a player made this object: vanilla stamps what a player builds with its creator.</summary>
    public static bool IsPlayers(ZDO zdo) => zdo.GetLong(ZDOVars.s_creator, 0L) != 0L;

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
            bool compiler = zdo.GetPrefab() == RoadTerrainModifier.TerrainCompilerPrefabHash;
            if (!compiler && !IsPlayers(zdo)) continue;
            Vector2s zone = ZoneSystem.GetZone(zdo.GetPosition());
            if (only != null && !only.Contains(zone)) continue;
            if (compiler && !compilerIsPlayers(zone)) continue;
            zones.Add(zone);
        }
        return zones;
    }
}
