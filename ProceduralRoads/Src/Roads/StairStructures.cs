using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>One staircase in the network: its pieces and the space each walked piece needs clear of rock.</summary>
public sealed class StairRun
{
    public Vector2 From;
    public Vector2 To;
    public float Width;
    /// <summary>A staircase onto a place's landing (built with its road): its clearance never carves or removes a rock standing in a place's
    /// footprint that is the place's own (a frost cave's boulders are likely
    /// part of its prefab).</summary>
    public bool KeepsSiteRocks;
    /// <summary>The kit it was laid in, decided once when laid (StairKit.ForSite / ForLine); null for a run read from a
    /// record without it, whose pieces name their kit.</summary>
    public StairKit? Kit;
    public readonly List<StructurePiece> Pieces = new();

    /// <summary>The kit's name for listings: the stored kit, else the one its first step names.</summary>
    public string KitName => Kit?.Name ?? (Pieces.Find(p => p.Kind == StructurePieceKind.StairStep)?.Prefab.Split('_')[0] ?? "?");

    public override string ToString() => System.FormattableString.Invariant($"{KitName} staircase {From} -> {To}, {Pieces.Count} pieces");
    public readonly List<StairClearance> Clearances = new();

    /// <summary>A run from a finished plan. The plan's cuts are not kept here:
    /// they go into the network as cut-only road points, which the road terrain
    /// writer applies as zones load.</summary>
    public static StairRun FromPlan(Vector2 from, Vector2 to, float width, StairPlan plan, StairKit? kit = null)
    {
        var run = new StairRun { From = from, To = to, Width = width, Kit = kit };
        foreach (StairPiece p in plan.Pieces)
            run.Pieces.Add(new StructurePiece
            {
                Kind = p.Kind switch
                {
                    StairPieceKind.Stair => StructurePieceKind.StairStep,
                    StairPieceKind.Landing => StructurePieceKind.StairFloor,
                    StairPieceKind.Filler => StructurePieceKind.StairFiller,
                    _ => StructurePieceKind.StairSupport,
                },
                Prefab = p.Prefab,
                Position = p.Position,
                YawDegrees = p.YawDegrees,
                Cantilevered = p.Cantilevered,
            });
        run.Clearances.AddRange(plan.Clearances);
        return run;
    }
}

/// <summary>
/// Staircases as a structure source. Unlike a bridge, a staircase is not
/// recomputed from the network on demand: a valley-to-summit plan takes seconds,
/// which a zone load cannot wait for. So each run's plan is made once, when the
/// run is added, and its pieces and clearances are saved with the network
/// (<see cref="Serialize"/>). Pure logic.
/// </summary>
public sealed class StairStructures : IStructureSource
{
    private static readonly List<StairRun> s_runs = new();
    /// <summary>Pathfinding cells a generated road climbed on a staircase, so a
    /// later road over the same ground walks that staircase instead of laying a
    /// second beside it. Generation only: never saved.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), byte> s_cells = new();

    /// <summary>The runs as they are now: a copy taken under the lock islands add under, so a reader never walks a list
    /// being added to.</summary>
    public static IReadOnlyList<StairRun> Runs { get { lock (s_runs) return s_runs.ToArray(); } }

    public static void Reset()
    {
        lock (s_runs) s_runs.Clear();
        s_cells.Clear();
        s_standing.Clear();
    }

    /// <summary>Mark the pathfinding cells (world / <see cref="RoadPathfinder.CellSize"/>, rounded) a road climbed on a staircase.</summary>
    public static void MarkLaid(IEnumerable<Vector2> points)
    {
        foreach (Vector2 p in points)
            s_cells[(Mathf.RoundToInt(p.x / RoadPathfinder.CellSize), Mathf.RoundToInt(p.y / RoadPathfinder.CellSize))] = 0;
    }

    // Every step of every road search asks these two, on every worker. ContainsKey alone: in the
    // game's Mono, ConcurrentDictionary.IsEmpty takes every one of the dictionary's locks (up to
    // 1024) on each call, and an IsEmpty guard here tripled in-game generation.
    // .NET 10 reads it without locks, so offline never showed it.
    public static bool OnLaidStair(int cellX, int cellZ) => s_cells.ContainsKey((cellX, cellZ));

    /// <summary>Pathfinding cells a staircase of the network stands on (laid, or loaded), its end cells apart:
    /// no road is built through them.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), byte> s_standing = new();

    /// <summary>Mark the cells under a laid run's walked pieces as closed to road,
    /// but for the cells of its two ends, where roads meet it.</summary>
    public static void MarkStanding(StairRun run)
    {
        foreach (var cell in StandingCells(run)) s_standing[cell] = 0;
    }

