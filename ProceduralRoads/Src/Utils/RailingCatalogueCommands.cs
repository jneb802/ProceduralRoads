using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// road_railcat: lays the railing catalogue's bays (<see cref="RailingCatalogue"/>) in game for correcting by hand, and
/// captures what a hand changed in each. Registered only with PROCEDURALROADS_DEV_COMMANDS on
/// (<see cref="ConsoleCommands.DevCommands"/>); host only.
/// <list type="bullet">
/// <item>lay [x z] [blackmarble|stone|both] [dir=PATH] [again]: a row of bays per kit running east from (x, z) (default: 10 m
/// north of the player), black marble's row there and stone's 20 m north of it, each bay moved on along its row to the first
/// ground gentle enough that it stands on walls (<see cref="RailingCatalogue.Air"/> within <see cref="StairSupport.WallHeight"/>,
/// as a layout's floors do; laid 1 m over the highest ground of a slope, a stone bay was held out from it by arches and
/// corbels, their low ends over the air), its lowest walking piece <see cref="RailingCatalogue.Clearance"/> over the highest
/// finished ground under it; each staircase
/// dressed by its kit as generation dresses one (<see cref="StairStructures.DressAmong"/>) and placed by the one placing path
/// (<see cref="StructurePlacement.SpawnLoose"/>), tagged with its bay and not with the network's marker. Each bay is said in
/// the log with its join's coordinates; bays.tsv and each bay's laid table go to the folder (default
/// BepInEx/config/ProceduralRoads.railcat). With again, the bays laid before are cleared first (only what the catalogue laid).</item>
/// <item>capture [dir=PATH]: every building piece standing in each bay's box, as a table (bay-NN-name.now.tsv), and its diff
/// against what its kit laid (bay-NN-name.diff.txt: kept, removed, added, moved; <see cref="RailingCatalogue.Diff"/>).</item>
/// <item>clear [all] [dir=PATH]: removes what the catalogue laid; with all, every building piece in the bays' boxes too.</item>
/// </list>
/// </summary>
public static class RailingCatalogueCommands
{
    /// <summary>The ZDO key a catalogue piece carries its bay under.</summary>
    internal static readonly int TagHash = "ProceduralRoads_RailCatalogue".GetStableHashCode();

    /// <summary>Between bays' boxes in a row, and between the two kits' rows.</summary>
    private const float BetweenBays = 6f, BetweenRows = 20f;

    /// <summary>How far on along its row a bay is moved, a metre at a time, to stand on gentle ground (debug only).</summary>
    private const float SearchAlong = 60f;

    public static void Register()
    {
        new Terminal.ConsoleCommand("road_railcat",
            "Railing catalogue at unsnapped staircase joins, for correcting by hand: road_railcat lay [x z] [blackmarble|stone|both] [dir=PATH] [again] " +
            "lays a row of bays per kit (each two staircases met end to end, dressed by the kit as generation does); road_railcat capture [dir=PATH] " +
            "writes each bay's pieces now and their diff against what the kit laid; road_railcat clear [all] [dir=PATH]. Host only.",
            Run, isCheat: true);
    }

    private static void Say(Terminal.ConsoleEventArgs args, string line)
    {
        args.Context.AddString(line);
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo("[RAILCAT] " + line);
    }

