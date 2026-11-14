using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Which objects clip the road surface around a point, and - when asked to -
/// clear the natural boulders among them. The overlap test is the road's
/// clearance standing on the loaded road surface (a box of solid-paint width
/// and player height, <see cref="RockClearances"/>) against real collider
/// geometry, never a centre radius or a bounding box. Driven by
/// <see cref="RoadRockClearing"/>.
/// </summary>
public static class RoadRockProbe
{
    /// <summary>
    /// The automatic pass round <paramref name="centre"/>, the one owner of its options: applied, outside location areas, the
    /// Mistlands' rocks and cliffs as <see cref="RoadRockPolicy.PassClearsMistlandsRocks"/> and
    /// <see cref="RoadRockPolicy.PassClearsMistlandsCliffs"/> say, each rock carved
    /// (<see cref="RoadRockCarve"/>), an ore deposit like any other. The client's pass (RoadRockClearing) and the server's bake (ServerTerrainBake)
    /// both run it. <paramref name="deferCarves"/> and <paramref name="clearedCount"/>: as <see cref="Run"/>.
    /// The one owner of which rocks a road clears or carves (docs/OWNERS.md).
    /// </summary>
    public static List<string> RunPass(Vector3 centre, float radius, List<PendingCarve>? deferCarves, out int clearedCount) =>
        Run(centre, radius, mist: RoadRockPolicy.PassClearsMistlandsRocks, cliffs: RoadRockPolicy.PassClearsMistlandsCliffs,
            deferCarves, out clearedCount);

    private static HashSet<string>? s_natural;
    private static (ZoneSystem? zs, int count, bool mist, bool cliffs) s_naturalKey;

    /// <summary>The world's own vegetation that counts as a rock, built once per world and option set.</summary>
    private static HashSet<string> NaturalRocks(bool mist, bool cliffs)
    {
        var key = (ZoneSystem.instance, ZoneSystem.instance.m_vegetation.Count, mist, cliffs);
        if (s_natural != null && s_naturalKey.zs == key.instance && s_naturalKey.count == key.Count &&
            s_naturalKey.mist == mist && s_naturalKey.cliffs == cliffs) return s_natural;
        var natural = new HashSet<string>();
        foreach (var veg in ZoneSystem.instance.m_vegetation)
            if (veg.m_enable && veg.m_prefab != null && (RoadRockPolicy.IsNaturalBoulder(veg.m_prefab.name) ||
                    RoadRockPolicy.IsMistlandsRock(veg.m_prefab.name, mist, cliffs)))
                natural.Add(veg.m_prefab.name);
        s_naturalKey = (ZoneSystem.instance, ZoneSystem.instance.m_vegetation.Count, mist, cliffs);
        return s_natural = natural;
    }

    /// <summary>Reused by every overlap query: the probe asks one per road point.</summary>
    private static Collider[] s_overlap = new Collider[256];
    private static int s_candidateMask;

    /// <summary>
    /// Layers a rock can be on: everything but the ones that only ever hold
    /// terrain, water, characters, loose items, leaves or effects. Objects on
    /// those were never cleared (terrain and players were skipped outright,
    /// the rest judged "keep"), so leaving them out changes no verdict and
    /// saves judging them.
    /// </summary>
    private static int CandidateMask
    {
        get
        {
            if (s_candidateMask == 0)
                s_candidateMask = ~LayerMask.GetMask("terrain", "character", "character_net", "character_ghost", "character_noenv",
                    "Water", "WaterVolume", "item", "viewblock", "effect", "UI", "hitbox", "smoke", "weapon");
            return s_candidateMask;
        }
    }

