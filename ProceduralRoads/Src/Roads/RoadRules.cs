using System;
using UnityEngine;
using System.Collections.Generic;

namespace ProceduralRoads;

/// <summary>
/// The rule values a biome changes, in one place: each rule's global value, and beside them every biome's overrides, read
/// through <see cref="For"/> alone (docs/OWNERS.md; the owner's rule of 13 Nov 2026: "We should have defined values in one
/// place for our rules. Biomes can define overrides that work well in their local context. Those overrides should all be
/// defined in one place next to the global ones."). Which biome a place is in is asked of <see cref="RoadBiome"/>.
/// A rule that no biome changes keeps its value with its owner (docs/OWNERS.md).
/// </summary>
public static class RoadRules
{
    // ---- Global values: every biome's, unless its override below says otherwise ----

    /// <summary>How dear a staircase is, times the stair prices (RoadPathfinder's stair step). Stairs are a style of road,
    /// wanted more in the mountains than elsewhere, hence a price per biome.</summary>
    public const float StairPrice = 2f;

    /// <summary>The meander's and the sway's biome factor (RoadPathfinder.MeanderCost, RoadWiggle.Apply): 1, the whole wander.</summary>
    public const float Meander = 1f;

    /// <summary>The slope over which the search refuses a step at the water's price (RoadPathfinder's step price), on top of the
    /// grade cap: none.</summary>
    public const float SteepGround = float.PositiveInfinity;

    /// <summary>Whether ground under the shallow line is waded (RoadWater.Of: Waded, not Shallow): no.</summary>
    public const bool WadesShallows = false;

    /// <summary>The lowest ground the search walks (RoadWater.Floor): the shallow line.</summary>
    public const float WalkedFloor = RoadWater.ShallowLine;

    /// <summary>The lowest ground a road may stand on (RoadWater.LandingFloorFor, RoadWater.OnLand): the shallow line plus the
    /// bank clearance.</summary>
    public const float LandingFloor = RoadWater.LandingFloor;

    /// <summary>Whether a river ford may have a bed down to the deep line with a sailable stretch shorter than
    /// <see cref="PotholeFairway"/> (RoadCrossingDetector.IsFord): no, a ford's bed is at wading depth with nothing sailable.</summary>
    public const bool FordsDeepBed = false;

    /// <summary>A sailable stretch shorter than a boat is a pothole, not a fairway (RoadCrossingDetector.IsFord): a standing
    /// pool's ford anywhere, and a river ford where <see cref="FordsDeepBed"/>, may have one this short, metres.</summary>
    public const float PotholeFairway = 8f;

    /// <summary>Whether a river is waded at any depth its ford allows (RoadCrossingDetector.WadeEligibility): no, at most
    /// RoadConstants.FordWadeMaxDepth.</summary>
    public const bool WadesAnyDepth = false;

    /// <summary>How far a river bridge's wet bank is walked out over the shelf to dry ground (RoadCrossingDetector.ExtendOverShelf),
    /// metres: none.</summary>
    public const float ShelfReach = 0f;

    /// <summary>The swamp's <see cref="ShelfReach"/>, metres (read too by the saved crossings' longest span,
    /// RoadNetworkPersistence.MaxCrossingSpan).</summary>
    public const float SwampShelfReach = 120f;

    /// <summary>Whether a bridge's banks are moved off the land toward the water (RoadCrossingDetector.Build, OffLand): yes.</summary>
    public const bool BridgesOffLand = true;

    /// <summary>Whether a road that ends at a location may take a staircase onto it (RoadNetworkGenerator.ApproachStairsAt): no,
    /// it ends at the location's ring.</summary>
    public const bool ApproachStairs = false;

    /// <summary>Whether a vanilla location may fall back to a road ending off its level (RoadNetworkGenerator.OffLevelAllowed): yes.</summary>
    public const bool KeepsOffLevel = true;

    /// <summary>Whether an end kept at a location's ring reaches it at any height (RoadNetworkGenerator.RingLevel): no, within
    /// two junction steps of its landing.</summary>
    public const bool RingAnyHeight = false;

    /// <summary>Whether a chosen location is always placed before its road (PlacedLocations.NeedsPlacing): no, by its turn and
    /// levelling.</summary>
    public const bool AlwaysPlacedFirst = false;

