using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

public enum BridgePieceKind
{
    Post,    // vertical pole segment, stacked down into the riverbed
    Beam,    // crossbeam tying a station's post pair under the deck
    Deck,    // walkable plank plate resting on a station pair
    Stair,   // a step piece from the road up onto the deck
    Debris,  // a collapsed pole settled on the riverbed, outside the fairway
}

/// <summary>One placed piece of a bridge (a persistent ZDO once spawned).</summary>
public sealed class BridgePiece
{
    public BridgePieceKind Kind;
    public string Prefab = "";
    public Vector3 Position;
    public float YawDegrees;
    public float PitchDegrees;
    public float RollDegrees;
    /// <summary>WearNTear health fraction (drives the vanilla damage visuals).</summary>
    public float HealthFraction = 1f;
}

/// <summary>
/// Deterministic layout of a ruined wooden bridge at a recorded crossing
/// (bridges prototype). Pure logic; the game places the returned plan later.
///
/// The bridge is a row of stations every <see cref="DeckSpan"/> metres from
/// bank to bank. THE DECK IS LEVEL: a wood_floor is placed by the vanilla
/// hammer at Quaternion.Euler(0, yaw, 0) -- yaw only, and the snap path
/// moves the ghost, never turns it -- so a pitched plate is one no player
/// could ever put back. The deck therefore sits at ONE height, the higher
/// bank's (both ends already lifted to the water plus a freeboard), and the
/// drop to the lower bank is walked off by that end's stair run, which is
/// as many snapped steps as the bank needs. A station is a pair of poles
/// stacked down from just under the deck until buried in the riverbed, tied
/// by two crossbeams; the deck is two lanes of plates, left and right of the
/// crossing line, four metres wide like the road. Plates exist only where
/// both end stations survive. Ruin is deterministic per (crossing, world seed):
/// survival falls toward mid-span, piers outlive the deck, a removed
/// station may leave a rotted stub or a toppled pole on the bed, and a
/// navigation gap around the fairway is always left open so boats still
/// pass. Every end is a stair down from the deck edge into the bank. A ford
/// gets no pieces unless it is spanned: a low footbridge, lightly ruined.
/// Every piece is grounded or rests on a grounded one by construction.
/// </summary>
public static class BridgeLayout
{
    // Vanilla wood pieces; geometry as measured in-game.
    public const string PostPrefab = "wood_pole2";   // 2 m pole, origin at its centre
    public const string BeamPrefab = "wood_beam";    // 2 m beam along its local x
    public const string DeckPrefab = "wood_floor";   // 2 x 2 m plate, walking surface at its origin
    public const string StairPrefab = "wood_stair";  // 2 m run, 1 m rise, origin at the foot, rises toward local -z
    public const string DebrisPrefab = "wood_pole2";

    public const float DeckSpan = 2f;         // one deck plate, measured ALONG the deck (slope distance)
    public const float PostSegment = 2f;      // vertical metres per pole
    public const float LaneOffset = 1f;       // the two lanes' centres, either side of the crossing line
    public const float DeckHalfWidth = 2f;    // two 2 m plates side by side: a 4 m deck, the road's width
    public const float PostSideOffset = 1.75f; // post pairs under the outer halves of the beams, never in the walkway
    public const int MaxStairSteps = 12;      // a stair marches outward until its foot is in the dirt, at most this far
    public const float StairRun = 2f;         // one step: 2 m out, 1 m down
    public const float StairRise = 1f;
    public const float StairHalfRun = 1f;     // origin at the foot: its LOW snaps are this far OUTWARD
    public const float StairHalfWidth = 1f;   // ... and this far to either side
    public const float StairFootTolerance = 0.15f; // a foot this far above the dirt still counts as landed

    /// <summary>Bumped whenever the emitted geometry changes shape. Persisted
    /// with the spawned-zone record, so a world built by an older layout is
    /// recognised on load and its pieces are replaced rather than mixed with
    /// new ones (see RoadNetworkPersistence.TryLoadBridgeZones).
    ///
    /// <para>4: the navigation gap narrowed from 20 m to 16 m, which moves the
    /// emitted shape of every bridge over a sailable channel, and free spans
    /// changed it again for narrow ones. Without this bump a world explored
    /// partly under layout 3 would carry both gap widths side by side.</para>
    /// </summary>
    public const int LayoutVersion = 4;
    public const float DeckFreeboard = 0.5f;  // deck at least this far above the water
    public const float PostTopBelowDeck = 0.2f;
    public const float BeamBelowDeck = 0.13f;

    // Ruin rule: station survival falls from BankSurvival at the banks to
    // MidSurvival at mid-span; piers outlive the deck by PierPersistence, so
    // a long span reads as a row of piers with a collapsed deck; a removed
    // station leaves a stub or debris by StubChance / DebrisChance.
    public const float BankSurvival = 0.85f;
    public const float MidSurvival = 0.4f;
    public const float PierPersistence = 0.85f;
    public const float StubChance = 0.5f;
    public const float DebrisChance = 0.5f;
    public const float RuinHealthMin = 0.05f;
    public const float RuinHealthMax = 0.70f;

    /// <summary>The navigation gap kept clear of piers and deck: at least
    /// FairwayGapWidth (a longship plus room to line up), growing with the
    /// span, never wider than the fairway itself, plus a clearance each side.</summary>
    public const float FairwayGapWidth = 16f;

