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
    /// <summary>Where the road meets the staircase at <see cref="From"/>'s end and at <see cref="To"/>'s, its own last point
    /// there, where that is not the end itself; null: the end itself. A road's own staircase is recorded between the path
    /// points it climbs, and the road stops on its foot and on the far edge of the top landing laid on past them (staircase
    /// -1880,6632 -> -1944,6608: the road came onto its landing at -1882.8,6634.0, 3.4 m on from the recorded end).</summary>
    public Vector2? FromMeet, ToMeet;
    /// <summary>The kit it was laid in, decided once when laid: the one its pieces stand in (StairKit.ForPlan); null for a
    /// run read from a record without it, whose pieces name their kit.</summary>
    public StairKit? Kit;
    /// <summary>The <see cref="StairLayout.LayoutVersion"/> that laid it; 0 for a run read from a record without it.</summary>
    public int Layout = StairLayout.LayoutVersion;
    /// <summary>An approach onto a place the game places after its roads (RoadStairRuns.LaySiteApproach): the place's centre
    /// and the ground there its levelling is reckoned from, the approach laid out over the ground the place is expected to
    /// leave, which its kit chooses its supports on too (RoadStairRuns.PlanningGround). Null for any other staircase, and
    /// for one read from a record without it.</summary>
    public (Vector2 Centre, float Ground)? ExpectedPlace;
    public readonly List<StructurePiece> Pieces = new();
    /// <summary>The island worker that laid it (<see cref="CrossIslandReads.Tag"/>): another island's worker does not see it.
    /// 0 for none. Not saved.</summary>
    public byte Island;

    /// <summary>The kit's name for listings: the stored kit, else the one its first step names.</summary>
    public string KitName => Kit?.Name ?? (Pieces.Find(p => p.Kind == StructurePieceKind.StairStep)?.Prefab.Split('_')[0] ?? "?");

    /// <summary>Its two ends, From's then To's, as the kits meet them (<see cref="StairKit.Dress"/>): the one owner of where a
    /// staircase meets its road or its place. To's end is a place's landing where the staircase is one onto a place
    /// (<see cref="KeepsSiteRocks"/>): an entry's or an approach's runs from the road's end to the place (RoadStairRuns.Span).</summary>
    public StairEnd[] Ends => new[] { new StairEnd(From, FromMeet ?? From, place: false), new StairEnd(To, ToMeet ?? To, KeepsSiteRocks) };

    public override string ToString() => System.FormattableString.Invariant($"{KitName} staircase {From} -> {To}, {Pieces.Count} pieces");

    /// <summary>Whether a walked piece of it (a step or a floor) may stand within <paramref name="reach"/> across of the box
    /// <paramref name="min"/>..<paramref name="max"/>: its walked pieces' box comes that close (a cheap test before any piece by
    /// piece one; no allocation).</summary>
    public bool WalkedNear(Vector2 min, Vector2 max, float reach)
    {
        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity, maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        foreach (StructurePiece p in Pieces)
        {
            if (p.Kind is not (StructurePieceKind.StairStep or StructurePieceKind.StairFloor)) continue;
            minX = Mathf.Min(minX, p.Position.x); maxX = Mathf.Max(maxX, p.Position.x);
            minZ = Mathf.Min(minZ, p.Position.z); maxZ = Mathf.Max(maxZ, p.Position.z);
        }
        return minX <= max.x + reach && maxX >= min.x - reach && minZ <= max.y + reach && maxZ >= min.y - reach;
    }
    public readonly List<StairClearance> Clearances = new();
    /// <summary>The finished ground the run was settled on (<see cref="StairStructures.Settle"/>), which every terrain write
    /// levels to under it (<see cref="StructureGround.Held"/>); empty until it is settled.</summary>
    public readonly List<GroundPin> Ground = new();

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

    /// <summary>A laid run's piece as the layout's (<see cref="FromPlan"/>'s the other way): a step a stair, a floor a landing,
    /// a filler a filler, anything else a support.</summary>
    internal static StairPiece PieceOf(StructurePiece p) => new()
    {
        Kind = p.Kind switch
        {
            StructurePieceKind.StairStep => StairPieceKind.Stair,
            StructurePieceKind.StairFloor => StairPieceKind.Landing,
            StructurePieceKind.StairFiller => StairPieceKind.Filler,
            _ => StairPieceKind.Support,
        },
        Prefab = p.Prefab, Position = p.Position, YawDegrees = p.YawDegrees, Cantilevered = p.Cantilevered,
    };
}