    /// <summary>
    /// The probe, reporting how many objects were cleared (<paramref name="clearedCount"/>). A clearable rock loses only its
    /// chunks in the road (<see cref="RoadRockCarve"/>); a rock with no chunks is removed whole, an ore deposit too. A rock through
    /// a deck goes whole, whatever the road's rule (<see cref="RoadRockPolicy.HowTaken"/>). The item
    /// drops near the probe are counted before and after, and any rise is reported and logged as a warning.
    /// With the carves left for later: every rock the pass would carve goes into
    /// <paramref name="deferCarves"/> instead (<see cref="PendingCarve"/>), for the caller to apply over the following
    /// frames (<see cref="CarveDeferred"/>); whole removals and keeps are done now. Null: carved now, as before.
    /// <paramref name="clearedCount"/> then counts the rocks removed whole only.
    /// </summary>
    public static List<string> Run(Vector3 centre, float radius, bool mist, bool cliffs,
        List<PendingCarve>? deferCarves, out int clearedCount)
    {
        clearedCount = 0;
        long passStarted = RoadRockStats.Now;
        try { return RunTimed(centre, radius, mist, cliffs, deferCarves, out clearedCount); }
        finally { RoadRockStats.EndPass(passStarted); }
    }

    /// <summary>A rock a pass judged for carving, carved later (<see cref="CarveDeferred"/>): the rock and the pass's
    /// line for it.</summary>
    public readonly struct PendingCarve
    {
        public readonly GameObject Root;
        public readonly string Line;
        public PendingCarve(GameObject root, string line) { Root = root; Line = line; }
    }

    /// <summary>What became of a carve applied after its pass.</summary>
    public enum DeferredOutcome { Carved, RemovedWhole, Kept, Skipped }

    /// <summary>
    /// Apply one carve a pass left for later. The rock may have gone since (its zone unloaded: ZNetScene.RemoveObjects
    /// resets and destroys an instance whose sector left the area, between frames, never inside a carve), or passed to
    /// another peer: then it is skipped, untouched, and its zone is looked at again.
    /// </summary>
    public static DeferredOutcome CarveDeferred(PendingCarve pending)
    {
        GameObject root = pending.Root;
        if (root == null || !root.activeInHierarchy) return DeferredOutcome.Skipped;
        var view = root.GetComponent<ZNetView>();
        if (view == null || !view.IsValid() || !view.IsOwner()) return DeferredOutcome.Skipped;
        var (carved, removed, refused) = CarveOne(root, pending.Line, null);
        return carved > 0 ? DeferredOutcome.Carved : removed > 0 ? DeferredOutcome.RemovedWhole :
            refused ? DeferredOutcome.Skipped : DeferredOutcome.Kept;
    }

    /// <summary>How far round a pass's centre its item drops are counted: its reach plus the largest rock's.</summary>
    internal static float DropRadius(float radius) => radius + 30f;

    /// <summary>Road points the last pass skipped because their ground is not known (RoadRockCarve.GroundReader: inside a location whose ground is not read yet).</summary>
    internal static int LastGroundMisses;

    /// <summary>Kept rocks and rocks left untouched, already logged: the pass over loaded zones revisits a zone.</summary>
    private static readonly HashSet<string> s_reported = new HashSet<string>();

    /// <summary>A rock the pass judged and left: logged once, so a rock in the road is never silent.</summary>
    private static void LogOnce(string line)
    {
        if (s_reported.Count > 4096) s_reported.Clear();
        if (s_reported.Add(line)) Log(line);
    }

