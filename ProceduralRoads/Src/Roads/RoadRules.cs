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
    public const float WalkedFloor = RoadConstants.ShallowWaterHeight;

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

    /// <summary>Whether a shore gives a coastal landing (RoadCoastalLandings): no.</summary>
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

    /// <summary>Whether the rock clearing takes a natural boulder (RoadRockPolicy.ClearingTakes, CanClear): no.</summary>
    public const bool ClearsBoulders = false;

    /// <summary>Whether the rock clearing takes a Mistlands rock or cliff (RoadRockPolicy.ClearingTakes, CanClearMistlands): no.</summary>
    public const bool ClearsMistlandsRocks = false;

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

    /// <summary>The most a mound raises the ground under a viaduct's pier over DvergrViaduct.PierCeiling
    /// (BridgeSupport.MostMoundOf): 5.5 m, the maintainer's number (13 Nov 2026), so a pier up to about PierCeiling + 5.5 m
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
        /// <summary><see cref="RoadRules.ClearsBoulders"/> here.</summary>
        public readonly bool ClearsBoulders;
        /// <summary><see cref="RoadRules.ClearsMistlandsRocks"/> here.</summary>
        public readonly bool ClearsMistlandsRocks;
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
            bool clearsBoulders = RoadRules.ClearsBoulders, bool clearsMistlandsRocks = RoadRules.ClearsMistlandsRocks,
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
            ClearsBoulders = clearsBoulders;
            ClearsMistlandsRocks = clearsMistlandsRocks;
            ShapedFirst = shapedFirst;
            StartsIslandFirst = startsIslandFirst;
            Ridge = ridge;
        }
    }

    /// <summary>The global values, as one biome's.</summary>
    public static readonly Rules Global = new();

    // ---- Biome overrides: every one, here, beside the globals ----

    /// <summary>Meadows: a wooden bridge over a stone one; vines grow here.</summary>
    public static readonly Rules Meadows = new(coastLandings: true, stoneOverWood: false, vines: true, clearsBoulders: true);
    public static readonly Rules Plains = new(coastLandings: true, clearsBoulders: true);
    public static readonly Rules BlackForest = new(meander: 0.7f, coastLandings: true, clearsBoulders: true);
    /// <summary>Swamps: waded down to the deep line, a ford's bed too (with a pothole), at any depth; a bridge's banks walked
    /// out over the shelf, not moved off the land.</summary>
    public static readonly Rules Swamp = new(meander: 0.5f, wadesShallows: true, walkedFloor: RoadConstants.DeepWaterHeight,
        landingFloor: RoadConstants.DeepWaterHeight, fordsDeepBed: true, wadesAnyDepth: true, shelfReach: SwampShelfReach, bridgesOffLand: false,
        clearsBoulders: true);
    /// <summary>Mountains: stairs cheaper (they are wanted there most), no wander, steep ground refused over 0.4; a location is
    /// placed first and reached at its height or not at all (its approach staircase or its landing, no off-level fallback).</summary>
    public static readonly Rules Mountain = new(stairPrice: 0.5f, meander: 0f, steepGround: 0.4f, approachStairs: true, keepsOffLevel: false,
        alwaysPlacedFirst: true, clearsBoulders: true);
    public static readonly Rules DeepNorth = new(stairPrice: 0.5f, meander: 0.5f);
    public static readonly Rules AshLands = new(meander: 0.5f);
    /// <summary>Mistlands: the cliffs get short stair runs; no wander (its ground bends its roads on its own); a location's ring
    /// is reached at any height, every location is placed to aim at, one turned at random read for placing by its levelling;
    /// its shores give coastal landings; the black-marble and Dvergr kits belong here, vines grow, the earthwork faces carry
    /// its coarser detail, and the rock clearing takes its rocks and cliffs. A road with any point here is planned on its
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
        coastLandings: true, mistlandsKits: true, vines: true, faceDetailCoarse: 6f, faceDetailFine: 2f, clearsMistlandsRocks: true,
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
