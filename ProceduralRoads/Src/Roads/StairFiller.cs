using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>One flat piece that fills part of a turn's wedge. <see cref="Back"/>
/// is the centre of its back edge; it reaches <see cref="Depth"/> along
/// <see cref="Heading"/> and <see cref="Across"/> across it.</summary>
public readonly struct FillerPiece
{
    public readonly string Prefab;
    public readonly Vector2 Back;
    public readonly float Heading;
    public readonly float Across;
    public readonly float Depth;

    public FillerPiece(string prefab, Vector2 back, float heading, float across, float depth)
    {
        Prefab = prefab; Back = back; Heading = heading; Across = across; Depth = depth;
    }

    public bool Contains(Vector2 p, float tolerance = 0f)
    {
        Vector2 local = p - Back;
        float along = Vector2.Dot(local, StairLayout.Forward(Heading));
        float across = Vector2.Dot(local, StairLayout.Right(Heading));
        return along >= -tolerance && along <= Depth + tolerance && Mathf.Abs(across) <= Across / 2f + tolerance;
    }
}

/// <summary>
/// The flat pieces that fill the wedge a turn leaves on its outside: the
/// triangle between the inside corner both pieces share, the outer corner of
/// the piece below and the outer corner of the piece above. Pure logic.
///
/// The layout chooses, turn by turn (a 45 degree turn filled by a fan of two blocks could instead take a single large
/// 2x2, or 2x1s side by side, or a 1x1 on the outer part snapped to the upslope stairs; the layout
/// chooses). Candidates are the kit's flat pieces (stone's
/// are 2x1 and 1x1 blocks and 2x2 floors), every one level with the top of the piece below and on the
/// hammer's grid: laid across the old edge, hinged on the inside corner at
/// any heading of the turn, or tucked behind the new edge. Of the sets of up
/// to <see cref="MaxPieces"/> that cover the whole wedge, the one that shows
/// least outside the clean corner wins; fewer pieces break a tie. The clean
/// corner is the outline a mason would give it: the outer side of the piece
/// below carried straight on until it meets the outer side of the piece
/// above. Measured against the wedge alone, the fan of two blocks
/// was as good as anything; measured against the corner, its
/// second block pokes past the stair above and a 2x1 across the old edge
/// with a 1x1 tucked behind the new one -- the third option -- wins.
/// A filler hidden under the stair above is not seen (its treads stand
/// higher); one that pokes up through the stair below is.
/// </summary>
public static class StairFiller
{
    public const int MaxPieces = 4;
    /// <summary>The wedge is cut into small triangles this many per metre of its
    /// side, and checked at their centres (16 across a 4 m wedge left 5 cm holes).</summary>
    private const int DivisionsPerMetre = 8;
    private const float Cell = 0.125f;
    private const float Tolerance = 0.02f;
    /// <summary>Square metres one more piece is worth: fewer pieces only break near ties.</summary>
    private const float PieceWeight = 0.05f;