    /// <summary>The cells under a run's walked pieces but for its two end cells (<see cref="MarkStanding"/>).</summary>
    private static HashSet<(int, int)> StandingCells(StairRun run)
    {
        (int, int) Cell(float x, float z) => (Mathf.RoundToInt(x / RoadPathfinder.CellSize), Mathf.RoundToInt(z / RoadPathfinder.CellSize));
        var ends = new HashSet<(int, int)> { Cell(run.From.x, run.From.y), Cell(run.To.x, run.To.y) };
        var cells = new HashSet<(int, int)>();
        foreach (StructurePiece p in run.Pieces)
        {
            if (p.Kind == StructurePieceKind.StairSupport) continue;
            var cell = Cell(p.Position.x, p.Position.z);
            if (!ends.Contains(cell)) cells.Add(cell);
        }
        return cells;
    }

    /// <summary>
    /// A standing run replaced by <paramref name="trimmed"/>, the same staircase cut back (RoadStairRuns.LayLanding): a
    /// reader holding the run as it stood keeps it whole, the cells under the pieces taken away are open to road again but
    /// where another run stands on them, and the structure plans are recomputed on their next use. False if
    /// <paramref name="standing"/> is not in the network.
    /// </summary>
    public static bool Swap(StairRun standing, StairRun trimmed)
    {
        lock (s_runs)
        {
            int i = s_runs.IndexOf(standing);
            if (i < 0) return false;
            s_runs[i] = trimmed;
            var stillStanding = new HashSet<(int, int)>();
            foreach (StairRun run in s_runs) stillStanding.UnionWith(StandingCells(run));
            foreach (var cell in StandingCells(standing))
                if (!stillStanding.Contains(cell)) s_standing.TryRemove(cell, out _);
            MarkStanding(trimmed);
        }
        StructurePlans.InvalidatePlans();
        return true;
    }

    /// <summary>Whether a staircase of the network stands on this pathfinding cell.</summary>
    public static bool Standing(int cellX, int cellZ) => s_standing.ContainsKey((cellX, cellZ));

    /// <summary>Pieces of the network's staircases within <paramref name="reach"/> across of a point.</summary>
    public static List<StructurePiece> PiecesNear(Vector3 point, float reach)
    {
        var near = new List<StructurePiece>();
        var at = new Vector2(point.x, point.z);
        lock (s_runs)
            foreach (StairRun run in s_runs)
            {
                if (Vector2.Distance(at, run.From) > 400f && Vector2.Distance(at, run.To) > 400f) continue;
                foreach (StructurePiece p in run.Pieces)
                    if (Vector2.Distance(at, new Vector2(p.Position.x, p.Position.z)) < reach) near.Add(p);
            }
        return near;
    }

    /// <summary>
    /// Pieces of the network's staircases within <paramref name="reach"/> across of any of
    /// <paramref name="points"/>, under one lock: a staircase's overlap check asked
    /// <see cref="PiecesNear"/> once for each of its pieces, each time taking the lock every island
    /// worker shares and scanning the runs again. Empty for no points.
    /// </summary>
    public static List<StructurePiece> PiecesNearAny(IReadOnlyList<Vector2> points, float reach)
    {
        var near = new List<StructurePiece>();
        if (points.Count == 0) return near;
        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity, maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        foreach (Vector2 q in points)
        {
            minX = Mathf.Min(minX, q.x); maxX = Mathf.Max(maxX, q.x);
            minZ = Mathf.Min(minZ, q.y); maxZ = Mathf.Max(maxZ, q.y);
        }
        // A run whose ends are both further than this from the points' box holds no piece near them
        // (as PiecesNear, by 400 m from its ends, widened by the box).
        var middle = new Vector2((minX + maxX) / 2f, (minZ + maxZ) / 2f);
        float runReach = 400f + Vector2.Distance(middle, new Vector2(maxX, maxZ));
        lock (s_runs)
            foreach (StairRun run in s_runs)
            {
                if (Vector2.Distance(middle, run.From) > runReach && Vector2.Distance(middle, run.To) > runReach) continue;
                foreach (StructurePiece p in run.Pieces)
                {
                    var at = new Vector2(p.Position.x, p.Position.z);
                    if (at.x < minX - reach || at.x > maxX + reach || at.y < minZ - reach || at.y > maxZ + reach) continue;
                    foreach (Vector2 q in points)
                        if (Vector2.Distance(at, q) < reach) { near.Add(p); break; }
                }
            }
        return near;
    }

