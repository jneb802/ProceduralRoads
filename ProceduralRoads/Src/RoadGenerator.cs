using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace ProceduralRoads;

public class RoadGenerator
{
    public static int roadCount = 3;
    public static List<Road> roads = new List<Road>();
    
    public static void PlaceRoads(WorldGenerator worldGenerator)
    {
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Starting road placement");
        UnityEngine.Random.State state = UnityEngine.Random.state;
        UnityEngine.Random.InitState(worldGenerator.GetSeed());
        
        for (int i = 0; i < roadCount; i++)
        {
            Vector2 start, end;
            if (FindRoadStartPoint(worldGenerator, out start) && FindRoadEndPoint(worldGenerator, start, out end))
            {
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Road " + i + 1 + " Start Point: " + start);
                
                RoadGenerator.Road road = new RoadGenerator.Road
                {
                    p0 = start,
                    p1 = end,
                    width = Random.Range(2,5),
                };

                roads.Add(road);

                GenerateRoadPoints(road,worldGenerator);
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

    public static void GenerateRoadPoints(Road road, WorldGenerator worldGenerator)
    {
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Generating road points for road at point " + road.p0);
        List<RoadPoint> roadPoints = new List<RoadPoint>();
        
        float segmentLength = 1;
        Vector2 direction = (road.p1 - road.p0).normalized;
        Vector2 perpendicular = new Vector2(-direction.y, direction.x);
        float distance = Vector2.Distance(road.p0, road.p1);
        
        for (float i = 0; i <= distance; i += segmentLength)
        {
            Vector2 roadPoint = road.p0 + direction * i;
            
            Vector3 roadPointVector3 = new Vector3(Mathf.RoundToInt(roadPoint.x),0,Mathf.RoundToInt(roadPoint.y));
            
            Vector2i zoneID = ZoneSystem.GetZone(roadPointVector3);
            
            roadPoints.Add(new RoadPoint
            {
                position = roadPointVector3,
                zoneID = zoneID,
                width = road.width,
                parentRoad = road,
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
                { ;
                    roadPointsInZone.Add(roadPoint); 
                }
            }
        }
        
        return roadPointsInZone;
    }

    public static void UpdateRoadPointHeight(RoadPoint roadPoint, ZoneSystem zoneSystem)
    {
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Updating height for Roadpoint at position " + roadPoint.position + " with height of " + roadPoint.position.y);
        float groundHeight = zoneSystem.GetGroundHeight(roadPoint.position);
        roadPoint.position.y = groundHeight;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("New height for Roadpoint " + roadPoint.position.y);
    }

    public static void LevelAreaNearRoadPoint(RoadPoint roadPoint, ZoneSystem zoneSystem)
    {
        Vector3 position = roadPoint.position;
        Vector3 normal;
        Heightmap.Biome biome;
        Heightmap.BiomeArea biomeArea;
        Heightmap hmap;
        
        zoneSystem.GetGroundData(ref position, out normal, out biome, out biomeArea, out hmap);
        
        hmap.WorldToVertex(roadPoint.position, out int centerX, out int centerY);
        
        float levelingRadius = 10f;  
        float heightmapScale = hmap.m_scale;  
        float scaledRadius = levelingRadius / heightmapScale;
        int gridRadius = Mathf.CeilToInt(scaledRadius);  
        
        Vector3 localWorldPos = roadPoint.position - hmap.transform.position;
        
        int heightmapWidth = hmap.m_width + 1;
        
        Vector2 centerPoint = new Vector2(centerX, centerY);
        for (int currentY = centerY - gridRadius; currentY <= centerY + gridRadius; ++currentY)
        {
            for (int currentX = centerX - gridRadius; currentX <= centerX + gridRadius; ++currentX)
            {
                if (Vector2.Distance(centerPoint, new Vector2(currentX, currentY)) <= scaledRadius && 
                    currentX >= 0 && currentY >= 0 && currentX < heightmapWidth && currentY < heightmapWidth)
                {
                    float targetHeight = localWorldPos.y;
                    
                    hmap.SetHeight(currentX, currentY, targetHeight);
                }
            }
        }
        
        hmap.RebuildCollisionMesh();
        hmap.RebuildRenderMesh();
    }
    
    public static void LevelAreaNearRoadPoint(RoadPoint roadPoint, Heightmap hmap)
    {
        
        hmap.WorldToVertex(roadPoint.position, out int centerX, out int centerY);
        
        float levelingRadius = 10f;  
        float heightmapScale = hmap.m_scale;  
        float scaledRadius = levelingRadius / heightmapScale;
        int gridRadius = Mathf.CeilToInt(scaledRadius);  
        
        Vector3 localWorldPos = roadPoint.position - hmap.transform.position;
        
        int heightmapWidth = hmap.m_width + 1;
        
        Vector2 centerPoint = new Vector2(centerX, centerY);
        for (int currentY = centerY - gridRadius; currentY <= centerY + gridRadius; ++currentY)
        {
            for (int currentX = centerX - gridRadius; currentX <= centerX + gridRadius; ++currentX)
            {
                if (Vector2.Distance(centerPoint, new Vector2(currentX, currentY)) <= scaledRadius && 
                    currentX >= 0 && currentY >= 0 && currentX < heightmapWidth && currentY < heightmapWidth)
                {
                    float targetHeight = localWorldPos.y;
                    
                    hmap.SetHeight(currentX, currentY, targetHeight);
                }
            }
        }
        
        hmap.RebuildCollisionMesh();
        hmap.RebuildRenderMesh();
    }

    
    public static void RenderRoadPoint(RoadPoint roadPoint)
    {
        if (roadPoint.pointRendered)
        {
            return;
        }
        
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Modifying terrain at point " + roadPoint.position);
        
        Heightmap heightmap = Heightmap.FindHeightmap(roadPoint.position);
        if (heightmap == null)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Heightmap at position " + roadPoint.position + " is null");
            return;
        }
        
        TerrainComp terrainComp = heightmap.GetAndCreateTerrainCompiler();
        if (terrainComp == null || !terrainComp.IsOwner())
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Terrain compiler is null");
            return;
        }
        
        TerrainOp.Settings modifierSettings = new TerrainOp.Settings
        {
            m_paintCleared = true,
            m_paintType = TerrainModifier.PaintType.Paved,
            m_paintRadius = roadPoint.width,
            m_paintHeightCheck = true,
            m_square = false,
        };

        // GameObject gameObject = new GameObject();
        // TerrainModifier terrainModifier = gameObject.AddComponent<TerrainModifier>();
        // terrainModifier.m_level = true;
        // terrainModifier.m_levelRadius = roadPoint.width;
        //
        // float slopeDelta;
        // Vector3 slopeDirection;
        // ZoneSystem.instance.GetTerrainDelta(roadPoint.position, terrainModifier.m_levelRadius, out slopeDelta, out slopeDirection);
        //
        // if (slopeDirection != Vector3.zero)
        // {
        //     Quaternion slopeRotation = Quaternion.LookRotation(Vector3.Cross(Vector3.right, slopeDirection), slopeDirection);
        //     
        //     gameObject.transform.rotation = slopeRotation;
        // }
        
        if (heightmap != null)
        {
            terrainComp.DoOperation(roadPoint.position, modifierSettings);
            LevelAreaNearRoadPoint(roadPoint, heightmap);
            roadPoint.pointRendered = true;
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
        public bool roadRendered;
    }

    public class RoadPoint
    {
        public Vector3 position;
        public Vector2i zoneID;
        public float width;
        public Road parentRoad;
        public bool pointRendered = false;
    }
    
}
