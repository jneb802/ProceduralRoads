using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Mistlands road bridges in the style of the game's own Dvergr viaduct
/// (Mistlands_Viaduct1). Pure logic; the game places the plan
/// through the bridges' structure source (<see cref="BridgeStructures"/>), so
/// the old-world cutoff, the spawned-zone record and the append queue are the
/// wooden bridges' own.
///
/// <para><b>The template is the vanilla viaduct.</b> Every offset below was read
/// off the 377 build pieces of one Mistlands_Viaduct1 (location centre
/// (-7804.310, 41.698, 4351.460), yaw 135), rotated onto the bridge's
/// own axes: u along the deck, v across it (to the left of the way the deck
/// runs, as <see cref="BridgeLayout"/>'s side), y up from the deck's walking
/// surface D, which is the top of the deck's black marble course. Its middle
/// pier, the one with every piece, is the pattern.</para>
///
/// <para><b>Piers</b>: 16 m centre to centre as in vanilla, or 14 m or 12 m where 16 m bays leave the deck out of the
/// game's support (<see cref="Spacing"/>, <see cref="BaySpacings"/>), never so close that two piers' tops overlap.
/// Over no water a ship sails, one at the crossing's midpoint and then every bay
/// outward while inside the gap; a short crossing still gets the middle one.
/// Over a river or deep water whose longship does not pass under the deck
/// (<see cref="BridgeLayout.NavigationChannel"/>, every kit's one test of where
/// boats pass), the navigation bay first: two vanilla
/// bays wide, piers at the navigation channel +-16 m with <see cref="NavigationGap"/>
/// of deck missing between them (<see cref="Ships.NavigationGapWidth"/>, in modules),
/// then every bay outward; the two bay piers stand even in or behind a bank,
/// the deck running on over them. Each pier is four shafts in a diamond, (u +-2, v 0) and (u 0, v +-1): a blackmarble_column_3 footing, then
/// blackmarble_2x2x2 stacks with a blackmarble_2x2_enforced band every 8 m,
/// then the corbel band of the top 8 m (out_1, outcorner, 2x2x1 and the
/// spreading 2x2x2s) under the deck, and an iron_wall_2x2 screen through the
/// pier at u +-1 from the footing to the deck, as vanilla has it.</para>
///
/// <para><b>The deck</b> is 2 m modules on the piers' grid: two iron_floor_2x2
/// abreast (4 m), a course of blackmarble_2x2x2 on them (three across, 6 m;
/// over a pier two, with a head either side), and blackmarble_arch along both
/// edges every 2 m. It runs bank to bank: the remainder past the outer piers
/// goes onto the banks. Its height and each end are the one owner's of how a
/// structure meets the road (<see cref="StructureEnds"/>): the deck at the
/// height the road arrives at, or for about half the crossings a step or two over it
/// (<see cref="RaisedSteps"/>, fixed per crossing and world seed), an end lower than it walked off on
/// blackmarble_stair steps (the wooden kit's run, <see cref="BridgeLayout.LayEndStairs"/>),
/// at most <see cref="MaxEndStairRise"/> of them (<see cref="Ends"/>), their foot on
/// the road, which comes in along the deck's line over its levelled apron
/// (StructureEnds.MeetEnds); an end the road arrives level with has none. The steps are railed on both sides in a stepped
/// black marble parapet (<see cref="StairRailing.LayParapet"/>, after a hand-built example), by the staircases' railing
/// rules; an end without steps has its parapet run on past the deck's end onto the bank, both sides, unless it gets in the
/// way (<see cref="BankParapet"/>, after another).</para>
///
/// <para><b>Price</b> (<see cref="Price"/>): dearer the
/// lower its piers, steeply cheaper over 10 m (<see cref="HeightFactor"/>),
/// and its end stairs at ten times an ordinary staircase's metre, so a deep
/// valley crossed high from level banks is the one bought.</para>
///
/// <para><b>Ruin</b> is the wooden kit's rule (<see cref="BridgeLayout.Ruin"/>,
/// <see cref="BridgeLayout.SpanSurvival"/>, <see cref="BridgeLayout.PierSurvival"/>):
/// fixed per crossing and world seed, survival falling toward mid-span, piers
/// outliving the deck. Each part takes its share of the one ruin profile
/// (<see cref="BridgeLayout.BridgeRuin"/>), calibrated on the vanilla viaduct: few floors, some of the course and about a
/// third of the arches gone, now and then a whole module open.
/// A pier's core (footing, shafts, bands, the corbel's 2x2x2s) never goes.
/// The deck keeps a way across a player jumps: no two modules running are both open.</para>
///
/// <para><b>Every piece is grounded or rests on one that is, by
/// construction</b>, and no piece is wholly under the ground (the game breaks
/// one there: <see cref="BuriedPieces"/>). <b>The game's support holds every piece</b> (<see cref="Solve"/>): the
/// layout is run through the game's own rule (<see cref="SupportGraph"/>, <see cref="ViaductSupport"/>, checked against
/// what was read in game on a viaduct whose deck fell) and every piece holds <see cref="HoldMargin"/> times its least. Where
/// the black marble course would not (over a navigation bay, which keeps a ship's width), the deck is walked on iron;
/// an ornament the rule would leave hanging is not laid; piers taller than <see cref="PierCeiling"/> are no viaduct.</para>
/// </summary>
public static class DvergrViaduct
{
    // ---------------------------------------------------------------- pieces
    // Every size below is MEASURED on the vanilla prefabs (each prefab's non-trigger colliders
    // in its own frame), origin at the centre of the bounds unless said:
    // column_3 2 x 8 x 2; 2x2x2, 2x2_enforced, outcorner, head_big01/02 2 x 2 x 2; 2x2x1, out_1, arch 2 x 2 x 1 (1 m along
    // local z); iron_floor_2x2 2 x 0.1 x 2; iron_wall_2x2 2 x 2 x 0.1 (0.1 m along local z); blackmarble_stair 2 x 1 x 2 with
    // its origin at its FOOT (y 0..1), rising toward local -z, as wood_stair. The layout itself was read piece for piece
    // against the vanilla Mistlands_Viaduct1 middle module and Mistlands_Viaduct2 (pier pattern, deck, arches, heads, screens:
    // all match; the deck course's 90 degree roll in vanilla is not copied, as blackmarble_2x2x2's mesh is turned at random on
    // every axis by its RandomPieceRotation).
    public const string Footing = "blackmarble_column_3";
    public const string Band = "blackmarble_2x2_enforced";
    public const string Block = "blackmarble_2x2x2";
    public const string Filler = "blackmarble_2x2x1";
    public const string Corbel = "blackmarble_out_1";
    public const string CorbelCorner = "blackmarble_outcorner";
    public const string Floor = "iron_floor_2x2";
    public const string Screen = "iron_wall_2x2";
    public const string Arch = "blackmarble_arch";
    public const string HeadLeft = "blackmarble_head_big01";
    public const string HeadRight = "blackmarble_head_big02";
    public const string StepPrefab = "blackmarble_stair";

    // ------------------------------------------------------------- the rules

    /// <summary>The floor: no viaduct whose tallest pier is under it. A valley then has its road cross lower or take its stairs, and
    /// a river its wooden bridge: <see cref="Refusal"/> is the one owner of the kit, and this carries its "under 6 m
    /// stays wood" (<see cref="MinClearance"/>) up to the floor. Its default.</summary>
    public const float DefaultMinViaductPier = 12f;
    /// <summary>As set (metres). Settable for tests.</summary>
    public static float MinViaductPier = DefaultMinViaductPier;

    /// <summary>Pier centre to pier centre, as the vanilla viaduct.</summary>
    public const float PierSpacing = 16f;
    /// <summary>One deck module: an iron floor's length.</summary>
    public const float Module = 2f;
    /// <summary>A crossing whose deck stands less than this over the ground or the water keeps
    /// the wooden kit.</summary>
    public const float MinClearance = 6f;
    /// <summary>
    /// The tallest pier, deck surface to the ground under its lowest shaft, a viaduct is built
    /// on; taller, the crossing keeps the wooden kit (on a river) or the road keeps its stairs
    /// (over a dry valley). By the game's support rule (<see cref="SupportGraph"/>, checked against readings in game):
    /// a pier's iron screen carries it (<see cref="ScreenTop"/>: 1500 at its foot, 0.84 a 2 m screen), and the black
    /// marble on the deck and the pier's corbels keep a share of that; a pier's own pieces (its core, its screens, the
    /// course over it) hold <see cref="HoldMargin"/> times their least up to 22 m of pier, on bays of any length, the
    /// middle of a bay its piers do not carry walked on iron (<see cref="Solve"/>). (It was 26 m, read off a 21 m pier's 208 at the deck over the pier: the deck between piers, the arches and
    /// the corbels' tips are weaker, and 22 m piers on 16 m bays dropped their deck.)
    /// </summary>
    public const float PierCeiling = 22f;
    /// <summary>The navigation bay over a river: two vanilla bays (32 m) centred on its navigation channel, the pier on that
    /// line left out (a boat found a 16 m bay too tight, so the gap over a river is
    /// wider if possible).</summary>
    public const float NavigationBay = 2f * PierSpacing;
    /// <summary>Least deck kept out from each bay pier's centre: past its shafts, onto its corbels.</summary>
    public const float NavigationStub = 3f;

    /// <summary>
    /// The deck missing over a river, centred on its navigation channel: at least the ship's way
    /// (<paramref name="shipWay"/>, <see cref="Ships.NavigationGapWidth"/>, the one owner of how wide a boat's way is), in
    /// whole modules, an odd number of them centred on the river (4 k + 2 m), and never closer than
    /// <see cref="NavigationStub"/> to a bay pier. Under the yard (9.8 m) that is 10 m open; at the yard's height (13.2 m)
    /// 14 m. The corbels over the river are kept for now.
    /// </summary>
    public static float NavigationGap(float shipWay)
    {
        float want = shipWay;
        int k = Mathf.CeilToInt((want - Module) / (2f * Module) - 1e-4f);
        int most = Mathf.FloorToInt((NavigationBay - 2f * NavigationStub - Module) / (2f * Module) + 1e-4f);
        return (2 * Mathf.Clamp(k, 0, most) + 1) * Module;
    }
    /// <summary>A pier stands only this far or more inside each bank (half its corbelled length
    /// along the deck is 6 m; its shafts reach 3 m).</summary>
    public const float PierBankMargin = 3f;
    /// <summary>A pier shorter than this under the deck is the bank itself: its band is left out.</summary>
    public const float MinPier = 2f;

    // Ruin, like vanilla's damage to its viaducts: more deck and railing pieces missing and the iron showing through in
    // some spots are fine, even if the player has to jump over a gap. The shares are every kit's (BridgeLayout.BridgeRuin),
    // read off the vanilla Mistlands_Viaduct1. Where this kit departs from them it says why: the pier's core never falls
    // (vanilla's viaduct piers all stand), only its ornaments, at the columns' survival; and the arches along the deck's
    // edges, though they are its railing, are lost at the support share with the heads beside them, as vanilla's viaduct
    // loses its arches (46 %) more than its course (30 %).
    private static BridgeLayout.RuinProfile Shares => BridgeLayout.BridgeRuin;

    // ------------------------------------------------------------- vines
    /// <summary>Vanilla's hanging vine: networked, WearNTear 20 / Wood, no support of its own to give; its box 1 x 1 x 0.3
    /// about its origin, its leaves 2.3 x 2.4 m on its local -z side. Hung with its origin on
    /// a stone face, local +z into the stone, as vanilla's are, so its box reaches into the stone it hangs from.</summary>
    public const string Vine = "vines";
    /// <summary>Each of a pier's eight vine curtains is there at the vanilla viaduct's 40 % (Mistlands_Viaduct1 GameObject/pillar/vines .. vines (7), RandomSpawn 40 each; Mistlands_Viaduct2's the same).</summary>
    public const float VineCurtainChance = 0.4f;
    /// <summary>A vine is hung only where its origin stands this far over the ground: its leaves reach 1.17 m under it
    /// (the vines' meshes span y -1.17..1.26), so none is in the ground.</summary>
    public const float VineGroundClear = 1.2f;
    /// <summary>A vine is hung only within this of a stone piece the viaduct has (its origin inside the piece's bounds grown
    /// by this): never in the air where a ruined corbel was. A vine with no piece under its box would wear away (vines'
    /// WearNTear m_noSupportWear).</summary>
    public const float VineTouch = 0.2f;

    /// <summary>
    /// The vine vanilla's viaduct hangs on a corbel's sloped face (<see cref="PierVines"/>' pitched vines and the corbels'
    /// own of curtains 4 to 7: on the blackmarble_out_1 at (5, -0.5, -3) turned 270, the vine at (5.55, -0.35, -2.96)
    /// turned 180, pitched 26 degrees): its origin 0.55 m out from the corbel's middle along its overhang (the corbel's
    /// local +x) and 0.04 m up, on the slope; pitched to it, hanging (roll 180), turned the corbel's yaw less 90 so its
    /// local +z goes into the stone. The one place a vine is hung on any corbel the viaduct's pattern does not list (a
    /// staircase's cornice columns, <see cref="StairSupport"/>).
    /// </summary>
    internal static StructurePiece CorbelVine(StructurePiece corbel)
    {
        Vector2 outward = StairLayout.Right(corbel.YawDegrees);
        return new StructurePiece
        {
            Kind = StructurePieceKind.Flair, Prefab = Vine,
            Position = corbel.Position + new Vector3(outward.x * CorbelVineOut, CorbelVineUp, outward.y * CorbelVineOut),
            PitchDegrees = CorbelVinePitch, YawDegrees = Wrap(corbel.YawDegrees - 90f), RollDegrees = 180f,
        };
    }

    /// <summary>Vanilla's corbel vine (<see cref="CorbelVine"/>): out from the corbel's middle, up, and pitched; the one owner
    /// of those offsets, which the pier pattern's matching slope vines are written from (<see cref="PierVines"/>).</summary>
    private const float CorbelVineOut = 0.55f, CorbelVineUp = 0.04f, CorbelVinePitch = -26f;

    /// <summary>One vine of the pier pattern: its curtain (0-7), offsets from the pier's centre on the deck line (du along, v
    /// across), its height under the deck surface D, and its turn (pitch, yaw from the deck's heading, roll).</summary>
    private readonly struct VinePart
    {
        public readonly int Curtain;
        public readonly float Du, V, Dy, Pitch, Turn, Roll;
        public VinePart(int curtain, float du, float v, float dy, float pitch, float turn, float roll)
        {
            Curtain = curtain; Du = du; V = v; Dy = dy; Pitch = pitch; Turn = turn; Roll = roll;
        }
    }

