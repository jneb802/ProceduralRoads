using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// One kind of structure the network builds out of vanilla pieces: bridges
/// today, stairs next. A source says what to place for the current network
/// and what the zone round it needs (vegetation kept off, rock cleared);
/// <see cref="StructurePlans"/> owns everything else -- buckets by zone, the
/// spawned record, appends, placement and the server bake -- for every source
/// alike. Pure logic.
/// </summary>
public interface IStructureSource
{
    /// <summary>Tag for this source's log lines, e.g. "[BRIDGES]".</summary>
    string LogTag { get; }

    /// <summary>Every piece this source plans for the current network.
    /// Deterministic from the network and the seed: never stored.</summary>
    IEnumerable<StructurePiece> Plan(WorldGenerator world, int seed);

    /// <summary>Circles to keep vegetation out of round one zone's pieces of this source.</summary>
    void AddClearAreas(List<StructurePiece> zonePieces, List<(Vector3 centre, float radius)> areas);

    /// <summary>Where a walker on this source's structures needs rock cleared, within reach of a point.</summary>
    void AddClearances(Vector3 centre, float radius, List<StructureClearance> clearances);
}

/// <summary>
/// Space on a structure that must be clear of rock, in one of two forms. A
/// walking-line point is cleared like a road at <see cref="Point"/> (heading
/// from its neighbours on <see cref="Line"/>), standing on <see cref="Surface"/>,
/// <see cref="Height"/> tall: a bridge deck. A box (<see cref="IsBox"/>) is
/// given whole, turned by <see cref="YawDegrees"/>: a stretch of a staircase.
/// <see cref="WholeOnly"/>: a rock here is removed whole, never carved.
/// </summary>
public readonly struct StructureClearance
{
    public readonly RoadSpatialGrid.RoadPoint Point;
    public readonly List<RoadSpatialGrid.RoadPoint>? Line;
    public readonly float Surface;
    public readonly float Height;
    public readonly bool WholeOnly;
    public readonly bool IsBox;
    public readonly Vector3 Centre;
    public readonly Vector3 Half;
    public readonly float YawDegrees;

    public StructureClearance(RoadSpatialGrid.RoadPoint point, List<RoadSpatialGrid.RoadPoint> line, float surface, float height, bool wholeOnly)
    {
        Point = point; Line = line; Surface = surface; Height = height; WholeOnly = wholeOnly;
        IsBox = false; Centre = default; Half = default; YawDegrees = 0f;
    }

    public StructureClearance(Vector3 centre, Vector3 half, float yawDegrees, float surface)
    {
        Point = default; Line = null; Surface = surface; Height = half.y * 2f; WholeOnly = false;
        IsBox = true; Centre = centre; Half = half; YawDegrees = yawDegrees;
    }
}
