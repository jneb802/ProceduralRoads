using System;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Generation choices, independent of config I/O and saved networks.</summary>
public sealed class RoadNetworkOptions
{
    public int CustomLocationPriority { get; set; } = 80;
    public Heightmap.Biome ExcludedBiomes { get; set; } = Heightmap.Biome.AshLands | Heightmap.Biome.DeepNorth;
    public bool RoutedConnections { get; set; } = true;
    public bool WalkableIslands { get; set; } = true;
    public bool CoastalLandings { get; set; } = true;
    public bool ContentFirst { get; set; } = true;
    public bool WeightedDestinations { get; set; } = true;
    public int TargetSubAreas { get; set; } = 9;
    public float WeightExponent { get; set; } = 2f;
    public float DistanceScale { get; set; } = 200f;

    public bool Allows(Heightmap.Biome biome) => (biome & ExcludedBiomes) == 0;
}