    /// <summary>
    /// Fillers for a turn of <paramref name="turnSteps"/> (positive right) about
    /// <paramref name="pivot"/>, the inside corner of the old edge.
    /// <paramref name="belowIsFloor"/>: the piece below is a floor, level with
    /// the fillers, so a filler lying on it does not show. <paramref name="kinds"/>:
    /// the kit's filler pieces (stone's when none are given).
    /// </summary>
    public static List<FillerPiece> Choose(Vector2 pivot, float oldHeading, int turnSteps, float width, bool belowIsFloor, FillerKind[]? kinds = null)
    {
        var wedge = new Wedge(pivot, oldHeading, turnSteps, width, belowIsFloor);
        var pool = new List<(FillerPiece piece, ulong[] covers, float shows)>();
        foreach (FillerPiece piece in Candidates(wedge, kinds ?? StairKit.Stone.Fillers))
        {
            ulong[] covers = wedge.Covers(piece);
            if (System.Array.Exists(covers, w => w != 0)) pool.Add((piece, covers, wedge.Shows(piece)));
        }

        // Exact cover, cheapest first: branch only on the pieces that cover the
        // first point still open, and stop a branch once it costs more than the best.
        List<int>? best = null;
        float bestCost = float.MaxValue;
        var picks = new List<int>();
        void Search(ulong[] covered, float cost)
        {
            if (cost >= bestCost - 1e-4f) return;
            int open = wedge.FirstOpen(covered);
            if (open < 0)
            {
                bestCost = cost;
                best = new List<int>(picks);
                return;
            }
            if (picks.Count == MaxPieces) return;
            ulong bit = 1UL << (open & 63);
            for (int p = 0; p < pool.Count; p++)
            {
                if ((pool[p].covers[open >> 6] & bit) == 0) continue;
                var next = (ulong[])covered.Clone();
                for (int w = 0; w < next.Length; w++) next[w] |= pool[p].covers[w];
                picks.Add(p);
                Search(next, cost + pool[p].shows + PieceWeight);
                picks.RemoveAt(picks.Count - 1);
            }
        }
        Search(new ulong[wedge.Words], 0f);
        return best?.ConvertAll(p => pool[p].piece) ?? Greedy(wedge, pool);
    }

    /// <summary>When no set of <see cref="MaxPieces"/> covers the wedge: the piece
    /// that closes most of what is still open, again and again.</summary>
    private static List<FillerPiece> Greedy(Wedge wedge, List<(FillerPiece piece, ulong[] covers, float shows)> pool)
    {
        var chosen = new List<FillerPiece>();
        var covered = new ulong[wedge.Words];
        while (wedge.FirstOpen(covered) >= 0)
        {
            int best = -1, bestGain = 0;
            for (int p = 0; p < pool.Count; p++)
            {
                int gain = 0;
                for (int w = 0; w < covered.Length; w++) gain += PopCount(pool[p].covers[w] & ~covered[w]);
                if (gain > bestGain) { best = p; bestGain = gain; }
            }
            if (best < 0) break;
            chosen.Add(pool[best].piece);
            for (int w = 0; w < covered.Length; w++) covered[w] |= pool[best].covers[w];
        }
        return chosen;
    }

    private static int PopCount(ulong v)
    {
        int n = 0;
        for (; v != 0; v &= v - 1) n++;
        return n;
    }

    private static IEnumerable<FillerPiece> Candidates(Wedge w, FillerKind[] kinds)
    {
        for (int i = 0; i <= w.Steps; i++)
        {
            float heading = StairLayout.Wrap(w.OldHeading + w.Side * i * StairLayout.HeadingStep);
            foreach (FillerKind kind in kinds)
                for (float offset = 0f; offset + kind.Across <= w.Width + 1e-3f; offset += 1f)
                {
                    // Reaching forward from a line through the pivot (across the old edge
                    // at i = 0), or back from one (behind the new edge at i = Steps).
                    if (i < w.Steps) yield return Hinged(w, kind, heading, offset, behind: false);
                    if (i > 0) yield return Hinged(w, kind, heading, offset, behind: true);
                }
        }
    }

    /// <summary>A piece whose back (or front, <paramref name="behind"/>) edge lies on
    /// the line through the pivot across <paramref name="heading"/>, starting
    /// <paramref name="offset"/> metres out from the pivot.</summary>
    private static FillerPiece Hinged(Wedge w, FillerKind kind, float heading, float offset, bool behind)
    {
        Vector2 back = w.Pivot - StairLayout.Right(heading) * (w.Side * (offset + kind.Across / 2f));
        if (behind) back -= StairLayout.Forward(heading) * kind.Depth;
        return new FillerPiece(kind.Prefab, back, heading, kind.Across, kind.Depth);
    }

    private sealed class Wedge
    {
        public readonly Vector2 Pivot;
        public readonly float OldHeading;
        public readonly float NewHeading;
        public readonly float Side;
        public readonly int Steps;
        public readonly float Width;
        public readonly int Words;
        private readonly Vector2 _outerOld, _outerNew, _corner;
        private readonly FillerPiece _below, _above;
        private readonly bool _belowIsFloor;
        private readonly List<Vector2> _samples = new();