    /// <summary>
    /// A bridge no wider than this spans bank to bank with NO piers in the
    /// water: two abutments and a deck between them.
    ///
    /// <para>16 m is what a wooden deck holds up unsupported, and it is
    /// arithmetic from the same rule as the pier ceiling. Wood loses 0.2 of
    /// its support per metre sideways against 0.125 downward, so a 2 m
    /// wood_beam chain keeps 58% per beam and a cantilever dies 8 m out; a
    /// deck anchored at BOTH ends gets the average of its two support points
    /// whenever they are more than 100 degrees apart, which doubles it. 16 m
    /// holds with the weakest piece at 11.3 against a minimum of 10, and 20 m
    /// falls.</para>
    ///
    /// <para>This is a ceiling for the generator, not a rule for the player:
    /// players get better materials that span further, and can fill a gap
    /// themselves if they want to. So nothing here validates what a player may
    /// build across a gap, and nothing should start to.</para>
    ///
    /// <para>It is also why the navigation gap above is 16 m rather than 20:
    /// a longship needs about 6 m and 10 m is comfortable, so 16 m is
    /// generous for boats and happens to be exactly what the kit can span.
    /// A free-spanned bridge needs no navigation gap at all -- there is
    /// nothing standing in the water to open.</para>
    /// </summary>
    public const float FreeSpanMaxWidth = 16f;
    public const float FairwayGapFraction = 0.3f;
    public const float FairwayClearance = 1f;

    /// <summary>A crossing shares a site only when its banks lie within this
    /// distance of the site's: a lane is 2 m wide, so anything further off
    /// would leave a road pointing at water. Crossings that were snapped onto
    /// a site (RoadNetworkGenerator) have identical banks.</summary>
    public const float SameSiteRadius = 0.5f;

    /// <summary>One bridge per site: crossings with the same banks share the
    /// first one's plan.</summary>
    public static List<RoadCrossing> DistinctSites(IEnumerable<RoadCrossing> crossings)
    {
        List<RoadCrossing> sites = new();
        foreach (RoadCrossing c in crossings)
        {
            bool shared = false;
            foreach (RoadCrossing s in sites)
            {
                if (RoadCrossing.SameBanks(s, c, SameSiteRadius))
                {
                    shared = true;
                    break;
                }
            }
            if (!shared)
                sites.Add(c);
        }
        return sites;
    }

    /// <summary>
    /// Whether this crossing spans bank to bank with nothing in the channel:
    /// short enough for the kit to hold up, over water that cannot carry a
    /// pier at the height this deck wants.
    ///
    /// <para>"Deep water" here means water that cannot support the bridge's
    /// piers at the level the bridge wants to build at. It is the principled
    /// definition, and it makes
    /// the free span the escape hatch for the pier ceiling rather than a
    /// second, unrelated rule - a crossing is spanned exactly when it is short
    /// enough to span and a pier there could not stand.</para>
    ///
    /// <para>Two definitions were tried and rejected. An absolute depth
    /// (deeper than a ford may wade, 0.8 m) is very nearly a tautology: a
    /// crossing under 16 m wide is a BRIDGE rather than a ford precisely
    /// because the water is deeper than that, so the test discriminated almost
    /// nothing and every short bridge lost its piers. A sailable fairway is
    /// the opposite problem - on one measured world the two narrowest bridges were 20 m over
    /// water with no fairway at all, too deep to wade and not deep enough to
    /// sail, and a fairway test misses every channel like them.</para>
    ///
    /// <para>There is a SECOND way a channel refuses a pier, and it is the
    /// commoner one: the boat lane covers the whole crossing, so there is
    /// nowhere a pier is allowed to stand. A 14 m bridge over a 14 m fairway
    /// is 100% navigation gap, and before this rule the generator emitted a
    /// bridge with no deck and no piers - two stair runs with open water
    /// between them. Both cases are "the channel cannot take a pier"; only the
    /// reason differs.</para>
    ///
    /// <para>Case (a) fires rarely by design: at 16 m the channel has to be
    /// deeper than the ceiling for a pier to fail, which is a slot canyon. If
    /// short deep crossings should be spanned more eagerly than that, this
    /// predicate is the one place to say so.</para>
    ///
    /// <para>It composes with the trim because a crossing this narrow is
    /// exempt from it: the trim never lowers the deck back under the ceiling,
    /// so a pier that could not stand still cannot, and the span is what gets
    /// built instead.</para>
    /// </summary>
    public static bool IsFreeSpan(RoadCrossing crossing, WorldGenerator world)
    {
        if (crossing == null || world == null) return false;
        if (crossing.Kind != CrossingKind.Bridge) return false;
        if (crossing.Width > FreeSpanMaxWidth) return false;

        // (a) A pier could not stand at the height this deck wants.
        if (PierHeight(crossing, world) > RoadConstants.MaxBridgePierHeight)
            return true;

        // (b) The boat lane covers the whole crossing, so there is nowhere a
        //     pier is ALLOWED to stand. Measured: a 14 m bridge over a 14 m
        //     fairway has 100% of its length inside the gap, and without this
        //     the generator emits a bridge with no deck and no piers at all --
        //     two stair runs and open water between them. That is a silent
        //     defect this rule now catches rather than causes.
        return crossing.FairwayWidth > 0f
            && FairwayGap(crossing) + FairwayClearance * 2f >= crossing.Width;
    }