    /// <summary>
    /// The vines of a pier: vanilla's middle pier, vine for vine (Mistlands_Viaduct1
    /// GameObject/GameObject/pillar/vines*/vines*, the pier centred at x 0, z 0.25, the deck surface at y 17; turned onto the
    /// bridge's axes as the pier pattern is: du = z - 0.25, v = -x, dy = y - 17; two vines vanilla has twice taken once).
    /// Eight curtains, one on each outer face of the four shafts, hanging (roll 180) every 2 m from 21 m under the deck up
    /// onto the corbels, every fifth turned sideways (roll 90), the lowest upright (roll 0), and on the corbels' slopes
    /// pitched 26 degrees. 84 vines. The slope vines on the corbels at 3, 4 and 5 m out (curtains 2 and 3) stand where
    /// <see cref="CorbelVine"/> hangs one on any corbel, and are written from its offsets (the same floats); vanilla's other
    /// slope vines stand off that rule and keep their own.
    /// </summary>
    private static readonly VinePart[] PierVines =
    {
        new(0, -2.1f, -2.55f, -2.96f, -26f, -90f, 180f),
        new(0, -0.97f, -2f, -4.75f, 0f, -90f, 180f),
        new(0, 0.03f, -2f, -19f, 0f, -90f, 90f),
        new(0, 0.03f, -2f, -17f, 0f, -90f, 180f),
        new(0, 0.03f, -2f, -14.75f, 0f, -90f, 180f),
        new(0, 0.03f, -2f, -12.75f, 0f, -90f, 180f),
        new(0, 0.03f, -2f, -10.75f, 0f, -90f, 180f),
        new(0, 0.03f, -2f, -9f, 0f, -90f, 90f),
        new(0, 0.03f, -2f, -7f, 0f, -90f, 180f),
        new(0, 0.28f, -2f, -21f, 0f, -90f, 0f),
        new(0, 1.03f, -2f, -4.75f, 0f, -90f, 180f),
        new(0, 1.9f, -2.55f, -2.96f, -26f, -90f, 180f),
        new(1, -1.9f, 2.55f, -2.96f, -26f, 90f, 180f),
        new(1, -1.03f, 2f, -5f, 0f, 90f, 180f),
        new(1, -0.28f, 2f, -21f, 0f, 90f, 0f),
        new(1, -0.03f, 2f, -19f, 0f, 90f, 90f),
        new(1, -0.03f, 2f, -17f, 0f, 90f, 180f),
        new(1, -0.03f, 2f, -14.75f, 0f, 90f, 180f),
        new(1, -0.03f, 2f, -12.75f, 0f, 90f, 180f),
        new(1, -0.03f, 2f, -10.75f, 0f, 90f, 180f),
        new(1, -0.03f, 2f, -9f, 0f, 90f, 90f),
        new(1, -0.03f, 2f, -7f, 0f, 90f, 180f),
        new(1, 1.22f, 2f, -5f, 0f, 90f, 180f),
        new(1, 2.1f, 2.55f, -2.96f, -26f, 90f, 180f),
        new(2, -5.55f, 0.1f, -3.21f, -26f, 0f, 180f),
        new(2, -4f - CorbelVineOut, 0.1f, -5f + CorbelVineUp, CorbelVinePitch, 0f, 180f),
        new(2, -3f - CorbelVineOut, 0.1f, -7f + CorbelVineUp, CorbelVinePitch, 0f, 180f),
        new(2, -3f, -0.28f, -21f, 0f, 0f, 0f),
        new(2, -3f, -0.03f, -19f, 0f, 0f, 90f),
        new(2, -3f, -0.03f, -17f, 0f, 0f, 180f),
        new(2, -3f, -0.03f, -14.75f, 0f, 0f, 180f),
        new(2, -3f, -0.03f, -12.75f, 0f, 0f, 180f),
        new(2, -3f, -0.03f, -10.75f, 0f, 0f, 180f),
        new(2, -3f, -0.03f, -9f, 0f, 0f, 90f),
        new(3, 3f, -0.22f, -19f, 0f, 180f, 90f),
        new(3, 3f, -0.22f, -17f, 0f, 180f, 180f),
        new(3, 3f, -0.22f, -14.75f, 0f, 180f, 180f),
        new(3, 3f, -0.22f, -12.75f, 0f, 180f, 180f),
        new(3, 3f, -0.22f, -10.75f, 0f, 180f, 180f),
        new(3, 3f, -0.22f, -9f, 0f, 180f, 90f),
        new(3, 3f, 0.03f, -21f, 0f, 180f, 0f),
        new(3, 3f + CorbelVineOut, -0.35f, -7f + CorbelVineUp, CorbelVinePitch, 180f, 180f),
        new(3, 4f + CorbelVineOut, -0.35f, -5f + CorbelVineUp, CorbelVinePitch, 180f, 180f),
        new(3, 5f + CorbelVineOut, -0.35f, -3f + CorbelVineUp, CorbelVinePitch, 180f, 180f),
        new(4, 1.78f, -1f, -19f, 0f, -90f, 90f),
        new(4, 1.78f, -1f, -17f, 0f, -90f, 180f),
        new(4, 1.78f, -1f, -14.75f, 0f, -90f, 180f),
        new(4, 1.78f, -1f, -12.75f, 0f, -90f, 180f),
        new(4, 1.78f, -1f, -10.75f, 0f, -90f, 180f),
        new(4, 1.78f, -1f, -9f, 0f, -90f, 90f),
        new(4, 2.03f, -1f, -21f, 0f, -90f, 0f),
        new(4, 2.03f, -1f, -7f, 0f, -90f, 180f),
        new(4, 3.28f, -1f, -5f, 0f, -90f, 180f),
        new(4, 4.28f, -1f, -3f, 0f, -90f, 180f),
        new(5, -4.22f, -1f, -3f, 0f, -90f, 180f),
        new(5, -2.97f, -1f, -5f, 0f, -90f, 180f),
        new(5, -1.97f, -1f, -19f, 0f, -90f, 90f),
        new(5, -1.97f, -1f, -17f, 0f, -90f, 180f),
        new(5, -1.97f, -1f, -14.75f, 0f, -90f, 180f),
        new(5, -1.97f, -1f, -12.75f, 0f, -90f, 180f),
        new(5, -1.97f, -1f, -10.75f, 0f, -90f, 180f),
        new(5, -1.97f, -1f, -9f, 0f, -90f, 90f),
        new(5, -1.97f, -1f, -7f, 0f, -90f, 180f),
        new(5, -1.72f, -1f, -21f, 0f, -90f, 0f),
        new(6, 1.72f, 1f, -21f, 0f, 90f, 0f),
        new(6, 1.97f, 1f, -19f, 0f, 90f, 90f),
        new(6, 1.97f, 1f, -17f, 0f, 90f, 180f),
        new(6, 1.97f, 1f, -14.75f, 0f, 90f, 180f),
        new(6, 1.97f, 1f, -12.75f, 0f, 90f, 180f),
        new(6, 1.97f, 1f, -10.75f, 0f, 90f, 180f),
        new(6, 1.97f, 1f, -9f, 0f, 90f, 90f),
        new(6, 2.22f, 1f, -7f, 0f, 90f, 180f),
        new(6, 3.22f, 1f, -5f, 0f, 90f, 180f),
        new(6, 3.97f, 1f, -3f, 0f, 90f, 180f),
        new(7, -4.03f, 1f, -3f, 0f, 90f, 180f),
        new(7, -3.28f, 1f, -5f, 0f, 90f, 180f),
        new(7, -2.28f, 1f, -7f, 0f, 90f, 180f),
        new(7, -2.03f, 1f, -21f, 0f, 90f, 0f),
        new(7, -1.78f, 1f, -19f, 0f, 90f, 90f),
        new(7, -1.78f, 1f, -17f, 0f, 90f, 180f),
        new(7, -1.78f, 1f, -14.75f, 0f, 90f, 180f),
        new(7, -1.78f, 1f, -12.75f, 0f, 90f, 180f),
        new(7, -1.78f, 1f, -10.75f, 0f, 90f, 180f),
        new(7, -1.78f, 1f, -9f, 0f, 90f, 90f),
    };

    /// <summary>Heights relative to the deck surface D (the course's top).</summary>
    public const float FloorDrop = 2f, CourseDrop = 1f, ArchRise = 1f;
    /// <summary>The viaduct's highest point over the deck surface: the parapet arches' tops.</summary>
    public static float TopOverDeck => ArchRise + Extents(Arch).y;
    /// <summary>The corbel band's lowest level; the shafts stand below it.</summary>
    private const float ShaftTop = -8f;

    // Ruin roles, apart from the wooden kit's own (1-10) so a key never means two things; the raise has 36 (RoleRaise).
    private const int RoleFloor = 21, RoleCourse = 22, RoleArch = 23, RoleOrnament = 24, RoleScreen = 25, RoleHead = 26, RoleHole = 27,
        RoleVine = 28;

    /// <summary>One piece of the pier pattern: offsets from the pier's centre on the deck line
    /// (du along, v across), its centre's height under D, its turn from the deck's heading, and
    /// whether it is the pier's core (never ruined).</summary>
    private readonly struct Part
    {
        public readonly string Prefab;
        public readonly float Du, V, Dy, Turn;
        public readonly bool Core;
        public Part(string prefab, float du, float v, float dy, float turn, bool core = false)
        {
            Prefab = prefab; Du = du; V = v; Dy = dy; Turn = turn; Core = core;
        }
    }

    /// <summary>Where a pier's two iron screens stand along the deck from its centre, on its line.</summary>
    private static readonly float[] ScreenAlongs = { -1f, 1f };

    /// <summary>The four shafts of a pier, (du, v), and the turn of each one's footing (vanilla's).</summary>
    private static readonly (float du, float v, float footingTurn)[] Shafts =
    {
        (-2f, 0f, 180f), (2f, 0f, 180f), (0f, -1f, 90f), (0f, 1f, 270f),
    };

    /// <summary>The corbel band, the top 8 m of the pier under the deck: vanilla's middle pier, piece for piece.</summary>
    private static readonly Part[] CorbelBand =
    {
        // 9-7 m under the deck: two blocks between the shafts' heads, corbels stepping out.
        new(Block, -1f, 0f, -7f, 90f, core: true), new(Block, 1f, 0f, -7f, 90f, core: true),
        new(Corbel, -3f, -0.5f, -7f, 90f), new(Corbel, -3f, 0.5f, -7f, 90f),
        new(Corbel, -1f, -1.5f, -7f, 90f), new(Corbel, -1f, 1.5f, -7f, 90f),
        new(Corbel, 1f, -1.5f, -7f, 270f), new(Corbel, 1f, 1.5f, -7f, 270f),
        new(Corbel, 3f, -0.5f, -7f, 270f), new(Corbel, 3f, 0.5f, -7f, 270f),
        // 7-5 m: three blocks, fillers either side of the middle one, corbels a metre further out.
        new(Block, -2f, 0f, -5f, 90f, core: true), new(Block, 0f, 0f, -5f, 90f, core: true), new(Block, 2f, 0f, -5f, 90f, core: true),
        new(Filler, 0f, -1.5f, -5f, 90f), new(Filler, 0f, 1.5f, -5f, 90f),
        new(Corbel, -4f, -0.5f, -5f, 90f), new(Corbel, -4f, 0.5f, -5f, 90f),
        new(Corbel, -2f, -1.5f, -5f, 90f), new(Corbel, -2f, 1.5f, -5f, 90f),
        new(Corbel, 2f, -1.5f, -5f, 270f), new(Corbel, 2f, 1.5f, -5f, 270f),
        new(Corbel, 4f, -0.5f, -5f, 270f), new(Corbel, 4f, 0.5f, -5f, 270f),
        // 4-2 m: four blocks under the floors, fillers, corner pieces, the outermost corbels.
        new(Block, -3f, 0f, -3f, 90f, core: true), new(Block, -1f, 0f, -3f, 90f, core: true),
        new(Block, 1f, 0f, -3f, 90f, core: true), new(Block, 3f, 0f, -3f, 90f, core: true),
        new(Filler, 0f, -1.5f, -3f, 90f), new(Filler, 0f, 1.5f, -3f, 90f),
        new(Corbel, -5f, -0.5f, -3f, 90f), new(Corbel, -5f, 0.5f, -3f, 90f),
        new(Corbel, 5f, -0.5f, -3f, 270f), new(Corbel, 5f, 0.5f, -3f, 270f),
        new(CorbelCorner, -3f, -2f, -3f, 90f), new(CorbelCorner, -3f, 2f, -3f, 180f),
        new(CorbelCorner, -1f, -2f, -3f, 0f), new(CorbelCorner, -1f, 2f, -3f, 270f),
        new(CorbelCorner, 1f, 2f, -3f, 180f), new(CorbelCorner, 1f, -2f, -3f, 90f),
        new(CorbelCorner, 3f, -2f, -3f, 0f), new(CorbelCorner, 3f, 2f, -3f, 270f),
    };

    /// <summary>Half-extents of a piece's bounds along the deck, across it and up, at the turns this layout gives it: layout
    /// geometry only (where a piece stands, what it overlaps, a vine's face, a road under a parapet), read off the prefab's
    /// measured bounds (<see cref="BuriedPieces.DataOf"/>, the one owner); the support and burial rules read those bounds
    /// themselves, at each piece's own turn. Every piece is laid with its local x along the deck (turned 90 or 270) but a
    /// floor (square) and a screen, turned 0 or 180, its 0.1 m across its local z along the deck. An unknown piece is a
    /// metre cube.</summary>
    public static (float u, float v, float y) Extents(string prefab)
    {
        if (!BuriedPieces.BoundsOf(prefab, out Vector3 min, out Vector3 max)) return (0.5f, 0.5f, 0.5f);
        float x = (max.x - min.x) / 2f, y = (max.y - min.y) / 2f, z = (max.z - min.z) / 2f;
        return prefab == Screen ? (z, x, y) : (x, z, y);
    }

    // --------------------------------------------------------------- choice

    /// <summary>
    /// Whether a crossing is built as a Dvergr viaduct rather than the wooden kit: a bridge
    /// (never a ford) in the Mistlands whose deck stands <see cref="MinClearance"/> or more over
    /// the ground or the water, its tallest pier at least <see cref="MinViaductPier"/> and no taller than
    /// <see cref="PierCeiling"/>, with no more
    /// than <see cref="MaxEndStairRise"/> of stairs at either end (<see cref="Ends"/>). The
    /// one owner of that choice: the plan, the search's valley jumps and the dev command all ask it.
    /// </summary>
    public static bool Applies(RoadCrossing crossing, WorldGenerator world) => Refusal(crossing, world) == null;

    /// <summary>Whether a crossing stands under the viaduct's floor: its deck under <see cref="MinClearance"/> over the
    /// ground or water, or its tallest pier under <see cref="MinViaductPier"/>, the same two tests <see cref="Refusal"/>
    /// makes, in its order, at grade as it makes them. Where a low Mistlands bridge gets the Dvergr pier's wood
    /// (BridgeStructures.KitFor).</summary>
    public static bool UnderTheFloor(RoadCrossing crossing, WorldGenerator world)
    {
        float deck = AtGrade(crossing, world);
        return deck - Mathf.Max(crossing.RiverbedHeight, crossing.WaterLevel) < MinClearance || TallestPier(crossing, world, deck) < MinViaductPier - 1e-3f;
    }

    /// <summary>Why <see cref="Applies"/> says no, or null. Asked of the deck at grade (<see cref="AtGrade"/>): the
    /// raise (<see cref="RaisedSteps"/>) is decided after it and keeps within it, so it never changes the answer.</summary>
    public static string? Refusal(RoadCrossing crossing, WorldGenerator world) => Refusal(crossing, world, PierCeiling, null);

    /// <summary><see cref="Refusal(RoadCrossing, WorldGenerator)"/> under <paramref name="ceiling"/>, its deck at grade read
    /// once (<see cref="GradeDeck"/>): <paramref name="grade"/> where the caller has read it already (a valley probe,
    /// <see cref="ValleyCrossing(Vector2, Vector2, WorldGenerator, out string, out GradeDeck?)"/>), else read here.</summary>
    private static string? Refusal(RoadCrossing crossing, WorldGenerator world, float ceiling, GradeDeck? grade)
    {
        if (crossing == null || world == null) return "no crossing";
        if (crossing.Kind != CrossingKind.Bridge) return "a ford";
        if (!BridgeStructures.InMistlands(crossing, world)) return "not in the Mistlands";
        grade ??= new GradeDeck(crossing, world);
        return DeckRefusal(crossing, world, ceiling, grade.Height, mounds: true, grade);
    }

    /// <summary>The tests of <see cref="Refusal"/> that hang on the deck's height, with the deck at <paramref name="deck"/>:
    /// the clearance, the pier ceiling and floor, and each end's stairs. A pier over the ceiling passes where
    /// <paramref name="mounds"/> lets a mound under it make up the difference (<see cref="Mounds(RoadCrossing, WorldGenerator, float)"/>):
    /// at grade, never for a raise (<see cref="RaisedSteps"/>), so a look's step drops before a mound is made.
    /// <paramref name="grade"/>: the deck at grade <paramref name="deck"/> is (<see cref="GradeDeck.Height"/>), its piers,
    /// tallest pier and ends read once there; null for any other deck, read here.</summary>
    private static string? DeckRefusal(RoadCrossing crossing, WorldGenerator world, float ceiling, float deck, bool mounds, GradeDeck? grade = null)
    {
        float clearance = deck - Mathf.Max(crossing.RiverbedHeight, crossing.WaterLevel);
        if (clearance < MinClearance) return FormattableString.Invariant($"deck {clearance:F1} m over the ground or water, under {MinClearance:F0} m");
        float tallest = grade != null ? grade.Tallest : TallestPier(crossing, world, deck);
        if (tallest > ceiling && !(mounds && Mounds(crossing, world, deck, grade?.Piers) != null))
            return FormattableString.Invariant($"a pier {tallest:F1} m tall, over the {ceiling:F0} m ceiling{(mounds ? " by more than a mound makes up" : "")}");
        if (tallest < MinViaductPier - 1e-3f) return FormattableString.Invariant($"tallest pier {tallest:F1} m, under the {MinViaductPier:F0} m floor");
        var (near, far) = grade != null ? grade.Ends : NaturalEnds(crossing, world, deck);
        foreach (StructureEnds.End end in new[] { near, far })
            if (EndStairsPastMost(end.Rise))
                return FormattableString.Invariant($"end stairs {end.Rise:F0} m, over the {MaxEndStairRise:F0} m most");
        return null;
    }

    /// <summary>The deck's walking surface as built: where the crossing's ends are recorded (<see cref="Recorded"/>), the
    /// height they were recorded at; else at grade (<see cref="AtGrade"/>), raised by <see cref="RaisedSteps"/> stair rises
    /// where the crossing's draw raises it.</summary>
    public static float DeckHeight(RoadCrossing crossing, WorldGenerator world)
    {
        if (Recorded(crossing) is { } ends) return ends.near.Height;
        float atGrade = AtGrade(crossing, world);
        return atGrade + RaisedSteps(crossing, world, atGrade) * StairLayout.PieceRise;
    }

    /// <summary>
    /// The crossing's ends as the road met them and they were saved with it (StructureEnds.MeetEnds), both a structure's;
    /// null before they are met. The deck is built to them: at their height (<see cref="DeckHeight"/>) and between their edges
    /// (<see cref="EdgesOf(RoadCrossing, WorldGenerator)"/>), as the layout that recorded them laid it out, so its end
    /// stairs, laid down from them, meet it. A network saved by a layout that stood every viaduct at grade and stopped its
    /// deck within a metre of each bank keeps that deck; one this layout recorded holds its raise
    /// (<see cref="RaisedSteps"/>) and its reach past a bay pier (<see cref="BayDeckReach"/>).
    /// </summary>
    private static (StructureEnds.End near, StructureEnds.End far)? Recorded(RoadCrossing crossing) =>
        crossing.FromEnd is StructureEnds.End near && crossing.ToEnd is StructureEnds.End far && near.IsStructure && far.IsStructure
            ? (near, far) : null;

