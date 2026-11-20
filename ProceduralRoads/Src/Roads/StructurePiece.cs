using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>What a structure piece is for. Saved as its number with a staircase's
/// pieces (StairStructures), and RoadRockClearing reads every value from StairStep
/// on as a staircase: add new kinds at the END only, never reorder or insert.</summary>
public enum StructurePieceKind
{
    Post,    // vertical pole segment: a column stacked down to the ground, or a railing post
    Beam,    // a beam: a deck's stringer or handrail, a stair's rail, a brace
    Deck,    // walkable plank plate resting on its bay's stringers
    Stair,   // a step piece from the road up onto the deck
    Debris,  // a collapsed pole settled on the riverbed, outside the fairway
    StairStep,    // a stone stair of a staircase (StairLayout)
    StairFloor,   // a landing of a staircase
    StairFiller,  // a block in the wedge of a staircase's turn
    StairSupport, // a pillar under a staircase piece, down into the ground
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
    /// <summary>The crossing whose bridge the piece is part of, null for any other structure: its kit is chosen when its
    /// first piece is built (BridgeStructures.ChooseKit). Plans only: not saved.</summary>
    public RoadCrossing? Crossing;
    /// <summary>A staircase's piece held out from a face, or what holds it (StairPiece.Cantilevered): its pillars are
    /// clipped into the face, not a column carried down to the ground. Generation only: not saved.</summary>
    public bool Cantilevered;
}
