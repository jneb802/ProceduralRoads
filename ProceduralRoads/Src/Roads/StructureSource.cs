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

    /// <summary>Squares (the game's clear areas, centre and half-size) to keep vegetation out of round one zone's pieces of
    /// this source.</summary>
    void AddClearAreas(List<StructurePiece> zonePieces, List<(Vector3 centre, float radius)> areas);

    /// <summary>The walking surfaces of this source's structures standing in the zones <paramref name="near"/> (StairSupport.Walk:
    /// footprint, underside and walking surface), <paramref name="buildable"/> giving a zone's buildable planned pieces of this
    /// source (null where none): what vegetation obstructs when it reaches into one or its headroom (VegetationClearing.Judge).</summary>
    void AddWalksNear(IReadOnlyList<Vector2s> near, System.Func<Vector2s, List<StructurePiece>?> buildable, List<StairSupport.Walk> walks);

    /// <summary>Where a walker on this source's structures needs rock cleared, within reach of a point.</summary>
    void AddClearances(Vector3 centre, float radius, List<StructureClearance> clearances);
}

/// <summary>
/// A box of space on a structure that must be clear of rock, turned by <see cref="YawDegrees"/> (its local z along the
/// walk), over a walking surface at <see cref="Surface"/>: a staircase's stored walking box (StairLayout.AddClearance), or
/// a bridge's deck or end step (BridgeStructures.AddClearances). <see cref="WholeOnly"/>: a rock here is removed whole,
/// never carved, and the box is the structure's own, given as it is (RockClearances.Near): a deck's.
/// <see cref="KeepsLocationRocks"/>: a rock standing in a location's footprint is kept.
/// </summary>
public readonly struct StructureClearance
{
    public readonly Vector3 Centre;
    public readonly Vector3 Half;
    public readonly float YawDegrees;
    public readonly float Surface;
    public readonly bool WholeOnly;
    public readonly bool KeepsLocationRocks;

    public StructureClearance(Vector3 centre, Vector3 half, float yawDegrees, float surface, bool wholeOnly, bool keepsLocationRocks)
    {
        Centre = centre; Half = half; YawDegrees = yawDegrees; Surface = surface; WholeOnly = wholeOnly; KeepsLocationRocks = keepsLocationRocks;
    }
}