    private static List<string> RunTimed(Vector3 centre, float radius, bool mist, bool cliffs,
        List<PendingCarve>? deferCarves, out int clearedCount)
    {
        clearedCount = 0;
        LastGroundMisses = 0;
        LastNotOwner = 0;
        var lines = new List<string>();
        if (!RoadSpatialGrid.IsInitialized || ZoneSystem.instance == null || ZNetScene.instance == null)
        { lines.Add("Error: world or road network not available"); return lines; }

        var natural = NaturalRocks(mist, cliffs);

        var hits = new Dictionary<GameObject, RoadRockHit>();
        var otherZone = new HashSet<GameObject>();
        long lookup = RoadRockStats.Now;
        // The boxes a walker needs clear here, roads', decks' and staircases' alike: the one owner, which the carve reads too.
        var boxes = RockClearances.Near(centre, radius, new RoadRockCarve.GroundReader().At, out LastGroundMisses);
        RoadRockStats.LookupTicks += RoadRockStats.Now - lookup;
        var onDeck = new HashSet<GameObject>();
        RoadRockStats.Points += boxes.Count;
        int probed = boxes.Count;
        foreach (var box in boxes)
        {
            foreach (var root in Judge(box, mist, cliffs, natural, hits, otherZone, box.KeepsLocationRocks))
            {
                if (box.WholeOnly) onDeck.Add(root);
            }
        }

        string outcome = Apply(hits, onDeck, deferCarves, centre, radius, lines, out clearedCount);
        string reach = (LastGroundMisses > 0 ? $", {LastGroundMisses} on ground not known yet skipped" : "") +
            (otherZone.Count > 0 ? $"; {otherZone.Count} object(s) of other zones passed over" : "");
        lines.Insert(0, System.FormattableString.Invariant(
            $"Road rocks: {probed} road points probed within {radius:F0} m{reach}, {hits.Count} object(s) clip the road surface{outcome}"));
        return lines;
    }

    /// <summary>Whether a rock in a location area is kept on an approach to the location, as planning keeps it
    /// (<see cref="LocationRock.KeptAt"/>, the rock's box as the reader makes it): a large one
    /// (<see cref="LocationRock.LargeRock"/> or more across), any round its mouth (inside its exterior
    /// radius), or any at a
    /// location that keeps every rock (<see cref="LocationRock.KeepsEveryRock"/>: Hildir's cave). Small
    /// ones further out are cleared out of the way, and a boulder that is not one of the location's
    /// parts (<see cref="LocationPrefabLevelling.IsPart"/>) is any road's. <paramref name="why"/>:
    /// what made it the location's own, for the verdict.</summary>
    private static bool KeptOnApproach(GameObject root, out string why)
    {
        Vector3 at = root.transform.position;
        Location? location = Location.GetLocation(at, checkDungeons: false);
        why = "";
        // No location round it: not one of a location's parts, though it stands in a protected area that
        // reaches further than the location itself. Kept for being large, a 55 m rock1_mountain beside
        // a frost cave's approach was left whole over its steps (MountainCave02 -2560,1536:
        // rocks not part of the prefab are fair game to clear).
        if (location == null) return false;
        // A boulder of the world's in the location's footprint, not one of its parts: as on any road.
        bool? part = LocationPrefabLevelling.IsPart(Utils.GetPrefabName(location.gameObject), location.transform.position, location.transform.rotation, at);
        if (part == false) return false;
        string kind = part == true ? "one of its parts" : "its parts not known";
        // The box planning reads (a location's rocks saved with it, PlacedLocationReader.LocationRocks), of this rock as it stands, and
        // the one rule planning asks of it (LocationRock.KeptAt): the same size, the same middle.
        var view = root.GetComponent<ZNetView>();
        var zdo = view != null && view.IsValid() ? view.GetZDO() : null;
        // Its prefab as the rock pass names it (RoadRockCarve.Prefab, from its ZDO): a fractured rock's object is renamed.
        GameObject? prefab = RoadRockCarve.Prefab(root, view);
        if (prefab == null) return false;
        LocationRock box = LocationRock.Of(at, root.transform.rotation, (zdo != null ? PlacedLocationReader.SavedScale(zdo) : null) ?? Vector3.one,
            PlacedLocationReader.LocalBox(prefab), prefab.name);
        var centre = new Vector2(location.transform.position.x, location.transform.position.z);
        if (!box.KeptAt(centre, PlacedLocationReader.RockRadius(centre), PlacedLocationReader.KeepsEveryRock(centre), out string reason)) return false;
        why = kind + ", " + reason;
        return true;
    }