    /// <summary>Whether a location turned at random, not to its slope, is still read for placing first by its levelling
    /// (PlacedLocations.NeedsPlacing): no.</summary>
    public const bool PlacedWithoutSlopeTurn = false;

    /// <summary>Whether a chosen location is placed only to aim its road at (PlacedLocations.AimsAt, while
    /// PlacedLocations.PlaceMistlandsDestinations): no.</summary>
    public const bool AimedAt = false;

    /// <summary>Whether a shore gives a coastal landing (RoadCoastalLandings): no, in every biome (off for the release; a setting
    /// for how many an island gets comes after it). The one place the search is switched.</summary>
    public const bool CoastLandings = false;

    /// <summary>Whether the black-marble and Dvergr kits belong here (StairKit.MistlandsKitBelongs, the share rule's
    /// "advanced" test for staircases and bridges): no.</summary>
    public const bool MistlandsKits = false;

    /// <summary>Whether a stone bridge belongs here over a wooden one (StairKit.StoneOverWoodBelongs): yes.</summary>
    public const bool StoneOverWood = true;

    /// <summary>Whether vanilla's vines grow here (StairSupport's vines; every location that hangs one stands in the Meadows or
    /// the Mistlands): no.</summary>
    public const bool Vines = false;

    /// <summary>The earthwork faces' coarse and fine detail, metres (RoadEarthworkNoise.Detail: vanilla's own).</summary>
    public const float FaceDetailCoarse = 2f, FaceDetailFine = 0.6f;

    /// <summary>Whether a searched road over standing water is shaped before its crossings are found
    /// (RoadNetworkGenerator.PlanCandidate's shaping first): yes, unless any point of it is where a biome says no.</summary>
    public const bool ShapedFirst = true;

    /// <summary>Whether an island's cells here count toward its start (RoadNetworkGenerator.MistlandsCells: the islands with the
    /// most such cells start first): no.</summary>
    public const bool StartsIslandFirst = false;

    // A stretch's ridge and trench values (RoadRidges.Find): RoadRidges.Depth over RoadRidges.MinLength, a trench at
    // RoadRidges.TrenchMinLength or RoadRidges.TrenchMinDepth, everywhere no biome gives its own RoadRidges.Override (Ridge).

    /// <summary>The Mistlands' ridge and trench depth (the Depth of its RoadRidges.Override, RoadRules.Mistlands' Ridge): a
    /// stretch there shorter than the override's long length counts only where it holds one deeper than this for
    /// RoadRidges.MinLength, metres. 4 m, the old RoadRidges.Depth (the owner's decision of 13 Nov 2026). Its own value:
    /// RoadRidges.TrenchMinDepth, the global depth at which a trench's worst point makes it one at any length, is a different
    /// rule that shares the number.</summary>
    public const float MistlandsRidgeDepth = 4f;

    /// <summary>How far along the road from a viaduct's end a Mistlands stretch is judged by its ridge override (the
    /// ViaductReach of its RoadRidges.Override, RoadRidges.Judged), metres: travelled along the road from a bank end of one
    /// of its own viaducts or of one already laid. A stretch further from every one is judged by the global rule. The
    /// owner's pick of 13 Nov 2026: infinite, the whole of a road whose route has a viaduct of its own (its crossings, one it
    /// rides included), at any distance; a laid viaduct elsewhere does not count, and a road without one of its own is judged
    /// by the global rule.</summary>
    public const float MistlandsRidgeViaductReach = float.PositiveInfinity;

    // ---- Mistlands viaducts (the Dvergr kit, built only where RoadBiome.Leans says Mistlands): values beside the globals
    // they replace ----

    /// <summary>The headings a valley viaduct is offered on (RoadPathfinder's valley probe): the grid's eight, the first eight
    /// of RoadPathfinder.Directions' sixteen, on which every water crossing is offered. On a knight's heading the cheaper
    /// jump was bought before the grid's at the same valley, its end was never met (none was built on two test worlds; the
    /// end rule's reasons: no way reached from the road, or none landed within the earthwork cap, though the site alone met
    /// it), and the searches again it cost, with the bans its neighbours fell under, left the grid's viaduct unbuilt.</summary>
    public const int ViaductHeadings = 8;

