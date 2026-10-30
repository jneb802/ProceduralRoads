using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using SoftReferenceableAssets;

namespace ProceduralRoads;

/// <summary>
/// Reads a location's levelling off its prefab, for LocationLevelling.
///
/// This is the engine half and is deliberately kept out of the pure sources:
/// it loads assets and walks Unity components, neither of which the test suite
/// can stand up. Everything it decides is a fact about the prefab; what that
/// means for a road is decided in LocationLevelling, which is tested.
///
/// A prefab is read once; only its copied operation values are kept. A world
/// has thousands of location instances and only dozens of distinct prefabs. Anything that goes wrong is
/// recorded as "this prefab levels nothing", which puts the road back on the
/// natural terrain under its own end - the behaviour before any of this.
/// </summary>
public static class LocationPrefabLevelling
{
    private static readonly Dictionary<string, IReadOnlyList<LevelOp>> m_byPrefab = new();

    private static readonly Dictionary<string, float> m_terrainRadius = new();

    /// <summary>Where each location prefab's networked objects stand, from its root, by prefab
    /// name: read as its parts are replayed (<see cref="PlacedOps"/>).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Vector3[]> s_parts = new();

    /// <summary>How far an object may stand from one of its location's part spots and be that part.</summary>
    public const float PartTolerance = 0.5f;

    /// <summary>
    /// Whether the object at <paramref name="at"/> is one of the networked parts of the location
    /// <paramref name="prefabName"/> placed at <paramref name="root"/> turned by <paramref name="rotation"/>:
    /// the game spawns each at root + rotation x its offset in the prefab (ZoneSystem.SpawnLocation).
    /// Null while that prefab's parts are unknown (not yet replayed this session). A
    /// rock2_mountain in a frost cave's footprint that stays put while the cave turns between
    /// runs is not the cave's.
    /// </summary>
    public static bool? IsPart(string prefabName, Vector3 root, Quaternion rotation, Vector3 at)
    {
        if (!s_parts.TryGetValue(prefabName, out Vector3[]? parts)) return null;
        foreach (Vector3 local in parts)
            if ((root + rotation * local - at).sqrMagnitude <= PartTolerance * PartTolerance) return true;
        return false;
    }

    /// <summary>Wire this up as LocationLevelling's source. Called from the plugin.</summary>
    public static void Install()
    {
        LocationLevelling.Source = OpsAt;
        LocationLevelling.PlacementHeightSource = PlacementHeightAt;
        LocationLevelling.FacesDownhillSource = FacesDownhillAt;
        // The same guarded door as Reset() uses.
        LocationLevelling.ResetPlacements = LocationPlacementHeights.Clear;
        LocationPlacementHeights.Source = SavedPlacements;
        LocationLevelling.Prime = PrimeSynchronously;
        RoadSiteProtection.Source = Footprints;
        LocationLevelling.PrimeIncremental=PrimeSteps;
        LocationLevelling.CanPrimeIncrementally=()=>LocationLevelling.Prime==PrimeSynchronously && BuiltInSources();
    }

    private static bool BuiltInSources()=>RoadSiteProtection.Source==Footprints && LocationPlacementHeights.Source==SavedPlacements;
    private static void PrimeSynchronously()
    {
        if(!BuiltInSources())
        {
            LocationPlacementHeights.Read(); RoadSiteProtection.Prime();
            return;
        }
        using var preparation=new RoadPreparation(PrimeSteps);
        preparation.Drain();
    }

