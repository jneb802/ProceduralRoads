using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Which objects clip the road surface around a point, and - when asked to -
/// clear the natural boulders among them. The overlap test is the road's
/// clearance standing on the loaded road surface (a box of solid-paint width
/// and player height, <see cref="RoadClearance"/>) against real collider
/// geometry, never a centre radius or a bounding box. Driven by
/// <see cref="RoadRockClearing"/>.
/// </summary>
public static class RoadRockProbe
{
    public static List<string> Run(Vector3 centre, float radius, bool apply, bool sites = false, bool mist = false, bool cliffs = false,
        bool partial = false, bool carve = false) => Run(centre, radius, apply, sites, mist, cliffs, partial, carve, out _);

    /// <summary>
    /// The same, reporting how many objects were cleared. With
    /// <paramref name="partial"/> a clearable rock that never blocks half the
    /// road's width is kept, since a rock that only grazes the road's edge is
    /// part of the scenery rather than an obstacle. Five narrow
    /// capsules stand across the road at every point the rock touches.
    /// </summary>
    public static List<string> Run(Vector3 centre, float radius, bool apply, bool sites, bool mist, bool cliffs,
        bool partial, out int clearedCount) => Run(centre, radius, apply, sites, mist, cliffs, partial, false, out clearedCount);

    /// <summary>
    /// The same, with <paramref name="carve"/>: a clearable rock loses only its
    /// chunks in the road (<see cref="RoadRockCarve"/>) instead of being
    /// removed whole; a rock with no chunks is still removed whole. The item
    /// drops near the probe are counted before and after, and any rise is
    /// reported and logged as a warning.
    /// </summary>
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