    /// <summary>Whether the search holds the turn onto a valley viaduct's jump to its turn limit (RoadPathfinder's
    /// ViaductTurnPrice; every other move, a water crossing's included, keeps the limit): no, the owner's decision of 13 Nov
    /// 2026. The jump pays the turn's price alone. A viaduct's end has a landing pad the road may leave at any heading, and
    /// whether the road reaches that end is the end rule's verdict (StructureEnds.MeetEnds); held to the limit as well, the
    /// search refused the turn onto a viaduct whose end the road met, arriving up a staircase along the rim.</summary>
    public const bool ViaductTurnLimit = false;

    // A mound's limit under a structure (StructureGround.MayRaise, through BridgeSupport.MostMoundOf, its one accessor): the
    // global BridgeSupport.MostMound, with its owner, for every kit but the Dvergr viaduct, whose override is below.

    /// <summary>The most a mound raises the ground under a viaduct's pier over its ceiling (DvergrViaduct.PierCeiling;
    /// BridgeSupport.MostMoundOf): 5.5 m, the maintainer's number (13 Nov 2026), so a pier up to about its ceiling + 5.5 m
    /// stands on its mound; the support rule still reads every pier on it (DvergrViaduct.Solve). It replaces the global
    /// BridgeSupport.MostMound (3 m), which every other structure keeps.</summary>
    public const float ViaductMostMound = 5.5f;

    /// <summary>One biome's values. Each parameter's default is its global value above; an override names only what it changes.</summary>
    public sealed class Rules
    {
        /// <summary><see cref="RoadRules.StairPrice"/> here.</summary>
        public readonly float StairPrice;
        /// <summary><see cref="RoadRules.Meander"/> here.</summary>
        public readonly float Meander;
        /// <summary><see cref="RoadRules.SteepGround"/> here.</summary>
        public readonly float SteepGround;
        /// <summary><see cref="RoadRules.WadesShallows"/> here.</summary>
        public readonly bool WadesShallows;
        /// <summary><see cref="RoadRules.WalkedFloor"/> here.</summary>
        public readonly float WalkedFloor;
        /// <summary><see cref="RoadRules.LandingFloor"/> here.</summary>
        public readonly float LandingFloor;
        /// <summary><see cref="RoadRules.FordsDeepBed"/> here.</summary>
        public readonly bool FordsDeepBed;
        /// <summary><see cref="RoadRules.WadesAnyDepth"/> here.</summary>
        public readonly bool WadesAnyDepth;
        /// <summary><see cref="RoadRules.ShelfReach"/> here.</summary>
        public readonly float ShelfReach;
        /// <summary><see cref="RoadRules.BridgesOffLand"/> here.</summary>
        public readonly bool BridgesOffLand;
        /// <summary><see cref="RoadRules.ApproachStairs"/> here.</summary>
        public readonly bool ApproachStairs;
        /// <summary><see cref="RoadRules.KeepsOffLevel"/> here.</summary>
        public readonly bool KeepsOffLevel;
        /// <summary><see cref="RoadRules.RingAnyHeight"/> here.</summary>
        public readonly bool RingAnyHeight;
        /// <summary><see cref="RoadRules.AlwaysPlacedFirst"/> here.</summary>
        public readonly bool AlwaysPlacedFirst;
        /// <summary><see cref="RoadRules.PlacedWithoutSlopeTurn"/> here.</summary>
        public readonly bool PlacedWithoutSlopeTurn;
        /// <summary><see cref="RoadRules.AimedAt"/> here.</summary>
        public readonly bool AimedAt;
        /// <summary><see cref="RoadRules.CoastLandings"/> here.</summary>
        public readonly bool CoastLandings;
        /// <summary><see cref="RoadRules.MistlandsKits"/> here.</summary>
        public readonly bool MistlandsKits;
        /// <summary><see cref="RoadRules.StoneOverWood"/> here.</summary>
        public readonly bool StoneOverWood;
        /// <summary><see cref="RoadRules.Vines"/> here.</summary>
        public readonly bool Vines;
        /// <summary><see cref="RoadRules.FaceDetailCoarse"/> here.</summary>
        public readonly float FaceDetailCoarse;
        /// <summary><see cref="RoadRules.FaceDetailFine"/> here.</summary>
        public readonly float FaceDetailFine;
        /// <summary><see cref="RoadRules.ShapedFirst"/> here.</summary>
        public readonly bool ShapedFirst;
        /// <summary><see cref="RoadRules.StartsIslandFirst"/> here.</summary>
        public readonly bool StartsIslandFirst;
        /// <summary>Its own ridge and trench values (RoadRidges.Find), null for the global ones.</summary>
        public readonly RoadRidges.Override? Ridge;

