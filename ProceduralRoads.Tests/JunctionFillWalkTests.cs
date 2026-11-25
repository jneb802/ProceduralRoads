using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A junction's fill meets the roads it lies between within the walk rule (RoadJunctionFillet, judged as two pieces of road
/// meet: RoadNetworkGenerator.PiecesMeet). A road came down at the grade cap onto a level junction landing, its last metres
/// doubling back on themselves, and met a trunk; its inside corner's fill read the road's height by distance along the
/// road, so its arc lay 0.4 m under the road a metre beside it where the road still climbed. The road points are those
/// stored in that world (x, height, z): the trunk, and the joining road's last 30 m to its end on the trunk.
/// </summary>
[Collection("RoadStatics")]
public class JunctionFillWalkTests
{
    private const float Width = 4f;

    private sealed class Ground : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 70f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    private static readonly Vector3[] Trunk =
    {
            new Vector3(2567.61f, 68.87f, -381.92f),
            new Vector3(2568.39f, 68.91f, -382.44f),
            new Vector3(2569.18f, 68.95f, -382.97f),
            new Vector3(2569.97f, 68.99f, -383.49f),
            new Vector3(2570.76f, 69.04f, -384.02f),
            new Vector3(2571.58f, 69.11f, -384.54f),
            new Vector3(2572.42f, 69.19f, -385.03f),
            new Vector3(2573.28f, 69.28f, -385.48f),
            new Vector3(2574.15f, 69.38f, -385.90f),
            new Vector3(2575.05f, 69.49f, -386.28f),
            new Vector3(2575.96f, 69.58f, -386.62f),
            new Vector3(2576.88f, 69.68f, -386.93f),
            new Vector3(2577.82f, 69.76f, -387.19f),
            new Vector3(2578.76f, 69.84f, -387.42f),
            new Vector3(2579.72f, 69.92f, -387.60f),
            new Vector3(2580.68f, 69.98f, -387.75f),
            new Vector3(2581.61f, 70.02f, -387.87f),
            new Vector3(2582.54f, 70.06f, -387.99f),
            new Vector3(2583.47f, 70.09f, -388.11f),
            new Vector3(2584.40f, 70.11f, -388.23f),
            new Vector3(2585.33f, 70.13f, -388.35f),
            new Vector3(2586.26f, 70.14f, -388.47f),
            new Vector3(2587.19f, 70.14f, -388.60f),
            new Vector3(2587.88f, 70.15f, -388.68f),
            new Vector3(2588.57f, 70.15f, -388.73f),
            new Vector3(2589.49f, 70.15f, -388.80f),
            new Vector3(2590.41f, 70.15f, -388.87f),
            new Vector3(2591.33f, 70.16f, -388.93f),
            new Vector3(2592.24f, 70.17f, -389.00f),
            new Vector3(2593.16f, 70.17f, -389.07f),
            new Vector3(2594.08f, 70.17f, -389.13f),
            new Vector3(2595.00f, 70.17f, -389.20f),
            new Vector3(2595.92f, 70.15f, -389.29f),
            new Vector3(2596.83f, 70.14f, -389.45f),
            new Vector3(2597.73f, 70.13f, -389.66f),
            new Vector3(2598.61f, 70.12f, -389.93f),
            new Vector3(2599.48f, 70.11f, -390.26f),
            new Vector3(2600.32f, 70.12f, -390.64f),
            new Vector3(2601.14f, 70.15f, -391.08f),
            new Vector3(2601.92f, 70.20f, -391.57f),
            new Vector3(2602.67f, 70.28f, -392.10f),
            new Vector3(2603.39f, 70.37f, -392.69f),
            new Vector3(2604.07f, 70.49f, -393.32f),
            new Vector3(2604.70f, 70.62f, -393.99f),
            new Vector3(2605.27f, 70.77f, -394.62f),
            new Vector3(2605.83f, 70.92f, -395.26f),
            new Vector3(2606.49f, 71.07f, -395.97f),
            new Vector3(2607.18f, 71.20f, -396.65f),
            new Vector3(2607.90f, 71.32f, -397.30f),
            new Vector3(2608.65f, 71.44f, -397.92f),
            new Vector3(2609.42f, 71.54f, -398.50f),
            new Vector3(2610.22f, 71.62f, -399.05f),
            new Vector3(2611.05f, 71.68f, -399.56f),
            new Vector3(2611.89f, 71.73f, -400.03f),
            new Vector3(2612.76f, 71.77f, -400.47f),
    };
    private static readonly Vector3[] Joining =
    {
            new Vector3(2580.91f, 71.50f, -361.45f),
            new Vector3(2581.34f, 71.47f, -362.28f),
            new Vector3(2581.72f, 71.45f, -363.12f),
            new Vector3(2582.06f, 71.43f, -363.95f),
            new Vector3(2582.38f, 71.39f, -364.78f),
            new Vector3(2582.67f, 71.35f, -365.61f),
            new Vector3(2582.94f, 71.29f, -366.45f),
            new Vector3(2583.20f, 71.24f, -367.29f),
            new Vector3(2583.46f, 71.18f, -368.15f),
            new Vector3(2583.70f, 71.14f, -369.01f),
            new Vector3(2583.95f, 71.11f, -369.90f),
            new Vector3(2584.21f, 71.07f, -370.82f),
            new Vector3(2584.47f, 71.04f, -371.75f),
            new Vector3(2584.75f, 71.00f, -372.72f),
            new Vector3(2585.06f, 70.96f, -373.72f),
            new Vector3(2585.32f, 70.91f, -374.59f),
            new Vector3(2585.59f, 70.85f, -375.54f),
            new Vector3(2585.88f, 70.78f, -376.54f),
            new Vector3(2586.17f, 70.69f, -377.59f),
            new Vector3(2586.47f, 70.61f, -378.67f),
            new Vector3(2586.78f, 70.53f, -379.77f),
            new Vector3(2587.08f, 70.13f, -380.87f),
            new Vector3(2587.39f, 70.14f, -381.97f),
            new Vector3(2587.69f, 70.14f, -383.04f),
            new Vector3(2587.98f, 70.14f, -384.08f),
            new Vector3(2588.27f, 70.14f, -385.07f),
            new Vector3(2588.54f, 70.15f, -386.00f),
            new Vector3(2588.81f, 70.15f, -386.85f),
            new Vector3(2589.05f, 70.15f, -387.61f),
            new Vector3(2589.28f, 70.15f, -388.26f),
            new Vector3(2589.49f, 70.15f, -388.80f),
            new Vector3(2590.68f, 70.16f, -389.81f),
            new Vector3(2591.33f, 70.16f, -388.93f),
    };

