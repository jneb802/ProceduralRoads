using System;

namespace ProceduralRoads;

/// <summary>The structures that may be laid weathered (the Structures.Weathered setting).</summary>
[Flags]
public enum WeatheredStructures
{
    None = 0,
    Bridges = 1,
    Stairs = 2,
}

/// <summary>
/// Which structures are laid weathered, as a ruin a player can repair: bridges and viaducts with pieces missing and the rest
/// worn as a vanilla location's (<see cref="BridgeLayout.Ruin"/>), staircases worn the same way with railings missing
/// (<see cref="StairWear"/>). One not listed is laid whole and at full health. Read when a structure is first built: one
/// already built keeps its pieces. Set from the config (ProceduralRoadsPlugin); both by default.
/// </summary>
public static class StructureWear
{
    public static WeatheredStructures Weathered = WeatheredStructures.Bridges | WeatheredStructures.Stairs;

    public static bool Bridges => (Weathered & WeatheredStructures.Bridges) != 0;
    public static bool Stairs => (Weathered & WeatheredStructures.Stairs) != 0;
}
