using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Everything one zone holds because of the roads, read from the saved data:
/// its terrain compiler's fingerprint, its bridge pieces, and its vegetation.
/// The point is comparison -- the same zone of the same world, written by a
/// modded client and written by the server, should report the same thing.
/// Works for zones nobody has loaded, so a server can answer for any zone.
/// </summary>
public static class ZoneReport
{
    public static List<string> Describe(Vector3 point)
    {
        var lines = new List<string>();
        if (ZoneSystem.instance == null || ZDOMan.instance == null)
        {
            lines.Add("No world loaded");
            return lines;
        }

        Vector2s zone = ZoneSystem.GetZone(point);
        int version = RoadSpatialGrid.RoadNetworkVersion;
        List<RoadSpatialGrid.RoadPoint> roadPoints = RoadSpatialGrid.GetRoadPointsInZone(zone);
        lines.Add($"ZONE {zone.x},{zone.y} network {version} generated {ZoneSystem.instance.IsZoneGenerated(zone)} " +
                  $"loadedHere {ZoneSystem.instance.m_zones.ContainsKey(zone)} roadPoints {roadPoints.Count}");
        lines.Add(TerrainLine(zone));
        lines.Add(BridgeLine(zone));
        lines.Add(VegetationLine(zone, version));
        return lines;
    }

    private static string TerrainLine(Vector2s zone)
    {
        List<ZDO> compilers = ServerTerrainBake.FindSavedCompilers(zone);
        if (compilers.Count != 1)
            return $"TERRAIN compilers {compilers.Count}";
        ZDO zdo = compilers[0];
        int stamp = zdo.GetInt(RoadTerrainModifier.AppliedVersionHash, 0);
        byte[]? data = zdo.GetByteArray(ZDOVars.s_TCData, null);
        if (data == null || data.Length == 0)
            return $"TERRAIN compilers 1 stamp {stamp} no terrain data";
        byte[] raw;
        try
        {
            raw = Utils.Decompress(data);
        }
        catch (System.Exception ex)
        {
            return $"TERRAIN compilers 1 stamp {stamp} bytes {data.Length} could not decompress: {ex.Message}";
        }
        return TerrainFingerprint.TryRead(raw, out TerrainFingerprint.Summary summary, out string error)
            ? $"TERRAIN compilers 1 stamp {stamp} bytes {data.Length} {summary}"
            : $"TERRAIN compilers 1 stamp {stamp} bytes {data.Length} unreadable: {error}";
    }

    private static string BridgeLine(Vector2s zone)
    {
        var zdos = new List<ZDO>();
        ZDOMan.instance.FindObjects(zone, zdos, new HashSet<ZoneSystem.SectorIndex>());
        var pieces = new List<string>();
        foreach (ZDO zdo in zdos)
        {
            if (zdo.GetInt(BridgePlans.MarkerHash) != 1)
                continue;
            Vector3 p = zdo.GetPosition();
            Vector3 euler = zdo.GetRotation().eulerAngles;
            // Sorted below, so the order objects come back in cannot change the hash.
            pieces.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}|{1:F2},{2:F2},{3:F2}|{4:F1},{5:F1},{6:F1}|{7:F2}",
                zdo.GetPrefab(), p.x, p.y, p.z, euler.x, euler.y, euler.z, zdo.GetFloat("health", 0f)));
        }
        pieces.Sort(System.StringComparer.Ordinal);
        uint hash = 2166136261u;
        foreach (string piece in pieces)
        {
            foreach (char c in piece)
            {
                unchecked
                {
                    hash ^= c;
                    hash *= 16777619u;
                }
            }
        }
        return $"BRIDGES pieces {pieces.Count} planned {BridgePlans.PlannedPieceCount(zone)} " +
               $"spawned {BridgePlans.IsSpawned(zone)} hash {hash:x8}";
    }

    private static string VegetationLine(Vector2s zone, int version)
    {
        HashSet<int> vegetation = ServerTerrainBake.VegetationPrefabs();
        var zdos = new List<ZDO>();
        ZDOMan.instance.FindObjects(zone, zdos, new HashSet<ZoneSystem.SectorIndex>());
        int total = 0;
        foreach (ZDO zdo in zdos)
        {
            if (vegetation.Contains(zdo.GetPrefab()))
                total++;
        }
        return $"VEGETATION inZone {total} onRoad {ServerTerrainBake.CountVegetationOnRoad(zone)} " +
               $"cleared {VegetationClearing.IsCleared(zone, version)}";
    }
}
