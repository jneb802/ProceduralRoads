// Roads-only stand-in: the replay of a location's prefab (PlacedLocationReader.cs, not compiled here: it reads the game's assets).

using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

public static partial class PlacedLocationReader
{
    /// <summary>The parts each placed location kept, as a test sets them: the harness has no prefab to replay. A location none were
    /// set for cannot be read, as a prefab that cannot be loaded.</summary>
    internal static readonly Dictionary<Vector2, IReadOnlyList<LevelOp>> ReplayedOps = new();

    private static partial IReadOnlyList<LevelOp>? Replay(Vector2 centre, ZDO proxy) =>
        ReplayedOps.TryGetValue(centre, out IReadOnlyList<LevelOp>? ops) ? ops : null;
}