    public static float FairwayGap(RoadCrossing crossing) =>
        Mathf.Min(crossing.FairwayWidth, Mathf.Max(FairwayGapWidth, crossing.Width * FairwayGapFraction));

    /// <summary>Deck height where it meets each bank: the bank's ground,
    /// lifted to the water plus freeboard where the ground is lower.</summary>
    public static (float from, float to) DeckEndHeights(RoadCrossing crossing, WorldGenerator world)
    {
        float minDeck = crossing.WaterLevel + DeckFreeboard;
        return (Mathf.Max(BiomeBlendedHeight.GetBlendedHeight(crossing.FromBank.x, crossing.FromBank.y, world), minDeck),
                Mathf.Max(BiomeBlendedHeight.GetBlendedHeight(crossing.ToBank.x, crossing.ToBank.y, world), minDeck));
    }

    // ------------------------------------------------------------ geometry

    /// <summary>
    /// The deck's single height: the higher of the two bank deck heights.
    /// DeckEndHeights has already lifted both to the water plus a freeboard,
    /// so the deck clears the water by construction, and the lower bank's
    /// stair run walks off the difference.
    /// </summary>
    public static float DeckHeight(RoadCrossing crossing, WorldGenerator world)
    {
        (float from, float to) = DeckEndHeights(crossing, world);
        return Mathf.Max(from, to);
    }

    /// <summary>
    /// How far the structure stands above the channel bed: deck height minus
    /// the lowest terrain between the banks. This is the quantity that decides
    /// whether vanilla structural support can reach the deck, and it is NOT
    /// the water depth the crossing diagnostic used to report on its own - a
    /// bridge over a 2.3 m deep channel can stand 12 m tall when its banks are
    /// high. Meaningful for bridges; a ford has no piers.
    /// </summary>
    public static float PierHeight(RoadCrossing crossing, WorldGenerator world) =>
        DeckHeight(crossing, world) - crossing.RiverbedHeight;

    /// <summary>How much bank the stairs have to absorb: the fall from the
    /// deck to each bank. Reported by the diagnostic; the deck itself is
    /// level, so this is a property of the CROSSING, not of the deck.</summary>
    public static (float from, float to) BankDrop(RoadCrossing crossing, WorldGenerator world)
    {
        (float from, float to) = DeckEndHeights(crossing, world);
        float deck = Mathf.Max(from, to);
        return (deck - from, deck - to);
    }

    /// <summary>
    /// Horizontal distance between stations. The deck is level, so a plate
    /// spans exactly its own length and its corners land on its neighbours'.
    /// (A pitched deck would need DeckSpan * cos(theta) here -- and would not
    /// be repairable at all, since the hammer cannot pitch a floor.)
    /// </summary>
    public static float StationSpacing() => DeckSpan;

    /// <summary>Whole spans that fit in the width, at least one. The epsilon
    /// keeps a width that IS a whole number of spans from losing one to float
    /// error.</summary>
    public static int Bays(float width) =>
        Mathf.Max(1, Mathf.FloorToInt(width / StationSpacing() + 1e-3f));

    /// <summary>The horizontal length actually built: whole spans only, and
    /// never longer than the crossing the pathfinder accepted.</summary>
    public static float BuiltLength(float width) => Bays(width) * StationSpacing();

    /// <summary>Where the stations stand, measured horizontally along the
    /// crossing from the near bank. The remainder past the last station is
    /// under one stair run by construction; the far stair, anchored at that
    /// station, crosses it in its first step.</summary>
    internal static float[] StationsAlong(float width)
    {
        int bays = Bays(width);
        float d = StationSpacing();
        float[] alongs = new float[bays + 1];
        for (int i = 0; i <= bays; i++)
            alongs[i] = i * d;
        return alongs;
    }

    /// <summary>Whether a point this far along the crossing lies in the
    /// navigation gap: the run of bays left open on purpose so boats pass.
    /// Not ruin -- the diagnostic reports the two separately.</summary>
    public static bool InNavigationGap(RoadCrossing crossing, float along)
    {
        if (crossing.Kind != CrossingKind.Bridge || crossing.FairwayWidth <= 0f)
            return false;
        float mid = crossing.Along(crossing.FairwayCenter);
        float half = FairwayGap(crossing) * 0.5f + FairwayClearance;
        return Mathf.Abs(along - mid) <= half;
    }

    // ---------------------------------------------------------- snap points

    // Measured off the prefabs in game (valheimCLI cli_piece_geometry):
    //   wood_floor  snaps at its four corners      (+-1, 0, +-1)
    //   wood_beam   snaps at its two ends          (+-1, 0, 0)
    //   wood_pole2  snaps top and bottom           (0, +-1, 0)
    //   wood_stair  snaps HIGH (+-1, 1, -1), LOW (+-1, 0, +1)
    private static readonly Vector3[] FloorSnaps = { new(1, 0, -1), new(-1, 0, -1), new(1, 0, 1), new(-1, 0, 1) };
    private static readonly Vector3[] BeamSnaps = { new(-1, 0, 0), new(1, 0, 0) };
    private static readonly Vector3[] PoleSnaps = { new(0, 1, 0), new(0, -1, 0) };
    private static readonly Vector3[] StairSnaps = { new(-1, 1, -1), new(1, 1, -1), new(-1, 0, 1), new(1, 0, 1) };

