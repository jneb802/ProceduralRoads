using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A staircase laid weathered (<see cref="StructureWear"/>), as the bridges are a ruin a player can repair: every piece worn
/// as a vanilla location's (<see cref="BridgeLayout.LocationDamageMin"/> to <see cref="BridgeLayout.LocationDamageMax"/> of
/// its health), and some of its railing's rails gone (<see cref="RailLoss"/>), a flight's lower rails and the roof top standing
/// for two crossed rails among them. Nothing anything rests on goes missing: no stair, floor or filler (the way stays
/// walkable), no support (what holds it up stays), no post (a rail stands on its posts, never a post on a rail). Fixed per
/// staircase, by its ends: the same staircase is worn the same way on every build.
/// </summary>
public static class StairWear
{
    /// <summary>The share of a staircase's rails gone.</summary>
    public const float RailLoss = 0.2f;

    private const uint RoleHealth = 1, RoleRail = 2;
    /// <summary>The role a black marble cornice column's vine curtain is rolled by (StairSupport's vines).</summary>
    internal const uint RoleVine = 3;

    /// <summary>The one owner of weathering and ruin of stairs and bridges (docs/OWNERS.md).</summary>
    public static void Weather(List<StructurePiece> pieces, Vector2 from, Vector2 to)
    {
        uint seed = Mix(Mix(Mix(Mix(0x5EEDu, Key(from.x)), Key(from.y)), Key(to.x)), Key(to.y));
        pieces.RemoveAll(p => p.Kind == StructurePieceKind.Beam && Roll(seed, p, RoleRail) < RailLoss);
        foreach (StructurePiece p in pieces)
            p.HealthFraction = BridgeLayout.LocationHealth(Roll(seed, p, RoleHealth));
    }

    /// <summary>A roll in [0, 1) for a piece, keyed by where it stands and what it decides.</summary>
    private static float Roll(uint seed, StructurePiece p, uint role) => Roll(seed, p.Position, role);

    /// <summary>A roll in [0, 1) for a location on a staircase, keyed by where it is and what it decides: the same on every
    /// build, whether the staircase is weathered or not.</summary>
    internal static float Roll(Vector3 at, uint role) => Roll(0x5EEDu, at, role);

    /// <summary>The one roll: <paramref name="seed"/>, the location to the centimetre, and the role.</summary>
    private static float Roll(uint seed, Vector3 at, uint role)
    {
        uint h = Mix(Mix(Mix(Mix(seed, Key(at.x)), Key(at.y)), Key(at.z)), role);
        return (h >> 8) / 16777216f;
    }

    private static uint Key(float v) => unchecked((uint)Mathf.RoundToInt(v * 100f));

    private static uint Mix(uint h, uint v)
    {
        unchecked
        {
            h ^= v + 0x9E3779B9u + (h << 6) + (h >> 2);
            h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
            return h;
        }
    }
}