    /// <summary>Why a rock is kept on an approach to its location (<see cref="KeptOnApproach"/>), or null.</summary>
    private static string? KeptOnApproachWhy(GameObject root) => KeptOnApproach(root, out string why) ? why : null;
    private static readonly System.Func<GameObject, string?> s_keptOnApproachWhy = KeptOnApproachWhy;

    /// <summary>Objects the last run kept because another peer owns them: the zone is not done until they are judged by
    /// their owner.</summary>
    internal static int LastNotOwner;

    /// <summary>
    /// Which objects stand in one clearance box, each judged once (into
    /// <paramref name="hits"/>) by the clearing policy. Returns the objects the
    /// box touched, judged now or before, so a caller can note where.
    /// <paramref name="keepLocationRocks"/>: the box is an approach to a location (a staircase to its
    /// landing, a road onto it), and a rock in a location area is the location's own: kept, whatever
    /// another box decided.
    /// </summary>
    private static List<GameObject> Judge(RockClearance box, bool mist, bool cliffs,
        HashSet<string> natural, Dictionary<GameObject, RoadRockHit> hits, HashSet<GameObject> otherZone, bool keepLocationRocks = false)
    {
        var touchedHere = new List<GameObject>();
        long t = RoadRockStats.Now;
        int count = Physics.OverlapBoxNonAlloc(box.Centre, box.Half, s_overlap, box.Turn, CandidateMask, QueryTriggerInteraction.Ignore);
        if (count == s_overlap.Length)
        {
            // A full buffer may have dropped some: ask again with room for all.
            s_overlap = Physics.OverlapBox(box.Centre, box.Half, box.Turn, CandidateMask, QueryTriggerInteraction.Ignore);
            count = s_overlap.Length;
        }
        RoadRockStats.QueryTicks += RoadRockStats.Now - t;
        RoadRockStats.Queries++;
        RoadRockStats.Colliders += count;
        for (int c = 0; c < count; c++)
        {
            var collider = s_overlap[c];
            if (collider == null || collider.GetComponentInParent<Heightmap>() != null) continue;
            // The object a player would call "the rock": the nearest
            // ancestor with a network view, else the collider's root.
            var view = collider.GetComponentInParent<ZNetView>();
            GameObject root = view != null ? view.gameObject : collider.transform.root.gameObject;
            if (root.GetComponent<Player>() != null) continue;
            // Generating a zone for a remote peer: only that zone's own
            // objects. Several zones can be generated in one frame, and
            // the previous one's are only destroyed at the end of it, so
            // they are still here, and were found and cleared twice.
            if (RoadRockCarve.GhostSpawned != null && !RoadRockCarve.GhostSpawned.Contains(root)) { otherZone.Add(root); continue; }
            touchedHere.Add(root);
            if (hits.TryGetValue(root, out RoadRockHit before))
            {
                hits[root] = before.Revisited(keepLocationRocks, root, s_keptOnApproachWhy);
                continue;
            }
            // A rock any of whose body is in a zone explored before the road network is left
            // whole, even when a neighbouring zone's pass reaches it, and its root is over the border.
            Bounds body = new Bounds(root.transform.position, Vector3.zero);
            foreach (var part in root.GetComponentsInChildren<Collider>()) body.Encapsulate(part.bounds);
            if (!RoadRockPolicy.MayTouch(body.min, body.max))
            {
                hits[root] = new RoadRockHit(root.name, "keep: zone explored before the road network", 0f, false);
                continue;
            }
            long judging = RoadRockStats.Now;
            RoadRockStats.Judged++;
            // A fractured rock's GameObject is renamed by MineRock5.Awake
            // ("___MineRock5 m_meshFilter"), so read the prefab from the ZDO.
            string name = RoadRockCarve.PrefabName(root, view);
            // Two kinds of protection, kept apart: an object that BELONGS to a
            // location is never touched; an object merely standing inside a
            // location's protected area is kept unless the road is carved through it.
            bool locationChild = root.GetComponentInParent<Location>() != null;
            bool inLocationArea = false;
            float size = RoadRockCarve.SurvivingSize(root);
            foreach (var part in root.GetComponentsInChildren<Collider>())
            {
                var b = part.bounds;
                if (RoadLocationProtection.BlocksSegment(new Vector2(b.center.x, b.center.z), new Vector2(b.center.x, b.center.z),
                        new Vector2(b.extents.x, b.extents.z).magnitude, default)) inLocationArea = true;
            }
            bool piece = root.GetComponentInParent<Piece>() != null;
            // Carving takes only what stands in the road's clearance, so a
            // location's protected area does not stop it: otherwise a 73 m boulder
            // was kept whole for one edge in an area, and chunks were left in
            // a road's last metres at a location. Parts of a location are still
            // never touched.
            string? keptOnApproach = inLocationArea && keepLocationRocks ? KeptOnApproachWhy(root) : null;
            hits[root] = RoadRockHit.Judged(name, size, RoadRockPolicy.PolicyNames(name, RoadRockCarve.FracturedFrom()),
                RoadBiome.At(root.transform.position), natural, mist, cliffs, locationChild, inLocationArea, keptOnApproach,
                piece, view != null, view != null && view.IsValid(), view != null && view.IsOwner());
            RoadRockStats.JudgeTicks += RoadRockStats.Now - judging;
        }
        return touchedHere;
    }

