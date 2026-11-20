using System.Collections.Generic;

namespace ProceduralRoads;

/// <summary>A pathfinding cell held as a pair of ints (the staircases' and viaducts' cells, StairStructures and DvergrViaduct),
/// hashed as <see cref="RoadGridComparer"/> hashes a Vector2i: asked of every step the search prices, a tuple's own hash
/// (HashCode.Combine) was a share of the step's cost.</summary>
internal sealed class CellPairComparer : IEqualityComparer<(int, int)>
{
    public static readonly CellPairComparer Instance = new();
    public bool Equals((int, int) a, (int, int) b) => a.Item1 == b.Item1 && a.Item2 == b.Item2;
    public int GetHashCode((int, int) p) => unchecked((p.Item1 * 73856093) ^ (p.Item2 * 83492791));
}
