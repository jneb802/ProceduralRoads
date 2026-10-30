using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Columns under pieces not sunk into the ground: a pillar column's whole base SupportBury under the lowest ground at its
/// corners, never by a pillar wholly under the ground (BuriedPieces.ColumnGoesOn). The layout sizes a column on the ground it
/// reads; where earthwork lowers that ground later (the staircase's own cuts, another's, a road's), the column is carried on
/// down to the FINISHED ground once, after every road and staircase is laid, by one owner (BuriedPieces.CarryColumns).
/// </summary>
public static partial class StairLayout
{
    /// <summary>The lowest pillar ends at least this far into the ground.</summary>
    public const float SupportBury = 0.25f;

    /// <summary>Half the width of a support column (stone and black marble are 1 m square).</summary>
    public const float SupportHalfWidth = 0.5f;
}