    /// <summary>
    /// Clear (or, dry, only list) the judged objects: carve or remove each
    /// boulder, log what changed and what was kept, and count item drops
    /// before and after. Returns the summary's outcome clause.
    /// </summary>
    private static string Apply(Dictionary<GameObject, RoadRockHit> hits, HashSet<GameObject> onDeck,
        List<PendingCarve>? deferCarves, Vector3 centre, float radius, List<string> lines, out int clearedCount)
    {
        int cleared = 0, carved = 0, deferred = 0;
        // Drops are counted over the probe's reach plus the largest rock seen,
        // so a chunk dropping stone at the far side of a rock is still inside.
        float dropRadius = DropRadius(radius);
        int dropsBefore = RoadRockCarve.DropsNear(centre, dropRadius);
        foreach (var kv in hits)
        {
            Vector3 p = kv.Key.transform.position;
            RoadRockHit hit = kv.Value;
            bool deck = onDeck.Contains(kv.Key) && hit.DeckTakes;
            string line = System.FormattableString.Invariant(
                $"  {hit.Name} at ({p.x:F1},{p.y:F1},{p.z:F1}) size {hit.Size:F1} m -> {(deck ? "through a deck: remove whole" : hit.Verdict)}");
            if (hit.Kind == RoadRockHitKind.KeepNotOwner) LastNotOwner++;
            switch (RoadRockPolicy.HowTaken(hit.Clears, hit.DeckTakes, onDeck.Contains(kv.Key), hit.Size,
                        kv.Key.GetComponent<ZNetView>() != null))
            {
                case RoadRockPolicy.Taking.Carve:
                    if (deferCarves != null)
                    {
                        // Carved over the next frames (a carve is ~18 ms of the main thread): no carve below this
                        // pass sees another's result, and none needs to (each takes only its own rock's chunks).
                        deferCarves.Add(new PendingCarve(kv.Key, line));
                        lines.Add(line + "; carve: over the next frames");
                        deferred++;
                        continue;
                    }
                    var (c, r, refused) = CarveOne(kv.Key, line, lines);
                    carved += c;
                    cleared += r;
                    if (refused) LastNotOwner++;
                    continue;
                case RoadRockPolicy.Taking.RemoveWhole:
                    if (!deck && RoadRockPolicy.CarveRemovesWhole(hit.Size))
                        line += System.FormattableString.Invariant($"; small rock (under {RoadRockPolicy.CarveMinSize:F0} m): whole");
                    cleared += RemoveWhole(kv.Key, line, lines);
                    continue;
            }
            // A listed rock kept in the road (location part, owner, biome, a
            // partial block) says why, once.
            if (hit.Listed) LogOnce("Road rocks:" + line);
            lines.Add(line);
        }
        clearedCount = cleared + carved;
        string outcome = $", carved {carved}, removed {cleared} whole{(deferred > 0 ? $", {deferred} to carve over the next frames" : "")}";
        int dropsAfter = RoadRockCarve.DropsNear(centre, dropRadius);
        outcome += $"; item drops within {dropRadius:F0} m: {dropsBefore} before, {dropsAfter} after";
        WarnIfDropsRose(centre, dropsBefore, dropsAfter);
        return outcome;
    }

