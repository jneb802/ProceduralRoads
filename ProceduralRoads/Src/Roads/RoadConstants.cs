namespace ProceduralRoads;

/// <summary>
/// Centralized constants for the road generation system.
/// </summary>
public static class RoadConstants
{
    public const float ZoneSize = 64f;
    public const float HalfZoneSize = ZoneSize / 2f;
    public const float SeaLevel = 30f;
    /// <summary>No road on land (and no raised shallow ford) stands lower than this: a cut into
    /// ground at the water line would flood (measured: a swamp road cut 4.8 m into a
    /// hummock ran 105 m under up to 4 m of water). A constant floor never steepens a profile,
    /// so it keeps the grade cap.</summary>
    public const float DryRoadFloor = SeaLevel + 0.5f;
    public const float DeepWaterHeight = 28f;
    public const float ShallowWaterHeight = 30.5f;
    public const float TerrainDeltaMin = -8f;
    public const float TerrainDeltaMax = 8f;
    
    public const float PathfindingCellSize = 8f;
    public const int PathfindingMaxIterations = 30000;
    public const float TerrainVarianceSampleRadius = 16f;
    public const int TerrainVarianceSampleCount = 8;
    public const float MountainSlopeThreshold = 0.4f;
    public const float RiverImpassableThreshold = 0.5f;
    
    public const float DefaultBaseCost = 1f;
    public const float DefaultSlopeMultiplier = 10f;
    public const float DefaultRiverPenalty = 100000f;
    public const float DefaultWaterPenalty = 100000f;
    public const float DefaultSteepSlopePenalty = 2000f;
    public const float DefaultSteepSlopeThreshold = 0.6f;
    public const float DefaultTerrainVariancePenalty = 1000f;
    public const float DefaultTerrainVarianceThreshold = 5f;

    // River fords. With
    // fords on, the pathfinder may jump a river core in a straight line to
    // dry ground within MaxRiverCrossingCells (measured in metres, so a
    // diagonal scan does not stretch it) when the water under the jump is
    // no deeper than FordWadeDepth, at RiverCrossingPenalty on top of the
    // distance; banks may differ in height by MaxFordBankDelta, and a step
    // pays BankDeltaPenalty * delta^2 on top, so near-level banks are
    // preferred. The ford is WADED (painted at ground height, only where
    // the water is at most FordWadeMaxDepth deep, always in a swamp) or
    // RAISED (leveled up to the bank clearance), by the Fords/* weights.
    // Banks, landings and a raised ford's surface stand BankClearance above
    // the shallow-water line. Swamp roads wade their shallows down to
    // DeepWaterHeight at SwampShallowWaterPenalty per cell. A jump whose
    // both ends already carry road is an existing crossing: it costs
    // SharedCrossingCostFraction of the crossing price, so a later road
    // detours to share it when the detour costs less than the saving and
    // makes its own crossing beyond that.
    public const int MaxRiverCrossingCells = 6; // 6 * 8 m = 48 m max ford
    public const float RiverCrossingPenalty = 5000f;
    public const float FordWadeDepth = 0.8f;
    public const float FordWadeMaxDepth = 0.5f;
    // In swamps a sailable stretch shorter than a boat is a pothole, not a
    // fairway: a wading-depth swamp channel with no longer sailable stretch
    // is still a ford.
    public const float SwampFordMaxFairway = 8f;
    public const float MaxFordBankDelta = 4f;
    public const float BankDeltaPenalty = 1250f;
    public const float BankClearance = 0.75f;
    public const float DefaultFordStyleWeight = 1f;
    public const float DefaultSwampShallowWaterPenalty = 500f;
    public const float SharedCrossingCostFraction = 0.5f;

    // Bridges. A jump
    // longer than a ford or over water deeper than wading is a BRIDGE, up
    // to MaxBridgeCrossingCells (measured in metres), at BridgeCostFixed +
    // BridgeCostPerMeter per metre (config Bridges/CostFixed and
    // Bridges/CostPerMeter), between banks within MaxBridgeBankDelta of
    // each other. For scale in this cost model: easy ground costs about 1
    // per metre of road, rough or steep ground 1000-2000 per cell, so the
    // defaults make a 100 m bridge worth roughly 1 km of rough detour; a
    // bridge appears where the way around is long or there is none.
    public const int MaxBridgeCrossingCells = 16; // 16 * 8 m = 128 m, dry cell to dry cell
    public const float MaxBridgeBankDelta = 2.5f;
    public const float DefaultBridgeCostFixed = 60000f;
    public const float DefaultBridgeCostPerMeter = 600f;

