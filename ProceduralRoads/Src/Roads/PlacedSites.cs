using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// One placed place as the game placed it: its turn and root, the levelling its parts
/// replayed, its rocks, and the grid its approach was laid out over. Everything
/// <see cref="PlacedSiteGround"/> and <see cref="PlacedSiteShape"/> need to rebuild its
/// ground and landing offline, from the natural ground of a world dump (
/// the turn is drawn from the game's unseeded Random when the zone is
/// placed, so only a run can say it).
/// </summary>
public sealed class PlacedSiteRecord
{
    public string Prefab = "";
    public Vector2 Centre;
    public float Yaw, Root, RockRadius;
    public bool KeepSmallRocks;
    public List<LevelOp> Ops = new();
    public List<SiteRock> Rocks = new();
    /// <summary>The ground grid the approach was laid out over (vertices, row by row in z).</summary>
    public int X0, Z0, W, H;

    /// <summary>The place's ground over its grid, rebuilt from <paramref name="natural"/>
    /// (the terrain before anything shaped it) and its own levelling; no road earthwork.</summary>
    public float[] Ground(Func<int, int, float> natural) =>
        PlacedSiteGround.Build(X0, Z0, W, H, Centre, Root, Yaw, Ops, natural);
}

/// <summary>
/// A place placed before its roads were generated, ready for them: its record's ground rebuilt
/// over its grid and its landing worked out. Built on the main thread (<see cref="PlacedSites.Prepare"/>)
/// and only read after: the island workers share it.
/// </summary>
public sealed class PlacedSite
{
    public PlacedSiteRecord Record = null!;
    public Vector2 Centre => Record.Centre;
    /// <summary>The ground the placed place leaves (<see cref="PlacedSiteGround"/>).</summary>
    public Func<float, float, float> Ground = (_, _) => 0f;
    public PlacedSiteShape Shape = null!;
}