        public Rules(float stairPrice = RoadRules.StairPrice, float meander = RoadRules.Meander, float steepGround = RoadRules.SteepGround,
            bool wadesShallows = RoadRules.WadesShallows, float walkedFloor = RoadRules.WalkedFloor, float landingFloor = RoadRules.LandingFloor,
            bool fordsDeepBed = RoadRules.FordsDeepBed, bool wadesAnyDepth = RoadRules.WadesAnyDepth, float shelfReach = RoadRules.ShelfReach,
            bool bridgesOffLand = RoadRules.BridgesOffLand, bool approachStairs = RoadRules.ApproachStairs, bool keepsOffLevel = RoadRules.KeepsOffLevel,
            bool ringAnyHeight = RoadRules.RingAnyHeight, bool alwaysPlacedFirst = RoadRules.AlwaysPlacedFirst,
            bool placedWithoutSlopeTurn = RoadRules.PlacedWithoutSlopeTurn, bool aimedAt = RoadRules.AimedAt, bool coastLandings = RoadRules.CoastLandings,
            bool mistlandsKits = RoadRules.MistlandsKits, bool stoneOverWood = RoadRules.StoneOverWood, bool vines = RoadRules.Vines,
            float faceDetailCoarse = RoadRules.FaceDetailCoarse, float faceDetailFine = RoadRules.FaceDetailFine,
            bool shapedFirst = RoadRules.ShapedFirst, bool startsIslandFirst = RoadRules.StartsIslandFirst, RoadRidges.Override? ridge = null)
        {
            StairPrice = stairPrice;
            Meander = meander;
            SteepGround = steepGround;
            WadesShallows = wadesShallows;
            WalkedFloor = walkedFloor;
            LandingFloor = landingFloor;
            FordsDeepBed = fordsDeepBed;
            WadesAnyDepth = wadesAnyDepth;
            ShelfReach = shelfReach;
            BridgesOffLand = bridgesOffLand;
            ApproachStairs = approachStairs;
            KeepsOffLevel = keepsOffLevel;
            RingAnyHeight = ringAnyHeight;
            AlwaysPlacedFirst = alwaysPlacedFirst;
            PlacedWithoutSlopeTurn = placedWithoutSlopeTurn;
            AimedAt = aimedAt;
            CoastLandings = coastLandings;
            MistlandsKits = mistlandsKits;
            StoneOverWood = stoneOverWood;
            Vines = vines;
            FaceDetailCoarse = faceDetailCoarse;
            FaceDetailFine = faceDetailFine;
            ShapedFirst = shapedFirst;
            StartsIslandFirst = startsIslandFirst;
            Ridge = ridge;
        }
    }

    /// <summary>The global values, as one biome's.</summary>
    public static readonly Rules Global = new();

    // ---- Biome overrides: every one, here, beside the globals ----

