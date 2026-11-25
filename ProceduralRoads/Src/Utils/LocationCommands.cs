namespace ProceduralRoads;

/// <summary>
/// Console commands for the locations placed before their roads:
/// road_locations_dump and road_placed_locations_dump (for offline tools).
/// Registered only with PROCEDURALROADS_DEV_COMMANDS on (<see cref="ConsoleCommands.DevCommands"/>);
/// off by default. Read-only.
/// </summary>
public static class LocationCommands
{
    public static void Register()
    {
        new Terminal.ConsoleCommand(
            "road_locations_dump",
            "Write every location prefab's levelling (radius, height, smoothing) to BepInEx/config/ProceduralRoads.locations.csv, for offline tools. Read-only.",
            args =>
            {
                if (ZoneSystem.instance == null) { args.Context.AddString("Error: no world loaded"); return; }
                string path = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "ProceduralRoads.locations.csv");
                string csv = LocationPrefabLevelling.DescribeCsv();
                System.IO.File.WriteAllText(path, csv);
                args.Context.AddString($"Wrote {csv.Split('\n').Length - 2} row(s) to {path}");
            }, isCheat: true);

        new Terminal.ConsoleCommand(
            "road_placed_locations_dump",
            "Write each location placed this session for its approach (turn, root, levelling, rocks) to BepInEx/config/ProceduralRoads.placed_locations.csv, for offline tools. Read-only.",
            args =>
            {
                string path = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "ProceduralRoads.placed_locations.csv");
                System.IO.File.WriteAllText(path, PlacedLocations.Write(PlacedLocations.All));
                args.Context.AddString($"Wrote {PlacedLocations.Count} placed location(s) to {path}");
            }, isCheat: true);
    }
}
