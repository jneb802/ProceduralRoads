using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

public static partial class RoadSpatialGrid
{
    // A durable chain of content hashes BEFORE each append: parents[k] is the
    // version before append k+1, so a compiler stamped parents[k] carries
    // additions 1..k and is owed every point with addition > k. Original v1/v2
    // networks have no chain.
    private static List<int> s_appendParents = new();
}