    /// <summary>Meadows: a wooden bridge over a stone one; vines grow here.</summary>
    public static readonly Rules Meadows = new(stoneOverWood: false, vines: true);
    public static readonly Rules Plains = new();
    public static readonly Rules BlackForest = new(meander: 0.7f);
    /// <summary>Swamps: waded down to the deep line, a ford's bed too (with a pothole), at any depth; a bridge's banks walked
    /// out over the shelf, not moved off the land.</summary>
    public static readonly Rules Swamp = new(meander: 0.5f, wadesShallows: true, walkedFloor: RoadWater.DeepLine,
        landingFloor: RoadWater.DeepLine, fordsDeepBed: true, wadesAnyDepth: true, shelfReach: SwampShelfReach, bridgesOffLand: false);
    /// <summary>Mountains: stairs cheaper (they are wanted there most), no wander, steep ground refused over 0.4; a location is
    /// placed first and reached at its height or not at all (its approach staircase or its landing, no off-level fallback).</summary>
    public static readonly Rules Mountain = new(stairPrice: 0.5f, meander: 0f, steepGround: 0.4f, approachStairs: true, keepsOffLevel: false,
        alwaysPlacedFirst: true);
    public static readonly Rules DeepNorth = new(stairPrice: 0.5f, meander: 0.5f);
    public static readonly Rules AshLands = new(meander: 0.5f);
    /// <summary>Mistlands: the cliffs get short stair runs; no wander (its ground bends its roads on its own); a location's ring
    /// is reached at any height, every location is placed to aim at, one turned at random read for placing by its levelling;
    /// the black-marble and Dvergr kits belong here, vines grow, the earthwork faces carry
    /// its coarser detail. A road with any point here is planned on its
    /// searched line alone, never shaped first (its ground bends its roads, and its valley roads are planned once); islands
    /// with the most of it start first. Its ridges and trenches (the owner's decision of 13 Nov 2026: 4 m ones are allowed as
    /// long as they are not long): a stretch shorter than RoadRidges.TrenchMinLength counts only where it is deeper than 4 m,
    /// the old RoadRidges.Depth (every road was judged at 4 m until 12 Nov 2026 put it at a player's height, and there the
    /// player's-height ridges took about half the road the Mistlands had); "long" is the length past which a trench is one at
    /// any depth, for ridges too: the shortest of 15, 16, 20 and 24 m that gained the Mistlands' road back, measured on two
    /// worlds. Its scope (owner-approved, 13 Nov 2026): a stretch with any one point in the Mistlands is judged by it
    /// (RoadRidges.Find), so one at a border lying mostly in another biome is too; and a part of a short stretch deeper than
    /// MistlandsRidgeDepth is allowed, at any depth the earthwork cap allows, while that part is shorter than
    /// RoadRidges.MinLength (fine for now).</summary>
    public static readonly Rules Mistlands = new(stairPrice: 1f, meander: 0f, ringAnyHeight: true, placedWithoutSlopeTurn: true, aimedAt: true,
        mistlandsKits: true, vines: true, faceDetailCoarse: 6f, faceDetailFine: 2f,
        shapedFirst: false, startsIslandFirst: true, ridge: new RoadRidges.Override(depth: MistlandsRidgeDepth, longLength: RoadRidges.TrenchMinLength,
            viaductReach: MistlandsRidgeViaductReach));
    public static readonly Rules Ocean = new(meander: 0f);
    /// <summary>No biome (no world, or a value that is not one biome): no wander.</summary>
    public static readonly Rules Unlisted = new(meander: 0f);

    /// <summary>The values in <paramref name="biome"/>: the one reader of the overrides.</summary>
    public static Rules For(Heightmap.Biome biome) => biome switch
    {
        Heightmap.Biome.Meadows => Meadows,
        Heightmap.Biome.Plains => Plains,
        Heightmap.Biome.BlackForest => BlackForest,
        Heightmap.Biome.Swamp => Swamp,
        Heightmap.Biome.Mountain => Mountain,
        Heightmap.Biome.DeepNorth => DeepNorth,
        Heightmap.Biome.AshLands => AshLands,
        Heightmap.Biome.Mistlands => Mistlands,
        Heightmap.Biome.Ocean => Ocean,
        _ => Unlisted,
    };
}

/// <summary>
/// The rule values a Location changes, in one place, as <see cref="RoadRules"/> holds a biome's: each rule's global value, and
/// beside it the exceptions, keyed by the Location's prefab name, read through <see cref="For"/> alone (docs/OWNERS.md; the
/// owner's decision of 18 Nov 2026: "per location settings, we could have a default, then a catch for exceptions"). A Location
/// not listed takes the globals. An exception is a line in <see cref="Exceptions"/>; a new per-Location rule is a field of
/// <see cref="Rules"/> with its global beside the others.
/// </summary>
public static class LocationRules
{
    // ---- Global values: every Location's, unless its exception below says otherwise ----