    /// <summary>The deck at grade: the higher of the two heights the road arrives at its ends
    /// (<see cref="StructureEnds.Arrival"/> over the apron outward of each deck end), lifted clear of any water, not the
    /// bank points' own ground (a bank point can be 2-6 m off the road that comes to it). The road runs onto it at the
    /// higher bank. What the search prices and <see cref="Refusal"/> judges.</summary>
    internal static float AtGrade(RoadCrossing crossing, WorldGenerator world) => AtGrade(crossing, world, EdgesOf(crossing, world));

    /// <summary>
    /// A crossing's deck at grade, read once: the ship's way under it (<see cref="ShipWayUnder"/>), its end edges
    /// (<see cref="EdgesOf(RoadCrossing, WorldGenerator)"/>), its height (<see cref="AtGrade(RoadCrossing, WorldGenerator)"/>),
    /// its piers (<see cref="PierAlongs(RoadCrossing, WorldGenerator)"/>) and, at that height, its tallest pier
    /// (<see cref="TallestPier(RoadCrossing, WorldGenerator, float)"/>) and its ends as the banks take them
    /// (<see cref="NaturalEnds(RoadCrossing, WorldGenerator, float)"/>): each those functions' own answer, each read the
    /// first time it is asked. What one valley probe reads (<see cref="ValleyCrossing(Vector2, Vector2, WorldGenerator, out string, out GradeDeck?)"/>)
    /// and hands to <see cref="Refusal(RoadCrossing, WorldGenerator)"/> and <see cref="Price(GradeDeck, float, float, float)"/>,
    /// which read every one of them again each before. Held by its caller alone, for the one crossing as it is: none of it reads
    /// the riverbed, which a valley probe sets once its line is sampled.
    /// </summary>
    internal sealed class GradeDeck
    {
        internal readonly RoadCrossing Crossing;
        private readonly WorldGenerator m_world;
        internal readonly BridgeLayout.ShipWay? Way;
        internal readonly (Vector2 near, Vector2 far) Edges;
        internal readonly float Height;
        private List<float>? m_piers;
        private float? m_tallest;
        private (StructureEnds.End near, StructureEnds.End far)? m_ends;

        internal GradeDeck(RoadCrossing crossing, WorldGenerator world)
        {
            Crossing = crossing;
            m_world = world;
            Way = ShipWayUnder(crossing, world);
            Edges = RecordedEdges(crossing) ?? EdgesOf(crossing, world, Way);
            Height = AtGrade(crossing, world, Edges);
        }

        internal List<float> Piers => m_piers ??= PierAlongs(Crossing.Width, Way, Spacing(Crossing, m_world, ReachDeck(Crossing, m_world, Height), Way));
        internal float Tallest => m_tallest ??= TallestPier(Crossing, m_world, Height, Piers);
        internal (StructureEnds.End near, StructureEnds.End far) Ends => m_ends ??= NaturalEnds(Crossing, m_world, Height, Edges);
    }

    /// <summary><see cref="AtGrade(RoadCrossing, WorldGenerator)"/> for a deck whose end edges are <paramref name="edges"/>.</summary>
    private static float AtGrade(RoadCrossing crossing, WorldGenerator world, (Vector2 near, Vector2 far) edges)
    {
        var (near, far) = edges;
        Vector2 dir = crossing.Direction;
        float G(Vector2 p) => Ground(p, world);
        float deck = Mathf.Max(StructureEnds.Arrival(near, -dir, G), StructureEnds.Arrival(far, dir, G));
        return Mathf.Max(deck, BridgeLayout.MinDeck(crossing.WaterLevel));
    }

    /// <summary>The middles of the deck's two end edges, near then far: where its walking surface stops. Those its ends
    /// were recorded at (<see cref="Recorded"/>), else its module grid's.</summary>
    internal static (Vector2 near, Vector2 far) EdgesOf(RoadCrossing crossing, WorldGenerator world) =>
        RecordedEdges(crossing) ?? EdgesOf(crossing, world, ShipWayUnder(crossing, world));

    /// <summary>The edges the crossing's ends were recorded at (<see cref="Recorded"/>), else null.</summary>
    private static (Vector2 near, Vector2 far)? RecordedEdges(RoadCrossing crossing) =>
        Recorded(crossing) is { } ends ? (ends.near.Edge, ends.far.Edge) : null;

    /// <summary>The deck's end edges on the module grid hung on <paramref name="way"/> (null: the grid off a river).</summary>
    private static (Vector2 near, Vector2 far) EdgesOf(RoadCrossing crossing, WorldGenerator world, BridgeLayout.ShipWay? way)
    {
        var (_, _, first, last) = DeckGrid(crossing.Width, way);
        return (crossing.FromBank + crossing.Direction * (first - Module * 0.5f), crossing.FromBank + crossing.Direction * (last + Module * 0.5f));
    }

    /// <summary>How far the deck as built stands over the lowest ground between the banks, or over the
    /// water where there is any (the rule's "above the ground or water").</summary>
    public static float Clearance(RoadCrossing crossing, WorldGenerator world) =>
        DeckHeight(crossing, world) - Mathf.Max(crossing.RiverbedHeight, crossing.WaterLevel);

    /// <summary>Whether the viaduct leaves a navigation bay: over a river whose ship does not pass under its deck
    /// (<see cref="BridgeLayout.NavigationChannel"/>, every kit's one test, not a second one).</summary>
    public static bool HasNavigationBay(RoadCrossing crossing, WorldGenerator world) => ShipWayUnder(crossing, world)?.DeckWidth > 0f;

    // ---------------------------------------------------------------- piers

    /// <summary>
    /// Where the piers stand, metres along the crossing from its near bank, nearest first.
    /// <paramref name="way"/>: the ship's way under the viaduct over a river, else null; <paramref name="spacing"/> the
    /// bays' length, one of <see cref="BaySpacings"/> (<see cref="Spacing"/>).
    /// Off a river, the midpoint and then every <paramref name="spacing"/> outward while inside the
    /// gap, at least the midpoint. On a river, the navigation bay's two piers at the way +- 16 m
    /// (<see cref="NavigationBay"/>) first, then every <paramref name="spacing"/> outward. Where the deck has its gap over the river
    /// (<see cref="HasNavigationBay"/>) both bay piers always stand, either side of it, even in or behind a bank wall: the
    /// deck runs on over each (<see cref="BayDeckReach"/>) and the pier is cut to the ground under it, one shorter than
    /// <see cref="MinPier"/> being the bank itself, as any pier (a check in game found the deck over a gap running on to the far bank
    /// with no pier at the gap's edge). Under a deck the ship passes under (no deck
    /// gap) a bay pier outside the gap is left out, as every other: a pier's corbels reach 6 m along the deck at the
    /// yard's height, so +- 8 m would put them in the hull's way; at +- 16 m they stand 10 m out, clear of it and of the yard.
    /// </summary>
    public static List<float> PierAlongs(float width, BridgeLayout.ShipWay? way, float spacing = PierSpacing)
    {
        var piers = new List<float>();
        bool Inside(float a) => a >= PierBankMargin - 1e-3f && a <= width - PierBankMargin + 1e-3f;
        if (way is BridgeLayout.ShipWay w)
        {
            var (nearBay, farBay) = BayPiers(w);
            bool gap = w.DeckWidth > 0f;
            foreach (float bay in new[] { nearBay, farBay })
                if (gap || Inside(bay)) piers.Add(bay);
            for (float a = nearBay - spacing; a >= -1e-3f; a -= spacing)
                if (Inside(a)) piers.Add(a);
            for (float a = farBay + spacing; a <= width + 1e-3f; a += spacing)
                if (Inside(a)) piers.Add(a);
        }
        else
        {
            float mid = width * 0.5f;
            piers.Add(mid);
            for (float a = mid - spacing; Inside(a); a -= spacing) piers.Add(a);
            for (float a = mid + spacing; Inside(a); a += spacing) piers.Add(a);
        }
        piers.Sort();
        return piers;
    }

    /// <summary>The navigation bay's two piers along the crossing, near then far: either side of the ship's way
    /// <paramref name="w"/>, half a <see cref="NavigationBay"/> out.</summary>
    private static (float near, float far) BayPiers(BridgeLayout.ShipWay w) =>
        (w.Centre - NavigationBay * 0.5f, w.Centre + NavigationBay * 0.5f);

    /// <summary>How far the deck runs on past a navigation bay's pier, pier to the centre of the last module: three
    /// modules, so its edge stands 7 m past the pier, clear of the pier's corbels (6 m), as vanilla's deck runs 6-8 m past
    /// its end piers. Only a bay pier needs it: it stands whatever the bank (<see cref="PierAlongs"/>), every other pier
    /// <see cref="PierBankMargin"/> inside it. With a bay pier in or behind a bank, the deck runs on over the bank to there
    /// and its end stairs go down from there.</summary>
    public const float BayDeckReach = 3f * Module;

    /// <summary>The ship's way under the viaduct (<see cref="BridgeLayout.NavigationChannel"/> for its deck: the walking
    /// surface down to the iron floors, <see cref="FloorDrop"/>), or null off a river. The deck is measured as
    /// laid without a bay: the bay hangs the module grid, and so the deck's ends, on the way it is deciding; and at grade,
    /// so a raise (<see cref="RaisedSteps"/>) never moves a pier or opens or closes the gap.</summary>
    public static BridgeLayout.ShipWay? ShipWayUnder(RoadCrossing crossing, WorldGenerator world)
    {
        float deck = AtGrade(crossing, world, EdgesOf(crossing, world, null));
        return BridgeLayout.NavigationChannel(crossing, world, deck - FloorDrop, deck, BridgeStructures.Kit.DvergrViaduct);
    }

    /// <summary>The middle of the ship's way along the crossing, else null.</summary>
    public static float? FairwayAlong(RoadCrossing crossing, WorldGenerator world) => ShipWayUnder(crossing, world)?.Centre;

    /// <summary>Where the piers of <paramref name="crossing"/> stand (<see cref="PierAlongs(float, BridgeLayout.ShipWay?, float)"/>),
    /// its bays as long as its deck at its highest lets them be (<see cref="Spacing"/>, <see cref="ReachDeck"/>): a raise never
    /// moves a pier.</summary>
    public static List<float> PierAlongs(RoadCrossing crossing, WorldGenerator world)
    {
        BridgeLayout.ShipWay? way = ShipWayUnder(crossing, world);
        return PierAlongs(crossing.Width, way, Spacing(crossing, world, ReachDeck(crossing, world), way));
    }

    /// <summary>The deck the bays are judged under (<see cref="Spacing"/>): the highest it is built at. A crossing's recorded
    /// height (<see cref="Recorded"/>), else at grade raised by its draw (<see cref="DrawnSteps"/>, at most
    /// <see cref="RoadCrossing.RaiseAtMost"/>): the raise taken is the draw or lower (<see cref="RaisedSteps"/>), and never
    /// moves a pier, so the bays carry whichever it takes. (The draw, not the raise: the raise asks the piers' height.)</summary>
    private static float ReachDeck(RoadCrossing crossing, WorldGenerator world) => ReachDeck(crossing, world, AtGrade(crossing, world));

    /// <summary><see cref="ReachDeck(RoadCrossing, WorldGenerator)"/> with the deck at grade <paramref name="atGrade"/>.</summary>
    private static float ReachDeck(RoadCrossing crossing, WorldGenerator world, float atGrade) =>
        Recorded(crossing) is { } ends ? ends.near.Height
            : atGrade + Mathf.Max(0, Mathf.Min(DrawnSteps(crossing, world.GetSeed()), crossing.RaiseAtMost)) * StairLayout.PieceRise;

    // ----------------------------------------------------------- deck reach

    /// <summary>The shortest bay, pier to pier: where two piers' corbel bands meet tip to tip (each reaches
    /// <see cref="CorbelReach"/> along the deck), so no two piers' tops overlap (in game six piers once stood
    /// grouped too close, their tops overlapping). A deck its piers do not carry even so is walked on iron over the middle of its
    /// bays (<see cref="Solve"/>), as over a navigation bay, rather than given another pier.</summary>
    public const float TightSpacing = 2f * CorbelReach;

    /// <summary>How far along the deck a pier's corbel band reaches from its centre: its outermost out_1, at 5 m, a metre
    /// long.</summary>
    public const float CorbelReach = 6f;

    /// <summary>The bays' lengths <see cref="Spacing"/> tries, widest first: vanilla's 16 m, then 14 m and
    /// <see cref="TightSpacing"/>, every one whole 2 m modules ("the other bays may be able to flex a bit").
    /// </summary>
    public static readonly float[] BaySpacings = { PierSpacing, 14f, TightSpacing };

    /// <summary>The least a laid piece's support is of its material's least (WearNTear's minSupport, under which the game
    /// breaks it): a margin for what the model leaves out (an arch's colliders are not one box; the game keeps a support
    /// cached).</summary>
    public const float HoldMargin = 1.2f;

    /// <summary>
    /// The floor support a deck module needs for its black marble (the course on the floors and the arches on the course)
    /// to hold at <see cref="HoldMargin"/>, by the deck reach (<see cref="FloorFromPier"/>): an arch keeps about 0.65 of
    /// what the reach gives the floor under it (the course 1.4 m off the floor's centre, the arch 2.1 m over the course's,
    /// both black marble losing 1/8 a metre). Read with SupportGraph off viaducts on 16 m and 8 m bays: 195 lets 16 m bays
    /// stand on 18 m piers (the weakest arch at 1.29, 1.23 with the model's over-reading taken off, ViaductSupport.Overread;
    /// on 20 m piers 1.08) and 8 m bays on 22 m.
    /// </summary>
    public const float FloorNeed = 195f;

    /// <summary>What an iron piece keeps of the iron one 2 m beside or under it (<see cref="SupportGraph.Iron"/>):
    /// 1 - (2 + 0.1) / 13.</summary>
    public static float IronStep => 1f - SupportGraph.Iron.HorizontalLoss * (Module + SupportGraph.DistanceMargin);

    /// <summary>The support at the top of a pier's iron screen (its last one, beside the floors), on a pier
    /// <paramref name="pierHeight"/> from the deck surface to the ground: the screen is what carries a pier (iron loses 1/13
    /// a metre up, the marble shafts 1/8), from its foot in the ground at 1500 a step a screen up to the floors.</summary>
    public static float ScreenTop(float pierHeight) =>
        SupportGraph.Iron.Max * Mathf.Pow(IronStep, Mathf.Max(0f, pierHeight - FloorDrop) / Module);

    /// <summary>The deck reach: a floor's support <paramref name="distance"/> along the deck from a pier
    /// <paramref name="pierHeight"/> tall, the floors over it a metre off its screen's top and then a step a module.</summary>
    public static float FloorFromPier(float pierHeight, float distance) =>
        ScreenTop(pierHeight) * (1f - SupportGraph.Iron.HorizontalLoss * (1f + SupportGraph.DistanceMargin))
        * Mathf.Pow(IronStep, Mathf.Max(0f, Mathf.Abs(distance) - Module) / Module);

    /// <summary>The deck reach from a floor on the ground (at the most support), <paramref name="distance"/> along.</summary>
    public static float FloorFromGround(float distance) => SupportGraph.Iron.Max * Mathf.Pow(IronStep, Mathf.Abs(distance) / Module);

    /// <summary>
    /// The bays' length off the navigation bay: the widest of <see cref="BaySpacings"/> where the deck reach gives every
    /// module the piers carry <see cref="FloorNeed"/> (from the nearest pier, <see cref="FloorFromPier"/>, or the deck's
    /// modules on the ground at either end, <see cref="FloorFromGround"/>), else <see cref="TightSpacing"/>, the middle of
    /// whose bays may then be walked on iron. The navigation
    /// bay keeps its width (<see cref="NavigationBay"/>: a ship's), so its span is left out: where its
    /// course is beyond reach the deck crosses it on iron (<see cref="Solve"/>). At <paramref name="deck"/>, the highest the
    /// deck is built (<see cref="ReachDeck"/>).
    /// </summary>
    internal static float Spacing(RoadCrossing crossing, WorldGenerator world, float deck, BridgeLayout.ShipWay? way)
    {
        foreach (float spacing in BaySpacings)
            if (spacing > TightSpacing && DeckReaches(crossing, world, deck, way, spacing)) return spacing;
        return TightSpacing;
    }

    private static bool DeckReaches(RoadCrossing crossing, WorldGenerator world, float deck, BridgeLayout.ShipWay? way, float spacing)
    {
        var (piers, _, first, last) = DeckGrid(crossing.Width, way, spacing);
        Vector2 dir = crossing.Direction;
        // A module's floors on the ground: the ground meets their bounds grown as the game grows them, anywhere along it (an
        // estimate at three samples on its line, by the one contact verdict, SupportGraph.Against).
        float reach = Module * 0.5f + SupportGraph.Reach, underside = deck - FloorDrop - Extents(Floor).y;
        bool OnGround(float along)
        {
            float highest = Mathf.Max(Ground(crossing.FromBank + dir * (along - reach), world), Mathf.Max(Ground(crossing.FromBank + dir * along, world),
                Ground(crossing.FromBank + dir * (along + reach), world)));
            return SupportGraph.Against(highest, highest, underside, underside) is SupportGraph.Terrain.Meets or SupportGraph.Terrain.Under;
        }
        // The modules on the ground, from each end in (a valley's deck has none past its banks' edges).
        float nearGround = float.NaN, farGround = float.NaN;
        for (float c = first; c <= last + 1e-3f && OnGround(c); c += Module) nearGround = c;
        for (float c = last; c >= first - 1e-3f && OnGround(c); c -= Module) farGround = c;
        var heights = new float[piers.Count];
        for (int k = 0; k < piers.Count; k++) heights[k] = deck - PierGround(crossing, piers[k], world);
        (float near, float far)? bay = way is BridgeLayout.ShipWay w && piers.Count > 0 ? BayPiers(w) : null;
        for (float c = first; c <= last + 1e-3f; c += Module)
        {
            if (bay is (float nb, float fb) && c > nb + 1e-3f && c < fb - 1e-3f) continue;
            float best = 0f;
            for (int k = 0; k < piers.Count; k++)
                if (heights[k] >= MinPier) best = Mathf.Max(best, FloorFromPier(heights[k], c - piers[k]));
            if (!float.IsNaN(nearGround)) best = Mathf.Max(best, FloorFromGround(c - nearGround));
            if (!float.IsNaN(farGround)) best = Mathf.Max(best, FloorFromGround(c - farGround));
            if (best < FloorNeed) return false;
        }
        return true;
    }