    private static RoadSpatialGrid.PlannedPath Piece(IEnumerable<Vector3> at)
    {
        var list = at.ToList();
        var points = list.Select(p => new Vector2(p.x, p.z)).ToList();
        float length = 0f;
        for (int i = 1; i < points.Count; i++) length += Vector2.Distance(points[i - 1], points[i]);
        return new RoadSpatialGrid.PlannedPath(points, list.Select(p => p.y).ToList(), points.Select(_ => default(RoadPointDebugInfo)).ToList(), Width, length, false);
    }

    /// <summary>Every point of the fill and every road point, of both roads, within 1.5 m of it (the walk gate's reach) are
    /// within the grade cap over their gap plus a step: no step a player cannot walk up between the fill and a road.</summary>
    [Fact]
    public void AJunctionFillMeetsBothRoadsWithinTheWalkRule()
    {
        var world = new Ground();
        using var scope = new ValheimWorldScope().WithWorld(world);
        float before = RoadJunctionFillet.Radius;
        RoadLocationProtection.Reset();
        RoadSpatialGrid.Clear();
        try
        {
            RoadJunctionFillet.Radius = RoadSwitchbacks.DefaultMinBendRadius;
            RoadSpatialGrid.Commit(Piece(Trunk));
            var fills = RoadJunctionFillet.For(new List<RoadSpatialGrid.PlannedPath> { Piece(Joining) }, world);
            var roads = Trunk.Concat(Joining).ToList();
            var fill = fills.SelectMany(f => f.Points.Select((p, i) => new Vector3(p.x, f.Heights[i], p.y))).ToList();
            // The junction keeps the fill that meets both roads (its other corner and its carry onto the trunk).
            Assert.NotEmpty(fill);
            foreach (Vector3 q in fill)
                foreach (Vector3 r in roads.Concat(fill))
                {
                    float gap = Vector2.Distance(new Vector2(q.x, q.z), new Vector2(r.x, r.z));
                    if (gap > 1.5f) continue;
                    Assert.True(Mathf.Abs(q.y - r.y) <= RoadGrade.Configured * gap + StairLayout.StepUp + 0.01f,
                        $"fill at ({q.x:F2}, {q.z:F2}) {q.y:F2} m meets a road point at ({r.x:F2}, {r.z:F2}) {r.y:F2} m, {gap:F2} m away");
                }
        }
        finally { RoadJunctionFillet.Radius = before; RoadSpatialGrid.Clear(); }
    }
}