    /// <summary>Whether a staircase may serve the Location: yes. Two readers: an approach staircase from a road's end up onto its landing,
    /// on every rung of the arrival ladder (RoadNetworkGenerator.ApproachStairsAt), and a road's own staircase ending within
    /// <see cref="RoadStairRuns.LocationReach"/> of the Location the road ends at (RoadNetworkGenerator.RoadRefusal, via
    /// <see cref="NoStaircaseEndingAt"/>). Where no, its roads still end on its landing or at its ring as any road does; a through road to
    /// another destination may climb a staircase past it. Only Locations placed first are covered by the road-staircase check (a known,
    /// rare cut: one not placed first is judged by ApproachStairsAt alone).</summary>
    public const bool Staircase = true;

    /// <summary>Whether a Location is entered only from its front: no. Its readers: <c>PlacedLocationShape</c> (the entrance side, the
    /// ring behind it, a landing's door) and <c>PlacedLocations</c> (placing it first). Where yes, its back is the blocked rock or wall
    /// of the building; its landing is already in front, and a road that got no further than the ring reaches it on its entrance side
    /// only, not anywhere round it.</summary>
    public const bool FrontOnly = false;

    /// <summary>Whether a Location's mouth is cut into a rock that is part of the prefab itself, so no boulder of its body stands for it:
    /// no. Where yes, its way in comes from the entrance side near the landing (<c>PlacedLocationShape.FrontLaneOnly</c>), not from the
    /// side, where a road ran in under a troll cave's overhang to its mouth.</summary>
    public const bool CutIntoRock = false;

    /// <summary>Whether a Location is reached only by a road onto its own landing, not by one passing within reach of it: no. Its
    /// readers are the callers of <c>RoadNetworkGenerator.OwnLandingOnly</c> (what counts as reached, its approach staircase, the
    /// off-level second attempt, placing it first).</summary>
    public const bool OwnLandingOnly = false;

    /// <summary>The Location pieces a way clears out of its own path, by name prefix (a Dvergr stake wall's stakes, dvergrprops_wood_stake
    /// and the like): the same in every Location, so a piece rule and not a per-prefab one. The rock pass removes, whole, every one a
    /// walk's clearance box meets; every other piece is kept off by the walker's own body. Read through <see cref="ClearedForAWay"/>.</summary>
    public const string ClearedPiecePrefix = "dvergrprops_wood_stake";

    /// <summary>The name part that marks a boss's entrance, in any case (a custom name too): every such prefab takes
    /// <see cref="BossEntrance"/>'s values, whatever its full name. Matched only in <see cref="For"/>.</summary>
    public const string BossEntranceMatch = "BossEntrance";

    /// <summary>One Location's values. Each parameter's default is its global value above; an exception names only what it
    /// changes.</summary>
    public sealed class Rules
    {
        /// <summary><see cref="LocationRules.Staircase"/> here.</summary>
        public readonly bool Staircase;

        /// <summary><see cref="LocationRules.FrontOnly"/> here.</summary>
        public readonly bool FrontOnly;

        /// <summary><see cref="LocationRules.CutIntoRock"/> here.</summary>
        public readonly bool CutIntoRock;

        /// <summary><see cref="LocationRules.OwnLandingOnly"/> here.</summary>
        public readonly bool OwnLandingOnly;

        public Rules(bool staircase = LocationRules.Staircase, bool frontOnly = LocationRules.FrontOnly,
            bool cutIntoRock = LocationRules.CutIntoRock, bool ownLandingOnly = LocationRules.OwnLandingOnly)
        {
            Staircase = staircase;
            FrontOnly = frontOnly;
            CutIntoRock = cutIntoRock;
            OwnLandingOnly = ownLandingOnly;
        }
    }

    /// <summary>The global values, as one Location's.</summary>
    public static readonly Rules Global = new();

    /// <summary>A troll cave's mouth and the bear cave's, the same cave (their prefabs carry the same six terrain modifiers, its
    /// landing a 4 m flat before the mouth under the overhang): no staircase. The owner's decision of 18 Nov 2026 ("disallow
    /// stairs to troll caves. It just looks bad, it never looked good, the landing is too small"); losing some is accepted.</summary>
    private static readonly Rules SmallCaveMouth = new(staircase: false);

    /// <summary>A troll cave (its overhang over its mouth, read off the placed game with only its treasure chest in its footprint):
    /// no staircase, and its mouth is cut into rock.</summary>
    private static readonly Rules TrollCave = new(staircase: false, cutIntoRock: true);

