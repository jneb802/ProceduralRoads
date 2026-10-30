using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>One crossing site's pieces as they were solved when its first zone was built, and the ground they were laid
/// on (<see cref="StructureGround.Measure"/>).</summary>
public sealed class BuiltBridge
{
    public Vector2 FromBank;
    public Vector2 ToBank;
    /// <summary>The kit the pieces were solved in.</summary>
    public BridgeStructures.Kit? Kit;
    /// <summary>The <see cref="BridgeLayout.LayoutVersion"/> that solved the pieces; 0 for a site read from a record without
    /// it.</summary>
    public int Layout = BridgeLayout.LayoutVersion;
    public readonly List<StructurePiece> Pieces = new();
    public readonly List<GroundPin> Ground = new();

    /// <summary>The pieces as a plan lays them, each carrying <paramref name="crossing"/> and the kit they were solved in.</summary>
    public List<StructurePiece> PiecesFor(RoadCrossing crossing) => Pieces.ConvertAll(p => Copy(p, crossing, Kit));

    /// <summary>A piece's placement alone, or with the crossing and kit a plan's pieces carry.</summary>
    internal static StructurePiece Copy(StructurePiece p, RoadCrossing? crossing = null, BridgeStructures.Kit? kit = null) => new()
    {
        Kind = p.Kind, Prefab = p.Prefab, Position = p.Position,
        YawDegrees = p.YawDegrees, PitchDegrees = p.PitchDegrees, RollDegrees = p.RollDegrees,
        HealthFraction = p.HealthFraction, Crossing = crossing, Kit = kit,
    };
}

/// <summary>
/// The crossing sites whose first zone is built (<see cref="BridgeStructures.Build"/>), saved with the network: every later
/// zone of a site lays the saved pieces, whatever layout and kits a later build has, and every terrain write levels to the
/// ground they were laid on. A site nobody has reached is solved when it is. Pure logic.
/// </summary>
public static class BuiltBridges
{
    private static readonly List<BuiltBridge> s_sites = new();

    /// <summary>The sites as they are now (a copy).</summary>
    public static IReadOnlyList<BuiltBridge> Sites { get { lock (s_sites) return s_sites.ToArray(); } }

    public static void Reset()
    {
        lock (s_sites) s_sites.Clear();
        StructureGround.Rebuild();
        StructurePlans.InvalidatePlans();
    }

    /// <summary>The saved sites a layout older than this build's solved (<see cref="BuiltBridge.Layout"/>).</summary>
    public static List<BuiltBridge> OlderLayout()
    {
        var older = new List<BuiltBridge>();
        foreach (BuiltBridge site in Sites)
            if (site.Layout < BridgeLayout.LayoutVersion) older.Add(site);
        return older;
    }

    /// <summary>Forget these sites and the ground they held: each is solved again when its first zone is built next.</summary>
    public static void Forget(ICollection<BuiltBridge> sites)
    {
        lock (s_sites) s_sites.RemoveAll(sites.Contains);
        StructureGround.Rebuild();
        StructurePlans.InvalidatePlans();
    }

    /// <summary>Whether a crossing is one of these sites (<see cref="Of"/>'s rule).</summary>
    public static bool IsOneOf(RoadCrossing crossing, IEnumerable<BuiltBridge> sites)
    {
        foreach (BuiltBridge site in sites)
            if (RoadCrossing.SameBanks(crossing, site.FromBank, site.ToBank, BridgeLayout.SameSiteRadius)) return true;
        return false;
    }

    /// <summary>The saved site of a crossing (<see cref="BridgeLayout.DistinctSites"/>'s rule), null if it has none.</summary>
    public static BuiltBridge? Of(RoadCrossing crossing)
    {
        lock (s_sites)
            foreach (BuiltBridge site in s_sites)
                if (RoadCrossing.SameBanks(crossing, site.FromBank, site.ToBank, BridgeLayout.SameSiteRadius)) return site;
        return null;
    }

    /// <summary>Whether sites may be saved: not on a network a released build saved, which keeps its format and its records.</summary>
    public static bool Saving => !RoadSpatialGrid.LoadedFromReleasedFormat;

