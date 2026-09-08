// Stand-ins for mod classes we deliberately do NOT compile into the harness
// (they drag in Harmony/MonoBehaviour dependencies). Signatures mirror only
// the members RoadNetworkGenerator / RoadSpatialGrid actually call.

using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Shim for BridgePlacement (the real one instantiates prefabs through
/// ZNetScene). Only the members RoadNetworkGenerator calls.
/// </summary>
public static class BridgePlacement
{
    public static int SpawnInLoadedZones() => 0;
}

/// <summary>
/// Shim for the debug-info struct from Src/Debug/RoadPointDebugMarker.cs
/// (that file also contains a MonoBehaviour, so it is not compiled here).
/// Field list mirrors the real struct.
/// </summary>
public struct RoadPointDebugInfo
{
    public int PointIndex;
    public int TotalPoints;
    public float OriginalHeight;
    public float SmoothedHeight;
    public int WindowStart;
    public int WindowEnd;
    public int ActualWindowSize;
    public float[] WindowHeights;
}