/// <summary>
/// The places placed this session (their approaches' jobs remember them, and the places placed
/// before the roads), written by road_placed_sites_dump and read back by offline tools.
/// Placed before the roads, a place is reached by its road all the way onto its real
/// landing (the sites are placed first and the roads built to their real landings; before, 14 of 36
/// approaches were given up as "too far": the road ended at the
/// outskirts on whichever side it came in, 80 m from Mistlands_DvergrTownEntrance2's landing at
/// -4160,-4672). Its road is aimed at the ring nearest its landing (<see cref="Aim"/>) and carried
/// on onto the landing as part of the same road (<see cref="RoadNetworkGenerator"/>).
/// </summary>
public static class PlacedSites
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), PlacedSiteRecord> s_known = new();

    private static (int, int) Key(Vector2 centre) => (Mathf.RoundToInt(centre.x), Mathf.RoundToInt(centre.y));

    public static void Remember(PlacedSiteRecord record) => s_known[Key(record.Centre)] = record;
    public static bool TryGet(Vector2 centre, out PlacedSiteRecord record) => s_known.TryGetValue(Key(centre), out record!);
    public static IReadOnlyList<PlacedSiteRecord> All => s_known.Values.OrderBy(r => r.Centre.x).ThenBy(r => r.Centre.y).ToList();
    public static int Count => s_known.Count;
    public static void Reset()
    {
        s_known.Clear();
        s_ready.Clear();
        s_landed.Clear();
    }

    /// <summary>The places ready for the roads, by centre: <see cref="Prepare"/>d from the records.</summary>
    private static readonly Dictionary<(int, int), PlacedSite> s_ready = new();

    /// <summary>
    /// Every remembered place made ready for the roads: its ground rebuilt from <paramref name="natural"/>
    /// over its grid with its own levelling, and its landing from its flat. Main thread, before the
    /// islands are generated; a place whose levelling has no flat is left out (no landing to go to).
    /// </summary>
    public static void Prepare(Func<float, float, float> natural)
    {
        s_ready.Clear();
        foreach (PlacedSiteRecord r in s_known.Values) PrepareOne(r, natural);
    }

    /// <summary><see cref="Prepare"/> as a road generation's preparation steps: one place a step, counted on
    /// its own stage of the loading panel.</summary>
    internal static IEnumerable<bool> PrepareSteps(Func<float, float, float> natural, RoadPreparation preparation)
    {
        s_ready.Clear();
        var records = s_known.Values.ToList();
        if (records.Count == 0) yield break;
        preparation.BeginStage("Rebuilding placed places' ground", records.Count, "places"); yield return true;
        foreach (PlacedSiteRecord r in records)
        {
            PrepareOne(r, natural);
            preparation.Checkpoint(); yield return true;
        }
    }

    private static void PrepareOne(PlacedSiteRecord r, Func<float, float, float> natural)
    {
        LevelOp? flat = RoadStairRuns.FlatOf(r.Ops);
        if (flat == null || r.W <= 1 || r.H <= 1) return;
        Func<float, float, float> grid = PlacedSiteGround.Sampler(r.Ground((x, z) => natural(x, z)), r.X0, r.Z0, r.W, r.H);
        // Past its grid the place has shaped nothing: the natural ground, where a way onto its landing
        // that starts further back along its road (RoadNetworkGenerator.PlanEntry) begins.
        Func<float, float, float> ground = (x, z) => grid(x, z) is float h && h < PlacedSiteGround.OffGrid ? h : natural(x, z);
        s_ready[Key(r.Centre)] = new PlacedSite
        {
            Record = r, Ground = ground,
            Shape = PlacedSiteShape.Of(r.Centre, r.Yaw, flat.Value, r.RockRadius, r.Rocks, ground, r.KeepSmallRocks, r.Root, r.Prefab),
        };
    }

    /// <summary>Register a place ready for the roads as it is (tests).</summary>
    internal static void Ready(PlacedSite site) => s_ready[Key(site.Centre)] = site;

    /// <summary>The place at <paramref name="centre"/>, if it was placed before its roads and made ready for them.</summary>
    public static bool TryGetReady(Vector2 centre, out PlacedSite site)
    {
        site = null!;
        return s_ready.TryGetValue(Key(centre), out site!);
    }

    public static int ReadyCount => s_ready.Count;

    /// <summary>The ground a place placed before its roads leaves at <paramref name="p"/> in its footprint (its preload,
    /// <see cref="PlacedSite.Ground"/>): the one reader for the terrain writer (<see cref="RoadTerrainModifier.PlacedBaseAt"/>)
    /// and the finished-ground predictor (<see cref="RoadTerrainModifier.FinishedGround"/>). Null outside a footprint or
    /// where the place was not placed first.</summary>
    public static float? GroundAt(Vector2 p) =>
        RoadSiteProtection.NearestFootprint(p, 0f, out RoadSiteProtection.Footprint f) && TryGetReady(f.Centre, out PlacedSite site)
            ? site.Ground(p.x, p.y) : null;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), byte> s_landed = new();

    /// <summary>A road or staircase was carried on onto the place's landing (one of the level landings in
    /// front of its mouth, <see cref="PlacedSiteShape.Landings"/>): it is reached, wherever on its landing that
    /// ended. Its pieces may stop short of the shape's own landing point, the flight's foot then walked over the
    /// level ground (the staircase onto a Dvergr entrance's landing at 5440,4800 ended
    /// on it, its last piece 9.0 m from the shape's point; a frost cave's 9.3 m).</summary>
    public static void MarkLanded(Vector2 centre) => s_landed[Key(centre)] = 0;
    /// <summary>Forget which places the network lands on: with the network they were landed by. The places
    /// placed stay: they are the world's.</summary>
    public static void ForgetLanded() => s_landed.Clear();
    public static bool Landed(Vector2 centre) => s_landed.ContainsKey(Key(centre));

    /// <summary>
    /// HARD RULE: a place the game leaves for a player to find (unique, or shown on the map once placed or always) is
    /// never a destination, so it is never placed or reached by the mod. The one owner is
    /// <see cref="RoadNetworkGenerator"/>'s location gathering, which leaves such a place out of every destination list.
    /// </summary>
    /// <remarks>Not Hildir's quest sites while roads go to them (<see cref="RoadNetworkOptions.HildirQuestSites"/>): placed
    /// first like the other caves, their map icons held back until a player comes near (<see cref="HiddenSiteIcons"/>).</remarks>
    public static bool PlayerDecides(string prefab, bool unique, bool iconPlaced, bool iconAlways) =>
        !RoadNetworkGenerator.HildirRoads(prefab) && (unique || iconPlaced || iconAlways);

    /// <summary>
    /// Whether a place's landing is only known once it is placed, so it is placed before its roads:
    /// turned by the game at placement (to face downhill, or at random) with its flat off its centre,
    /// so the flat moves with the turn. The Mistlands' and the mountains' places and the caves (the terrain
    /// there is hard to navigate); a place turned at random elsewhere, swamp crypts included, keeps the
    /// outskirts end.
    /// </summary>
    public static bool NeedsPlacing(bool slopeRotation, bool randomRotation, Heightmap.Biome biome, IReadOnlyList<LevelOp>? ops)
    {
        if (ops == null || ops.Count == 0) return false;
        LevelOp? flat = RoadStairRuns.FlatOf(ops);
        if (flat == null || flat.Value.Concentric) return false;
        if (slopeRotation) return true;
        return randomRotation && (biome == Heightmap.Biome.Mountain || biome == Heightmap.Biome.Mistlands);
    }

    /// <summary>How much further out than the road's trim ring the search's end is put, so the trim keeps it.</summary>
    public const float AimOut = 0.5f;
    /// <summary>The turns tried either side of the landing's bearing where the point there is no road ground,
    /// round to the place's back: a landing facing the sea is come to along the shore (at -6016,4736).</summary>
    public const float AimStep = 22.5f;
    public const int AimSteps = 8;
    /// <summary>What a metre of height between a ring point's ground and the landing's costs, in metres round the
    /// ring: the way on from the ring point climbs it as a metre of staircase or up to three of road (the grade
    /// cap), where round the ring the road keeps its level.</summary>
    public const float AimRise = 2f;

    /// <summary>How many ring points a road to a placed place is aimed at in turn, the last the landing's bearing's
    /// (<see cref="Aims"/>): each one tried is the whole road searched and planned again.</summary>
    public const int AimTries = 3;

    /// <summary>
    /// Where a road to <paramref name="site"/> ends its search, in the order it is tried: points on its trim ring
    /// (<paramref name="ring"/> from the centre) where the ring is nearest the place's landing and its ground
    /// nearest the landing's level, so the road comes in on the landing's side at the level the way onto it starts
    /// from, whichever way it arrives; last the first road ground round the ring from the landing's bearing, by
    /// bearing alone. Each road ground point round the ring is priced by how far round from the landing's bearing
    /// it is and how far above or below the landing its ground is (<see cref="AimRise"/>); those cheaper than the
    /// bearing's point come first, cheapest first, the landing's bearing first on a tie, up to
    /// <see cref="AimTries"/> in all. Searched to the centre, a road came in from behind and ended 80 m from the
    /// landing; aimed by bearing alone at a frost cave on a ledge, it went down a staircase to a bench 38 m under
    /// the landing and the way onto the landing climbed back up. Aimed by level alone, a Hildir frost cave (-2304,1536)
    /// the bearing's point reached was lost, so the bearing's point is always tried last.
    /// </summary>
    /// <param name="turn">Degrees round the ring from the landing's bearing, for a road that cannot get to that side:
    /// aimed by bearing alone, the one point (priced by level, every turn would come back to the same level point).</param>
    public static IReadOnlyList<Vector2> Aims(PlacedSite site, float ring, Func<Vector2, bool> isGround, float turn = 0f)
    {
        Vector2 toward = site.Shape.Landing - site.Centre;
        if (toward.sqrMagnitude < 1e-4f) toward = site.Shape.Front;
        float bearing = Mathf.Atan2(toward.x, toward.y) * Mathf.Rad2Deg + turn;
        float r = ring + AimOut;
        float level = site.Ground(site.Shape.Landing.x, site.Shape.Landing.y);
        Vector2 At(float b) => site.Centre + new Vector2(Mathf.Sin(b * Mathf.Deg2Rad), Mathf.Cos(b * Mathf.Deg2Rad)) * r;
        // Round the ring from the bearing: the first road ground is the bearing's point, priced first.
        var priced = new List<(Vector2 p, float cost)>();
        for (int i = 0; i <= AimSteps; i++)
        {
            float round = i * AimStep * Mathf.Deg2Rad * r;
            if (priced.Count > 0 && (turn != 0f || round >= priced[0].cost)) break;
            foreach (int side in i == 0 ? new[] { 0 } : new[] { 1, -1 })
            {
                Vector2 p = At(bearing + side * i * AimStep);
                if (!isGround(p)) continue;
                priced.Add((p, round + AimRise * Mathf.Abs(site.Ground(p.x, p.y) - level)));
                if (turn != 0f) break;
            }
        }
        if (priced.Count == 0) return new[] { At(bearing) };
        var aims = priced.Where(c => c.cost < priced[0].cost).OrderBy(c => c.cost).Take(AimTries - 1).Select(c => c.p).ToList();
        aims.Add(priced[0].p);
        return aims;
    }
    public static void Load(IEnumerable<PlacedSiteRecord> records) { foreach (var r in records) Remember(r); }

    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static float P(string s) => s.Length == 0 ? float.NaN : float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>
    /// One block per place: a "site" row, then its "op" rows, then a "rock" row per rock, each
    /// followed by a "top" row of its real top over its grid (empty cells: nothing of it stands
    /// there). Floats round-trip exactly.
    /// </summary>
    public static string Write(IEnumerable<PlacedSiteRecord> records)
    {
        var sb = new StringBuilder("# placed sites: site,prefab,cx,cz,yaw,root,rockRadius,keepSmallRocks,x0,z0,w,h | op,offsetX,offsetZ,height,radius,square,smoothRadius,smoothPower,sortOrder | rock,name,x,z,halfX,halfZ,yaw,minY,maxY,topX0,topZ0,topW | top,v;v;...\n");
        foreach (PlacedSiteRecord r in records)
        {
            sb.Append("site,").Append(r.Prefab.Replace(',', '_')).Append(',').Append(F(r.Centre.x)).Append(',').Append(F(r.Centre.y)).Append(',')
              .Append(F(r.Yaw)).Append(',').Append(F(r.Root)).Append(',').Append(F(r.RockRadius)).Append(',').Append(r.KeepSmallRocks ? 1 : 0).Append(',')
              .Append(r.X0).Append(',').Append(r.Z0).Append(',').Append(r.W).Append(',').Append(r.H).Append('\n');
            foreach (LevelOp op in r.Ops)
                sb.Append("op,").Append(F(op.OffsetX)).Append(',').Append(F(op.OffsetZ)).Append(',').Append(F(op.Height)).Append(',').Append(F(op.Radius)).Append(',')
                  .Append(op.Square ? 1 : 0).Append(',').Append(F(op.SmoothRadius)).Append(',').Append(F(op.SmoothPower)).Append(',').Append(op.SortOrder).Append('\n');
            foreach (SiteRock k in r.Rocks)
            {
                sb.Append("rock,").Append(k.Name.Replace(',', '_')).Append(',').Append(F(k.At.x)).Append(',').Append(F(k.At.y)).Append(',').Append(F(k.HalfX)).Append(',')
                  .Append(F(k.HalfZ)).Append(',').Append(F(k.Yaw)).Append(',').Append(F(k.MinY)).Append(',').Append(F(k.MaxY)).Append(',')
                  .Append(k.TopX0).Append(',').Append(k.TopZ0).Append(',').Append(k.TopW).Append('\n');
                sb.Append("top,");
                if (k.Top != null)
                    for (int i = 0; i < k.Top.Length; i++) sb.Append(i == 0 ? "" : ";").Append(float.IsNaN(k.Top[i]) ? "" : F(k.Top[i]));
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    public static List<PlacedSiteRecord> Read(string text)
    {
        var records = new List<PlacedSiteRecord>();
        PlacedSiteRecord? current = null;
        string[]? rock = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            string[] c = line.Split(',');
            switch (c[0])
            {
                case "site":
                    current = new PlacedSiteRecord
                    {
                        Prefab = c[1], Centre = new Vector2(P(c[2]), P(c[3])), Yaw = P(c[4]), Root = P(c[5]), RockRadius = P(c[6]),
                        KeepSmallRocks = c[7] == "1", X0 = int.Parse(c[8], CultureInfo.InvariantCulture), Z0 = int.Parse(c[9], CultureInfo.InvariantCulture),
                        W = int.Parse(c[10], CultureInfo.InvariantCulture), H = int.Parse(c[11], CultureInfo.InvariantCulture),
                    };
                    records.Add(current);
                    break;
                case "op":
                    current!.Ops.Add(new LevelOp(P(c[1]), P(c[2]), P(c[3]), P(c[4]), c[5] == "1", P(c[6]), P(c[7]), int.Parse(c[8], CultureInfo.InvariantCulture)));
                    break;
                case "rock":
                    rock = c;
                    break;
                case "top":
                    string[] k = rock ?? throw new FormatException("a top row with no rock row before it");
                    string values = line.Substring(4);
                    float[]? top = values.Length == 0 ? null : values.Split(';').Select(P).ToArray();
                    current!.Rocks.Add(new SiteRock(new Vector2(P(k[2]), P(k[3])), P(k[4]), P(k[5]), P(k[6]), P(k[7]), P(k[8]), k[1], top,
                        int.Parse(k[9], CultureInfo.InvariantCulture), int.Parse(k[10], CultureInfo.InvariantCulture), int.Parse(k[11], CultureInfo.InvariantCulture)));
                    rock = null;
                    break;
                default:
                    throw new FormatException($"unknown row kind '{c[0]}'");
            }
        }
        return records;
    }
}
