using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace ProceduralRoads;

public class RoadGenerator
{
    public static int roadCount = 10;
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
        int attempts = 0;
        do
        {
            start = new Vector2(UnityEngine.Random.Range(-1000, 1000), UnityEngine.Random.Range(-1000, 1000));
            Heightmap.Biome biome = Heightmap.FindBiome(new Vector3(start.x,0, start.y));

            
            if (biome != Heightmap.Biome.Ocean)
            {
                return true;
            }
            attempts++;

        } while (attempts < 100);

        start = Vector2.zero;
        return false;
    }

    private static bool FindRoadEndPoint(WorldGenerator worldGenerator, Vector2 start, out Vector2 end)
    {
        int attempts = 0;
        do
        {
            end = start + new Vector2(UnityEngine.Random.Range(-100, 100), UnityEngine.Random.Range(-100, 100));
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
        
        float segmentLength = 1;
        Vector2 direction = (road.p1 - road.p0).normalized;
        Vector2 perpendicular = new Vector2(-direction.y, direction.x);
        float distance = Vector2.Distance(road.p0, road.p1);
        
        // float noiseScale = 0.05f;
        //float noiseOffset = UnityEngine.Random.Range(0f, 1000f);
        
        int roadPointIndex = 0;

        for (float i = 0; i <= distance; i += segmentLength)
        {
            Vector2 roadPoint = road.p0 + direction * i;
            
            // float noiseValue = DUtils.PerlinNoise(i * noiseScale + noiseOffset, 0f); 
            // float curveOffset = (noiseValue - 0.5f) * road.curveWidth; 
            
            // roadPoint += perpendicular * curveOffset;
            
            Vector2i roadPointInt = new Vector2i(Mathf.RoundToInt(roadPoint.x), Mathf.RoundToInt(roadPoint.y));
            Vector3 roadPointVector3 = new Vector3(Mathf.RoundToInt(roadPoint.x),0,Mathf.RoundToInt(roadPoint.y));
            
            Vector2i zoneID = ZoneSystem.instance.GetZone(roadPointVector3);
            
            roadPoints.Add(new RoadPoint
            {
                roadPointIndex = roadPointIndex++,
                position = roadPointInt,
                zoneID = zoneID,
                width = road.width,
                parentRoad = road,
            });
        }
        
        road.roadPoints = roadPoints;
    }
    
    public static float AddRoad(float height, float wx, float wy)
    {
        Debug.Log("Processing road height at world coordinates (" + wx + ", " + wy + ") with initial height: " + height);
        
        foreach (Road road in roads)
        {
            Debug.Log("Checking road with " + road.roadPoints.Count + " road points.");
            foreach (RoadPoint roadPoint in road.roadPoints)
            {
                Debug.Log("Checking road point at position (" + roadPoint.position.x + ", " + roadPoint.position.y + ") with width: " + roadPoint.width);
                if (IsPointNearRoad(wx, wy, roadPoint, roadPoint.width))
                {
                    Debug.Log("Point (" + wx + ", " + wy + ") is near road point. Adjusting height from " + height + " to road point height: " + roadPoint.position.y);
                    
                    height = roadPoint.position.y;
                }
            }
        }
        Debug.Log("Final height at world coordinates (" + wx + ", " + wy + ") is: " + height);
        return height;
    }

    
    public static bool IsPointNearRoad(float wx, float wy, RoadPoint roadPoint, float roadWidth)
    {
        float distance = Vector2.Distance(new Vector2(wx, wy), new Vector2(roadPoint.position.x, roadPoint.position.y));
        return distance <= roadWidth;
    }

    public static List<RoadPoint> GetRoadPointsInZone(Vector2i zoneID)
    {
        List<RoadPoint> roadPointsInZone = new List<RoadPoint>();
        
        foreach (Road road in roads)
        {
            int roadPointZoneIndex = 0;
            foreach (RoadPoint roadPoint in road.roadPoints)
            {
                if (roadPoint.zoneID == zoneID)
                {
                    roadPoint.roadPointZoneIndex = roadPointZoneIndex++;
                    roadPointsInZone.Add(roadPoint); 
                }
            }
        }
        
        return roadPointsInZone;
    }

    public static bool CheckTerrainDelta(Vector3 position, float width, Heightmap heightmap)
    {
        if (heightmap.IsBiomeEdge())
        {
            return false;
        }
        
        WorldGenerator.instance.GetTerrainDelta(position, width, out float delta, out Vector3 slopeDirection);
        if (delta < 2)
        {
            return true;
        }

        return false;
    }

    public static float CheckTerrainDeltaBetweenPoints(RoadPoint roadPoint, Road road)
    {
        if (roadPoint.roadPointIndex > 0 && roadPoint.roadPointIndex < road.roadPoints.Count)
        {
            int roadPointIndex_0 = roadPoint.roadPointIndex - 1;
            int roadPointIndex_1 = roadPoint.roadPointIndex;
            
            if (roadPointIndex_0 >= 0 && roadPointIndex_1 < road.roadPoints.Count)
            {
                float roadPointsTerrainDelta = road.roadPoints[roadPointIndex_1].position.y -
                                               road.roadPoints[roadPointIndex_0].position.y;

                return roadPointsTerrainDelta;
            }
        }
        
        return 0;
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
        
        TerrainComp terrainComp = heightmap.GetAndCreateTerrainCompiler();
        if (terrainComp == null || !terrainComp.IsOwner())
        {
            Debug.Log("Terrain compiler is null");
            return;
        }
        
        TerrainOp.Settings modifierSettings = new TerrainOp.Settings
        {
            m_level = CheckTerrainDelta(roadPosition,roadPoint.width, heightmap),
            m_levelRadius = roadPoint.width,
            // m_smooth = true,
            // m_smoothRadius = roadPoint.width + 6f,
            // m_smoothPower = 10,
            m_paintCleared = true,
            m_paintType = TerrainModifier.PaintType.Paved,
            m_paintRadius = roadPoint.width,
            m_paintHeightCheck = true,
            m_square = false,
            // m_levelOffset = -CheckTerrainDeltaBetweenPoints(roadPoint, roadPoint.parentRoad)
        };

        if (heightmap != null)
        {
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
        public int roadPointZoneIndex;
        public Vector2i position;
        public Vector2i zoneID;
        public float width;
        public Road parentRoad;
    }
    
}
