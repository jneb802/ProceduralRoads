using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The railed wooden kits, the wooden bridges outside the Mistlands: bridges and fords' spans laid as a hand-built wood
/// bridge was, in vanilla wood pieces on the hammer's yaw grid, level, each on its neighbours' snap points. Pure logic. The
/// deck height, its length, the navigation gap and both stair runs are the wooden kits' shared rules
/// (<see cref="WoodDeckHeight"/>, <see cref="BuiltLength"/>, <see cref="NavigationGap"/>, <see cref="LayEndStairs"/>), which the
/// Dvergr wood kit's and the stone kit's ends share too. Which crossings get one is <see cref="BridgeStructures.KitFor"/>'s choice.
///
/// <para>A ford's span is the same kit on the footbridge's deck (<see cref="SpanDeckHeight"/>, a metre over the higher
/// bank): no navigation gap and no free span (a ford has neither), its columns short ones down to the ford's bed, its ends
/// the stair runs from that deck, usually a step.</para>
///
/// <para>In the bridge's own frame (along the deck from the near bank, across it, height), with the stations every
/// <see cref="DeckSpan"/>:</para>
/// <list type="bullet">
/// <item><b>deck</b>: two lanes of <see cref="DeckPrefab"/> plates in every bay, across +-<see cref="LaneOffset"/>;</item>
/// <item><b>stringers</b>: a <see cref="BeamPrefab"/> along each edge in every bay, at the walking surface, across
/// +-<see cref="DeckHalfWidth"/>, on the plates' outer corners;</item>
/// <item><b>handrail</b>: the same beam <see cref="RailHeight"/> over each stringer, on a <see cref="RailPostPrefab"/>
/// railing post at every station each side;</item>
/// <item><b>columns</b>: <see cref="PostPrefab"/> poles stacked under both edges from the deck down to the ground, at every
/// station (<see cref="Rail"/>) or every other one (<see cref="Braced"/>), however high the deck stands within the wood
/// support ceiling (<see cref="RailedMaxColumn"/>);</item>
/// <item><b>braces</b> (<see cref="Braced"/> only): one <see cref="BracePrefab"/> in every bay each side, from a column's
/// foot of the tier (<see cref="BraceRise"/> under the deck) up to the stringer, rising from the even stations so the row
/// zig-zags between the columns; the two end braces lean from the bank columns into the banks;</item>
/// <item><b>ends</b>: the stairs as the road meets the end (<see cref="LayEndStairs"/>), two abreast, each step with a <see cref="StairRailPrefab"/>
/// stringer at its outer edge and a handrail over it each side, on a railing post at every step's joint as on the deck
/// (every <see cref="StairRun"/>), the last one (the newel) at the foot; a step over the ground stands on columns under its
/// edges at its low joint.</item>
/// </list>
/// <para><b>Runs without a column</b>: none stands in the open bays over a river, under a free span (<see cref="IsFreeSpan"/>)
/// but at its banks, or where it would stand over <see cref="RailedMaxColumn"/>. The deck is carried over such a run from
/// the columns either side, which always stand, when they are within <see cref="FreeSpanMaxWidth"/> of each other (what a
/// wooden deck holds up unsupported); a longer run is left open, its rails stopping on newels as at a navigation gap.</para>
/// <para><b>Nothing is buried</b>: a stringer, handrail or brace wholly under the ground is not laid (a stair's may meet a
/// hillside), nor a railing post wholly under it, a column (a fallen one's stub too) ends at the first pole whose foot is
/// in the ground, and a stub's break under the ground leaves none: the ground
/// as the road leaves it under an end (<see cref="RailedGround"/>). <b>A navigation gap</b> over a river
/// is whole bays of the kit's own grid (<see cref="RailedNavigationBays"/>: 2 m for <see cref="Rail"/>, 4 m for
/// <see cref="Braced"/>), the fewest that hold the ship's way, a column at each end: no column, post, plate or rail in it,
/// the stringers and handrail stopping at its ends on the railing posts there (its newels); under a deck the ship passes
/// under, only its columns are left out. <b>The ruin</b> is every kit's (<see cref="BridgeRuin"/>, keyed by what it
/// decides): plates lost at its deck share and now and then a bay's two open at its hole share, never two bays running;
/// handrails and posts at its course share, a post under a handrail left put back; braces at its support share; interior
/// columns fall toward mid-span and leave a stub or debris, the deck carried over a fallen one from the columns either
/// side (one is put back where they would stand further apart than a wooden deck holds); the bank bays, the ends and their
/// stairs always stand, and every piece is worn as a vanilla location's. The complete plan
/// (<see cref="SolveRailedComplete"/>) is the unruined kit, the gap filled in, which is what a player can repair it to.</para>
/// </summary>
public static partial class BridgeLayout
{
    /// <summary>One railed kit's data: what differs between the kits; the layout is <see cref="SolveRailed"/>'s.</summary>
    public sealed class RailedKit
    {
        /// <summary>The kit's name, as <see cref="BridgeStructures.Kit"/> prints it.</summary>
        public readonly string Name;
        /// <summary>A column under every this many stations (1: every 2 m; 2: every 4 m).</summary>
        public readonly int ColumnEvery;
        /// <summary>Whether a tier of diagonal braces runs under the stringers.</summary>
        public readonly bool Braced;