    /// <summary>A frost cave: its mouth is cut into rock too (its staircase rule is the global).</summary>
    private static readonly Rules FrostCave = new(cutIntoRock: true);

    /// <summary>A Dvergr town entrance: a mine shaft (Mistlands_DvergrTownEntrance1, its gate on the prefab's +z) and the same building
    /// as the Queen's entrance (Mistlands_DvergrTownEntrance2, its stairs up to the door on +z). Entered only from the front; the back
    /// is the blocked rock or wall of the building.</summary>
    private static readonly Rules DvergrTownEntrance = new(frontOnly: true);

    /// <summary>A boss's entrance: reached only on its own landing. Not listed in <see cref="Exceptions"/>: <see cref="For"/> matches
    /// <see cref="BossEntranceMatch"/> in the name.</summary>
    internal static readonly Rules BossEntrance = new(ownLandingOnly: true);

    // ---- Exceptions: every one, here, beside the globals ----

    /// <summary>The Locations whose values are not the globals, by prefab name (exact, as the game names it).</summary>
    internal static readonly IReadOnlyDictionary<string, Rules> Exceptions = new Dictionary<string, Rules>
    {
        ["TrollCave02"] = TrollCave,
        ["BearCave"] = SmallCaveMouth,
        ["MountainCave02"] = FrostCave,
        ["Mistlands_DvergrTownEntrance1"] = DvergrTownEntrance,
        ["Mistlands_DvergrTownEntrance2"] = DvergrTownEntrance,
    };

    /// <summary>Exceptions a test adds over <see cref="Exceptions"/> (null in a game). Settable for tests.</summary>
    internal static IReadOnlyDictionary<string, Rules>? TestExceptions { get; set; }

    /// <summary>The values at the Location <paramref name="prefab"/> (null where not known: the globals): the one reader of the
    /// exceptions.</summary>
    public static Rules For(string? prefab) =>
        prefab == null ? Global
        : TestExceptions != null && TestExceptions.TryGetValue(prefab, out Rules? t) ? t
        : Exceptions.TryGetValue(prefab, out Rules? r) ? r
        : prefab.IndexOf(BossEntranceMatch, StringComparison.OrdinalIgnoreCase) >= 0 ? BossEntrance : Global;

    /// <summary>Whether a Location's own built piece named <paramref name="name"/> is one a road or staircase clears out of its way
    /// (<see cref="ClearedPiecePrefix"/>): the one reader of the prefix (<c>LocationRock.ClearedForAWay</c> asks it).</summary>
    public static bool ClearedForAWay(string? name) => name != null && name.StartsWith(ClearedPiecePrefix, StringComparison.Ordinal);

    /// <summary>Why no staircase serves the Location <paramref name="prefab"/> by its rule (<see cref="Rules.Staircase"/>), for the
    /// roads log; null where one may.</summary>
    public static string? NoStaircase(string? prefab) => For(prefab).Staircase ? null : $"no staircase to {prefab} (rule)";

    /// <summary>Whether a staircase of a road the road lays itself (not one an earlier road laid) ends at the Location the road ends at, where
    /// that Location's rule says none serves it: either end of the staircase within <see cref="RoadStairRuns.LocationReach"/>, the one reach
    /// a staircase is counted to serve a Location by, of one of the road's <paramref name="ends"/> (its end Locations placed first; a
    /// through road to another destination may climb past the Location). The reason for the roads log and where, null where none.</summary>
    internal static string? NoStaircaseEndingAt(IEnumerable<RoadStairSpan> stairs, IEnumerable<PlacedLocationRecord> ends, out Vector2 at)
    {
        foreach (PlacedLocationRecord record in ends)
        {
            if (For(record.Prefab).Staircase) continue;
            foreach (RoadStairSpan stair in stairs)
            {
                if (stair.Existing) continue;
                foreach (Vector2 end in new[] { stair.FromPoint, stair.ToPoint })
                    if ((end - record.Centre).magnitude <= RoadStairRuns.LocationReach) { at = end; return NoStaircase(record.Prefab); }
            }
        }
        at = default;
        return null;
    }
}
