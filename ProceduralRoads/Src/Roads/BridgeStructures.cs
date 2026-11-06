using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Bridges as a structure source: the ruined wooden bridges of
/// <see cref="BridgeLayout"/> at the network's recorded crossings. Pure logic.
/// </summary>
public sealed class BridgeStructures : IStructureSource
{
    /// <summary>How far below a deck the deck clearance starts, metres: the
    /// deck's own beams and a rock the deck runs into.</summary>
    public const float DeckBelow = 1f;

    public string LogTag => "[BRIDGES]";

    public IEnumerable<StructurePiece> Plan(WorldGenerator world, int seed)
    {
        foreach (RoadCrossing crossing in BridgeLayout.DistinctSites(RoadNetworkGenerator.GetRoadCrossings()))
        {
            List<StructurePiece> bridge = Solve(crossing, world, seed);
            var bridgeZones = new HashSet<Vector2s>();
            foreach (StructurePiece piece in bridge)
            {
                // Debris on the riverbed is not part of the bridge: it never holds one back.
                if (piece.Kind != StructurePieceKind.Debris)
                    bridgeZones.Add(ZoneSystem.GetZone(piece.Position));
                piece.StructureZones = bridgeZones;
            }
            foreach (StructurePiece piece in bridge)
                yield return piece;
        }
    }

    /// <summary>The kits a crossing is built in (<see cref="KitFor"/>).</summary>
    public enum Kit
    {
        /// <summary>The plain wooden kit (<see cref="BridgeLayout"/>): every biome but the Mistlands.</summary>
        Wood,
        /// <summary>The Dvergr harbour pier's wooden pieces (<see cref="DvergrWoodBridge"/>): a Mistlands bridge that is not a
        /// viaduct, and a Mistlands ford's spanned footbridge.</summary>
        DvergrWood,
        /// <summary>The Dvergr viaduct (<see cref="DvergrViaduct"/>).</summary>
        DvergrViaduct,
    }

    /// <summary>
    /// The one owner of a crossing's kit. A Dvergr viaduct where <see cref="DvergrViaduct.Applies"/> (the Mistlands, piers
    /// from the 12 m floor to the ceiling, end stairs within their most). Else every Mistlands bridge, whatever kept it from
    /// being a viaduct, is built from the Dvergr pier's wooden pieces (its piles stack down to the ground at any height); every
    /// other biome keeps the plain wooden kit. A Mistlands ford laid as a span
    /// (<see cref="FordStyle.Span"/>, the short low footbridge) is Dvergr wood too; a wading or raised ford is road and has no kit's pieces.
    /// <para>Only <see cref="Solve"/> reads the Dvergr wood choice: that kit's deck height and ends are the plain wooden
    /// kit's (a ford span's its footbridge's), so <see cref="DeckHeight"/>, <see cref="Ends"/> and the generator ask only
    /// whether it is a viaduct.</para>
    /// </summary>
    public static Kit KitFor(RoadCrossing crossing, WorldGenerator world)
    {
        if (crossing == null) return Kit.Wood;
        if (crossing.Kit is Kit stored) return stored;
        if (DvergrViaduct.Applies(crossing, world)) return Kit.DvergrViaduct;
        if (world == null || world.GetBiome(crossing.Center.x, crossing.Center.y) != Heightmap.Biome.Mistlands)
            return Kit.Wood;
        if (crossing.Kind == CrossingKind.Bridge) return Kit.DvergrWood;
        if (crossing.Kind == CrossingKind.Ford && crossing.Style == FordStyle.Span) return Kit.DvergrWood;
        return Kit.Wood;
    }

    /// <summary>
    /// The pieces of one crossing, in its kit (<see cref="KitFor"/>). The one place the pieces are chosen: the plan, the
    /// append queue and the counts all come through here.
    /// </summary>
    public static List<StructurePiece> Solve(RoadCrossing crossing, WorldGenerator world, int seed) => KitFor(crossing, world) switch
    {
        Kit.DvergrViaduct => DvergrViaduct.Solve(crossing, world, seed),
        Kit.DvergrWood => DvergrWoodBridge.Solve(crossing, world, seed),
        _ => BridgeLayout.Solve(crossing, world, seed),
    };

    /// <summary>The deck's walking surface in the crossing's kit and kind (<see cref="Solve"/>'s choice): a ford span's
    /// footbridge (<see cref="BridgeLayout.SpanDeckHeight"/>) in either kit, a viaduct's, else the wooden kit's (the Dvergr
    /// wood kit's is the plain wooden kit's).</summary>
    public static float DeckHeight(RoadCrossing crossing, WorldGenerator world) =>
        crossing.Kind == CrossingKind.Ford ? BridgeLayout.SpanDeckHeight(crossing, world)
        : KitFor(crossing, world) == Kit.DvergrViaduct ? DvergrViaduct.DeckHeight(crossing, world) : BridgeLayout.DeckHeight(crossing, world);