        public RailedKit(string name, int columnEvery, bool braced)
        {
            Name = name;
            ColumnEvery = columnEvery;
            Braced = braced;
        }

        public override string ToString() => Name;
    }

    /// <summary>Kit A: columns at every station, 2 m bays.</summary>
    public static readonly RailedKit Rail = new("WoodRail", 1, false);
    /// <summary>Kit B: columns every 4 m, a zig-zag of braces under the stringers.</summary>
    public static readonly RailedKit Braced = new("WoodBraced", 2, true);

    /// <summary>The railing posts and newels: a 1 m pole, origin at its centre, snaps top and bottom (0, +-0.5, 0).</summary>
    public const string RailPostPrefab = "wood_pole";
    /// <summary>The stair's stringers and handrails: a 26-degree beam, origin at its low snap (-1, 0, 0), rising along its
    /// local x to its high snap (1, 1, 0): 2 m out, 1 m up, one stair step.</summary>
    public const string StairRailPrefab = "wood_beam_26";
    /// <summary>The braces: a steep beam, origin at its low snap (-1, 0, 0), rising along its local x to its high snap
    /// (1, 4, 0): one bay out, <see cref="BraceRise"/> up.</summary>
    public const string BracePrefab = "wood_beam_67";

    /// <summary>The handrail over the walking surface (and over each step's stringer).</summary>
    public const float RailHeight = 1f;
    /// <summary>How far a brace rises over its one bay.</summary>
    public const float BraceRise = 4f;
    /// <summary>A <see cref="PostPrefab"/> pole's width (0.4 m square, measured in game with cli_piece_geometry): a column
    /// at a navigation gap's edge reaches half of it into the opening.</summary>
    public const float PoleWidth = 0.4f;

    /// <summary>Half a beam's depth about its snap line: a beam is wholly buried once the ground stands this far over it
    /// along its whole length (an estimate of the vanilla beams' section, not measured).</summary>
    public const float RailHalfDepth = 0.15f;

    /// <summary>The tallest column a railed kit stands (deck to the ground under it): the wood support ceiling
    /// (<see cref="RoadConstants.MaxBridgePierHeight"/>, measured in game), the deck carried on its column tops. A station
    /// whose column would stand taller has none, and the deck is carried over it from the columns either side within the
    /// free span (<see cref="FreeSpanMaxWidth"/>), or left open past it. The braces add no height: they hang from a column
    /// under its top, so they offer the stringer less support than the column's top does.</summary>
    public static float RailedMaxColumn => RoadConstants.MaxBridgePierHeight;

    /// <summary>Whether a wooden column (railed or Dvergr wood) of this height, the deck to the ground under it, stands
    /// over the wood support ceiling (<see cref="RailedMaxColumn"/>): there is none in every wooden kit.</summary>
    public static bool OverWoodCeiling(float column) => column > RailedMaxColumn;

    /// <summary>Whether a wooden kit's deck is carried over a run of its stations (or bents) without a column,
    /// <paramref name="first"/> to <paramref name="last"/> of <paramref name="alongs"/>, from the columns either side: they
    /// stand within <see cref="FreeSpanMaxWidth"/> of each other. A longer run, or one reaching a bank, would fall and is
    /// left open.</summary>
    public static bool RunCarried(IReadOnlyList<float> alongs, int first, int last) =>
        first > 0 && last < alongs.Count - 1 && alongs[last + 1] - alongs[first - 1] <= FreeSpanMaxWidth + 1e-3f;

