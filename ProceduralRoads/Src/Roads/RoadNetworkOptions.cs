using System;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Generation choices, independent of config I/O and saved networks.</summary>
public sealed class RoadNetworkOptions
{
    /// <summary>The shipped Locations.ExcludedRoadBiomes.</summary>
    public const Heightmap.Biome DefaultExcludedBiomes = Heightmap.Biome.AshLands | Heightmap.Biome.DeepNorth;
    public Heightmap.Biome ExcludedBiomes { get; set; } = DefaultExcludedBiomes;
    /// <summary>The shipped value of the player's config Locations.AttemptRoadsToHildirQuestLocations: off.</summary>
    public const bool DefaultHildirQuestLocations = false;
    /// <summary>Hildir's three quest locations (<see cref="RoadNetworkGenerator.IsHildirQuestLocation"/>) are destinations,
    /// always included as the bosses are and placed before their roads like the other caves; off, no road goes
    /// to them (config Locations.AttemptRoadsToHildirQuestLocations).</summary>
    public bool HildirQuestLocations { get; set; } = DefaultHildirQuestLocations;
    public float WeightExponent { get; set; } = 2f;
    public float DistanceScale { get; set; } = 200f;

    internal RoadNetworkOptions Snapshot()=>(RoadNetworkOptions)MemberwiseClone();

    public bool Allows(Heightmap.Biome biome) => (biome & ExcludedBiomes) == 0;
}