    /// <summary>A crossing's pieces as solved now saved, with the ground they stand on on <paramref name="vertex"/>, which is
    /// held from now on. A site saved already keeps what it has; null where none may be saved (<see cref="Saving"/>).</summary>
    internal static BuiltBridge? Keep(RoadCrossing crossing, List<StructurePiece> pieces, Func<int, int, float> vertex)
    {
        if (!Saving) return null;
        lock (s_sites)
        {
            if (Of(crossing) is BuiltBridge known) return known;
            var site = new BuiltBridge { FromBank = crossing.FromBank, ToBank = crossing.ToBank, Kit = pieces.Count > 0 ? pieces[0].Kit : null };
            foreach (StructurePiece p in pieces) site.Pieces.Add(BuiltBridge.Copy(p));
            site.Ground.AddRange(StructureGround.Measure(pieces, vertex));
            s_sites.Add(site);
            StructureGround.Hold(site.Ground);
            return site;
        }
    }

    /// <summary>The format the record is written in, 2: per site its banks, kit (0 none, else the kit's number and one)
    /// and the layout that solved it; per piece its kind, prefab, position, pitch, yaw, roll and health; then the ground it
    /// stands on (<see cref="GroundPin"/>). Record 1 (without the layout) is read as before, its sites as layout 0.</summary>
    private const int FormatVersion = 2;

    public static byte[] Serialize()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(FormatVersion);
        IReadOnlyList<BuiltBridge> sites = Sites;
        writer.Write(sites.Count);
        foreach (BuiltBridge site in sites)
        {
            writer.Write(site.FromBank.x); writer.Write(site.FromBank.y);
            writer.Write(site.ToBank.x); writer.Write(site.ToBank.y);
            writer.Write(site.Kit is BridgeStructures.Kit kit ? (int)kit + 1 : 0);
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
    public static void Load(byte[]? bytes)
    {
        List<BuiltBridge> loaded = Parse(bytes);
        lock (s_sites)
        {
            s_sites.Clear();
            s_sites.AddRange(loaded);
        }
        StructureGround.Rebuild();
        StructurePlans.InvalidatePlans();
    }

    /// <summary>Saved sites, read without touching the current ones. Malformed data throws.</summary>
    public static List<BuiltBridge> Parse(byte[]? bytes)
    {
        var loaded = new List<BuiltBridge>();
        if (bytes == null || bytes.Length == 0) return loaded;
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);
        int version = reader.ReadInt32();
        if (version < 1 || version > FormatVersion) throw new InvalidDataException("Unknown built bridge data version.");
        int sites = reader.ReadInt32();
        if (sites < 0 || sites > 10000) throw new InvalidDataException("Invalid built bridge count.");
        int total = 0;
        for (int i = 0; i < sites; i++)
        {
            var site = new BuiltBridge
            {
                FromBank = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                ToBank = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
            };
            int kit = reader.ReadInt32();
            if (kit != 0 && !Enum.IsDefined(typeof(BridgeStructures.Kit), kit - 1)) throw new InvalidDataException("Unknown built bridge kit.");
            if (kit != 0) site.Kit = (BridgeStructures.Kit)(kit - 1);
            site.Layout = version >= 2 ? reader.ReadInt32() : 0;
            if (site.Layout < 0) throw new InvalidDataException("Invalid built bridge layout.");
            if (!ManualRoadDraft.ValidPoint(site.FromBank) || !ManualRoadDraft.ValidPoint(site.ToBank))
                throw new InvalidDataException("Invalid built bridge banks.");
            int pieces = reader.ReadInt32();
            if (pieces < 0 || (total += pieces) > 1_000_000) throw new InvalidDataException("Invalid built bridge piece count.");
            for (int j = 0; j < pieces; j++)
            {
                int kind = reader.ReadInt32();
                string prefab = reader.ReadString();
                var pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                float pitch = reader.ReadSingle(), yaw = reader.ReadSingle(), roll = reader.ReadSingle(), health = reader.ReadSingle();
                if (prefab.Length > 100 || !Enum.IsDefined(typeof(StructurePieceKind), kind) ||
                    !ManualRoadDraft.ValidPoint(new Vector2(pos.x, pos.z)) || !ManualRoadDraft.IsFinite(pos.y) ||
                    !ManualRoadDraft.IsFinite(pitch) || !ManualRoadDraft.IsFinite(yaw) || !ManualRoadDraft.IsFinite(roll) ||
                    !ManualRoadDraft.IsFinite(health) || health < 0f || health > 1f)
                    throw new InvalidDataException("Invalid built bridge piece.");
                site.Pieces.Add(new StructurePiece
                {
                    Kind = (StructurePieceKind)kind, Prefab = prefab, Position = pos,
                    PitchDegrees = pitch, YawDegrees = yaw, RollDegrees = roll, HealthFraction = health,
                });
            }
            GroundPin.ReadAll(reader, site.Ground);
            loaded.Add(site);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing built bridge data.");
        return loaded;
    }
}
