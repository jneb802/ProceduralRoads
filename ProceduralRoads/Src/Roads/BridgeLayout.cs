using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The wooden kits' shared rules at a recorded crossing, which the stone kit fills too. Pure logic; the game places the
/// planned pieces later. The kits themselves are the railed kits (<see cref="RailedKit"/>, every biome but the Mistlands),
/// the stone kit (<see cref="StoneBay"/>, from the Black Forest on) and the Dvergr wood kit (<see cref="DvergrWoodBridge"/>);
/// <see cref="BridgeStructures.KitFor"/> chooses.
///
/// A wooden bridge is a row of stations every <see cref="DeckSpan"/> metres from
/// bank to bank. THE DECK IS LEVEL: a wood_floor is placed by the vanilla
/// hammer at Quaternion.Euler(0, yaw, 0) -- yaw only, and the snap path
/// moves the ghost, never turns it -- so a pitched plate is one no player
/// could ever put back. The deck therefore sits at ONE height, the higher
/// bank's (both ends already lifted to the water plus a freeboard), and the
/// drop to the lower bank is walked off by that end's stair run, which is
/// as many snapped steps as the bank needs (<see cref="EmitSteps"/>). Ruin is
/// deterministic per (crossing, world seed) (<see cref="Ruin"/>) and one
/// profile for every kit (<see cref="BridgeRuin"/>), and a navigation gap over
/// a river is always left open so boats still pass. A ford gets no pieces unless it is
/// spanned: a low footbridge (<see cref="SpanDeckHeight"/>).
/// </summary>
public static partial class BridgeLayout
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
    public const int MaxStairSteps = 12;      // a stair marches outward until its foot is in the dirt, at most this far
    public const float StairRun = 2f;         // one step: 2 m out, 1 m down
    public const float StairRise = 1f;
    public const float StairHalfRun = 1f;     // origin at the foot: its LOW snaps are this far OUTWARD
    public const float StairHalfWidth = 1f;   // ... and this far to either side
    public const float StairFootTolerance = 0.15f; // a foot this far above the dirt still counts as landed

    /// <summary>Bumped whenever the emitted geometry changes shape. Persisted
    /// with the spawned-zone record, so a world built by an older layout is
    /// recognised on load and its pieces are replaced rather than mixed with
    /// new ones (see RoadNetworkPersistence.ReadBridgeZones).
    ///
    /// <para>4: the navigation gap narrowed from 20 m to 16 m, which moves the
    /// emitted shape of every bridge over a sailable channel, and free spans
    /// changed it again for narrow ones. Without this bump a world explored
    /// partly under layout 3 would carry both gap widths side by side.</para>
    ///
    /// <para>5: a Mistlands crossing whose deck stands 6 m or more over the
    /// ground or water is a Dvergr viaduct (<see cref="DvergrViaduct"/>), not
    /// a wooden bridge. A world explored partly under layout 4 keeps the wooden
    /// pieces it has; only zones without pieces get the viaduct's.</para>
    ///
    /// <para>6: a viaduct's deck height and end stairs come from the road's arrival (<see cref="StructureEnds"/>),
    /// its navigation bay is two vanilla bays wide, and its ruin is calibrated on the vanilla viaduct. A world explored
    /// partly under layout 5 keeps the pieces it has.</para>
    ///
    /// <para>7: a viaduct's ruin halfway back toward layout 5's, every piece damaged like vanilla's locations, and
    /// its ends met by the road as a stretch of road the structure replaces (StructureEnds). A world explored partly under
    /// layout 6 keeps the pieces it has.</para>
    ///
    /// <para>8: a Mistlands bridge under the viaduct floor is built from the Dvergr harbour pier's wooden
    /// pieces (<see cref="DvergrWoodBridge"/>), not the plain wooden kit. A world explored partly under layout 7 keeps the
    /// pieces it has.</para>
    ///
    /// <para>9: the Dvergr wood kit on the pieces' measured sizes (planks and stringers 0.8 m
    /// deep, piles centred), damaged like the vanilla harbour, on the Mistlands' ford spans
    /// too, its stair posts in the Dvergr pile, and no lanterns (the harbour's hang from its buildings). A world
    /// explored partly under layout 8 keeps the pieces it has.</para>
    ///
    /// <para>10: both Dvergr kits checked against the vanilla prefabs: the
    /// Dvergr wood stringers 0.3 m up into the planks as the harbour's, the viaduct's piece sizes measured, a piece's roll
    /// spawned; and the first flair. A world explored partly under layout 9 keeps the pieces it has.</para>
    ///
    /// <para>11: a Meadows, Black Forest, Swamp or Plains bridge whose columns stand within the wood support ceiling is built
    /// in one of the railed wooden kits (<see cref="RailedKit"/>), half and half. A crossing saved with its kit keeps it, and a world explored
    /// partly under layout 10 keeps the pieces it has.</para>
    ///
    /// <para>12: every bridge and every ford's span outside the Mistlands is built in a railed wooden kit: the plain wooden
    /// kit is gone. A free span's railed deck is carried bank to bank, and a station whose column would stand over the wood
    /// support ceiling has none. A railed kit's navigation gap is whole bays of its own grid. A world explored partly under
    /// layout 11 keeps the pieces it has.</para>
    ///
    /// <para>13: a bridge or a ford's span outside the Meadows and the Mistlands that the stone kit fits
    /// (<see cref="StoneFits"/>) may be built in it, at even odds with the railed kits. A crossing saved with its kit keeps it,
    /// and a world explored partly under layout 12 keeps the pieces it has.</para>
    /// </summary>
    public const int LayoutVersion = 13;
    public const float DeckFreeboard = 0.5f;  // deck at least this far above the water
    /// <summary>How far under the walking surface a wooden deck's beams reach, and as far again their depth
    /// (<see cref="DeckDepth"/>).</summary>
    public const float BeamBelowDeck = 0.13f;

    // The base curve of the one ruin profile (BridgeRuin): survival falls from BankSurvival at the banks to MidSurvival at
    // mid-span; a column outlives the deck by PierPersistence, and a fallen one leaves a stub or debris by StubChance /
    // DebrisChance.
    public const float BankSurvival = 0.85f;
    public const float MidSurvival = 0.4f;
    public const float PierPersistence = 0.85f;
    public const float StubChance = 0.5f;
    public const float DebrisChance = 0.5f;
    /// <summary>The health range <see cref="Ruin.Health"/> draws a piece's from before the ruin wears it as a location's
    /// (<see cref="Ruin.ApplyLocationDamage"/>); the complete plan keeps it.</summary>
    public const float RuinHealthMin = 0.05f;
    public const float RuinHealthMax = 0.70f;

    /// <summary>The base curve's survival at a point of a span: <see cref="BankSurvival"/> at the banks, falling to
    /// <see cref="MidSurvival"/> at mid-span. <paramref name="midCloseness"/> is 0 at a bank and 1 at mid-span. Each part
    /// takes its share of the losses this leaves (<see cref="RuinProfile.Survives"/>).</summary>
    public static float SpanSurvival(float midCloseness) => BridgeRuin.Span(midCloseness);

    /// <summary>A column's survival where the base curve's is <paramref name="survival"/>: columns outlive the deck.</summary>
    public static float PierSurvival(float survival) => BridgeRuin.Pier(survival);

    /// <summary>The health a vanilla location's piece is placed with, as a fraction of its prefab's: ZoneSystem.SpawnLocation
    /// sets WearNTear.m_randomInitialDamage for a location with Location.m_applyRandomDamage (the Mistlands viaducts and
    /// harbours have it), and WearNTear.Awake then puts every piece at Random.Range(0.1, 0.6) of its health (decompiled
    /// game, WearNTear.cs 322-325, ZoneSystem.cs ~2828). Read on a Mistlands_Harbour1 in
    /// game (cli_piece_health on 40 pieces): 10-57 %, median 35 %.</summary>
    public const float LocationDamageMin = 0.1f, LocationDamageMax = 0.6f;

    /// <summary>
    /// How a bridge ruins: one profile for every kit (<see cref="BridgeRuin"/>), by the part a piece is, not by the kit. The
    /// rolls are <see cref="Ruin"/>'s, keyed by what they decide; the profile only says the rates.
    ///
    /// <para>The base curve is <see cref="Span"/> (15 % lost at a bank, 60 % at mid-span, 37.5 % on average), and each part
    /// takes a share of its losses (<see cref="Survives"/>). The shares are read off the vanilla Mistlands_Viaduct1 (24
    /// modules), which vanilla ruins: 3 of 48 iron floors missing (6 %), 21 of 69 deck course blocks (30 %), 21 of 46 arches
    /// (46 %), and one whole module open, a 2 m gap a player jumps.</para>
    /// </summary>
    public sealed class RuinProfile
    {
        /// <summary>The base curve's survival at a bank and at mid-span (<see cref="Span"/>).</summary>
        public readonly float BankSurvival, MidSurvival;
        /// <summary>How much a column outlives the base curve (<see cref="Pier"/>).</summary>
        public readonly float PierPersistence;
        /// <summary>Where a column fell, the chance it leaves a stub, else a piece of debris on the bed.</summary>
        public readonly float StubChance, DebrisChance;
        /// <summary>The deck: planks, floors, iron.</summary>
        public readonly float DeckShare;
        /// <summary>The railing and the courses along the deck's edges.</summary>
        public readonly float CourseShare;
        /// <summary>What stands under the deck between its columns or piers: arches, braces.</summary>
        public readonly float SupportShare;
        /// <summary>A whole bay open from side to side; never two running, so every gap is one a player jumps.</summary>
        public readonly float HoleShare;

        public RuinProfile(float bankSurvival, float midSurvival, float pierPersistence, float stubChance, float debrisChance,
            float deckShare, float courseShare, float supportShare, float holeShare)
        {
            BankSurvival = bankSurvival; MidSurvival = midSurvival; PierPersistence = pierPersistence;
            StubChance = stubChance; DebrisChance = debrisChance;
            DeckShare = deckShare; CourseShare = courseShare; SupportShare = supportShare; HoleShare = holeShare;
        }

        /// <summary>The base curve's survival at a point of a span, 0 at a bank, 1 at mid-span.</summary>
        public float Span(float midCloseness) => Mathf.Lerp(BankSurvival, MidSurvival, midCloseness);
        /// <summary>A column's survival where the base curve's is <paramref name="survival"/>.</summary>
        public float Pier(float survival) => survival + (1f - survival) * PierPersistence;
        /// <summary>A part's survival where the base curve's is <paramref name="spanSurvival"/>, at its
        /// <paramref name="share"/> of the losses.</summary>
        public static float Survives(float spanSurvival, float share) => 1f - (1f - spanSurvival) * share;
    }

    // Too many missing blocks looked wrong on an 80 m viaduct, so the course and support shares are halfway between a light
    // ruin (course 0.15, about 4 % gone; arches 0.5, about 19 %; no whole module open) and a heavy one (course 0.8, 30 %;
    // arches 1.2, 45 %; holes 0.1). The deck was the same in both.
    /// <summary>
    /// The ruin every kit gets: the deck about 6 % gone on average (vanilla's viaduct 6 %), the railing and edge courses
    /// about 17 %, the supports under the deck about 32 %, and about 2 % of bays open whole, never two running. The ends,
    /// their stairs and footing and the bank bays always stand; a fallen column leaves a stub or debris; every piece left is
    /// worn as a vanilla location's (<see cref="LocationDamageMin"/> to <see cref="LocationDamageMax"/>,
    /// <see cref="Ruin.ApplyLocationDamage"/>), and every piece left rests on a neighbour's snap points: where the rolls would
    /// leave one with none, a neighbour is put back rather than the piece dropped. A kit departs from it only where it says
    /// why.
    /// </summary>
    public static readonly RuinProfile BridgeRuin = new(BankSurvival, MidSurvival, PierPersistence, StubChance, DebrisChance,
        deckShare: 0.15f, courseShare: 0.45f, supportShare: 0.85f, holeShare: 0.05f);

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
    /// <para>The navigation gap over a river (<see cref="Ships.NavigationGapWidth"/>,
    /// a longship's hull, or its yard at the yard's height, and room to line up)
    /// stays within it. A free-spanned
    /// bridge needs no navigation gap at all -- there is nothing standing in
    /// the water to open.</para>
    /// </summary>
    public const float FreeSpanMaxWidth = 16f;
    /// <summary>Clearance kept each side of the navigation gap: no piece stands closer to it.</summary>
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
    /// commoner one: over a river the boat lane covers the whole crossing, so there is
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
        return NavigationGap(crossing, world) is ShipWay way
            && way.PierWidth + FairwayClearance * 2f >= crossing.Width;
    }

    /// <summary>The lowest a deck stands over water at <paramref name="waterLevel"/>: the water plus
    /// <see cref="DeckFreeboard"/>, and a step (<see cref="StairRise"/>) over the floor every road on land is built up to
    /// (RoadConstants.DryRoadFloor), so a low deck's end can take a step down to the road
    /// (StructureEnds.OverTheFloor).</summary>
    public static float MinDeck(float waterLevel) => Mathf.Max(waterLevel + DeckFreeboard, RoadConstants.DryRoadFloor + StairRise);

    /// <summary>Deck height where it meets each bank: the bank's ground,
    /// lifted to <see cref="MinDeck"/> where the ground is lower.</summary>
    public static (float from, float to) DeckEndHeights(RoadCrossing crossing, WorldGenerator world)
    {
        float minDeck = MinDeck(crossing.WaterLevel);
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

    /// <summary>Whether a point this far along the crossing lies in the ship's way through the deck: the deck's gap
    /// (<see cref="ShipWay.DeckWidth"/>) and the <see cref="FairwayClearance"/> each side of it, which every kit's open bays
    /// hold. Not ruin -- the diagnostic reports the two separately.</summary>
    public static bool InNavigationGap(RoadCrossing crossing, WorldGenerator world, float along) =>
        InNavigationGap(NavigationGap(crossing, world), along);

    /// <summary><see cref="InNavigationGap(RoadCrossing, WorldGenerator, float)"/> with the
    /// crossing's <see cref="NavigationGap"/> already found (null: no gap): within the deck's gap
    /// and the <see cref="FairwayClearance"/> each side of it. None under a deck the ship passes under.</summary>
    public static bool InNavigationGap(ShipWay? way, float along) =>
        way is ShipWay w && w.DeckWidth > 0f && Mathf.Abs(along - w.Centre) <= w.DeckWidth * 0.5f + FairwayClearance;

    /// <summary>Whether a point this far along the crossing lies in the way kept free of piers
    /// (<see cref="ShipWay.PierWidth"/>) and the <see cref="FairwayClearance"/> each side of it.</summary>
    public static bool InPierOpening(ShipWay? way, float along) =>
        way is ShipWay w && Mathf.Abs(along - w.Centre) <= w.PierWidth * 0.5f + FairwayClearance;

    /// <summary>
    /// The bays a kit leaves open over the ship's way: the first and how many, of <paramref name="bays"/> equal bays of
    /// <paramref name="bay"/> metres along the deck from its near end, the supports standing at their ends. The fewest whole
    /// bays whose opening holds the way kept free of piers (<see cref="ShipWay.PierWidth"/>, which the deck's gap is within)
    /// and the <see cref="FairwayClearance"/> each side, clear of what reaches <paramref name="reach"/> past the supports
    /// either side; the run nearest the way's centre, never a bank's bay where a middle run fits. The one owner of how a
    /// kit's grid covers the ship's way: the way is the crossing's (<see cref="NavigationChannel"/>), the bays the kit's.
    /// </summary>
    public static (int first, int count) OpenBays(ShipWay way, float bay, int bays, float reach)
    {
        int count = Mathf.Min(bays, Mathf.CeilToInt((way.PierWidth + 2f * FairwayClearance + 2f * reach) / bay - 1e-4f));
        int lo = 1, hi = bays - 1 - count;
        if (hi < lo) (lo, hi) = (0, bays - count);
        return (Mathf.Clamp(Mathf.RoundToInt((way.Centre - count * bay * 0.5f) / bay), lo, hi), count);
    }

    /// <summary>Where a ship's way under a bridge lies: its middle, metres along the crossing from
    /// the near bank, the deck missing over it (0 when the ship passes under the deck) and the way
    /// kept free of piers (<see cref="Ships.NavigationGapWidth"/>).</summary>
    public readonly struct ShipWay
    {
        public readonly float Centre, DeckWidth, PierWidth;

        public ShipWay(float centre, float deckWidth, float pierWidth)
        {
            Centre = centre;
            DeckWidth = deckWidth;
            PierWidth = pierWidth;
        }
    }

    /// <summary>A wooden deck from its walking surface to its underside, as the ship's way measures it: beams
    /// <see cref="BeamBelowDeck"/> under the surface and as deep again under that, deeper than the railed kits' stringers
    /// (<see cref="RailHalfDepth"/> under it), so a ship that clears it clears them.</summary>
    public const float DeckDepth = 2f * BeamBelowDeck;

    /// <summary>The wooden deck's ship's way: <see cref="NavigationChannel"/> for its deck
    /// (<see cref="DeckHeight"/>, <see cref="DeckDepth"/>) and the railed kits' handrail over it.</summary>
    public static ShipWay? NavigationGap(RoadCrossing crossing, WorldGenerator world)
    {
        if (crossing == null || world == null || crossing.Kind != CrossingKind.Bridge) return null;
        float deck = DeckHeight(crossing, world);
        return NavigationChannel(crossing, world, deck - DeckDepth, deck, BridgeStructures.Kit.WoodRail);
    }

    /// <summary>Spacing of the pass along a crossing reading its river weight and its depth.</summary>
    private const float ChannelSampleSpacing = 1f;

    /// <summary>
    /// The ship's way under a bridge whose deck has its <paramref name="underside"/> and walking
    /// <paramref name="surface"/> at these heights, in <paramref name="kit"/>, whose structure stands
    /// <see cref="BridgeStructures.TopOverDeck"/> over the surface: where it lies along the crossing and how wide it
    /// is, or null when the bridge leaves no navigation gap. The one owner of whether and where every
    /// kit opens one: only a river, as the world generator lays it (its river weight), is sailed
    /// through; a lake, a swamp pool or a sea inlet gets a continuous deck and piers however deep it
    /// is. How wide, in the deck and between the piers, is <see cref="Ships.NavigationGapWidth"/> for
    /// the structure's heights over the water, from the deck's underside to its top: a deck high enough that the ship passes under it
    /// (<see cref="Ships.Clears"/>) is continuous, its piers still clear of the hull's way.
    ///
    /// <para><b>The river's part</b> of the crossing: sampled every metre, the longest run of samples
    /// that are both river (<see cref="RoadWater.River"/>) and water a ship floats in
    /// (<see cref="RoadCrossingDetector.FairwayMinDepth"/>). The weight alone is not enough: it can pass
    /// the river line over a bank the terrain lifted out of the water, and a gap there opens the deck
    /// over dry ground. A crossing whose river part is narrower than the narrowest way a ship asks
    /// (<see cref="Ships.NarrowestWay"/>) crosses no river a ship sails.</para>
    ///
    /// <para><b>Where in it:</b> over the deepest water a ship's way wide: the window whose SHALLOWEST water is deepest, since a
    /// ship grounds on the shallowest water it crosses, not on the average; then the deeper on average;
    /// then the nearer the river's centreline (the weight's peak, where a bed carved evenly is
    /// deepest). Depth is the water level over the ground the crossing measured its bed on
    /// (<see cref="BiomeBlendedHeight"/>). A river part narrower than the ship's way centres the gap on
    /// it. Read from the world, which is fixed per seed, so a saved crossing needs nothing new.</para>
    /// </summary>
    public static ShipWay? NavigationChannel(RoadCrossing crossing, WorldGenerator world, float underside, float surface, BridgeStructures.Kit kit)
    {
        if (crossing == null || world == null || crossing.Kind != CrossingKind.Bridge || crossing.Width <= 0f)
            return null;
        float structureTop = surface + BridgeStructures.TopOverDeck(kit);
        var (deckWidth, shipWay) = Ships.NavigationGapWidth(underside - crossing.WaterLevel, structureTop - crossing.WaterLevel);
        Vector2 At(float along) => crossing.FromBank + crossing.Direction * along;
        float Weight(float along)
        {
            Vector2 p = At(along);
            world.GetRiverWeight(p.x, p.y, out float weight, out _);
            return weight;
        }
        int samples = Mathf.Max(1, Mathf.CeilToInt(crossing.Width / ChannelSampleSpacing));
        float step = crossing.Width / samples;
        var weights = new float[samples + 1];
        var depth = new float[samples + 1];
        int first = -1, last = -2;
        for (int i = 0, run = -1; i <= samples; i++)
        {
            weights[i] = Weight(i * step);
            Vector2 p = At(i * step);
            depth[i] = crossing.WaterLevel - BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
            if (!RoadWater.River(weights[i]) || depth[i] < RoadCrossingDetector.FairwayMinDepth)
            {
                run = -1;
                continue;
            }
            if (run < 0) run = i;
            if (i - run > last - first) (first, last) = (run, i);
        }
        if ((last - first) * step < Ships.NarrowestWay)
            return null;
        if ((last - first) * step < shipWay)
            return new ShipWay((first + last) * 0.5f * step, deckWidth, shipWay);

        // The centreline: the middle of a run of equal highest weights, or a single peak narrowed to its place.
        int peak = first;
        for (int i = first; i <= last; i++) if (weights[i] > weights[peak]) peak = i;
        float centreline;
        int top = peak;
        while (top < last && weights[top + 1] >= weights[peak] - 1e-5f) top++;
        if (top > peak)
            centreline = (peak + top) * 0.5f * step;
        else
        {
            float lo = Mathf.Max(0f, (peak - 1) * step), hi = Mathf.Min(crossing.Width, (peak + 1) * step);
            for (int k = 0; k < 24; k++)
            {
                float x = lo + (hi - lo) / 3f, y = hi - (hi - lo) / 3f;
                if (Weight(x) < Weight(y)) lo = x; else hi = y;
            }
            centreline = (lo + hi) * 0.5f;
        }

        // Slide a ship's way along the river's part to its deepest water.
        float from = first * step + shipWay * 0.5f, to = last * step - shipWay * 0.5f;
        float best = from, bestMin = float.MinValue, bestMean = float.MinValue;
        void Try(float centre)
        {
            float min = float.MaxValue, sum = 0f;
            int count = 0;
            int i0 = Mathf.Max(first, Mathf.CeilToInt((centre - shipWay * 0.5f) / step - 1e-4f));
            int i1 = Mathf.Min(last, Mathf.FloorToInt((centre + shipWay * 0.5f) / step + 1e-4f));
            for (int i = i0; i <= i1; i++)
            {
                min = Mathf.Min(min, depth[i]);
                sum += depth[i];
                count++;
            }
            if (count == 0) return;
            float mean = sum / count;
            const float same = 1e-3f;
            bool better = min > bestMin + same
                || (min >= bestMin - same && (mean > bestMean + same
                    || (mean >= bestMean - same && Mathf.Abs(centre - centreline) < Mathf.Abs(best - centreline))));
            if (!better) return;
            best = centre;
            bestMin = min;
            bestMean = mean;
        }
        for (float c = from; c < to; c += step) Try(c);
        Try(to);
        if (centreline > from && centreline < to) Try(centreline);
        return new ShipWay(best, deckWidth, shipWay);
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
    // The railed kits' (read off the vanilla prefabs' snappoint children, ValheimRIP):
    //   wood_pole     snaps top and bottom           (0, +-0.5, 0)
    //   wood_beam_26  snaps LOW (-1, 0, 0), HIGH (1, 1, 0)
    //   wood_beam_67  snaps LOW (-1, 0, 0), HIGH (1, 4, 0)
    private static readonly Vector3[] ShortPoleSnaps = { new(0, 0.5f, 0), new(0, -0.5f, 0) };
    private static readonly Vector3[] StairRailSnaps = { new(-1, 0, 0), new(1, 1, 0) };
    private static readonly Vector3[] BraceSnaps = { new(-1, 0, 0), new(1, 4, 0) };
    // The stone kit's (read off the vanilla prefabs' snappoint children, ValheimRIP):
    //   stone_floor_2x2  snaps at its eight corners          (+-1, +-0.5, +-1)
    //   stone_wall_2x1   snaps at its eight corners          (+-1, +-0.5, +-0.5)
    //   stone_arch       snaps at its tall end, top and bottom (1, +-0.5, +-0.5), on its crown (-1, 0.5, +-0.5)
    //   stone_stair      snaps HIGH (+-1, 1, -1), LOW (+-1, 0, 1), under its top edge (+-1, 0, -1)
    //   stone_pillar     snaps at its eight corners          (+-0.5, +-1, +-0.5)
    private static readonly Vector3[] StoneFloorSnaps = Corners(1f, 0.5f, 1f);
    private static readonly Vector3[] StoneBlockSnaps = Corners(1f, 0.5f, 0.5f);
    private static readonly Vector3[] StoneArchSnaps =
        { new(1, 0.5f, 0.5f), new(1, 0.5f, -0.5f), new(1, -0.5f, 0.5f), new(1, -0.5f, -0.5f), new(-1, 0.5f, 0.5f), new(-1, 0.5f, -0.5f) };
    private static readonly Vector3[] StoneStairSnaps = { new(-1, 1, -1), new(1, 1, -1), new(-1, 0, 1), new(1, 0, 1), new(-1, 0, -1), new(1, 0, -1) };
    private static readonly Vector3[] StonePillarSnaps = Corners(0.5f, 1f, 0.5f);

    /// <summary>A box's eight corners, half its size along each axis from its centre.</summary>
    private static Vector3[] Corners(float x, float y, float z)
    {
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++)
            corners[i] = new Vector3((i & 1) == 0 ? -x : x, (i & 2) == 0 ? -y : y, (i & 4) == 0 ? -z : z);
        return corners;
    }

    /// <summary>
    /// A piece's snap points in the world, from the prefab's measured local
    /// snaps and the rotation the game applies when spawning it
    /// (Quaternion.Euler(pitch, yaw, roll), which Unity composes as
    /// yaw about y, then pitch about x, then roll about z, applied to the
    /// local vector in the order roll, pitch, yaw). This is what "two pieces
    /// meet" means for a player: a snap of one lands on a snap of the other.
    /// </summary>
    public static Vector3[] SnapPoints(StructurePiece piece)
    {
        Vector3[] local = piece.Prefab switch
        {
            DeckPrefab => FloorSnaps,
            BeamPrefab => BeamSnaps,
            StairPrefab or DvergrWoodBridge.StairPrefab => StairSnaps,
            PostPrefab => PoleSnaps,
            RailPostPrefab => ShortPoleSnaps,
            StairRailPrefab => StairRailSnaps,
            BracePrefab => BraceSnaps,
            StoneFloorPrefab => StoneFloorSnaps,
            StonePierBlock => StoneBlockSnaps,
            StoneArchPrefab => StoneArchSnaps,
            StoneStairPrefab => StoneStairSnaps,
            StonePillarPrefab => StonePillarSnaps,
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

        /// <summary>Its own role, apart from every kit's (wood 1-10, Dvergr wood 11-14, railed 15-19 and 32-34, viaduct 21-28,
        /// stone 29-31).</summary>
        private const int RoleLocationHealth = 35;

        /// <summary>
        /// Every piece damaged as vanilla damages a location's pieces (<see cref="LocationDamageMin"/> to
        /// <see cref="LocationDamageMax"/> of its health), fixed per crossing and world seed: the roll is keyed by the piece's
        /// place and prefab, not drawn from Unity's random state, so the server and every reload agree. Every kit's ruin
        /// damages its pieces here (<see cref="BridgeRuin"/>).
        /// </summary>
        public void ApplyLocationDamage(List<StructurePiece> pieces)
        {
            foreach (StructurePiece p in pieces)
            {
                int key = unchecked(Mathf.RoundToInt(p.Position.x * 2f) * 73856093 ^ Mathf.RoundToInt(p.Position.z * 2f) * 19349663);
                int side = Mathf.RoundToInt(p.Position.y * 2f);
                int extra = StableHash(p.Prefab) ^ Mathf.RoundToInt(p.YawDegrees);
                p.HealthFraction = LocationDamageMin + Roll(key, side, RoleLocationHealth, extra) * (LocationDamageMax - LocationDamageMin);
            }
        }

        /// <summary>A hash of a prefab name that is the same in every process (string.GetHashCode is not on .NET Core).</summary>
        private static int StableHash(string? s)
        {
            unchecked
            {
                int h = 17;
                if (s != null) foreach (char ch in s) h = h * 31 + ch;
                return h;
            }
        }

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

    // ---------------------------------------------------------------- emit

    /// <summary>A ford span's deck height: clear of the higher bank by
    /// FordSpanDeckRise and of the water by FordSpanDeckClearance, and no lower
    /// than any deck (<see cref="MinDeck"/>).</summary>
    public static float SpanDeckHeight(RoadCrossing crossing, WorldGenerator world)
    {
        float bankFromH = BiomeBlendedHeight.GetBlendedHeight(crossing.FromBank.x, crossing.FromBank.y, world);
        float bankToH = BiomeBlendedHeight.GetBlendedHeight(crossing.ToBank.x, crossing.ToBank.y, world);
        return Mathf.Max(Mathf.Max(bankFromH, bankToH) + RoadConstants.FordSpanDeckRise,
            Mathf.Max(crossing.WaterLevel + RoadConstants.FordSpanDeckClearance, MinDeck(crossing.WaterLevel)));
    }

    /// <summary>A wooden kit's walking surface: a ford span's footbridge (<see cref="SpanDeckHeight"/>), else the bridge's
    /// (<see cref="DeckHeight"/>).</summary>
    public static float WoodDeckHeight(RoadCrossing crossing, WorldGenerator world) =>
        crossing.Kind == CrossingKind.Ford ? SpanDeckHeight(crossing, world) : DeckHeight(crossing, world);

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
    ///
    /// <para><paramref name="carry"/> false: no post under a step; the caller carries the steps its own way (the railed
    /// kits on columns under the stair's edges, <see cref="RailedKit"/>).</para>
    /// <para>The kits lay their end stairs through <see cref="LayEndStairs"/>, never here: <paramref name="count"/> null
    /// marches the run as the bank takes it (<see cref="NaturalEnds"/> asks so), else lays that many.</para>
    /// <para><paramref name="ground"/>: the ground the steps stand on, the generator's where the ends are decided, the
    /// finished ground (<see cref="StructureGround"/>) where they are laid.</para>
    /// </summary>
    private static bool EmitSteps(List<StructurePiece> pieces, System.Func<Vector2, float> ground,
        Vector2 anchor, Vector2 inward, Vector2 side, float deckH, Ruin r, int bayKey,
        string stairPrefab = StairPrefab, string postPrefab = PostPrefab, int? count = null, float postSegment = PostSegment,
        bool carry = true)
    {
        float stepYaw = YawDegrees(inward) + 180f; // the stair prefab rises toward local -z
        float yaw = YawDegrees(inward);
        Vector2 outward = -inward;
        bool landed = false;
        System.Func<int, int, float> vertex = (x, z) => ground(new Vector2(x, z));
        // A run of a given count (StructureEnds decided it; the road's apron is levelled to its foot) is laid whole, but a
        // step wholly in the ground (BuriedPieces), which the game breaks.
        for (int k = 0; count.HasValue ? k < count.Value : k < MaxStairSteps && !landed; k++)
        {
            Vector2 c = anchor - inward * (StairHalfRun + k * StairRun);
            float foot = deckH - StairRise - k * StairRise;
            bool bothLanded = true;
            foreach (int lane in new[] { Ruin.Left, Ruin.Right })
            {
                Vector2 p = c + side * (lane * LaneOffset);
                float health = r.Health(bayKey, lane, Ruin.RoleStairHealth, k);
                var at = new Vector3(p.x, foot, p.y);
                if (!count.HasValue || !BuriedPieces.Buried(stairPrefab, at, stepYaw, vertex))
                    pieces.Add(new StructurePiece
                    {
                        Kind = StructurePieceKind.Stair,
                        Prefab = stairPrefab,
                        Position = at,
                        YawDegrees = stepYaw,
                        HealthFraction = health,
                    });
                // Two separate questions. Is the step itself carried? (if not,
                // a post carries it.) And has the run ARRIVED -- is the edge a
                // walker steps off onto in the dirt? A run ends only when both
                // are true of both lanes: the old rule asked the first alone
                // and stopped with the exit edge in the air.
                float under = ground(p);
                bool centreGrounded = foot <= under + StairFootTolerance;
                if (!centreGrounded && carry)
                    EmitColumn(pieces, p, under, foot, yaw, health, postPrefab, postSegment);
                if (!centreGrounded || !FootEdgeIsGrounded(ground, p, outward, side, foot))
                    bothLanded = false;
            }
            landed = bothLanded;
        }
        return count.HasValue || landed;
    }

    /// <summary>
    /// The one way a structure's end stairs are laid, by every kit (the railed and Dvergr wood kits, the viaduct, a ford's
    /// span): from the end as the road meets it (<see cref="StructureEnds.Of"/>, decided by StructureEnds.MeetEnds),
    /// its <see cref="StructureEnds.End.Steps"/> down from its edge along its line at its height: the whole run where the
    /// road comes to the run's foot, down to the row the road lands on, none where it meets the end level. The generator,
    /// the paint rule (<see cref="StructureEnds.Unpainted"/>) and the repair command read the same end, so they see the
    /// stairs laid. The kit supplies only its pieces: <paramref name="stairPrefab"/>, and a column of
    /// <paramref name="postPrefab"/> under a step over the ground (<paramref name="carry"/> false: none, the kit carries
    /// its steps its own way), down to <paramref name="ground"/>: the finished ground (<see cref="StructureGround"/>), so a
    /// step on the road's landing or apron needs no column, and one over ground the road cut away has its column carried down
    /// to it. Returns the steps laid.
    /// </summary>
    internal static int LayEndStairs(List<StructurePiece> pieces, System.Func<Vector2, float> ground, StructureEnds.End end, Vector2 side,
        Ruin r, int bayKey, string stairPrefab = StairPrefab, string postPrefab = PostPrefab, float postSegment = PostSegment,
        bool carry = true)
    {
        if (!end.IsSet || end.Steps <= 0) return 0;
        EmitSteps(pieces, ground, end.Edge, end.Up, side, end.Height, r, bayKey, stairPrefab, postPrefab, end.Steps, postSegment, carry);
        return end.Steps;
    }

    /// <summary>The generator's ground, which a structure's ends are decided on (the road's levelling is made from them).</summary>
    internal static System.Func<Vector2, float> Generator(WorldGenerator world) => p => BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);

    /// <summary>Both corners of one step's foot edge, in the dirt.</summary>
    private static bool FootEdgeIsGrounded(System.Func<Vector2, float> ground, Vector2 stepPos, Vector2 outward, Vector2 side, float foot)
    {
        foreach (float lat in new[] { -StairHalfWidth, StairHalfWidth })
        {
            Vector2 corner = stepPos + outward * StairHalfRun + side * lat;
            if (foot > ground(corner) + StairFootTolerance)
                return false;
        }
        return true;
    }

    /// <summary>
    /// The two ends of a bridge or a ford's spanned footbridge in every kit but the viaduct, as the bank takes them
    /// (StructureEnds.Natural), near end first: each stair run from its deck end as it marches until its foot lands
    /// (<see cref="EmitSteps"/>). The road meets them as the one owner decides (StructureEnds.MeetEnds), which may lay
    /// fewer. Unset for a span too short for a deck.
    /// </summary>
    internal static (StructureEnds.End near, StructureEnds.End far) NaturalEnds(RoadCrossing crossing, WorldGenerator world)
    {
        bool span = crossing != null && crossing.Kind == CrossingKind.Ford && crossing.Style == FordStyle.Span;
        if (crossing == null || world == null || !BridgeStructures.HasDeck(crossing) || crossing.Width < DeckSpan) return default;
        Vector2 dir = crossing.Direction, side = new(-dir.y, dir.x);
        float deckH = span ? SpanDeckHeight(crossing, world) : DeckHeight(crossing, world);
        Vector2 deckEnd = crossing.FromBank + dir * BuiltLength(crossing.Width);
        Ruin r = new(0, crossing);
        StructureEnds.End End(Vector2 anchor, Vector2 inward)
        {
            var scratch = new List<StructurePiece>();
            EmitSteps(scratch, Generator(world), anchor, inward, side, deckH, r, 0);
            int steps = scratch.FindAll(p => p.Kind == StructurePieceKind.Stair).Count / 2;
            return new StructureEnds.End(anchor, inward, deckH, steps);
        }
        return (End(crossing.FromBank, dir), End(deckEnd, -dir));
    }

    /// <summary>
    /// Whether each of a crossing's two stair runs reaches the dirt, near end
    /// first. The answer is geometry, not ruin -- a missing step is a repair,
    /// a run that never lands is a crossing this layout cannot walk off --
    /// so the diagnostic reports it apart from the missing pieces. An end the
    /// road met (<see cref="RoadCrossing.FromEnd"/>) lands on the road.
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
        Ruin r = new(0, crossing);   // ruin sets health, never a position
        List<StructurePiece> scratch = new();
        var (near, far) = StructureEnds.Of(crossing, world);
        bool Lands(StructureEnds.End end, bool met) =>
            met || EmitSteps(scratch, Generator(world), end.Edge, end.Up, side, end.Height, r, 0);
        return (Lands(near, crossing.FromEnd != null), Lands(far, crossing.ToEnd != null));
    }

    /// <summary>A column of <paramref name="segment"/>-long posts (origin at their centre) from topHeight down until one
    /// is buried in the ground (grounded by construction).
    /// <para><paramref name="toFoot"/>: stop instead at the first post whose foot is in the ground (within
    /// <see cref="BuriedPieces.Growth"/> of it, where the game grounds it), and lay none where the top already is; the
    /// railed kits' columns (<see cref="RailedKit"/>) stand on the ground as the hand-built reference's do.</para></summary>
    private static void EmitColumn(List<StructurePiece> pieces, Vector2 pos, float ground, float topHeight, float yaw, float health,
        string postPrefab = PostPrefab, float segment = PostSegment, bool toFoot = false)
    {
        if (toFoot ? topHeight <= ground + BuriedPieces.Growth : topHeight <= ground - segment)
            return;
        for (float top = topHeight; ; top -= segment)
        {
            float center = top - segment * 0.5f;
            pieces.Add(new StructurePiece
            {
                Kind = StructurePieceKind.Post,
                Prefab = postPrefab,
                Position = new Vector3(pos.x, center, pos.y),
                YawDegrees = yaw,
                HealthFraction = health,
            });
            if (toFoot ? top - segment <= ground + BuriedPieces.Growth : center <= ground)
                break;
        }
    }

    /// <summary>A toppled pole settled into the bed, displaced to the side of
    /// the crossing line (never along it toward the fairway), on <paramref name="groundAt"/>, the ground the kit is laid
    /// on.</summary>
    private static void EmitDebris(List<StructurePiece> pieces, Vector2 station, Vector2 dir, System.Func<Vector2, float> groundAt, Ruin r, int bay)
    {
        Vector2 side = new(-dir.y, dir.x);
        float offset = 1f + r.Roll(bay, Ruin.Centre, Ruin.RoleDebris, 1) * 2f;
        if (r.Roll(bay, Ruin.Centre, Ruin.RoleDebris, 2) < 0.5f) offset = -offset;

        Vector2 pos = station + side * offset;
        float ground = groundAt(pos);
        pieces.Add(new StructurePiece
        {
            Kind = StructurePieceKind.Debris,
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