/// <summary>One end of a staircase (<see cref="StairRun.Ends"/>): the point it was recorded at, where the road comes onto it
/// (<see cref="Meet"/>: the road's last point, or the place's ground the staircase ends on), and whether it ends on a
/// place's landing instead of a road.</summary>
public readonly struct StairEnd
{
    public readonly Vector2 At, Meet;
    public readonly bool Place;

    public StairEnd(Vector2 at, Vector2 meet, bool place)
    {
        At = at; Meet = meet; Place = place;
    }
}

/// <summary>
/// Staircases as a structure source. Unlike a bridge, a staircase's layout is not
/// recomputed from the network on demand: a valley-to-summit plan takes seconds,
/// which a zone load cannot wait for. So each run's layout is made once, when the
/// run is added, and saved with the network with its clearances and the ground it
/// was settled on (<see cref="Serialize"/>). Its kit's pieces are laid over the
/// layout when its first zone is built, as a bridge's are solved, and saved then
/// (<see cref="Build"/>, <see cref="BuiltStairs"/>): every later zone lays the saved
/// pieces, and a staircase nobody has reached takes the kits of the build that
/// reaches it. Pure logic.
/// </summary>
public sealed class StairStructures : IStructureSource
{
    private static readonly List<StairRun> s_runs = new();
    /// <summary>Pathfinding cells a generated road climbed on a staircase, so a
    /// later road over the same ground walks that staircase instead of laying a
    /// second beside it. Generation only: never saved.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), byte> s_cells = new();

    /// <summary>The runs as they are now: a copy taken under the lock islands add under, so a reader never walks a list
    /// being added to. On an island worker, its own island's and untagged runs (CrossIslandReads).</summary>
    public static IReadOnlyList<StairRun> Runs
    {
        get
        {
            int me = CrossIslandReads.Me;
            lock (s_runs) return me == 0 ? s_runs.ToArray() : s_runs.FindAll(r => CrossIslandReads.Sees(me, r.Island)).ToArray();
        }
    }

    /// <summary>The approaches onto places (<see cref="StairRun.KeepsSiteRocks"/>) with a walked piece's box within
    /// <paramref name="reach"/> of the box <paramref name="min"/>..<paramref name="max"/> (<see cref="StairRun.WalkedNear"/>), as
    /// <see cref="Runs"/> reads them: under the lock, on an island worker its own island's and untagged runs. Only those are
    /// copied.</summary>
    public static IReadOnlyList<StairRun> ApproachesNear(Vector2 min, Vector2 max, float reach)
    {
        int me = CrossIslandReads.Me;
        var near = new List<StairRun>();
        lock (s_runs)
            foreach (StairRun r in s_runs)
                if (r.KeepsSiteRocks && (me == 0 || CrossIslandReads.Sees(me, r.Island)) && r.WalkedNear(min, max, reach)) near.Add(r);
        return near;
    }

    /// <summary>
    /// Per whole-metre vertex under a walked piece of the network's staircases (a step, a floor, a filler; not a support), the
    /// lowest a staircase's cut may take the ground there: the lowest walking surface over it, less
    /// <see cref="StairLayout.CutBelowSurface"/>, with the island that laid it. A cut lowers the ground only down to just under a
    /// walk (RoadTerrainModifier.CutAt): a flight's cut and its batter reaching under another flight beside it, or a row's
    /// reaching under the higher treads of its own, dug a hollow under the walk its supports stood in. Made again as a whole and swapped in (<see cref="RebuildWalkFloor"/>),
    /// so a reader on another thread never sees it half built.
    /// </summary>
    private static volatile System.Collections.Concurrent.ConcurrentDictionary<(int, int), (float floor, byte island)> s_walkFloor = new();