    /// <summary>Ruin roles of the railed kits' own decisions (15-18, between the Dvergr wood kit's and the viaduct's; the
    /// stone kit's railing rolls them too), and the kit's pick (19, <see cref="PickKit{T}"/>).</summary>
    public const int RoleRail = 15, RoleRailPost = 16, RoleRailHealth = 17, RoleBraceHealth = 18, RoleKit = 19;
    /// <summary>Ruin roles of the railed kits' braces lost, bays open from side to side and plates put back (32-34, past
    /// the stone kit's).</summary>
    internal const int RoleBraceLost = 32, RoleRailedHole = 33, RolePlateBack = 34;

    /// <summary>The column a railed kit would stand at a station: the walking surface over the lower of the ground under
    /// its two edges (on <paramref name="ground"/>, <see cref="RailedGround"/>). Over <see cref="RailedMaxColumn"/>, the
    /// station has none.</summary>
    private static float ColumnHeight(Vector2 station, Vector2 side, float deckH, System.Func<Vector2, float> ground) =>
        deckH - Mathf.Min(ground(station - side * DeckHalfWidth), ground(station + side * DeckHalfWidth));

    /// <summary>
    /// The ground the railed and stone kits are laid on: the finished ground (<see cref="StructureGround.Of"/>, ends as
    /// <see cref="StructureEnds.Of"/> gives them), so a bank column reaches the levelled road rather than stopping on the
    /// bank the road cuts away, and a piece under a landing the road raises is left out as buried.
    /// </summary>
    private static System.Func<Vector2, float> RailedGround(StructureEnds.End near, StructureEnds.End far, WorldGenerator world) =>
        StructureGround.Of(world, near, far, DeckHalfWidth + BuriedPieces.Growth);

    /// <summary>The crossings a railed kit is laid on: a bridge, and a ford laid as a span (its footbridge, the same kit on
    /// a lower deck, <see cref="SpanDeckHeight"/>). A wading or raised ford is road.</summary>
    internal static bool Railed(RoadCrossing? crossing) => BridgeStructures.HasDeck(crossing);

    /// <summary>The length of one of a kit's bays: the run between its columns (<see cref="RailedKit.ColumnEvery"/> stations).</summary>
    public static float BayLength(RailedKit kit) => kit.ColumnEvery * DeckSpan;

    /// <summary>
    /// The bays a railed kit leaves open over a river (<see cref="OpenBays"/>), in its own bays (<see cref="BayLength"/>)
    /// from the near deck end: the first, how many, and whether their deck is open too (false: the ship passes under the
    /// deck, and only the columns are left out). Null with no navigation gap: off a river, over a ford, under a free span.
    /// A column stands at both ends of the opening, half a pole (<see cref="PoleWidth"/>) reaching into it; the stringers
    /// end on it.
    /// </summary>
    public static (int first, int count, bool deck)? RailedNavigationBays(RoadCrossing crossing, WorldGenerator world, RailedKit kit)
    {
        if (crossing == null || world == null || crossing.Kind != CrossingKind.Bridge || IsFreeSpan(crossing, world))
            return null;
        if (NavigationGap(crossing, world) is not ShipWay way) return null;
        int bays = Bays(crossing.Width) / kit.ColumnEvery;
        if (bays < 1) return null;
        var (first, count) = OpenBays(way, BayLength(kit), bays, PoleWidth * 0.5f);
        return (first, count, way.DeckWidth > 0f);
    }

    /// <summary>Whether a point this far along the deck lies in a railed kit's open bays with their deck open
    /// (<see cref="RailedNavigationBays"/>), strictly between the columns that bound them.</summary>
    public static bool InRailedNavigationGap(RoadCrossing crossing, WorldGenerator world, RailedKit kit, float along) =>
        RailedNavigationBays(crossing, world, kit) is (int first, int count, true)
        && along > first * BayLength(kit) + 1e-3f && along < (first + count) * BayLength(kit) - 1e-3f;

