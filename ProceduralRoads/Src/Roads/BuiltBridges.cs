using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>One structure site's pieces as they were laid when its first zone was built, and the ground they were laid on
/// (<see cref="StructureGround.Measure"/>): a crossing site (<see cref="BuiltBridge"/>) or a staircase
/// (<see cref="BuiltStair"/>). Its two ends find it again (<see cref="BuiltSites{T}.Of"/>).</summary>
public abstract class BuiltSite
{
    public Vector2 From;
    public Vector2 To;
    /// <summary>The kit the pieces were laid in, by its record number (0 for none): the site's own kind reads it.</summary>
    public int KitNumber;
    /// <summary>The version of the laying that built the pieces (the build stamp).</summary>
    public int Layout;
    public readonly List<StructurePiece> Pieces = new();
    public readonly List<GroundPin> Ground = new();

    /// <summary>A piece's placement alone, or with the crossing and kit a bridge plan's pieces carry.</summary>
    internal static StructurePiece Copy(StructurePiece p, RoadCrossing? crossing = null, BridgeStructures.Kit? kit = null) => new()
    {
        Kind = p.Kind, Prefab = p.Prefab, Position = p.Position,
        YawDegrees = p.YawDegrees, PitchDegrees = p.PitchDegrees, RollDegrees = p.RollDegrees,
        HealthFraction = p.HealthFraction, Crossing = crossing, Kit = kit,
    };
}

/// <summary>One crossing site's pieces as they were solved when its first zone was built (<see cref="BridgeStructures.Build"/>).</summary>
public sealed class BuiltBridge : BuiltSite
{
    public BuiltBridge() { Layout = BridgeLayout.LayoutVersion; }

    public Vector2 FromBank { get => From; set => From = value; }
    public Vector2 ToBank { get => To; set => To = value; }

    /// <summary>The kit the pieces were solved in (record number: the kit's number and one).</summary>
    public BridgeStructures.Kit? Kit
    {
        get => KitNumber == 0 ? null : (BridgeStructures.Kit)(KitNumber - 1);
        set => KitNumber = value is BridgeStructures.Kit kit ? (int)kit + 1 : 0;
    }

    /// <summary>The pieces as a plan lays them, each carrying <paramref name="crossing"/> and the kit they were solved in.</summary>
    public List<StructurePiece> PiecesFor(RoadCrossing crossing) => Pieces.ConvertAll(p => Copy(p, crossing, Kit));

    internal static bool KnownKit(int number) => Enum.IsDefined(typeof(BridgeStructures.Kit), number - 1);
}

/// <summary>One staircase's pieces as its kit laid them over its layout when its first zone was built
/// (<see cref="StairStructures.Build"/>): the network saves the layout (<see cref="StairRun"/>), this the kit's pieces.</summary>
public sealed class BuiltStair : BuiltSite
{
    public BuiltStair() { Layout = StairKit.DressingVersion; }

    /// <summary>The kit that laid the pieces.</summary>
    public StairKit? Kit => StairKit.Numbered(KitNumber);

    /// <summary>The pieces as a plan lays them.</summary>
    public List<StructurePiece> PiecesForPlan() => Pieces.ConvertAll(p => Copy(p));

    internal static bool KnownKit(int number) => StairKit.Numbered(number) != null;
}

/// <summary>
/// The structure sites of one kind whose first zone is built, saved with the network: every later zone of a site lays the
/// saved pieces, whatever layout and kits a later build has, and every terrain write levels to the ground they were laid
/// on. A site nobody has reached is laid when it is. The one store and record of both kinds (<see cref="BuiltBridges"/>,
/// <see cref="BuiltStairs"/>). Pure logic.
/// </summary>
public sealed class BuiltSites<T> where T : BuiltSite, new()
{
    private readonly List<T> _sites = new();
    private readonly string _what;
    private readonly Func<int, bool> _knownKit;
    private readonly Func<BuiltSite, Vector2, Vector2, bool> _same;