    /// <summary>
    /// A piece's snap points in the world, from the prefab's measured local
    /// snaps and the rotation the game applies when spawning it
    /// (Quaternion.Euler(pitch, yaw, roll), which Unity composes as
    /// yaw about y, then pitch about x, then roll about z, applied to the
    /// local vector in the order roll, pitch, yaw). This is what "two pieces
    /// meet" means for a player: a snap of one lands on a snap of the other.
    /// </summary>
    public static Vector3[] SnapPoints(BridgePiece piece)
    {
        Vector3[] local = piece.Prefab switch
        {
            DeckPrefab => FloorSnaps,
            BeamPrefab => BeamSnaps,
            StairPrefab => StairSnaps,
            PostPrefab => PoleSnaps,
            _ => System.Array.Empty<Vector3>(),
        };
        Vector3[] world = new Vector3[local.Length];
        for (int i = 0; i < local.Length; i++)
            world[i] = piece.Position + Rotate(local[i], piece.PitchDegrees, piece.YawDegrees, piece.RollDegrees);
        return world;
    }

    /// <summary>Unity's Euler convention, spelled out so the tests can rely on
    /// it without a Quaternion: roll about z, then pitch about x, then yaw
    /// about y.</summary>
    public static Vector3 Rotate(Vector3 v, float pitchDeg, float yawDeg, float rollDeg)
    {
        const float k = Mathf.PI / 180f;
        float cr = Mathf.Cos(rollDeg * k), sr = Mathf.Sin(rollDeg * k);
        v = new Vector3(cr * v.x - sr * v.y, sr * v.x + cr * v.y, v.z);
        float cp = Mathf.Cos(pitchDeg * k), sp = Mathf.Sin(pitchDeg * k);
        v = new Vector3(v.x, cp * v.y - sp * v.z, sp * v.y + cp * v.z);
        float cy = Mathf.Cos(yawDeg * k), sy = Mathf.Sin(yawDeg * k);
        return new Vector3(cy * v.x + sy * v.z, v.y, -sy * v.x + cy * v.z);
    }

    // ---------------------------------------------------------------- ruin

    /// <summary>
    /// Every random decision in a bridge, keyed by WHAT it decides rather than
    /// by draw order: (world seed, crossing, bay, side, role). Adding a piece
    /// on one side therefore cannot reshuffle the damage on the other, a
    /// respawn reproduces the same ruin, and the two lanes of one bay are
    /// ruined independently because they are different keys.
    /// </summary>
    public readonly struct Ruin
    {
        private readonly int m_seed;
        public Ruin(int worldSeed, RoadCrossing crossing) => m_seed = worldSeed ^ StableSeed(crossing);

        public const int RoleStation = 1, RoleDeck = 2, RoleStair = 3, RoleStub = 4, RoleDebris = 5,
            RolePostHealth = 6, RoleBeamHealth = 7, RoleDeckHealth = 8, RoleStairHealth = 9, RoleColumnHealth = 10;
        public const int Left = -1, Centre = 0, Right = 1;

        /// <summary>A stable float in [0, 1).
        ///
        /// The four keys are folded into one 32-bit word with distinct odd
        /// multipliers and the word is finalised TWICE with murmur3's fmix32.
        /// A first version mixed once per key with an XOR, and two rolls that
        /// differed only in the role -- "does this station stand" and "does its
        /// ruin leave a stub" -- came out correlated strongly enough that every
        /// dead station in 100 seeds rolled a stub and none toppled. The roles
        /// have to be independent for the ruin to look like ruin.
        /// </summary>
        public float Roll(int bay, int side, int role, int extra = 0)
        {
            unchecked
            {
                uint h = (uint)m_seed * 0x9E3779B1u;
                h += (uint)bay * 0x85EBCA6Bu;
                h = Fmix(h);
                h += (uint)(side + 7) * 0xC2B2AE35u;
                h = Fmix(h);
                h += (uint)role * 0x27D4EB2Fu;
                h = Fmix(h);
                h += (uint)extra * 0x165667B1u;
                h = Fmix(Fmix(h));
                return (h >> 8) * (1f / 16777216f);
            }
        }

        public float Health(int bay, int side, int role, int extra = 0) =>
            RuinHealthMin + Roll(bay, side, role, extra) * (RuinHealthMax - RuinHealthMin);

        /// <summary>murmur3's 32-bit finaliser.</summary>
        private static uint Fmix(uint h)
        {
            unchecked
            {
                h ^= h >> 16; h *= 0x85EBCA6Bu;
                h ^= h >> 13; h *= 0xC2B2AE35u;
                h ^= h >> 16;
                return h;
            }
        }
    }

    // --------------------------------------------------------------- solve

    /// <summary>
    /// The bridge as it would stand with nothing missing: every station, every
    /// plate in both lanes, both stairs, the navigation gap filled in too.
    /// Diagnostic and test-only -- the game always builds the ruined plan --
    /// and the reference the repairability checks compare against. The
    /// fairway gap is included deliberately: it is not ruin, but it is a run
    /// of bays a player COULD fill, and if the grid did not continue across it
    /// they could not.
    /// </summary>
    internal static List<BridgePiece> SolveComplete(RoadCrossing crossing, WorldGenerator world, int worldSeed) =>
        Solve(crossing, world, worldSeed, ruin: false);

