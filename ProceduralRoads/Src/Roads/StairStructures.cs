using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The road that meets one end of a staircase (StairRun.FromRoad, ToRoad): that road's own points, in its order from the
/// staircase's end outward, as it was planned and stored (<see cref="Of"/>). Its identity is its points' positions; their
/// heights are read from the network as it stands (<see cref="Stored"/>), so what a landing raised (RoadSpatialGrid.SetRoadHeights)
/// or an append moved (RoadSpatialGrid.PublishAppend) is what it reads, never the profile as it was planned. Saved with the
/// staircase (StairStructures.Serialize).
/// </summary>
public sealed class StairRoad
{
    /// <summary>The road's stored points, from the staircase's end outward.</summary>
    public readonly List<Vector2> Points;

    public StairRoad(List<Vector2> points) { Points = points; }

    /// <summary>The road meeting a staircase's end that <paramref name="piece"/> starts at (<paramref name="atStart"/>) or
    /// ends at.</summary>
    internal static StairRoad Of(RoadSpatialGrid.PlannedPath piece, bool atStart)
    {
        var points = new List<Vector2>(piece.Points);
        if (!atStart) points.Reverse();
        return new StairRoad(points);
    }

    /// <summary>Its points as the network stores them now, in order, and before its road is stored as they will be
    /// (RoadTerrainModifier.StoredAt, inside RoadTerrainModifier.AsStored); null where any is not.</summary>
    internal RoadSpatialGrid.RoadPoint[]? Stored() => RoadTerrainModifier.StoredAt(Points);
}

/// <summary>One staircase in the network: its pieces and the space each walked piece needs clear of rock.</summary>
public sealed class StairRun
{
    public Vector2 From;
    public Vector2 To;
    public float Width;
    /// <summary>A staircase onto a location's landing (built with its road): its clearance never carves or removes a rock standing in a location's
    /// footprint that is the location's own (a frost cave's boulders are likely
    /// part of its prefab).</summary>
    public bool KeepsLocationRocks;
    /// <summary>Where the road meets the staircase at <see cref="From"/>'s end and at <see cref="To"/>'s, its own last point
    /// there, where that is not the end itself; null: the end itself. A road's own staircase is recorded between the path
    /// points it climbs, and the road stops on its foot and on the far edge of the top landing laid on past them (staircase
    /// -1880,6632 -> -1944,6608: the road came onto its landing at -1882.8,6634.0, 3.4 m on from the recorded end).</summary>
    public Vector2? FromMeet, ToMeet;
    /// <summary>The road that meets <see cref="From"/>'s end and <see cref="To"/>'s (<see cref="StairRoad"/>): that road's own
    /// stored points from the end outward (RoadNetworkGenerator.RunOf, from RoadSpatialGrid.PlannedPath.StairAtStart and
    /// StairAtEnd; a landing's moved end, the landing's road: RoadStairRuns.MeetLanding). What the end audit reads a foot's
    /// road's slope on (StairEnds.OntoItsRoad) and the one road a landing onto the flight raises with it
    /// (RoadStairRuns.Land). Saved with the run. Null where it is not known: no road planned to that end, the road's end
    /// there not kept (RoadRide.Own).</summary>
    public StairRoad? FromRoad, ToRoad;
    /// <summary>The kit it was laid in, decided once when laid: the one its pieces stand in (StairKit.ForPlan); null for a
    /// run read from a record without it, whose pieces name their kit.</summary>
    public StairKit? Kit;
    /// <summary>The <see cref="StairLayout.LayoutVersion"/> that laid it.</summary>
    public int Layout = StairLayout.LayoutVersion;
    /// <summary>An approach onto a location the game places after its roads (RoadStairRuns.LayLocationApproach): the location's centre
    /// and the ground there its levelling is reckoned from, the approach laid out over the ground the location is expected to
    /// leave, which its kit chooses its supports on too (RoadStairRuns.PlanningGround). Null for any other staircase, and
    /// for one read from a record without it.</summary>
    public (Vector2 Centre, float Ground)? ExpectedLocation;
    public readonly List<StructurePiece> Pieces = new();
    /// <summary>The island worker that laid it (<see cref="CrossIslandReads.Tag"/>): another island's worker does not see it.
    /// 0 for none. Not saved.</summary>
    public int Island;

    /// <summary>The kit's name for listings: the stored kit, else the one its first step names.</summary>
    public string KitName => Kit?.Name ?? (Pieces.Find(p => p.Kind == StructurePieceKind.StairStep)?.Prefab.Split('_')[0] ?? "?");