    /// <param name="what">The site's name in a refusal ("built bridge").</param>
    /// <param name="knownKit">Whether a saved kit number (not 0) names a kit this build has.</param>
    /// <param name="same">Whether a site is the one with these two ends.</param>
    internal BuiltSites(string what, Func<int, bool> knownKit, Func<BuiltSite, Vector2, Vector2, bool> same)
    {
        _what = what; _knownKit = knownKit; _same = same;
    }

    /// <summary>The sites as they are now (a copy).</summary>
    public IReadOnlyList<T> Sites { get { lock (_sites) return _sites.ToArray(); } }

    public void Reset()
    {
        lock (_sites) _sites.Clear();
        Changed();
    }

    /// <summary>Forget these sites and the ground they held: each is laid again when its first zone is built next.</summary>
    public void Forget(ICollection<T> sites)
    {
        lock (_sites) _sites.RemoveAll(sites.Contains);
        Changed();
    }

    /// <summary>The saved sites a laying older than <paramref name="layout"/> built.</summary>
    public List<T> OlderThan(int layout)
    {
        var older = new List<T>();
        foreach (T site in Sites)
            if (site.Layout < layout) older.Add(site);
        return older;
    }

    /// <summary>The saved site with these ends, null if it has none.</summary>
    public T? Of(Vector2 from, Vector2 to)
    {
        lock (_sites)
            foreach (T site in _sites)
                if (_same(site, from, to)) return site;
        return null;
    }

    /// <summary>A site's pieces as laid now saved with <paramref name="ground"/>, which is held from now on. A site saved
    /// already keeps what it has; null where none may be saved (<see cref="StructureSites.Saving"/>), and where the structure
    /// is not in the world (<see cref="StructureSites.SpatialFault"/>): none of it is saved, as a load refuses its record.</summary>
    internal T? Keep(Vector2 from, Vector2 to, int kit, IEnumerable<StructurePiece> pieces, IEnumerable<GroundPin> ground)
    {
        if (!StructureSites.Saving) return null;
        lock (_sites)
        {
            if (Of(from, to) is T known) return known;
            var site = new T { From = from, To = to, KitNumber = kit };
            foreach (StructurePiece p in pieces) site.Pieces.Add(BuiltSite.Copy(p));
            site.Ground.AddRange(ground);
            if (StructureSites.SpatialFault(site.From, site.To, site.Pieces, site.Ground) is string fault)
            {
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(System.FormattableString.Invariant(
                    $"The {_what} ({from.x:F1}, {from.y:F1}) -> ({to.x:F1}, {to.y:F1}) not saved: {fault}"));
                return null;
            }
            _sites.Add(site);
            StructureGround.Hold(site.Ground);
            return site;
        }
    }

    private static void Changed()
    {
        StructureGround.Rebuild();
        StructurePlans.InvalidatePlans();
    }

    /// <summary>The format the record is written in, 2: per site its ends, kit (0 none, else its record number) and the
    /// laying that built it; per piece its kind, prefab, position, pitch, yaw, roll and health; then the ground it stands on
    /// (<see cref="GroundPin"/>). No other record is read.</summary>
    private const int FormatVersion = 2;

