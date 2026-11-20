using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Each road as it was stored, its pieces in travel order: what the ridge and trench judge reads of a road already laid
/// where a new road meets it (RoadRidges.Joined, <see cref="JoinedAt"/>), and what the offline gate judges each stored road
/// by. The grid's points carry no road; this is the one record of which stored points are one road, and in what order.
/// Positions only: heights are read from the grid as they stand when asked (<see cref="StoredAt"/>, an append's raise
/// included). Kept for this network's life (<see cref="Clear"/>): saved with the network (<see cref="SerializeLaid"/>) and read
/// back when it loads (<see cref="LoadLaid"/>), so a road added to a loaded network (a manual road) is judged against the roads
/// it meets as on the session that laid them.
/// </summary>
public static partial class RoadSpatialGrid
{
    private sealed class LaidRoad
    {
        /// <summary>Its pieces in travel order, each its points as stored; empty where a piece is not judged as road (a ford
        /// painted on its bed: RoadRidges reads it as a gap).</summary>
        public readonly Vector2[][] Pieces;
        public readonly int Island;
        /// <summary>Which turn of the network's laying it was laid in (<see cref="LaidBefore"/>): untagged roads and the roads the
        /// islands lay side by side take turns, each turn a phase.</summary>
        public readonly int Phase;
        public readonly string? Label;
        public LaidRoad(Vector2[][] pieces, int island, int phase, string? label) { Pieces = pieces; Island = island; Phase = phase; Label = label; }
    }

    /// <summary>The phase the next road is laid in (<see cref="LaidRoad.Phase"/>), and whether it is the islands' turn (roads laid
    /// on island workers) or untagged roads'. Under <see cref="m_recordGate"/>.</summary>
    private static int s_laidPhase;
    private static bool s_laidPhaseIslands;

    private static readonly List<LaidRoad> s_laid = new();
    /// <summary>Every laid road's points by cell of <see cref="RoadRidges.Link"/> metres: (road, piece, index).</summary>
    private static readonly Dictionary<(int, int), List<(int road, int piece, int index)>> s_laidCells = new();

    private static (int, int) LaidCell(Vector2 p) => (Mathf.FloorToInt(p.x / RoadRidges.Link), Mathf.FloorToInt(p.y / RoadRidges.Link));

    /// <summary>Records a road as stored: <paramref name="line"/>, its pieces in travel order (RoadNetworkGenerator.InTravelOrder
    /// of what it committed), on this thread's island. Returns its number.</summary>
    internal static int RecordLaid(IReadOnlyList<PlannedPath> line, string? label = null)
    {
        var pieces = new Vector2[line.Count][];
        for (int k = 0; k < line.Count; k++)
            pieces[k] = line[k].FollowTerrain || line[k].Mound ? System.Array.Empty<Vector2>() : line[k].Points.ToArray();
        int island = CrossIslandReads.Tag;
        lock (m_recordGate)
        {
            // A road laid in the other kind's turn starts a new phase: the islands' roads laid between two untagged roads are
            // one phase, whichever island laid first.
            if (s_laid.Count > 0 && (island != 0) != s_laidPhaseIslands) s_laidPhase++;
            s_laidPhaseIslands = island != 0;
            return AddLaid(new LaidRoad(pieces, island, s_laidPhase, label));
        }
    }

    /// <summary>
    /// The laid order, the one order every reader of the record goes by (<see cref="SerializeLaid"/>, <see cref="Branches"/> and
    /// its <c>before</c>): negative where road <paramref name="a"/> was laid before road <paramref name="b"/>, positive after, 0
    /// for the same road. Roads are laid in phases (<see cref="LaidRoad.Phase"/>): untagged roads (laid off an island worker: a
    /// manual road, one laid on a loaded network) in the order they were laid, and the roads the islands lay side by side, island
    /// by island in island number order, each island's in its own order. How the islands' laying interleaved is not the
    /// network's, so it is not in the order. A load reads the roads back in this order, untagged, so the order is kept.
    /// Under <see cref="m_recordGate"/>.
    /// </summary>
    private static int LaidBefore(int a, int b)
    {
        LaidRoad x = s_laid[a], y = s_laid[b];
        if (x.Phase != y.Phase) return x.Phase.CompareTo(y.Phase);
        if (x.Island != y.Island) return x.Island.CompareTo(y.Island);
        return a.CompareTo(b);
    }

