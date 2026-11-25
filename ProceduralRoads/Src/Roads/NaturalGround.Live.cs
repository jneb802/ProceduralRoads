// NaturalGround's game-only part: the game's loaded terrain, read only in game (not linked into the offline tests, whose
// ZoneSystem and Heightmap have no loaded terrain). Barred everywhere else at compile time (BannedSymbols.txt).
#pragma warning disable RS0030
using UnityEngine;

namespace ProceduralRoads;

internal static partial class NaturalGround
{
    /// <summary>The game's live terrain where its zone is loaded (ZoneSystem.GetGroundHeight): the finished terrain in game, for
    /// the rock pass, the game's own physics and diagnostics. False where nothing is loaded.</summary>
    internal static bool Live(Vector3 at, out float height) => ZoneSystem.instance.GetGroundHeight(at, out height);

    /// <summary><see cref="Live(Vector3, out float)"/>'s height alone, as the game gives it.</summary>
    internal static float Live(Vector3 at) => ZoneSystem.instance.GetGroundHeight(at);

    /// <summary>Diagnostics only: the loaded heightmap's height at a point (Heightmap.GetHeight).</summary>
    internal static bool Loaded(Vector3 at, out float height) => Heightmap.GetHeight(at, out height);
}