    public byte[] Serialize()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(FormatVersion);
        IReadOnlyList<T> sites = Sites;
        writer.Write(sites.Count);
        foreach (T site in sites)
        {
            writer.Write(site.From.x); writer.Write(site.From.y);
            writer.Write(site.To.x); writer.Write(site.To.y);
            writer.Write(site.KitNumber);
            writer.Write(site.Layout);
            writer.Write(site.Pieces.Count);
            foreach (StructurePiece p in site.Pieces)
            {
                writer.Write((int)p.Kind); writer.Write(p.Prefab);
                writer.Write(p.Position.x); writer.Write(p.Position.y); writer.Write(p.Position.z);
                writer.Write(p.PitchDegrees); writer.Write(p.YawDegrees); writer.Write(p.RollDegrees);
                writer.Write(p.HealthFraction);
            }
            GroundPin.WriteAll(writer, site.Ground);
        }
        return stream.ToArray();
    }

    /// <summary>Replace the sites with saved ones (none for null or empty data). Malformed data throws, changing nothing.</summary>
    public void Load(byte[]? bytes)
    {
        List<T> loaded = Parse(bytes);
        lock (_sites)
        {
            _sites.Clear();
            _sites.AddRange(loaded);
        }
        Changed();
    }

    /// <summary>Saved sites, read without touching the current ones. Malformed data throws.</summary>
    public List<T> Parse(byte[]? bytes)
    {
        var loaded = new List<T>();
        if (bytes == null || bytes.Length == 0) return loaded;
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);
        int version = reader.ReadInt32();
        if (version != FormatVersion) throw new InvalidDataException($"Unknown {_what} data version.");
        int sites = reader.ReadInt32();
        if (sites < 0 || sites > 10000) throw new InvalidDataException($"Invalid {_what} count.");
        int total = 0;
        for (int i = 0; i < sites; i++)
        {
            var site = new T
            {
                From = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                To = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
            };
            int kit = reader.ReadInt32();
            if (kit != 0 && !_knownKit(kit)) throw new InvalidDataException($"Unknown {_what} kit.");
            site.KitNumber = kit;
            site.Layout = reader.ReadInt32();
            if (site.Layout < 0) throw new InvalidDataException($"Invalid {_what} layout.");
            int pieces = reader.ReadInt32();
            if (pieces < 0 || (total += pieces) > 1_000_000) throw new InvalidDataException($"Invalid {_what} piece count.");
            for (int j = 0; j < pieces; j++)
            {
                int kind = reader.ReadInt32();
                string prefab = reader.ReadString();
                var pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                float pitch = reader.ReadSingle(), yaw = reader.ReadSingle(), roll = reader.ReadSingle(), health = reader.ReadSingle();
                if (prefab.Length > RoadNetworkPersistence.MaxSavedPrefabName || !Enum.IsDefined(typeof(StructurePieceKind), kind) ||
                    !ManualRoadDraft.IsFinite(pos.y) ||
                    !ManualRoadDraft.IsFinite(pitch) || !ManualRoadDraft.IsFinite(yaw) || !ManualRoadDraft.IsFinite(roll) ||
                    !ManualRoadDraft.IsFinite(health) || health < 0f || health > 1f)
                    throw new InvalidDataException($"Invalid {_what} piece.");
                site.Pieces.Add(new StructurePiece
                {
                    Kind = (StructurePieceKind)kind, Prefab = prefab, Position = pos,
                    PitchDegrees = pitch, YawDegrees = yaw, RollDegrees = roll, HealthFraction = health,
                });
            }
            GroundPin.ReadAll(reader, site.Ground);
            if (StructureSites.SpatialFault(site.From, site.To, site.Pieces, site.Ground) is string fault)
                throw new InvalidDataException($"Invalid {_what}: {fault}.");
            loaded.Add(site);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException($"Trailing {_what} data.");
        return loaded;
    }
}

/// <summary>
/// What every structure solved whole at its first zone build shares: whether sites may be saved, and the one rule for a
/// zone built for the first time (<see cref="KeepFirstBuilt"/>). A site is a crossing site (<see cref="BuiltBridges"/>) or a
/// staircase (<see cref="BuiltStairs"/>).
/// </summary>
public static class StructureSites
{
    /// <summary>Whether sites may be saved: not on a network a released build saved, which keeps its format and its records
    /// (<see cref="RoadNetworkPersistence.AllowsNetworkChanges"/>).</summary>
    public static bool Saving => RoadNetworkPersistence.AllowsNetworkChanges;