    /// <summary>Adds <paramref name="laid"/> to the record and its points to their cells; returns its number. Under
    /// <see cref="m_recordGate"/>.</summary>
    private static int AddLaid(LaidRoad laid)
    {
        int road = s_laid.Count;
        s_laid.Add(laid);
        for (int k = 0; k < laid.Pieces.Length; k++)
            for (int i = 0; i < laid.Pieces[k].Length; i++)
            {
                var cell = LaidCell(laid.Pieces[k][i]);
                if (!s_laidCells.TryGetValue(cell, out var list)) s_laidCells[cell] = list = new List<(int, int, int)>();
                list.Add((road, k, i));
            }
        return road;
    }

    private const int LaidFormat = 1;

    /// <summary>The record as a save keeps it (RoadNetworkPersistence): per road its label and its pieces' points, in the laid
    /// order (<see cref="LaidBefore"/>), so a load reads them back in it and a new road meets them in the same order as on the
    /// session that laid them. The stored road points keep no road nor which point follows which, so the record is
    /// saved as its own, as the pitches are (RoadPitches.Serialize).</summary>
    internal static byte[] SerializeLaid()
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new System.IO.BinaryWriter(ms))
        {
            writer.Write(LaidFormat);
            lock (m_recordGate)
            {
                var order = new List<int>(s_laid.Count);
                for (int r = 0; r < s_laid.Count; r++) order.Add(r);
                order.Sort(LaidBefore);
                writer.Write(order.Count);
                foreach (int r in order)
                {
                    LaidRoad laid = s_laid[r];
                    writer.Write(laid.Label != null);
                    if (laid.Label != null) writer.Write(laid.Label);
                    writer.Write(laid.Pieces.Length);
                    foreach (Vector2[] piece in laid.Pieces)
                    {
                        writer.Write(piece.Length);
                        foreach (Vector2 p in piece) { writer.Write(p.x); writer.Write(p.y); }
                    }
                }
            }
        }
        return ms.ToArray();
    }

    /// <summary>The record read from a save (<see cref="SerializeLaid"/>), in place of any held: untagged, as the network it
    /// belongs to is once loaded (CrossIslandReads), numbered in the laid order it was saved in (one phase: a road laid on the
    /// loaded network comes after them). None for no record (a network a released build saved, or one sent to a
    /// client, which judges no road).</summary>
    internal static void LoadLaid(byte[]? data)
    {
        var read = new List<LaidRoad>();
        if (data != null && data.Length > 0)
        {
            using var reader = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
            int format = reader.ReadInt32();
            if (format != LaidFormat) throw new System.IO.InvalidDataException($"laid roads record format {format}, this build reads {LaidFormat}");
            int count = reader.ReadInt32();
            CheckCount(reader, count, sizeof(bool) + sizeof(int), "laid roads");
            for (int r = 0; r < count; r++)
            {
                string? label = reader.ReadBoolean() ? reader.ReadString() : null;
                int n = reader.ReadInt32();
                CheckCount(reader, n, sizeof(int), "pieces in a laid road");
                var pieces = new Vector2[n][];
                for (int k = 0; k < n; k++)
                {
                    int m = reader.ReadInt32();
                    CheckCount(reader, m, sizeof(float) * 2, "points in a laid piece");
                    pieces[k] = new Vector2[m];
                    for (int i = 0; i < m; i++)
                    {
                        var p = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                        if (!RoadConstants.InWorld(p)) throw new System.IO.InvalidDataException($"a laid road point at ({p.x}, {p.y}) outside the world");
                        pieces[k][i] = p;
                    }
                }
                read.Add(new LaidRoad(pieces, 0, 0, label));
            }
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new System.IO.InvalidDataException($"{reader.BaseStream.Length - reader.BaseStream.Position} byte(s) left over after the laid roads");
        }
        lock (m_recordGate)
        {
            ClearLaid();
            foreach (LaidRoad laid in read) AddLaid(laid);
        }
    }

    /// <summary>A count read from a record is no more than the bytes left in it can hold at <paramref name="bytesPerEntry"/> each, and
    /// not negative: a corrupt count makes the record unreadable (RoadNetworkPersistence.ReadOrKeep), never an allocation of its size.</summary>
    internal static void CheckCount(System.IO.BinaryReader reader, int count, int bytesPerEntry, string what)
    {
        long left = reader.BaseStream.Length - reader.BaseStream.Position;
        if (count < 0 || (long)count * bytesPerEntry > left)
            throw new System.IO.InvalidDataException($"{count} {what} with {left} byte(s) left in the record");
    }

    /// <summary>How many roads are recorded as laid (<see cref="RecordLaid"/>): read by the offline gate (its routes and ridge
    /// judge, by reflection), never by the mod.</summary>
    internal static int LaidCount { get { lock (m_recordGate) return s_laid.Count; } }

    /// <summary>Laid road <paramref name="road"/>: its label and its pieces' points in travel order (the offline gate's
    /// routes).</summary>
    internal static (string? label, IReadOnlyList<Vector2[]> pieces) LaidRoadAt(int road)
    {
        lock (m_recordGate) return (s_laid[road].Label, s_laid[road].Pieces);
    }

    /// <summary>
    /// The laid roads a road meets at the ends of its runs (RoadRidges.Joined; RoadRidges.Runs of <paramref name="walked"/>, its
    /// pieces in travel order: a road that rides a laid road between two of its pieces meets it at both): where a run's first
    /// point or its last lies within <see cref="RoadRidges.Link"/> (RoadRidges.Find's own joining
    /// distance) of a laid road this island sees, that road's stored stretch from its point nearest the junction, each way
    /// along it, out to <see cref="RoadRidges.JoinedReach"/> (on into its next or previous piece where that one joins within
    /// <see cref="RoadRidges.Link"/>, as RoadRidges.Join joins them), at its heights as stored now; a stretch stops at a point
    /// no longer stored as road. The roads met are read in the laid order (<see cref="LaidBefore"/>), the same on the session that
    /// laid them as on the network loaded from its save. <paramref name="before"/>: only the roads laid before this one, in the
    /// laid order, are read (the offline gate judging each stored road as the mod judged it, against the roads laid before it).
    /// </summary>
    internal static RoadRidges.Joined JoinedAt(IReadOnlyList<PlannedPath> walked, int before = int.MaxValue)
    {
        var runs = new List<RoadRidges.Joined.Run>();
        foreach (var (from, to) in RoadRidges.Runs(walked))
        {
            var atStart = new List<PlannedPath>();
            var atEnd = new List<PlannedPath>();
            PlannedPath last = walked[to];
            Branches(walked[from].Points[0], atStart, toward: true, before);
            Branches(last.Points[Mathf.Min(last.Points.Count, last.Heights.Count) - 1], atEnd, toward: false, before);
            runs.Add(new RoadRidges.Joined.Run(from, to, atStart, atEnd));
        }
        return new RoadRidges.Joined(runs, walked);
    }

    /// <summary>Whether an end of one of <paramref name="walked"/>'s runs (RoadRidges.Runs) lies within <paramref name="within"/>
    /// of a laid road this island sees: the one cheap test before a road's junctions are read on what it stores
    /// (RoadNetworkGenerator.Ridges).</summary>
    internal static bool NearALaidRoad(IReadOnlyList<PlannedPath> walked, float within)
    {
        int me = CrossIslandReads.Me;
        lock (m_recordGate)
        {
            if (s_laid.Count == 0) return false;
            int reach = Mathf.CeilToInt(within / RoadRidges.Link);
            bool Near(Vector2 at)
            {
                var (cx, cz) = LaidCell(at);
                for (int x = cx - reach; x <= cx + reach; x++)
                    for (int z = cz - reach; z <= cz + reach; z++)
                        if (s_laidCells.TryGetValue((x, z), out var list))
                            foreach (var (road, piece, index) in list)
                                if (CrossIslandReads.Sees(me, s_laid[road].Island) && Vector2.Distance(s_laid[road].Pieces[piece][index], at) <= within) return true;
                return false;
            }
            foreach (var (from, to) in RoadRidges.Runs(walked))
            {
                PlannedPath last = walked[to];
                if (Near(walked[from].Points[0]) || Near(last.Points[Mathf.Min(last.Points.Count, last.Heights.Count) - 1])) return true;
            }
            return false;
        }
    }

    private static void Branches(Vector2 junction, List<PlannedPath> into, bool toward, int before)
    {
        int me = CrossIslandReads.Me;
        float reach = RoadRidges.JoinedReach;
        var walks = new List<List<Vector2>>();
        lock (m_recordGate)
        {
            if (s_laid.Count == 0) return;
            var nearest = new Dictionary<int, (int piece, int index, float d)>();
            var (cx, cz) = LaidCell(junction);
            for (int x = cx - 1; x <= cx + 1; x++)
                for (int z = cz - 1; z <= cz + 1; z++)
                {
                    if (!s_laidCells.TryGetValue((x, z), out var list)) continue;
                    foreach (var (road, piece, index) in list)
                    {
                        if (before < s_laid.Count && LaidBefore(road, before) >= 0 || !CrossIslandReads.Sees(me, s_laid[road].Island)) continue;
                        float d = Vector2.Distance(s_laid[road].Pieces[piece][index], junction);
                        if (d > RoadRidges.Link) continue;
                        if (!nearest.TryGetValue(road, out var best) || d < best.d || d == best.d && (piece, index).CompareTo((best.piece, best.index)) < 0)
                            nearest[road] = (piece, index, d);
                    }
                }
            var met = new List<int>(nearest.Keys);
            met.Sort(LaidBefore);
            foreach (int road in met)
                foreach (int dir in new[] { 1, -1 })
                    walks.Add(Walk(s_laid[road].Pieces, nearest[road].piece, nearest[road].index, dir, reach));
        }
        foreach (List<Vector2> walk in walks)
        {
            // Its heights as stored now; it stops at the first point no longer stored as road.
            var points = new List<Vector2>(walk.Count);
            var heights = new List<float>(walk.Count);
            var one = new Vector2[1];
            foreach (Vector2 p in walk)
            {
                one[0] = p;
                if (StoredAt(one) is not RoadPoint[] stored) break;
                points.Add(p); heights.Add(stored[0].h);
            }
            if (points.Count < 2) continue;
            if (toward) { points.Reverse(); heights.Reverse(); }
            float length = 0f;
            for (int i = 1; i < points.Count; i++) length += Vector2.Distance(points[i - 1], points[i]);
            into.Add(new PlannedPath(points, heights, new List<RoadPointDebugInfo>(), RoadNetworkGenerator.RoadWidth, length, false));
        }
    }

    /// <summary>A laid road's points from piece <paramref name="piece"/>'s point <paramref name="index"/> along it
    /// (<paramref name="dir"/> +1 on, -1 back) until <paramref name="reach"/> metres are passed or the road ends, on into the
    /// next piece where it joins within <see cref="RoadRidges.Link"/>.</summary>
    private static List<Vector2> Walk(Vector2[][] pieces, int piece, int index, int dir, float reach)
    {
        var points = new List<Vector2> { pieces[piece][index] };
        float along = 0f;
        int pc = piece, i = index;
        while (along < reach)
        {
            Vector2[] here = pieces[pc];
            int next = i + dir;
            if (next >= 0 && next < here.Length)
            {
                along += Vector2.Distance(here[i], here[next]);
                i = next;
                points.Add(here[i]);
                continue;
            }
            int nextPiece = pc + dir;
            if (nextPiece < 0 || nextPiece >= pieces.Length || pieces[nextPiece].Length == 0) break;
            Vector2[] there = pieces[nextPiece];
            int at = dir > 0 ? 0 : there.Length - 1;
            float gap = Vector2.Distance(here[i], there[at]);
            if (gap > RoadRidges.Link) break;
            along += gap;
            pc = nextPiece; i = at;
            if (there[at] != points[points.Count - 1]) points.Add(there[at]);
        }
        return points;
    }

    /// <summary>Forgets every laid road (<see cref="Clear"/>; a save's record not read).</summary>
    internal static void ClearLaid()
    {
        lock (m_recordGate) { s_laid.Clear(); s_laidCells.Clear(); s_laidPhase = 0; s_laidPhaseIslands = false; }
    }
}
