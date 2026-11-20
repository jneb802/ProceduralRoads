using System;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Generation choices, independent of config I/O and saved networks.</summary>
public sealed class RoadNetworkOptions
{
    public Heightmap.Biome ExcludedBiomes { get; set; } = Heightmap.Biome.AshLands | Heightmap.Biome.DeepNorth;
    public float WeightExponent { get; set; } = 2f;
    public float DistanceScale { get; set; } = 200f;

    internal RoadNetworkOptions Snapshot()=>(RoadNetworkOptions)MemberwiseClone();

    public bool Allows(Heightmap.Biome biome) => (biome & ExcludedBiomes) == 0;
}
