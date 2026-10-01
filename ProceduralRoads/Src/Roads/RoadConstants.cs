namespace ProceduralRoads;

/// <summary>
/// Centralized constants for the road generation system.
/// </summary>
public static class RoadConstants
{
    public const float ZoneSize = 64f;
    public const float HalfZoneSize = ZoneSize / 2f;
    public const float SeaLevel = 30f;
    public const float DeepWaterHeight = 28f;
    public const float ShallowWaterHeight = 30.5f;
    public const float TerrainDeltaMin = -8f;
    public const float TerrainDeltaMax = 8f;
    
    public const float PathfindingCellSize = 8f;
    public const int PathfindingMaxIterations = 10000;
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
