using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>What a structure piece is for. The values are not saved anywhere
/// (the append queue stores prefab and placement only), so new kinds may be added.</summary>
public enum StructurePieceKind
{
    Post,    // vertical pole segment, stacked down into the riverbed
    Beam,    // crossbeam tying a station's post pair under the deck
    Deck,    // walkable plank plate resting on a station pair
    Stair,   // a step piece from the road up onto the deck
    Debris,  // a collapsed pole settled on the riverbed, outside the fairway
    ViaductPier,     // a Dvergr viaduct's pier: footing, shafts, bands, corbels, iron screens (DvergrViaduct)
    ViaductDeck,     // a Dvergr viaduct's iron floor or the black marble course on it
    ViaductParapet,  // an arch along a Dvergr viaduct's edge, or a head beside it over a pier
    Flair,           // a decoration on a structure, as vanilla's locations dress theirs: a mist light, a vine (no support, no clearance)
}

/// <summary>One placed piece of a structure the network builds (a bridge today), a
/// persistent ZDO of a vanilla prefab once spawned. See StructurePlans.</summary>
public sealed class StructurePiece
{
    public StructurePieceKind Kind;
    public string Prefab = "";
    public Vector3 Position;
    public float YawDegrees;
    public float PitchDegrees;
    public float RollDegrees;
    /// <summary>WearNTear health fraction (drives the vanilla damage visuals).</summary>
    public float HealthFraction = 1f;
    /// <summary>Every zone the piece's own structure puts pieces in (one set per structure, shared by its pieces;
    /// debris on the riverbed does not count). Null: the piece never holds a structure back.
    /// See StructurePlans.BuildablePlanFor.</summary>
    public HashSet<Vector2s>? StructureZones;
}