    /// <summary>Half the deck's width in the crossing's kit: a viaduct's, the Dvergr wood kit's (its planks are
    /// <see cref="DvergrWoodBridge.BeamLength"/> beams laid across the deck), else the wooden kit's.</summary>
    public static float DeckHalfWidth(RoadCrossing crossing, WorldGenerator world) => KitFor(crossing, world) switch
    {
        Kit.DvergrViaduct => DvergrViaduct.HalfWidth,
        Kit.DvergrWood => DvergrWoodBridge.BeamLength * 0.5f,
        _ => BridgeLayout.DeckHalfWidth,
    };

    /// <summary>
    /// Both ends of a crossing structure as they meet the road (<see cref="StructureEnds"/>, the one owner), near (from-bank)
    /// end first, the road meeting each alike at its stairs' foot; kits differ in their deck and how many steps their stairs
    /// take (a viaduct's chosen from the road's arrival within its stair limit, the wooden kits' laid until their foot lands).
    /// Unset (default) for a waded or raised ford, which is road.
    /// </summary>
    public static (StructureEnds.End near, StructureEnds.End far) Ends(RoadCrossing crossing, WorldGenerator world)
    {
        if (crossing == null || world == null) return default;
        return crossing.Kind == CrossingKind.Bridge && KitFor(crossing, world) == Kit.DvergrViaduct ? DvergrViaduct.Ends(crossing, world) : BridgeLayout.Ends(crossing, world);
    }

    /// <summary>Pieces the plan puts at a crossing (0 for a ford that is not spanned).</summary>
    public static int PiecesAt(RoadCrossing crossing) =>
        WorldGenerator.instance == null ? 0 : Solve(crossing, WorldGenerator.instance, WorldGenerator.instance.GetSeed()).Count;

    /// <summary>
    /// Clear-areas around the plan so vegetation does not spawn through the
    /// deck (a bridge zone often carries no painted road points of its own).
    /// </summary>
    public void AddClearAreas(List<StructurePiece> zonePieces, List<(Vector3 centre, float radius)> areas)
    {
        Vector3? last = null;
        foreach (StructurePiece piece in zonePieces)
        {
            // Flair hangs on the structure's own pieces, whose areas already clear it: none of its own.
            if (piece.Kind == StructurePieceKind.Flair) continue;
            if (last.HasValue && Vector3.Distance(last.Value, piece.Position) < 2f)
                continue;
            // Wide enough for a two-lane deck: pieces sit up to DeckHalfWidth
            // either side of the crossing line, and the clearing has to keep
            // trees out of BOTH lanes, not just the centreline. A viaduct's
            // pieces reach a metre further (its course and arches, 3 m out).
            areas.Add((piece.Position, BridgeLayout.DeckHalfWidth + 1.5f));
            last = piece.Position;
        }
    }

    /// <summary>
    /// Bridge decks are not road points, so rocks on a bridge were never looked
    /// at. Each span in reach is walked every metre at deck height (the road's
    /// stored height at each bank, interpolated), from just under the deck; a
    /// rock there goes whole: bridges are few, and no rock is missed.
    /// </summary>
    public void AddClearances(Vector3 centre, float radius, List<StructureClearance> clearances)
    {
        var deck = new List<RoadSpatialGrid.RoadPoint>();
        var c = new Vector2(centre.x, centre.z);
        foreach (var crossing in RoadNetworkGenerator.GetRoadCrossings())
        {
            // Bridges of every kit (wood, Dvergr wood, viaduct) and a ford built as a footbridge.
            bool span = crossing.Kind == CrossingKind.Ford && crossing.Style == FordStyle.Span;
            if (crossing.Kind != CrossingKind.Bridge && !span) continue;
            Vector2 a = crossing.FromBank, b = crossing.ToBank;
            float length = Vector2.Distance(a, b);
            if (length < 1f) continue;
            // Nearest point of the span to the probe centre.
            float t0 = Mathf.Clamp01(Vector2.Dot(c - a, b - a) / (length * length));
            if (Vector2.Distance(a + (b - a) * t0, c) > radius) continue;
            // The deck's own height in its kit: the road ends at a stair's foot, below it.
            float height = WorldGenerator.instance != null
                ? DeckHeight(crossing, WorldGenerator.instance)
                : RoadSpatialGrid.TryGetRoadHeightWithin(a, 4f, out float road) ? road : RoadConstants.SeaLevel + BridgeLayout.DeckFreeboard;
            int n = Mathf.CeilToInt(length);
            for (int i = 0; i <= n; i++)
                deck.Add(new RoadSpatialGrid.RoadPoint(a + (b - a) * (i / (float)n), RoadSpatialGrid.DefaultRoadWidth, height));
        }
        // Headings come from every deck point in reach.
        foreach (var point in deck)
            clearances.Add(new StructureClearance(point, deck, point.h - DeckBelow, DeckBelow + RoadRockPolicy.ClearanceHeight, wholeOnly: true));
    }
}