    private static IEnumerable<bool> PrimeSteps(RoadPreparation preparation)
    {
        // Copy live collections before retaining an iterator across Unity frames.
        var locations=RoadSiteProtection.InstalledButUnprepared ? ZoneSystem.instance?.GetLocationList()?.ToArray() : null;
        if(locations!=null && locations.Length>0)
        {
            preparation.BeginStage("Finding location prefabs",locations.Length,"locations"); yield return true;
            var distinct=new List<ZoneSystem.ZoneLocation>();
            var names=new HashSet<string>();
            foreach(var location in locations)
            {
                if(names.Add(location.m_location.m_prefab.Name)) distinct.Add(location.m_location);
                if(preparation.Checkpoint()) yield return true;
            }
            preparation.BeginStage("Preparing location prefabs",distinct.Count,"prefabs"); yield return true;
            foreach(var location in distinct)
            {
                // ForPrefab fully releases its temporary asset before control is yielded.
                ForPrefab(location);
                preparation.Checkpoint(); yield return true;
            }
            preparation.BeginStage("Preparing location footprints",locations.Length,"locations"); yield return true;
            var footprints=new List<RoadSiteProtection.Footprint>(locations.Length);
            foreach(var location in locations)
            {
                footprints.Add(FootprintAt(location));
                if(preparation.Checkpoint()) yield return true;
            }
            preparation.BeginStage("Indexing location footprints",footprints.Count,"footprints"); yield return true;
            foreach(var step in RoadSiteProtection.SetSteps(footprints,preparation)) yield return step;
        }
        if(!LocationPlacementHeights.HasBeenRead && ZDOMan.instance!=null)
        {
            var objects=ZDOMan.instance.m_objectsByID.Values.ToArray();
            preparation.BeginStage("Reading saved location placements",objects.Length,"saved objects"); yield return true;
            var placements=new List<(Vector2 centre,float height)>();
            foreach(var zdo in objects)
            {
                if(TryPlacement(zdo,out var placement)) placements.Add(placement);
                if(preparation.Checkpoint()) yield return true;
            }
            preparation.BeginStage("Indexing saved location placements",placements.Count,"placements"); yield return true;
            foreach(var step in LocationPlacementHeights.SetSteps(placements,preparation)) yield return step;
        }
    }

    private static IEnumerable<RoadSiteProtection.Footprint>? Footprints()
    {
        if (ZoneSystem.instance == null) return null;
        var locations = ZoneSystem.instance.GetLocationList();
        if (locations == null || locations.Count == 0) return null;
        var result = new List<RoadSiteProtection.Footprint>();
        foreach (var inst in locations)
        {
            ForPrefab(inst.m_location);
            result.Add(FootprintAt(inst));
        }
        return result;
    }

    /// <summary>
    /// Every location prefab of the loaded world with what it does to the ground,
    /// as CSV: one row per levelling operation (a prefab that levels nothing has
    /// one row with no operation), for offline tools.
    /// Main thread, outside generation.
    /// </summary>
    public static string DescribeCsv()
    {
        var sb = new System.Text.StringBuilder("prefab,terrainRadius,offsetX,offsetZ,height,radius,square,smoothRadius,smoothPower,exteriorRadius,slopeRotation,randomRotation,unique,iconPlaced,iconAlways\n");
        if (ZoneSystem.instance == null) return sb.ToString();
        var seen = new HashSet<string>();
        foreach (var inst in ZoneSystem.instance.GetLocationList())
        {
            string name = inst.m_location.m_prefab.Name;
            if (!seen.Add(name)) continue;
            IReadOnlyList<LevelOp> ops = ForPrefab(inst.m_location);
            m_terrainRadius.TryGetValue(name, out float terrainRadius);
            string head = FormattableString.Invariant($"{name},{terrainRadius:F3}");
            string tail = FormattableString.Invariant(
                $",{inst.m_location.m_exteriorRadius:F3},{(inst.m_location.m_slopeRotation ? 1 : 0)},{(inst.m_location.m_randomRotation ? 1 : 0)}") +
                FormattableString.Invariant($",{(inst.m_location.m_unique ? 1 : 0)},{(inst.m_location.m_iconPlaced ? 1 : 0)},{(inst.m_location.m_iconAlways ? 1 : 0)}\n");
            if (ops.Count == 0) sb.Append(head).Append(",,,,,,,").Append(tail);
            foreach (LevelOp op in ops)
                sb.Append(head).Append(FormattableString.Invariant(
                    $",{op.OffsetX:F3},{op.OffsetZ:F3},{op.Height:F3},{op.Radius:F3},{(op.Square ? 1 : 0)},{op.SmoothRadius:F3},{op.SmoothPower:F3}")).Append(tail);
        }
        return sb.ToString();
    }

