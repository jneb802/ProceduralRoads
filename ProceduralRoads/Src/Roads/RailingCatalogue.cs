using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A catalogue of staircase railing at joins that do not snap, for correcting by hand in game (road_railcat,
/// RailingCatalogueCommands): bays of two staircases met end to end at an unsnapped join (floors turned by a
/// few angles either way, set off sideways, a floor laid 0.1 m on from the last, a floor onto a stair at a jog, and one bend
/// as a network laid it), each laid as a layout lays such a join (a floor turned off a floor on the outer corner the two share,
/// a stair off a floor on the inside one with its wedge filled, a clipped floor overlapping the other's: no gap between
/// walking surfaces; <see cref="StairLayout.TurnOff"/>, <see cref="StairLayout.LayFlight"/>), and each dressed by its kit as generation dresses a network's staircases (<see cref="StairStructures.DressAmong"/>, the one
/// call: <see cref="StairKit.Dress"/>, its railing <see cref="StairRailing.Lay"/>), never a copy of it. What a bay's
/// pieces are after a hand's corrections is compared with what its kit laid (<see cref="Diff"/>). Debug only: nothing here
/// is laid by generation or saved with a network. Pure logic.
/// </summary>
public static class RailingCatalogue
{
    /// <summary>One staircase of a bay, in the bay's frame: the join at the origin, its walking surface there at 0, the walk
    /// coming in heading north (+z).</summary>
    internal sealed class BayRun
    {
        public Vector2 From, To;
        public Vector2? FromMeet, ToMeet;
        public readonly List<StructurePiece> Pieces = new();
    }

    /// <summary>A bay: two staircases met end to end at one join.</summary>
    internal sealed class Bay
    {
        public string Name = "";
        public StairKit Kit = StairKit.BlackMarble;
        public readonly List<BayRun> Runs = new();
    }

    /// <summary>The turns between the two staircases' floors at a join, each way.</summary>
    internal static readonly float[] Turns = { 11.25f, 22.5f, 33.75f, 45f };

    /// <summary>The sideways offsets of an end to end join (a clip: the second's first floor half a floor into the first's
    /// last, in line, set aside up to half the width, as <see cref="StairLayout"/>'s clipped joins lay it).</summary>
    internal static readonly float[] Offsets = { 0.25f, 0.5f };

    /// <summary>How far on from the first staircase's last floor the second's first floor stands: two floors 0.1 m apart along
    /// their row, overlapping all but that, as a network laid them at a join.</summary>
    public const float Gap = 0.1f;

    /// <summary>The jog where a floor meets a stair.</summary>
    public const float StairJog = 22.5f;

    /// <summary>How far a bay's lowest walking piece stands over the highest ground under it: every edge then over the ledge
    /// (<see cref="StairRailing.LedgeDrop"/>), so the railing shows what the join alone leaves open.</summary>
    public const float Clearance = 1f;

    /// <summary>How far past its walking pieces a bay's railing and supports may reach: its box.</summary>
    public const float Margin = 2f;

    /// <summary>Every bay of a kit, in order: the turns (left then right, smallest first), the offsets, a floor on, a floor onto
    /// a stair; with black marble, the network's bend last.</summary>
    internal static List<Bay> Bays(StairKit kit)
    {
        var bays = new List<Bay>();
        foreach (float turn in Turns)
            foreach (int side in new[] { -1, 1 })
                bays.Add(Turned(kit, side * turn));
        foreach (float offset in Offsets) bays.Add(Straight(kit, $"offset-{Number(offset)}", offset, -StairLayout.PieceRun / 2f));
        bays.Add(Straight(kit, $"on-{Number(Gap)}", 0f, Gap - StairLayout.PieceRun));
        bays.Add(OntoAStair(kit, StairJog));
        if (kit == StairKit.BlackMarble) bays.Add(NetworkBend());
        return bays;
    }

    /// <summary>The signed turn from <paramref name="from"/> to <paramref name="to"/>, -180..180 degrees.</summary>
    internal static float Delta(float from, float to)
    {
        float d = (to - from) % 360f;
        if (d > 180f) d -= 360f;
        else if (d < -180f) d += 360f;
        return d;
    }

    private static string Number(float v) => v.ToString("0.0#", CultureInfo.InvariantCulture);

    private static StructurePiece Floor(StairKit kit, Vector2 centre, float surface, float yaw) => new()
    {
        Kind = StructurePieceKind.StairFloor, Prefab = kit.Floor, Position = new Vector3(centre.x, surface - kit.FloorTop, centre.y),
        YawDegrees = StairLayout.Wrap(yaw),
    };

    /// <summary>The staircase coming into every bay's join: two floors heading north, its far end 4 m back.</summary>
    private static BayRun Incoming(StairKit kit)
    {
        var run = new BayRun { From = new Vector2(0f, -2f * StairLayout.PieceRun), To = new Vector2(0f, 0f) };
        run.Pieces.Add(Floor(kit, new Vector2(0f, -1.5f * StairLayout.PieceRun), 0f, 0f));
        run.Pieces.Add(Floor(kit, new Vector2(0f, -0.5f * StairLayout.PieceRun), 0f, 0f));
        return run;
    }

    /// <summary>Two floors turned <paramref name="turn"/> degrees (right positive) off the incoming's last floor as a layout
    /// turns a floor off a floor (<see cref="StairLayout.TurnOff"/>): pivoting on the outer corner the two share, overlapping on
    /// the inside of the turn, no wedge open on the outside.</summary>
    private static Bay Turned(StairKit kit, float turn)
    {
        var bay = new Bay { Name = $"turn-{(turn < 0f ? "left" : "right")}-{Number(Mathf.Abs(turn))}", Kit = kit };
        bay.Runs.Add(Incoming(kit));
        Vector2 on = StairLayout.Forward(turn);
        (Vector2 start, _) = StairLayout.TurnOff(new Vector2(0f, 0f), 0f, turn, turn < 0f ? -1 : 1, StairLayout.PieceRun / 2f, floorOnFloor: true);
        var run = new BayRun { From = start, To = start + on * (2f * StairLayout.PieceRun) };
        run.Pieces.Add(Floor(kit, start + on * (0.5f * StairLayout.PieceRun), 0f, turn));
        run.Pieces.Add(Floor(kit, start + on * (1.5f * StairLayout.PieceRun), 0f, turn));
        bay.Runs.Add(run);
        return bay;
    }

    /// <summary>Two floors in line with the incoming's, the first set <paramref name="offset"/> to the right and starting
    /// <paramref name="on"/> past the join (negative: back over the incoming's last floor).</summary>
    internal static Bay Straight(StairKit kit, string name, float offset, float on)
    {
        var bay = new Bay { Name = name, Kit = kit };
        bay.Runs.Add(Incoming(kit));
        var start = new Vector2(offset, on);
        // Where it is met: the incoming's end, the join (a floor laid back over the incoming's last starts further back than
        // two ends met end to end stand apart, StairStructures.MetAtEnds; the network recorded its two staircases met there).
        var run = new BayRun { From = start, FromMeet = new Vector2(0f, 0f), To = start + new Vector2(0f, 2f * StairLayout.PieceRun) };
        run.Pieces.Add(Floor(kit, start + new Vector2(0f, 0.5f * StairLayout.PieceRun), 0f, 0f));
        run.Pieces.Add(Floor(kit, start + new Vector2(0f, 1.5f * StairLayout.PieceRun), 0f, 0f));
        bay.Runs.Add(run);
        return bay;
    }

    /// <summary>A stair turned <paramref name="turn"/> degrees right off the incoming's last floor and a floor on from it a
    /// metre up, laid as a layout lays a stair off a floor (<see cref="StairLayout.LayFlight"/>: pivoting on the inside corner,
    /// the wedge on the outside filled by the kit's fillers, <see cref="StairFiller"/>): the stair and its wedge the incoming
    /// staircase's, as a layout's turns are; the floor on from it the second's, in line.</summary>
    private static Bay OntoAStair(StairKit kit, float turn)
    {
        var bay = new Bay { Name = $"floor-to-stair-{Number(turn)}", Kit = kit };
        var kinds = new[] { StairPieceKind.Landing, StairPieceKind.Landing, StairPieceKind.Stair, StairPieceKind.Landing };
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, -2f * StairLayout.PieceRun), 0f, new[] { 0f, 0f, turn, turn }, kit,
            (x, z) => -100f, kinds);
        StairPiece top = plan.Pieces.Last(p => p.Kind == StairPieceKind.Landing);
        Vector2 on = StairLayout.Forward(turn), topCentre = new(top.Position.x, top.Position.z);
        Vector2 join = topCentre - on * (0.5f * StairLayout.PieceRun);
        var incoming = new BayRun { From = new Vector2(0f, -2f * StairLayout.PieceRun), To = join };
        var second = new BayRun { From = join, To = join + on * StairLayout.PieceRun };
        foreach (StairPiece p in plan.Pieces)
            if (p.Kind is StairPieceKind.Landing or StairPieceKind.Stair or StairPieceKind.Filler)
                (ReferenceEquals(p, top) ? second : incoming).Pieces.Add(StairRun.LaidPiece(p));
        bay.Runs.Add(incoming);
        bay.Runs.Add(second);
        return bay;
    }

    /// <summary>Where the network's bend's two staircases meet (their recorded ends), the bay's origin.</summary>
    internal static readonly Vector2 NetworkBendJoin = new(-6592f, -3488f);

    /// <summary>The walking surface of the bend's floors at the join, the bay's 0.</summary>
    internal const float NetworkBendSurface = 62.435f;

    /// <summary>
    /// Two black marble staircases met end to end on a ridge as a network laid them (a generated world, offline, at -6592,-3488:
    /// the lower one's top floor turned 22.5 degrees from the upper one's first floors, these overlapping, a floor 0.1 m on):
    /// their walking pieces within 7 m of the join and their recorded ends, as saved, moved into the bay's frame.
    /// </summary>
    private static Bay NetworkBend()
    {
        StructurePiece P(StructurePieceKind kind, string prefab, float x, float y, float z, float yaw) =>
            new() { Kind = kind, Prefab = prefab, Position = new Vector3(x, y, z), YawDegrees = yaw };
        var bay = new Bay { Name = "network-bend", Kit = StairKit.BlackMarble };
        var lower = new BayRun { From = new Vector2(0f, -32f), To = new Vector2(0f, 0f), ToMeet = new Vector2(-2.741f, -0.875f) };
        lower.Pieces.Add(P(StructurePieceKind.StairStep, "blackmarble_stair", -5.512f, -1.000f, -2.023f, 247.5f));
        lower.Pieces.Add(P(StructurePieceKind.StairFloor, "blackmarble_floor", -3.665f, -0.500f, -1.258f, 67.5f));
        var upper = new BayRun { From = new Vector2(0f, 0f), FromMeet = new Vector2(-2.741f, -0.875f), To = new Vector2(0f, 32f), ToMeet = new Vector2(-1.386f, 32.574f) };
        upper.Pieces.Add(P(StructurePieceKind.StairFloor, "blackmarble_floor", 5.458f, -1.431f, 2.987f, 247.5f));
        upper.Pieces.Add(P(StructurePieceKind.StairFiller, "blackmarble_2x1x1", 4.073f, -1.431f, 2.413f, 67.5f));
        upper.Pieces.Add(P(StructurePieceKind.StairFiller, "blackmarble_2x1x1", 4.185f, -1.431f, 2.525f, 22.5f));
        upper.Pieces.Add(P(StructurePieceKind.StairStep, "blackmarble_stair", 3.611f, -0.931f, 1.139f, 22.5f));
        upper.Pieces.Add(P(StructurePieceKind.StairFiller, "blackmarble_2x1x1", 3.037f, -0.431f, -0.247f, 22.5f));
        upper.Pieces.Add(P(StructurePieceKind.StairFiller, "blackmarble_1x1", 2.805f, -0.431f, -0.902f, 90f));
        upper.Pieces.Add(P(StructurePieceKind.StairFloor, "blackmarble_floor", 1.305f, -0.431f, -0.402f, 270f));
        upper.Pieces.Add(P(StructurePieceKind.StairFloor, "blackmarble_floor", -1.817f, -0.500f, -0.492f, 67.5f));
        upper.Pieces.Add(P(StructurePieceKind.StairFloor, "blackmarble_floor", -0.799f, -0.500f, -0.402f, 90f));
        upper.Pieces.Add(P(StructurePieceKind.StairFloor, "blackmarble_floor", -0.696f, -0.500f, -0.402f, 90f));
        bay.Runs.Add(lower);
        bay.Runs.Add(upper);
        return bay;
    }

    /// <summary>The bay's box in its frame: its walking pieces' middles, <see cref="Margin"/> round.</summary>
    internal static (Vector2 min, Vector2 max) Box(Bay bay)
    {
        var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        foreach (BayRun run in bay.Runs)
            foreach (StructurePiece p in run.Pieces)
            {
                min = new Vector2(Mathf.Min(min.x, p.Position.x), Mathf.Min(min.y, p.Position.z));
                max = new Vector2(Mathf.Max(max.x, p.Position.x), Mathf.Max(max.y, p.Position.z));
            }
        var margin = new Vector2(Margin, Margin);
        return (min - margin, max + margin);
    }

    /// <summary>The lowest underside of the bay's walking pieces under its join's walking surface (negative).</summary>
    internal static float LowestUnderside(Bay bay)
    {
        float lowest = 0f;
        foreach (BayRun run in bay.Runs)
            foreach (StructurePiece p in run.Pieces)
                if (StairSupport.WalkOf(p, bay.Kit) is StairSupport.Walk w) lowest = Mathf.Min(lowest, w.Underside);
        return lowest;
    }

    /// <summary>The join's walking surface over ground whose highest point under the bay is <paramref name="highestGround"/>:
    /// its lowest walking piece <see cref="Clearance"/> over it.</summary>
    internal static float SurfaceOver(Bay bay, float highestGround) => highestGround + Clearance - LowestUnderside(bay);

    /// <summary>The highest underside of the bay's walking pieces under its join's walking surface.</summary>
    internal static float HighestUnderside(Bay bay)
    {
        float highest = float.NegativeInfinity;
        foreach (BayRun run in bay.Runs)
            foreach (StructurePiece p in run.Pieces)
                if (StairSupport.WalkOf(p, bay.Kit) is StairSupport.Walk w) highest = Mathf.Max(highest, w.Underside);
        return highest;
    }

    /// <summary>The lowest and highest finished ground at the whole-metre vertices under the bay's box with its join at
    /// <paramref name="join"/> (NaN, NaN where none is known).</summary>
    internal static (float lowest, float highest) GroundUnder(Bay bay, Vector2 join, Func<int, int, float> finished)
    {
        var (min, max) = Box(bay);
        float lowest = float.PositiveInfinity, highest = float.NegativeInfinity;
        for (int vx = Mathf.FloorToInt(join.x + min.x); vx <= Mathf.CeilToInt(join.x + max.x); vx++)
            for (int vz = Mathf.FloorToInt(join.y + min.y); vz <= Mathf.CeilToInt(join.y + max.y); vz++)
            {
                float h = finished(vx, vz);
                if (float.IsNaN(h)) continue;
                lowest = Mathf.Min(lowest, h);
                highest = Mathf.Max(highest, h);
            }
        return float.IsInfinity(highest) ? (float.NaN, float.NaN) : (lowest, highest);
    }

    /// <summary>How far the bay's highest walking piece's underside stands over the lowest ground under it, laid
    /// <see cref="Clearance"/> over the highest (<see cref="SurfaceOver"/>): within <see cref="StairSupport.WallHeight"/>, every
    /// piece of a stone bay stands on a wall to the ground, as on the gentle ground a layout lays its floors on; higher, it is
    /// held from the slope (arches and corbels out into it, their low ends over the air) or refused.</summary>
    internal static float Air(Bay bay, float lowestGround, float highestGround) =>
        highestGround + Clearance + (HighestUnderside(bay) - LowestUnderside(bay)) - lowestGround;

    /// <summary>The box over every piece a bay's kits laid (<paramref name="pieces"/>, its supports out on a slope and its
    /// railing too), <see cref="Kept"/> round: where its capture looks (its walking pieces' box, <see cref="Box"/>, left a
    /// corbel 2 m out from a floor's side out of it).</summary>
    internal static (Vector2 min, Vector2 max) Reach(IEnumerable<StructurePiece> pieces)
    {
        var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        foreach (StructurePiece p in pieces)
        {
            min = new Vector2(Mathf.Min(min.x, p.Position.x), Mathf.Min(min.y, p.Position.z));
            max = new Vector2(Mathf.Max(max.x, p.Position.x), Mathf.Max(max.y, p.Position.z));
        }
        var kept = new Vector2(Kept, Kept);
        return (min - kept, max + kept);
    }

    /// <summary>The bay's staircases in the world, its join at <paramref name="join"/> with its walking surface there at
    /// <paramref name="surface"/>.</summary>
    internal static List<StairRun> Place(Bay bay, Vector2 join, float surface)
    {
        var runs = new List<StairRun>();
        foreach (BayRun b in bay.Runs)
        {
            var run = new StairRun
            {
                From = b.From + join, To = b.To + join, FromMeet = b.FromMeet + join, ToMeet = b.ToMeet + join,
                Width = StairLayout.PieceRun, Kit = bay.Kit,
            };
            foreach (StructurePiece p in b.Pieces)
                run.Pieces.Add(new StructurePiece
                {
                    Kind = p.Kind, Prefab = p.Prefab, Position = p.Position + new Vector3(join.x, surface, join.y), YawDegrees = p.YawDegrees,
                });
            runs.Add(run);
        }
        return runs;
    }

    /// <summary>Each of the bay's staircases dressed by its kit among the bay's (the other met end to end), as generation
    /// dresses a network's (<see cref="StairStructures.DressAmong"/>), unweathered; an empty list for one its kit refuses,
    /// said in <paramref name="shortfall"/>.</summary>
    internal static List<List<StructurePiece>> Dress(List<StairRun> runs, Func<StairRun, Func<float, float, float>> natural,
        Func<int, int, float> finished, StairSupport.Shortfall shortfall, Func<float, float, Heightmap.Biome>? biomeAt) =>
        runs.ConvertAll(run => StairStructures.DressAmong(run, runs, natural, finished, shortfall, false, biomeAt));

    /// <summary>A piece as it stands: its prefab, where, its turn (degrees), and whether the catalogue laid it.</summary>
    internal readonly struct Placed
    {
        public readonly string Prefab;
        public readonly Vector3 At;
        public readonly float Yaw, Pitch, Roll;
        public readonly bool Laid;

        public Placed(string prefab, Vector3 at, float yaw, float pitch, float roll, bool laid)
        {
            Prefab = prefab; At = at; Yaw = StairLayout.Wrap(yaw); Pitch = Signed(pitch); Roll = Signed(roll); Laid = laid;
        }

        public static Placed Of(StructurePiece p) => new(p.Prefab, p.Position, p.YawDegrees, p.PitchDegrees, p.RollDegrees, true);

        private static float Signed(float a) => Mathf.Abs(Delta(0f, a)) < 0.01f ? 0f : Delta(0f, a);
    }

    /// <summary>The header of a bay's piece table (<see cref="Row"/>).</summary>
    internal const string Header = "prefab\tx\ty\tz\tyaw\tpitch\troll\tlaid\tlocal_x\tlocal_y\tlocal_z";

    /// <summary>A piece as a row of its bay's table, with where it stands in the bay's frame (from the join at its walking
    /// surface, <paramref name="join"/>).</summary>
    internal static string Row(Placed p, Vector3 join) => string.Join("\t", p.Prefab, F(p.At.x), F(p.At.y), F(p.At.z), F(p.Yaw), F(p.Pitch),
        F(p.Roll), p.Laid ? "kit" : "hand", F(p.At.x - join.x), F(p.At.y - join.y), F(p.At.z - join.z));

    /// <summary>A row of a bay's table back (<see cref="Row"/>); null for the header or a line it cannot read.</summary>
    internal static Placed? Parse(string line)
    {
        string[] f = line.Split('\t');
        if (f.Length < 8 || f[0] == "prefab") return null;
        static float R(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        try { return new Placed(f[0], new Vector3(R(f[1]), R(f[2]), R(f[3])), R(f[4]), R(f[5]), R(f[6]), f[7] == "kit"); }
        catch (FormatException) { return null; }
    }

    private static string F(float v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>How far a piece may stand from where it was laid and still be the one laid (a hand's build snaps to the
    /// pieces it is built on, a hand's move is never smaller).</summary>
    public const float Kept = 0.05f;

    /// <summary>How far a piece removed and one of the same prefab added are taken for one moved.</summary>
    public const float MovedReach = 1.5f;

    /// <summary>The turn a piece's prefab looks the same after (its footprint's symmetry): a post a quarter turn, a level rail
    /// or a crossed rail and a floor a half and a quarter, anything else a whole turn (a sloped rail, a stair).</summary>
    internal static float Period(string prefab) => prefab switch
    {
        BridgeLayout.RailPostPrefab or "wood_pole2" => 90f,
        StairRailing.LevelRailPrefab or StairRailing.ShortRailPrefab or StairRailing.CrossedRailPrefab => 180f,
        _ when prefab.EndsWith("_floor", StringComparison.Ordinal) => 90f,
        _ => 360f,
    };

    private static float Turned(float a, float b, float period)
    {
        float d = (a - b) % period;
        if (d < 0f) d += period;
        return Mathf.Min(d, period - d);
    }

    private static bool Same(Placed a, Placed b) =>
        a.Prefab == b.Prefab && Vector3.Distance(a.At, b.At) <= Kept && Turned(a.Yaw, b.Yaw, Period(a.Prefab)) < 1f
        && Mathf.Abs(Delta(a.Pitch, b.Pitch)) < 1f && Mathf.Abs(Delta(a.Roll, b.Roll)) < 1f;

    /// <summary>What a hand changed in a bay: each piece its kit laid that stands as laid (kept), each gone (removed), each
    /// added, and a removed and an added piece of one prefab within <see cref="MovedReach"/> of each other paired as moved
    /// (nearest first). Lines of the diff, coordinates in the world and in the bay's frame (from <paramref name="join"/>).</summary>
    internal static List<string> Diff(IReadOnlyList<Placed> laid, IReadOnlyList<Placed> now, Vector3 join,
        out (int kept, int moved, int removed, int added) counts)
    {
        var gone = new List<Placed>();
        var left = new List<Placed>(now);
        int kept = 0;
        foreach (Placed p in laid)
        {
            int i = left.FindIndex(q => Same(p, q));
            if (i >= 0) { left.RemoveAt(i); kept++; }
            else gone.Add(p);
        }
        var pairs = new List<(Placed from, Placed to)>();
        foreach (var (g, a) in gone.SelectMany(g => left.Where(a => a.Prefab == g.Prefab && Vector3.Distance(g.At, a.At) <= MovedReach)
                     .Select(a => (g, a))).OrderBy(t => Vector3.Distance(t.g.At, t.a.At)).ToList())
        {
            if (!gone.Contains(g) || !left.Contains(a)) continue;
            gone.Remove(g); left.Remove(a);
            pairs.Add((g, a));
        }
        string Where(Placed p) => FormattableString.Invariant(
            $"{p.Prefab} at ({p.At.x:F3}, {p.At.y:F3}, {p.At.z:F3}) yaw {p.Yaw:F1}{(p.Pitch != 0f || p.Roll != 0f ? $" pitch {p.Pitch:F1} roll {p.Roll:F1}" : "")}, local ({p.At.x - join.x:F3}, {p.At.y - join.y:F3}, {p.At.z - join.z:F3})");
        var lines = new List<string>();
        foreach (Placed p in gone) lines.Add("removed\t" + Where(p));
        foreach (Placed p in left) lines.Add("added\t" + Where(p));
        foreach (var (from, to) in pairs)
            lines.Add("moved\t" + Where(from) + " -> " + FormattableString.Invariant(
                $"({to.At.x:F3}, {to.At.y:F3}, {to.At.z:F3}) yaw {to.Yaw:F1}, by ({to.At.x - from.At.x:F3}, {to.At.y - from.At.y:F3}, {to.At.z - from.At.z:F3}) and {Delta(from.Yaw, to.Yaw):F1} degrees"));
        counts = (kept, pairs.Count, gone.Count, left.Count);
        return lines;
    }
}
