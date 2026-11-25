using System.Collections.Generic;
using UnityEngine;
using Xunit;
namespace ProceduralRoads.Tests;

/// <summary>The earthwork's blend passes over a road point beyond the widest margin it can have before sampling its
/// noise, and every vertex comes out to the bit as when every point is judged in full.</summary>
[Collection("RoadStatics")]
public class BlendCullTests
{
    [Fact]
    public void PassingOverFarPointsChangesNoVertex()
    {
        var random = new System.Random(28);
        float Next(float lo, float hi) => lo + (float)random.NextDouble() * (hi - lo);
        try
        {
            for (int round = 0; round < 40; round++)
            {
                var points = new List<RoadSpatialGrid.RoadPoint>();
                var at = new Vector2(Next(-500f, 500f), Next(-500f, 500f));
                for (int i = 0; i < 80; i++)
                {
                    points.Add(new RoadSpatialGrid.RoadPoint(at, Next(2f, 8f), Next(20f, 60f), paintOnly: i % 17 == 0));
                    at += new Vector2(Next(-1.5f, 1.5f), Next(-1.5f, 1.5f));
                }
                float[]? fill = round % 2 == 0 ? null : new float[points.Count];
                if (fill != null) for (int i = 0; i < fill.Length; i++) fill[i] = i % 3 == 0 ? Next(0f, 4f) : 0f;
                for (int v = 0; v < 400; v++)
                {
                    var vertex = points[random.Next(points.Count)].p + new Vector2(Next(-14f, 14f), Next(-14f, 14f));
                    float ground = Next(10f, 70f);
                    RoadTerrainModifier.CullFarPoints = true;
                    float culled = RoadTerrainModifier.ModelledHeight(points, vertex, ground, fill);
                    RoadTerrainModifier.CullFarPoints = false;
                    float full = RoadTerrainModifier.ModelledHeight(points, vertex, ground, fill);
                    Assert.Equal(System.BitConverter.ToInt32(System.BitConverter.GetBytes(full), 0), System.BitConverter.ToInt32(System.BitConverter.GetBytes(culled), 0));
                }
            }
        }
        finally { RoadTerrainModifier.CullFarPoints = true; }
    }
}
