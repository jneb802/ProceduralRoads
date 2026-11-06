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
    /// <summary>The ground a placed place leaves at its landing: its level.</summary>
    public float LandingHeight => Ground(Shape.Landing.x, Shape.Landing.y);
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
        s_whyNotPlaced.Clear();
        ForgetNotPlaced();
        RoadStairRuns.ForgetGround();
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
        RoadStairRuns.ForgetGround();
        foreach (PlacedSiteRecord r in s_known.Values) PrepareOne(r, natural);
    }

    /// <summary><see cref="Prepare"/> as a road generation's preparation steps: one place a step, counted on
    /// its own stage of the loading panel.</summary>
    internal static IEnumerable<bool> PrepareSteps(Func<float, float, float> natural, RoadPreparation preparation)
    {
        s_ready.Clear();
        RoadStairRuns.ForgetGround();
        var records = s_known.Values.ToList();
        if (records.Count == 0) yield break;
        preparation.BeginStage("Rebuilding placed places' ground", records.Count, "places"); yield return true;
        var unready = new List<PlacedSiteRecord>();
        foreach (PlacedSiteRecord r in records)
        {
            if (!PrepareOne(r, natural)) unready.Add(r);
            preparation.Checkpoint(); yield return true;
        }
        // Placed, but with no landing to go onto: a place with no flat whose smoothing leaves no level ground beside it
        // (PlacedSiteShape.OnLevelGround: a Mountain cabin on a steep slope, which only smooths round its centre) is
        // reached at its ring like a place not placed first, level with its ground at its edge (FindNotPlaced,
        // RoadNetworkGenerator.ArrivalHeight), on the level landing a place's end gets (RoadNetworkGenerator.ApproachGround);
        // one with a flat but no ground grid gets no road (NotPlaced).
        if (unready.Count > 0)
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(FormattableString.Invariant(
                $"[SITES] {unready.Count} placed place(s) with no landing to go onto (no flat among the levelling it kept, nor level ground its smoothing leaves: reached at its ring; or no ground grid): ") +
                string.Join(", ", unready.Select(r => FormattableString.Invariant($"{r.Prefab} at ({r.Centre.x:F0}, {r.Centre.y:F0})"))));
    }

    /// <summary>One record made ready; false where it has no landing to go onto (no flat nor level ground its smoothing
    /// leaves, or no grid).</summary>
    private static bool PrepareOne(PlacedSiteRecord r, Func<float, float, float> natural)
    {
        // A place with a door lands its road in front of it, flat or not (PlacedSiteShape.DoorLandings).
        LevelOp? flat = RoadStairRuns.FlatOf(r.Ops) ?? (PlacedSiteShape.HasDoor(r.Prefab) ? PlacedSiteShape.DoorFlat : null);
        if ((flat == null && !SmoothsItsGround(r.Ops)) || r.W <= 1 || r.H <= 1) return false;
        Func<float, float, float> grid = PlacedSiteGround.Sampler(r.Ground((x, z) => natural(x, z)), r.X0, r.Z0, r.W, r.H);
        // Past its grid the place has shaped nothing: the natural ground, where a way onto its landing
        // that starts further back along its road (RoadNetworkGenerator.PlanEntry) begins.
        Func<float, float, float> ground = (x, z) => grid(x, z) is float h && h < PlacedSiteGround.OffGrid ? h : natural(x, z);
        // No flat: landed on the level ground its smoothing leaves beside it, if any (PlacedSiteShape.OnLevelGround).
        PlacedSiteShape? shape = flat is LevelOp f
            ? PlacedSiteShape.Of(r.Centre, r.Yaw, f, r.RockRadius, r.Rocks, ground, r.KeepSmallRocks, r.Root, r.Prefab)
            : PlacedSiteShape.OnLevelGround(r.Centre, r.Yaw, r.RockRadius, r.Rocks, ground, r.KeepSmallRocks, r.Root, r.Prefab);
        if (shape == null) return false;
        s_ready[Key(r.Centre)] = new PlacedSite { Record = r, Ground = ground, Shape = shape };
        RoadStairRuns.ForgetGround();
        return true;
    }

    /// <summary>Whether a place with no flat smooths the ground round it, so may leave level ground to land on
    /// (<see cref="PlacedSiteShape.OnLevelGround"/>); its own pieces are then read with its boulders (SitesFirstLoad).</summary>
    internal static bool SmoothsItsGround(IReadOnlyList<LevelOp> ops) => ops.Any(op => op.SmoothRadius > 0f);

    /// <summary>Register a place ready for the roads as it is (tests).</summary>
    internal static void Ready(PlacedSite site)
    {
        s_ready[Key(site.Centre)] = site;
        RoadStairRuns.ForgetGround();
    }

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

    /// <summary>
    /// The ground as places will leave it (one owner, which every staircase planner reads): in the footprint of a place placed
    /// before its roads the ground it levels (<see cref="GroundAt(Vector2)"/>), anywhere else the natural ground. A road's
    /// staircases and the staircases onto a placed place's landing are laid out on it (through
    /// <see cref="RoadStairRuns.GroundAt"/>, a lattice of it: <see cref="RoadStairRuns.PlanningGround(StairRun, WorldGenerator)"/>)
    /// and a staircase's kit chooses its supports on it (<see cref="StairStructures.Dresser"/>): on the natural ground alone,
    /// a staircase up to a landing a place raises over the slope stays under it, and the game breaks the pieces it buries.
    /// A placed place's landing, doors and rock are worked out on its preload directly (<see cref="PlacedSite.Ground"/>),
    /// which runs on past the footprint's edge over the place's grid, where the place shapes nothing and it is the natural
    /// ground at the lattice's metres. A place the game places after its roads has no ground here until it is placed
    /// (<see cref="ExpectedGroundAt"/>).
    /// </summary>
    public static float GroundAt(float x, float z, WorldGenerator world) =>
        GroundAt(new Vector2(x, z)) ?? BiomeBlendedHeight.GetBlendedHeight(x, z, world);

    /// <summary>
    /// The ground a place the game places after its roads will leave at <paramref name="p"/>, for the staircase onto the
    /// flat round its centre (<see cref="RoadStairRuns.LaySiteApproach"/>): <paramref name="ground"/>, the ground as the
    /// places placed first leave it (<see cref="GroundAt(float, float, WorldGenerator)"/>), levelled by the place's
    /// operations round its centre from <paramref name="centreGround"/> (<see cref="LocationLevelling.PlacedGroundAt"/>).
    /// The owner's second source, for a place with no preload, read only where it is exact: every operation round the
    /// centre, so the same whichever way the place is turned. A place with any off its centre is placed first
    /// (<see cref="NeedsPlacing(string, bool, bool, Heightmap.Biome, IReadOnlyList{LevelOp}?)"/>), or, not placed, gets
    /// no road (<see cref="NotPlaced"/>): left out, the corner levellings of the
    /// StoneTowerRuins05 put this up to 5 m under the ground the game left 10 to 14 m from its centre.
    /// </summary>
    public static float ExpectedGroundAt(Vector2 p, Vector2 centre, float centreGround, IReadOnlyList<LevelOp>? ops, float ground) =>
        LocationLevelling.PlacedGroundAt(p, centre, centreGround, ops, ground);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), byte> s_landed = new();

    /// <summary>A road or staircase was carried on onto the place's landing (one of the level landings in
    /// front of its mouth, <see cref="PlacedSiteShape.Landings"/>): it is reached, wherever on its landing that
    /// ended. Its pieces may stop short of the shape's own landing point, the flight's foot then walked over the
    /// level ground (the staircase onto a Dvergr entrance's landing at 5440,4800 ended
    /// on it, its last piece 9.0 m from the shape's point; a frost cave's 9.3 m).</summary>
    public static void MarkLanded(Vector2 centre)
    {
        s_landed[Key(centre)] = 0;
        RoadNetworkGenerator.IslandNetworkChanged();
    }
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
    /// Whether a place is placed before its roads (the one owner): where the ground it leaves is only known once it is
    /// placed, and the network comes close to it. Every place in the Mountains (the Mountains are too varied to
    /// guess): a road there is reached only level with the place (<see cref="RoadNetworkGenerator.Reached"/>), and only the
    /// placing says how the game stood it on its slope. Predicted, a Mountain cabin's road ended 14 m out and 10 m under it
    /// (AbandonedLogCabin04, -1867,7101: it only smooths round its centre, so it was never placed, and with no flat its
    /// height was not known and any road within reach counted it reached). Elsewhere, known only once placed: the game turns it
    /// as it places it (to face downhill, or at random) and some of its levelling stands off its centre (<see cref="TurnsItsGround"/>),
    /// so turns with it: its flat (a frost cave's floor, dug behind its mouth) or any other levelling or smoothing round it. A
    /// Mountain tower levels a flat 8 m round its centre and four corners and a doorstep that raise its landing out to about
    /// 15 m: predicted from the levelling round its centre alone, the staircase onto it stood up to 5 m under the ground the
    /// game left, and the game broke the stairs it buried. Placed first, its staircase is laid on its real ground
    /// (<see cref="GroundAt(float, float, WorldGenerator)"/>). It needs a flat, a landing for its road to be carried onto.
    /// Come close to: one that turns to face downhill (the caves, the Mistlands' entrances, towers and digs), any in the
    /// Mistlands (hard ground, a staircase where a road cannot get there), or a boss's entrance (reached only on its own
    /// landing, <see cref="RoadNetworkGenerator.OwnLandingOnly"/>), and a place with a door, wherever it is: a crypt's road
    /// ends in front of its door (<see cref="PlacedSiteShape.DoorLandings"/>), which turns with it. Placing costs loading
    /// time, so not every place elsewhere: one turned at random elsewhere keeps the outskirts end. The load places every one, however long that takes (the
    /// player can cancel it); one that still was not placed before its roads gets none (<see cref="NotPlaced"/>).
    /// </summary>
    public static bool NeedsPlacing(string prefab, bool slopeRotation, bool randomRotation, Heightmap.Biome biome, IReadOnlyList<LevelOp>? ops) =>
        NeedsPlacing(prefab, slopeRotation, randomRotation, () => biome, () => ops);

    /// <summary><see cref="NeedsPlacing(string, bool, bool, Heightmap.Biome, IReadOnlyList{LevelOp}?)"/> for a location as the
    /// world lists it: the list the load places first (SitesFirstLoad) and the list <see cref="NotPlaced"/> leaves
    /// without a road are this one.</summary>
    public static bool NeedsPlacing(ZoneSystem.LocationInstance instance, WorldGenerator world)
    {
        Vector3 at = instance.m_position;
        return NeedsPlacing(instance.m_location.m_prefab.Name, instance.m_location.m_slopeRotation, instance.m_location.m_randomRotation,
            () => world.GetBiome(at.x, at.z), () => LocationLevelling.OpsAt(new Vector2(at.x, at.z)));
    }

    /// <summary>The rule, its cheap tests first: the biome is noise and a place's levelling a search of the world's
    /// locations, so each is read only once the place's turn and name have not settled it.</summary>
    private static bool NeedsPlacing(string prefab, bool slopeRotation, bool randomRotation, Func<Heightmap.Biome> biome,
        Func<IReadOnlyList<LevelOp>?> levelling)
    {
        // A place with a door (a crypt): its road ends in front of the door, which turns with it.
        if (PlacedSiteShape.HasDoor(prefab) && (slopeRotation || randomRotation)) return true;
        Heightmap.Biome here = biome();
        if (here == Heightmap.Biome.Mountain) return true;
        if (!(slopeRotation || randomRotation)) return false;
        if (!slopeRotation && !RoadNetworkGenerator.OwnLandingOnly(prefab) && here != Heightmap.Biome.Mistlands)
            return false;
        IReadOnlyList<LevelOp>? ops = levelling();
        return ops != null && RoadStairRuns.FlatOf(ops) != null && TurnsItsGround(ops);
    }

    /// <summary>The places this generation gives no road (<see cref="NotPlaced"/>), by centre: found once, on the main
    /// thread (<see cref="FindNotPlaced"/>), and only read after, by the island workers.</summary>
    private static volatile Dictionary<(int, int), string> s_notPlaced = new();

    /// <summary>
    /// Which of a generation's destinations get no road (<see cref="NotPlaced"/>): each that waits for its placing
    /// (<see cref="NeedsPlacing(ZoneSystem.LocationInstance, WorldGenerator)"/>) and that was not placed before its roads and
    /// made ready for them (<see cref="TryGetReady"/>). A place placed with no flat (<see cref="FlatlessPlaced"/>) is not one:
    /// it has no landing to wait for, and is reached at its ring, level with the ground the game stood it on. Main thread,
    /// once the generation's placing is done (RoadNetworkGenerator.PrepareSteps): the world's location list is read here,
    /// never by an island worker.
    /// </summary>
    internal static void FindNotPlaced(IEnumerable<Vector2> destinations, WorldGenerator world)
    {
        var notPlaced = new Dictionary<(int, int), string>();
        if (ZoneSystem.instance != null)
            foreach (Vector2 centre in destinations)
            {
                if (TryGetReady(centre, out _) || FlatlessPlaced(centre)) continue;
                Vector2s zone = ZoneSystem.GetZone(new Vector3(centre.x, 0f, centre.y));
                if (ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance instance)
                    && new Vector2(instance.m_position.x - centre.x, instance.m_position.z - centre.y).sqrMagnitude <= 1f
                    && NeedsPlacing(instance, world))
                    notPlaced[Key(centre)] = s_whyNotPlaced.TryGetValue(Key(centre), out string? noted) ? noted
                        : TryGet(centre, out _) ? "placed, but with no landing to go onto"
                        : NetworkCutoff.Excludes(zone) ? ZoneLeftAlone
                        : "not placed before its roads";
            }
        s_notPlaced = notPlaced;
        s_whyNotPlaced.Clear();
    }

    /// <summary>Why a place the generation's placing went to was not made ready, as the placing knew it
    /// (<see cref="NoteWhyNotPlaced"/>): read once by <see cref="FindNotPlaced"/>. Main thread.</summary>
    private static readonly Dictionary<(int, int), string> s_whyNotPlaced = new();

    /// <summary>What a place in a zone explored before the network is told: the game placed it, the mod leaves it as it is.</summary>
    internal const string ZoneLeftAlone = "its zone was explored before the network and is left as it is, so its landing is not read";

    /// <summary>The placing (SitesFirstLoad) says why the place at <paramref name="centre"/> will not be ready: placed but not
    /// readable, its zone left alone or generated without it found. Main thread.</summary>
    internal static void NoteWhyNotPlaced(Vector2 centre, string why) => s_whyNotPlaced[Key(centre)] = why;

    /// <summary>The generation is over: no place is left out by <see cref="NotPlaced"/> until the next one finds its own.</summary>
    internal static void ForgetNotPlaced() => s_notPlaced = new Dictionary<(int, int), string>();

    /// <summary>
    /// Whether the place at <paramref name="centre"/> is left without a road (the one owner of "a place that waits for its
    /// placing is visited only if placed"): one of the generation's destinations that waits for its placing and that was not
    /// placed before its roads and made ready for them (<see cref="FindNotPlaced"/>): its zone was explored before the network
    /// (NetworkCutoff: nothing is placed there), its zone was generated but no placing of it was found, or what its placing
    /// settled could not be read. The load places every one however long that takes (no cap: the player has the
    /// loading panel's Cancel). Its landing is not known, so no road is aimed at its ring and no staircase is predicted onto
    /// it: no road goes to it (RoadNetworkGenerator.GenerateIslandRoads) and the network does not reach it
    /// (RoadNetworkGenerator.Reached). Outside a generation no place is.
    /// </summary>
    public static bool NotPlaced(Vector2 centre) => s_notPlaced.ContainsKey(Key(centre));

    /// <summary>Whether the place at <paramref name="centre"/> was placed before its roads with no flat to land on (a Mountain
    /// cabin smooths round its centre and levels nothing) and no level ground beside it (<see cref="PlacedSiteShape.OnLevelGround"/>): not made ready (<see cref="TryGetReady"/>), it is reached at its
    /// ring as a place not placed first, level with its ground at its edge on the road's side
    /// (RoadNetworkGenerator.ArrivalHeight).</summary>
    public static bool FlatlessPlaced(Vector2 centre) =>
        !TryGetReady(centre, out _) && TryGet(centre, out PlacedSiteRecord record) && RoadStairRuns.FlatOf(record.Ops) == null
        && !PlacedSiteShape.HasDoor(record.Prefab);

    /// <summary>Why <see cref="NotPlaced"/> leaves the place at <paramref name="centre"/> without a road, or null.</summary>
    internal static string? NotPlacedWhy(Vector2 centre) => s_notPlaced.TryGetValue(Key(centre), out string? why) ? why : null;

    /// <summary>Whether any of a place's levelling or smoothing stands off its centre (<see cref="LevelOp.Concentric"/>), so
    /// lies wherever the game turns it: the ground it leaves is then known only once it is placed.</summary>
    public static bool TurnsItsGround(IReadOnlyList<LevelOp>? ops) => ops != null && ops.Any(op => !op.Concentric);

    /// <summary>How much further out than the road's trim ring the search's end is put, so the trim keeps it.</summary>
    public const float AimOut = 0.5f;
    /// <summary>The angle between a place's doors round its ring (RoadNetworkGenerator.DoorsOf), from its landing's
    /// bearing round to its back: a landing facing the sea is come to along the shore (at -6016,4736).</summary>
    public const float AimStep = 22.5f;
    /// <summary>What a metre of height between a door's ground and the landing's costs, in metres round the
    /// ring: the way on from the ring point climbs it as a metre of staircase or up to three of road (the grade
    /// cap), where round the ring the road keeps its level.</summary>
    public const float AimRise = 2f;

    public static void Load(IEnumerable<PlacedSiteRecord> records) { foreach (var r in records) Remember(r); }

    private const int Format = 1;

    /// <summary>
    /// The places placed before their roads, saved with the network: format 1, then their records as <see cref="Write"/>
    /// writes them. A load remembers them again (<see cref="Restore"/>) and the generator makes them ready
    /// (<see cref="Prepare"/>), so a staircase whose first zone is built after a restart is dressed on the ground it would
    /// have been dressed on in the session that generated it, and the ground in their footprints is known again.
    /// </summary>
    public static byte[] Serialize()
    {
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(stream);
        writer.Write(Format);
        writer.Write(Write(All));
        return stream.ToArray();
    }

    /// <summary>Replace the remembered places with saved ones (<see cref="Serialize"/>); none saved (a network saved before
    /// they were) leaves none. Not yet ready for the roads: <see cref="Prepare"/> makes them so. A damaged record throws and
    /// changes nothing.</summary>
    public static void Restore(byte[]? data)
    {
        var records = new List<PlacedSiteRecord>();
        if (data != null && data.Length > 0)
        {
            using var reader = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
            int format = reader.ReadInt32();
            if (format != Format) throw new System.IO.InvalidDataException($"placed places format {format}");
            records = Read(reader.ReadString());
        }
        Reset();
        Load(records);
    }

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
