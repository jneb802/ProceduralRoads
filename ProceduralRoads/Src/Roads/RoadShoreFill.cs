using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Shallow water beside a place is land the road builds, not a reason to squeeze the road
/// out. The squeeze (<see cref="RoadPathfinder.SqueezeCorridor"/>, 12 m) still refuses road between a
/// place's footprint and water nearer than that, unchanged; only what counts as water there changes:
/// water no more than <see cref="FillDepth"/> under the ground a road may stand on is buildable, and
/// the road raises it as it passes (<see cref="Reach"/>); a river channel never is. One owner for the
/// search's test and the terrain writer's fill, so the land the search assumed is the land laid.
/// Pure over the world and the footprint index: any thread.
/// </summary>
public static class RoadShoreFill
{
    /// <summary>How far under the ground a road may stand on (the shallow line; a swamp's deep line) water beside a
    /// place is still land the road builds. Settable for tests; 0 is off (every water is water).</summary>
    internal static float FillDepth = 2f;

    /// <summary>Water the search would not walk at <paramref name="q"/> (<see cref="RoadWater.Walked"/>).</summary>
    internal static bool Wet(WorldGenerator world, Vector2 q, bool wade = true) => !RoadWater.Walked(world, q, wade);

    /// <summary>Water at <paramref name="q"/> a road standing at <paramref name="roadGround"/> cannot build out: a
    /// river, or ground more than <see cref="FillDepth"/> under the floor, or a fill past the earthwork cap.</summary>
    internal static bool Unbuildable(WorldGenerator world, Vector2 q, float roadGround, bool wade = true)
    {
        world.GetRiverWeight(q.x, q.y, out float river, out _);
        if (RoadWater.River(river)) return true;
        float h = world.GetHeight(q.x, q.y), floor = RoadWater.Floor(world, q, wade);
        if (h >= floor) return false;
        if (h < floor - FillDepth) return true;
        return RoadNetworkGenerator.EarthworkCap > 0f && roadGround - h > RoadNetworkGenerator.EarthworkCap;
    }

    /// <summary>
    /// Whether <paramref name="p"/> lies between a place's footprint and water less than the squeeze corridor apart
    /// (<paramref name="buildable"/>: only water the road cannot build out counts); <paramref name="gap"/>, how far
    /// it is from the footprint's edge (infinity where no footprint is that near). Inside a footprint it is not:
    /// the search's own ends reach their centres there, and every other footprint is refused anyway.
    /// </summary>
    internal static bool Squeezed(WorldGenerator world, Vector2 p, bool buildable, out float gap, bool wade = true)
    {
        gap = float.PositiveInfinity;
        float corridor = RoadPathfinder.SqueezeCorridor;
        if (corridor <= 0f) return false;
        gap = RoadSiteProtection.EdgeDistance(p, corridor);
        if (gap < 0f || gap >= corridor) return false;
        float reach = corridor - gap, here = world.GetHeight(p.x, p.y);
        for (int i = 0; i < 8; i++)
        {
            float a = i * Mathf.PI / 4f;
            var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            for (float r = reach * 0.5f; r <= reach + 0.01f; r += reach * 0.5f)
            {
                Vector2 q = p + dir * r;
                if (buildable && FillDepth > 0f ? Unbuildable(world, q, here, wade) : Wet(world, q, wade)) return true;
            }
        }
        return false;
    }

    /// <summary>Whether the search goes through <paramref name="p"/> only because the shallows beside it are built
    /// out: squeezed by water, not by water a road cannot build out.</summary>
    internal static bool LetThrough(WorldGenerator world, Vector2 p, out float gap) =>
        Squeezed(world, p, buildable: false, out gap) && !Squeezed(world, p, buildable: true, out _);

    /// <summary>
    /// How much wider a road point's side slope is (on top of its blend margin) where the search let it through on
    /// built-out shallows (<see cref="LetThrough"/>): out to the squeeze corridor past the place's edge, at most the
    /// fill spread's widest (<see cref="RoadEarthworkNoise.FillSpreadMax"/>; a road's centre keeps the site clearance
    /// off a footprint, so that reaches the corridor). 0 elsewhere. Read by the fill-spread owner
    /// (<see cref="RoadTerrainModifier.FillExtras"/>), so the zone gather's reach is unchanged. The same test as the
    /// search's, so a network the old squeeze built has no point that gets it.
    /// </summary>
    internal static float Reach(WorldGenerator world, Vector2 p, float width)
    {
        if (FillDepth <= 0f || !LetThrough(world, p, out float gap)) return 0f;
        return Mathf.Clamp(RoadPathfinder.SqueezeCorridor - gap - width * 0.5f, 0f, RoadEarthworkNoise.FillSpreadMax);
    }
}
