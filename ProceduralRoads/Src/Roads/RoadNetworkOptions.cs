using System;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Generation choices, independent of config I/O and saved networks.</summary>
public sealed class RoadNetworkOptions
{
    public Heightmap.Biome ExcludedBiomes { get; set; } = Heightmap.Biome.AshLands | Heightmap.Biome.DeepNorth;
    /// <summary>The shipped value of the player's config Locations.AttemptRoadsToHildirQuestSites: off.</summary>
    public const bool DefaultHildirQuestSites = false;
    /// <summary>Hildir's three quest sites (<see cref="RoadNetworkGenerator.IsHildirQuestSite"/>) are destinations,
    /// always included as the bosses are and placed before their roads like the other caves; off, no road goes
    /// to them (config Locations.AttemptRoadsToHildirQuestSites).</summary>
    public bool HildirQuestSites { get; set; } = DefaultHildirQuestSites;
    public float WeightExponent { get; set; } = 2f;
    public float DistanceScale { get; set; } = 200f;

    internal RoadNetworkOptions Snapshot()=>(RoadNetworkOptions)MemberwiseClone();

    public bool Allows(Heightmap.Biome biome) => (biome & ExcludedBiomes) == 0;
}
