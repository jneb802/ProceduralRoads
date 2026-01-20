using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Provides biome-blended terrain height queries that match what Heightmap actually renders.
/// 
/// The problem: WorldGenerator.GetHeight() does NOT blend biomes at boundaries.
/// It just calls GetBiome(x,y) and returns that single biome's height.
/// But HeightmapBuilder (what actually renders terrain) blends heights from multiple
/// biomes across chunk boundaries using bilinear interpolation.
/// 
/// Result: At biome boundaries, roads using WorldGenerator.GetHeight see "phantom cliffs"
/// that don't exist in the rendered terrain.
/// 
/// HeightmapBuilder Logic:
/// - Heightmaps are 64m x 64m (m_width=32 vertices, m_scale=2 meters/vertex)
/// - Centered on zone positions (zones are 64m apart)
/// - Biomes sampled at heightmap corners (±32m from center)
/// - Heights blended using bilinear interpolation with SmoothStep
/// 
/// This utility replicates that blending logic.
/// </summary>
public static class BiomeBlendedHeight
{
    // Heightmap dimensions matching Valheim's Heightmap class
    // IMPORTANT: m_width=32 vertices, but m_scale=2 in the actual game prefab!
    // This makes heightmaps 64m wide to match zone spacing (64m between zone centers).
    // The default m_scale=1 in decompiled code is overridden by Unity prefab serialization.
    private const int HeightmapWidth = 32;       // m_width (vertex count)
    private const float HeightmapScale = 2f;     // m_scale (actual game uses 2, not 1!)
    private const float HeightmapSize = HeightmapWidth * HeightmapScale;  // 64m total
    private const float HeightmapHalfSize = HeightmapSize / 2f;           // 32m from center
    
    // Zone size for calculating heightmap centers
    private const float ZoneSize = 64f;
    
    /// <summary>
    /// Get terrain height with biome blending that matches rendered terrain.
    /// This replicates HeightmapBuilder.Build()'s biome blending logic exactly.
    /// </summary>
    public static float GetBlendedHeight(float wx, float wy, WorldGenerator worldGen)
    {
        if (worldGen == null)
            return 0f;
        
        // Find which zone/heightmap contains this point
        // Zones are at 64m intervals (0, 64, 128, -64, etc.)
        // Each heightmap is 64m wide centered on zone position (covers ±32m from center)
        // So zone at x=64 has heightmap covering [32, 96]
        //
        // To find which zone contains point wx:
        // - Shift by half heightmap size (32m) so the floor division aligns with heightmap boundaries
        // - Floor divide by zone size
        float zoneX = Mathf.Floor((wx + HeightmapHalfSize) / ZoneSize) * ZoneSize;
        float zoneZ = Mathf.Floor((wy + HeightmapHalfSize) / ZoneSize) * ZoneSize;
        
        // HeightmapBuilder calculates corner from center:
        // corner = center + new Vector3(m_width * m_scale * -0.5f, 0, m_width * m_scale * -0.5f)
        // With m_width=32 and m_scale=2, this is center + (-32, 0, -32)
        float cornerX = zoneX - HeightmapHalfSize;
        float cornerZ = zoneZ - HeightmapHalfSize;
        
        // Biome sampling points (at the 4 corners of the heightmap)
        float x0 = cornerX;
        float x1 = cornerX + HeightmapSize;
        float z0 = cornerZ;
        float z1 = cornerZ + HeightmapSize;
        
        // Get biomes at the four corners
        Heightmap.Biome biome00 = worldGen.GetBiome(x0, z0);  // bottom-left
        Heightmap.Biome biome10 = worldGen.GetBiome(x1, z0);  // bottom-right  
        Heightmap.Biome biome01 = worldGen.GetBiome(x0, z1);  // top-left
        Heightmap.Biome biome11 = worldGen.GetBiome(x1, z1);  // top-right
        
        // If all corners have the same biome, no blending needed
        // (This is the fast path that HeightmapBuilder uses)
        if (biome00 == biome10 && biome00 == biome01 && biome00 == biome11)
        {
            return worldGen.GetHeight(wx, wy);
        }
        
        // Different biomes at corners - need to blend
        // Calculate position within the heightmap (0 to m_width)
        float localX = (wx - cornerX) / HeightmapScale;
        float localZ = (wy - cornerZ) / HeightmapScale;
        
        // Normalize to 0-1 range and apply SmoothStep (matches HeightmapBuilder)
        // HeightmapBuilder: float tx = DUtils.SmoothStep(0f, 1f, l / data.m_width);
        float tx = SmoothStep(localX / HeightmapWidth);
        float tz = SmoothStep(localZ / HeightmapWidth);
        
        // Get heights for each corner biome at THIS point (not at corners)
        // This is exactly what HeightmapBuilder does
        Color mask;  // Unused, but required by API
        float h00 = worldGen.GetBiomeHeight(biome00, wx, wy, out mask);
        float h10 = worldGen.GetBiomeHeight(biome10, wx, wy, out mask);
        float h01 = worldGen.GetBiomeHeight(biome01, wx, wy, out mask);
        float h11 = worldGen.GetBiomeHeight(biome11, wx, wy, out mask);
        
        // Bilinear interpolation (matching HeightmapBuilder.Build)
        // float h_bottom = DUtils.Lerp(h00, h10, tx);
        // float h_top = DUtils.Lerp(h01, h11, tx);  
        // height = DUtils.Lerp(h_bottom, h_top, tz);
        float hBottom = Mathf.Lerp(h00, h10, tx);
        float hTop = Mathf.Lerp(h01, h11, tx);
        float height = Mathf.Lerp(hBottom, hTop, tz);
        
        return height;
    }
    