    /// <summary>The lowest ground under a pier's four shafts.</summary>
    public static float PierGround(RoadCrossing crossing, float along, WorldGenerator world)
    {
        Vector2 dir = crossing.Direction, side = new(-dir.y, dir.x);
        Vector2 at = crossing.FromBank + dir * along;
        float lowest = float.MaxValue;
        foreach (var (du, v, _) in Shafts)
        {
            Vector2 p = at + dir * du + side * v;
            lowest = Mathf.Min(lowest, Ground(p, world));
        }
        return lowest;
    }

    /// <summary>The tallest pier as built, deck surface to the ground under its lowest shaft (0 with none).</summary>
    public static float TallestPier(RoadCrossing crossing, WorldGenerator world) => TallestPier(crossing, world, DeckHeight(crossing, world));

    /// <summary>
    /// The mounds under the viaduct's piers as built (<see cref="DeckHeight"/>): <see cref="Mounds(RoadCrossing, WorldGenerator, float)"/>,
    /// none where it needs none; empty where it is not one. What its road stores with it (RoadNetworkGenerator.GroundOf).
    /// </summary>
    public static List<StructureGround.Mound> Mounds(RoadCrossing crossing, WorldGenerator world) =>
        crossing == null || world == null || !BridgeStructures.IsViaduct(crossing, world) ? new List<StructureGround.Mound>()
            : Mounds(crossing, world, DeckHeight(crossing, world)) ?? new List<StructureGround.Mound>();

    /// <summary>
    /// The mounds that bring every pier of a deck at <paramref name="deck"/> taller than <see cref="PierCeiling"/> (deck
    /// surface to the ground under its lowest shaft, <see cref="PierGround"/>) down to it: under each such pier, the ground
    /// where its shafts and screens stand (where <see cref="Solve"/> reads it) raised to the ceiling under the deck, the
    /// least it needs, where a mound may be made there (<see cref="StructureGround.MayRaise"/>: RoadRules.ViaductMostMound at
    /// most, within the terrain writer's cap, its top under any water it stands in). Null where one may not: the pier stands
    /// over the ceiling, and the viaduct is refused (<see cref="Refusal"/>). Empty where no pier is over it.
    /// </summary>
    internal static List<StructureGround.Mound>? Mounds(RoadCrossing crossing, WorldGenerator world, float deck, List<float>? piers = null)
    {
        var mounds = new List<StructureGround.Mound>();
        Vector2 dir = crossing.Direction, side = new(-dir.y, dir.x);
        float top = deck - PierCeiling;
        foreach (float a in piers ?? PierAlongs(crossing, world))
        {
            float ground = PierGround(crossing, a, world);
            if (!(deck - ground > PierCeiling)) continue;
            if (!StructureGround.MayRaise(ground, top, BridgeStructures.Kit.DvergrViaduct)) return null;
            Vector2 at = crossing.FromBank + dir * a;
            var points = new List<Vector2>();
            foreach (var (du, v, _) in Shafts) points.Add(at + dir * du + side * v);
            foreach (float du in ScreenAlongs) points.Add(at + dir * du);
            mounds.Add(new StructureGround.Mound(points, top));
        }
        return mounds;
    }

    /// <summary><see cref="TallestPier(RoadCrossing, WorldGenerator)"/> under a deck at <paramref name="deck"/>.</summary>
    private static float TallestPier(RoadCrossing crossing, WorldGenerator world, float deck) =>
        TallestPier(crossing, world, deck, PierAlongs(crossing, world));

    /// <summary><see cref="TallestPier(RoadCrossing, WorldGenerator, float)"/> of the piers at <paramref name="piers"/>.</summary>
    private static float TallestPier(RoadCrossing crossing, WorldGenerator world, float deck, List<float> piers)
    {
        float tallest = 0f;
        foreach (float a in piers)
            tallest = Mathf.Max(tallest, deck - PierGround(crossing, a, world));
        return tallest;
    }

    /// <summary>
    /// The tallest pier over the FINISHED ground: under each pier, the higher of the generator's ground and any road built
    /// there (<see cref="StructureEnds.BuiltRoadNear"/>: the network so far and <paramref name="plans"/>), since a road's
    /// fill raises the valley floor. The floor (<see cref="MinViaductPier"/>) holds on what a player sees. Under the deck at
    /// grade, as <see cref="Refusal"/> judges it: a raise only makes the piers taller, and never saves a viaduct.
    /// </summary>
    public static float TallestPierOnFinishedGround(RoadCrossing crossing, WorldGenerator world, IReadOnlyList<RoadSpatialGrid.PlannedPath>? plans)
    {
        float deck = AtGrade(crossing, world), tallest = 0f;
        Vector2 dir = crossing.Direction, side = new(-dir.y, dir.x);
        var (nearEdge, farEdge) = EdgesOf(crossing, world);
        float first = Vector2.Dot(nearEdge - crossing.FromBank, dir) + StructureEnds.JoinOverlap, last = Vector2.Dot(farEdge - crossing.FromBank, dir) - StructureEnds.JoinOverlap;
        foreach (float a in PierAlongs(crossing, world))
        {
            float ground = PierGround(crossing, a, world);
            Vector2 at = crossing.FromBank + dir * a;
            // Only road under the deck, beside the pier: never the viaduct's own road at its ends, on the bank or under a
            // joining piece (a pier 3 m in from a bank with end stairs read the apron at the stairs' foot as its floor).
            foreach (var (p, h, w) in StructureEnds.BuiltRoadNear(at, HalfWidth + RoadNetworkGenerator.RoadWidth * 0.5f + 1f, plans))
            {
                float along = Vector2.Dot(p - crossing.FromBank, dir);
                if (along <= first || along >= last || Mathf.Abs(Vector2.Dot(p - crossing.FromBank, side)) > HalfWidth + w * 0.5f) continue;
                if (h < deck) ground = Mathf.Max(ground, h);
            }
            tallest = Mathf.Max(tallest, deck - ground);
        }
        return tallest;
    }

    // --------------------------------------------------------------- valleys

    /// <summary>The longest dry valley a viaduct is laid over (six bays).</summary>
    public const float MaxValleySpan = 96f;
    /// <summary>The shortest: two search cells.</summary>
    public const float MinValleySpan = 16f;
    /// <summary>
    /// A viaduct straight across a dry Mistlands valley from <paramref name="from"/> to
    /// <paramref name="to"/>, the deck at the higher bank, or null with <paramref name="reason"/>.
    /// What makes the search offer it (RoadPathfinder's valley jump) and the generator record
    /// it: one predicate, so the two cannot disagree. Refused: a span outside
    /// <see cref="MinValleySpan"/>..<see cref="MaxValleySpan"/>, a heading the hammer cannot
    /// build, banks further apart in height than a bridge's (<see cref="RoadConstants.MaxBridgeBankDelta"/>),
    /// water or a river under it (a river is a bridge's crossing), ground over the deck, and
    /// whatever <see cref="Refusal"/> refuses (not the Mistlands, under 6 m, piers too tall). Its Mistlands share is asked
    /// before any ground is read: the verdict is Refusal's, only sooner.
    /// </summary>
    public static RoadCrossing? ValleyCrossing(Vector2 from, Vector2 to, WorldGenerator world, out string reason) =>
        ValleyCrossing(from, to, world, out reason, out _);

    /// <summary><see cref="ValleyCrossing(Vector2, Vector2, WorldGenerator, out string)"/>, with the deck at grade it read
    /// (<see cref="GradeDeck"/>) in <paramref name="grade"/> where it lays one, for the search's price
    /// (<see cref="Price(GradeDeck, float, float, float)"/>): one probe reads its deck once.</summary>
    internal static RoadCrossing? ValleyCrossing(Vector2 from, Vector2 to, WorldGenerator world, out string reason, out GradeDeck? grade)
    {
        grade = null;
        float length = Vector2.Distance(from, to);
        if (length < MinValleySpan - 1e-3f || length > MaxValleySpan + 1e-3f) { reason = "span"; return null; }
        Vector2 dir = (to - from) * (1f / length);
        if (!BridgeLayout.HeadingIsPlaceable(BridgeLayout.YawDegrees(dir), 0.5f)) { reason = "heading"; return null; }
        // The share rule first (BridgeStructures.InMistlands, which Refusal asks of the same banks): it reads the banks
        // alone, so a valley outside the Mistlands is refused before its deck and every ground sample under it are read.
        if (!BridgeStructures.InMistlands(from, to, world)) { reason = "not in the Mistlands"; return null; }
        float hFrom = Ground(from, world), hTo = Ground(to, world);
        if (Mathf.Abs(hFrom - hTo) > RoadConstants.MaxBridgeBankDelta) { reason = "banks apart"; return null; }
        float lowest = float.MaxValue;
        // The crossing, its riverbed set once the line is sampled: its deck at grade reads no riverbed.
        var crossing = RoadCrossing.Between(from, to, Mathf.Min(hFrom, hTo), (from + to) * 0.5f, 0f, CrossingKind.Bridge);
        var atGrade = new GradeDeck(crossing, world);
        float deck = atGrade.Height;
        int samples = Mathf.CeilToInt(length);
        for (int i = 0; i <= samples; i++)
        {
            Vector2 p = Vector2.Lerp(from, to, i / (float)samples);
            float g = Ground(p, world);
            lowest = Mathf.Min(lowest, g);
            if (g < RoadWater.LandingFloor) { reason = "water"; return null; }
            if (i % 2 == 0)
            {
                world.GetRiverWeight(p.x, p.y, out float river, out _);
                if (river > 0f) { reason = "river"; return null; }
            }
            if (g > deck + RoadCrossingDetector.DeckClip) { reason = "ground over the deck"; return null; }
        }
        crossing.RiverbedHeight = lowest;
        crossing.ValleyJump = true;
        string? refused = Refusal(crossing, world, PierCeiling, atGrade);
        if (refused != null) { reason = refused; return null; }
        reason = "";
        grade = atGrade;
        return crossing;
    }

    /// <summary>
    /// Where else a valley viaduct's banks may stand on its line, in the order they are tried: its banks moved along the line
    /// (<see cref="BankMoves"/>), each the viaduct <see cref="ValleyCrossing(Vector2, Vector2, WorldGenerator, out string)"/>
    /// lays there (span, heading, banks, ground over the deck, every refusal as it stands); a move it refuses is left out.
    /// <paramref name="nearFirst"/>: the near end's moves before the far end's (the end the road did not meet first). The one
    /// owner of a valley viaduct's banks past the probe's (RoadPathfinder.ViaductOverTerrain: the rim and the first cell back at
    /// its height): StructureEnds.MeetEnds takes the first whose ends the road meets.
    /// </summary>
    internal static IEnumerable<RoadCrossing> MovedBanks(RoadCrossing viaduct, WorldGenerator world, bool nearFirst)
    {
        Vector2 from = viaduct.FromBank, to = viaduct.ToBank, dir = viaduct.Direction;
        foreach (var (near, far) in BankMoves(nearFirst))
            if (ValleyCrossing(from - dir * near, to + dir * far, world, out _) is RoadCrossing moved)
                yield return moved;
    }

    /// <summary>How far each bank of a valley viaduct is moved along its line (<see cref="MovedBanks"/>), outward positive, in
    /// the order tried: one end alone, a deck <see cref="Module"/> at a time out and back up to a search cell
    /// (RoadConstants.PathfindingCellSize), <paramref name="nearFirst"/>'s end first, then the other's; then both, two modules
    /// at a time. Every bound is an existing number: the deck's module and the search's cell.</summary>
    private static IEnumerable<(float near, float far)> BankMoves(bool nearFirst) => nearFirst ? s_nearFirst : s_farFirst;

    private static readonly (float near, float far)[] s_nearFirst = Moves(true), s_farFirst = Moves(false);

    private static (float near, float far)[] Moves(bool nearFirst)
    {
        const float most = RoadConstants.PathfindingCellSize;
        var singles = new List<float>();
        for (float d = Module; d <= most + 1e-3f; d += Module) { singles.Add(d); singles.Add(-d); }
        var moves = new List<(float, float)>();
        foreach (float d in singles) moves.Add(nearFirst ? (d, 0f) : (0f, d));
        foreach (float d in singles) moves.Add(nearFirst ? (0f, d) : (d, 0f));
        for (float n = -most; n <= most + 1e-3f; n += 2f * Module)
            for (float f = -most; f <= most + 1e-3f; f += 2f * Module)
                if (n != 0f && f != 0f) moves.Add((n, f));
        return moves.ToArray();
    }

    /// <summary>How near two jumps' ends are to be the same viaduct jump, either way round (<see cref="AddJumps"/>, and a
    /// banned valley viaduct's match, RoadPathfinder.BanViaduct): a metre.</summary>
    internal const float JumpMatch = 1f;

    /// <summary>
    /// The viaducts a search bought (RoadPathfinder.LastViaductJumps) as crossings of the finished
    /// path: a segment whose two ends lie on a jump's (either way round, within <see cref="JumpMatch"/>) is one,
    /// judged again at the path's own points. Added to <paramref name="crossings"/> in path order;
    /// a segment another crossing already covers is left to it.
    /// </summary>
    public static void AddJumps(List<Vector2> path, IReadOnlyList<(Vector2 from, Vector2 to)> jumps, WorldGenerator world, List<RoadCrossing> crossings)
    {
        if (jumps.Count == 0 || path.Count < 2) return;
        bool added = false;
        for (int i = 1; i < path.Count; i++)
        {
            Vector2 a = path[i - 1], b = path[i];
            bool onJump = false;
            foreach (var (from, to) in jumps)
                if (SameJump(a, b, from, to)) { onJump = true; break; }
            if (!onJump) continue;
            bool taken = false;
            foreach (RoadCrossing c in crossings)
                if (c.FromIndex < i && c.ToIndex > i - 1) { taken = true; break; }
            if (taken) continue;
            RoadCrossing? viaduct = ValleyCrossing(a, b, world, out _);
            if (viaduct == null) continue;
            viaduct.FromIndex = i - 1;
            viaduct.ToIndex = i;
            crossings.Add(viaduct);
            added = true;
        }
        if (added) crossings.Sort((x, y) => x.FromIndex.CompareTo(y.FromIndex));
    }

    /// <summary>Whether a jump from <paramref name="a"/> to <paramref name="b"/> is the jump from <paramref name="from"/> to
    /// <paramref name="to"/>, either way round, each end within <see cref="JumpMatch"/>.</summary>
    internal static bool SameJump(Vector2 a, Vector2 b, Vector2 from, Vector2 to) =>
        (Vector2.Distance(a, from) <= JumpMatch && Vector2.Distance(b, to) <= JumpMatch)
        || (Vector2.Distance(a, to) <= JumpMatch && Vector2.Distance(b, from) <= JumpMatch);

    // ------------------------------------------- what later roads keep clear of

    /// <summary>Pathfinding cells under the valley viaducts in the network (laid this session, or loaded with it), their bank
    /// cells apart: no later road is built through a pier or under a deck.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), int> s_standing = new(CellPairComparer.Instance);
    private static volatile bool s_anyStanding;
    /// <summary>Cells within this of a viaduct's line stand under it.</summary>
    public const float StandingReach = 6f;

    public static void ResetStanding()
    {
        s_standing.Clear();
        s_anyStanding = false;
        lock (s_laidGate) { s_laid = System.Array.Empty<(Vector2 from, Vector2 to)>(); s_laidIslands = System.Array.Empty<int>(); }
    }

    // ------------------------------------------- between structures

    /// <summary>No viaduct's end within this of another viaduct's end: one vanilla bay. The road crosses that valley
    /// another way.</summary>
    public const float EndSpacing = PierSpacing;

    /// <summary>How far past each bank a viaduct's end-approach volume reaches: its most end stairs, then the road's
    /// levelled apron (StructureEnds).</summary>
    public static float ApproachReach => StairLayout.PieceRun * Mathf.CeilToInt(MaxEndStairRise / StairLayout.PieceRise - 1e-3f) + StructureEnds.ApronLength;

    /// <summary>Half a viaduct's width, its arches and heads included (the course and arches stand 3 m out).</summary>
    public const float HalfWidth = 3f;

    /// <summary>Viaducts in the network (laid this session, or loaded with it), banks only (valley and river alike). Copied on write under
    /// <see cref="s_laidGate"/> and published whole, so the search (<see cref="TooClose"/>, from every island worker's
    /// valley jumps) reads it without a lock or a copy.</summary>
    private static volatile (Vector2 from, Vector2 to)[] s_laid = System.Array.Empty<(Vector2 from, Vector2 to)>();
    /// <summary>The island that laid each of <see cref="s_laid"/> (<see cref="CrossIslandReads.Tag"/>), by index; published
    /// before <see cref="s_laid"/>, so a reader of <see cref="s_laid"/> reads at least as many.</summary>
    private static volatile int[] s_laidIslands = System.Array.Empty<int>();
    private static readonly object s_laidGate = new();