    public static List<BridgePiece> Solve(RoadCrossing crossing, WorldGenerator world, int worldSeed) =>
        Solve(crossing, world, worldSeed, ruin: true);

    private static List<BridgePiece> Solve(RoadCrossing crossing, WorldGenerator world, int worldSeed, bool ruin)
    {
        List<BridgePiece> pieces = new();
        if (crossing == null || world == null || crossing.Width < DeckSpan)
            return pieces;

        Ruin r = new(worldSeed, crossing);

        if (crossing.Kind == CrossingKind.Ford)
        {
            // Wading and raised fords are road, not pieces; a span is a short
            // low footbridge with steps.
            if (crossing.Style == FordStyle.Span)
                EmitShallowSpan(pieces, crossing, world, r, ruin);
            return pieces;
        }

        Vector2 from = crossing.FromBank;
        Vector2 dir = crossing.Direction;
        Vector2 side = new(-dir.y, dir.x);
        float yaw = YawDegrees(dir);
        float deckH = DeckHeight(crossing, world);

        // Short enough to hold itself up, over water deep enough that piers
        // are the wrong answer: abutments only, nothing in the channel.
        //
        // Both halves matter. Water a ford may not wade is what makes a pier
        // bad: it would stand in real water, tall and in the way. A short
        // crossing over a shallow bed keeps its piers, which look better and
        // give a player more to repair against, and the first version of this
        // rule took those away too -- a test that asks every deck plate to
        // rest on a beam at each end failed, correctly, on a short shallow
        // bridge that had just lost its middle.
        //
        // Depth rather than a SAILABLE fairway, deliberately. On one measured
        // world the two narrowest bridges were 20 m over water with no fairway at all: too
        // deep for a ford to wade, not deep enough to sail. Those are exactly
        // the channels not worth standing a pier in, and a fairway test misses
        // every one of them.
        bool freeSpan = IsFreeSpan(crossing, world);

        float[] alongs = StationsAlong(crossing.Width);
        int stationCount = alongs.Length;
        // The deck ends here, a remainder short of the far bank at most. Both
        // stairs are anchored at a DECK END so their top snap meets the deck
        // edge; anchoring the far one at the bank instead would leave exactly
        // the gap that anchoring here removes.
        Vector2 deckEnd = from + dir * alongs[stationCount - 1];
        bool[] stationAlive = new bool[stationCount];
        Vector2[] stationPos = new Vector2[stationCount];
        float mid = (stationCount - 1) * 0.5f;

        for (int i = 0; i < stationCount; i++)
        {
            float along = alongs[i];
            stationPos[i] = from + dir * along;

            // The navigation gap is intentional and permanent, not ruin -- but
            // it is still a section a player may fill in, so the complete
            // layout includes it: "fully repaired" means every bay on the grid.
            // A free span has no navigation gap to open: nothing of it stands
            // in the water, so the whole channel under it is already clear.
            bool inGap = ruin && !freeSpan && InNavigationGap(crossing, along);
            bool isBankStation = i == 0 || i == stationCount - 1;

            float midCloseness = mid > 0f ? 1f - Mathf.Abs(i - mid) / mid : 0f;
            float survival = Mathf.Lerp(BankSurvival, MidSurvival, midCloseness);
            float pierSurvival = survival + (1f - survival) * PierPersistence;
            stationAlive[i] = !inGap && (freeSpan
                ? isBankStation
                : !ruin || isBankStation || r.Roll(i, Ruin.Centre, Ruin.RoleStation) < pierSurvival);

            float ground = BiomeBlendedHeight.GetBlendedHeight(stationPos[i].x, stationPos[i].y, world);
            if (stationAlive[i])
            {
                EmitStation(pieces, world, stationPos[i], side, deckH, yaw, r, i);
            }
            // No stubs or debris under a free span. Both are the remains of a
            // pier, and this crossing never had one in the water -- leaving
            // them there tells the player a story about a structure that was
            // never built.
            else if (freeSpan)
            {
                // nothing: the channel stays clear
            }
            else if (ruin && !inGap && r.Roll(i, Ruin.Centre, Ruin.RoleStub) < StubChance)
            {
                // Rotted stub: a single buried segment poking out near the waterline.
                EmitColumn(pieces, stationPos[i], ground,
                    Mathf.Min(ground + PostSegment, crossing.WaterLevel + 0.3f), yaw,
                    0.25f + r.Roll(i, Ruin.Centre, Ruin.RoleStub, 1) * 0.15f);
            }
            else if (ruin && !inGap && r.Roll(i, Ruin.Centre, Ruin.RoleDebris) < DebrisChance)
            {
                EmitDebris(pieces, stationPos[i], dir, world, r, i);
            }
        }

        // A plate needs both of its stations standing, so every surviving
        // plate rests on a beam at each end. Beyond that the two lanes of a
        // bay are ruined on their own: left and right may each be present or
        // missing, and carry their own health.
        for (int i = 0; i + 1 < stationCount; i++)
        {
            // A plate normally needs both of its stations standing. A free
            // span has none in the middle by design, and its deck is carried
            // from the two ends instead.
            if (!freeSpan && (!stationAlive[i] || !stationAlive[i + 1]))
                continue;
            float midCloseness = mid > 0f ? 1f - Mathf.Abs(i + 0.5f - mid) / mid : 0f;
            float survival = Mathf.Lerp(BankSurvival, MidSurvival, midCloseness);
            bool endBay = i == 0 || i + 2 == stationCount;
            foreach (int lane in new[] { Ruin.Left, Ruin.Right })
            {
                if (ruin && !endBay && r.Roll(i, lane, Ruin.RoleDeck) >= survival)
                    continue;
                EmitDeck(pieces, stationPos[i], stationPos[i + 1], deckH,
                    side * (lane * LaneOffset), yaw, r.Health(i, lane, Ruin.RoleDeckHealth));
            }
        }

        // Every end is a stair down from the level deck into its bank, two
        // abreast like the deck: as many steps as that bank's drop needs.
        EmitSteps(pieces, world, from, dir, side, deckH, r, 0);
        EmitSteps(pieces, world, deckEnd, -dir, side, deckH, r, stationCount);

        return pieces;
    }

