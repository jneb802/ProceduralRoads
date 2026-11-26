using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The one question every road, staircase, bridge, ford span and viaduct asks of ground, on every path (generation, a
/// manual road, the final check): may the network enter or change it. Two sources answer it, each at its own grain and
/// neither widened to the other's: a location's footprint (<see cref="RoadLocationProtection"/>: every location but those a road is
/// planned between, its targets; a footprint, never its whole zone) and the zones the network goes round
/// (<see cref="NetworkCutoff"/>: what players built or changed and the world explored before the network, whole zones). A
/// road's line asks <see cref="BlocksRoute"/> as it is searched and <see cref="BlocksRoad"/> as it is shaped; a structure's
/// pieces ask <see cref="StructureRefusal"/>. The one owner of the ground the network may enter (docs/OWNERS.md).
/// </summary>
public static class NetworkGround
{
    /// <summary>Whether a searched step or jump from <paramref name="a"/> to <paramref name="b"/> may not be taken: it comes
    /// within <paramref name="clearance"/> of a location's footprint but its targets' (<see cref="RoadLocationProtection.BlocksRoute"/>)
    /// or enters a zone the network goes round (<see cref="NetworkCutoff.AvoidsSegment"/>, a manual road's ends
    /// <paramref name="start"/> and <paramref name="end"/> exempt).</summary>
    internal static bool BlocksRoute(Vector2 a, Vector2 b, float clearance, RoadLocationProtection.Targets locations, Vector2? start, Vector2? end) =>
        RoadLocationProtection.BlocksRoute(a, b, clearance, locations) || NetworkCutoff.AvoidsSegment(a, b, start, end);

    /// <summary><see cref="BlocksRoute(Vector2, Vector2, float, RoadLocationProtection.Targets, Vector2?, Vector2?)"/> for a step
    /// from a point the caller found no location's footprint near (<paramref name="clearOfLocations"/>: no footprint within the
    /// clearance and the step's length of <paramref name="a"/>, <see cref="RoadLocationProtection.NoneWithin"/>), where the
    /// footprints' answer is known to be no: the same answer, the footprints not walked again for each step from it.</summary>
    internal static bool BlocksRoute(Vector2 a, Vector2 b, float clearance, RoadLocationProtection.Targets locations, Vector2? start, Vector2? end,
        bool clearOfLocations) =>
        (!clearOfLocations && RoadLocationProtection.BlocksRoute(a, b, clearance, locations)) || NetworkCutoff.AvoidsSegment(a, b, start, end);

    /// <summary>Whether a stretch of a road's shaped line from <paramref name="a"/> to <paramref name="b"/> may not be laid: it
    /// comes within the road's clearance of a location's footprint as the final check reads it (<see cref="RoadLocationProtection.BlocksRoad"/>,
    /// between <paramref name="start"/> and <paramref name="end"/>) or enters a zone the network goes round
    /// (<see cref="NetworkCutoff.AvoidsSegment"/>).</summary>
    internal static bool BlocksRoad(Vector2 a, Vector2 b, float width, Vector2 start, Vector2 end, RoadLocationProtection.Targets locations) =>
        RoadLocationProtection.BlocksRoad(a, b, width, start, end, locations) || NetworkCutoff.AvoidsSegment(a, b, start, end);

    /// <summary>The refusal of a structure in a location its road does not go to.</summary>
    internal const string InALocation = "in a location its road does not go to";
    /// <summary>The refusal of a structure in a zone the network goes round.</summary>
    internal const string InAvoidedZone = "in a zone the network goes round";

    /// <summary>
    /// Why a structure's <paramref name="pieces"/> may not be laid where they are, with the piece's place in
    /// <paramref name="at"/>; null where they may. A piece in a location its road does not go to, by its body or the ground it
    /// stands on (<see cref="RoadLocationProtection.StructureEnters"/>, <paramref name="own"/> the road's targets), at the
    /// footprint's grain; or a piece whose body reaches a zone the network goes round (StructurePlans.ZonesOf, every zone its
    /// bodies are laid in, debris included; <see cref="NetworkCutoff.Protected"/>, the final check's reader), at the zone's.
    /// A staircase's cuts reach as far as the terrain writer's ground (<see cref="RoadTerrainModifier.StairCutReach"/>),
    /// and ask the same planning zones.
    /// </summary>
    internal static string? StructureRefusal(IEnumerable<StructurePiece> pieces, IEnumerable<RoadLocationProtection.Targets> own, out Vector2 at, IEnumerable<Vector3>? cuts = null)
    {
        at = default;
        var all = new List<StructurePiece>(pieces);
        if (RoadLocationProtection.StructureEnters(all, own, out RoadLocationProtection.Footprint hit) is StructurePiece inside)
        {
            at = new Vector2(inside.Position.x, inside.Position.z);
            return FormattableString.Invariant($"{InALocation}: its {inside.Prefab} at ({at.x:F1}, {at.y:F1}), the location at ({hit.Centre.x:F0}, {hit.Centre.y:F0})");
        }
        if (!NetworkCutoff.AvoidsAny) return null;
        foreach (StructurePiece piece in all)
        {
            var zones = new HashSet<Vector2s>();
            StructurePlans.BodyZones(piece, zones);
            if (NetworkCutoff.Protected(zones) is not { Count: > 0 } kept) continue;
            at = new Vector2(piece.Position.x, piece.Position.z);
            return FormattableString.Invariant($"{InAvoidedZone}: its {piece.Prefab} at ({at.x:F1}, {at.y:F1}), zone ({kept[0].x}, {kept[0].y})");
        }
        if (cuts != null)
        {
            float reach = RoadTerrainModifier.StairCutReach;
            foreach (Vector3 cut in cuts)
                foreach (float dx in new[] { -reach, reach })
                    foreach (float dz in new[] { -reach, reach })
                    {
                        Vector2s zone = ZoneSystem.GetZone(new Vector3(cut.x + dx, 0f, cut.z + dz));
                        if (!NetworkCutoff.Avoids(zone)) continue;
                        at = new Vector2(cut.x, cut.z);
                        return FormattableString.Invariant($"{InAvoidedZone}: its cut at ({at.x:F1}, {at.y:F1}), zone ({zone.x}, {zone.y})");
                    }
        }
        return null;
    }
}
