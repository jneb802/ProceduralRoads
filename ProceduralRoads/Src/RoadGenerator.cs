using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

public class RoadGenerator
{
    public static int roadCount = 10;
    public static void PlaceRoads(WorldGenerator worldGenerator)
    {
        Debug.Log("Starting road generation");
        UnityEngine.Random.State state = UnityEngine.Random.state;
        UnityEngine.Random.InitState(worldGenerator.GetSeed()); // Use the world's seed for consistency

        List<WorldGenerator.River> roads = new List<WorldGenerator.River>();
        
        for (int i = 0; i < roadCount; i++)
        {
            Vector2 start, end;
            if (FindRoadStartPoint(worldGenerator, out start) && FindRoadEndPoint(worldGenerator, start, out end))
            {
                Debug.Log("Road " + i + 1 + " Start Point: " + start);
                
                WorldGenerator.River road = new WorldGenerator.River
                {
                    p0 = start,
                    p1 = end,
                    center = (start + end) * 0.5f,
                    widthMax = 10f,
                    widthMin = 10f,
                    curveWidth = 0f,
                    curveWavelength = 0f  // Less curvature for roads
                };

                roads.Add(road);
            }
        }

        // Render and modify terrain for roads
        RenderRoads(roads, worldGenerator);

        UnityEngine.Random.state = state;
    }

    private static bool FindRoadStartPoint(WorldGenerator worldGenerator, out Vector2 start)
    {
        // Loop to ensure we get a valid start point that's not in the Ocean biome
        int attempts = 0;
        do
        {
            start = new Vector2(UnityEngine.Random.Range(-10000f, 10000f), UnityEngine.Random.Range(-10000f, 10000f));
            Heightmap.Biome biome = Heightmap.FindBiome(new Vector3(start.x,0, start.y));

            // Check if the point is in the Ocean biome, and retry if it is
            if (biome != Heightmap.Biome.Ocean)
            {
                return true;
            }
            attempts++;

        } while (attempts < 100); // Limit the number of attempts to avoid infinite loops

        start = Vector2.zero;
        return false;
    }

    private static bool FindRoadEndPoint(WorldGenerator worldGenerator, Vector2 start, out Vector2 end)
    {
        // Loop to ensure we get a valid end point that's not in the Ocean biome
        int attempts = 0;
        do
        {
            end = start + new Vector2(UnityEngine.Random.Range(-500f, 500f), UnityEngine.Random.Range(-500f, 500f));
            Heightmap.Biome biome = Heightmap.FindBiome(new Vector3(end.x, 0, end.y));
            
            if (biome != Heightmap.Biome.Ocean)
            {
                return true;
            }
            attempts++;

        } while (attempts < 100);

        end = start;
        return false;
    }

    private static void RenderRoads(List<WorldGenerator.River> roads, WorldGenerator worldGenerator)
    {
        foreach (var road in roads)
        {
            Debug.Log("Rendering road at point " + road.p0);
            
            float segmentLength = road.widthMin / 8f;
            Vector2 direction = (road.p1 - road.p0).normalized;
            Vector2 perpendicular = new Vector2(-direction.y, direction.x);
            float distance = Vector2.Distance(road.p0, road.p1);
        
            for (float i = 0; i <= distance; i += segmentLength)
            {
                Vector2 roadPoint = road.p0 + direction * i + perpendicular * 0;

                // Flatten the terrain and pave the road
                ModifyTerrainForRoad(worldGenerator, roadPoint, road.widthMin);
            }
        }
    }

    private static void ModifyTerrainForRoad(WorldGenerator worldGenerator, Vector2 roadPoint, float width)
    {
        Debug.Log("Modifying terrain at point " + roadPoint);
        
        // Convert road point from Vector2 to Vector3 for terrain operations
        Vector3 roadPosition = new Vector3(roadPoint.x, 0, roadPoint.y);
        
        // Create a new GameObject for the TerrainModifier
        GameObject terrainModifierObj = new GameObject("RoadTerrainModifier");
        TerrainModifier terrainModifier = terrainModifierObj.AddComponent<TerrainModifier>();

        // Configure the TerrainModifier settings for leveling and paving the road
        terrainModifier.m_level = true;
        terrainModifier.m_levelRadius = 10;
        terrainModifier.m_paintCleared = true;
        terrainModifier.m_paintType = TerrainModifier.PaintType.Paved;
        terrainModifier.m_paintRadius = 10;
        terrainModifier.m_levelOffset = 5f;

        // Set the position of the modifier
        terrainModifierObj.transform.position = roadPosition;

        // Trigger terrain modification
        // terrainModifier.PokeHeightmaps(true); // This ensures the terrain gets modified immediately

        // Optionally clean up the terrain modifier after the operation
        // UnityEngine.Object.Destroy(terrainModifierObj);
    }
}