    /// <summary>
    /// Ford span: a continuous low deck from shore to shore, one station per
    /// DeckSpan with posts to the bed, deck at least FordSpanDeckClearance
    /// above the water and FordSpanDeckRise above the higher bank, lightly
    /// ruined (it is a footbridge, not a monument), a stair at each end. The
    /// same two-lane grid as a bridge: a footbridge a player cannot repair
    /// with an ordinary wood_floor is no better than one they cannot walk.
    /// </summary>
    private static void EmitShallowSpan(List<BridgePiece> pieces, RoadCrossing crossing, WorldGenerator world, Ruin r, bool ruin)
    {
        Vector2 from = crossing.FromBank;
        Vector2 to = crossing.ToBank;
        Vector2 dir = crossing.Direction;
        Vector2 side = new(-dir.y, dir.x);
        float yaw = YawDegrees(dir);

        float deckH = SpanDeckHeight(crossing, world);

        float[] alongs = StationsAlong(crossing.Width);   // the deck is level
        int stationCount = alongs.Length;
        Vector2 deckEnd = from + dir * alongs[stationCount - 1];
        bool[] alive = new bool[stationCount];
        Vector2[] pos = new Vector2[stationCount];
        for (int i = 0; i < stationCount; i++)
        {
            pos[i] = from + dir * alongs[i];
            bool isEnd = i == 0 || i == stationCount - 1;
            alive[i] = !ruin || isEnd || r.Roll(i, Ruin.Centre, Ruin.RoleStation) < BankSurvival;
            if (alive[i])
                EmitStation(pieces, world, pos[i], side, deckH, yaw, r, i);
        }
        for (int i = 0; i + 1 < stationCount; i++)
        {
            if (!alive[i] || !alive[i + 1])
                continue;
            foreach (int lane in new[] { Ruin.Left, Ruin.Right })
            {
                if (ruin && r.Roll(i, lane, Ruin.RoleDeck) >= BankSurvival)
                    continue;
                EmitDeck(pieces, pos[i], pos[i + 1], deckH, side * (lane * LaneOffset), yaw,
                    r.Health(i, lane, Ruin.RoleDeckHealth));
            }
        }

        EmitSteps(pieces, world, from, dir, side, deckH, r, 0);
        EmitSteps(pieces, world, deckEnd, -dir, side, deckH, r, stationCount);
    }

    // ---------------------------------------------------------------- emit

    /// <summary>A ford span's deck height: clear of the higher bank by
    /// FordSpanDeckRise and of the water by FordSpanDeckClearance.</summary>
    public static float SpanDeckHeight(RoadCrossing crossing, WorldGenerator world)
    {
        float bankFromH = BiomeBlendedHeight.GetBlendedHeight(crossing.FromBank.x, crossing.FromBank.y, world);
        float bankToH = BiomeBlendedHeight.GetBlendedHeight(crossing.ToBank.x, crossing.ToBank.y, world);
        return Mathf.Max(Mathf.Max(bankFromH, bankToH) + RoadConstants.FordSpanDeckRise,
            crossing.WaterLevel + RoadConstants.FordSpanDeckClearance);
    }

    /// <summary>One deck plate between two stations, offset to its lane. Its
    /// centre is the midpoint and it is LEVEL -- yaw only, the one orientation
    /// a player's hammer can reproduce -- so its two ends land on the stations
    /// that are DeckSpan apart.</summary>
    private static void EmitDeck(List<BridgePiece> pieces, Vector2 a, Vector2 b, float deckH, Vector2 laneOffset, float yaw, float health)
    {
        Vector2 mid2 = (a + b) * 0.5f + laneOffset;
        pieces.Add(new BridgePiece
        {
            Kind = BridgePieceKind.Deck,
            Prefab = DeckPrefab,
            Position = new Vector3(mid2.x, deckH, mid2.y),
            YawDegrees = yaw,
            HealthFraction = health,
        });
    }

