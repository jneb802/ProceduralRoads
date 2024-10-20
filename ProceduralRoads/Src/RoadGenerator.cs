using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace ProceduralRoads;

public class RoadGenerator
{
    public static int roadCount = 100;
    public static List<WorldGenerator.River> roads = new List<WorldGenerator.River>();
    public static bool roadsRendered = false;
    
    public static void PlaceRoads(WorldGenerator worldGenerator)
    {
        Debug.Log("Starting road placement");
        UnityEngine.Random.State state = UnityEngine.Random.state;
        UnityEngine.Random.InitState(worldGenerator.GetSeed()); // Use the world's seed for consistency
        
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
                    widthMax = 5,
                    widthMin = Random.Range(2,5),
                    curveWidth = 0f,
                    curveWavelength = 0f  // Less curvature for roads
                };

                roads.Add(road);
            }
        }

        UnityEngine.Random.state = state;
    }

    private static bool FindRoadStartPoint(WorldGenerator worldGenerator, out Vector2 start)
    {
        // Loop to ensure we get a valid start point that's not in the Ocean biome
        int attempts = 0;
        do
        {
            start = new Vector2(UnityEngine.Random.Range(-8000f, 8000f), UnityEngine.Random.Range(-8000f, 8000f));
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

    public static void RenderRoads(List<WorldGenerator.River> roads)
    {
        foreach (var road in roads)
        {
            Debug.Log("Rendering road at point " + road.p0);
            
            float segmentLength = road.widthMin * 2;
            Vector2 direction = (road.p1 - road.p0).normalized;
            Vector2 perpendicular = new Vector2(-direction.y, direction.x);
            float distance = Vector2.Distance(road.p0, road.p1);
        
            for (float i = 0; i <= distance; i += segmentLength)
            {
                Vector2 roadPoint = road.p0 + direction * i + perpendicular * 0;
                
                ModifyTerrainForRoad(roadPoint, road.widthMin);
            }
        }
    }
    
    private static void ModifyTerrainForRoad(Vector2 roadPoint, float width)
    {
        Debug.Log("Modifying terrain at point " + roadPoint);
        
        Vector3 roadPositionTemp = new Vector3(roadPoint.x, 0, roadPoint.y);
        Vector3 roadPosition = new Vector3(roadPoint.x, ZoneSystem.instance.GetGroundHeight(roadPositionTemp), roadPoint.y);

        // Get the heightmap for the given road point
        Heightmap heightmap = Heightmap.FindHeightmap(roadPosition);
        if (heightmap == null)
        {
            Debug.Log("Heightmap at position " + roadPosition + " is null");
            return;
        }

        // Get terrain compiler to modify terrain
        TerrainComp terrainComp = heightmap.GetAndCreateTerrainCompiler();
        if (terrainComp == null || !terrainComp.IsOwner()) return;

        // Create a terrain operation for flattening and paving the road
        TerrainOp.Settings modifierSettings = new TerrainOp.Settings
        {
            m_level = true,
            m_levelRadius = width,
            m_smooth = true,
            m_smoothRadius = width + 3f,
            m_smoothPower = Random.Range(4,6),
            m_paintCleared = true,
            m_paintType = TerrainModifier.PaintType.Paved,
            m_paintRadius = width
        };
        
        terrainComp.DoOperation(roadPosition, modifierSettings);
    }
}