    private static void Run(Terminal.ConsoleEventArgs args)
    {
        try
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || WorldGenerator.instance == null || ZDOMan.instance == null)
            {
                args.Context.AddString("ERROR: road_railcat needs a world this peer hosts.");
                return;
            }
            var words = args.Args.Skip(1).ToList();
            string verb = words.Count > 0 ? words[0] : "";
            words = words.Skip(1).ToList();
            string dir = words.FirstOrDefault(w => w.StartsWith("dir=", StringComparison.Ordinal))?.Substring(4)
                         ?? Path.Combine(BepInEx.Paths.ConfigPath, "ProceduralRoads.railcat");
            words.RemoveAll(w => w.StartsWith("dir=", StringComparison.Ordinal));
            switch (verb)
            {
                case "lay": Lay(args, words, dir); break;
                case "capture": Capture(args, dir); break;
                case "clear": Say(args, $"OK: RAILCAT_CLEAR {Clear(dir, words.Contains("all"))} piece(s) removed"); break;
                default: args.Context.AddString("usage: road_railcat lay|capture|clear (see help road_railcat)"); break;
            }
        }
        catch (Exception e)
        {
            args.Context.AddString("ERROR: road_railcat: " + e.Message);
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError("[RAILCAT] " + e);
        }
    }

    /// <summary>A laid bay as bays.tsv keeps it: its number, name, kit, join (at its walking surface) and box.</summary>
    private sealed class LaidBay
    {
        public int Number;
        public string Name = "", Kit = "";
        public Vector3 Join;
        public Vector2 Min, Max;
        public string File(string dir, string what) => Path.Combine(dir, FormattableString.Invariant($"bay-{Number:D2}-{Kit}-{Name}.{what}"));
        public bool Holds(Vector3 p) => p.x >= Min.x && p.x <= Max.x && p.z >= Min.y && p.z <= Max.y && Mathf.Abs(p.y - Join.y) < 20f;
    }

    private static string F(float v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private static List<LaidBay> ReadBays(string dir)
    {
        string path = Path.Combine(dir, "bays.tsv");
        var bays = new List<LaidBay>();
        if (!File.Exists(path)) return bays;
        static float R(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        foreach (string line in File.ReadAllLines(path).Skip(1))
        {
            string[] f = line.Split('\t');
            if (f.Length < 10) continue;
            bays.Add(new LaidBay
            {
                Number = int.Parse(f[0], CultureInfo.InvariantCulture), Name = f[1], Kit = f[2],
                Join = new Vector3(R(f[3]), R(f[4]), R(f[5])), Min = new Vector2(R(f[6]), R(f[7])), Max = new Vector2(R(f[8]), R(f[9])),
            });
        }
        return bays;
    }

    private static void Lay(Terminal.ConsoleEventArgs args, List<string> words, string dir)
    {
        WorldGenerator world = WorldGenerator.instance;
        bool again = words.Remove("again");
        var kits = new List<StairKit>();
        if (words.Remove("stone")) kits.Add(StairKit.Stone);
        if (words.Remove("blackmarble")) kits.Add(StairKit.BlackMarble);
        if (words.Remove("both") || kits.Count == 0) kits = new List<StairKit> { StairKit.BlackMarble, StairKit.Stone };
        Vector2 start;
        if (words.Count >= 2)
            start = new Vector2(float.Parse(words[0], NumberStyles.Float, CultureInfo.InvariantCulture), float.Parse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture));
        else if (Player.m_localPlayer != null)
            start = new Vector2(Player.m_localPlayer.transform.position.x, Player.m_localPlayer.transform.position.z + 10f);
        else { args.Context.AddString("ERROR: give x z (no local player)"); return; }

        if (ReadBays(dir).Count > 0)
        {
            if (!again) { args.Context.AddString($"ERROR: bays already laid ({Path.Combine(dir, "bays.tsv")}): capture first, then lay ... again"); return; }
            Say(args, $"cleared {Clear(dir, all: false)} piece(s) the catalogue laid before");
        }
        Directory.CreateDirectory(dir);
        Func<int, int, float> finished = StructureGround.Vertices(world);
        var table = new List<string> { "bay\tname\tkit\tjoin_x\tjoin_y\tjoin_z\tmin_x\tmin_z\tmax_x\tmax_z\tpieces\trails\tposts\trefused" };
        int number = 0, total = 0;
        for (int row = 0; row < kits.Count; row++)
        {
            StairKit kit = kits[row];
            float x = start.x;
            bool first = true;
            foreach (RailingCatalogue.Bay bay in RailingCatalogue.Bays(kit))
            {
                number++;
                var (min, max) = RailingCatalogue.Box(bay);
                if (!first) x -= min.x;
                first = false;
                // On along the row to the first ground gentle enough that the bay stands on walls (RailingCatalogue.Air within
                // StairSupport.WallHeight), as a layout's floors do; else the gentlest seen within SearchAlong.
                Vector2 join = new(x, start.y + row * BetweenRows);
                float lowest = float.NaN, highest = float.NaN, air = float.PositiveInfinity;
                for (float on = 0f; on <= SearchAlong; on += 1f)
                {
                    var at = new Vector2(x + on, join.y);
                    var (lo, hi) = RailingCatalogue.GroundUnder(bay, at, finished);
                    if (float.IsNaN(hi)) continue;
                    float a = RailingCatalogue.Air(bay, lo, hi);
                    if (a < air) (join, lowest, highest, air) = (at, lo, hi, a);
                    if (a <= StairSupport.WallHeight) break;
                }
                x = join.x + max.x + BetweenBays;
                if (float.IsNaN(highest)) { Say(args, $"bay {number:D2} {kit.Name} {bay.Name}: ground not known, not laid"); continue; }
                float surface = RailingCatalogue.SurfaceOver(bay, highest);
                List<StairRun> runs = RailingCatalogue.Place(bay, join, surface);
                var shortfall = new StairSupport.Shortfall();
                List<List<StructurePiece>> dressed = RailingCatalogue.Dress(runs, run => RoadStairRuns.PlanningGround(run, world), finished,
                    shortfall, (bx, bz) => world.GetBiome(bx, bz));
                var pieces = dressed.SelectMany(p => p).ToList();
                int refused = dressed.Count(p => p.Count == 0);
                // Its capture looks over every piece its kits laid (a corbel 2 m out from a floor's side stood outside its
                // walking pieces' box, and was read as removed).
                var (reachMin, reachMax) = RailingCatalogue.Reach(pieces);
                var laid = new LaidBay
                {
                    Number = number, Name = bay.Name, Kit = kit.Name, Join = new Vector3(join.x, surface, join.y),
                    Min = Vector2.Min(min + join, reachMin), Max = Vector2.Max(max + join, reachMax),
                };
                int spawned = StructurePlacement.SpawnLoose(pieces, TagHash, laid.Number.ToString(CultureInfo.InvariantCulture),
                    at => !ZoneSystem.instance.IsZoneLoaded(at));
                total += spawned;
                File.WriteAllLines(laid.File(dir, "laid.tsv"),
                    new[] { RailingCatalogue.Header }.Concat(pieces.Select(p => RailingCatalogue.Row(RailingCatalogue.Placed.Of(p), laid.Join))));
                int rails = pieces.Count(p => p.Kind == StructurePieceKind.Beam), posts = pieces.Count(p => p.Kind == StructurePieceKind.Post);
                table.Add(string.Join("\t", number.ToString(CultureInfo.InvariantCulture), bay.Name, kit.Name, F(laid.Join.x), F(laid.Join.y), F(laid.Join.z),
                    F(laid.Min.x), F(laid.Min.y), F(laid.Max.x), F(laid.Max.y), pieces.Count.ToString(CultureInfo.InvariantCulture),
                    rails.ToString(CultureInfo.InvariantCulture), posts.ToString(CultureInfo.InvariantCulture), refused.ToString(CultureInfo.InvariantCulture)));
                Say(args, FormattableString.Invariant(
                    $"bay {number:D2} {kit.Name} {bay.Name}: join ({join.x:F1}, {surface:F1}, {join.y:F1}), box x {laid.Min.x:F0}..{laid.Max.x:F0} z {laid.Min.y:F0}..{laid.Max.y:F0}, {spawned} piece(s), {rails} rail(s), {posts} post(s), ground {lowest:F1}..{highest:F1}, {air:F1} m under its highest piece")
                    + (air > StairSupport.WallHeight ? FormattableString.Invariant($" (over {StairSupport.WallHeight:F1}: held from the slope, no gentler ground within {SearchAlong:F0} m)") : "")
                    + (refused > 0 ? $", {refused} staircase(s) refused by its kit" : "")
                    + (shortfall.Any ? FormattableString.Invariant(
                        $"; supports short: {shortfall.Unheld.Count} unheld, {shortfall.ShortWalls.Count} wall cell(s) stopped short, {shortfall.Fell.Count} fell, {shortfall.Falling.Count} walking falling, {shortfall.Weak.Count} weak") : ""));
            }
        }
        File.WriteAllLines(Path.Combine(dir, "bays.tsv"), table);
        Say(args, $"OK: RAILCAT_LAY {number} bay(s), {total} piece(s); tables in {dir}");
    }

    /// <summary>Every building piece standing in a bay's box (a prefab with a Piece), loaded or not.</summary>
    private static List<(ZDO zdo, RailingCatalogue.Placed placed)> Standing(LaidBay bay)
    {
        var found = new List<(ZDO, RailingCatalogue.Placed)>();
        foreach (ZDO zdo in ZDOMan.instance.m_objectsByID.Values)
        {
            Vector3 at = zdo.GetPosition();
            if (!bay.Holds(at)) continue;
            GameObject? prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            if (prefab == null || prefab.GetComponent<Piece>() == null) continue;
            Vector3 euler = zdo.GetRotation().eulerAngles;
            found.Add((zdo, new RailingCatalogue.Placed(prefab.name, at, euler.y, euler.x, euler.z, zdo.GetString(TagHash, "").Length > 0)));
        }
        return found;
    }

    private static void Capture(Terminal.ConsoleEventArgs args, string dir)
    {
        List<LaidBay> bays = ReadBays(dir);
        if (bays.Count == 0) { args.Context.AddString($"ERROR: no bays laid in {dir}"); return; }
        int changed = 0;
        foreach (LaidBay bay in bays)
        {
            var laid = File.Exists(bay.File(dir, "laid.tsv"))
                ? File.ReadAllLines(bay.File(dir, "laid.tsv")).Select(RailingCatalogue.Parse).Where(p => p != null).Select(p => p!.Value).ToList()
                : new List<RailingCatalogue.Placed>();
            var now = Standing(bay).ConvertAll(s => s.placed);
            File.WriteAllLines(bay.File(dir, "now.tsv"), new[] { RailingCatalogue.Header }.Concat(now.Select(p => RailingCatalogue.Row(p, bay.Join))));
            List<string> diff = RailingCatalogue.Diff(laid, now, bay.Join, out var c);
            string head = FormattableString.Invariant(
                $"# bay {bay.Number:D2} {bay.Kit} {bay.Name}: join ({bay.Join.x:F3}, {bay.Join.y:F3}, {bay.Join.z:F3}); laid {laid.Count}, now {now.Count}; kept {c.kept}, moved {c.moved}, removed {c.removed}, added {c.added}");
            File.WriteAllLines(bay.File(dir, "diff.txt"), new[] { head }.Concat(diff));
            if (diff.Count > 0) changed++;
            Say(args, head.Substring(2));
        }
        Say(args, $"OK: RAILCAT_CAPTURE {bays.Count} bay(s), {changed} changed; tables in {dir}");
    }

    private static int Clear(string dir, bool all)
    {
        int removed = 0;
        foreach (LaidBay bay in ReadBays(dir))
            foreach (var (zdo, placed) in Standing(bay))
                if (all || placed.Laid)
                {
                    StructurePlacement.Destroy(zdo);
                    removed++;
                }
        return removed;
    }
}
