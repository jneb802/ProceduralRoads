using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The one ground every structure is laid on: the finished ground, as the terrain writer leaves it once the network is
/// levelled, not the generator's. The road's earthwork raises the ground under a structure's ends (a landing, a levelled
/// apron, a fill to the road's floor) or cuts it away; a piece laid against the generator's ground is then wholly in the
/// ground (the game breaks it, dropping its resources: <see cref="BuriedPieces"/>), or a column stops short of the ground
/// cut from under it. Every kit's laying reads this: the bridges' and ford spans' columns, piers, footing, end stairs and
/// what is left out as buried (BridgeLayout's kits, <see cref="DvergrWoodBridge"/>, <see cref="DvergrViaduct"/>), and the
/// staircases' settling on the finished ground (<see cref="StairStructures.SettleOnFinishedGround"/>).
/// <para>Decisions the network is built from (where a structure's ends are, how many steps they take, whether a pier
/// stands) stay on the generator's ground: the road's levelling is made from them.</para>
/// </summary>
public static class StructureGround
{
    /// <summary>
    /// The finished ground at whole-metre vertices (the game's heightmap vertices): the generator's ground with the network's
    /// earthwork and the staircases' cuts as the terrain writer applies them (<see cref="RoadTerrainModifier.FinishedGround"/>),
    /// from the network as it now stands. NaN inside a place whose own levelling is not known. One caller at a time.
    /// </summary>
    public static Func<int, int, float> Vertices(WorldGenerator world) =>
        RoadTerrainModifier.FinishedGround((x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world));

    /// <summary>
    /// The finished ground at any point: the generator's ground there, changed by what the terrain writer changes at the
    /// vertices round it, spread between them as the game's terrain mesh spreads it (<see cref="BuriedPieces.MeshHeight"/>).
    /// Where the writer changes nothing it is the generator's ground exactly. A vertex inside a place whose own levelling is
    /// not known is taken as unchanged. One caller at a time.
    /// </summary>
    public static Func<Vector2, float> At(WorldGenerator world)
    {
        Func<int, int, float> finished = Vertices(world);
        var change = new Dictionary<(int, int), float>();
        float Change(int x, int z)
        {
            if (change.TryGetValue((x, z), out float known)) return known;
            float h = finished(x, z);
            float d = float.IsNaN(h) ? 0f : h - BiomeBlendedHeight.GetBlendedHeight(x, z, world);
            change[(x, z)] = d;
            return d;
        }
        return p => BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world) + BuriedPieces.MeshHeight(p.x, p.y, Change);
    }

    /// <summary>
    /// The finished ground under a structure with ends <paramref name="near"/> and <paramref name="far"/>
    /// (<see cref="StructureEnds.Of"/>): <see cref="At"/>, and no higher than an end's meeting height where the road runs on
    /// under its joining piece (<see cref="StructureEnds.End.UnderTheJoint"/>, <paramref name="halfWidth"/> either side of
    /// its line), which the road's levelling cuts the bank to.
    /// </summary>
    public static Func<Vector2, float> Of(WorldGenerator world, StructureEnds.End near, StructureEnds.End far, float halfWidth)
    {
        Func<Vector2, float> at = At(world);
        return p => UnderTheEnds(p, at(p), near, far, halfWidth);
    }

    /// <summary><paramref name="ground"/> at <paramref name="p"/>, no higher than an end's meeting height where the road
    /// runs on under its joining piece.</summary>
    internal static float UnderTheEnds(Vector2 p, float ground, StructureEnds.End near, StructureEnds.End far, float halfWidth)
    {
        foreach (StructureEnds.End end in new[] { near, far })
            if (end.IsSet && end.UnderTheJoint(p, halfWidth)) ground = Mathf.Min(ground, end.MeetHeight);
        return ground;
    }
}
