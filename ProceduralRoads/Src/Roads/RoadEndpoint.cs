using UnityEngine;

namespace ProceduralRoads;

/// <summary>Finds nearby ground for a location whose authored centre lies in water.</summary>
public static class RoadEndpoint
{
    public static Vector2 FindGround(WorldGenerator world, Vector2 center, float radius)
    {
        if (IsGround(world, center)) return center;
        float reach = Mathf.Max(0, radius) + RoadPathfinder.CellSize * 2;
        for (float ring = RoadPathfinder.CellSize; ring <= reach; ring += RoadPathfinder.CellSize)
        {
            // One sample per path cell of arc, so the ring is searched at the
            // same density it will be pathfound at. Scaling by the radius alone
            // left 40 m gaps at the outer rings of a large exterior radius.
            int samples = Mathf.Max(12, Mathf.CeilToInt(2 * Mathf.PI * ring / RoadPathfinder.CellSize));
            for (int i = 0; i < samples; i++)
            {
                float angle = i * Mathf.PI * 2 / samples;
                var point = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * ring;
                if (IsGround(world, point)) return point;
            }
        }
        // Keep the original destination when no nearby ground exists. A failed
        // search must remain visible, not become a road to some unrelated place.
        return center;
    }

    private static bool IsGround(WorldGenerator world, Vector2 point)
    {
        if (world.GetHeight(point.x, point.y) < RoadPathfinder.FloorFor(world.GetBiome(point.x, point.y)))
            return false;
        world.GetRiverWeight(point.x, point.y, out float weight, out _);
        return weight <= RoadConstants.RiverImpassableThreshold;
    }
}