    /// <summary>
    /// Steps down from a deck edge into the bank, two abreast: each step's
    /// top edge meets the previous one's foot (or the deck), and the stair
    /// marches OUTWARD until its FOOT EDGE is in the dirt.
    ///
    /// The foot edge -- the part a walker steps off onto -- is StairHalfRun
    /// outward of the step's origin and spans StairHalfWidth to either side,
    /// so it is not where the origin is. Sampling the origin ended a run with
    /// its centre on a shelf and its exit edge a metre in the air; both of a
    /// lane's foot corners have to be grounded, and each lane is asked
    /// separately because the bank falls away across the deck as well as
    /// along it. A step whose own origin is above the ground gets a post
    /// under it, so every step is carried by construction.
    ///
    /// Returns whether the run LANDED. It may not: MaxStairSteps of 2 m out
    /// and 1 m down cannot follow ground that falls faster than 1 in 2. The
    /// steps emitted in that case are still each supported to the ground --
    /// nothing floats -- but the walk stops above the dirt, and the caller
    /// is told so rather than the comment claiming every run reaches it.
    /// </summary>
    private static bool EmitSteps(List<BridgePiece> pieces, WorldGenerator world,
        Vector2 anchor, Vector2 inward, Vector2 side, float deckH, Ruin r, int bayKey)
    {
        float stepYaw = YawDegrees(inward) + 180f; // the stair prefab rises toward local -z
        float yaw = YawDegrees(inward);
        Vector2 outward = -inward;
        bool landed = false;
        for (int k = 0; k < MaxStairSteps && !landed; k++)
        {
            Vector2 c = anchor - inward * (StairHalfRun + k * StairRun);
            float foot = deckH - StairRise - k * StairRise;
            bool bothLanded = true;
            foreach (int lane in new[] { Ruin.Left, Ruin.Right })
            {
                Vector2 p = c + side * (lane * LaneOffset);
                float health = r.Health(bayKey, lane, Ruin.RoleStairHealth, k);
                pieces.Add(new BridgePiece
                {
                    Kind = BridgePieceKind.Stair,
                    Prefab = StairPrefab,
                    Position = new Vector3(p.x, foot, p.y),
                    YawDegrees = stepYaw,
                    HealthFraction = health,
                });
                // Two separate questions. Is the step itself carried? (if not,
                // a post carries it.) And has the run ARRIVED -- is the edge a
                // walker steps off onto in the dirt? A run ends only when both
                // are true of both lanes: the old rule asked the first alone
                // and stopped with the exit edge in the air.
                float under = BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
                bool centreGrounded = foot <= under + StairFootTolerance;
                if (!centreGrounded)
                    EmitColumn(pieces, p, under, foot, yaw, health);
                if (!centreGrounded || !FootEdgeIsGrounded(world, p, outward, side, foot))
                    bothLanded = false;
            }
            landed = bothLanded;
        }
        return landed;
    }