    /// <summary>Record a viaduct in the network, for <see cref="TooClose"/> (RoadNetworkGenerator.KeepClearOfViaducts).</summary>
    public static void MarkLaid(RoadCrossing crossing)
    {
        RoadNetworkGenerator.IslandNetworkChanged();
        lock (s_laidGate)
        {
            var laid = s_laid;
            var grown = new (Vector2 from, Vector2 to)[laid.Length + 1];
            System.Array.Copy(laid, grown, laid.Length);
            grown[laid.Length] = (crossing.FromBank, crossing.ToBank);
            var islands = new int[laid.Length + 1];
            System.Array.Copy(s_laidIslands, islands, laid.Length);
            islands[laid.Length] = CrossIslandReads.Tag;
            s_laidIslands = islands;
            s_laid = grown;
        }
    }

    private static bool Same((Vector2 from, Vector2 to) x, Vector2 a, Vector2 b) =>
        (Vector2.Distance(x.from, a) <= 1f && Vector2.Distance(x.to, b) <= 1f) || (Vector2.Distance(x.from, b) <= 1f && Vector2.Distance(x.to, a) <= 1f);

    /// <summary>
    /// Whether a viaduct from <paramref name="a"/> to <paramref name="b"/> would stand too close to another: one owner of
    /// the clearance between structures, like stairs against roads. Back to back: an end within <see cref="EndSpacing"/> of
    /// the other's end. Or in it: its deck and end-approach volume (<see cref="Footprint"/>) overlapping the other's. The
    /// same viaduct (a shared crossing) is
    /// not another. <paramref name="others"/>: the viaducts to keep clear of, else those in the network.
    /// </summary>
    public static string? TooClose(Vector2 a, Vector2 b, IReadOnlyList<(Vector2 from, Vector2 to)>? others = null)
    {
        IReadOnlyList<(Vector2 from, Vector2 to)> list = others ?? s_laid;
        int hit = Clash(a, b, list, others == null ? s_laidIslands : null, out Vector2 end, out Vector2 theirs);
        if (hit < 0) return null;
        var other = list[hit];
        return !float.IsNaN(end.x)
            ?FormattableString.Invariant($"back to back: an end {Vector2.Distance(end, theirs):F0} m from another viaduct's at ({theirs.x:F0}, {theirs.y:F0})")
            : FormattableString.Invariant($"in another viaduct's deck or approach, ({other.from.x:F0}, {other.from.y:F0}) to ({other.to.x:F0}, {other.to.y:F0})");
    }

    /// <summary><see cref="TooClose"/> as the search asks it, for each valley jump it looks at: whether, without a reason
    /// (formatting one per refused jump was most of a refusal's cost) and without allocating.</summary>
    internal static bool IsTooClose(Vector2 a, Vector2 b, IReadOnlyList<(Vector2 from, Vector2 to)>? others = null) =>
        others != null ? Clash(a, b, others, null, out _, out _) >= 0 : Clash(a, b, s_laid, s_laidIslands, out _, out _) >= 0;

    /// <summary>The first viaduct in <paramref name="list"/> a viaduct from <paramref name="a"/> to <paramref name="b"/> stands
    /// too close to (<see cref="TooClose"/>), or -1: back to back (<paramref name="end"/> and <paramref name="theirs"/>, the two
    /// ends; else both NaN), or in its deck or approach. Allocation-free: it runs on every valley jump the search looks at.</summary>
    private static int Clash(Vector2 a, Vector2 b, IReadOnlyList<(Vector2 from, Vector2 to)> list, int[]? islands, out Vector2 end, out Vector2 theirs)
    {
        end = theirs = new Vector2(float.NaN, float.NaN);
        int n = list.Count;
        if (n == 0) return -1;
        Quad mine = Quad.Of(a, b);
        // Another island's viaduct is not this island's to keep clear of (CrossIslandReads): read once, it holds a tag for
        // each viaduct in the list read after it.
        int me = islands == null ? 0 : CrossIslandReads.Me;
        for (int k = 0; k < n; k++)
        {
            var other = list[k];
            if (Same(other, a, b)) continue;
            if (me != 0 && !CrossIslandReads.Sees(me, islands![k]))
            {
                if (Clash(a, b, new[] { other }, null, out _, out _) == 0) CrossIslandReads.Skipped(CrossIslandReads.Kind.Viaducts, islands[k], other.from);
                continue;
            }
            for (int e = 0; e < 2; e++)
            {
                Vector2 mineEnd = e == 0 ? a : b;
                for (int t = 0; t < 2; t++)
                {
                    Vector2 otherEnd = t == 0 ? other.from : other.to;
                    if (Vector2.Distance(mineEnd, otherEnd) < EndSpacing - 1e-3f) { end = mineEnd; theirs = otherEnd; return k; }
                }
            }
            if (Overlap(mine, Quad.Of(other.from, other.to))) return k;
        }
        return -1;
    }

    /// <summary>A viaduct's deck and end-approach volume on the ground: its line from bank to bank lengthened by
    /// <see cref="ApproachReach"/> at each end, <see cref="HalfWidth"/> either side. Four corners.</summary>
    public static Vector2[] Footprint(Vector2 a, Vector2 b)
    {
        Quad q = Quad.Of(a, b);
        return new[] { q.C0, q.C1, q.C2, q.C3 };
    }

    /// <summary><see cref="Footprint"/>'s four corners, held without an array.</summary>
    private readonly struct Quad
    {
        public readonly Vector2 C0, C1, C2, C3;
        private Quad(Vector2 c0, Vector2 c1, Vector2 c2, Vector2 c3) { C0 = c0; C1 = c1; C2 = c2; C3 = c3; }
        public static Quad Of(Vector2 a, Vector2 b)
        {
            Vector2 dir = (b - a).normalized, side = new(-dir.y, dir.x);
            Vector2 s0 = a - dir * ApproachReach, s1 = b + dir * ApproachReach;
            return new Quad(s0 - side * HalfWidth, s1 - side * HalfWidth, s1 + side * HalfWidth, s0 + side * HalfWidth);
        }
        public Vector2 this[int i] => i switch { 0 => C0, 1 => C1, 2 => C2, _ => C3 };
    }

    /// <summary>Two convex quadrilaterals overlap (separating axes on their edges).</summary>
    private static bool Overlap(in Quad p, in Quad q)
    {
        for (int which = 0; which < 2; which++)
        {
            Quad poly = which == 0 ? p : q;
            for (int i = 0; i < 4; i++)
            {
                Vector2 e = poly[(i + 1) % 4] - poly[i], axis = new(-e.y, e.x);
                float pMin = float.MaxValue, pMax = float.MinValue, qMin = float.MaxValue, qMax = float.MinValue;
                for (int j = 0; j < 4; j++) { float d = Vector2.Dot(p[j], axis); pMin = Mathf.Min(pMin, d); pMax = Mathf.Max(pMax, d); }
                for (int j = 0; j < 4; j++) { float d = Vector2.Dot(q[j], axis); qMin = Mathf.Min(qMin, d); qMax = Mathf.Max(qMax, d); }
                if (pMax <= qMin + 1e-4f || qMax <= pMin + 1e-4f) return false;
            }
        }
        return true;
    }

    /// <summary>Mark the cells under a viaduct closed to road, but for its two bank cells.</summary>
    public static void MarkStanding(RoadCrossing crossing)
    {
        RoadNetworkGenerator.IslandNetworkChanged();
        float cell = RoadPathfinder.CellSize;
        (int, int) Cell(Vector2 p) => (Mathf.RoundToInt(p.x / cell), Mathf.RoundToInt(p.y / cell));
        var ends = new HashSet<(int, int)> { Cell(crossing.FromBank), Cell(crossing.ToBank) };
        Vector2 a = crossing.FromBank, b = crossing.ToBank;
        int tag = CrossIslandReads.Tag;
        int x0 = Mathf.FloorToInt((Mathf.Min(a.x, b.x) - StandingReach) / cell), x1 = Mathf.CeilToInt((Mathf.Max(a.x, b.x) + StandingReach) / cell);
        int z0 = Mathf.FloorToInt((Mathf.Min(a.y, b.y) - StandingReach) / cell), z1 = Mathf.CeilToInt((Mathf.Max(a.y, b.y) + StandingReach) / cell);
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
            {
                if (ends.Contains((x, z))) continue;
                var p = new Vector2(x * cell, z * cell);
                if (DistanceToSegment(p, a, b) <= StandingReach) s_standing[(x, z)] = tag;
            }
        s_anyStanding = true;
    }

    /// <summary>Whether a viaduct in the network stands over this pathfinding cell: on an island worker, one its own
    /// island laid or an untagged one (CrossIslandReads).</summary>
    public static bool Standing(int cellX, int cellZ)
    {
        if (!s_anyStanding || !s_standing.TryGetValue((cellX, cellZ), out int tag)) return false;
        if (CrossIslandReads.Sees(tag)) return true;
        CrossIslandReads.Skipped(CrossIslandReads.Kind.Viaducts, tag, new Vector2(cellX * RoadPathfinder.CellSize, cellZ * RoadPathfinder.CellSize));
        return false;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
        return Vector2.Distance(p, a + ab * t);
    }

    // ------------------------------------------------------------ end stairs

    /// <summary>The most stairs, bank to deck, a viaduct has at either end (at most about 3 m of end
    /// stairs between the bank and the deck on each side; past that, no viaduct there). Its default.</summary>
    public const float DefaultMaxEndStairRise = 3f;
    /// <summary>The most end stairs, as set (metres).
    /// Past it at either end <see cref="Refusal"/> refuses the viaduct: a valley keeps
    /// its stairs or road, a river its wooden bridge. 0: level banks only. Settable for tests.</summary>
    public static float MaxEndStairRise = DefaultMaxEndStairRise;

    /// <summary>Whether end stairs rising <paramref name="rise"/> are past <see cref="MaxEndStairRise"/>: the one test, for
    /// the viaduct's ends as the bank takes them (<see cref="Refusal"/>) and the most steps the road may meet an end by
    /// (StructureEnds.MostSteps).</summary>
    internal static bool EndStairsPastMost(float rise) => rise > MaxEndStairRise + 1e-3f;

    // ----------------------------------------------------- at grade or raised

    /// <summary>The share of viaducts whose deck stands a step or two over the deck at grade (some
    /// at grade, some one or two steps up): about half. Its default.</summary>
    public const float DefaultRaisedShare = 0.5f;
    /// <summary>The share raised, as set. 0: every viaduct at grade. Settable for
    /// tests.</summary>
    public static float RaisedShare = DefaultRaisedShare;
    /// <summary>Of the viaducts raised, the share raised two steps rather than one: half.</summary>
    public const float TwoStepShare = 0.5f;
    /// <summary>The most steps a deck is raised.</summary>
    public const int MostRaisedSteps = 2;
    /// <summary>The raise's ruin role, apart from every kit's (BridgeLayout.Ruin).</summary>
    private const int RoleRaise = 36;

    /// <summary>
    /// How many stair rises (<see cref="StairLayout.PieceRise"/>) the deck stands over the deck at grade
    /// (<see cref="AtGrade"/>): 0, the road running onto it at the higher bank, or 1 or 2, each end then a short flight
    /// whose foot the road meets (StructureEnds.MeetEnds, as any end's). The one owner of the choice, which the ends then
    /// carry once they are recorded with the crossing (<see cref="Recorded"/>). Drawn per crossing
    /// and world seed (<see cref="DrawnSteps"/>), so the same crossing in the same world is always raised the same. A raise
    /// that would take a pier over <see cref="PierCeiling"/> or an end's stairs over <see cref="MaxEndStairRise"/>
    /// (<see cref="Refusal"/>'s tests at the raised deck) is taken a step lower, down to none, as is one with an end the
    /// road reaches no way of meeting (StructureEnds.MeetEnds, <see cref="RoadCrossing.RaiseAtMost"/>). Decided after the search:
    /// <see cref="Refusal"/> and <see cref="Price"/> are the deck's at grade, which the raise keeps within, so it never
    /// changes whether or where a viaduct is built. The module grid along the deck, the piers and the gap over a river
    /// are the deck's at grade too (<see cref="ShipWayUnder"/>); the piers reach the raised deck from the same ground.
    /// </summary>
    public static int RaisedSteps(RoadCrossing crossing, WorldGenerator world) =>
        crossing == null || world == null ? 0 : RaisedSteps(crossing, world, AtGrade(crossing, world));

    private static int RaisedSteps(RoadCrossing crossing, WorldGenerator world, float atGrade)
    {
        for (int steps = Mathf.Min(DrawnSteps(crossing, world.GetSeed()), crossing.RaiseAtMost); steps > 0; steps--)
            if (DeckRefusal(crossing, world, PierCeiling, atGrade + steps * StairLayout.PieceRise, mounds: false) == null)
                return steps;
        return 0;
    }

    /// <summary>The raise <paramref name="crossing"/> draws in the world of <paramref name="worldSeed"/>, before any limit:
    /// none at about 1 - <see cref="RaisedShare"/> of crossings, else two at <see cref="TwoStepShare"/> of those, else one.
    /// One roll of the ruin's (BridgeLayout.Ruin: the world seed and the crossing's centre), as the kit's pick
    /// (BridgeLayout.PickKit).</summary>
    internal static int DrawnSteps(RoadCrossing crossing, int worldSeed)
    {
        float roll = new BridgeLayout.Ruin(worldSeed, crossing).Roll(0, BridgeLayout.Ruin.Centre, RoleRaise);
        if (roll >= RaisedShare) return 0;
        return roll / RaisedShare < 1f - TwoStepShare ? 1 : MostRaisedSteps;
    }

    /// <summary>The deck's module grid along the crossing: the piers <paramref name="spacing"/> apart off the navigation
    /// bay, the anchor every module hangs on, and the first and last module centres (each reaching onto its bank: its centre
    /// within a metre of it, or behind it; and over a river's gap, at least <see cref="BayDeckReach"/> past each bay pier).
    /// The first and last are the same whatever the spacing: every pier stands on one grid, 2 m steps from the middle or
    /// the bay piers.</summary>
    private static (List<float> piers, float anchor, float first, float last) DeckGrid(float width, BridgeLayout.ShipWay? way,
        float spacing = PierSpacing)
    {
        float? fairway = way?.Centre;
        List<float> piers = PierAlongs(width, way, spacing);
        // Every module, pier and arch hangs on one grid: the piers' (vanilla's modules are centred on its piers).
        float anchor = piers.Count > 0 ? piers[0] : fairway ?? width * 0.5f;
        float first = anchor - Module * Mathf.CeilToInt((anchor - 1f) / Module - 1e-4f);
        float last = anchor + Module * Mathf.CeilToInt((width - 1f - anchor) / Module - 1e-4f);
        // A bay pier is one of the piers the grid hangs on: whole modules past it are on the grid.
        if (way is BridgeLayout.ShipWay w && w.DeckWidth > 0f)
        {
            var (nearBay, farBay) = BayPiers(w);
            first = Mathf.Min(first, nearBay - BayDeckReach);
            last = Mathf.Max(last, farBay + BayDeckReach);
        }
        return (piers, anchor, first, last);
    }

    /// <summary>
    /// Each end as the bank takes it (StructureEnds.Natural), near (from-bank) end first: the deck's edge, its height as
    /// built (<see cref="DeckHeight"/>, a raised deck's a step or two over the bank), and the stairs whose foot sits nearest
    /// the ground the road arrives on (<see cref="StructureEnds.Decide"/>). <see cref="Refusal"/> caps them
    /// (<see cref="MaxEndStairRise"/>) and <see cref="Price"/> charges for them, both at grade; the road meets them as the
    /// one owner decides (StructureEnds.MeetEnds), which may lay fewer.
    /// </summary>
    internal static (StructureEnds.End near, StructureEnds.End far) NaturalEnds(RoadCrossing crossing, WorldGenerator world) =>
        NaturalEnds(crossing, world, crossing == null || world == null ? 0f : DeckHeight(crossing, world));

    /// <summary><see cref="NaturalEnds(RoadCrossing, WorldGenerator)"/> from a deck at <paramref name="deck"/>.</summary>
    private static (StructureEnds.End near, StructureEnds.End far) NaturalEnds(RoadCrossing crossing, WorldGenerator world, float deck,
        (Vector2 near, Vector2 far)? edges = null)
    {
        if (crossing == null || world == null || crossing.Width < Module) return default;
        Vector2 dir = crossing.Direction;
        var (nearEdge, farEdge) = edges ?? EdgesOf(crossing, world);
        float G(Vector2 p) => Ground(p, world);
        return (StructureEnds.Decide(nearEdge, dir, deck, G), StructureEnds.Decide(farEdge, -dir, deck, G));
    }

    /// <summary>Each end as it meets the road (StructureEnds.Of): what <see cref="Solve"/> lays.</summary>
    public static (StructureEnds.End near, StructureEnds.End far) Ends(RoadCrossing crossing, WorldGenerator world) => StructureEnds.Of(crossing, world);

    // ----------------------------------------------------------------- price

