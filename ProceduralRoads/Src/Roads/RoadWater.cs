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
/// a location's entry and approach, a location's platform. Swamp shallows the search waded are not dry for these: they keep
/// to the ground the search walked or drier, and never move a road into water.</item>
/// <item><see cref="LandingFloorFor"/>: where a crossing lands, a road ends or an island's land is: the shallow line
/// plus a bank clearance, a swamp wading down to the deep line.</item>
/// </list>
/// Every test here that reads the world itself reads the ground as it will be (<see cref="Ground"/>), never
/// WorldGenerator.GetHeight.
/// </summary>
public static class RoadWater
{
    /// <summary>
    /// The ground a water test judges at <paramref name="p"/>: the ground as it will be, the one owner's
    /// (<see cref="PlacedLocations.GroundAt(float, float, WorldGenerator)"/>): a location placed before its roads' levelling in its
    /// footprint, elsewhere the terrain the game renders (<see cref="BiomeBlendedHeight"/>). WorldGenerator.GetHeight is the
    /// formula of the biome at the point alone, which the game does not render where the zone's corners are another biome:
    /// in game at (4293.0, -3515.2) a Swamp point inside a zone whose heightmap is built from Black Forest corners reads 29.0 m
    /// raw, under the shallow line, and is rendered at 36.3 m, dry land; read raw, the road's end there was "in water", its
    /// start pads were not laid and a staircase climbed where a road would do.
    /// </summary>
    public static float Ground(WorldGenerator world, Vector2 p) => PlacedLocations.GroundAt(p.x, p.y, world);

    /// <summary><see cref="Ground(WorldGenerator, Vector2)"/> where the caller already holds the natural ground at
    /// <paramref name="p"/> (<paramref name="natural"/>, the rendered terrain <see cref="BiomeBlendedHeight"/> gives): a location's
    /// levelled ground in its footprint, else that.</summary>
    public static float Ground(Vector2 p, float natural) => PlacedLocations.GroundAt(p) ?? natural;

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
    /// (always on in a game; the terrain writer has no search and lays what that walks). The one owner of is this ground water (docs/OWNERS.md).</summary>
    public static Kind Of(float height, float riverWeight, WorldGenerator world, Vector2 p, bool wade = true)
    {
        if (River(riverWeight)) return Kind.River;
        if (height < RoadConstants.DeepWaterHeight) return Kind.Deep;
        if (height < RoadConstants.ShallowWaterHeight)
            return wade && world.GetBiome(p.x, p.y) == Heightmap.Biome.Swamp ? Kind.Waded : Kind.Shallow;
        return Kind.Dry;
    }

    /// <summary><see cref="Of(float, float, WorldGenerator, Vector2, bool)"/> on the <see cref="Ground"/> and the world's river.</summary>
    public static Kind At(WorldGenerator world, Vector2 p, bool wade = true)
    {
        world.GetRiverWeight(p.x, p.y, out float river, out _);
        return Of(Ground(world, p), river, world, p, wade);
    }

    /// <summary>Ground the search walks at <paramref name="p"/>: dry, or a swamp's shallows waded.</summary>
    public static bool Walked(WorldGenerator world, Vector2 p, bool wade = true) =>
        At(world, p, wade) is Kind.Dry or Kind.Waded;

    /// <summary>Ground under the sea line (named case): water, whatever a road may do on it. The one owner of is this ground water (docs/OWNERS.md).</summary>
    public static bool Wet(float height) => height < RoadConstants.SeaLevel;

    /// <summary>Ground at or over the sea line (named case). Not <c>!Wet</c>: an unknown (NaN) height is neither. The one owner of is this ground water (docs/OWNERS.md).</summary>
    public static bool AtOrOverSea(float height) => height >= RoadConstants.SeaLevel;

    /// <summary>The lowest ground the search walks at <paramref name="p"/>: the deep line in a swamp it wades, else the
    /// shallow line.</summary>
    public static float Floor(WorldGenerator world, Vector2 p, bool wade = true) =>
        wade && world.GetBiome(p.x, p.y) == Heightmap.Biome.Swamp ? RoadConstants.DeepWaterHeight : RoadConstants.ShallowWaterHeight;

    /// <summary>Ground a shaped or built road stands on (named case: never waded): at or over the shallow line. The one owner of is this ground water (docs/OWNERS.md).</summary>
    public static bool BuiltDry(float height) => height >= RoadConstants.ShallowWaterHeight;

    /// <summary>The height raised to the floor every road on land is built up to (<see cref="RoadConstants.DryRoadFloor"/>). The one owner of is this ground water (docs/OWNERS.md).</summary>
    public static float AtLeastDryFloor(float height) => Mathf.Max(height, RoadConstants.DryRoadFloor);

    /// <summary><see cref="BuiltDry(float, float)"/> on the <see cref="Ground"/> and the world's river at <paramref name="p"/>.</summary>
    public static bool BuiltDry(WorldGenerator world, Vector2 p)
    {
        world.GetRiverWeight(p.x, p.y, out float river, out _);
        return BuiltDry(Ground(world, p), river);
    }

    /// <summary><see cref="BuiltDry(float)"/> and off a river's core.</summary>
    public static bool BuiltDry(float height, float riverWeight) => !River(riverWeight) && BuiltDry(height);

    /// <summary>Ground a crossing may land on (named case): the shallow line plus the bank clearance.</summary>
    public const float LandingFloor = RoadConstants.ShallowWaterHeight + RoadConstants.BankClearance;

    /// <summary>The landing floor in a biome (named case): a swamp wades down to the deep line.</summary>
    public static float LandingFloorFor(Heightmap.Biome biome) =>
        biome == Heightmap.Biome.Swamp ? RoadConstants.DeepWaterHeight : LandingFloor;

    /// <summary>Whether a road may stand at <paramref name="p"/>: its ground (the <see cref="Ground"/>, unless the caller's
    /// <paramref name="height"/> is given) at or over its biome's landing floor. The one owner of is this ground water (docs/OWNERS.md).</summary>
    public static bool OnLand(WorldGenerator world, Vector2 p, float? height = null) =>
        (height ?? Ground(world, p)) >= LandingFloorFor(world.GetBiome(p.x, p.y));
}
