// Roads' part of the Heightmap double (Valheim.Testing.Doubles): the rebuild uses the mod's own base height
// and runs its terrain seam, exactly as the production Harmony prefix does in the game.
using System.Collections.Generic;

public partial class Heightmap
{
    partial void ModBaseHeight(float wx, float wz, ref float height) =>
        height = ProceduralRoads.BiomeBlendedHeight.GetBlendedHeight(wx, wz, WorldGenerator.instance);
    // No compiler deltas have been added to these location-shaped heights yet.
    partial void ModTerrainPass(List<float> heights) =>
        ProceduralRoads.RoadTerrainModifier.ApplyPendingTerrain(m_terrainComp!, this, heights);
}
