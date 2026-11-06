using System.Collections.Generic;

namespace ProceduralRoads;

/// <summary>Preserve Valheim coordinate equality without x^y concentrating
/// diagonal cells in one bucket. Shared by search and location-footprint lookups.</summary>
internal sealed class RoadGridComparer : IEqualityComparer<Vector2i>
{
    public static readonly RoadGridComparer Instance = new();
    public bool Equals(Vector2i a, Vector2i b) => a.x == b.x && a.y == b.y;
    public int GetHashCode(Vector2i p) => unchecked((p.x * 73856093) ^ (p.y * 83492791));
}