    /// <summary>A run with a walked piece within <paramref name="reach"/> of each of
    /// two points, and those pieces; null if none.</summary>
    public static (StairRun run, StructurePiece nearA, StructurePiece nearB)? Carrying(Vector2 a, Vector2 b, float reach)
    {
        lock (s_runs)
            foreach (StairRun run in s_runs)
            {
                StructurePiece? nearA = null, nearB = null;
                float bestA = reach, bestB = reach;
                foreach (StructurePiece p in run.Pieces)
                {
                    if (p.Kind == StructurePieceKind.StairSupport) continue;
                    var at = new Vector2(p.Position.x, p.Position.z);
                    float da = Vector2.Distance(at, a), db = Vector2.Distance(at, b);
                    if (da <= bestA) { bestA = da; nearA = p; }
                    if (db <= bestB) { bestB = db; nearB = p; }
                }
                if (nearA != null && nearB != null && nearA != nearB) return (run, nearA, nearB);
            }
        return null;
    }

    /// <summary>The run already laid between these two ends (either way round, within <paramref name="reach"/>), if any.</summary>
    public static StairRun? Between(Vector2 a, Vector2 b, float reach)
    {
        lock (s_runs)
            foreach (StairRun run in s_runs)
                if ((Vector2.Distance(run.From, a) <= reach && Vector2.Distance(run.To, b) <= reach)
                    || (Vector2.Distance(run.From, b) <= reach && Vector2.Distance(run.To, a) <= reach))
                    return run;
        return null;
    }

    /// <summary>
    /// Every run settled on the finished ground (<see cref="BuriedPieces"/>): its columns carried on down where the
    /// earthwork lowered the ground under them, then its pieces wholly under the ground dropped, which the game breaks as
    /// soon as it weighs them. Returns how many pillars were added and pieces dropped, adding each dropped to <paramref name="byPrefab"/>.
    /// </summary>
    public static (int carried, int dropped) SettleOnFinishedGround(Func<int, int, float> vertex, IDictionary<string, int>? byPrefab = null)
    {
        int carried = 0, dropped = 0;
        lock (s_runs)
            foreach (StairRun run in s_runs)
            {
                carried += BuriedPieces.CarryColumns(run, vertex);
                dropped += BuriedPieces.Drop(run, vertex, byPrefab);
            }
        if (carried + dropped > 0) StructurePlans.InvalidatePlans();
        return (carried, dropped);
    }

    /// <summary>Add a run; the structure plans are recomputed on their next use.</summary>
    public static void Add(StairRun run)
    {
        lock (s_runs) s_runs.Add(run);
        StructurePlans.InvalidatePlans();
    }

    /// <summary>Runs in a fixed order, whichever island laid its runs first:
    /// generation lays islands in parallel, and the saved bytes must not
    /// depend on which finished first.</summary>
    public static void SortCanonical()
    {
        lock (s_runs) s_runs.Sort((a, b) =>
        {
            int c = a.From.x.CompareTo(b.From.x);
            if (c == 0) c = a.From.y.CompareTo(b.From.y);
            if (c == 0) c = a.To.x.CompareTo(b.To.x);
            return c != 0 ? c : a.To.y.CompareTo(b.To.y);
        });
    }

    public string LogTag => "[STAIRS]";

    public IEnumerable<StructurePiece> Plan(WorldGenerator world, int seed)
    {
        foreach (StairRun run in Runs)
            foreach (StructurePiece piece in run.Pieces)
                yield return piece;
    }

    /// <summary>Vegetation kept off every walked piece: a circle round its 2 m footprint.</summary>
    public void AddClearAreas(List<StructurePiece> zonePieces, List<(Vector3 centre, float radius)> areas)
    {
        foreach (StructurePiece piece in zonePieces)
            if (piece.Kind != StructurePieceKind.StairSupport)
                areas.Add((piece.Position, StairLayout.PieceRun * 0.75f));
    }

    public void AddClearances(Vector3 centre, float radius, List<StructureClearance> clearances)
    {
        var c = new Vector2(centre.x, centre.z);
        float reach = radius + StairLayout.PieceRun;
        foreach (StairRun run in Runs)
            foreach (StairClearance box in run.Clearances)
                if (Vector2.Distance(new Vector2(box.Centre.x, box.Centre.z), c) <= reach)
                    clearances.Add(new StructureClearance(box.Centre, box.Half, box.YawDegrees, box.Surface));
    }

    /// <summary>The format the staircase record is written in, 3: per run its ends, width, whether it keeps a place's rocks
    /// and its kit; per piece its kind, prefab, position, yaw and whether it is cantilevered; then its clearances. A number
    /// no earlier build wrote, so an earlier build refuses the record whole instead of misreading it. Records 1 (without the
    /// kept-rocks flag) and 2 (without the kit and the cantilever flag) are read as before.</summary>
    private const int FormatVersion = 3;