    /// <summary>Both corners of one step's foot edge, in the dirt.</summary>
    private static bool FootEdgeIsGrounded(WorldGenerator world, Vector2 stepPos, Vector2 outward, Vector2 side, float foot)
    {
        foreach (float lat in new[] { -StairHalfWidth, StairHalfWidth })
        {
            Vector2 corner = stepPos + outward * StairHalfRun + side * lat;
            if (foot > BiomeBlendedHeight.GetBlendedHeight(corner.x, corner.y, world) + StairFootTolerance)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether each of a crossing's two stair runs reaches the dirt, near end
    /// first. The answer is geometry, not ruin -- a missing step is a repair,
    /// a run that never lands is a crossing this layout cannot walk off --
    /// so the diagnostic reports it apart from the missing pieces.
    /// </summary>
    public static (bool near, bool far) StairRunsLand(RoadCrossing crossing, WorldGenerator world)
    {
        if (crossing == null || world == null || crossing.Width < DeckSpan)
            return (true, true);
        bool span = crossing.Kind == CrossingKind.Ford && crossing.Style == FordStyle.Span;
        if (crossing.Kind == CrossingKind.Ford && !span)
            return (true, true);   // a wading or raised ford is road, not pieces

        Vector2 dir = crossing.Direction;
        Vector2 side = new(-dir.y, dir.x);
        float deckH = span ? SpanDeckHeight(crossing, world) : DeckHeight(crossing, world);
        Vector2 deckEnd = crossing.FromBank + dir * BuiltLength(crossing.Width);
        Ruin r = new(0, crossing);   // ruin sets health, never a position
        List<BridgePiece> scratch = new();
        return (EmitSteps(scratch, world, crossing.FromBank, dir, side, deckH, r, 0),
                EmitSteps(scratch, world, deckEnd, -dir, side, deckH, r, 1));
    }

    /// <summary>One surviving station: two post pairs stacked down into the
    /// riverbed under the outer halves of the deck, tied by two crossbeams
    /// just under it -- one per lane, meeting at the centre seam.</summary>
    private static void EmitStation(List<BridgePiece> pieces, WorldGenerator world,
        Vector2 pos, Vector2 side, float deckH, float yaw, Ruin r, int bay)
    {
        float postTop = deckH - PostTopBelowDeck;
        foreach (float s in new[] { -PostSideOffset, PostSideOffset })
        {
            Vector2 postPos = pos + side * s;
            EmitColumn(pieces, postPos, BiomeBlendedHeight.GetBlendedHeight(postPos.x, postPos.y, world), postTop, yaw,
                r.Health(bay, s < 0 ? Ruin.Left : Ruin.Right, Ruin.RolePostHealth));
        }
        foreach (int lane in new[] { Ruin.Left, Ruin.Right })
        {
            Vector2 beamPos = pos + side * (lane * LaneOffset);
            pieces.Add(new BridgePiece
            {
                Kind = BridgePieceKind.Beam,
                Prefab = BeamPrefab,
                Position = new Vector3(beamPos.x, deckH - BeamBelowDeck, beamPos.y),
                YawDegrees = yaw, // the beam runs along its local x: across the deck
                HealthFraction = r.Health(bay, lane, Ruin.RoleBeamHealth),
            });
        }
    }

    /// <summary>A column of PostSegment poles from topHeight down until one is
    /// buried in the ground (grounded by construction).</summary>
    private static void EmitColumn(List<BridgePiece> pieces, Vector2 pos, float ground, float topHeight, float yaw, float health)
    {
        if (topHeight <= ground - PostSegment)
            return;
        for (float top = topHeight; ; top -= PostSegment)
        {
            float center = top - PostSegment * 0.5f;
            pieces.Add(new BridgePiece
            {
                Kind = BridgePieceKind.Post,
                Prefab = PostPrefab,
                Position = new Vector3(pos.x, center, pos.y),
                YawDegrees = yaw,
                HealthFraction = health,
            });
            if (center <= ground)
                break;
        }
    }

    /// <summary>A toppled pole settled into the bed, displaced to the side of
    /// the crossing line (never along it toward the fairway).</summary>
    private static void EmitDebris(List<BridgePiece> pieces, Vector2 station, Vector2 dir, WorldGenerator world, Ruin r, int bay)
    {
        Vector2 side = new(-dir.y, dir.x);
        float offset = 1f + r.Roll(bay, Ruin.Centre, Ruin.RoleDebris, 1) * 2f;
        if (r.Roll(bay, Ruin.Centre, Ruin.RoleDebris, 2) < 0.5f) offset = -offset;

        Vector2 pos = station + side * offset;
        float ground = BiomeBlendedHeight.GetBlendedHeight(pos.x, pos.y, world);
        pieces.Add(new BridgePiece
        {
            Kind = BridgePieceKind.Debris,
            Prefab = DebrisPrefab,
            Position = new Vector3(pos.x, ground + 0.2f, pos.y),
            YawDegrees = r.Roll(bay, Ruin.Centre, Ruin.RoleDebris, 3) * 360f,
            PitchDegrees = 50f + r.Roll(bay, Ruin.Centre, Ruin.RoleDebris, 4) * 70f, // toppled, not standing
            RollDegrees = r.Roll(bay, Ruin.Centre, Ruin.RoleDebris, 5) * 30f,
            HealthFraction = 0.2f + r.Roll(bay, Ruin.Centre, Ruin.RoleDebris, 6) * 0.2f,
        });
    }

    /// <summary>Unity yaw (degrees about +y) that turns local +z onto a world XZ direction.</summary>
    public static float YawDegrees(Vector2 dir) => Mathf.Atan2(dir.x, dir.y) * 180f / Mathf.PI;

    // ------------------------------------------------- placeable headings

    /// <summary>
    /// Vanilla's build rotation step (Player.m_placeRotationDegrees): the
    /// hammer turns a ghost only in these steps, and its copy-the-rotation-of-
    /// this-piece path rounds an existing piece's yaw to them too. Sixteen
    /// headings, and nothing between. A piece on any other heading is one no
    /// player can reproduce, which is why a crossing that carries pieces is
    /// laid out on one of these lines rather than on the road's own bearing.
    /// </summary>
    public const float PlaceableHeadingStep = 22.5f;
    public const int PlaceableHeadings = 16;

    /// <summary>The unit direction of a heading, inverse of YawDegrees.</summary>
    public static Vector2 HeadingDirection(float yawDegrees)
    {
        float rad = yawDegrees * Mathf.PI / 180f;
        return new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
    }

    public static float SnapHeadingDegrees(float yawDegrees) =>
        Mathf.Round(yawDegrees / PlaceableHeadingStep) * PlaceableHeadingStep;

    /// <summary>Whether a heading is one the hammer can produce. Rounding to
    /// the nearest step already puts the difference in +-half a step, so there
    /// is no wrap to handle.</summary>
    public static bool HeadingIsPlaceable(float yawDegrees, float toleranceDegrees = 0.05f) =>
        Mathf.Abs(yawDegrees - SnapHeadingDegrees(yawDegrees)) <= toleranceDegrees;

    /// <summary>The admissible headings for a bearing, NEAREST FIRST: the one
    /// it rounds to, then its two neighbours, and so on outward. A site whose
    /// nearest line does not reach land on both banks may still be crossable
    /// on the next one, which is the whole point of offering more than one --
    /// but the further the line turns, the further the bridgeheads walk along
    /// the shore from where routing put them, so the search is bounded by the
    /// caller and the nearest workable line always wins.</summary>
    public static IEnumerable<float> NearestPlaceableHeadings(float yawDegrees, int count = 3)
    {
        float nearest = SnapHeadingDegrees(yawDegrees);
        yield return nearest;
        for (int k = 1; k <= count / 2 && k * 2 + 1 <= PlaceableHeadings; k++)
        {
            yield return nearest - k * PlaceableHeadingStep;
            yield return nearest + k * PlaceableHeadingStep;
        }
    }

    /// <summary>Per-site seed from the crossing's centre, so a world regenerates with the same ruins.</summary>
    public static int StableSeed(RoadCrossing crossing)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + Mathf.RoundToInt(crossing.Center.x * 10f);
            h = h * 31 + Mathf.RoundToInt(crossing.Center.y * 10f);
            return h;
        }
    }
}