    /// <summary>The lowest a staircase's cut may take the ground at a whole-metre vertex (<see cref="s_walkFloor"/>), negative
    /// infinity where no walked piece this thread sees stands over it. Any thread.</summary>
    public static float CutFloor(Vector2 vertex) =>
        s_walkFloor.TryGetValue((Mathf.RoundToInt(vertex.x), Mathf.RoundToInt(vertex.y)), out var at) && CrossIslandReads.Sees(at.island)
            ? at.floor : float.NegativeInfinity;

    /// <summary>A run's walked pieces into <see cref="s_walkFloor"/>: every vertex inside a piece's footprint
    /// (<see cref="StairLayout.Footprint"/>), at its walking height there, a stair's rising along it.</summary>
    private static void AddWalkFloor(StairRun run, System.Collections.Concurrent.ConcurrentDictionary<(int, int), (float floor, byte island)> into)
    {
        foreach (StructurePiece p in run.Pieces)
        {
            if (p.Kind is not (StructurePieceKind.StairStep or StructurePieceKind.StairFloor or StructurePieceKind.StairFiller)) continue;
            var (back, heading, across, depth, height) = StairLayout.Footprint(StairRun.PieceOf(p));
            bool stair = p.Kind == StructurePieceKind.StairStep;
            Vector2 dir = StairLayout.Forward(heading), right = StairLayout.Right(heading);
            float reach = Mathf.Sqrt(across * across / 4f + depth * depth);
            for (int x = Mathf.FloorToInt(back.x - reach); x <= Mathf.CeilToInt(back.x + reach); x++)
                for (int z = Mathf.FloorToInt(back.y - reach); z <= Mathf.CeilToInt(back.y + reach); z++)
                {
                    Vector2 off = new Vector2(x, z) - back;
                    float along = Vector2.Dot(off, dir), side = Vector2.Dot(off, right);
                    if (along < 0f || along > depth || Mathf.Abs(side) > across / 2f) continue;
                    float floor = height + (stair ? StairLayout.PieceRise * along / StairLayout.PieceRun : 0f) - StairLayout.CutBelowSurface;
                    into.AddOrUpdate((x, z), (floor, run.Island), (_, was) => floor < was.floor ? (floor, run.Island) : was);
                }
        }
    }