    /// <summary>
    /// Smooth step function matching Valheim's DUtils.SmoothStep(0, 1, t)
    /// Standard smoothstep: 3t² - 2t³
    /// </summary>
    private static float SmoothStep(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }
    
    /// <summary>
    /// Check if a point is near a biome boundary (within detection radius).
    /// Useful for debugging or deciding when to apply special handling.
    /// </summary>
    public static bool IsNearBiomeBoundary(float wx, float wy, WorldGenerator worldGen, float radius = 32f)
    {
        if (worldGen == null)
            return false;
            
        Heightmap.Biome centerBiome = worldGen.GetBiome(wx, wy);
        
        // Check cardinal + diagonal directions
        float[] offsets = { -radius, 0, radius };
        foreach (float dz in offsets)
        {
            foreach (float dx in offsets)
            {
                if (dx == 0 && dz == 0) continue;
                if (worldGen.GetBiome(wx + dx, wy + dz) != centerBiome)
                {
                    return true;
                }
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Get detailed debug info about height calculations at a point.
    /// Returns a struct with all the intermediate values for diagnosis.
    /// </summary>
    public static BlendDebugInfo GetBlendDebugInfo(float wx, float wy, WorldGenerator worldGen)
    {
        var info = new BlendDebugInfo();
        
        if (worldGen == null)
            return info;
        
        // Find containing heightmap using same logic as GetBlendedHeight
        float zoneX = Mathf.Floor((wx + HeightmapHalfSize) / ZoneSize) * ZoneSize;
        float zoneZ = Mathf.Floor((wy + HeightmapHalfSize) / ZoneSize) * ZoneSize;
        float cornerX = zoneX - HeightmapHalfSize;
        float cornerZ = zoneZ - HeightmapHalfSize;
        
        info.ZoneCenter = new Vector2(zoneX, zoneZ);
        info.HeightmapCorner = new Vector2(cornerX, cornerZ);
        
        // Get corner biomes
        info.Biome00 = worldGen.GetBiome(cornerX, cornerZ);
        info.Biome10 = worldGen.GetBiome(cornerX + HeightmapSize, cornerZ);
        info.Biome01 = worldGen.GetBiome(cornerX, cornerZ + HeightmapSize);
        info.Biome11 = worldGen.GetBiome(cornerX + HeightmapSize, cornerZ + HeightmapSize);
        
        info.IsBiomeBoundary = !(info.Biome00 == info.Biome10 && 
                                  info.Biome00 == info.Biome01 && 
                                  info.Biome00 == info.Biome11);
        
        // Calculate interpolation parameters
        float localX = (wx - cornerX) / HeightmapScale;
        float localZ = (wy - cornerZ) / HeightmapScale;
        info.LocalPosition = new Vector2(localX, localZ);
        info.Tx = SmoothStep(localX / HeightmapWidth);
        info.Tz = SmoothStep(localZ / HeightmapWidth);
        
        // Get heights
        Color mask;
        info.Height00 = worldGen.GetBiomeHeight(info.Biome00, wx, wy, out mask);
        info.Height10 = worldGen.GetBiomeHeight(info.Biome10, wx, wy, out mask);
        info.Height01 = worldGen.GetBiomeHeight(info.Biome01, wx, wy, out mask);
        info.Height11 = worldGen.GetBiomeHeight(info.Biome11, wx, wy, out mask);
        
        info.RawHeight = worldGen.GetHeight(wx, wy);
        info.BlendedHeight = GetBlendedHeight(wx, wy, worldGen);
        info.HeightDifference = info.BlendedHeight - info.RawHeight;
        
        return info;
    }
    
    public struct BlendDebugInfo
    {
        public Vector2 ZoneCenter;
        public Vector2 HeightmapCorner;
        public Vector2 LocalPosition;
        public float Tx, Tz;
        
        public Heightmap.Biome Biome00, Biome10, Biome01, Biome11;
        public bool IsBiomeBoundary;
        
        public float Height00, Height10, Height01, Height11;
        public float RawHeight;
        public float BlendedHeight;
        public float HeightDifference;
    }
}