        public Wedge(Vector2 pivot, float oldHeading, int turnSteps, float width, bool belowIsFloor)
        {
            Pivot = pivot;
            OldHeading = oldHeading;
            Side = turnSteps > 0 ? 1f : -1f;
            Steps = System.Math.Abs(turnSteps);
            NewHeading = StairLayout.Wrap(oldHeading + turnSteps * StairLayout.HeadingStep);
            Width = width;
            _belowIsFloor = belowIsFloor;
            _outerOld = pivot - StairLayout.Right(oldHeading) * (Side * width);
            _outerNew = pivot - StairLayout.Right(NewHeading) * (Side * width);
            // Where the outer sides of the two pieces, carried on, meet.
            Vector2 dOld = StairLayout.Forward(oldHeading), dNew = StairLayout.Forward(NewHeading);
            float t = Cross(_outerNew - _outerOld, dNew) / Cross(dOld, dNew);
            _corner = _outerOld + dOld * t;
            _below = new FillerPiece("", pivot - StairLayout.Right(oldHeading) * (Side * width / 2f) - StairLayout.Forward(oldHeading) * StairLayout.PieceRun,
                oldHeading, width, StairLayout.PieceRun);
            _above = new FillerPiece("", pivot - StairLayout.Right(NewHeading) * (Side * width / 2f), NewHeading, width, StairLayout.PieceRun);

            // Centres of the small triangles of the wedge, n to a side.
            int n = Mathf.CeilToInt(width * DivisionsPerMetre);
            for (int i = 0; i < n; i++)
                for (int j = 0; i + j < n; j++)
                {
                    _samples.Add(Point((i + 1f / 3f) / n, (j + 1f / 3f) / n));
                    if (i + j < n - 1) _samples.Add(Point((i + 2f / 3f) / n, (j + 2f / 3f) / n));
                }
            Words = (_samples.Count + 63) / 64;
        }

        private Vector2 Point(float a, float b) => Pivot + (_outerOld - Pivot) * a + (_outerNew - Pivot) * b;

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            Vector2 e0 = b - a, e1 = c - a, d = p - a;
            float det = Cross(e0, e1);
            float u = Cross(d, e1) / det, v = Cross(e0, d) / det;
            return u >= -Tolerance && v >= -Tolerance && u + v <= 1f + Tolerance;
        }

        /// <summary>Inside the clean corner: the wedge and the mitre beyond it out to where the outer sides meet.</summary>
        public bool InCorner(Vector2 p) => InTriangle(p, Pivot, _outerOld, _corner) || InTriangle(p, Pivot, _corner, _outerNew);

        public ulong[] Covers(FillerPiece piece)
        {
            var mask = new ulong[Words];
            for (int s = 0; s < _samples.Count; s++)
                if (piece.Contains(_samples[s], Tolerance)) mask[s >> 6] |= 1UL << (s & 63);
            return mask;
        }

        /// <summary>The first wedge point no piece covers yet, or -1.</summary>
        public int FirstOpen(ulong[] covered)
        {
            for (int s = 0; s < _samples.Count; s++)
                if ((covered[s >> 6] & (1UL << (s & 63))) == 0) return s;
            return -1;
        }

        /// <summary>Square metres of the piece seen outside the clean corner, the
        /// stair above (it hides under the treads) and a floor below (level with it).</summary>
        public float Shows(FillerPiece piece)
        {
            int seen = 0;
            Vector2 f = StairLayout.Forward(piece.Heading), r = StairLayout.Right(piece.Heading);
            for (float along = Cell / 2f; along < piece.Depth; along += Cell)
                for (float across = -piece.Across / 2f + Cell / 2f; across < piece.Across / 2f; across += Cell)
                {
                    Vector2 p = piece.Back + f * along + r * across;
                    if (InCorner(p) || _above.Contains(p, Tolerance) || (_belowIsFloor && _below.Contains(p, Tolerance))) continue;
                    seen++;
                }
            return seen * Cell * Cell;
        }
    }
}
