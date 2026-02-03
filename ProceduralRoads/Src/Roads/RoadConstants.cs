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
    public const int PathfindingMaxIterations = 1000;   
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