    // A ford may also be SPANNED (Fords/SpanWeight): a short low footbridge
    // with a step at each end, where the crossing is at least
    // FordSpanMinWidth wide; its deck stands FordSpanDeckClearance above
    // the water and FordSpanDeckRise above the higher bank. A swamp BRIDGE
    // walks its banks outward over the wade shelf up to SwampBridgeDryReach
    // to find ground above the waterline.
    public const float FordSpanMinWidth = 6f;
    public const float FordSpanDeckClearance = 1f;
    public const float FordSpanDeckRise = 1f;
    public const float SwampBridgeDryReach = 120f;

    // High bridge: when the ground within HighBankReach of each bank along
    // the road stands at least HighBankRise above that bank, the deck
    // springs from the bank tops (abutments there, piers taller, the road
    // stopping at the top) instead of from the water's edge.
    public const float HighBankReach = 12f;
    public const float HighBankRise = 2.5f;

    // The tallest a wooden pier may stand above the channel bed. Not a taste
    // limit: vanilla's structural support decays geometrically up a column of
    // poles, and above this height the deck's support falls under the
    // material's minimum and the bridge collapses some time after it is built.
    // Derived from the decompiled rule in BridgeSupport rather than chosen, so
    // that a Valheim patch moves it by moving the material table.
    //
    // This is applied where the height is OPTIONAL. The bank-top climb is an
    // adjustment the crossing does not need - "the crossing is sound either
    // way" - so a climb that would put the deck out of the support rule's
    // reach is declined and the water's-edge deck stands instead. Lower deck,
    // longer approach, a bridge that is still there next week.
    public static float MaxBridgePierHeight => BridgeSupport.MaxPierHeight();
    // The steepest a road may climb, as rise over run, or 0 for no cap.
    // Without a cap every steep step is a large but finite price and never a
    // refusal, so when a destination sits on a cliff the cheapest expensive
    // line is the direct climb: RoadPathfinder refuses a step over the cap,
    // which leaves the search to traverse across the slope or fail, and
    // RoadGrade holds the stored height profile to it as well, because
    // smoothing and the endpoint ramp both move heights after the search.
    //
    // 0.35 is one metre up for every three along, about 19 degrees. It is the
    // knee of a sweep over one real world (RoadSeedE, 296 destinations): the
    // steepest road built falls from 962% to 35%, and one destination in 296
    // is lost for it. Below the knee the losses arrive quickly - 25% loses
    // thirteen, 20% loses twenty-one, 15% loses thirty-nine - while above it
    // 50% saves nothing more and leaves roads at half again the grade.
    public const float DefaultMaxRoadGrade = 0.35f;

    // Road cross-section (see RoadProfile): flat core fully leveled and
    // solidly painted; paint fades out strictly inside the leveled footprint
    // so roads keep an unpainted, smoothed verge; leveling eases to natural
    // terrain over TerrainBlendMargin beyond the half-width.
    public const float RoadFlatCoreRatio = 0.6f;
    public const float RoadPaintOuterRatio = 0.85f;

    // Terrain leveling fits a line along the road through the nearby road
    // points (see RoadTerrainModifier.CalculateBlendedHeight): the spread of
    // the prior that holds an undetermined gradient at zero, and the steepest
    // gradient the fit may report (metres per metre).
    public const float HeightFitRidgeMetres = 0.02f;
    public const float HeightFitMaxGradient = 1.5f;
    
    public const float SpatialGridSize = 64f;
    public const float DefaultRoadWidth = 4f;
    public const float EdgeFalloffStart = 0.6f;
    public const int HeightSmoothingWindow = 41;
    public const float OverlapThreshold = 0.3f;
    public const float OverlapSearchRadiusMultiplier = 0.6f;
    public const float OverlapBlendRadiusMultiplier = 0.8f;
    
    public const float TerrainBlendMargin = 2.0f;
    public const float PaintDedupeInterval = 1.5f;
    public const float MinHeightDeltaThreshold = 0.01f;
    public const float MinBlendForModification = 0.5f;
    
    public const float VegetationClearMultiplier = 0.6f;
    public const float VegetationClearSampleInterval = 4f;
    
    public const int MaxCoordDebugLogs = 3;
    public const int MaxVertexModificationLogs = 3;
}