    /// <summary>Which of <paramref name="kits"/> (those that fit the crossing, in a fixed order) a crossing draws: each at
    /// even odds, fixed by the world seed and the crossing (<see cref="Ruin"/>'s roll, its own role), so every visit draws
    /// the same. Of two kits, the first where the roll is under a half.</summary>
    public static T PickKit<T>(RoadCrossing crossing, int worldSeed, IReadOnlyList<T> kits) =>
        kits[Mathf.Min(kits.Count - 1, Mathf.FloorToInt(new Ruin(worldSeed, crossing).Roll(0, Ruin.Centre, RoleKit) * kits.Count))];

    /// <summary>The railed bridge as it stands, ruined (the game's plan).</summary>
    public static List<StructurePiece> SolveRailed(RoadCrossing crossing, WorldGenerator world, int worldSeed, RailedKit kit) =>
        SolveRailed(crossing, world, worldSeed, kit, ruin: true);

    /// <summary>The railed bridge with nothing missing, the navigation gap filled in: what a player can repair it to.</summary>
    internal static List<StructurePiece> SolveRailedComplete(RoadCrossing crossing, WorldGenerator world, int worldSeed, RailedKit kit) =>
        SolveRailed(crossing, world, worldSeed, kit, ruin: false);

    private static List<StructurePiece> SolveRailed(RoadCrossing crossing, WorldGenerator world, int worldSeed, RailedKit kit, bool ruin)
    {
        var pieces = new List<StructurePiece>();
        if (!Railed(crossing) || world == null || crossing.Width < DeckSpan)
            return pieces;

        Ruin r = new(worldSeed, crossing);
        Vector2 from = crossing.FromBank, dir = crossing.Direction, side = new(-dir.y, dir.x);
        float yaw = YawDegrees(dir);
        float along90 = yaw + 90f;   // a beam's local x (and the plates', as the reference's) along the deck
        float deckH = WoodDeckHeight(crossing, world);
        bool freeSpan = IsFreeSpan(crossing, world);
        // The open bays over a river (none over a ford or under a free span), stations strictly between their columns.
        (int first, int count, bool deck)? gap = RailedNavigationBays(crossing, world, kit);
        bool deckGap = gap is (_, _, true);
        var (nearEnd, farEnd) = StructureEnds.Of(crossing, world);
        System.Func<Vector2, float> groundAt = RailedGround(nearEnd, farEnd, world);
        float Ground(Vector2 p) => groundAt(p);

        float[] alongs = StationsAlong(crossing.Width);
        int last = alongs.Length - 1;
        var pos = new Vector2[last + 1];
        // No column in the open bays (where the deck opens, the complete plan fills the gap in: a player may),
        // under a free span but at its banks, nor where it would stand over the wood support ceiling.
        var noColumn = new bool[last + 1];
        var deckOpen = new bool[last + 1];
        for (int i = 0; i <= last; i++)
        {
            pos[i] = from + dir * alongs[i];
            bool open = gap is (int g0, int gn, _) && i > g0 * kit.ColumnEvery && i < (g0 + gn) * kit.ColumnEvery;
            noColumn[i] = (open && (ruin || !deckGap)) || (freeSpan && i > 0 && i < last)
                || OverWoodCeiling(ColumnHeight(pos[i], side, deckH, groundAt));
            deckOpen[i] = open && deckGap && ruin;
        }
        // A run of stations without a column is carried by the deck from the columns either side, as a free span is, when
        // those stand within FreeSpanMaxWidth of each other; a longer run would fall, and is left open.
        var spanned = new bool[last + 1];
        for (int i = 0; i <= last; i++)
        {
            if (!noColumn[i]) continue;
            int j = i;
            while (j < last && noColumn[j + 1]) j++;
            bool held = RunCarried(alongs, i, j);
            for (int k = i; k <= j; k++) spanned[k] = held && !deckOpen[k];
            i = j;
        }
        float mid = last * 0.5f;
        float Survival(float at) => SpanSurvival(mid > 0f ? 1f - Mathf.Abs(at - mid) / mid : 0f);
        RuinProfile profile = BridgeRuin;
        float Survives(float at, float share) => RuinProfile.Survives(Survival(at), share);

        // Columns: every ColumnEvery-th station, both banks', and each side of a run without one. A column is the one part
        // of a wooden kit that falls whole (the profile's columns, outliving the deck): the banks' and those either side
        // of a run without one always stand, for the rails stop on them or the deck is carried from them.
        var column = new bool[last + 1];
        var standing = new bool[last + 1];
        for (int i = 0; i <= last; i++)
        {
            bool bordersOpening = (i > 0 && noColumn[i - 1]) || (i < last && noColumn[i + 1]);
            column[i] = !noColumn[i] && (i % kit.ColumnEvery == 0 || i == last || bordersOpening);
            if (!column[i]) continue;
            bool bank = i == 0 || i == last || bordersOpening;
            standing[i] = !ruin || bank || r.Roll(i, Ruin.Centre, Ruin.RoleStation) < PierSurvival(Survival(i));
        }
        // The deck is carried over a fallen column from the columns either side, as over a run without one, while they
        // stand within FreeSpanMaxWidth of each other; where more have fallen than that, the fallen one nearest the middle
        // is put back until they do, so no stretch of deck falls with its columns.
        for (bool changed = ruin; changed;)
        {
            changed = false;
            for (int i = 0, prev = -1; i <= last && !changed; i++)
            {
                if (noColumn[i]) prev = -1;
                if (!standing[i]) continue;
                if (prev >= 0 && alongs[i] - alongs[prev] > FreeSpanMaxWidth + 1e-3f)
                {
                    int put = -1;
                    for (int k = prev + 1; k < i; k++)
                        if (column[k] && (put < 0 || Mathf.Abs(k - (prev + i) * 0.5f) < Mathf.Abs(put - (prev + i) * 0.5f))) put = k;
                    if (put >= 0) { standing[put] = true; changed = true; }
                }
                prev = i;
            }
        }
        for (int i = 0; i <= last; i++)
        {
            if (!column[i]) continue;
            if (standing[i])
            {
                foreach (int s in new[] { Ruin.Left, Ruin.Right })
                {
                    Vector2 p = pos[i] + side * (s * DeckHalfWidth);
                    EmitColumn(pieces, p, Ground(p), deckH, along90, r.Health(i, s, Ruin.RolePostHealth), toFoot: true);
                }
            }
            else if (r.Roll(i, Ruin.Centre, Ruin.RoleStub) < profile.StubChance)
            {
                // A stub broken off a post's length over its own ground, at most just over the water, standing on that
                // ground: none where the ground already stands at the break (a bank, or a landing the road raised).
                foreach (int s in new[] { Ruin.Left, Ruin.Right })
                {
                    Vector2 p = pos[i] + side * (s * DeckHalfWidth);
                    float ground = Ground(p);
                    EmitColumn(pieces, p, ground, Mathf.Min(ground + PostSegment, crossing.WaterLevel + 0.3f), along90,
                        0.25f + r.Roll(i, s, Ruin.RoleStub, 1) * 0.15f, toFoot: true);
                }
            }
            else if (r.Roll(i, Ruin.Centre, Ruin.RoleDebris) < profile.DebrisChance)
                EmitDebris(pieces, pos[i], dir, groundAt, r, i);
        }

        // Braces: one a bay each side, rising from the bay's even station (its column) to the other; past each bank's
        // column, one more leaning into the bank. Laid with a standing column under it, never into a run without one; in
        // the ruin lost at the profile's support share, but never in or past a bank's bay.
        var braced = new bool[last + 1];
        if (kit.Braced)
        {
            for (int bay = -1; bay <= last; bay++)
            {
                int foot = bay < 0 ? 0 : bay == last ? last : bay % 2 == 0 ? bay : bay + 1;
                int head = bay < 0 ? -1 : bay == last ? last + 1 : foot == bay ? bay + 1 : bay;
                if (!standing[foot]) continue;
                if (head >= 0 && head <= last && noColumn[head]) continue;
                bool bankBay = bay <= 0 || bay >= last - 1;
                Vector2 footAt = pos[foot], headAt = from + dir * (alongs[foot] + (head - foot) * DeckSpan);
                Vector2 up = (headAt - footAt).normalized;
                foreach (int s in new[] { Ruin.Left, Ruin.Right })
                {
                    Vector2 off = side * (s * DeckHalfWidth);
                    if (Buried(groundAt, footAt + off, deckH - BraceRise, headAt + off, deckH)) continue;
                    if (ruin && !bankBay && r.Roll(bay + 1, s, RoleBraceLost) >= Survives(bay + 0.5f, profile.SupportShare)) continue;
                    Vector2 c = (footAt + headAt) * 0.5f + off;
                    pieces.Add(new StructurePiece
                    {
                        Kind = StructurePieceKind.Beam,
                        Prefab = BracePrefab,
                        Position = new Vector3(c.x, deckH - BraceRise, c.y),
                        YawDegrees = YawDegrees(up) - 90f,   // its local x from its foot to its head
                        HealthFraction = r.Health(bay + 1, s, RoleBraceHealth),
                    });
                    if (head >= 0 && head <= last) braced[head] = true;
                }
            }
        }

        // What carries the deck at a station: its column, or a brace's head, or the deck itself, carried over it from the
        // standing columns either side within FreeSpanMaxWidth (a station between columns, or one whose column fell), or
        // over a run without a column (spanned).
        var carried = new bool[last + 1];
        var prevStanding = new int[last + 1];
        var nextStanding = new int[last + 1];
        for (int i = 0, p = -1; i <= last; i++)
        {
            if (noColumn[i]) p = -1;
            if (standing[i]) p = i;
            prevStanding[i] = p;
        }
        for (int i = last, n = -1; i >= 0; i--)
        {
            if (noColumn[i]) n = -1;
            if (standing[i]) n = i;
            nextStanding[i] = n;
        }
        for (int i = 0; i <= last; i++)
        {
            bool betweenColumns = !noColumn[i] && prevStanding[i] >= 0 && nextStanding[i] >= 0
                && alongs[nextStanding[i]] - alongs[prevStanding[i]] <= FreeSpanMaxWidth + 1e-3f;
            carried[i] = !deckOpen[i] && (standing[i] || braced[i] || (noColumn[i] && spanned[i]) || betweenColumns);
        }
        var bayUp = new bool[last];
        for (int j = 0; j < last; j++)
            bayUp[j] = carried[j] && carried[j + 1];

        // The railing: a post at every carried station on a stringer, a handrail on each pair of them, both lost at the
        // profile's course share but over the bank bays; the posts at the gap's edges (its newels) always stand. A handrail
        // left rests on both its posts: one the rolls took is put back.
        var post = new bool[last + 1, 2];
        var canPost = new bool[last + 1];
        for (int i = 0; i <= last; i++)
        {
            canPost[i] = carried[i] && ((i > 0 && bayUp[i - 1]) || (i < last && bayUp[i]));
            if (!canPost[i]) continue;
            bool newel = i == 0 || i == last || (i > 0 && noColumn[i - 1] && !carried[i - 1]) || (i < last && noColumn[i + 1] && !carried[i + 1]);
            for (int k = 0; k < 2; k++)
                post[i, k] = !ruin || newel || r.Roll(i, k == 0 ? Ruin.Left : Ruin.Right, RoleRailPost) < Survives(i, profile.CourseShare);
        }
        var rail = new bool[last, 2];
        for (int j = 0; j < last; j++)
        {
            if (!bayUp[j]) continue;
            bool endBay = j == 0 || j + 1 == last;
            for (int k = 0; k < 2; k++)
            {
                int s = k == 0 ? Ruin.Left : Ruin.Right;
                Vector2 off = side * (s * DeckHalfWidth);
                if (Buried(groundAt, pos[j] + off, deckH + RailHeight, pos[j + 1] + off, deckH + RailHeight)) continue;
                if (ruin && !endBay && r.Roll(j, s, RoleRail) >= Survives(j + 0.5f, profile.CourseShare)) continue;
                rail[j, k] = true;
                post[j, k] = post[j + 1, k] = true;
            }
        }
        for (int i = 0; i <= last; i++)
            for (int k = 0; k < 2; k++)
            {
                if (!post[i, k]) continue;
                int s = k == 0 ? Ruin.Left : Ruin.Right;
                Vector2 p = pos[i] + side * (s * DeckHalfWidth);
                AddPiece(pieces, StructurePieceKind.Post, RailPostPrefab, p, deckH + RailHeight * 0.5f, along90, r.Health(i, s, RoleRailHealth, 1));
            }

        // The plates: lost at the profile's deck share, never over a bank's bay; now and then a bay open from side to side
        // (both plates) at its hole share, never two within three bays, and never two bays running without a plate, a
        // plate put back in the one that is not a hole, so every gap is one a player jumps.
        var plate = new bool[last, 2];
        var hole = new bool[last];
        for (int j = 0; j < last; j++)
        {
            if (!bayUp[j]) continue;
            bool endBay = j == 0 || j + 1 == last;
            for (int l = 0; l < 2; l++)
                plate[j, l] = !ruin || endBay || r.Roll(j, l == 0 ? Ruin.Left : Ruin.Right, Ruin.RoleDeck) < Survives(j + 0.5f, profile.DeckShare);
        }
        if (ruin)
        {
            for (int j = 1, lastHole = -3; j + 2 <= last; j++)
                if (bayUp[j - 1] && bayUp[j] && bayUp[j + 1] && j - lastHole >= 3
                    && r.Roll(j, Ruin.Centre, RoleRailedHole) >= Survives(j + 0.5f, profile.HoleShare))
                {
                    hole[j] = true;
                    plate[j, 0] = plate[j, 1] = false;
                    lastHole = j;
                }
            for (int j = 1; j < last; j++)
                if (bayUp[j] && bayUp[j - 1] && !plate[j, 0] && !plate[j, 1] && !plate[j - 1, 0] && !plate[j - 1, 1])
                {
                    int b = hole[j] ? j - 1 : j;
                    plate[b, r.Roll(b, Ruin.Centre, RolePlateBack) < 0.5f ? 0 : 1] = true;
                }
        }

        // Each bay: its two plates, its stringers and its handrails.
        for (int j = 0; j < last; j++)
        {
            if (!bayUp[j]) continue;
            Vector2 c = (pos[j] + pos[j + 1]) * 0.5f;
            for (int l = 0; l < 2; l++)
            {
                if (!plate[j, l]) continue;
                int lane = l == 0 ? Ruin.Left : Ruin.Right;
                AddPiece(pieces, StructurePieceKind.Deck, DeckPrefab, c + side * (lane * LaneOffset), deckH, along90, r.Health(j, lane, Ruin.RoleDeckHealth));
            }
            for (int k = 0; k < 2; k++)
            {
                int s = k == 0 ? Ruin.Left : Ruin.Right;
                Vector2 off = side * (s * DeckHalfWidth);
                if (!Buried(groundAt, pos[j] + off, deckH, pos[j + 1] + off, deckH))
                    AddPiece(pieces, StructurePieceKind.Beam, BeamPrefab, c + off, deckH, along90, r.Health(j, s, Ruin.RoleBeamHealth));
                if (rail[j, k])
                    AddPiece(pieces, StructurePieceKind.Beam, BeamPrefab, c + off, deckH + RailHeight, along90, r.Health(j, s, RoleRailHealth));
            }
        }

        // Both ends as the road meets them: the stairs laid (LayEndStairs), railed down every step, a newel at their foot;
        // none where the road meets the end level, the rails ending on the deck's end posts.
        EmitRailedEnd(pieces, groundAt, nearEnd, side, along90, r, 0);
        EmitRailedEnd(pieces, groundAt, farEnd, side, along90, r, last + 1);
        if (ruin) r.ApplyLocationDamage(pieces);   // vanilla's Location.m_applyRandomDamage, as every kit's ruin
        return pieces;
    }