    /// <summary>
    /// What the search pays for a viaduct, the road on it apart. Its own price,
    /// <paramref name="fixedCost"/> plus <paramref name="perMeter"/> per metre of span, times
    /// <see cref="HeightFactor"/> of its tallest pier: "the price falls as the piers get taller, up to the pier
    /// ceiling", and steeply over <see cref="PriceHeight"/> (height pays more, then a lower crossing is the fallback
    /// if nothing is found). On top, <paramref name="endStairPerMeter"/> per metre of its end stairs
    /// (<see cref="Ends"/>): "stairs to or from a viaduct cost much more than ordinary stairs", so level banks win.
    /// The one owner of a viaduct's price: the search's valley jump asks it.
    /// <para>All of it at grade (<see cref="AtGrade"/>): the raise (<see cref="RaisedSteps"/>) is a look decided after the
    /// search, so a random step or two never steers the road (charged at the end stairs' ten times a stair's metre, a
    /// drawn raise would turn the road away from a crossing it would otherwise take). Its steps are free, as a low
    /// structure's first steps are (StructureEnds.LowSteps), and it keeps within every limit <see cref="Refusal"/> holds.</para>
    /// </summary>
    public static float Price(RoadCrossing crossing, WorldGenerator world, float fixedCost, float perMeter, float endStairPerMeter) =>
        Price(new GradeDeck(crossing, world), fixedCost, perMeter, endStairPerMeter);

    /// <summary><see cref="Price(RoadCrossing, WorldGenerator, float, float, float)"/> of a deck at grade already read
    /// (<see cref="GradeDeck"/>: a valley probe's, which its refusal read).</summary>
    internal static float Price(GradeDeck grade, float fixedCost, float perMeter, float endStairPerMeter)
    {
        var (near, far) = grade.Ends;
        return (fixedCost + perMeter * grade.Crossing.Width) * HeightFactor(grade.Tallest) + endStairPerMeter * (near.Rise + far.Rise);
    }

    /// <summary>
    /// The share of its base price a viaduct with its tallest pier <paramref name="tallest"/> m pays (taken between
    /// <see cref="MinClearance"/> and <see cref="PriceCeiling"/>).
    /// <para>Up to <see cref="PriceHeight"/> it is the plain inverse of the height (1 at 10 m, 1.67 at 6 m), so a low
    /// viaduct costs what it did and is never priced out into stairs: the fallback is the same search, not a second
    /// pass. Over it the share falls geometrically to <see cref="CeilingPriceShare"/> at the ceiling (0.56 at 14 m,
    /// 0.37 at 17 m, 0.24 at 20 m, 0.10 at 26 m, against the inverse's 0.71, 0.59, 0.5 and 0.38), and never above the inverse: a valley that
    /// offers a high crossing and a low one has the high one win clearly. With <see cref="CeilingPriceShare"/> 0.5 it
    /// is the inverse price exactly.</para>
    /// </summary>
    public static float HeightFactor(float tallest)
    {
        float h = Mathf.Clamp(tallest, MinClearance, PriceCeiling);
        float inverse = PriceHeight / h;
        if (h <= PriceHeight || PriceCeiling <= PriceHeight) return inverse;
        float steep = (float)Math.Pow(CeilingPriceShare, (h - PriceHeight) / (PriceCeiling - PriceHeight));
        return Mathf.Min(inverse, steep);
    }

    /// <summary>The pier height the price falls to <see cref="CeilingPriceShare"/> at: the pier ceiling the price was set
    /// on (26 m), kept when the support rule lowered the ceiling (<see cref="PierCeiling"/>) so that lowering moves no road
    /// the price chose among viaducts it still builds.</summary>
    public const float PriceCeiling = 26f;

    /// <summary>The pier height at which a viaduct costs its base price (<see cref="Price"/>), and under which it is
    /// priced as the plain inverse. Its default, about the median tallest pier (9.8 m on six test
    /// worlds): with the inverse price anchored at a 20 m ceiling 16 valley viaducts were left of 79, at 10 m
    /// 59.</summary>
    public const float DefaultPriceHeight = 10f;
    /// <summary>Settable for tests.</summary>
    public static float PriceHeight = DefaultPriceHeight;
    /// <summary>The share of its base price a viaduct on piers at the ceiling pays (a tenth). Its default.</summary>
    public const float DefaultCeilingPriceShare = 0.1f;
    /// <summary>Settable for tests (0.5: the inverse price).</summary>
    public static float CeilingPriceShare = DefaultCeilingPriceShare;

    // ------------------------------------------------------- meeting the road


    // ---------------------------------------------------------------- solve

    /// <summary>The viaduct as built: ruined, the navigation gap open.</summary>
    public static List<StructurePiece> Solve(RoadCrossing crossing, WorldGenerator world, int worldSeed) =>
        Solve(crossing, world, worldSeed, ruin: true);

    /// <summary>The viaduct with nothing missing but the navigation gap's deck: as built whole (<see cref="StructureWear"/>:
    /// bridges not weathered), whose gap over a river stays open, and the tests' reference.</summary>
    internal static List<StructurePiece> SolveComplete(RoadCrossing crossing, WorldGenerator world, int worldSeed) =>
        Solve(crossing, world, worldSeed, ruin: false);

    /// <summary>
    /// The viaduct as the game's support rule holds it (<see cref="SupportGraph"/>, <see cref="ViaductSupport"/>), every
    /// piece at <see cref="HoldMargin"/> times its material's least or more (the builder's response to the rule: docs/OWNERS.md,
    /// support estimates and responses):
    /// <list type="bullet">
    /// <item>its bays as long as the deck reach lets them be (<see cref="Spacing"/>);</item>
    /// <item>a module whose black marble course the piers do not hold, as over a navigation bay too long for it (its width a
    /// ship's, which never shrinks), walked on iron instead: two iron floors at the course's top
    /// (<see cref="IronDeckDrop"/>), each on an iron plate across the deck standing on the deck's own floor, iron losing
    /// 1/13 a metre where black marble loses 1/2 sideways, so the way across stays level;</item>
    /// <item>then an ornament that still would not hold left out (an arch, a corbel, a head: <see cref="Hold"/>), in the
    /// whole viaduct and again in the ruin, which takes more of what holds the rest; nothing anyone walks on; read again
    /// after a bank parapet is left out (<see cref="HoldBankParapets"/>);</item>
    /// <item>last, a piece that carries the deck or the walk which the rule still breaks (<see cref="HoldReport.Falling"/>):
    /// the viaduct is refused, no piece laid (an empty list, which nothing saves), and said. One held under the margin but not
    /// broken is laid and said (<see cref="HoldReport.Unheld"/>).</item>
    /// </list>
    /// </summary>
    private static List<StructurePiece> Solve(RoadCrossing crossing, WorldGenerator world, int worldSeed, bool ruin) =>
        Solve(crossing, world, worldSeed, ruin, out _);

    /// <summary>
    /// Whether the support rule refuses the viaduct as it is built (whole or ruined as <see cref="StructureWear.Bridges"/>
    /// says, as BridgeStructures.Solve lays it): its own solve's verdict, a piece carrying the deck or the walk still falling
    /// with every remedy taken, so nothing is laid (<see cref="Solve(RoadCrossing, WorldGenerator, int, bool, out HoldReport, bool)"/>),
    /// said nothing (the record is its build's). Ground not known under a piece is no verdict: not refused. What
    /// BridgeStructures.BuiltInNoKit asks of a viaduct before its road is stored.
    /// </summary>
    internal static bool Refuses(RoadCrossing crossing, WorldGenerator world, int worldSeed) =>
        Solve(crossing, world, worldSeed, StructureWear.Bridges, out HoldReport report, say: false).Count == 0 && report.Falling.Count > 0;

    /// <inheritdoc cref="Solve(RoadCrossing, WorldGenerator, int, bool)"/>
    /// <param name="report">What the rule changed: the modules walked on iron, the ornaments left out, and any piece that
    /// carries the deck or the walk left under the margin (laid all the same, and said in the log).</param>
    /// <param name="say">Whether its calibration record and what the rule changed are said (false: read before its road is
    /// stored, <see cref="Refuses"/>).</param>
    internal static List<StructurePiece> Solve(RoadCrossing crossing, WorldGenerator world, int worldSeed, bool ruin, out HoldReport report,
        bool say = true)
    {
        report = new HoldReport();
        if (crossing == null || world == null || crossing.Width < Module) return new List<StructurePiece>();
        // The finished ground (StructureGround), its ends' joining stretches as the writer levels them: what its piers
        // reach and what grounds its pieces, at its vertices exactly (the one reader of ground not known, as the bridge's
        // solve reads it: BridgeStructures.Solve).
        var grounding = new ViaductSupport.Grounding(StructureGround.Vertices(world));
        // The whole viaduct, its course laid in iron module by module where the rule does not hold it: a round takes every
        // module under the margin at once, and the course beside them, a neighbour short, may follow in the next.
        var iron = new HashSet<int>();
        Layout layout;
        for (int round = 0; ; round++)
        {
            layout = Lay(crossing, world, worldSeed, ruin: false, iron, grounding);
            bool more = false;
            foreach (ViaductSupport.Held h in ViaductSupport.Of(grounding, layout.Pieces))
                if (h.Share < HoldMargin && IsCourse(h.Piece) && iron.Add(layout.ModuleOf(crossing, h.Piece)))
                    more = true;
            if (!more || round >= layout.Modules) break;
        }
        foreach (int m in iron) report.IronAlongs.Add(layout.First + m * Module);
        if (ruin) layout = Lay(crossing, world, worldSeed, ruin: true, iron, grounding);
        report.Buried.AddRange(layout.Buried);
        Hold(grounding, layout.Pieces, ruin, report);
        // A bank parapet left out takes support from what it held: the rest read again.
        if (HoldBankParapets(layout, report)) Hold(grounding, layout.Pieces, ruin, report);
        // A piece carrying the deck or the walk that the rule breaks with every remedy taken (iron, ornaments left out): no
        // viaduct is laid (none saved), never one laid to fall.
        bool refused = report.Falling.Count > 0;
        // Ground not known under a piece (StructureGround.FirstOnUnknownGround, over the vertices the bridge's solve reads)
        // is no verdict: neither refused nor recorded, the pieces as they stand, for the caller to lay none of them and say
        // so (BridgeStructures.Solve, which finds the same vertex), as a staircase does.
        if (StructureGround.FirstOnUnknownGround(layout.Pieces, grounding.Vertex) != null) return layout.Pieces;
        // The calibration record (BridgeStructures.SupportLine): the pier ceiling's margin, a limit read in game, against the
        // rule's reading of what stands; once per viaduct, whole or ruined as it is first solved. A pier over the ceiling on
        // its mound (Mounds) is within the estimate: the margin is the natural ground's, the mound makes it up.
        if (say && ruin == StructureWear.Bridges && !BridgeStructures.SupportSaid(crossing))
            BridgeStructures.SaySupport(crossing, BridgeStructures.Read(BridgeStructures.Kit.DvergrViaduct,
                TallestPier(crossing, world) <= PierCeiling || Mounds(crossing, world).Count > 0,
                PierCeiling - TallestPier(crossing, world), "m under the pier ceiling", ViaductSupport.Of(grounding, layout.Pieces)),
                refused ? "refused" : report.IronAlongs.Count > 0 || report.Dropped.Count > 0 ? "built, iron or ornaments taken" : "built");
        if (refused)
        {
            if (say) Say(crossing, ruin, report);
            return new List<StructurePiece>();
        }
        layout.HangVines(crossing);
        if (ruin) layout.Ruin.ApplyLocationDamage(layout.Pieces); // vanilla's Location.m_applyRandomDamage (BridgeLayout.LocationDamageMin)
        if (say) Say(crossing, ruin, report);
        return layout.Pieces;
    }

    /// <summary>What <see cref="Solve"/> changed for the game's support rule.</summary>
    internal sealed class HoldReport
    {
        /// <summary>The modules walked on iron (their centres along the crossing).</summary>
        public readonly List<float> IronAlongs = new();
        /// <summary>The pieces left out, by prefab: ornaments (<see cref="IsOrnament"/>) in a whole viaduct, and course in a
        /// ruin.</summary>
        public readonly Dictionary<string, int> Dropped = new();
        /// <summary>The pieces left out.</summary>
        public readonly List<StructurePiece> DroppedPieces = new();
        /// <summary>Pieces that carry the deck or the walk under the margin, laid all the same where the rule does not break
        /// them: none, by the layout's rules (its bays, its ceiling, its iron); a stored deck taller than those rules would
        /// build, or ground not known under a pier, is where one shows.</summary>
        public readonly List<StructurePiece> Unheld = new();
        /// <summary>Of <see cref="Unheld"/>, those the rule breaks (<see cref="ViaductSupport.Held.Falls"/>): with any, the
        /// viaduct is refused, no piece laid (<see cref="Solve(RoadCrossing, WorldGenerator, int, bool, out HoldReport)"/>).</summary>
        public readonly List<StructurePiece> Falling = new();
        /// <summary>The pieces of a parapet run on past a deck's end left out where they would stand on a road
        /// (<see cref="OnTheRoad"/>).</summary>
        public readonly List<StructurePiece> OffTheRoad = new();
        /// <summary>The pieces left out as wholly in the finished ground, the rest standing without them
        /// (<see cref="LeaveOutBuried"/>).</summary>
        public readonly List<StructurePiece> Buried = new();
    }

    /// <summary>An ornament: an arch or a head along the deck's edge, the end stairs' parapet and what it stands on, a corbel's
    /// out_1, outcorner or filler. Nothing walks
    /// on it and nothing the deck needs rests on it: the one kind of piece the support rule may leave out of a whole
    /// viaduct.</summary>
    internal static bool IsOrnament(StructurePiece p) =>
        p.Kind == StructurePieceKind.ViaductParapet
        || (p.Kind == StructurePieceKind.ViaductPier && (p.Prefab == Corbel || p.Prefab == CorbelCorner || p.Prefab == Filler));

    /// <summary>A block of the deck's black marble course, which the walk is on.</summary>
    private static bool IsCourse(StructurePiece p) => p.Kind == StructurePieceKind.ViaductDeck && p.Prefab == Block;

    /// <summary>Whether a piece of the deck is walked on, <paramref name="deck"/> the deck's walking surface
    /// (<see cref="DeckHeight"/>): a block of its course (<see cref="IsCourse"/>), or an iron floor of a module walked on iron,
    /// laid <see cref="IronDeckDrop"/> under the deck at the course's top. Not the deck's own floors under the course
    /// (<see cref="FloorDrop"/> under it) nor the iron plate under a walk: a floor is the walk where it lies over the
    /// course's middle (<see cref="CourseDrop"/> under the deck). A pit a ruin leaves in the course is not counted a walk.</summary>
    internal static bool Walked(StructurePiece p, float deck) =>
        IsCourse(p) || p.Kind == StructurePieceKind.ViaductDeck && p.Prefab == Floor && p.Position.y > deck - CourseDrop;

    /// <summary>How far under the deck surface the iron walking floors of a module laid in iron lie (their centre): their
    /// top at the course's.</summary>
    public const float IronDeckDrop = 0.05f;

    /// <summary>
    /// Leave out of <paramref name="pieces"/> every ornament (<see cref="IsOrnament"/>) the rule holds under
    /// <see cref="HoldMargin"/>, and in a ruin the course too (a ruin takes course anyway: a pit to the floors a player
    /// jumps), again until none is left: a piece left out only takes support from those it held. Floors, the walk on iron,
    /// the pier's core and screens and the end steps are never left out: the ruin's way across is the floors', chosen
    /// before; one of them under the margin is reported (<see cref="HoldReport.Unheld"/>), and one the rule breaks too
    /// (<see cref="HoldReport.Falling"/>), for <see cref="Solve(RoadCrossing, WorldGenerator, int, bool, out HoldReport)"/> to refuse.
    /// </summary>
    private static void Hold(ViaductSupport.Grounding grounding, List<StructurePiece> pieces, bool ruin, HoldReport report)
    {
        for (int round = 0; ; round++)
        {
            var drop = new HashSet<StructurePiece>();
            report.Unheld.Clear();
            report.Falling.Clear();
            foreach (ViaductSupport.Held h in ViaductSupport.Of(grounding, pieces))
            {
                if (h.Share >= HoldMargin) continue;
                if (IsOrnament(h.Piece) || (ruin && IsCourse(h.Piece))) drop.Add(h.Piece);
                else
                {
                    report.Unheld.Add(h.Piece);
                    if (h.Falls) report.Falling.Add(h.Piece);
                }
            }
            if (drop.Count == 0 || round >= 32) return;
            foreach (StructurePiece p in drop)
            {
                report.Dropped[p.Prefab] = report.Dropped.TryGetValue(p.Prefab, out int n) ? n + 1 : 1;
                report.DroppedPieces.Add(p);
            }
            pieces.RemoveAll(drop.Contains);
        }
    }

    /// <summary>Viaducts whose <see cref="HoldReport"/> was said, whole or ruined: once each a process.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Vector2, Vector2, bool), byte> s_said = new();