    /// <summary>
    /// Why a structure is not in the world, or null when it is: its two ends, every piece's origin and every vertex of
    /// ground it holds, each by the world's one rule (<see cref="RoadConstants.InWorld"/>). The one verdict over a whole
    /// bridge or staircase, the same when it is built (<see cref="BuiltSites{T}.Keep"/>, before anything is saved) and when
    /// its saved records are read (<see cref="BuiltSites{T}.Parse"/>, <see cref="StairStructures.Parse"/>), whatever zones
    /// are loaded. A structure it refuses is refused whole: no piece of it is kept or read.
    /// </summary>
    public static string? SpatialFault(Vector2 from, Vector2 to, IReadOnlyList<StructurePiece> pieces, IReadOnlyList<GroundPin> ground)
    {
        static string At(float x, float z) => System.FormattableString.Invariant($"({x}, {z})");
        if (!RoadConstants.InWorld(from) || !RoadConstants.InWorld(to)) return $"an end outside the world at {At(from.x, from.y)} -> {At(to.x, to.y)}";
        foreach (StructurePiece piece in pieces)
            if (!RoadConstants.InWorld(new Vector2(piece.Position.x, piece.Position.z)))
                return $"a piece outside the world at {At(piece.Position.x, piece.Position.z)}";
        foreach (GroundPin pin in ground)
            if (!RoadConstants.InWorld(new Vector2(pin.X, pin.Z))) return $"held ground outside the world at {At(pin.X, pin.Z)}";
        return null;
    }

    /// <summary>The structures refused as not in the world whole (<see cref="Refuses"/>), each said once.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> s_refused = new();

    /// <summary>
    /// Whether a structure solved to be laid is not in the world whole (<see cref="SpatialFault"/> over its ends, its pieces and
    /// <paramref name="ground"/>, the ground its record would hold): the one verdict that decides both whether it is laid and
    /// whether it is saved, asked where its pieces are made (BridgeStructures.Solve, StairStructures.Dresser), so a structure
    /// refused at build lays nothing, in any zone, as it saves nothing (<see cref="BuiltSites{T}.Keep"/> asks the same).
    /// Said once per structure as a warning. The verdict is the point rule's: a piece's extent past its origin is not judged.
    /// </summary>
    internal static bool Refuses(object structure, string what, Vector2 from, Vector2 to, IReadOnlyList<StructurePiece> pieces, IReadOnlyList<GroundPin> ground)
    {
        if (SpatialFault(from, to, pieces, ground) is not string fault) return false;
        lock (s_refused)
        {
            if (s_refused.TryGetValue(structure, out _)) return true;
            s_refused.Add(structure, structure);
        }
        ProceduralRoadsPlugin.ProceduralRoadsLogger?.LogWarning(System.FormattableString.Invariant(
            $"The {what} ({from.x:F1}, {from.y:F1}) -> ({to.x:F1}, {to.y:F1}) refused, nothing of it laid or saved: {fault}"));
        return true;
    }

    /// <summary>
    /// The sites these planned pieces of a zone built for the first time belong to saved (<see cref="PlannedSite.Keep"/>),
    /// each whose zones are none of them built yet, owed pieces (<see cref="StructureAppendQueue"/>) or holding pieces. A site
    /// with a zone built and no saved pieces (a world built before sites were saved, or whose zone record was lost) is laid
    /// zone by zone as before, as is every site of a network a released build saved (<see cref="Saving"/>). The records are
    /// written with the zones built (<see cref="RoadNetworkPersistence.SaveBridgeZones"/>).
    /// </summary>
    internal static void KeepFirstBuilt(IEnumerable<StructurePiece> pieces, WorldGenerator world, ICollection<ZDOID>? condemned = null)
    {
        if (!Saving) return;
        PlannedSite? last = null;
        foreach (StructurePiece piece in pieces)
        {
            if (piece.Site == null || piece.Site == last) continue;
            last = piece.Site;
            if (piece.Site.Saved() || Started(piece.Site.Zones, condemned)) continue;
            // The plan's own pieces of the site: laid once, in this zone and saved alike.
            piece.Site.Keep(world);
        }
    }