    /// <summary><see cref="s_walkFloor"/> made again from the runs as they now stand (a run swapped or the runs replaced).</summary>
    private static void RebuildWalkFloor()
    {
        lock (s_runs)
        {
            var fresh = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), (float floor, byte island)>();
            foreach (StairRun run in s_runs) AddWalkFloor(run, fresh);
            s_walkFloor = fresh;
        }
    }

    /// <summary>No staircases: the network's are gone, and the pieces their first builds saved with them.</summary>
    public static void Reset()
    {
        lock (s_runs) { s_runs.Clear(); s_walkFloor = new(); }
        s_cells.Clear();
        s_standing.Clear();
        BuiltStairs.Reset();
    }

    /// <summary>Mark the pathfinding cells (world / <see cref="RoadPathfinder.CellSize"/>, rounded) a road climbed on a staircase,
    /// each holding the island that marked it (<see cref="CrossIslandReads.Tag"/>).</summary>
    public static void MarkLaid(IEnumerable<Vector2> points)
    {
        RoadNetworkGenerator.IslandNetworkChanged();
        byte tag = CrossIslandReads.Tag;
        foreach (Vector2 p in points)
            s_cells[(Mathf.RoundToInt(p.x / RoadPathfinder.CellSize), Mathf.RoundToInt(p.y / RoadPathfinder.CellSize))] = tag;
    }

    // Every step of every road search asks these two, on every worker. ContainsKey alone: in the
    // game's Mono, ConcurrentDictionary.IsEmpty takes every one of the dictionary's locks (up to
    // 1024) on each call, and an IsEmpty guard here tripled in-game generation.
    // .NET 10 reads it without locks, so offline never showed it.
    // A cell found is checked against this thread's island (CrossIslandReads), the cells not found, nearly all, are not.
    public static bool OnLaidStair(int cellX, int cellZ) => s_cells.TryGetValue((cellX, cellZ), out byte tag) && Seen(tag, cellX, cellZ);

    /// <summary>Whether this thread sees a cell marked by island <paramref name="tag"/> (CrossIslandReads), counted if not.</summary>
    private static bool Seen(byte tag, int cellX, int cellZ)
    {
        if (CrossIslandReads.Sees(tag)) return true;
        CrossIslandReads.Skipped(CrossIslandReads.Kind.Staircases, tag, new Vector2(cellX * RoadPathfinder.CellSize, cellZ * RoadPathfinder.CellSize));
        return false;
    }

    /// <summary>Pathfinding cells a staircase of the network stands on (laid, or loaded), its end cells apart:
    /// no road is built through them.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), byte> s_standing = new();

    /// <summary>Mark the cells under a laid run's walked pieces as closed to road,
    /// but for the cells of its two ends, where roads meet it.</summary>
    public static void MarkStanding(StairRun run)
    {
        RoadNetworkGenerator.IslandNetworkChanged();
        foreach (var cell in StandingCells(run)) s_standing[cell] = run.Island;
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
            RoadNetworkGenerator.IslandNetworkChanged();
            // The ground it stands on stays the ground it was settled on.
            if (trimmed.Ground.Count == 0) trimmed.Ground.AddRange(standing.Ground);
            trimmed.Island = standing.Island;
            s_runs[i] = trimmed;
            RebuildWalkFloor();
            var stillStanding = new HashSet<(int, int)>();
            foreach (StairRun run in s_runs) stillStanding.UnionWith(StandingCells(run));
            foreach (var cell in StandingCells(standing))
                if (!stillStanding.Contains(cell)) s_standing.TryRemove(cell, out _);
            MarkStanding(trimmed);
        }
        // Pieces saved for the run as it stood are not the trimmed run's: it is laid from its own layout.
        if (BuiltStairs.Of(standing) is BuiltStair saved) BuiltStairs.Store.Forget(new[] { saved });
        StructurePlans.InvalidatePlans();
        return true;
    }

    /// <summary>Whether a staircase of the network stands on this pathfinding cell.</summary>
    public static bool Standing(int cellX, int cellZ) => s_standing.TryGetValue((cellX, cellZ), out byte tag) && Seen(tag, cellX, cellZ);

    /// <summary>Pieces of the network's staircases within <paramref name="reach"/> across of a point.</summary>
    public static List<StructurePiece> PiecesNear(Vector3 point, float reach)
    {
        var near = new List<StructurePiece>();
        var at = new Vector2(point.x, point.z);
        int me = CrossIslandReads.Me;
        byte skipped = 0;
        lock (s_runs)
            foreach (StairRun run in s_runs)
            {
                if (Vector2.Distance(at, run.From) > 400f && Vector2.Distance(at, run.To) > 400f) continue;
                bool seen = CrossIslandReads.Sees(me, run.Island);
                foreach (StructurePiece p in run.Pieces)
                    if (Vector2.Distance(at, new Vector2(p.Position.x, p.Position.z)) < reach)
                    {
                        if (!seen) { skipped = run.Island; break; }
                        near.Add(p);
                    }
            }
        if (skipped != 0) CrossIslandReads.Skipped(CrossIslandReads.Kind.Staircases, skipped, at);
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
        int me = CrossIslandReads.Me;
        byte skipped = 0;
        lock (s_runs)
            foreach (StairRun run in s_runs)
            {
                if (Vector2.Distance(middle, run.From) > runReach && Vector2.Distance(middle, run.To) > runReach) continue;
                bool seen = CrossIslandReads.Sees(me, run.Island);
                foreach (StructurePiece p in run.Pieces)
                {
                    var at = new Vector2(p.Position.x, p.Position.z);
                    if (at.x < minX - reach || at.x > maxX + reach || at.y < minZ - reach || at.y > maxZ + reach) continue;
                    bool within = false;
                    foreach (Vector2 q in points)
                        if (Vector2.Distance(at, q) < reach) { within = true; break; }
                    if (!within) continue;
                    if (!seen) { skipped = run.Island; break; }
                    near.Add(p);
                }
            }
        if (skipped != 0) CrossIslandReads.Skipped(CrossIslandReads.Kind.Staircases, skipped, middle);
        return near;
    }

    /// <summary>A run with a walked piece within <paramref name="reach"/> of each of
    /// two points, and those pieces; null if none.</summary>
    public static (StairRun run, StructurePiece nearA, StructurePiece nearB)? Carrying(Vector2 a, Vector2 b, float reach)
    {
        int me = CrossIslandReads.Me;
        byte skipped = 0;
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
                if (nearA != null && nearB != null && nearA != nearB)
                {
                    if (!CrossIslandReads.Sees(me, run.Island)) { skipped = run.Island; continue; }
                    if (skipped != 0) CrossIslandReads.Skipped(CrossIslandReads.Kind.Staircases, skipped, a);
                    return (run, nearA, nearB);
                }
            }
        if (skipped != 0) CrossIslandReads.Skipped(CrossIslandReads.Kind.Staircases, skipped, a);
        return null;
    }

    /// <summary>The run already laid between these two ends (either way round, within <paramref name="reach"/>), if any.</summary>
    public static StairRun? Between(Vector2 a, Vector2 b, float reach)
    {
        int me = CrossIslandReads.Me;
        byte skipped = 0;
        StairRun? found = null;
        lock (s_runs)
            foreach (StairRun run in s_runs)
                if ((Vector2.Distance(run.From, a) <= reach && Vector2.Distance(run.To, b) <= reach)
                    || (Vector2.Distance(run.From, b) <= reach && Vector2.Distance(run.To, a) <= reach))
                {
                    if (!CrossIslandReads.Sees(me, run.Island)) { skipped = run.Island; continue; }
                    found = run;
                    break;
                }
        if (skipped != 0) CrossIslandReads.Skipped(CrossIslandReads.Kind.Staircases, skipped, a);
        return found;
    }

    /// <summary>
    /// Every run settled on the finished ground (<see cref="Settle"/>). Returns how many pieces were dropped, adding each
    /// to <paramref name="byPrefab"/>.
    /// </summary>
    public static int SettleOnFinishedGround(Func<int, int, float> vertex, IDictionary<string, int>? byPrefab = null)
    {
        int dropped = 0;
        lock (s_runs)
            foreach (StairRun run in s_runs)
                dropped += Settle(run, vertex, byPrefab);
        // The cut floor from the pieces that stand, as a loaded network makes it (Replace).
        if (dropped > 0) { RebuildWalkFloor(); StructurePlans.InvalidatePlans(); }
        return dropped;
    }

    /// <summary>
    /// A run settled on the finished ground (<see cref="BuriedPieces"/>): its pieces wholly under the ground dropped, which
    /// the game breaks as soon as it weighs them; then the ground it stands on saved with it and held
    /// (<see cref="StructureGround.Measure"/>). Returns how many were dropped.
    /// </summary>
    public static int Settle(StairRun run, Func<int, int, float> vertex, IDictionary<string, int>? byPrefab = null)
    {
        int dropped = BuriedPieces.Drop(run, vertex, byPrefab);
        run.Ground.Clear();
        run.Ground.AddRange(StructureGround.Measure(run.Pieces, vertex));
        StructureGround.Hold(run.Ground);
        return dropped;
    }

    /// <summary>Add a run; the structure plans are recomputed on their next use.</summary>
    public static void Add(StairRun run)
    {
        run.Island = CrossIslandReads.Tag;
        lock (s_runs) { s_runs.Add(run); AddWalkFloor(run, s_walkFloor); }
        RoadNetworkGenerator.IslandNetworkChanged();
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
        {
            List<StructurePiece> pieces = PiecesOf(run, world);
            StairRun saved = run;
            var planned = new PlannedSite(pieces, () => BuiltStairs.Of(saved) != null, w => Build(saved, w, pieces));
            foreach (StructurePiece piece in pieces)
            {
                piece.Site = planned;
                yield return piece;
            }
        }
    }

    /// <summary>Whether a piece kind is one a staircase's layout holds (steps, floors, fillers, columns and brackets): any
    /// other a kit adds over it (<see cref="StairKit.Dress"/>).</summary>
    internal static bool IsLayoutKind(StructurePieceKind kind) =>
        kind is StructurePieceKind.StairStep or StructurePieceKind.StairFloor or StructurePieceKind.StairFiller or StructurePieceKind.StairSupport;

    /// <summary>The kit a staircase is laid in: the one its layout was made for (<see cref="StairRun.Kit"/>), else the one its
    /// steps name; stone for neither.</summary>
    public static StairKit KitOf(StairRun run) => run.Kit ?? StairKit.Named(run.KitName) ?? StairKit.Stone;

    /// <summary>A staircase's kit laying its pieces over its layout now: this build's kits (<see cref="StairKit.Dress"/>),
    /// its supports chosen on the ground its layout was laid out on (<see cref="RoadStairRuns.PlanningGround"/>), the
    /// ground as places will leave it: chosen on the natural ground, a flight on a placed place's raised landing was held
    /// up as if over the slope under it, its arches and cornices run down through the landing; its walls down to the
    /// finished ground (<see cref="RoadStairRuns.FinishedGround"/>). A later build's kits stand here; so may a test's.</summary>
    internal static Func<StairRun, WorldGenerator, List<StructurePiece>> Dresser = (run, world) =>
    {
        var shortfall = new StairSupport.Shortfall();
        List<StructurePiece> pieces = KitOf(run).Dress(run, RoadStairRuns.PlanningGround(run, world), RoadStairRuns.FinishedGround(run, world), shortfall: shortfall,
            beside: MetAtEnds(run), biomeAt: (x, z) => world.GetBiome(x, z));
        if (shortfall.Any) SayShortfall(run, shortfall);
        return pieces;
    };

    /// <summary>How near another staircase's end must stand to one of <see cref="MetAtEnds"/>'s run's for the two to meet end to
    /// end, and how far round it their walking pieces are taken.</summary>
    private const float EndToEnd = 1.5f, EndToEndReach = 6f;

    /// <summary>The walking pieces (steps, floors, fillers) of every other staircase with an end where one of
    /// <paramref name="run"/>'s is (its recorded point or where the road meets it, within <see cref="EndToEnd"/>), those within
    /// <see cref="EndToEndReach"/> of it: two staircases meeting end to end, the walk carried on from one onto the other
    /// (two black marble staircases in game, a 2x2 connector at -6921.0,-3653.8 with a flight on each side). Each is
    /// laid by its own staircase; the railing walks on across them (<see cref="StairRailing.Lay"/>).</summary>
    internal static List<StructurePiece> MetAtEnds(StairRun run)
    {
        var beside = new List<StructurePiece>();
        Vector2[] Points(StairRun r) => new[] { r.From, r.To, r.FromMeet ?? r.From, r.ToMeet ?? r.To };
        Vector2[] mine = Points(run);
        foreach (StairRun other in Runs)
        {
            if (ReferenceEquals(other, run)) continue;
            foreach (Vector2 end in mine)
            {
                if (!Array.Exists(Points(other), p => Vector2.Distance(p, end) <= EndToEnd)) continue;
                foreach (StructurePiece p in other.Pieces)
                    if (p.Kind is StructurePieceKind.StairStep or StructurePieceKind.StairFloor or StructurePieceKind.StairFiller
                        && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), end) <= EndToEndReach && !beside.Contains(p))
                        beside.Add(p);
            }
        }
        return beside;
    }

    /// <summary>The staircases whose shortfall was said already: a staircase is dressed again each time the plans are made.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StairRun, object> s_saidShortfall = new();

    /// <summary>Say, once per staircase, what its kit could not hold up as its search priced it (<see cref="StairSupport.Lay"/>):
    /// a walking piece left with nothing under it, a wall stopped short of the ground, a support the game's support rule would
    /// break, or a walking piece it holds under the margin. The search never lays any of them (an
    /// older build's saved layout, or the search and the kit reading different ground): unsaid, a check in game found a floating stair
    /// by eye or not at all.</summary>
    private static void SayShortfall(StairRun run, StairSupport.Shortfall shortfall)
    {
        lock (s_saidShortfall)
        {
            if (s_saidShortfall.TryGetValue(run, out _)) return;
            s_saidShortfall.Add(run, run);
        }
        static string At(List<Vector3> points) => string.Join(" ", points.ConvertAll(p =>
            FormattableString.Invariant($"({p.x:F1}, {p.y:F1}, {p.z:F1})")));
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(FormattableString.Invariant(
            $"[STAIRS] staircase {KitOf(run).Name} ({run.From.x:F1}, {run.From.y:F1}) -> ({run.To.x:F1}, {run.To.y:F1}){(run.KeepsSiteRocks ? " at a place" : "")}: ") +
            string.Join("; ", Array.FindAll(new[]
            {
                shortfall.Unheld.Count > 0 ? $"{shortfall.Unheld.Count} walking piece(s) with nothing within reach to hold them up, left unheld at {At(shortfall.Unheld)}" : "",
                shortfall.ShortWalls.Count > 0 ? FormattableString.Invariant($"{shortfall.ShortWalls.Count} wall cell(s) stopped at {StairSupport.MostCourses} courses short of the ground, lowest at {At(shortfall.ShortWalls)}") : "",
                shortfall.Fell.Count > 0 ? $"{shortfall.Fell.Count} support piece(s) the game's support rule would break, left out, at {At(shortfall.Fell)}" : "",
                shortfall.Weak.Count > 0 ? FormattableString.Invariant($"{shortfall.Weak.Count} walking piece(s) held under {DvergrViaduct.HoldMargin} times their least, laid, at {At(shortfall.Weak)}") : "",
            }, t => t.Length > 0)));
    }

    /// <summary>The pieces of a staircase as they are laid: those saved when its first zone was built
    /// (<see cref="BuiltStairs"/>), else its kit's laid over its layout now (<see cref="Dresser"/>): a staircase nobody has
    /// reached, or one a world built zones of before staircases were saved, which is laid zone by zone so.</summary>
    public static List<StructurePiece> PiecesOf(StairRun run, WorldGenerator world) => BuiltStairs.Of(run)?.PiecesForPlan() ?? Dresser(run, world);

    /// <summary>
    /// The pieces of a staircase whose first zone is built now: <paramref name="planned"/>, the pieces of the plan being
    /// laid, else its kit's laid over its layout now (<see cref="Dresser"/>), with this build's kits; saved with the ground its
    /// layout was settled on (<see cref="BuiltStairs.Keep"/>), so every later zone, on this build or a later one, lays the
    /// same pieces on the same ground. A staircase saved already keeps its pieces.
    /// </summary>
    public static List<StructurePiece> Build(StairRun run, WorldGenerator world, List<StructurePiece>? planned = null)
    {
        if (BuiltStairs.Of(run) is BuiltStair built) return built.PiecesForPlan();
        List<StructurePiece> pieces = planned ?? Dresser(run, world);
        if (pieces.Count > 0) BuiltStairs.Keep(run, KitOf(run), pieces, world);
        return pieces;
    }

    /// <summary>Vegetation kept off every walked piece: a circle round its 2 m footprint (none round a vine, which hangs on
    /// the staircase's own pieces).</summary>
    public void AddClearAreas(List<StructurePiece> zonePieces, List<(Vector3 centre, float radius)> areas)
    {
        foreach (StructurePiece piece in zonePieces)
            if (piece.Kind is not (StructurePieceKind.StairSupport or StructurePieceKind.Flair))
                areas.Add((piece.Position, StairLayout.PieceRun * 0.75f));
    }

    public void AddClearances(Vector3 centre, float radius, List<StructureClearance> clearances)
    {
        var c = new Vector2(centre.x, centre.z);
        float reach = radius + StairLayout.PieceRun;
        foreach (StairRun run in Runs)
            foreach (StairClearance box in run.Clearances)
                if (Vector2.Distance(new Vector2(box.Centre.x, box.Centre.z), c) <= reach)
                    clearances.Add(new StructureClearance(box.Centre, box.Half, box.YawDegrees, box.Surface, run.KeepsSiteRocks));
    }

    /// <summary>The format the staircase record is written in, 7: per run its ends, width, whether it keeps a place's rocks,
    /// its kit and the layout that laid it; per piece its kind, prefab, position, yaw and whether it is cantilevered; then its clearances; then the
    /// ground it stands on (<see cref="GroundPin"/>); then where the road meets each end (<see cref="StairRun.FromMeet"/>,
    /// <see cref="StairRun.ToMeet"/>); then an approach's place (<see cref="StairRun.ExpectedPlace"/>). A number no earlier
    /// build wrote, so an earlier build refuses the record
    /// whole instead of misreading it. Records 1 (without the kept-rocks flag), 2 (without the kit and the cantilever flag)
    /// 3 (without the ground), 4 (without the layout, read as layout 0), 5 (without the meeting points, read as the ends
    /// themselves) and 6 (without an approach's place, its kit choosing on the place's own ground) are read as before.</summary>
    private const int FormatVersion = 7;

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
            writer.Write(run.Layout);
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
            GroundPin.WriteAll(writer, run.Ground);
            Vector2 fromMeet = run.FromMeet ?? run.From, toMeet = run.ToMeet ?? run.To;
            writer.Write(fromMeet.x); writer.Write(fromMeet.y);
            writer.Write(toMeet.x); writer.Write(toMeet.y);
            writer.Write(run.ExpectedPlace.HasValue);
            if (run.ExpectedPlace is var (centre, ground))
            {
                writer.Write(centre.x); writer.Write(centre.y); writer.Write(ground);
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
        RebuildWalkFloor();
        // A saved staircase stands for the roads planned on the loaded network (a manual road's search) as a laid one does.
        s_standing.Clear();
        foreach (StairRun run in runs) MarkStanding(run);
        StructureGround.Rebuild();
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
                run.Layout = version >= 5 ? reader.ReadInt32() : 0;
                if (run.Layout < 0) throw new InvalidDataException("Invalid stair layout.");
                int pieces = reader.ReadInt32();
                if (pieces < 0 || (total += pieces) > 500000) throw new InvalidDataException("Invalid stair piece count.");
                for (int j = 0; j < pieces; j++)
                {
                    int kind = reader.ReadInt32();
                    string prefab = reader.ReadString();
                    var pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    float yaw = reader.ReadSingle();
                    bool cantilevered = version >= 3 && reader.ReadBoolean();
                    if (prefab.Length > RoadNetworkPersistence.MaxSavedPrefabName || !Enum.IsDefined(typeof(StructurePieceKind), kind) ||
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
                if (version >= 4) GroundPin.ReadAll(reader, run.Ground);
                if (version >= 6)
                {
                    run.FromMeet = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    run.ToMeet = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    if (!ManualRoadDraft.ValidPoint(run.FromMeet.Value) || !ManualRoadDraft.ValidPoint(run.ToMeet.Value))
                        throw new InvalidDataException("Invalid stair meeting point.");
                }
                if (version >= 7 && reader.ReadBoolean())
                {
                    var centre = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    float ground = reader.ReadSingle();
                    if (!ManualRoadDraft.ValidPoint(centre) || !ManualRoadDraft.IsFinite(ground)) throw new InvalidDataException("Invalid stair approach place.");
                    run.ExpectedPlace = (centre, ground);
                }
                loaded.Add(run);
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing stair run data.");
        }
        return loaded;
    }
}