    public static byte[] Serialize()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(FormatVersion);
        IReadOnlyList<StairRun> runs = Runs;
        writer.Write(runs.Count);
        foreach (StairRun run in runs)
        {
            writer.Write(run.From.x); writer.Write(run.From.y);
            writer.Write(run.To.x); writer.Write(run.To.y);
            writer.Write(run.Width);
            writer.Write(run.KeepsSiteRocks);
            writer.Write(run.Kit?.Name ?? "");
            writer.Write(run.Pieces.Count);
            foreach (StructurePiece p in run.Pieces)
            {
                writer.Write((int)p.Kind); writer.Write(p.Prefab);
                writer.Write(p.Position.x); writer.Write(p.Position.y); writer.Write(p.Position.z);
                writer.Write(p.YawDegrees);
                writer.Write(p.Cantilevered);
            }
            writer.Write(run.Clearances.Count);
            foreach (StairClearance b in run.Clearances)
            {
                writer.Write(b.Centre.x); writer.Write(b.Centre.y); writer.Write(b.Centre.z);
                writer.Write(b.Half.x); writer.Write(b.Half.y); writer.Write(b.Half.z);
                writer.Write(b.YawDegrees); writer.Write(b.Surface);
            }
        }
        return stream.ToArray();
    }

    /// <summary>Replace the runs with saved ones (none for null or empty data). Malformed data throws, changing nothing.</summary>
    public static void Load(byte[]? bytes) => Replace(Parse(bytes));

    /// <summary>Set the runs to ones already parsed (<see cref="Load"/>).</summary>
    public static void Replace(List<StairRun> runs)
    {
        lock (s_runs)
        {
            s_runs.Clear();
            s_runs.AddRange(runs);
        }
        // A saved staircase stands for the roads planned on the loaded network (a manual road's search) as a laid one does.
        s_standing.Clear();
        foreach (StairRun run in runs) MarkStanding(run);
        StructurePlans.InvalidatePlans();
    }

    /// <summary>Saved runs, read without touching the current ones. Malformed data throws.</summary>
    public static List<StairRun> Parse(byte[]? bytes)
    {
        var loaded = new List<StairRun>();
        if (bytes != null && bytes.Length > 0)
        {
            using var stream = new MemoryStream(bytes);
            using var reader = new BinaryReader(stream);
            int version = reader.ReadInt32();
            if (version < 1 || version > FormatVersion) throw new InvalidDataException("Unknown stair run data version.");
            int runs = reader.ReadInt32();
            if (runs < 0 || runs > 10000) throw new InvalidDataException("Invalid stair run count.");
            int total = 0;
            for (int i = 0; i < runs; i++)
            {
                var run = new StairRun
                {
                    From = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                    To = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                    Width = reader.ReadSingle(),
                    KeepsSiteRocks = version >= 2 && reader.ReadBoolean(),
                };
                string kit = version >= 3 ? reader.ReadString() : "";
                if (kit.Length > 0) run.Kit = StairKit.Named(kit) ?? throw new InvalidDataException("Unknown stair kit.");
                int pieces = reader.ReadInt32();
                if (pieces < 0 || (total += pieces) > 500000) throw new InvalidDataException("Invalid stair piece count.");
                for (int j = 0; j < pieces; j++)
                {
                    int kind = reader.ReadInt32();
                    string prefab = reader.ReadString();
                    var pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    float yaw = reader.ReadSingle();
                    bool cantilevered = version >= 3 && reader.ReadBoolean();
                    if (prefab.Length > 100 || !Enum.IsDefined(typeof(StructurePieceKind), kind) ||
                        !ManualRoadDraft.ValidPoint(new Vector2(pos.x, pos.z)) || !ManualRoadDraft.IsFinite(pos.y) || !ManualRoadDraft.IsFinite(yaw))
                        throw new InvalidDataException("Invalid stair piece.");
                    run.Pieces.Add(new StructurePiece { Kind = (StructurePieceKind)kind, Prefab = prefab, Position = pos, YawDegrees = yaw, Cantilevered = cantilevered });
                }
                int boxes = reader.ReadInt32();
                if (boxes < 0 || boxes > 500000) throw new InvalidDataException("Invalid stair clearance count.");
                for (int j = 0; j < boxes; j++)
                {
                    var centre = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    var half = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    float yaw = reader.ReadSingle(), surface = reader.ReadSingle();
                    if (!ManualRoadDraft.ValidPoint(new Vector2(centre.x, centre.z)) || !ManualRoadDraft.IsFinite(centre.y) ||
                        !ManualRoadDraft.IsFinite(half.x) || !ManualRoadDraft.IsFinite(half.y) || !ManualRoadDraft.IsFinite(half.z) ||
                        !ManualRoadDraft.IsFinite(yaw) || !ManualRoadDraft.IsFinite(surface))
                        throw new InvalidDataException("Invalid stair clearance.");
                    run.Clearances.Add(new StairClearance(centre, half, yaw, surface));
                }
                loaded.Add(run);
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing stair run data.");
        }
        return loaded;
    }
}