    /// <summary>One line to the log for a viaduct the support rule changed: iron, ornaments left out (info), a piece that
    /// carries the deck left under the margin (a warning: the layout's rules should not let one through), or the viaduct
    /// refused for one the rule breaks (a warning).</summary>
    private static void Say(RoadCrossing crossing, bool ruin, HoldReport report)
    {
        if (report.IronAlongs.Count == 0 && report.Dropped.Count == 0 && report.Unheld.Count == 0 && report.OffTheRoad.Count == 0) return;
        if (!s_said.TryAdd((crossing.FromBank, crossing.ToBank, ruin), 0)) return;
        string at = FormattableString.Invariant($"[VIADUCT] viaduct ({crossing.FromBank.x:F1}, {crossing.FromBank.y:F1}) -> ({crossing.ToBank.x:F1}, {crossing.ToBank.y:F1}) {(ruin ? "ruined" : "whole")}: ");
        string what = FormattableString.Invariant($"{report.IronAlongs.Count} module(s) walked on iron; left out under the support rule: ")
            + (report.Dropped.Count == 0 ? "nothing" : string.Join(", ", report.Dropped.OrderBy(kv => kv.Key).Select(kv => $"{kv.Value} {kv.Key}")))
            + (report.OffTheRoad.Count == 0 ? "" : FormattableString.Invariant($"; {report.OffTheRoad.Count} bank parapet piece(s) left off the road: ")
                + string.Join(", ", report.OffTheRoad.Take(6).Select(p => FormattableString.Invariant($"{p.Prefab} ({p.Position.x:F1}, {p.Position.y:F1}, {p.Position.z:F1})"))));
        var log = ProceduralRoadsPlugin.ProceduralRoadsLogger;
        if (report.Falling.Count > 0)
        {
            log?.LogWarning(at + FormattableString.Invariant($"refused, nothing laid: {report.Falling.Count} piece(s) carrying the deck the game's support rule breaks: ")
                + string.Join(", ", report.Falling.Take(6).Select(p => FormattableString.Invariant($"{p.Prefab} ({p.Position.x:F1}, {p.Position.y:F1}, {p.Position.z:F1})")))
                + "; " + what);
            return;
        }
        if (report.Unheld.Count == 0) { log?.LogInfo(at + what); return; }
        log?.LogWarning(at + what + FormattableString.Invariant($"; {report.Unheld.Count} piece(s) carrying the deck under {HoldMargin} times their least, laid: ")
            + string.Join(", ", report.Unheld.Take(6).Select(p => FormattableString.Invariant($"{p.Prefab} ({p.Position.x:F1}, {p.Position.y:F1}, {p.Position.z:F1})"))));
    }

    /// <summary>One laying of a viaduct (<see cref="Lay"/>): its pieces and what its vines and ruin need after.</summary>
    private sealed class Layout
    {
        public readonly List<StructurePiece> Pieces = new();
        public readonly List<float> PierTops = new();
        public readonly List<int> PierKeys = new();
        /// <summary>Each side's parapet run on past a deck's end onto the bank (<see cref="BankParapet"/>): one whole, or none.</summary>
        public readonly List<List<StructurePiece>> BankParapets = new();
        /// <summary>The bank parapet's pieces left out for standing on a road (<see cref="OnTheRoad"/>), and the black marble
        /// that held only them.</summary>
        public readonly List<StructurePiece> OffTheRoad = new();
        /// <summary>The pieces left out as wholly in the finished ground (<see cref="LeaveOutBuried"/>).</summary>
        public readonly List<StructurePiece> Buried = new();
        public BridgeLayout.Ruin Ruin;
        public Func<Vector2, float> Ground = null!;
        public float First, Deck, Yaw;
        public int Modules;

        /// <summary>The deck module a piece of it stands over.</summary>
        public int ModuleOf(RoadCrossing crossing, StructurePiece piece) =>
            Mathf.RoundToInt((crossing.Along(new Vector2(piece.Position.x, piece.Position.z)) - First) / Module);

        /// <summary>The vines, as the vanilla viaduct's: each pier's curtains at 40 %, every vine on a stone face the viaduct
        /// still has (after <see cref="Hold"/>), none in the ground.</summary>
        public void HangVines(RoadCrossing crossing)
        {
            Vector2 dir = crossing.Direction, side = new(-dir.y, dir.x);
            Vector2 At(float along, float v) => crossing.FromBank + dir * along + side * v;
            var laid = new List<(string prefab, float along, float v, float y)>();
            foreach (StructurePiece p in Pieces)
            {
                if (p.Prefab == StepPrefab || !BuriedPieces.DataOf(p.Prefab, out BuriedPieces.Measured data) || data.Material == null) continue;
                Vector2 rel = new Vector2(p.Position.x, p.Position.z) - crossing.FromBank;
                laid.Add((p.Prefab, Vector2.Dot(rel, dir), Vector2.Dot(rel, side), p.Position.y));
            }
            for (int k = 0; k < PierTops.Count; k++)
            {
                float a = PierTops[k];
                foreach (VinePart vine in PierVines)
                {
                    if (Ruin.Roll(PierKeys[k], vine.Curtain, RoleVine) >= VineCurtainChance) continue;
                    float along = a + vine.Du, y = Deck + vine.Dy;
                    if (y < Ground(At(along, vine.V)) + VineGroundClear || !OnStone(laid, along, vine.V, y)) continue;
                    Vector2 p = At(along, vine.V);
                    Pieces.Add(new StructurePiece
                    {
                        Kind = StructurePieceKind.Flair, Prefab = Vine, Position = new Vector3(p.x, y, p.y),
                        PitchDegrees = vine.Pitch, YawDegrees = Wrap(Yaw + vine.Turn), RollDegrees = vine.Roll,
                    });
                }
            }
        }
    }

    /// <summary>The viaduct laid, whole or ruined, the modules in <paramref name="iron"/> walked on iron, no vines yet; its
    /// pieces wholly in the finished ground left out where the rest stands without them (<see cref="LeaveOutBuried"/>).</summary>
    private static Layout Lay(RoadCrossing crossing, WorldGenerator world, int worldSeed, bool ruin, ISet<int> iron,
        ViaductSupport.Grounding grounding)
    {
        var layout = new Layout();
        List<StructurePiece> pieces = layout.Pieces;
        var r = layout.Ruin = new BridgeLayout.Ruin(worldSeed, crossing);
        Vector2 dir = crossing.Direction, side = new(-dir.y, dir.x);
        float yaw = layout.Yaw = BridgeLayout.YawDegrees(dir);
        float deck = layout.Deck = DeckHeight(crossing, world);
        float width = crossing.Width;
        BridgeLayout.ShipWay? way = ShipWayUnder(crossing, world);
        float? fairway = way?.Centre;
        float navigationGap = way is BridgeLayout.ShipWay w && w.DeckWidth > 0f ? NavigationGap(w.DeckWidth) : 0f;
        // The first module reaches onto the near bank (its centre within a metre of it, or behind it), the last onto the far;
        // the bays as long as the deck at its highest lets them be.
        var (piers, anchor, first, last) = DeckGrid(width, way, Spacing(crossing, world, ReachDeck(crossing, world), way));
        // Ends recorded with the crossing: the deck runs between them (Recorded), on the same grid.
        if (Recorded(crossing) is { } recorded)
        {
            float OnGrid(float along) => anchor + Module * Mathf.RoundToInt((along - anchor) / Module);
            first = OnGrid(crossing.Along(recorded.near.Edge) + Module * 0.5f);
            last = OnGrid(crossing.Along(recorded.far.Edge) - Module * 0.5f);
        }
        layout.First = first;
        int modules = layout.Modules = Mathf.RoundToInt((last - first) / Module) + 1;
        float Mid(float along) => width <= 0f ? 0f : Mathf.Clamp01(1f - Mathf.Abs(along - width * 0.5f) / (width * 0.5f));
        float Survives(float along, float share) => BridgeLayout.RuinProfile.Survives(BridgeLayout.SpanSurvival(Mid(along)), share);

        Vector2 At(float along, float v) => crossing.FromBank + dir * along + side * v;
        // The finished ground (StructureGround): raised or cut by the road's levelling, which levels the road's width to the
        // meeting height where it runs on under the end module (StructureEnds.JoinOverlap). The piers' shafts and screens
        // reach it; what is wholly under it is left out once the whole viaduct is laid (LeaveOutBuried).
        var (nearEnd, farEnd) = Ends(crossing, world);
        Func<Vector2, float> finished = layout.Ground = StructureGround.At(world);
        float G(Vector2 p) => finished(p);
        // Every pier's piece laid, on the bridge's axes, and its pier: a corbel's ornament is left out where another pier's
        // piece stands (a pier past the deck's end beside a bay pier; bays are never shorter than two corbel bands).
        var laid = new List<(string prefab, float along, float v, float y)>();
        var laidPier = new List<int>();
        int pierOf = -1;
        void Add(StructurePieceKind kind, string prefab, float along, float v, float y, float turn, float health)
        {
            Vector2 p = At(along, v);
            if (kind == StructurePieceKind.ViaductPier) { laid.Add((prefab, along, v, y)); laidPier.Add(pierOf); }
            pieces.Add(new StructurePiece
            {
                Kind = kind, Prefab = prefab, Position = new Vector3(p.x, y, p.y),
                YawDegrees = Wrap(yaw + turn), HealthFraction = health,
            });
        }

        // ----- piers
        List<float> pierTops = layout.PierTops;
        List<int> pierKeys = layout.PierKeys;
        for (int k = 0; k < piers.Count; k++)
        {
            float a = piers[k];
            float pierSurvival = BridgeLayout.PierSurvival(BridgeLayout.SpanSurvival(Mid(a)));
            if (deck - PierGround(crossing, a, world) < MinPier) continue;   // the bank itself: nothing to stand
            if (a < first - Module * 0.5f - 1e-3f || a > last + Module * 0.5f + 1e-3f) continue;   // past the deck: nothing to carry
            pierTops.Add(a);
            pierKeys.Add(k);
            pierOf = k;
            foreach (Part part in CorbelBand)
                if (part.Core)
                    Add(StructurePieceKind.ViaductPier, part.Prefab, a + part.Du, part.V, deck + part.Dy, part.Turn, 1f);
            foreach (var (du, v, footingTurn) in Shafts)
            {
                float ground = G(At(a + du, v));
                // Over ground not known no shaft: none reaches it, and the solve that read it lays nothing (BridgeStructures.Solve).
                if (float.IsNaN(ground)) continue;
                float bottom = deck + ShaftTop;
                // Down from under the corbel band, a band every 8 m (vanilla's two at 9 and 17 m under the deck).
                for (int level = 0; ; level++)
                {
                    float centre = deck + ShaftTop - 1f - level * 2f;
                    if (centre - 1f <= ground) break;
                    Add(StructurePieceKind.ViaductPier, level % 4 == 0 ? Band : Block, a + du, v, centre, 90f, 1f);
                    bottom = centre - 1f;
                }
                // The footing, its top under the lowest block or the corbel band (at most 2 m over the
                // ground), sunk 8 m: through the ground's surface, so the game grounds it.
                if (bottom > ground + 0.01f)
                    Add(StructurePieceKind.ViaductPier, Footing, a + du, v, bottom - 4f, footingTurn, 1f);
            }
            // The iron screens through the pier, from the deck down until one is in the ground.
            foreach (float du in ScreenAlongs)
            {
                float ground = G(At(a + du, 0f));
                if (float.IsNaN(ground)) continue;
                for (int level = 0; ; level++)
                {
                    float centre = deck - FloorDrop - level * 2f;
                    if (centre + 1f <= ground) break;
                    // Never ruined: the screens carry the pier (ScreenTop); a ruin that took one left the corbel band's core on
                    // the marble shafts alone, under the support rule's margin on a 22 m pier.
                    Add(StructurePieceKind.ViaductPier, Screen, a + du, 0f, centre, du < 0 ? 0f : 180f, 1f);
                    if (centre <= ground) break;
                }
            }
        }
        // The corbels' ornaments after every pier's core: one laid where another pier's piece stands is left out.
        for (int t = 0; t < pierTops.Count; t++)
        {
            float a = pierTops[t];
            int k = pierOf = pierKeys[t];
            float pierSurvival = BridgeLayout.PierSurvival(BridgeLayout.SpanSurvival(Mid(a)));
            foreach (Part part in CorbelBand)
            {
                if (part.Core) continue;
                if (ruin && r.Roll(k, PartKey(part), RoleOrnament) >= pierSurvival) continue;
                if (Overlaps(laid, laidPier, k, part.Prefab, a + part.Du, part.V, deck + part.Dy)) continue;
                Add(StructurePieceKind.ViaductPier, part.Prefab, a + part.Du, part.V, deck + part.Dy, part.Turn, 1f);
            }
        }

        // ----- deck: which floors stand
        var floor = new bool[modules, 2];
        var gap = new bool[modules];
        for (int m = 0; m < modules; m++)
        {
            float c = first + m * Module;
            gap[m] = fairway is float f && Mathf.Abs(c - f) <= navigationGap * 0.5f - Module * 0.5f + 1e-3f;
            bool end = m == 0 || m == modules - 1;
            for (int lane = 0; lane < 2; lane++)
                floor[m, lane] = !gap[m] && (!ruin || end || r.Roll(m, lane == 0 ? -1 : 1, RoleFloor) < Survives(c, Shares.DeckShare));
        }
        // Now and then a whole module open, a gap a player jumps (never at an end, never two within three modules).
        var holes = new List<int>();
        if (ruin)
            for (int m = 1; m + 1 < modules; m++)
                if (!gap[m] && (holes.Count == 0 || m - holes[holes.Count - 1] >= 3)
                    && r.Roll(m, 0, RoleHole) >= Survives(first + m * Module, Shares.HoleShare))
                {
                    holes.Add(m);
                    floor[m, 0] = floor[m, 1] = false;
                }
        // A way across a player jumps: never two modules running open from side to side (but where the river's gap is).
        for (int m = 1; m < modules; m++)
            if (!gap[m] && !gap[m - 1] && !floor[m, 0] && !floor[m, 1] && !floor[m - 1, 0] && !floor[m - 1, 1])
                floor[m, r.Roll(m, 0, RoleFloor, 1) < 0.5f ? 0 : 1] = true;
        // Every floor joined, floor to floor, to one a pier's corbel or the ground carries.
        var anchored = new bool[modules, 2];
        for (int m = 0; m < modules; m++)
        {
            float c = first + m * Module;
            bool onPier = pierTops.Exists(p => Mathf.Abs(c - p) <= 4f + 1e-3f);
            bool grounded = deck - FloorDrop - 0.1f <= G(At(c, 0f)) + 0.05f;
            for (int lane = 0; lane < 2; lane++)
                anchored[m, lane] = onPier || grounded || deck - FloorDrop - 0.1f <= G(At(c, lane == 0 ? -1f : 1f)) + 0.05f;
        }
        bool[,] Carry()
        {
            var carried = new bool[modules, 2];
            var queue = new Queue<(int m, int lane)>();
            for (int m = 0; m < modules; m++)
                for (int lane = 0; lane < 2; lane++)
                    if (floor[m, lane] && anchored[m, lane]) { carried[m, lane] = true; queue.Enqueue((m, lane)); }
            while (queue.Count > 0)
            {
                var (m, lane) = queue.Dequeue();
                foreach (var (nm, nl) in new[] { (m - 1, lane), (m + 1, lane), (m, 1 - lane) })
                {
                    if (nm < 0 || nm >= modules || !floor[nm, nl] || carried[nm, nl]) continue;
                    carried[nm, nl] = true;
                    queue.Enqueue((nm, nl));
                }
            }
            return carried;
        }
        bool[,] carried = Carry();
        // A hole that cut floors off from every pier left a longer gap than a jump: closed again, nearest the cut first.
        for (int guard = 0; guard < modules; guard++)
        {
            int open = -1;
            for (int m = 1; m < modules && open < 0; m++)
                if (!gap[m] && !gap[m - 1] && !carried[m, 0] && !carried[m, 1] && !carried[m - 1, 0] && !carried[m - 1, 1]) open = m;
            if (open < 0) break;
            int hole = holes.Count == 0 ? -1 : holes.OrderBy(h => Mathf.Abs(h - open)).First();
            if (hole < 0 || Mathf.Abs(hole - open) > 8)
            {
                // Not a hole's doing: both floors of the two modules put back.
                floor[open, 0] = floor[open, 1] = floor[open - 1, 0] = floor[open - 1, 1] = true;
            }
            else
            {
                holes.Remove(hole);
                floor[hole, 0] = floor[hole, 1] = true;
            }
            carried = Carry();
        }

        // ----- deck pieces
        var course = new bool[modules, 3];   // v = -2, 0, +2
        for (int m = 0; m < modules; m++)
        {
            float c = first + m * Module;
            // An end module is the joining piece the road runs on under (StructureEnds.JoinOverlap): its course is never
            // ruined, or the deck's walking surface stops a module short of the road (at the 80 m viaduct at (-3968, -5384) a
            // black marble floor laid by hand there left the road and deck nearly even).
            bool joining = m == 0 || m == modules - 1;
            bool overPier = pierTops.Exists(p => Mathf.Abs(c - p) < 0.01f);
            for (int lane = 0; lane < 2; lane++)
            {
                if (!carried[m, lane]) continue;
                int sideKey = lane == 0 ? -1 : 1;
                Add(StructurePieceKind.ViaductDeck, Floor, c, sideKey * 1f, deck - FloorDrop, 0f, 1f);
            }
            if (iron.Contains(m))
            {
                // Walked on iron (Solve): on each floor an iron plate across the deck, as a pier's screen, and an iron floor on
                // it at the course's top. The plate is the walk's own support: it rests on the floor under it, whatever the ruin
                // took beside it. Never ruined: over a navigation bay the iron is the one way across, and its course, had it
                // stood, would have been a run of it missing.
                for (int lane = 0; lane < 2; lane++)
                {
                    if (!carried[m, lane]) continue;
                    int sideKey = lane == 0 ? -1 : 1;
                    Add(StructurePieceKind.ViaductDeck, Screen, c, sideKey * 1f, deck - CourseDrop, 0f, 1f);
                    Add(StructurePieceKind.ViaductDeck, Floor, c, sideKey * 1f, deck - IronDeckDrop, 0f, 1f);
                }
                continue;
            }
            if (overPier)
            {
                // Over a pier: two blocks on the floors, a head either side (vanilla's).
                for (int lane = 0; lane < 2; lane++)
                {
                    if (!carried[m, lane]) continue;
                    int sideKey = lane == 0 ? -1 : 1;
                    if (ruin && !joining && r.Roll(m, sideKey, RoleCourse) >= Survives(c, Shares.CourseShare)) continue;
                    course[m, lane == 0 ? 0 : 2] = true;
                    Add(StructurePieceKind.ViaductDeck, Block, c, sideKey * 1f, deck - CourseDrop, 90f, 1f);
                    if (!ruin || r.Roll(m, sideKey, RoleHead) < BridgeLayout.PierSurvival(Survives(c, Shares.SupportShare)))
                        Add(StructurePieceKind.ViaductParapet, sideKey > 0 ? HeadLeft : HeadRight, c, sideKey * 3f, deck - CourseDrop,
                            sideKey > 0 ? 270f : 90f, 1f);
                }
                continue;
            }
            for (int slot = 0; slot < 3; slot++)
            {
                int v = (slot - 1) * 2;
                bool under = v < 0 ? carried[m, 0] : v > 0 ? carried[m, 1] : carried[m, 0] || carried[m, 1];
                if (!under) continue;
                if (ruin && !joining && r.Roll(m, v, RoleCourse) >= Survives(c, Shares.CourseShare)) continue;
                course[m, slot] = true;
                Add(StructurePieceKind.ViaductDeck, Block, c, v, deck - CourseDrop, 90f, 1f);
            }
        }
        // Arches along both edges every 2 m, between module centres, each on the course of at
        // least one of the two modules it stands across; vanilla pairs them, tall ends together
        // every 4 m from a pier.
        // Paired about the nearest pier, as vanilla's (on 14 m bays the anchor's pairing would be off at every other pier).
        float ArchTurn(float a)
        {
            float nearest = anchor;
            foreach (float pier in piers) if (Mathf.Abs(a - pier) < Mathf.Abs(a - nearest)) nearest = pier;
            return ((Mathf.RoundToInt(a - nearest) % 4) + 4) % 4 == 1 ? 270f : 90f;
        }
        for (int m = 0; m + 1 < modules; m++)
        {
            float a = first + m * Module + Module * 0.5f;
            foreach (int sideKey in new[] { -1, 1 })
            {
                int slot = sideKey < 0 ? 0 : 2;
                // Over a pier the course is at v +-1: its outer face carries the arch there.
                if (!course[m, slot] && !course[m + 1, slot]) continue;
                if (ruin && r.Roll(m, sideKey, RoleArch) >= Survives(a, Shares.SupportShare)) continue;
                Add(StructurePieceKind.ViaductParapet, Arch, a, sideKey * 2.5f, deck + ArchRise, ArchTurn(a), 1f);
            }
        }

        // ----- ends: a bank lower than the deck is walked off on marble steps, the wooden kit's run (Ends: none
        // where the bank is at the deck's height, and the road runs onto the deck).
        foreach (var (end, key) in new[] { (nearEnd, 0), (farEnd, modules) })
        {
            var steps = new List<StructurePiece>();
            BridgeLayout.LayEndStairs(steps, finished, end, side, r, key, StepPrefab, Block);
            foreach (StructurePiece s in steps)
            {
                s.HealthFraction = 1f;
                pieces.Add(s);
            }
            // The railing down these steps: a stepped black marble parapet on both sides, by the staircases' railing
            // rules (StairRailing.LayParapet), held as an ornament is (Hold): never ruined, as the steps.
            foreach (StructurePiece p in StairRailing.LayParapet(end, BridgeLayout.EndStairsWidth, (x, z) => finished(new Vector2(x, z))))
            {
                p.Kind = StructurePieceKind.ViaductParapet;
                p.HealthFraction = 1f;
                pieces.Add(p);
            }
            // An end without steps over a drop beside the road: the parapet run on past the deck's end onto the bank
            // (BankParapet), never ruined, as the steps' parapet.
            float edge = crossing.Along(end.Edge);
            foreach (List<StructurePiece> bank in BankParapet(end, finished, dir, side, ArchTurn(edge), yaw, layout.OffTheRoad))
            {
                pieces.AddRange(bank);
                layout.BankParapets.Add(bank);
            }
        }

        LeaveOutBuried(layout, grounding);
        return layout;
    }

