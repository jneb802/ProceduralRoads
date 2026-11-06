using System;

namespace ProceduralRoads;

/// <summary>The game's own world backup, made before a road network is first written into explored land.</summary>
internal static class WorldBackup
{
    /// <summary>
    /// Copies the world as it is on disk to "&lt;world&gt;_backup_&lt;date&gt;", as the game's save system does before it
    /// migrates a save: the game's Manage saves lists and restores it. Nothing of this session has been saved into the
    /// world yet when it is asked, so the copy is the world before the roads. Returns the backup's name, or null when
    /// it could not be made.
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