    public static List<string> Run(Vector3 centre, float radius, bool apply, bool sites, bool mist, bool cliffs,
        bool partial, bool carve, out int clearedCount)
    {
        clearedCount = 0;
        long passStarted = RoadRockStats.Now;
        try { return RunTimed(centre, radius, apply, sites, mist, cliffs, partial, carve, out clearedCount); }
        finally { RoadRockStats.EndPass(passStarted); }
    }

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
        bool partial, bool carve, out int clearedCount)
    {
        clearedCount = 0;
        LastGroundMisses = 0;
        LastRoadHeightPoints = 0;
        LastOtherZoneObjects = 0;
        var lines = new List<string>();
        if (!RoadSpatialGrid.IsInitialized || ZoneSystem.instance == null || ZNetScene.instance == null)
        { lines.Add("Error: world or road network not available"); return lines; }

        var natural = NaturalRocks(mist, cliffs);

        var hits = new Dictionary<GameObject, (string name, string verdict, float size, bool listed)>();
        var otherZone = new HashSet<GameObject>();
        var touched = new Dictionary<GameObject, List<(RoadSpatialGrid.RoadPoint point, float surface)>>();
        long lookup = RoadRockStats.Now;
        var points = RoadSpatialGrid.GetRoadPointsNearPosition(centre, radius);
        // Bridge decks are not road points, so rocks on a bridge were never
        // looked at. Each span in reach is walked every metre at deck height;
        // a rock there goes whole: bridges are few, and no rock is missed.
        var decks = DeckPoints(centre, radius);
        RoadRockStats.LookupTicks += RoadRockStats.Now - lookup;
        var onDeck = new HashSet<GameObject>();
        int probed = 0;
        var all = new List<(RoadSpatialGrid.RoadPoint point, float surface, bool deck)>();
        long t = RoadRockStats.Now;
        foreach (var point in points)
        {
            if (point.paintOnly) continue;
            bool ground = ZoneSystem.instance.GetGroundHeight(new Vector3(point.p.x, 0f, point.p.y), out float surface);
            if (!RoadRockPolicy.ClearanceSurface(ground, surface, point.h, RoadRockCarve.GhostSpawned != null, out surface)) { LastGroundMisses++; continue; }
            if (!ground) LastRoadHeightPoints++;
            all.Add((point, surface, false));
        }
        foreach (var (point, deckHeight) in decks) all.Add((point, deckHeight - DeckBelow, true));
        RoadRockStats.GroundTicks += RoadRockStats.Now - t;
        RoadRockStats.Points += all.Count;
        foreach (var (point, surface, deck) in all)
        {
            probed++;
            long clearance = RoadRockStats.Now;
            RoadClearance(point, surface, deck ? decks.ConvertAll(d => d.point) : points, out var boxCentre, out var boxHalf, out var boxTurn,
                deck ? DeckBelow + ClearanceHeight : ClearanceHeight);
            RoadRockStats.ClearanceTicks += RoadRockStats.Now - clearance;
            t = RoadRockStats.Now;
            int count = Physics.OverlapBoxNonAlloc(boxCentre, boxHalf, s_overlap, boxTurn, CandidateMask, QueryTriggerInteraction.Ignore);
            if (count == s_overlap.Length)
            {
                // A full buffer may have dropped some: ask again with room for all.
                s_overlap = Physics.OverlapBox(boxCentre, boxHalf, boxTurn, CandidateMask, QueryTriggerInteraction.Ignore);
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
                if (deck) onDeck.Add(root);
                if (partial)
                {
                    if (!touched.TryGetValue(root, out var list)) touched[root] = list = new List<(RoadSpatialGrid.RoadPoint, float)>();
                    list.Add((point, surface));
                }
                if (hits.ContainsKey(root)) continue;
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
                bool protectedSite = locationChild || (inSiteArea && !sites && !carve);
                var biome = Heightmap.FindBiome(root.transform.position);
                bool listed = false, ok = false;
                foreach (string judged in RoadRockPolicy.PolicyNames(name, RoadRockCarve.FracturedFrom()))
                {
                    listed |= RoadRockPolicy.IsNaturalBoulder(judged) || RoadRockPolicy.IsMistlandsRock(judged, mist, cliffs);
                    ok |= RoadRockPolicy.CanClear(judged, natural.Contains(judged), biome,
                        protectedSite, piece, view != null, view != null && view.IsValid(), view != null && view.IsOwner()) ||
                        RoadRockPolicy.CanClearMistlands(judged, mist, cliffs, natural.Contains(judged), biome,
                        protectedSite, piece, view != null, view != null && view.IsValid(), view != null && view.IsOwner());
                }
                string verdict = ok ? (inSiteArea ? (sites ? "boulder in a site area: clear (sites)" : "boulder in a site area: carve the road through it") : "boulder: clear") :
                    !listed ? (RoadRockPolicy.IsMistlandsBoulder(name) ? "keep: Mistlands rock (add 'mist' to include)" :
                        RoadRockPolicy.IsMistlandsCliff(name) ? "keep: Mistlands cliff (add 'cliffs' to include)" : "keep: not a listed natural boulder") :
                    locationChild ? "keep: part of a location" :
                    protectedSite ? "keep: protected site area (add 'sites' to include)" : piece ? "keep: player piece" :
                    view != null && !view.IsOwner() ? "keep: not owner" : "keep: biome or vegetation rule";
                hits[root] = (name, verdict, size, listed);
                RoadRockStats.JudgeTicks += RoadRockStats.Now - judging;
            }
        }

        // Partial rocks: the worst share of the road's width a rock blocks at
        // any point it touches, from five narrow capsules across the road.
        // Carving takes only the chunks in the road, so a rock that merely
        // clips the road's edge loses just that edge: the keep rule is for
        // whole-rock removal, where clearing a clip cost the whole rock.
        // With carving on, a small rock in the road is removed whole, never kept: this rule keeps a
        // rock that blocks under half the width, and that can still be the centre of the lane
        // (measured: four kept small rocks blocked the centre line).
        if (partial && !carve)
            foreach (var root in new List<GameObject>(hits.Keys))
            {
                var h = hits[root];
                if (!h.verdict.StartsWith("boulder") || !touched.TryGetValue(root, out var at)) continue;
                int worst = 0;
                foreach (var (point, surface) in at)
                    worst = Mathf.Max(worst, BlockedSlots(root, point, surface, points));
                if (RoadRockPolicy.KeepPartial(worst, Slots))
                    hits[root] = (h.name, $"keep: blocks under half the road ({worst} of {Slots} slots at worst)", h.size, h.listed);
            }

        int cleared = 0, carved = 0;
        // Drops are counted over the probe's reach plus the largest rock seen,
        // so a chunk dropping stone at the far side of a rock is still inside.
        float dropRadius = radius + 30f;
        int dropsBefore = apply ? RoadRockCarve.DropsNear(centre, dropRadius) : 0;
        var carvedRocks = new List<(MineRock5 rock, int line)>();
        bool removedAny = false;
        foreach (var kv in hits)
        {
            Vector3 p = kv.Key.transform.position;
            string line = System.FormattableString.Invariant(
                $"  {kv.Value.name} at ({p.x:F1},{p.y:F1},{p.z:F1}) size {kv.Value.size:F1} m -> {kv.Value.verdict}");
            if (apply && kv.Value.verdict.StartsWith("boulder"))
            {
                if (carve && kv.Value.size < RoadRockPolicy.CarveMinSize)
                    line += System.FormattableString.Invariant($"; small rock (under {RoadRockPolicy.CarveMinSize:F0} m): whole");
                else if (carve && kv.Key.GetComponent<ZNetView>() != null && !onDeck.Contains(kv.Key))
                {
                    long carving = RoadRockStats.Now;
                    var r = RoadRockCarve.Carve(kv.Key);
                    RoadRockStats.CarveTicks += RoadRockStats.Now - carving;
                    if (r.outcome != RoadRockCarve.Outcome.NotCarvable)
                    {
                        line += System.FormattableString.Invariant(
                            $"; carve: {r.outcome}, {r.onRoad} of {r.chunks} chunks in the road (mean {r.chunkSize:F1} m) + {r.hanging} hanging + {r.unsupported} unsupported{(r.swapped ? ", swapped for the fractured copy" : "")}{(r.note.Length > 0 ? " (" + r.note + ")" : "")}");
                        if (r.outcome == RoadRockCarve.Outcome.Carved && r.rock != null) carvedRocks.Add((r.rock, lines.Count));
                        lines.Add(line);
                        // A rock the pass looked at and left alone is logged once: the
                        // automatic pass revisits every loaded zone, and logging it
                        // wrote the same line every cycle; never logging it left a
                        // rock in the road with no trace of why.
                        if (r.outcome != RoadRockCarve.Outcome.Untouched) Log("Road rocks:" + line);
                        else LogOnce("Road rocks:" + line);
                        if (r.outcome != RoadRockCarve.Outcome.Untouched) { carved++; RoadRockStats.Carved++; }
                        continue;
                    }
                    line += $"; carve: not carvable ({r.note}), removed whole";
                }
                // ZNetScene.Destroy only schedules destruction: switch the colliders off now, so no
                // support test later in this pass leans on a rock that is about to vanish.
                foreach (var c in kv.Key.GetComponentsInChildren<Collider>()) c.enabled = false;
                removedAny = true;
                if (kv.Key.GetComponent<ZNetView>() != null) ZNetScene.instance.Destroy(kv.Key);
                else Object.Destroy(kv.Key);
                cleared++;
                RoadRockStats.Removed++;
                Log("Road rocks:" + line + "; removed whole");
            }
            // A listed rock kept in the road (location part, owner, biome, a
            // partial block) says why, once.
            else if (apply && kv.Value.listed) LogOnce("Road rocks:" + line);
            lines.Add(line);
        }
        // Neighbours removed whole no longer hold anything up: test the carved rocks again.
        if (removedAny && carvedRocks.Count > 0)
        {
            Physics.SyncTransforms();
            foreach (var (rock, index) in carvedRocks)
            {
                int loose = RoadRockCarve.Resupport(rock);
                if (loose <= 0) continue;
                Vector3 q = rock.transform.position;
                Log(System.FormattableString.Invariant($"Road rocks:  {rock.name} at ({q.x:F1},{q.y:F1},{q.z:F1}): {loose} more chunk(s) unsupported once the neighbours removed whole were gone"));
            }
        }
        clearedCount = cleared + carved;
        string outcome = !apply ? " (dry run: add 'apply' to clear the boulders)" :
            carve ? $", carved {carved}, removed {cleared} whole" : $", cleared {cleared} natural boulder(s)";
        if (apply)
        {
            int dropsAfter = RoadRockCarve.DropsNear(centre, dropRadius);
            outcome += $"; item drops within {dropRadius:F0} m: {dropsBefore} before, {dropsAfter} after";
            if (dropsAfter > dropsBefore)
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(System.FormattableString.Invariant(
                    $"Road rocks: item drops near ({centre.x:F0},{centre.z:F0}) rose {dropsBefore} -> {dropsAfter} after clearing"));
        }
        LastOtherZoneObjects = otherZone.Count;
        string reach = (LastRoadHeightPoints > 0 ? $", {LastRoadHeightPoints} at the road's own height (outside the zone's terrain)" : "") +
            (LastGroundMisses > 0 ? $", {LastGroundMisses} without ground skipped" : "") +
            (otherZone.Count > 0 ? $"; {otherZone.Count} object(s) of other zones passed over" : "");
        lines.Insert(0, System.FormattableString.Invariant(
            $"Road rocks: {probed} road points probed within {radius:F0} m{reach}, {hits.Count} object(s) clip the road surface{outcome}"));
        return lines;
    }

    // Every rock actually changed goes to the BepInEx log: the automatic
    // clearing has no console.
    private static void Log(string line) => ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(line);

    public const int Slots = 5;

    /// <summary>How far below a deck the deck clearance starts, metres: the
    /// deck's own beams and a rock the deck runs into.</summary>
    public const float DeckBelow = 1f;

    /// <summary>Points every metre along each bridge span within reach, with
    /// the deck height: the road's stored height at each bank, interpolated.</summary>
    internal static List<(RoadSpatialGrid.RoadPoint point, float deck)> DeckPoints(Vector3 centre, float radius)
    {
        var result = new List<(RoadSpatialGrid.RoadPoint, float)>();
        var c = new Vector2(centre.x, centre.z);
        foreach (var crossing in RoadNetworkGenerator.GetRoadCrossings())
        {
            if (crossing.Kind != CrossingKind.Bridge) continue;
            Vector2 a = crossing.FromBank, b = crossing.ToBank;
            float length = Vector2.Distance(a, b);
            if (length < 1f) continue;
            // Nearest point of the span to the probe centre.
            float t0 = Mathf.Clamp01(Vector2.Dot(c - a, b - a) / (length * length));
            if (Vector2.Distance(a + (b - a) * t0, c) > radius) continue;
            if (!RoadSpatialGrid.TryGetRoadHeightWithin(a, 4f, out float ha)) ha = RoadConstants.SeaLevel + BridgeLayout.DeckFreeboard;
            if (!RoadSpatialGrid.TryGetRoadHeightWithin(b, 4f, out float hb)) hb = ha;
            int n = Mathf.CeilToInt(length);
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n;
                result.Add((new RoadSpatialGrid.RoadPoint(a + (b - a) * t, RoadSpatialGrid.DefaultRoadWidth, Mathf.Lerp(ha, hb, t)), Mathf.Lerp(ha, hb, t)));
            }
        }
        return result;
    }

    /// <summary>Height of the clearance over the road: a player's. It is the
    /// painted width (or a little less) wide.</summary>
    public const float ClearanceHeight = 2f;
    /// <summary>Half the clearance's length along the road: road points are
    /// stored about 1 m apart, so neighbouring boxes overlap.</summary>
    public const float ClearanceHalfLength = 0.6f;
    /// <summary>The clearance starts this far above the ground, so a chunk whose
    /// top only reaches the levelled surface stays.</summary>
    public const float ClearanceLift = 0.1f;

    /// <summary>
    /// The road's clearance at a point: a box turned to the road's heading, as
    /// wide as the solid paint and <see cref="ClearanceHeight"/> tall. It was a
    /// capsule as wide as the road; a capsule is never shorter than its width,
    /// so on a 4 m road it was a 4 m ball, reaching 4 m up and leaving arches.
    /// </summary>
    internal static void RoadClearance(RoadSpatialGrid.RoadPoint point, float surface, List<RoadSpatialGrid.RoadPoint> all,
        out Vector3 centre, out Vector3 half, out Quaternion turn, float height = ClearanceHeight)
    {
        var dir = Heading(point, all);
        turn = Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.y));
        half = new Vector3(point.w * 0.5f * RoadConstants.RoadPaintOuterRatio, height * 0.5f, ClearanceHalfLength);
        centre = new Vector3(point.p.x, surface + ClearanceLift + half.y, point.p.y);
    }

    internal static Vector2 Heading(RoadSpatialGrid.RoadPoint point, List<RoadSpatialGrid.RoadPoint> all) =>
        RoadRockPolicy.Heading(point, all);

    /// <summary>How many of <see cref="Slots"/> narrow capsules across the road
    /// at this point the rock's colliders touch.</summary>
    private static int BlockedSlots(GameObject root, RoadSpatialGrid.RoadPoint point, float surface, List<RoadSpatialGrid.RoadPoint> all)
    {
        var dir = Heading(point, all);
        var across = new Vector2(-dir.y, dir.x);
        float half = point.w * 0.5f, r = half / Slots;
        int blocked = 0;
        for (int k = 0; k < Slots; k++)
        {
            float off = (-1f + (2f * k + 1f) / Slots) * half;
            var c = point.p + across * off;
            var bottom = new Vector3(c.x, surface + r, c.y);
            var top = bottom + Vector3.up * Mathf.Max(0f, 2.5f - 2f * r);
            foreach (var col in Physics.OverlapCapsule(bottom, top, r, ~0, QueryTriggerInteraction.Ignore))
                if (col != null && col.transform.IsChildOf(root.transform)) { blocked++; break; }
        }
        return blocked;
    }
}
