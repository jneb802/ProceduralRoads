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
    /// The automatic pass round <paramref name="centre"/>, the one owner of its options: applied, outside site areas, the
    /// Mistlands' rocks and cliffs as <see cref="RoadRockPolicy.PassClearsMistlandsRocks"/> and
    /// <see cref="RoadRockPolicy.PassClearsMistlandsCliffs"/> say, partial rocks kept, carved as Roads/RockClearing says
    /// (<see cref="RoadRockPolicy.Carves"/>). The client's pass (RoadRockClearing) and the server's bake (ServerTerrainBake)
    /// both run it. <paramref name="deferCarves"/> and <paramref name="clearedCount"/>: as <see cref="Run"/>.
    /// </summary>
    public static List<string> RunPass(Vector3 centre, float radius, List<PendingCarve>? deferCarves, out int clearedCount) =>
        Run(centre, radius, apply: true, sites: false, mist: RoadRockPolicy.PassClearsMistlandsRocks, cliffs: RoadRockPolicy.PassClearsMistlandsCliffs,
            partial: true, carve: RoadRockPolicy.Carves(RoadRockClearing.Mode), deferCarves, out clearedCount);

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
    /// The probe, reporting how many objects were cleared (<paramref name="clearedCount"/>). With
    /// <paramref name="partial"/> a clearable rock that never blocks half the
    /// road's width is kept, since a rock that only grazes the road's edge is
    /// part of the scenery rather than an obstacle. Five narrow
    /// capsules stand across the road at every point the rock touches.
    /// With <paramref name="carve"/>: a clearable rock loses only its
    /// chunks in the road (<see cref="RoadRockCarve"/>) instead of being
    /// removed whole; a rock with no chunks is still removed whole. The item
    /// drops near the probe are counted before and after, and any rise is
    /// reported and logged as a warning.
    /// With the carves left for later: every rock the pass would carve goes into
    /// <paramref name="deferCarves"/> instead (<see cref="PendingCarve"/>), for the caller to apply over the following
    /// frames (<see cref="CarveDeferred"/>); whole removals and keeps are done now. Null: carved now, as before.
    /// <paramref name="clearedCount"/> then counts the rocks removed whole only.
    /// </summary>
    public static List<string> Run(Vector3 centre, float radius, bool apply, bool sites, bool mist, bool cliffs,
        bool partial, bool carve, List<PendingCarve>? deferCarves, out int clearedCount)
    {
        clearedCount = 0;
        long passStarted = RoadRockStats.Now;
        try { return RunTimed(centre, radius, apply, sites, mist, cliffs, partial, carve, deferCarves, out clearedCount); }
        finally { RoadRockStats.EndPass(passStarted); }
    }

    /// <summary>A rock a pass judged for carving, carved later (<see cref="CarveDeferred"/>): the rock, the pass's
    /// line for it, and whether the rules let it go whole when it cannot be carved.</summary>
    public readonly struct PendingCarve
    {
        public readonly GameObject Root;
        public readonly string Line;
        public readonly bool MayRemoveWhole;
        public PendingCarve(GameObject root, string line, bool mayRemoveWhole) { Root = root; Line = line; MayRemoveWhole = mayRemoveWhole; }
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
        var (carved, removed) = CarveOne(root, pending.Line, pending.MayRemoveWhole, null);
        return carved > 0 ? DeferredOutcome.Carved : removed > 0 ? DeferredOutcome.RemovedWhole : DeferredOutcome.Kept;
    }

    /// <summary>How far round a pass's centre its item drops are counted: its reach plus the largest rock's.</summary>
    internal static float DropRadius(float radius) => radius + 30f;

    /// <summary>Road points the last pass skipped because no ground answered there (a terrain collider not built yet).</summary>
    internal static int LastGroundMisses;
    /// <summary>Road points the last pass probed at the road's stored height: generating a zone, outside its terrain.</summary>
    internal static int LastRoadHeightPoints;
    /// <summary>Objects the last pass found in the road but passed over: generating a zone, they belong to another.</summary>
    internal static int LastOtherZoneObjects;

    /// <summary>Kept rocks and rocks left untouched, already logged: the pass over loaded zones revisits a zone.</summary>
    private static readonly HashSet<string> s_reported = new HashSet<string>();

    /// <summary>A rock the pass judged and left: logged once, so a rock in the road is never silent.</summary>
    private static void LogOnce(string line)
    {
        if (s_reported.Count > 4096) s_reported.Clear();
        if (s_reported.Add(line)) Log(line);
    }

    private static List<string> RunTimed(Vector3 centre, float radius, bool apply, bool sites, bool mist, bool cliffs,
        bool partial, bool carve, List<PendingCarve>? deferCarves, out int clearedCount)
    {
        clearedCount = 0;
        LastGroundMisses = 0;
        LastNotOwner = 0;
        LastRoadHeightPoints = 0;
        LastOtherZoneObjects = 0;
        var lines = new List<string>();
        if (!RoadSpatialGrid.IsInitialized || ZoneSystem.instance == null || ZNetScene.instance == null)
        { lines.Add("Error: world or road network not available"); return lines; }

        var natural = NaturalRocks(mist, cliffs);

        var hits = new Dictionary<GameObject, Hit>();
        var otherZone = new HashSet<GameObject>();
        var touched = new Dictionary<GameObject, List<(RoadSpatialGrid.RoadPoint point, float surface)>>();
        long lookup = RoadRockStats.Now;
        // The boxes a walker needs clear here, roads', decks' and staircases' alike: the one owner, which the carve reads too.
        var boxes = RockClearances.Near(centre, radius, RoadRockCarve.GhostSpawned != null, ZoneSystem.instance.GetGroundHeight, out var points, out LastGroundMisses, out LastRoadHeightPoints);
        RoadRockStats.LookupTicks += RoadRockStats.Now - lookup;
        var onDeck = new HashSet<GameObject>();
        RoadRockStats.Points += boxes.Count;
        int probed = boxes.Count;
        foreach (var box in boxes)
        {
            foreach (var root in Judge(box, sites, mist, cliffs, carve, natural, hits, otherZone, box.KeepsSiteRocks))
            {
                if (box.WholeOnly) onDeck.Add(root);
                if (partial && box.HasPoint)
                {
                    if (!touched.TryGetValue(root, out var list)) touched[root] = list = new List<(RoadSpatialGrid.RoadPoint, float)>();
                    list.Add((box.Point, box.Surface));
                }
            }
        }

        // Partial rocks: the worst share of the road's width a rock blocks at
        // any point it touches, from five narrow capsules across the road.
        // Carving takes only the chunks in the road, so a rock that merely
        // clips the road's edge loses just that edge: the keep rule is for
        // whole-rock removal, where clearing a clip cost the whole rock.
        // With carving on, a small rock in the road is removed whole, never kept: this rule keeps a
        // rock that blocks under half the width, and that can still be the centre of the lane.
        if (partial && !carve)
            foreach (var root in new List<GameObject>(hits.Keys))
            {
                var h = hits[root];
                if (!h.Clears || !touched.TryGetValue(root, out var at)) continue;
                int worst = 0;
                foreach (var (point, surface) in at)
                    worst = Mathf.Max(worst, BlockedSlots(root, point, surface, points));
                if (RoadRockPolicy.KeepPartial(worst, RoadRockPolicy.Slots))
                    hits[root] = new Hit(h.Name, $"keep: blocks under half the road ({worst} of {RoadRockPolicy.Slots} slots at worst)", h.Size, h.Listed);
            }

        string outcome = Apply(hits, onDeck, apply, carve, deferCarves, centre, radius, lines, out clearedCount);
        LastOtherZoneObjects = otherZone.Count;
        string reach = (LastRoadHeightPoints > 0 ? $", {LastRoadHeightPoints} at the road's own height (outside the zone's terrain)" : "") +
            (LastGroundMisses > 0 ? $", {LastGroundMisses} without ground skipped" : "") +
            (otherZone.Count > 0 ? $"; {otherZone.Count} object(s) of other zones passed over" : "");
        lines.Insert(0, System.FormattableString.Invariant(
            $"Road rocks: {probed} road points probed within {radius:F0} m{reach}, {hits.Count} object(s) clip the road surface{outcome}"));
        return lines;
    }

    private static string KeptForApproach(string why) => $"keep: the place's own rock ({why}), on its approach";

    /// <summary>A rock's whole box across, corner to corner: a fractured one's largest chunk is small.</summary>
    private static float Across(GameObject root)
    {
        Bounds? whole = null;
        foreach (var part in root.GetComponentsInChildren<Collider>())
            if (whole == null) whole = part.bounds; else { Bounds u = whole.Value; u.Encapsulate(part.bounds); whole = u; }
        return whole?.size.magnitude ?? 0f;
    }

    /// <summary>Whether a rock in a site area is kept on an approach to the place: a large one
    /// (<see cref="SiteRock.LargeRock"/> or more across), any round its mouth (inside its exterior
    /// radius), or any at a
    /// place that keeps every rock (<see cref="SiteRock.KeepsEveryRock"/>: Hildir's cave). Small
    /// ones further out are cleared out of the way, and a boulder that is not one of the place's
    /// parts (<see cref="LocationPrefabLevelling.IsPart"/>) is any road's. <paramref name="why"/>:
    /// what made it the place's own, for the verdict.</summary>
    private static bool KeptOnApproach(GameObject root, float size, out string why)
    {
        Vector3 at = root.transform.position;
        Location? place = Location.GetLocation(at, checkDungeons: false);
        why = "";
        // No location round it: not one of a place's parts, though it stands in a protected area that
        // reaches further than the location itself. Kept for being large, a 55 m rock1_mountain beside
        // a frost cave's approach was left whole over its steps (MountainCave02 -2560,1536:
        // rocks not part of the prefab are fair game to clear).
        if (place == null) return false;
        // A boulder of the world's in the place's footprint, not one of its parts: as on any road.
        bool? part = LocationPrefabLevelling.IsPart(Utils.GetPrefabName(place.gameObject), place.transform.position, place.transform.rotation, at);
        if (part == false) return false;
        string kind = part == true ? "one of its parts" : "its parts not known";
        Vector3 d = at - place.transform.position;
        if (!SiteRock.Kept(size >= SiteRock.LargeRock, SiteRock.KeepsEveryRock(place.name),
                new Vector2(d.x, d.z).magnitude <= place.m_exteriorRadius, out string reason)) return false;
        why = kind + ", " + reason;
        return true;
    }

    /// <summary>What a judged object is to the road: cleared (and whether it stands in a site area), or kept, and kept
    /// because another peer owns it. Decisions read this; <see cref="Hit.Verdict"/> is the log's wording.</summary>
    private enum HitKind { Keep, KeepNotOwner, Clear, ClearInSiteArea }

    /// <summary>What the probe decided about one object in the clearance.</summary>
    private readonly struct Hit
    {
        public readonly string Name, Verdict;
        public readonly float Size;
        public readonly bool Listed;
        public readonly HitKind Kind;
        /// <summary>The name the clearing rules judged it by (a fractured rock's own rock).</summary>
        public readonly string JudgedAs;
        public Hit(string name, string verdict, float size, bool listed, HitKind kind = HitKind.Keep, string? judgedAs = null)
        { Name = name; Verdict = verdict; Size = size; Listed = listed; Kind = kind; JudgedAs = judgedAs ?? name; }
        public bool Clears => Kind == HitKind.Clear || Kind == HitKind.ClearInSiteArea;
    }

    /// <summary>Objects the last run kept because another peer owns them: the zone is not done until they are judged by
    /// their owner.</summary>
    internal static int LastNotOwner;

    /// <summary>
    /// Which objects stand in one clearance box, each judged once (into
    /// <paramref name="hits"/>) by the clearing policy. Returns the objects the
    /// box touched, judged now or before, so a caller can note where.
    /// <paramref name="keepSiteRocks"/>: the box is an approach to a place (a staircase to its
    /// landing, a road onto it), and a rock in a site area is the place's own: kept, whatever
    /// another box decided.
    /// </summary>
    private static List<GameObject> Judge(RockClearance box, bool sites, bool mist, bool cliffs, bool carve,
        HashSet<string> natural, Dictionary<GameObject, Hit> hits, HashSet<GameObject> otherZone, bool keepSiteRocks = false)
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
            if (hits.TryGetValue(root, out Hit before))
            {
                if (keepSiteRocks && before.Kind == HitKind.ClearInSiteArea && KeptOnApproach(root, Across(root), out string keptBefore))
                    hits[root] = new Hit(before.Name, KeptForApproach(keptBefore), before.Size, before.Listed);
                continue;
            }
            // A rock in a zone explored before the road network is left
            // whole, even when a neighbouring zone's pass reaches it.
            if (!RoadRockPolicy.MayTouch(root.transform.position))
            {
                hits[root] = new Hit(root.name, "keep: zone explored before the road network", 0f, false);
                continue;
            }
            long judging = RoadRockStats.Now;
            RoadRockStats.Judged++;
            // A fractured rock's GameObject is renamed by MineRock5.Awake
            // ("___MineRock5 m_meshFilter"), so read the prefab from the ZDO.
            string name = RoadRockCarve.PrefabName(root, view);
            // Two kinds of protection, kept apart: an object that BELONGS to a
            // location is never touched; an object merely standing inside a
            // location's protected area may be, with 'sites', for a look.
            bool locationChild = root.GetComponentInParent<Location>() != null;
            bool inSiteArea = false;
            float size = 0f;
            foreach (var part in root.GetComponentsInChildren<Collider>())
            {
                var b = part.bounds;
                size = Mathf.Max(size, b.size.magnitude);
                if (RoadSiteProtection.BlocksSegment(new Vector2(b.center.x, b.center.z), new Vector2(b.center.x, b.center.z),
                        new Vector2(b.extents.x, b.extents.z).magnitude, null, null)) inSiteArea = true;
            }
            bool piece = root.GetComponentInParent<Piece>() != null;
            // Carving takes only what stands in the road's clearance, so a
            // site's protected area does not stop it: otherwise a 73 m boulder
            // was kept whole for one edge in an area, and chunks were left in
            // a road's last metres at a site. Parts of a location are still
            // never touched.
            string keptWhy = "";
            bool keptOnApproach = inSiteArea && keepSiteRocks && KeptOnApproach(root, Across(root), out keptWhy);
            bool protectedSite = locationChild || keptOnApproach || (inSiteArea && !sites && !carve);
            var biome = Heightmap.FindBiome(root.transform.position);
            bool listed = false, ok = false;
            string judgedAs = name;
            foreach (string judged in RoadRockPolicy.PolicyNames(name, RoadRockCarve.FracturedFrom()))
            {
                if (RoadRockPolicy.IsResourceDeposit(judged)) judgedAs = judged;
                listed |= RoadRockPolicy.IsNaturalBoulder(judged) || RoadRockPolicy.IsMistlandsRock(judged, mist, cliffs);
                ok |= RoadRockPolicy.CanClear(judged, natural.Contains(judged), biome,
                    protectedSite, piece, view != null, view != null && view.IsValid(), view != null && view.IsOwner()) ||
                    RoadRockPolicy.CanClearMistlands(judged, mist, cliffs, natural.Contains(judged), biome,
                    protectedSite, piece, view != null, view != null && view.IsValid(), view != null && view.IsOwner());
            }
            // Removing whole is the only clearing the Remove setting has, and a deposit is never removed whole.
            bool depositKept = ok && !carve && !RoadRockPolicy.MayRemoveWhole(judgedAs);
            if (depositKept) ok = false;
            string verdict = depositKept ? "keep: ore deposit (only carving takes from it)" : ok ? (inSiteArea ? (sites ? "boulder in a site area: clear (sites)" : "boulder in a site area: carve the road through it") : "boulder: clear") :
                !listed ? (RoadRockPolicy.IsMistlandsBoulder(name) ? "keep: Mistlands rock (add 'mist' to include)" :
                    RoadRockPolicy.IsMistlandsCliff(name) ? "keep: Mistlands cliff (add 'cliffs' to include)" : "keep: not a listed natural boulder") :
                locationChild ? "keep: part of a location" :
                keptOnApproach ? KeptForApproach(keptWhy) :
                protectedSite ? "keep: protected site area (add 'sites' to include)" : piece ? "keep: player piece" :
                view != null && !view.IsOwner() ? "keep: not owner" : "keep: biome or vegetation rule";
            HitKind kind = ok ? (inSiteArea ? HitKind.ClearInSiteArea : HitKind.Clear)
                : listed && !depositKept && !locationChild && !keptOnApproach && !protectedSite && !piece && view != null && !view.IsOwner() ? HitKind.KeepNotOwner
                : HitKind.Keep;
            hits[root] = new Hit(name, verdict, size, listed, kind, judgedAs);
            RoadRockStats.JudgeTicks += RoadRockStats.Now - judging;
        }
        return touchedHere;
    }

    /// <summary>
    /// Clear (or, dry, only list) the judged objects: carve or remove each
    /// boulder, log what changed and what was kept, and count item drops
    /// before and after. Returns the summary's outcome clause.
    /// </summary>
    private static string Apply(Dictionary<GameObject, Hit> hits, HashSet<GameObject> onDeck, bool apply, bool carve,
        List<PendingCarve>? deferCarves, Vector3 centre, float radius, List<string> lines, out int clearedCount)
    {
        int cleared = 0, carved = 0, deferred = 0;
        // Drops are counted over the probe's reach plus the largest rock seen,
        // so a chunk dropping stone at the far side of a rock is still inside.
        float dropRadius = DropRadius(radius);
        int dropsBefore = apply ? RoadRockCarve.DropsNear(centre, dropRadius) : 0;
        foreach (var kv in hits)
        {
            Vector3 p = kv.Key.transform.position;
            string line = System.FormattableString.Invariant(
                $"  {kv.Value.Name} at ({p.x:F1},{p.y:F1},{p.z:F1}) size {kv.Value.Size:F1} m -> {kv.Value.Verdict}");
            if (kv.Value.Kind == HitKind.KeepNotOwner) LastNotOwner++;
            if (apply && kv.Value.Clears)
            {
                bool mayRemoveWhole = RoadRockPolicy.MayRemoveWhole(kv.Value.JudgedAs);
                if (carve && RoadRockPolicy.CarveRemovesWhole(kv.Value.JudgedAs, kv.Value.Size))
                    line += System.FormattableString.Invariant($"; small rock (under {RoadRockPolicy.CarveMinSize:F0} m): whole");
                else if (carve && kv.Key.GetComponent<ZNetView>() != null && !onDeck.Contains(kv.Key))
                {
                    if (deferCarves != null)
                    {
                        // Carved over the next frames (a carve is ~18 ms of the main thread): no carve below this
                        // pass sees another's result, and none needs to (each takes only its own rock's chunks).
                        deferCarves.Add(new PendingCarve(kv.Key, line, mayRemoveWhole));
                        lines.Add(line + "; carve: over the next frames");
                        deferred++;
                        continue;
                    }
                    var (c, r) = CarveOne(kv.Key, line, mayRemoveWhole, lines);
                    carved += c;
                    cleared += r;
                    continue;
                }
                cleared += RemoveWhole(kv.Key, line, mayRemoveWhole, lines);
                continue;
            }
            // A listed rock kept in the road (location part, owner, biome, a
            // partial block) says why, once.
            if (apply && kv.Value.Listed) LogOnce("Road rocks:" + line);
            lines.Add(line);
        }
        clearedCount = cleared + carved;
        string outcome = !apply ? " (dry run: add 'apply' to clear the boulders)" :
            carve ? $", carved {carved}, removed {cleared} whole{(deferred > 0 ? $", {deferred} to carve over the next frames" : "")}" : $", cleared {cleared} natural boulder(s)";
        if (apply)
        {
            int dropsAfter = RoadRockCarve.DropsNear(centre, dropRadius);
            outcome += $"; item drops within {dropRadius:F0} m: {dropsBefore} before, {dropsAfter} after";
            WarnIfDropsRose(centre, dropsBefore, dropsAfter);
        }
        return outcome;
    }

    /// <summary>The "no materials left behind" check: item drops round a cleared area must not rise.</summary>
    internal static void WarnIfDropsRose(Vector3 centre, int dropsBefore, int dropsAfter)
    {
        if (dropsAfter > dropsBefore)
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(System.FormattableString.Invariant(
                $"Road rocks: item drops near ({centre.x:F0},{centre.z:F0}) rose {dropsBefore} -> {dropsAfter} after clearing"));
    }

    /// <summary>Carve one rock (<see cref="RoadRockCarve.Carve"/>), or remove it whole when it cannot be carved and the
    /// rules allow; logs what changed. Returns (carved, removed whole), each 0 or 1.</summary>
    private static (int carved, int removed) CarveOne(GameObject root, string line, bool mayRemoveWhole, List<string>? lines)
    {
        long carving = RoadRockStats.Now;
        var r = RoadRockCarve.Carve(root);
        RoadRockStats.CarveTicks += RoadRockStats.Now - carving;
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
            if (r.outcome == RoadRockCarve.Outcome.Untouched) return (0, 0);
            RoadRockStats.Carved++;
            return (1, 0);
        }
        line += $"; carve: not carvable ({r.note})" + (mayRemoveWhole ? ", removed whole" : "");
        return (0, RemoveWhole(root, line, mayRemoveWhole, lines));
    }

    /// <summary>Remove a rock whole, unless it is an ore deposit (never removed whole); logs it. Returns 1 when removed.</summary>
    private static int RemoveWhole(GameObject root, string line, bool mayRemoveWhole, List<string>? lines)
    {
        if (!mayRemoveWhole)
        {
            // An ore deposit the carve could not cut (or one under a deck) stays, ore and all.
            line += "; ore deposit: kept, never removed whole";
            LogOnce("Road rocks:" + line);
            lines?.Add(line);
            return 0;
        }
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

    /// <summary>How many of <see cref="RoadRockPolicy.Slots"/> narrow capsules across the road
    /// at this point the rock's colliders touch.</summary>
    private static int BlockedSlots(GameObject root, RoadSpatialGrid.RoadPoint point, float surface, List<RoadSpatialGrid.RoadPoint> all)
    {
        var dir = RoadRockPolicy.Heading(point, all);
        var across = new Vector2(-dir.y, dir.x);
        int blocked = 0;
        for (int k = 0; k < RoadRockPolicy.Slots; k++)
        {
            var (off, r) = RoadRockPolicy.Slot(k, RoadRockPolicy.Slots, point.w);
            var c = point.p + across * off;
            var bottom = new Vector3(c.x, surface + r, c.y);
            var top = bottom + Vector3.up * Mathf.Max(0f, 2.5f - 2f * r);
            foreach (var col in Physics.OverlapCapsule(bottom, top, r, ~0, QueryTriggerInteraction.Ignore))
                if (col != null && col.transform.IsChildOf(root.transform)) { blocked++; break; }
        }
        return blocked;
    }
}