    /// <summary>Its two ends, From's then To's, as the kits meet them (<see cref="StairKit.Dress"/>): the one owner of where a
    /// staircase meets its road or its location. To's end is a location's landing where the staircase is one onto a location
    /// (<see cref="KeepsLocationRocks"/>): an entry's or an approach's runs from the road's end to the location (RoadStairRuns.Span).</summary>
    public StairEnd[] Ends => new[] { new StairEnd(From, FromMeet ?? From, atLocation: false), new StairEnd(To, ToMeet ?? To, KeepsLocationRocks) };

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
            run.Pieces.Add(LaidPiece(p));
        run.Clearances.AddRange(plan.Clearances);
        return run;
    }

    /// <summary>A layout's piece as the run lays it (<see cref="FromPlan"/>; <see cref="PieceOf"/> the other way): a stair a
    /// step, a landing a floor, a filler a filler, anything else a support.</summary>
    internal static StructurePiece LaidPiece(StairPiece p) => new()
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
    };

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
/// (<see cref="Meet"/>: the road's last point, or the location's ground the staircase ends on), and whether it ends on a
/// location's landing instead of a road.</summary>
public readonly struct StairEnd
{
    public readonly Vector2 At, Meet;
    public readonly bool AtLocation;

    public StairEnd(Vector2 at, Vector2 meet, bool atLocation)
    {
        At = at; Meet = meet; AtLocation = atLocation;
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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), int> s_cells = new(CellPairComparer.Instance);

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

    /// <summary>The approaches onto locations (<see cref="StairRun.KeepsLocationRocks"/>) with a walked piece's box within
    /// <paramref name="reach"/> of the box <paramref name="min"/>..<paramref name="max"/> (<see cref="StairRun.WalkedNear"/>), as
    /// <see cref="Runs"/> reads them: under the lock, on an island worker its own island's and untagged runs. Only those are
    /// copied.</summary>
    public static IReadOnlyList<StairRun> ApproachesNear(Vector2 min, Vector2 max, float reach)
    {
        int me = CrossIslandReads.Me;
        var near = new List<StairRun>();
        lock (s_runs)
            foreach (StairRun r in s_runs)
                if (r.KeepsLocationRocks && (me == 0 || CrossIslandReads.Sees(me, r.Island)) && r.WalkedNear(min, max, reach)) near.Add(r);
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
    private static volatile System.Collections.Concurrent.ConcurrentDictionary<(int, int), (float floor, int island)> s_walkFloor = new();

    /// <summary>The lowest a staircase's cut may take the ground at a whole-metre vertex (<see cref="s_walkFloor"/>), negative
    /// infinity where no walked piece this thread sees stands over it; with <paramref name="notStored"/> (a road's staircases
    /// read before it is stored, RoadTerrainModifier.AsStored), as it will be once they are (<see cref="Add"/>: a lower walk
    /// over the vertex takes it). Any thread.</summary>
    internal static float CutFloor(Vector2 vertex, WalkFloor? notStored = null)
    {
        var key = (Mathf.RoundToInt(vertex.x), Mathf.RoundToInt(vertex.y));
        bool has = notStored is { Replaces: true } ? notStored.Floors.TryGetValue(key, out var at) : s_walkFloor.TryGetValue(key, out at);
        if (notStored is { Replaces: false } && notStored.Floors.TryGetValue(key, out var walk) && (!has || walk.floor < at.floor)) { at = walk; has = true; }
        return has && CrossIslandReads.Sees(at.island) ? at.floor : float.NegativeInfinity;
    }

    /// <summary>The walk floor (<see cref="s_walkFloor"/>) of staircases not stored yet, made as <see cref="Add"/> would add
    /// them (<see cref="AddWalkFloor"/>, on this thread's island): read through <see cref="CutFloor"/> beside the stored one.</summary>
    internal sealed class WalkFloor
    {
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), (float floor, int island)> Floors = new();
        /// <summary>The whole walk floor as it will stand (a staircase swapped for itself cut back, <see cref="Swap"/>), read
        /// instead of the stored one; else these runs' floors, read beside it.</summary>
        internal readonly bool Replaces;
        internal WalkFloor(IEnumerable<StairRun> runs, StairRun? standing = null, StairRun? trimmed = null)
        {
            if (standing != null && trimmed != null)
            {
                // As Swap leaves it (RebuildWalkFloor over the runs, the trimmed one in its place, on its island), then these
                // added as Add adds them.
                Replaces = true;
                lock (s_runs)
                    foreach (StairRun run in s_runs)
                        AddWalkFloor(ReferenceEquals(run, standing) ? trimmed : run, Floors, run.Island);
            }
            int island = CrossIslandReads.Tag;
            foreach (StairRun run in runs) AddWalkFloor(run, Floors, island);
        }
    }

    /// <summary>A run's walked pieces into <see cref="s_walkFloor"/>: every vertex inside a piece's footprint
    /// (<see cref="StairLayout.Footprint"/>), at its walking height there, a stair's rising along it.</summary>
    private static void AddWalkFloor(StairRun run, System.Collections.Concurrent.ConcurrentDictionary<(int, int), (float floor, int island)> into) =>
        AddWalkFloor(run, into, run.Island);

    private static void AddWalkFloor(StairRun run, System.Collections.Concurrent.ConcurrentDictionary<(int, int), (float floor, int island)> into, int island)
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
                    float floor = StairLayout.WalkHeight(stair, height, along, StairLayout.CutBelowSurface);
                    into.AddOrUpdate((x, z), (floor, island), (_, was) => floor < was.floor ? (floor, island) : was);
                }
        }
    }

    /// <summary><see cref="s_walkFloor"/> made again from the runs as they now stand (a run swapped or the runs replaced).</summary>
    private static void RebuildWalkFloor()
    {
        lock (s_runs)
        {
            var fresh = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), (float floor, int island)>();
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
        int tag = CrossIslandReads.Tag;
        foreach (Vector2 p in points)
            s_cells[(Mathf.RoundToInt(p.x / RoadPathfinder.CellSize), Mathf.RoundToInt(p.y / RoadPathfinder.CellSize))] = tag;
    }

    // Every step of every road search asks these two, on every worker. ContainsKey alone: in the
    // game's Mono, ConcurrentDictionary.IsEmpty takes every one of the dictionary's locks (up to
    // 1024) on each call, and an IsEmpty guard here tripled in-game generation.
    // .NET 10 reads it without locks, so offline never showed it.
    // A cell found is checked against this thread's island (CrossIslandReads), the cells not found, nearly all, are not.
    public static bool OnLaidStair(int cellX, int cellZ) => s_cells.TryGetValue((cellX, cellZ), out int tag) && Seen(tag, cellX, cellZ);

    /// <summary>Whether this thread sees a cell marked by island <paramref name="tag"/> (CrossIslandReads), counted if not.</summary>
    private static bool Seen(int tag, int cellX, int cellZ)
    {
        if (CrossIslandReads.Sees(tag)) return true;
        CrossIslandReads.Skipped(CrossIslandReads.Kind.Staircases, tag, new Vector2(cellX * RoadPathfinder.CellSize, cellZ * RoadPathfinder.CellSize));
        return false;
    }

    /// <summary>Pathfinding cells a staircase of the network stands on (laid, or loaded), its end cells apart:
    /// no road is built through them.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), int> s_standing = new(CellPairComparer.Instance);

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
    public static bool Standing(int cellX, int cellZ) => s_standing.TryGetValue((cellX, cellZ), out int tag) && Seen(tag, cellX, cellZ);

    /// <summary>Pieces of the network's staircases within <paramref name="reach"/> across of a point.</summary>
    public static List<StructurePiece> PiecesNear(Vector3 point, float reach)
    {
        var near = new List<StructurePiece>();
        var at = new Vector2(point.x, point.z);
        int me = CrossIslandReads.Me;
        int skipped = 0;
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
        int skipped = 0;
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
        int skipped = 0;
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
        int skipped = 0;
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
    /// A run settled on the finished ground (<see cref="BuriedPieces"/>): its pieces wholly under the ground dropped where
    /// the rest stands without them on that ground (<see cref="BuriedPieces.Drop"/>; ground not known grounds and buries
    /// nothing); then the ground it stands on saved with it and held (<see cref="StructureGround.Measure"/>). Returns how
    /// many were dropped.
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
            // Held back whole, as a bridge is, by every zone its pieces' bodies reach (StructurePlans.HoldWhole).
            StructurePlans.HoldWhole(pieces);
            StairRun saved = run;
            var planned = new PlannedSite(pieces, () => BuiltStairs.Of(saved) != null, w => Build(saved, w, pieces));
            foreach (StructurePiece piece in pieces)
            {
                piece.Site = planned;
                piece.Staircase = run;
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
    public static StairKit KitOf(StairRun run) => run.Kit ?? StairKit.Of(run.KitName);

    /// <summary>A staircase's kit laying its pieces over its layout on <paramref name="finished"/> ground
    /// (<see cref="StairKit.Dress"/>), each piece's support style chosen on the ground it was laid out on
    /// (<see cref="RoadStairRuns.PlanningGround"/>), what it refuses said in the shortfall. The one call of a staircase's kit:
    /// its first build (<see cref="Dresser"/>) and its road's reading before the road is stored (<see cref="Refusal"/>). A
    /// test's kit may stand here. <c>sharedPosts</c> off (the verdict, <see cref="Refusal"/>, which reads only the shortfall), the
    /// staircases it meets are not dressed for the posts they share (<see cref="DressAmong"/>).</summary>
    internal static Func<StairRun, WorldGenerator, Func<int, int, float>, StairSupport.Shortfall, bool, List<StructurePiece>> Dressing =
        (run, world, finished, shortfall, sharedPosts) => DressAmong(run, Runs, r => RoadStairRuns.PlanningGround(r, world), finished, shortfall, null,
            (x, z) => RoadBiome.At(world, x, z), sharedPosts);

    /// <summary>A staircase's kit laying its pieces over its layout (<see cref="StairKit.Dress"/>) among the staircases
    /// <paramref name="among"/>, those meeting it end to end walked on across (<see cref="MetAtEnds(StairRun, IEnumerable{StairRun})"/>):
    /// the network's (<see cref="Dressing"/>), or a catalogue's laid apart from it (RailingCatalogue). The one call both make.
    /// <para>Two staircases met end to end each rail their own pieces, so at a corner they share both railings lay a post:
    /// it stands once (<see cref="StairRailing.LeaveShared"/>). Each one met that lays the shared posts
    /// (<see cref="StairRailing.LaysShared"/>) is dressed here as its own call dresses it, on its own planning ground
    /// (<paramref name="natural"/> of it), and the posts it lays are left out of this one's.</para>
    /// <para>With <paramref name="sharedPosts"/> off, the ones met are not dressed: only this one's own pieces and its
    /// <paramref name="shortfall"/>, which the ones met never change (each is dressed with a shortfall of its own), for a caller
    /// that reads only the shortfall (<see cref="Refusal"/>).</para></summary>
    internal static List<StructurePiece> DressAmong(StairRun run, IEnumerable<StairRun> among, Func<StairRun, Func<float, float, float>> natural,
        Func<int, int, float> finished, StairSupport.Shortfall shortfall, bool? weathered, Func<float, float, Heightmap.Biome>? biomeAt,
        bool sharedPosts = true)
    {
        var all = among as IReadOnlyCollection<StairRun> ?? among.ToList();
        List<StructurePiece> pieces = KitOf(run).Dress(run, natural(run), finished, weathered, shortfall, MetAtEnds(run, all), biomeAt);
        if (pieces.Count == 0 || !sharedPosts) return pieces;
        foreach (StairRun other in Meeting(run, all))
        {
            // One met that lays nothing (its build's verdicts, Dresser: ground not known under it, its kit refusing it, not in the
            // world whole) lays no shared post either, so this one's stand.
            if (!StairRailing.LaysShared(other, run) || FirstUnknown(other, finished) != null) continue;
            List<StructurePiece> theirs = KitOf(other).Dress(other, natural(other), finished, weathered, new StairSupport.Shortfall(),
                MetAtEnds(other, all), biomeAt);
            if (theirs.Count == 0 || WholeFault(other, theirs, finished) != null) continue;
            StairRailing.LeaveShared(pieces, theirs);
        }
        return pieces;
    }

    /// <summary>Why a staircase dressed as <paramref name="pieces"/> is not in the world whole, or null where it is:
    /// <see cref="StructureSites.SpatialFault"/> over its ends, its pieces and the ground its record would hold
    /// (<see cref="BuiltStairs.GroundOf"/>). The one test its build asks (<see cref="Dresser"/>, through
    /// <see cref="StructureSites.Refuses"/>, which says it) and a staircase it shares posts with (<see cref="DressAmong"/>).</summary>
    private static string? WholeFault(StairRun run, List<StructurePiece> pieces, Func<int, int, float> finished) =>
        StructureSites.SpatialFault(run.From, run.To, pieces, BuiltStairs.GroundOf(run, pieces, finished));

    /// <summary>A staircase's kit laying its pieces over its layout now: this build's kits (<see cref="StairKit.Dress"/>),
    /// each piece's support style chosen on the ground its layout was laid out on and priced on
    /// (<see cref="RoadStairRuns.PlanningGround"/>), the ground as locations will leave it (chosen on the natural ground, a
    /// flight on a placed location's raised landing was held up as if over the slope under it); what it lays (walls, arches,
    /// corbels, cornices) stands on the finished ground (<see cref="StructureGround.Vertices"/>), on which the support
    /// rule grounds it: chosen and laid on the planning ground, an arch over a road's cut stopped short of the ground the
    /// writer leaves and fell. A later build's kits stand here; so may a test's.</summary>
    /// <para>Where the finished ground under its walking pieces is not known (<see cref="UnknownGround"/>: inside a location whose
    /// ground is not published, a bug under the preload-list rule) its kit lays nothing, said as an error once
    /// (<see cref="StructureGround.NotLaid"/>): its layout's ground is not a stand-in, and nothing waits for it.</para>
    /// <para>One its kit refuses (a walking piece the game's support rule breaks however it is held,
    /// <see cref="StairKit.Dress"/>) lays nothing either, said once (<see cref="SayShortfall"/>); nothing of it is saved, so
    /// each later plan dresses it again on the ground as it then stands. Nor does one not in the world whole
    /// (<see cref="StructureSites.Refuses"/>), which no record keeps either.</para></summary>
    internal static Func<StairRun, WorldGenerator, List<StructurePiece>> Dresser = (run, world) =>
    {
        Func<int, int, float> finished = StructureGround.Vertices(world);
        if (FirstUnknown(run, finished) is StructurePiece unknown)
        {
            StructureGround.NotLaid(run, Named(run), unknown);
            return new List<StructurePiece>();
        }
        var shortfall = new StairSupport.Shortfall();
        List<StructurePiece> pieces = Dressing(run, world, finished, shortfall, true);
        if (shortfall.Any) SayShortfall(run, shortfall);
        // Not in the world whole (StructureSites.SpatialFault over its pieces and the ground its record would hold,
        // BuiltStairs.GroundOf): one verdict for laying and saving, so none of it is laid, in any zone.
        if (pieces.Count > 0 && StructureSites.Refuses(run, "staircase", run.From, run.To, pieces, BuiltStairs.GroundOf(run, pieces, finished)))
            return new List<StructurePiece>();
        return pieces;
    };

    /// <summary>The network's staircases as their first build lays them (<see cref="Dresser"/>, the one call: ground not known
    /// under them, the support rule breaking a walking piece however held, not in the world whole), on
    /// <paramref name="world"/>'s finished ground: how many were dressed, and a line for each one its build lays nothing for.
    /// A staircase refused there leaves its road without stairs; the ends audit (StairEnds.AuditAll) reads the layouts, so
    /// only this shows it offline (the maps tool's summary).</summary>
    internal static (int dressed, List<string> refused) DressAll(WorldGenerator world)
    {
        var refused = new List<string>();
        List<StairRun> runs;
        lock (s_runs) runs = new List<StairRun>(s_runs);
        foreach (StairRun run in runs)
            if (Dresser(run, world).Count == 0) refused.Add(Named(run) + ": laid nothing at its first build (StairStructures.Dresser)");
        return (runs.Count, refused);
    }

    /// <summary>Why a road is refused for a staircase its kit refuses (<see cref="Refusal"/>), in its refusal line.</summary>
    internal const string RefusedBySupport = "a staircase the support rule breaks however held";

    /// <summary>
    /// Whether <paramref name="run"/>, not stored yet, would be refused by its kit (<see cref="StairKit.Dress"/>: a walking
    /// piece the game's support rule breaks however held), read through <see cref="Dressing"/> on the finished ground
    /// (<see cref="StructureGround.Vertices"/>), the ground its first build reads (<see cref="Dresser"/>): asked by its road
    /// before it is stored (RoadNetworkGenerator.StairsRefusal), inside RoadTerrainModifier.AsStored, so that ground is the
    /// network's with the road's own fill, cuts and mounds and its staircases' cuts in it, as the writer will leave them:
    /// <see cref="RefusedBySupport"/>, and at <paramref name="at"/> the first piece that falls; null where its kit lays it.
    /// Where the ground under its walking pieces is not known (<see cref="UnknownGround"/>: a bug under the preload-list rule)
    /// its build would lay nothing (<see cref="Dresser"/>), so its road is refused for it: <see cref="UnknownGroundRefusal"/>,
    /// and at <paramref name="at"/> that piece; the one list says the refusal (RoadNetworkGenerator.StairsRefusal, as an error).
    /// </summary>
    internal static string? Refusal(StairRun run, WorldGenerator world, out Vector2 at)
    {
        at = run.From;
        Func<int, int, float> finished = StructureGround.Vertices(world);
        if (FirstUnknown(run, finished) is StructurePiece unknown)
        {
            at = new Vector2(unknown.Position.x, unknown.Position.z);
            return UnknownGroundRefusal + ": " + StructureGround.NotKnown(unknown);
        }
        // Its verdict reads only its own shortfall: the staircases it meets are not dressed for the posts they share.
        var shortfall = new StairSupport.Shortfall();
        Dressing(run, world, finished, shortfall, false);
        if (shortfall.Falling.Count == 0) return null;
        at = new Vector2(shortfall.Falling[0].x, shortfall.Falling[0].z);
        return RefusedBySupport;
    }

    /// <summary>Why a road is refused for a staircase on ground not known under its walking pieces (<see cref="Refusal"/>).</summary>
    internal const string UnknownGroundRefusal = "a staircase on ground not known";

    /// <summary>A staircase as said in the logs.</summary>
    private static string Named(StairRun run) => FormattableString.Invariant(
        $"[STAIRS] staircase {KitOf(run).Name} ({run.From.x:F1}, {run.From.y:F1}) -> ({run.To.x:F1}, {run.To.y:F1})");

    /// <summary>The first of <paramref name="run"/>'s walking pieces on ground not known (<see cref="UnknownGround"/>'s test).</summary>
    private static StructurePiece? FirstUnknown(StairRun run, Func<int, int, float> finished) =>
        StructureGround.FirstOnUnknownGround(WalkingPieces(run), finished);

    /// <summary>The first of <paramref name="run"/>'s walking pieces with a vertex of <paramref name="finished"/> ground not
    /// known within <see cref="StructureGround.HeldReach"/> of its origin (<see cref="StructureGround.FirstOnUnknownGround"/>),
    /// said; null where all of it is known.</summary>
    internal static string? UnknownGround(StairRun run, Func<int, int, float> finished) =>
        StructureGround.FirstOnUnknownGround(WalkingPieces(run), finished) is StructurePiece piece ? StructureGround.NotKnown(piece) : null;

    /// <summary>The pieces of <paramref name="run"/>'s layout whose ground must be known (<see cref="UnknownGround"/>): its
    /// walking pieces, its layout's columns and brackets not among them.</summary>
    internal static IEnumerable<StructurePiece> WalkingPieces(StairRun run) =>
        run.Pieces.Where(p => IsLayoutKind(p.Kind) && p.Kind != StructurePieceKind.StairSupport);

    /// <summary>How near another staircase's end must stand to one of <see cref="MetAtEnds"/>'s run's for the two to meet end to
    /// end, and how far round it their walking pieces are taken.</summary>
    private const float EndToEnd = 1.5f, EndToEndReach = 6f;

    /// <summary>The walking pieces (steps, floors, fillers) of every other staircase with an end where one of
    /// <paramref name="run"/>'s is (its recorded point or where the road meets it, within <see cref="EndToEnd"/>), those within
    /// <see cref="EndToEndReach"/> of it: two staircases meeting end to end, the walk carried on from one onto the other
    /// (two black marble staircases in game, a 2x2 connector at -6921.0,-3653.8 with a flight on each side). Each is
    /// laid by its own staircase; the railing walks on across them (<see cref="StairRailing.Lay"/>).</summary>
    internal static List<StructurePiece> MetAtEnds(StairRun run) => MetAtEnds(run, Runs);

    /// <summary><see cref="MetAtEnds(StairRun)"/> among the staircases <paramref name="among"/> rather than the network's.</summary>
    internal static List<StructurePiece> MetAtEnds(StairRun run, IEnumerable<StairRun> among)
    {
        var beside = new List<StructurePiece>();
        foreach (var (other, end) in MetEnds(run, among))
            foreach (StructurePiece p in other.Pieces)
                if (p.Kind is StructurePieceKind.StairStep or StructurePieceKind.StairFloor or StructurePieceKind.StairFiller
                    && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), end) <= EndToEndReach && !beside.Contains(p))
                    beside.Add(p);
        return beside;
    }

    /// <summary>The staircases among <paramref name="among"/> that meet <paramref name="run"/> end to end
    /// (<see cref="MetEnds"/>), each once.</summary>
    private static IEnumerable<StairRun> Meeting(StairRun run, IEnumerable<StairRun> among) =>
        MetEnds(run, among).Select(m => m.other).Distinct();

    /// <summary>Each other staircase among <paramref name="among"/> with an end where one of <paramref name="run"/>'s is (its
    /// recorded point or where the road meets it, within <see cref="EndToEnd"/>), with that end of <paramref name="run"/>'s: the
    /// one test of two staircases met end to end (<see cref="MetAtEnds(StairRun, IEnumerable{StairRun})"/>).</summary>
    private static IEnumerable<(StairRun other, Vector2 end)> MetEnds(StairRun run, IEnumerable<StairRun> among)
    {
        Vector2[] Points(StairRun r) => new[] { r.From, r.To, r.FromMeet ?? r.From, r.ToMeet ?? r.To };
        Vector2[] mine = Points(run);
        foreach (StairRun other in among)
        {
            if (ReferenceEquals(other, run)) continue;
            foreach (Vector2 end in mine)
                if (Array.Exists(Points(other), p => Vector2.Distance(p, end) <= EndToEnd)) yield return (other, end);
        }
    }

    /// <summary>The staircases whose shortfall was said already: a staircase is dressed again each time the plans are made.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StairRun, object> s_saidShortfall = new();

    /// <summary>Say, once per staircase, what its kit could not hold up as its search priced it (<see cref="StairSupport.Lay"/>):
    /// a walking piece left with nothing under it, a wall stopped short of the ground, a support the game's support rule would
    /// break, a walking piece it breaks however held (the staircase refused, <see cref="StairKit.Dress"/>), or a walking piece
    /// it holds under the margin. The search never lays any of them (an
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
            $"[STAIRS] staircase {KitOf(run).Name} ({run.From.x:F1}, {run.From.y:F1}) -> ({run.To.x:F1}, {run.To.y:F1}){(run.KeepsLocationRocks ? " at a atLocation" : "")}: ") +
            string.Join("; ", Array.FindAll(new[]
            {
                shortfall.Unheld.Count > 0 ? $"{shortfall.Unheld.Count} walking piece(s) with nothing within reach to hold them up, left unheld at {At(shortfall.Unheld)}" : "",
                shortfall.ShortWalls.Count > 0 ? FormattableString.Invariant($"{shortfall.ShortWalls.Count} wall cell(s) stopped at {StairSupport.MostCourses} courses short of the ground, lowest at {At(shortfall.ShortWalls)}") : "",
                shortfall.Fell.Count > 0 ? $"{shortfall.Fell.Count} support piece(s) the game's support rule would break, left out, at {At(shortfall.Fell)}" : "",
                shortfall.Falling.Count > 0 ? $"refused, nothing laid: {shortfall.Falling.Count} walking piece(s) the game's support rule breaks however held, at {At(shortfall.Falling)}" : "",
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

    /// <summary>Vegetation kept off a staircase's pieces: a square 1.5 m each way round the middle of each walked piece and of
    /// each railing post and rail, the railing's own clearing (not walking space: about 1.5 m past the walk's edge). None
    /// round a support, nor a vine, which hangs on the staircase's own pieces. The one owner of the railing's clearing
    /// (docs/OWNERS.md, vegetation).</summary>
    public void AddClearAreas(List<StructurePiece> zonePieces, List<(Vector3 centre, float radius)> areas)
    {
        foreach (StructurePiece piece in zonePieces)
            if (piece.Kind is not (StructurePieceKind.StairSupport or StructurePieceKind.Flair))
                areas.Add((piece.Position, StairLayout.PieceRun * 0.75f));
    }

    /// <summary>
    /// A staircase's walk: its stored walking boxes (<see cref="StairRun.Clearances"/>, StairLayout.AddClearance), every walked
    /// piece's whether it is laid or left out as buried (BuriedPieces.Drop leaves its box), those whose middle stands in a
    /// near zone, each as a walk (<see cref="WalkOf(StairClearance)"/>). The one stored walk the rock clearing reads too
    /// (<see cref="AddClearances"/>), as a bridge's vegetation and rock read its own (BridgeStructures.WalkOf). Read from the
    /// staircase's stored boxes when any of its planned pieces may be laid (<see cref="StructurePlans.BuildableStaircases"/>).
    /// The nearby-zone <paramref name="buildable"/> alone cannot decide this: a buried flight may have pieces only far away.
    /// </summary>
    public void AddWalksNear(IReadOnlyList<Vector2s> near, Func<Vector2s, List<StructurePiece>?> buildable, List<StairSupport.Walk> walks)
    {
        foreach (StairClearance box in WalkBoxes())
        {
            Vector2s zone = ZoneSystem.GetZone(box.Centre);
            for (int i = 0; i < near.Count; i++)
                if (near[i].x == zone.x && near[i].y == zone.y) { walks.Add(WalkOf(box)); break; }
        }
    }

    /// <summary>The stored walking boxes generation reads, of every staircase any of whose planned pieces may be laid
    /// (<see cref="StructurePlans.BuildableStaircases"/>: one that lays nothing clears nothing), even where burial leaves no
    /// planned piece to supply a zone. The bake's walk reach uses the same inventory.</summary>
    internal static IEnumerable<StairClearance> WalkBoxes()
    {
        HashSet<StairRun> laid = StructurePlans.BuildableStaircases();
        foreach (StairRun run in Runs)
        {
            if (!laid.Contains(run)) continue;
            foreach (StairClearance box in run.Clearances)
                yield return box;
        }
    }

    /// <summary>A stored walking box as the walk it stands over: its footprint, from its lowest walk
    /// (<see cref="StairLayout.LowestWalk"/>) to its highest (<see cref="StairLayout.HighestWalk"/>), so what reaches it is what
    /// reaches the walking surface or the headroom over it (VegetationClearing.CentresOf), not the piece's underside: a buried
    /// flight's walk is its walking surface, under the ground over it.</summary>
    internal static StairSupport.Walk WalkOf(StairClearance box)
    {
        float lowest = StairLayout.LowestWalk(box.Centre, box.Half), highest = StairLayout.HighestWalk(box.Centre, box.Half, box.Surface);
        Vector2 back = new Vector2(box.Centre.x, box.Centre.z) - StairLayout.Forward(box.YawDegrees) * box.Half.z;
        return new StairSupport.Walk(back, box.YawDegrees, 2f * box.Half.z, 2f * box.Half.x, lowest, false, highest - lowest);
    }

    public void AddClearances(Vector3 centre, float radius, List<StructureClearance> clearances)
    {
        var c = new Vector2(centre.x, centre.z);
        float reach = radius + StairLayout.PieceRun;
        HashSet<StairRun> laid = StructurePlans.BuildableStaircases();
        foreach (StairRun run in Runs)
        {
            if (!laid.Contains(run)) continue;
            foreach (StairClearance box in run.Clearances)
                if (Vector2.Distance(new Vector2(box.Centre.x, box.Centre.z), c) <= reach)
                    clearances.Add(new StructureClearance(box.Centre, box.Half, box.YawDegrees, box.Surface, wholeOnly: false, run.KeepsLocationRocks));
        }
    }

    /// <summary>The format the staircase record is written in, 7: per run its ends, width, whether it keeps a location's rocks,
    /// its kit and the layout that laid it; per piece its kind, prefab, position, yaw and whether it is cantilevered; then its clearances; then the
    /// ground it stands on (<see cref="GroundPin"/>); then where the road meets each end (<see cref="StairRun.FromMeet"/>,
    /// <see cref="StairRun.ToMeet"/>); then an approach's location (<see cref="StairRun.ExpectedLocation"/>); then the road that meets
    /// each end (<see cref="StairRun.FromRoad"/>, <see cref="StairRun.ToRoad"/>: a count of its points, -1 for none, then
    /// each). The record is not in the released build's format (that build saved no staircases), so no other format is read
    /// and the number stands. A number no earlier
    /// build wrote, so an earlier build refuses the record
    /// whole instead of misreading it. No other record is read.</summary>
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
            writer.Write(run.KeepsLocationRocks);
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
            writer.Write(run.ExpectedLocation.HasValue);
            if (run.ExpectedLocation is var (centre, ground))
            {
                writer.Write(centre.x); writer.Write(centre.y); writer.Write(ground);
            }
            foreach (StairRoad? road in new[] { run.FromRoad, run.ToRoad })
            {
                writer.Write(road?.Points.Count ?? -1);
                if (road != null)
                    foreach (Vector2 p in road.Points) { writer.Write(p.x); writer.Write(p.y); }
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
            if (version != FormatVersion) throw new InvalidDataException("Unknown stair run data version.");
            int runs = reader.ReadInt32();
            if (runs < 0 || runs > 10000) throw new InvalidDataException("Invalid stair run count.");
            int total = 0, roadPoints = 0;
            for (int i = 0; i < runs; i++)
            {
                var run = new StairRun
                {
                    From = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                    To = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                    Width = reader.ReadSingle(),
                    KeepsLocationRocks = reader.ReadBoolean(),
                };
                string kit = reader.ReadString();
                if (kit.Length > 0) run.Kit = StairKit.Named(kit) ?? throw new InvalidDataException("Unknown stair kit.");
                run.Layout = reader.ReadInt32();
                if (run.Layout < 0) throw new InvalidDataException("Invalid stair layout.");
                int pieces = reader.ReadInt32();
                if (pieces < 0 || (total += pieces) > 500000) throw new InvalidDataException("Invalid stair piece count.");
                for (int j = 0; j < pieces; j++)
                {
                    int kind = reader.ReadInt32();
                    string prefab = reader.ReadString();
                    var pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    float yaw = reader.ReadSingle();
                    bool cantilevered = reader.ReadBoolean();
                    if (prefab.Length > RoadNetworkPersistence.MaxSavedPrefabName || !Enum.IsDefined(typeof(StructurePieceKind), kind) ||
                        !ManualRoadDraft.IsFinite(pos.y) || !ManualRoadDraft.IsFinite(yaw))
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
                    if (!RoadConstants.InWorld(new Vector2(centre.x, centre.z)) || !ManualRoadDraft.IsFinite(centre.y) ||
                        !ManualRoadDraft.IsFinite(half.x) || !ManualRoadDraft.IsFinite(half.y) || !ManualRoadDraft.IsFinite(half.z) ||
                        !ManualRoadDraft.IsFinite(yaw) || !ManualRoadDraft.IsFinite(surface))
                        throw new InvalidDataException("Invalid stair clearance.");
                    run.Clearances.Add(new StairClearance(centre, half, yaw, surface));
                }
                GroundPin.ReadAll(reader, run.Ground);
                if (StructureSites.SpatialFault(run.From, run.To, run.Pieces, run.Ground) is string fault)
                    throw new InvalidDataException($"Invalid stair run: {fault}.");
                run.FromMeet = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                run.ToMeet = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                if (!RoadConstants.InWorld(run.FromMeet.Value) || !RoadConstants.InWorld(run.ToMeet.Value))
                    throw new InvalidDataException("Invalid stair meeting point.");
                if (reader.ReadBoolean())
                {
                    var centre = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    float ground = reader.ReadSingle();
                    if (!RoadConstants.InWorld(centre) || !ManualRoadDraft.IsFinite(ground)) throw new InvalidDataException("Invalid stair approach location.");
                    run.ExpectedLocation = (centre, ground);
                }
                run.FromRoad = ReadRoad(reader, ref roadPoints);
                run.ToRoad = ReadRoad(reader, ref roadPoints);
                loaded.Add(run);
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing stair run data.");
        }
        return loaded;
    }

    /// <summary>The road a saved run records at one end (<see cref="Serialize"/>), or null for none; <paramref name="total"/>
    /// counts the points read, held to the record's piece limit. Malformed data throws.</summary>
    private static StairRoad? ReadRoad(BinaryReader reader, ref int total)
    {
        int count = reader.ReadInt32();
        if (count == -1) return null;
        if (count < 0 || (total += count) > 500000) throw new InvalidDataException("Invalid stair road point count.");
        var points = new List<Vector2>(count);
        for (int i = 0; i < count; i++)
        {
            var p = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            if (!RoadConstants.InWorld(p)) throw new InvalidDataException("Invalid stair road point.");
            points.Add(p);
        }
        return new StairRoad(points);
    }
}
