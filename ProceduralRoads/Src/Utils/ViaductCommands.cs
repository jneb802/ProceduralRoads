using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// road_viaducts: the network's Dvergr viaducts (DvergrViaduct) with where to stand
/// to see them. Read-only. Registered only with PROCEDURALROADS_DEV_COMMANDS on
/// (<see cref="ConsoleCommands.DevCommands"/>, the switch the staircase and site commands read too); off by default.
/// </summary>
public static class ViaductCommands
{
    public static void Register()
    {
        new Terminal.ConsoleCommand("road_viaducts",
            "The network's Dvergr viaducts, nearest first: road_viaducts [count, default 20]. Each row: its middle and deck height " +
            "(a place to stand on it), its banks, span, how high the deck stands, its piers and tallest pier, over a valley or a river, its pieces.",
            List, isCheat: true);
    }

    private static void List(Terminal.ConsoleEventArgs args)
    {
        WorldGenerator? world = WorldGenerator.instance;
        if (world == null) { args.Context.AddString("No world."); return; }
        int count = 20;
        if (args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0) count = n;
        Vector3 me = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
        var viaducts = new List<RoadCrossing>();
        foreach (RoadCrossing c in BridgeLayout.DistinctSites(RoadNetworkGenerator.GetRoadCrossings()))
            if (c.Kind == CrossingKind.Bridge && BridgeStructures.KitFor(c, world) == BridgeStructures.Kit.DvergrViaduct) viaducts.Add(c);
        viaducts.Sort((a, b) => Vector2.Distance(a.Center, new Vector2(me.x, me.z)).CompareTo(Vector2.Distance(b.Center, new Vector2(me.x, me.z))));
        args.Context.AddString($"{viaducts.Count} Dvergr viaduct(s) in the network; nearest {Mathf.Min(count, viaducts.Count)}:");
        int seed = world.GetSeed();
        for (int i = 0; i < viaducts.Count && i < count; i++)
        {
            RoadCrossing c = viaducts[i];
            float deck = DvergrViaduct.DeckHeight(c, world);
            args.Context.AddString(string.Format(CultureInfo.InvariantCulture,
                "  {0}: stand at {1:F0} {2:F0} {3:F0} ({4:F0} m away); {5}; {6} pieces",
                i + 1, c.Center.x, deck + 1f, c.Center.y, Vector2.Distance(c.Center, new Vector2(me.x, me.z)),
                DvergrViaduct.Describe(c, world), DvergrViaduct.Solve(c, world, seed).Count));
        }
    }
}