    // Pieces a respawn condemned moments ago are not standing (StructurePlans.ZoneHasLivePieces).
    private static bool Started(HashSet<Vector2s> zones, ICollection<ZDOID>? condemned)
    {
        foreach (Vector2s zone in zones)
            if (StructurePlans.Started(zone, condemned)) return true;
        return false;
    }
}

/// <summary>A structure as the plan being laid has it (one per structure, shared by its pieces): its pieces, every zone they
/// stand in, and how it is saved when its first zone is built (<see cref="StructureSites.KeepFirstBuilt"/>). Plans only.</summary>
public sealed class PlannedSite
{
    public readonly List<StructurePiece> Pieces;
    public readonly HashSet<Vector2s> Zones;
    private readonly Func<bool> _saved;
    private readonly Action<WorldGenerator> _keep;

    /// <param name="zones">The zones that make it started when one is built: by default every zone a piece stands in.</param>
    internal PlannedSite(List<StructurePiece> pieces, Func<bool> saved, Action<WorldGenerator> keep, HashSet<Vector2s>? zones = null)
    {
        Pieces = pieces; _saved = saved; _keep = keep;
        if (zones == null)
        {
            zones = new HashSet<Vector2s>();
            foreach (StructurePiece piece in pieces) zones.Add(ZoneSystem.GetZone(piece.Position));
        }
        Zones = zones;
    }

    /// <summary>Whether the site is saved already.</summary>
    public bool Saved() => _saved();

    /// <summary>The site saved with these pieces.</summary>
    internal void Keep(WorldGenerator world) => _keep(world);
}

/// <summary>The crossing sites whose first zone is built (<see cref="BridgeStructures.Build"/>), saved with the network: the
/// shared store (<see cref="BuiltSites{T}"/>) found by a crossing's banks. Pure logic.</summary>
public static class BuiltBridges
{
    internal static readonly BuiltSites<BuiltBridge> Store = new("built bridge", BuiltBridge.KnownKit,
        (site, from, to) => SameBanks(site, from, to));

    private static bool SameBanks(BuiltSite site, Vector2 from, Vector2 to)
    {
        float r2 = BridgeLayout.SameSiteRadius * BridgeLayout.SameSiteRadius;
        return ((site.From - from).sqrMagnitude <= r2 && (site.To - to).sqrMagnitude <= r2)
            || ((site.From - to).sqrMagnitude <= r2 && (site.To - from).sqrMagnitude <= r2);
    }

    /// <summary>The sites as they are now (a copy).</summary>
    public static IReadOnlyList<BuiltBridge> Sites => Store.Sites;

    public static void Reset() => Store.Reset();

    /// <summary>The saved sites a layout older than this build's solved (<see cref="BuiltSite.Layout"/>).</summary>
    public static List<BuiltBridge> OlderLayout() => Store.OlderThan(BridgeLayout.LayoutVersion);

    /// <summary>Forget these sites and the ground they held: each is solved again when its first zone is built next.</summary>
    public static void Forget(ICollection<BuiltBridge> sites) => Store.Forget(sites);

    /// <summary>Whether a crossing is one of these sites (<see cref="Of"/>'s rule).</summary>
    public static bool IsOneOf(RoadCrossing crossing, IEnumerable<BuiltBridge> sites)
    {
        foreach (BuiltBridge site in sites)
            if (SameBanks(site, crossing.FromBank, crossing.ToBank)) return true;
        return false;
    }

    /// <summary>The saved site of a crossing (<see cref="BridgeLayout.DistinctSites"/>'s rule), null if it has none.</summary>
    public static BuiltBridge? Of(RoadCrossing crossing) => Store.Of(crossing.FromBank, crossing.ToBank);

    /// <summary>Whether sites may be saved (<see cref="StructureSites.Saving"/>).</summary>
    public static bool Saving => StructureSites.Saving;

