using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The one owner of "is this ground water" for roads: one set of thresholds (a river's core, the deep line, the
/// shallow line, the bank clearance) and the named cases where a rule intentionally asks a different question of
/// them. Pure over the world: any thread.
/// <list type="bullet">
/// <item><see cref="Walked"/>: what the search steps on and the shore fill builds beside: dry ground, or a swamp's
/// shallows where fords are on (waded). Its floor: <see cref="Floor"/>.</item>
/// <item><see cref="BuiltDry"/>: what is shaped or built never wades: a road's pull, sway and turn curves, staircases,
/// a place's entry and approach, a place's platform. Swamp shallows the search waded are not dry for these: they keep
/// to the ground the search walked or drier, and never move a road into water.</item>
/// <item><see cref="LandingFloorFor"/>: where a crossing lands, a road ends or an island's land is: the shallow line
/// plus a bank clearance, a swamp wading down to the deep line.</item>
/// </list>
/// </summary>
public static class RoadWater
{
    /// <summary>What ground is to a road.</summary>
    public enum Kind
    {
        /// <summary>At or over the shallow line, off a river's core.</summary>
        Dry,
        /// <summary>A swamp's shallows (deep line to shallow line), waded where fords are on.</summary>
        Waded,
        /// <summary>Shallows (deep line to shallow line) nobody wades.</summary>
        Shallow,
        /// <summary>Under the deep line.</summary>
        Deep,
        /// <summary>A river's core.</summary>
        River,
    }

    /// <summary>A river's core: the weight past which no road walks.</summary>
    public static bool River(float riverWeight) => riverWeight > RoadConstants.RiverImpassableThreshold;

    /// <summary>The one classification: <paramref name="height"/> and <paramref name="riverWeight"/> at
    /// <paramref name="p"/>; the biome is read only for shallows. <paramref name="wade"/>: the search's fords switch
    /// (always on in a game; the terrain writer has no search and lays what that walks).</summary>
    public static Kind Of(float height, float riverWeight, WorldGenerator world, Vector2 p, bool wade = true)
    {
        if (River(riverWeight)) return Kind.River;
        if (height < RoadConstants.DeepWaterHeight) return Kind.Deep;
        if (height < RoadConstants.ShallowWaterHeight)
            return wade && world.GetBiome(p.x, p.y) == Heightmap.Biome.Swamp ? Kind.Waded : Kind.Shallow;
        return Kind.Dry;
    }

    /// <summary><see cref="Of(float, float, WorldGenerator, Vector2, bool)"/> on the world's own height and river.</summary>
    public static Kind At(WorldGenerator world, Vector2 p, bool wade = true)
    {
        world.GetRiverWeight(p.x, p.y, out float river, out _);
        return Of(world.GetHeight(p.x, p.y), river, world, p, wade);
    }

    /// <summary>Ground the search walks at <paramref name="p"/>: dry, or a swamp's shallows waded.</summary>
    public static bool Walked(WorldGenerator world, Vector2 p, bool wade = true) =>
        At(world, p, wade) is Kind.Dry or Kind.Waded;

    /// <summary>The lowest ground the search walks at <paramref name="p"/>: the deep line in a swamp it wades, else the
    /// shallow line.</summary>
    public static float Floor(WorldGenerator world, Vector2 p, bool wade = true) =>
        wade && world.GetBiome(p.x, p.y) == Heightmap.Biome.Swamp ? RoadConstants.DeepWaterHeight : RoadConstants.ShallowWaterHeight;

    /// <summary>Ground a shaped or built road stands on (named case: never waded): at or over the shallow line.</summary>
    public static bool BuiltDry(float height) => height >= RoadConstants.ShallowWaterHeight;

    /// <summary><see cref="BuiltDry(float)"/> and off a river's core.</summary>
    public static bool BuiltDry(float height, float riverWeight) => !River(riverWeight) && BuiltDry(height);

    /// <summary>Ground a crossing may land on (named case): the shallow line plus the bank clearance.</summary>
    public const float LandingFloor = RoadConstants.ShallowWaterHeight + RoadConstants.BankClearance;

    /// <summary>The landing floor in a biome (named case): a swamp wades down to the deep line.</summary>
    public static float LandingFloorFor(Heightmap.Biome biome) =>
        biome == Heightmap.Biome.Swamp ? RoadConstants.DeepWaterHeight : LandingFloor;

    /// <summary>Whether a road may stand at <paramref name="p"/>: its ground (the world generator's, unless the caller's
    /// <paramref name="height"/> is given) at or over its biome's landing floor.</summary>
    public static bool OnLand(WorldGenerator world, Vector2 p, float? height = null) =>
        (height ?? world.GetHeight(p.x, p.y)) >= LandingFloorFor(world.GetBiome(p.x, p.y));
}