    /// <summary>The "no materials left behind" check: item drops round a cleared area must not rise.</summary>
    internal static void WarnIfDropsRose(Vector3 centre, int dropsBefore, int dropsAfter)
    {
        if (dropsAfter > dropsBefore)
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(System.FormattableString.Invariant(
                $"Road rocks: item drops near ({centre.x:F0},{centre.z:F0}) rose {dropsBefore} -> {dropsAfter} after clearing"));
    }

    /// <summary>Carve one rock (<see cref="RoadRockCarve.Carve"/>), or remove it whole when it has no chunks to carve; logs
    /// what changed. A carve refused because another peer owns the rock leaves it as it is (<c>refused</c>: its zone is
    /// looked at again), never removed whole. Returns (carved, removed whole, refused).</summary>
    private static (int carved, int removed, bool refused) CarveOne(GameObject root, string line, List<string>? lines)
    {
        long carving = RoadRockStats.Now;
        var r = RoadRockCarve.Carve(root);
        RoadRockStats.CarveTicks += RoadRockStats.Now - carving;
        if (r.outcome == RoadRockCarve.Outcome.Refused)
        {
            line += $"; carve: refused ({r.note}), kept";
            LogOnce("Road rocks:" + line);
            lines?.Add(line);
            return (0, 0, true);
        }
        if (r.outcome != RoadRockCarve.Outcome.NotCarvable)
        {
            line += System.FormattableString.Invariant(
                $"; carve: {r.outcome}, {r.onRoad} of {r.chunks} chunks in the road (mean {r.chunkSize:F1} m) + {r.unsupported} cut off{(r.swapped ? ", swapped for the fractured copy" : "")}{(r.note.Length > 0 ? " (" + r.note + ")" : "")}");
            lines?.Add(line);
            // A rock the pass looked at and left alone is logged once: the
            // automatic pass revisits every loaded zone, and logging it
            // wrote the same line every cycle; never logging it left a
            // rock in the road with no trace of why.
            if (r.outcome != RoadRockCarve.Outcome.Untouched) Log("Road rocks:" + line);
            else LogOnce("Road rocks:" + line);
            if (r.outcome == RoadRockCarve.Outcome.Untouched) return (0, 0, false);
            RoadRockStats.Carved++;
            return (1, 0, false);
        }
        line += $"; carve: not carvable ({r.note}), removed whole";
        return (0, RemoveWhole(root, line, lines), false);
    }

    /// <summary>Remove a rock whole; logs it. Returns 1.</summary>
    private static int RemoveWhole(GameObject root, string line, List<string>? lines)
    {
        // ZNetScene.Destroy only schedules destruction: switch the colliders off now, so no
        // carve later in this pass sees a rock that is about to vanish holding the road.
        foreach (var c in root.GetComponentsInChildren<Collider>()) c.enabled = false;
        if (root.GetComponent<ZNetView>() != null) ZNetScene.instance.Destroy(root);
        else Object.Destroy(root);
        RoadRockStats.Removed++;
        Log("Road rocks:" + line + "; removed whole");
        lines?.Add(line);
        return 1;
    }

    // Every rock actually changed goes to the BepInEx log: the automatic
    // clearing has no console.
    private static void Log(string line) => ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(line);
}
