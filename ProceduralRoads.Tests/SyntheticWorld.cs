using UnityEngine;
using Valheim.Testing;
namespace ProceduralRoads.Tests;
// Test-only adaptation; no toolkit dependency is introduced in the shipped mod.
public class SyntheticWorld : WorldGenerator
{
    private readonly SyntheticTerrain _terrain = new SyntheticTerrain();
    public bool HasRiver { get => _terrain.HasRiver; set => _terrain.HasRiver = value; }
    public float RiverX { get => _terrain.RiverX; set => _terrain.RiverX = value; }
    public float RiverHalfWidth { get => _terrain.RiverHalfWidth; set => _terrain.RiverHalfWidth = value; }
    public bool HasMountain { get => _terrain.HasMountain; set => _terrain.HasMountain = value; }
    public float MountainX { get => _terrain.MountainX; set => _terrain.MountainX = value; }
    public float MountainHalfWidth { get => _terrain.MountainHalfWidth; set => _terrain.MountainHalfWidth = value; }
    public float MountainHeight { get => _terrain.MountainHeight; set => _terrain.MountainHeight = value; }
    public float IslandRadius { get => _terrain.IslandRadius; set => _terrain.IslandRadius = value; }
    public float IslandPeakHeight { get => _terrain.IslandPeakHeight; set => _terrain.IslandPeakHeight = value; }
    public override float GetHeight(float x, float z) => _terrain.GetHeight(x, z);
    public override Heightmap.Biome GetBiome(float x, float z) => _terrain.GetBiome(x, z) switch
    {
        TerrainBiome.Ocean => Heightmap.Biome.Ocean,
        TerrainBiome.Mountain => Heightmap.Biome.Mountain,
        _ => Heightmap.Biome.Meadows
    };
    public override void GetRiverWeight(float x, float z, out float weight, out float width) => _terrain.GetRiverWeight(x, z, out weight, out width);
}
