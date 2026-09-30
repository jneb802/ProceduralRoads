using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx;
using UnityEngine;
using Valheim.Testing.Adapter;
using valheimCLI.Extensions;

namespace ProceduralRoads.TestAdapter;
[BepInPlugin("testing.proceduralroads.adapter", "ProceduralRoads Test Adapter", "0.1.0")]
[BepInDependency("warpalicious.ProceduralRoads")]
[BepInDependency("valheimCLI.valheimCLI")]
public sealed class Plugin : BaseUnityPlugin
{
    private ExtensionRegistration? _registration;
    // The toolkit's registration waits for ValheimCLI's extension API and serves the owned-session identity
    // (roads.testing/session), complete once the world is up and the roads are available.
    private IEnumerator Start() => TestExtension.Register("roads.testing", "0.1.0", "ROADS_TEST_SESSION_TOKEN",
        () => RoadNetworkGenerator.RoadsAvailable, registration => _registration = registration, Logger.LogError,
        new ExtensionCommand("terrain-persist", "Prepare one opt-in natural terrain replication fixture", PersistentTerrain.Run, role: ExtensionRole.Server, needsWorld: true),
        new ExtensionCommand("terrain-calibrate", "Run the opt-in two-zone declared terrain fixture", TerrainCalibration.Run, role: ExtensionRole.Server, needsWorld: true),
        new ExtensionCommand("network", "Read completed network and outstanding append counts", Network, readOnly: true, role: ExtensionRole.Server, needsWorld: true),
        new ExtensionCommand("bridge-zone", "Read marked bridge ZDOs in one zone: <zoneX> <zoneZ>", BridgeZone, readOnly: true, role: ExtensionRole.Server, needsWorld: true));
    private void OnDestroy() => _registration?.Dispose();
    private static IEnumerator Network(ExtensionContext context)
    {
        if (context.Arguments.Count != 0) { context.Fail("usage", "network takes no arguments"); yield break; }
        context.Succeed(new Dictionary<string, object?>
        {
            ["source"] = "roads-memory", ["complete"] = RoadNetworkGenerator.RoadsAvailable,
            ["version"] = RoadSpatialGrid.RoadNetworkVersion, ["cells"] = RoadSpatialGrid.GridCellsWithRoads,
            ["points"] = RoadSpatialGrid.TotalRoadPoints, ["crossings"] = RoadNetworkGenerator.GetRoadCrossings().Count,
            ["pendingZones"] = BridgeAppendQueue.Count, ["loadedFromSave"] = RoadNetworkGenerator.RoadsLoadedFromZDO
        }); yield break;
    }
    private static IEnumerator BridgeZone(ExtensionContext context)
    {
        if (context.Arguments.Count != 2 || !int.TryParse(context.Arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(context.Arguments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z) || Math.Abs((long)x) > ZoneTerrain.MaxZoneIndex || Math.Abs((long)z) > ZoneTerrain.MaxZoneIndex)
        { context.Fail("usage", "bridge-zone <zoneX> <zoneZ> within +/-" + ZoneTerrain.MaxZoneIndex); yield break; }
        // Every saved object in the zone, loaded or not; the census limit below is Roads' own, reported rather than thrown.
        var found = ZoneTerrain.ZoneObjects(new Vector2s(x, z), int.MaxValue);
        var pieces = found.Where(item => item.GetInt(BridgePlans.MarkerHash) == 1).ToArray();
        if (pieces.Length > 512) { context.Fail("census_limit", "More than 512 marked pieces in one zone; result omitted, not truncated."); yield break; }
        context.Succeed(new Dictionary<string, object?>
        {
            ["source"] = "zdo-store", ["complete"] = true, ["zoneX"] = x, ["zoneZ"] = z,
            ["pieces"] = pieces.Select(item => new Dictionary<string, object?>
            {
                ["id"] = item.m_uid.ToString(), ["prefabHash"] = item.GetPrefab(),
                ["position"] = new[] { item.GetPosition().x, item.GetPosition().y, item.GetPosition().z },
                ["rotation"] = new[] { item.GetRotation().x, item.GetRotation().y, item.GetRotation().z, item.GetRotation().w }
            }).ToArray()
        }); yield break;
    }
}