    /// <summary>
    /// One end: its stairs as the road meets it (<see cref="LayEndStairs"/>), and each side of it, a stringer
    /// (<see cref="StairRailPrefab"/>) along every step's outer edge, a handrail <see cref="RailHeight"/> over it, and a
    /// railing post (<see cref="RailPostPrefab"/>) at every joint down the run, where one step's stringer and handrail meet
    /// the next's, every <see cref="StairRun"/> as the deck's every <see cref="DeckSpan"/>; the last is the newel at the foot
    /// under the last handrail. A step whose middle stands over the ground (<see cref="EmitSteps"/>'s test) is carried on a
    /// column of <see cref="PostPrefab"/> poles under each edge at its low joint, down to the ground as the deck's are. A
    /// stringer, handrail or post wholly in the ground is left out. All on <paramref name="ground"/>, the finished ground
    /// (<see cref="RailedGround"/>): a step on the road's landing needs no column.
    /// With no stairs laid the end is the deck's, its rails ending on the deck's end posts.
    /// </summary>
    private static void EmitRailedEnd(List<StructurePiece> pieces, System.Func<Vector2, float> ground, StructureEnds.End end, Vector2 side,
        float postYaw, Ruin r, int bayKey)
    {
        int steps = LayEndStairs(pieces, ground, end, side, r, bayKey, carry: false);
        Vector2 anchor = end.Edge, inward = end.Up;
        float deckH = end.Height;
        float railYaw = YawDegrees(inward) - 90f;   // its local x from its low snap (outward) to its high one
        for (int k = 0; k < steps; k++)
        {
            Vector2 c = anchor - inward * (StairHalfRun + k * StairRun);
            float foot = deckH - StairRise - k * StairRise;
            // A step over the ground (the stair run's test, either lane's middle) stands on a column under each edge at its
            // low joint, as the deck does on its stations.
            bool carried = false;
            foreach (int lane in new[] { Ruin.Left, Ruin.Right })
            {
                Vector2 p = c + side * (lane * LaneOffset);
                carried |= foot > ground(p) + StairFootTolerance;
            }
            foreach (int s in new[] { Ruin.Left, Ruin.Right })
            {
                Vector2 off = side * (s * DeckHalfWidth);
                Vector2 low = c - inward * StairHalfRun + off, high = c + inward * StairHalfRun + off;
                if (carried)
                    EmitColumn(pieces, low, ground(low), foot, postYaw,
                        r.Health(bayKey, s, Ruin.RolePostHealth, k + 1), toFoot: true);
                for (int tier = 0; tier < 2; tier++)
                {
                    float y = foot + tier * RailHeight;
                    if (Buried(ground, low, y, high, y + StairRise)) continue;
                    // Its origin over the middle of the step, at its low snap's height.
                    AddPiece(pieces, StructurePieceKind.Beam, StairRailPrefab, c + off, y, railYaw,
                        r.Health(bayKey, s, tier == 0 ? Ruin.RoleBeamHealth : RoleRailHealth, k + 1));
                }
            }
        }
        // The deck's railing post stands at the top joint; one at each joint below it, the newel at the foot.
        for (int k = 1; k <= steps; k++)
        {
            Vector2 at = anchor - inward * (k * StairRun);
            float bottom = deckH - k * StairRise;
            bool newel = k == steps;
            foreach (int s in new[] { Ruin.Left, Ruin.Right })
            {
                Vector2 p = at + side * (s * DeckHalfWidth);
                if (ground(p) >= bottom + RailHeight + BuriedPieces.Growth) continue;
                AddPiece(pieces, StructurePieceKind.Post, RailPostPrefab, p, bottom + RailHeight * 0.5f, postYaw,
                    newel ? r.Health(bayKey, s, RoleRailHealth, 0) : r.Health(bayKey, s, RoleRailPost, k));
            }
        }
    }

    /// <summary>Whether a beam from <paramref name="a"/> at <paramref name="ha"/> to <paramref name="b"/> at
    /// <paramref name="hb"/> lies wholly in the ground: the ground over its top (<see cref="RailHalfDepth"/> over its
    /// snap line) at both ends and every half metre between, on <paramref name="ground"/>.</summary>
    private static bool Buried(System.Func<Vector2, float> ground, Vector2 a, float ha, Vector2 b, float hb)
    {
        int n = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / 0.5f));
        for (int k = 0; k <= n; k++)
        {
            float t = k / (float)n;
            Vector2 p = Vector2.Lerp(a, b, t);
            if (ground(p) < Mathf.Lerp(ha, hb, t) + RailHalfDepth)
                return false;
        }
        return true;
    }

    private static void AddPiece(List<StructurePiece> pieces, StructurePieceKind kind, string prefab, Vector2 at, float height, float yaw, float health) =>
        pieces.Add(new StructurePiece
        {
            Kind = kind,
            Prefab = prefab,
            Position = new Vector3(at.x, height, at.y),
            YawDegrees = yaw,
            HealthFraction = health,
        });
}