    /// <summary>The laid viaduct's pieces wholly in the finished ground (<paramref name="grounding"/>'s, at its vertices) left
    /// out where the rest stands without them, the whole viaduct read by the support rule as <see cref="Solve"/> reads it
    /// (<see cref="BuriedPieces.Omit"/>, the one owner): piers, deck, ornaments, end steps and both parapets alike.</summary>
    private static void LeaveOutBuried(Layout layout, ViaductSupport.Grounding grounding)
    {
        List<StructurePiece> left = BuriedPieces.Omit(layout.Pieces, grounding.Vertex, pieces => ViaductSupport.Of(grounding, pieces));
        if (left.Count == 0) return;
        layout.Buried.AddRange(left);
        var gone = new HashSet<StructurePiece>(left);
        foreach (List<StructurePiece> bank in layout.BankParapets) bank.RemoveAll(gone.Contains);
        layout.BankParapets.RemoveAll(bank => bank.Count == 0);
    }

    /// <summary>
    /// The parapet a deck's end without steps runs on past its edge onto the bank, after the one built by hand at a river
    /// viaduct's east end ((4443.2, -4992), the bank falling away both sides of the road), on both sides whatever the bank
    /// does beside the road, so the end looks even (<see cref="StairRailing.LayBankParapet"/>, which lays the cap, the
    /// parapet piece sloping onto the bank and the black marble under them to the ground, none in it), the
    /// deck's arches run on one over the edge, on the end module's course and the course carried on past it, at the turn
    /// the deck's arches take there (<paramref name="archTurn"/>). A piece that would stand on a road (<see cref="OnTheRoad"/>:
    /// a road turning off the deck's line at its end, or another road there) is left out, into <paramref name="offTheRoad"/>,
    /// with the black marble that held only it, the rest of its side kept (a side with no railing piece left goes whole);
    /// the support rule then holds each side, what is kept of it, whole or leaves it out (<see cref="HoldBankParapets"/>).
    /// </summary>
    internal static List<List<StructurePiece>> BankParapet(StructureEnds.End end, Func<Vector2, float> finished, Vector2 dir, Vector2 side,
        float archTurn, float yaw, List<StructurePiece>? offTheRoad = null)
    {
        var sides = new List<List<StructurePiece>>();
        float Finished(int x, int z) => finished(new Vector2(x, z));
        foreach (var (outward, laid) in StairRailing.LayBankParapet(end, BridgeLayout.EndStairsWidth, Finished))
        {
            // The arch laid whatever the ground: one wholly in it is left out with the rest of the viaduct's (LeaveOutBuried),
            // and unknown ground never leaves it out.
            Vector2 at = end.Edge + outward * 2.5f;
            laid.Insert(0, new StructurePiece { Prefab = Arch, Position = new Vector3(at.x, end.Height + ArchRise, at.y), YawDegrees = Wrap(yaw + archTurn) });
            foreach (StructurePiece p in laid)
            {
                p.Kind = StructurePieceKind.ViaductParapet;
                p.HealthFraction = 1f;
            }
            // A piece on a road is left out, the rest of its side kept (the owner, at a road turning south off a 24 m viaduct's
            // west end at (9056, 128): the arch and cap stood clear, the parapet piece a metre onto the turn's inside edge),
            // and with it the black marble that held only what the road took: no column stands holding nothing.
            List<StructurePiece> taken = laid.FindAll(p => OnTheRoad(p, dir, side));
            if (taken.Count > 0)
            {
                laid.RemoveAll(taken.Contains);
                List<StructurePiece> idle = laid.FindAll(p => !IsRailing(p) && !laid.Exists(r => IsRailing(r) && Under(p, r, dir, side)));
                laid.RemoveAll(idle.Contains);
                taken.AddRange(idle);
                if (!laid.Exists(IsRailing)) { taken.AddRange(laid); laid.Clear(); }
                offTheRoad?.AddRange(taken);
            }
            if (laid.Count > 0) sides.Add(laid);
        }
        return sides;
    }

    /// <summary>A bank parapet's railing piece (an arch, a cap, a parapet piece), not the black marble under them.</summary>
    private static bool IsRailing(StructurePiece p) =>
        p.Prefab != StairRailing.ParapetCoursePrefab && p.Prefab != StairRailing.ParapetHalfCoursePrefab;

    /// <summary>Whether <paramref name="course"/> stands under <paramref name="railing"/>: their boxes on the bridge's axes
    /// (<see cref="Extents"/>) overlap across the ground, the course lower.</summary>
    private static bool Under(StructurePiece course, StructurePiece railing, Vector2 dir, Vector2 side)
    {
        var (cu, cv, _) = Extents(course.Prefab);
        var (ru, rv, _) = Extents(railing.Prefab);
        Vector2 d = new(railing.Position.x - course.Position.x, railing.Position.z - course.Position.z);
        return course.Position.y < railing.Position.y && Mathf.Abs(Vector2.Dot(d, dir)) < cu + ru - 1e-3f
            && Mathf.Abs(Vector2.Dot(d, side)) < cv + rv - 1e-3f;
    }

    /// <summary>The most of a road's width a bank parapet's piece may block at any point and stand (<see cref="OnTheRoad"/>),
    /// counted in the rock clearing's slots across the road (<see cref="RoadRockPolicy.Slots"/>): a fifth, one slot of five.
    /// The owner's, 5 Nov 2026 ("40% seems like a lot, lets start with 20% and see"): a railing laid on the road's edge blocks
    /// less than the half a rock could.</summary>
    public const float ParapetRoadOverlap = 0.2f;

    /// <summary>
    /// Whether <paramref name="p"/> (its box on the bridge's axes, <see cref="Extents"/>) stands on a road built there
    /// (the network as it now stands, as <see cref="StructureEnds.BuiltRoadNear"/> reads it) more than it is let: at any
    /// road point, in the road's headroom box (over its surface by more than the lift, <see cref="RockClearances.Bottom"/>, and under
    /// its top, <see cref="RockClearances.Top"/>: the box the rock clearing keeps clear, a stair's too), it blocks
    /// more of the road's width than <see cref="ParapetRoadOverlap"/>, counted as the rock clearing counts a rock
    /// (<see cref="RoadRockPolicy.Slots"/> narrow capsules across the road, <see cref="RoadRockPolicy.Slot"/>, the road's
    /// heading there <see cref="RoadRockPolicy.Heading"/>). A piece clipping the road's edge less than that stands; a piece
    /// flush with the surface or under it (the course carried on past a deck's end under the inside edge of a turn) stands
    /// in the road's fill (the owner's corner at (9056, 128)).
    /// </summary>
    internal static bool OnTheRoad(StructurePiece p, Vector2 dir, Vector2 side)
    {
        var (hu, hv, hy) = Extents(p.Prefab);
        Vector2 c = new(p.Position.x, p.Position.z);
        float bottom = p.Position.y - hy, top = p.Position.y + hy;
        // The points round it, and their neighbours for each one's heading.
        List<RoadSpatialGrid.RoadPoint> near = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(c.x, 0f, c.y),
            hu + hv + 2f * RoadNetworkGenerator.RoadWidth).FindAll(q => q.IsRoad);
        float reach = hu + hv + RoadNetworkGenerator.RoadWidth;
        foreach (RoadSpatialGrid.RoadPoint q in near)
        {
            if ((q.p - c).sqrMagnitude > reach * reach) continue;
            if (top <= RockClearances.Bottom(q.h) || bottom >= RockClearances.Top(q.h)) continue;
            Vector2 heading = RoadRockPolicy.Heading(q, near), across = new(-heading.y, heading.x);
            int blocked = 0;
            for (int k = 0; k < RoadRockPolicy.Slots; k++)
            {
                var (off, r) = RoadRockPolicy.Slot(k, RoadRockPolicy.Slots, q.w);
                Vector2 s = q.p + across * off;
                float du = Mathf.Max(Mathf.Abs(Vector2.Dot(s - c, dir)) - hu, 0f), dv = Mathf.Max(Mathf.Abs(Vector2.Dot(s - c, side)) - hv, 0f);
                if (du * du + dv * dv < r * r - 1e-6f) blocked++;
            }
            if (blocked > ParapetRoadOverlap * RoadRockPolicy.Slots + 1e-3f) return true;
        }
        return false;
    }

    /// <summary>A side of a parapet run on past a deck's end the support rule took a piece of (<see cref="Hold"/>) is left out
    /// whole: no column stands in a drop with nothing on it, nor a cap on a column that fell. Said with what the rule left
    /// out (<see cref="HoldReport.Dropped"/>); and the pieces left off a road (<see cref="Layout.OffTheRoad"/>). Whether any
    /// was left out now.</summary>
    private static bool HoldBankParapets(Layout layout, HoldReport report)
    {
        report.OffTheRoad.AddRange(layout.OffTheRoad);
        var dropped = new HashSet<StructurePiece>(report.DroppedPieces);
        var rest = new HashSet<StructurePiece>();
        foreach (List<StructurePiece> bank in layout.BankParapets)
        {
            if (!bank.Exists(dropped.Contains)) continue;
            foreach (StructurePiece p in bank)
            {
                if (dropped.Contains(p) || !rest.Add(p)) continue;
                report.Dropped[p.Prefab] = report.Dropped.TryGetValue(p.Prefab, out int n) ? n + 1 : 1;
                report.DroppedPieces.Add(p);
            }
        }
        layout.Pieces.RemoveAll(rest.Contains);
        return rest.Count > 0;
    }

    /// <summary>Whether a <paramref name="prefab"/> of pier <paramref name="pier"/> centred at (<paramref name="along"/>,
    /// <paramref name="v"/>, <paramref name="y"/>) would share room with another pier's piece in <paramref name="laid"/>
    /// (their bounds overlapping by more than 5 cm every way, <see cref="Extents"/>). A pier's own pieces overlap as
    /// vanilla's do.</summary>
    private static bool Overlaps(List<(string prefab, float along, float v, float y)> laid, List<int> laidPier, int pier, string prefab,
        float along, float v, float y)
    {
        var (hu, hv, hy) = Extents(prefab);
        for (int i = 0; i < laid.Count; i++)
        {
            if (laidPier[i] == pier) continue;
            var (other, pa, pv, py) = laid[i];
            var (ou, ov, oy) = Extents(other);
            if (Mathf.Abs(along - pa) < hu + ou - 0.05f && Mathf.Abs(v - pv) < hv + ov - 0.05f && Mathf.Abs(y - py) < hy + oy - 0.05f)
                return true;
        }
        return false;
    }


    /// <summary>Whether a vine's origin lies within <see cref="VineTouch"/> of a stone piece laid (its bounds,
    /// <see cref="Extents"/>), on the bridge's axes.</summary>
    private static bool OnStone(List<(string prefab, float along, float v, float y)> laid, float along, float v, float y)
    {
        foreach (var (prefab, pa, pv, py) in laid)
        {
            var (hu, hv, hy) = Extents(prefab);
            if (Mathf.Abs(along - pa) <= hu + VineTouch && Mathf.Abs(v - pv) <= hv + VineTouch && Mathf.Abs(y - py) <= hy + VineTouch)
                return true;
        }
        return false;
    }

    /// <summary>A stable small number per pier part, for its ruin key.</summary>
    private static int PartKey(Part part) =>
        Mathf.RoundToInt(part.Du * 2f) * 1000 + Mathf.RoundToInt(part.V * 2f) * 10 + Mathf.RoundToInt(-part.Dy);

    private static float Wrap(float deg)
    {
        deg %= 360f;
        return deg < 0f ? deg + 360f : deg;
    }

    /// <summary>The ground the layout stands on: the game's blended terrain, as the wooden kit's.</summary>
    internal static float Ground(Vector2 p, WorldGenerator world) => BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);

    // ----------------------------------------------------------- describing

    /// <summary>One line about a viaduct: where, how long, how high, its piers. For the dev
    /// command and the generation log.</summary>
    public static string Describe(RoadCrossing crossing, WorldGenerator world)
    {
        float deck = DeckHeight(crossing, world);
        int raised = Mathf.RoundToInt((deck - AtGrade(crossing, world)) / StairLayout.PieceRise);
        List<float> piers = PierAlongs(crossing, world);
        bool river = HasNavigationBay(crossing, world), water = crossing.RiverbedHeight < crossing.WaterLevel;
        return FormattableString.Invariant(
            $"viaduct at ({crossing.Center.x:F0}, {deck:F0}, {crossing.Center.y:F0}): {crossing.Width:F0} m from ({crossing.FromBank.x:F0}, {crossing.FromBank.y:F0}) to ({crossing.ToBank.x:F0}, {crossing.ToBank.y:F0}), deck {Clearance(crossing, world):F1} m over the {(water ? "water" : "ground")}, {piers.Count} pier(s), tallest {TallestPier(crossing, world):F1} m{(river ? ", navigation gap" : "")}{(raised > 0 ? $", raised {raised} step(s)" : "")}");
    }
}
