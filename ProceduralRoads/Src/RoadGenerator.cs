using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace ProceduralRoads;

public class RoadGenerator
{
    public static int roadCount = 100;
    public static List<Road> roads = new List<Road>();
    
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
                
                RoadGenerator.Road road = new RoadGenerator.Road
                {
                    p0 = start,
                    p1 = end,
                    center = (start + end) * 0.5f,
                    width = Random.Range(2,5),
                    curveWidth = 5
                };

                roads.Add(road);

                GenerateRoadPoints(road);
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
            start = new Vector2(UnityEngine.Random.Range(-8000, 8000), UnityEngine.Random.Range(-8000, 8000));
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
            end = start + new Vector2(UnityEngine.Random.Range(-500, 500), UnityEngine.Random.Range(-500, 500));
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

    public static void GenerateRoadPoints(Road road)
    {
        Debug.Log("Generating road points for road at point " + road.p0);
        
        List<RoadPoint> roadPoints = new List<RoadPoint>();
        
        float segmentLength = 5;
        Vector2 direction = (road.p1 - road.p0).normalized;
        Vector2 perpendicular = new Vector2(-direction.y, direction.x); 
        float distance = Vector2.Distance(road.p0, road.p1);
        
        float noiseScale = 0.05f;
        float noiseOffset = UnityEngine.Random.Range(0f, 1000f);

        for (float i = 0; i <= distance; i += segmentLength)
        {
            Vector2 roadPoint = road.p0 + direction * i;
            
            float noiseValue = DUtils.PerlinNoise(i * noiseScale + noiseOffset, 0f); 
            float curveOffset = (noiseValue - 0.5f) * road.curveWidth; 
            
            roadPoint += perpendicular * curveOffset;
            
            Vector2i roadPointInt = new Vector2i(Mathf.RoundToInt(roadPoint.x), Mathf.RoundToInt(roadPoint.y));
            Vector3 roadPointVector3 = new Vector3(Mathf.RoundToInt(roadPoint.x), 0,Mathf.RoundToInt(roadPoint.y));
            
            Vector2i zoneID = ZoneSystem.instance.GetZone(roadPointVector3);
            
            roadPoints.Add(new RoadPoint
            {
                position = roadPointInt,
                zoneID = zoneID,
                width = road.width
            });
        }
        
        road.roadPoints = roadPoints;
}

    public static List<RoadPoint> GetRoadPointsInZone(Vector2i zoneID)
    {
        List<RoadPoint> roadPointsInZone = new List<RoadPoint>();
        
        foreach (Road road in roads)
        {
            foreach (RoadPoint roadPoint in road.roadPoints)
            {
                if (roadPoint.zoneID == zoneID)
                {
                    roadPointsInZone.Add(roadPoint); 
                }
            }
        }
        
        return roadPointsInZone;
    }
    
    public static void RenderRoadPoint(RoadPoint roadPoint)
    {
        Debug.Log("Modifying terrain at point " + roadPoint.position);
        
        Vector3 roadPositionTemp = new Vector3(roadPoint.position.x, 0, roadPoint.position.y);
        Vector3 roadPosition = new Vector3(roadPoint.position.x, ZoneSystem.instance.GetGroundHeight(roadPositionTemp), roadPoint.position.y);

        // Get the heightmap for the given road point
        Heightmap heightmap = Heightmap.FindHeightmap(roadPosition);
        if (heightmap == null)
        {
            Debug.Log("Heightmap at position " + roadPosition + " is null");
            return;
        }
        
        // Trying out another approach below
        // TerrainComp terrainComp = heightmap.GetAndCreateTerrainCompiler();
        
        TerrainComp terrainComp = heightmap.m_terrainCompilerPrefab.GetComponent<TerrainComp>();
        if (terrainComp == null)
        {
            Debug.Log("Terrain compiler is null");
            return;
            // || !terrainComp.IsOwner()
        }

        // Create a terrain operation for flattening and paving the road
        TerrainOp modifier = new TerrainOp();
        modifier.transform.position = roadPosition;
        TerrainOp.Settings modifierSettings = new TerrainOp.Settings
        {
            m_level = true,
            m_levelRadius = roadPoint.width,
            m_smooth = true,
            m_smoothRadius = roadPoint.width + 6f,
            m_smoothPower = 10,
            m_paintCleared = true,
            m_paintType = TerrainModifier.PaintType.Paved,
            m_paintRadius = roadPoint.width,
            m_square = false
            // m_levelOffset = new Vector3(roadPoint.position.x, ZoneSystem.instance.GetGroundHeight(roadPositionTemp), roadPoint.position.y).y
        };
        modifier.m_settings = modifierSettings;

        if (heightmap != null)
        {
            // terrainComp.ApplyOperation(modifier);
            
            // Trying out another approach above
            terrainComp.DoOperation(roadPosition, modifierSettings);
        }
    }
    
    public class Road
    {
        public Vector2 p0;
        public Vector2 p1;
        public Vector2 center;
        public float width;
        public float curveWidth;
        public List<RoadPoint> roadPoints;
    }

    public class RoadPoint
    {
        public int roadPointIndex;
        public Vector2i position;
        public Vector2i zoneID;
        public float width;
    }
    
}