    public static void Reset()
    {
        if (LocationLevelling.Sealed)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError(
                "levelling data was asked to clear while roads were being generated; refused");
            return;
        }
        m_byPrefab.Clear(); m_terrainRadius.Clear();
        LocationPlacementHeights.Clear();
        RoadSiteProtection.Reset();
    }

    /// <summary>Every LocationProxy in the saved world, as a centre and the
    /// height it stands at. One pass over the ZDO table; the store decides
    /// when, and holds the result.</summary>
    private static IEnumerable<(Vector2 centre, float height)>? SavedPlacements()
    {
        if (ZDOMan.instance == null) return null;
        var found = new List<(Vector2, float)>();
        foreach(var zdo in ZDOMan.instance.m_objectsByID.Values)
            if(TryPlacement(zdo,out var placement)) found.Add(placement);
        return found;
    }

    private static RoadSiteProtection.Footprint FootprintAt(ZoneSystem.LocationInstance location)
    {
        string name=location.m_location.m_prefab.Name;
        m_terrainRadius.TryGetValue(name,out float terrainRadius);
        m_byPrefab.TryGetValue(name,out IReadOnlyList<LevelOp>? ops);
        float exterior=location.m_location.m_exteriorRadius, whole=Mathf.Max(exterior,terrainRadius);
        return new RoadSiteProtection.Footprint(new Vector2(location.m_position.x,location.m_position.z),
            whole,LocationLevelling.ArrivalReach(ops,exterior,whole));
    }
    private static readonly int LocationProxyHash="LocationProxy".GetStableHashCode();
    private static bool TryPlacement(ZDO zdo,out (Vector2 centre,float height) placement)
    {
        placement=default;
        if(zdo.GetPrefab()!=LocationProxyHash) return false;
        Vector3 position=zdo.GetPosition();
        placement=(new Vector2(position.x,position.z),position.y);
        return true;
    }

    private static float? PlacementHeightAt(Vector2 centre) => LocationPlacementHeights.At(centre);

    private static IReadOnlyList<LevelOp>? OpsAt(Vector2 centre)
    {
        if (ZoneSystem.instance == null) return null;
        var list = ZoneSystem.instance.GetLocationList();
        if (list == null) return null;
        foreach (ZoneSystem.LocationInstance inst in list)
        {
            if (Mathf.Abs(inst.m_position.x - centre.x) > 0.5f ||
                Mathf.Abs(inst.m_position.z - centre.y) > 0.5f)
                continue;
            return ForPrefab(inst.m_location);
        }
        return null;
    }

    /// <summary>
    /// The terrain modifiers a placed location really has. A location's parts are
    /// switched on or off at random as it is placed (RandomSpawn, RandomObject), so the
    /// prefab lists more than any one placement keeps: a frost cave's lists 10, the
    /// two placed ones had 7 and 8, and rebuilt from all 10 the ground in front of one
    /// was 8.9 m off. The draw is the game's own, replayed: ZoneSystem.SpawnLocation
    /// seeds Random with the placement's seed (saved on its LocationProxy), then randomizes
    /// each enabled RandomSpawn and then each RandomObject at its placed position, in that
    /// order; the location then appears with the modifiers left active. Main thread only;
    /// the prefab (its transform and every object's active flag), Random's state and the
    /// location's cached biome are all put back.
    /// Null when the prefab cannot be read.
    /// </summary>
    public static IReadOnlyList<LevelOp>? PlacedOps(Vector2 centre, int seed, Vector3 pos, Quaternion rot)
    {
        if (ZoneSystem.instance == null) return null;
        ZoneSystem.ZoneLocation? location = null;
        foreach (ZoneSystem.LocationInstance inst in ZoneSystem.instance.GetLocationList())
            if (Mathf.Abs(inst.m_position.x - centre.x) <= 0.5f && Mathf.Abs(inst.m_position.z - centre.y) <= 0.5f) { location = inst.m_location; break; }
        if (location == null) return null;
        var ops = new List<LevelOp>();
        try
        {
            var reference = location.m_prefab;
            var loader = AssetBundleLoader.Instance;
            int index = loader.m_assetIDToLoaderIndex[reference.m_assetID];
            bool read = TemporaryAssetRead.Read(
                () => loader.m_assetLoaders[index].ReferenceCount,
                () => reference.Load(),
                () => reference.Release(),
                () =>
            {
                GameObject asset = reference.Asset;
                if (asset == null) return false;
                Location component = asset.GetComponent<Location>();
                ZNetView[] views = Utils.GetEnabledComponentsInChildren<ZNetView>(asset);
                RandomSpawn[] spawns = Utils.GetEnabledComponentsInChildren<RandomSpawn>(asset);
                RandomObject[] objects = Utils.GetEnabledComponentsInChildren<RandomObject>(asset);
                // Every object's own active flag, to put back exactly: switching a part off also
                // switches off the networked objects under it, and Reset does not switch them back
                // on (the game does, from its own list). Left off, the next draw on this prefab --
                // ours or the game's own -- saw different parts.
                Transform[] all = asset.GetComponentsInChildren<Transform>(true);
                var active = new bool[all.Length];
                for (int i = 0; i < all.Length; i++) active[i] = all[i].gameObject.activeSelf;
                foreach (RandomSpawn spawn in spawns) spawn.Prepare();
                Vector3 position = asset.transform.position;
                Quaternion rotation = asset.transform.rotation;
                UnityEngine.Random.State state = UnityEngine.Random.state;
                Heightmap.Biome biome = component != null ? component.m_biome : Heightmap.Biome.None;
                asset.transform.position = Vector3.zero;
                asset.transform.rotation = Quaternion.identity;
                try
                {
                    var parts = new Vector3[views.Length];
                    for (int i = 0; i < views.Length; i++) parts[i] = views[i].gameObject.transform.position;
                    s_parts[location.m_prefab.Name] = parts;
                    UnityEngine.Random.InitState(seed);
                    foreach (RandomSpawn spawn in spawns) spawn.Randomize(pos + rot * spawn.gameObject.transform.position, component);
                    foreach (RandomObject obj in objects) obj.Randomize(pos + rot * obj.gameObject.transform.position, component);
                    foreach (TerrainModifier modifier in asset.GetComponentsInChildren<TerrainModifier>(false))
                    {
                        if (!modifier.enabled || (!modifier.m_level && !modifier.m_smooth)) continue;
                        Vector3 local = modifier.transform.position;
                        ops.Add(new LevelOp(local.x, local.z, local.y + modifier.m_levelOffset,
                            modifier.m_level ? modifier.m_levelRadius : 0f, modifier.m_square,
                            modifier.m_smooth ? modifier.m_smoothRadius : 0f, modifier.m_smoothPower, modifier.m_sortOrder));
                    }
                }
                finally
                {
                    // As SpawnLocation ends: Reset each, then switch its networked objects back on;
                    // then every flag as it was.
                    foreach (RandomSpawn spawn in spawns) spawn.Reset();
                    foreach (RandomObject obj in objects) obj.Reset();
                    foreach (ZNetView view in views) view.gameObject.SetActive(true);
                    for (int i = 0; i < all.Length; i++)
                        if (all[i] != null && all[i].gameObject.activeSelf != active[i]) all[i].gameObject.SetActive(active[i]);
                    UnityEngine.Random.state = state;
                    if (component != null) component.m_biome = biome;
                    asset.transform.position = position;
                    asset.transform.rotation = rotation;
                }
                return true;
            });
            return read ? ops : null;
        }
        catch (Exception e)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug($"Could not replay {location.m_prefab.Name}'s placement: {e.GetType().Name} {e.Message}");
            return null;
        }
    }

    private static bool FacesDownhillAt(Vector2 centre)
    {
        if (ZoneSystem.instance == null) return false;
        var list = ZoneSystem.instance.GetLocationList();
        if (list == null) return false;
        foreach (ZoneSystem.LocationInstance inst in list)
            if (Mathf.Abs(inst.m_position.x - centre.x) <= 0.5f && Mathf.Abs(inst.m_position.z - centre.y) <= 0.5f)
                return inst.m_location.m_slopeRotation;
        return false;
    }

    private static IReadOnlyList<LevelOp> ForPrefab(ZoneSystem.ZoneLocation location)
    {
        string name = location.m_prefab.Name;
        if (m_byPrefab.TryGetValue(name, out IReadOnlyList<LevelOp> cached))
            return cached;
        // Past this point lies an AssetBundle read and two writes into shared
        // dictionaries. Neither may happen on an island worker. A prefab that
        // could not be read is remembered as an EMPTY list by the catch below,
        // so a miss here means the prefab was never offered to preparation at
        // all -- not that reading it failed.
        if (LocationLevelling.RefuseAfterSealing(name)) return System.Array.Empty<LevelOp>();

        var ops = new List<LevelOp>();
        float terrainRadius = 0f;
        try
        {
            var reference = location.m_prefab;
            var loader = AssetBundleLoader.Instance;
            int index = loader.m_assetIDToLoaderIndex[reference.m_assetID];
            TemporaryAssetRead.Read(
                () => loader.m_assetLoaders[index].ReferenceCount,
                () => reference.Load(),
                () => reference.Release(),
                () =>
            {
                GameObject asset = reference.Asset;
                if (asset == null) return false;
                // Offsets are taken against the prefab's own root rather than
                // assumed to be zero: a prefab authored off its origin would
                // otherwise report every operation as concentric.
                Vector3 root = asset.transform.position;
                foreach (TerrainModifier modifier in asset.GetComponentsInChildren<TerrainModifier>(true))
                {
                    Vector3 local = modifier.transform.position - root;
                    float reach = modifier.m_level ? modifier.m_levelRadius : 0f;
                    if (modifier.m_smooth) reach = Mathf.Max(reach, modifier.m_smoothRadius);
                    if (modifier.m_paintCleared) reach = Mathf.Max(reach, modifier.m_paintRadius);
                    // Rotation-independent enclosure, including square corners
                    // and off-centre smoothing/paint beyond the exterior radius.
                    if (modifier.m_square) reach *= Mathf.Sqrt(2f);
                    terrainRadius = Mathf.Max(terrainRadius, new Vector2(local.x, local.z).magnitude + reach);
                    // Smoothing-only modifiers too (radius 0): a frost cave carries six, out to
                    // 26 m, and its ground cannot be rebuilt without them.
                    if (!modifier.m_level && !modifier.m_smooth) continue;
                    ops.Add(new LevelOp(local.x, local.z, local.y + modifier.m_levelOffset,
                        modifier.m_level ? modifier.m_levelRadius : 0f, modifier.m_square,
                        modifier.m_smooth ? modifier.m_smoothRadius : 0f, modifier.m_smoothPower, modifier.m_sortOrder));
                }
                return true;
            });
        }
        catch (Exception e)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                $"Could not read levelling for {name}: {e.GetType().Name}; road ends there meet natural terrain");
            ops.Clear();
        }

        m_terrainRadius[name] = terrainRadius;
        m_byPrefab[name] = ops;
        return ops;
    }
}
