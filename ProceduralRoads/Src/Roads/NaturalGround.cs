// The one door to the world's raw ground (with its game-only part, NaturalGround.Live.cs): outside it and the blend itself every
// file is barred at compile time (BannedSymbols.txt, RS0030 an error), and docs/OWNERS.md "Raw ground readers" lists which files call which member (NaturalGroundTests).
#pragma warning disable RS0030
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The natural ground and the game's live terrain, read raw: the one door to them (docs/OWNERS.md "Ground readers").
/// A road decision never reads this. A pre-road judge reads the ground before roads
/// (<see cref="RoadNetworkGenerator.GroundBeforeRoads(float, float, WorldGenerator)"/>), a water test <see cref="RoadWater.Ground(WorldGenerator, Vector2)"/>,
/// a reader of the built terrain the finished ground (<see cref="RoadTerrainModifier.FinishedGround(System.Func{int, int, float}, bool)"/>,
/// <see cref="StructureGround.At"/>). Each member here is one allowed reason to read the raw ground; a new caller of one is a
/// new row in docs/OWNERS.md "Raw ground readers", with its reason, for review (NaturalGroundTests).
/// </summary>
internal static partial class NaturalGround
{
    /// <summary>A Location's own levelling: the natural ground a placed-first Location is prepared on, its centre's height, and
    /// the ground before roads outside every placed-first footprint (<see cref="PlacedLocations.GroundAt(float, float, WorldGenerator)"/>),
    /// which is built from it.</summary>
    internal static float ForLevelling(float x, float z, WorldGenerator world) => BiomeBlendedHeight.GetBlendedHeight(x, z, world);

    /// <summary>The planners' shared search samples and trial profiles (<see cref="RoadTerrainSamples"/>,
    /// RoadSpatialGrid.PlanRoadPathShaped's trial profiles and sway, a way in's procedural profile): shared, cheap samples that
    /// never hold a Location's authored ground. Every judge of the plan they shape reads the ground before roads.</summary>
    internal static float ForSearch(float x, float z, WorldGenerator world, BiomeBlendedHeight.Corners? corners = null) =>
        BiomeBlendedHeight.GetBlendedHeight(x, z, world, corners);

    /// <summary>Island detection: which ground is land, read over the whole world before any Location is placed.</summary>
    internal static float ForIslands(float x, float z, WorldGenerator world, BiomeBlendedHeight.Corners? corners = null) =>
        BiomeBlendedHeight.GetBlendedHeight(x, z, world, corners);

    /// <summary>The terrain writer's input: the natural ground the finished ground (<see cref="RoadTerrainModifier.FinishedGround(System.Func{int, int, float}, bool)"/>)
    /// is written over, a placed Location's levelling laid over it by the writer itself, and the writer's fill spread.</summary>
    internal static float UnderTheWriter(float x, float z, WorldGenerator world) => BiomeBlendedHeight.GetBlendedHeight(x, z, world);

    /// <summary>Diagnostics: a log line, a console command or a debug marker printing the natural ground. It decides nothing.</summary>
    internal static float ForDiagnostics(float x, float z, WorldGenerator world) => BiomeBlendedHeight.GetBlendedHeight(x, z, world);

    /// <summary>Diagnostics only: the point biome's own height formula (WorldGenerator.GetHeight), which the game does not render
    /// where a zone's corners are another biome. Printed beside the blended ground so a gap between them shows.</summary>
    internal static float PointBiomeFormula(float x, float z, WorldGenerator world) => world.GetHeight(x, z);

    /// <summary>Diagnostics only: the blend's corners, weights and heights at a point.</summary>
    internal static BiomeBlendedHeight.BlendDebugInfo BlendDebug(float x, float z, WorldGenerator world) =>
        BiomeBlendedHeight.GetBlendDebugInfo(x, z, world);
}
