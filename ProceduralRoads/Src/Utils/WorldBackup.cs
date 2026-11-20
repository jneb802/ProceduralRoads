using System;

namespace ProceduralRoads;

/// <summary>The game's own world backup, made before a road network is written into explored land (the first load's
/// answer, or road_generate and road_regen_island told IncludeExplored).</summary>
internal static class WorldBackup
{
    /// <summary>
    /// Saves the world now with the game's own save (ZNet.Save), waited for, as the game's save command does: the
    /// network's records go with it (the save hook). True when the game started the save; it skips one it may not make
    /// (a world that failed to load, a save the session blocks).
    /// </summary>
    internal static bool SaveNow()
    {
        try
        {
            ZNet? net = ZNet.instance;
            if (net == null || !net.IsServer() || ZNet.World == null) return false;
            bool started = false;
            void Started() => started = true;
            ZNet.WorldSaveStarted += Started;
            try { net.Save(sync: true); }
            finally { ZNet.WorldSaveStarted -= Started; }
            return started;
        }
        catch (Exception error)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError($"World save before the backup failed: {error}");
            return false;
        }
    }

    /// <summary>
    /// Copies the world as it is on disk to "&lt;world&gt;_backup_&lt;date&gt;", as the game's save system does before it
    /// migrates a save: the game's Manage saves lists and restores it, and it lies beside the world's own files. At a first
    /// load nothing of this session has been saved yet, so the copy is the world before the roads; a command saves first
    /// (<see cref="SaveNow"/>), so the copy is the world as it stands, the network it replaces in it. Returns the backup's
    /// name, or null when it could not be made.
    /// </summary>
    internal static string? Make()
    {
        try
        {
            World? world = ZNet.World;
            if (world == null) return null;
            if (!SaveSystem.TryGetSaveByName(world.m_name, SaveDataType.World, out SaveWithBackups save) || save.IsDeleted)
                return null;
            DateTime now = DateTime.Now;
            if (!SaveSystem.CreateBackup(save.PrimaryFile, now, world.m_fileSource)) return null;
            SaveSystem.InvalidateCache(SaveDataType.World);
            return save.Name + "_backup_" + now.ToString(SaveSystem.s_defaultDateFormat);
        }
        catch (Exception error)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError($"World backup failed: {error}");
            return null;
        }
    }
}