    /// <summary>A crossing's pieces as solved now saved, with the ground they stand on on <paramref name="vertex"/>, which is
    /// held from now on. A site saved already keeps what it has; null where none may be saved (<see cref="Saving"/>).</summary>
    internal static BuiltBridge? Keep(RoadCrossing crossing, List<StructurePiece> pieces, Func<int, int, float> vertex)
    {
        if (!Saving) return null;
        BridgeStructures.Kit? kit = pieces.Count > 0 ? pieces[0].Kit : null;
        return Store.Of(crossing.FromBank, crossing.ToBank)
            ?? Store.Keep(crossing.FromBank, crossing.ToBank, kit is BridgeStructures.Kit k ? (int)k + 1 : 0, pieces, StructureGround.Measure(pieces, vertex));
    }

    public static byte[] Serialize() => Store.Serialize();
    public static void Load(byte[]? bytes) => Store.Load(bytes);
    public static List<BuiltBridge> Parse(byte[]? bytes) => Store.Parse(bytes);
}

/// <summary>The staircases whose first zone is built (<see cref="StairStructures.Build"/>), saved with the network: the
/// shared store (<see cref="BuiltSites{T}"/>) found by a run's two ends. Pure logic.</summary>
public static class BuiltStairs
{
    /// <summary>How near a saved staircase's ends are to a run's for it to be the run's: the same run read back.</summary>
    public const float SameRunReach = 0.01f;

    internal static readonly BuiltSites<BuiltStair> Store = new("built staircase", BuiltStair.KnownKit,
        (site, from, to) => (site.From - from).sqrMagnitude <= SameRunReach * SameRunReach && (site.To - to).sqrMagnitude <= SameRunReach * SameRunReach);

    /// <summary>The sites as they are now (a copy).</summary>
    public static IReadOnlyList<BuiltStair> Sites => Store.Sites;

    public static void Reset() => Store.Reset();

    /// <summary>The saved staircase of a run, null if it has none.</summary>
    public static BuiltStair? Of(StairRun run) => Store.Of(run.From, run.To);

    /// <summary>A run's pieces as its kit lays them now saved, with the ground its layout was settled on and the ground under
    /// any piece the kit adds to the layout (<see cref="StairStructures.Build"/>). Null where none may be saved.</summary>
    internal static BuiltStair? Keep(StairRun run, StairKit kit, List<StructurePiece> pieces, WorldGenerator? world)
    {
        if (!StructureSites.Saving) return null;
        if (Store.Of(run.From, run.To) is BuiltStair known) return known;
        return Store.Keep(run.From, run.To, kit.Number, pieces, GroundOf(run, pieces, world != null ? StructureGround.Vertices(world) : null));
    }

    /// <summary>The ground a staircase's record holds (<see cref="Keep"/>): the ground its layout was settled on, and the
    /// finished ground (<paramref name="vertex"/>) under any piece its kit adds to the layout, each vertex once; with no
    /// ground to read, its layout's alone. What the structure's verdict reads (StairStructures.Dresser).</summary>
    internal static List<GroundPin> GroundOf(StairRun run, List<StructurePiece> pieces, Func<int, int, float>? vertex)
    {
        var ground = new List<GroundPin>(run.Ground);
        // What the kit lays over the layout's pieces: its supports (StairSupport) and anything not of the layout's kinds.
        List<StructurePiece> added = pieces.FindAll(p => p.Kind == StructurePieceKind.StairSupport || !StairStructures.IsLayoutKind(p.Kind));
        if (added.Count > 0 && vertex != null)
        {
            var pinned = new HashSet<(int, int)>();
            foreach (GroundPin pin in ground) pinned.Add((pin.X, pin.Z));
            foreach (GroundPin pin in StructureGround.Measure(added, vertex))
                if (pinned.Add((pin.X, pin.Z))) ground.Add(pin);
        }
        return ground;
    }

    public static byte[] Serialize() => Store.Serialize();
    public static void Load(byte[]? bytes) => Store.Load(bytes);
    public static List<BuiltStair> Parse(byte[]? bytes) => Store.Parse(bytes);
}
