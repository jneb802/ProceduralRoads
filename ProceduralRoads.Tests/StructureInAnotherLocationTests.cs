using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using Xunit.Abstractions;

namespace ProceduralRoads.Tests;

/// <summary>
/// A structure keeps out of every location its road does not go to, as its road's line does (RoadLocationProtection.StructureEnters,
/// asked by RoadNetworkGenerator.InAnotherLocation): a bridge's railing, posts and end steps reach past the road's width, so a
/// location whose footprint keeps clear of the road's line may still hold a piece of its bridge or the ground the piece stands
/// on. The fixture: the synthetic river world with a temple and an altar either side and one road over a wooden bridge
/// between them. A small location is put where only the bridge reaches (a vertex its pieces stand on, further from the road's
/// line than location protection keeps a road), and the same location is given to a generation and to a manual road over the same
/// river: neither builds a bridge into it (parity). The river's bridge is tall: the support rule breaks every kit that fits it,
/// so no road is stored over it (RoadNetworkGenerator.WithNoKit) and the bridge is read from the marked road as planned,
/// before its refusals (<see cref="Planned"/>); a location's verdict is asked before the kit's (BuiltRoadRefusal's order), and a
/// bridge whose every piece the rule holds reaches no further from its road than location protection keeps a road on this river.
/// </summary>
public sealed class StructureInAnotherLocationTests : IDisposable
{
    /// <summary>The synthetic river world turned 45 degrees about the origin: the road crosses the river on the diagonal,
    /// so the vertices its bridge's pieces stand on (whole metres, rounded out round each piece) reach further across from
    /// the deck's line than on a square crossing.</summary>
    private sealed class TurnedWorld : WorldGenerator
    {
        private readonly SyntheticWorld _flat = new() { HasRiver = true, HasMountain = false };
        private static readonly float Cos = Mathf.Cos(Mathf.PI / 4f), Sin = Mathf.Sin(Mathf.PI / 4f);
        /// <summary>A point of the unturned world where it lies in this one.</summary>
        internal static Vector2 Turn(float x, float z) => new(x * Cos - z * Sin, x * Sin + z * Cos);
        private static Vector2 Back(float x, float z) => new(x * Cos + z * Sin, -x * Sin + z * Cos);
        public override float GetHeight(float x, float z) { Vector2 b = Back(x, z); return _flat.GetHeight(b.x, b.y); }
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false)
        { Vector2 b = Back(x, z); return _flat.GetBiome(b.x, b.y, oceanLevel, waterAlwaysOcean); }
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        { Vector2 b = Back(x, z); _flat.GetRiverWeight(b.x, b.y, out weight, out width); }
    }

    private readonly TurnedWorld _land = new();
    private readonly ValheimWorldScope _world;
    private readonly ITestOutputHelper _out;
    private const float Small = 0.1f;

    public StructureInAnotherLocationTests(ITestOutputHelper output)
    {
        _out = output;
        _world = new ValheimWorldScope().WithWorld(_land);
        _world.WithZoneSystem();
        foreach ((string name, float x, float z, float radius) in new[] { ("StartTemple", -200f, 0f, 25f), ("Eikthyrnir", 300f, 0f, 10f) })
        {
            Vector2 at = TurnedWorld.Turn(x, z);
            TestLocations.Add(ZoneSystem.instance!, new ZoneSystem.LocationInstance
            {
                m_location = new ZoneSystem.ZoneLocation { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(name), m_exteriorRadius = radius },
                m_position = new Vector3(at.x, _land.GetHeight(at.x, at.y), at.y),
            });
        }
        _world.WithZdos().WithNetwork(server: true);
        RoadNetworkGenerator.Reset();
        RoadLocationProtection.Source = null; RoadLocationProtection.Reset();
    }

    public void Dispose()
    {
        RoadLocationProtection.Source = null;
        RoadNetworkGenerator.Reset();
        RoadLocationProtection.Reset();
        RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
        _world.Dispose();
    }

    private static List<RoadCrossing> Decks() => BridgeLayout.DistinctSites(RoadNetworkGenerator.GetRoadCrossings()).FindAll(BridgeStructures.HasDeck);

    private static Island Island()
    {
        var island = new Island { Id = 1, CellSize = 8, WorldOffset = 0, Min = new Vector2(-80, -80), Max = new Vector2(240, 240) };
        for (int x = -10; x <= 30; x++) for (int z = -10; z <= 30; z++) island.Cells.Add(new Vector2Int(x, z));
        return island;
    }

    private static readonly Vector2[] Marks = { TurnedWorld.Turn(40f, 0f), TurnedWorld.Turn(160f, 0f) };

    /// <summary>The marked road between <see cref="Marks"/> as planned, before the refusals of a road as planned: the
    /// generator's one entry for a searched road (RoadNetworkGenerator.PlanSearchedPath), as ManualRoads.Prepare plans it.
    /// Prepare refuses it, now first for a trench on its way to the bridge, which no kit stands in.</summary>
    private RoadNetworkGenerator.RoadPlan Planned()
    {
        RoadTerrainModifier.ResetForWorld();
        RoadNetworkGenerator.MarkLocationsReady();
        // Prepare refuses the marked road first for the trench its approach cuts to the deck (the one list asks a trench
        // before a deck's kit); the plan below, before the refusals, still carries the bridge no kit stands in.
        Assert.Contains(RoadNetworkGenerator.TrenchRoadRefusal,
            Assert.Throws<InvalidOperationException>(() => ManualRoads.Prepare(Marks, false, new[] { Island() })).Message);
        float width = RoadNetworkGenerator.RoadWidth;
        RoadPathfinder finder = RoadNetworkGenerator.NewFinder(_land, RoadLocationProtection.Clearance(width));
        List<Vector2> path = finder.FindPath(Marks[0], Marks[1]) ?? throw new InvalidOperationException("no route between the marks");
        LocationLevelling.BeginRoad();
        RoadNetworkGenerator.RoadPlan planned = RoadNetworkGenerator.PlanSearchedPath(finder, path, path[0], 0f, path[path.Count - 1], 0f,
            false, false, false, false, width, default, Marks);
        Assert.NotNull(planned.Plans);
        return planned;
    }

    /// <summary>A vertex the crossing's pieces stand on (StructureGround.VerticesUnder of every piece it may be laid with)
    /// furthest from the road's line (its stored points and every crossing's line bank to bank, which the search and the
    /// final check keep a location's clearance from), and that distance.</summary>
    private (Vector2 vertex, float apart) FurthestVertexOf(RoadCrossing crossing, IEnumerable<RoadCrossing> all)
    {
        var road = RoadSpatialGrid.SnapshotAllRoadPoints().Select(rp => rp.p).ToList();
        var decks = all.Select(c => (c.FromBank, c.ToBank)).ToList();
        static float ToSegment(Vector2 v, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = ab.sqrMagnitude == 0f ? 0f : Mathf.Clamp01(Vector2.Dot(v - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(v, a + ab * t);
        }
        (Vector2 vertex, float apart) best = (default, -1f);
        foreach (StructurePiece piece in BridgeStructures.EveryPieceItMayLay(crossing, _land))
            foreach ((int x, int z) in StructureGround.VerticesUnder(piece.Position))
            {
                var v = new Vector2(x, z);
                float d = Mathf.Min(road.Min(p => Vector2.Distance(p, v)), decks.Min(k => ToSegment(v, k.FromBank, k.ToBank)));
                if (d > best.apart) best = (v, d);
            }
        return best;
    }

    /// <summary>The location: a footprint round <paramref name="vertex"/>, kept clear of the road's line by more than location
    /// protection keeps a road (its clearance for the road's width).</summary>
    private RoadLocationProtection.Footprint LocationAt(Vector2 vertex, float apart)
    {
        float clearance = RoadLocationProtection.Clearance(RoadNetworkGenerator.RoadWidth);
        _out.WriteLine(FormattableString.Invariant($"vertex ({vertex.x}, {vertex.y}) {apart:F2} m from the road; protection keeps a road {clearance + Small:F2} m off"));
        Assert.True(apart > clearance + Small, "no vertex of the bridge lies further from its road than protection keeps a road");
        return new RoadLocationProtection.Footprint(vertex, Small);
    }

    /// <summary>Generation with the location there builds no bridge into it. Here its search already keeps the road far
    /// enough off (the location is a step from where the search ran), and this river's tall bridges no kit stands in are
    /// searched round as well (RoadNetworkGenerator.WithNoKit), so this holds without the structure check too; the check
    /// on generation's path is the same function the refusal list asks (InAnotherLocation, the next tests), and the worlds
    /// where generation laid a ford into a location show it.</summary>
    [Fact]
    public void AGenerationBuildsNoBridgeIntoALocationItsRoadDoesNotGoTo()
    {
        RoadNetworkGenerator.RoadPlan planned = Planned();
        RoadCrossing before = Assert.Single(planned.Crossings, BridgeStructures.HasDeck);
        foreach (var p in planned.Plans!) RoadSpatialGrid.Commit(p);
        var (vertex, apart) = FurthestVertexOf(before, planned.Crossings);
        RoadSpatialGrid.Clear();
        RoadLocationProtection.Footprint location = LocationAt(vertex, apart);
        // The bridge as first planned stands in it: a vertex its pieces stand on is the location's centre.
        RoadLocationProtection.Set(new[] { location });
        Assert.NotNull(RoadLocationProtection.StructureEnters(BridgeStructures.EveryPieceItMayLay(before, _land),
            Array.Empty<RoadLocationProtection.Targets>(), out _));

        RoadNetworkGenerator.Reset();
        RoadLocationProtection.Source = () => new[] { location };
        RoadLocationProtection.Reset();
        RoadNetworkGenerator.GenerateRoads(force: true);

        Assert.DoesNotContain(Decks(), c => RoadCrossing.SameBanks(c, before, BridgeLayout.SameSiteRadius));
        foreach (RoadCrossing c in Decks())
        {
            Assert.Null(RoadLocationProtection.StructureEnters(BridgeStructures.EveryPieceItMayLay(c, _land), Array.Empty<RoadLocationProtection.Targets>(), out _));
            Assert.False(BridgeStructures.BuiltInNoKit(c, _land));
        }
    }

    [Fact]
    public void AManualRoadIsRefusedABridgeIntoALocationItDoesNotGoTo()
    {
        RoadNetworkGenerator.RoadPlan plan = Planned();
        RoadCrossing crossing = Assert.Single(plan.Crossings, BridgeStructures.HasDeck);
        foreach (var p in plan.Plans!) RoadSpatialGrid.Commit(p);
        var (vertex, apart) = FurthestVertexOf(crossing, plan.Crossings);
        RoadSpatialGrid.Clear();
        RoadLocationProtection.Footprint location = LocationAt(vertex, apart);

        RoadLocationProtection.Set(new[] { location });
        // The road as it was planned: the one list of a road's refusals (a manual road's and an approach trial's) refuses it
        // for its bridge, as generation's crossing bans do (RoadNetworkGenerator.InAnotherLocation), before its kit is asked.
        // The one list now refuses this road first for the trench its approach cuts to the deck (a ridge or trench is asked
        // before a structure's location or kit), so the list's verdict is the trench; the location verdict is still its own.
        string? why = RoadNetworkGenerator.BuiltRoadRefusal(plan.Plans!, plan.Crossings, out _);
        Assert.NotNull(why);
        Assert.StartsWith(RoadNetworkGenerator.TrenchRoadRefusal, why);
        Assert.Same(crossing, RoadNetworkGenerator.InAnotherLocation(plan.Plans!, plan.Crossings, out _, out _));
        // Marked again with the location there: refused for it, or searched round it to a deck no kit stands in, or to a
        // trench on its way to a deck, and refused for that, or planned with no bridge into it.
        try
        {
            ManualRoads.Plan again = ManualRoads.Prepare(Marks, false, new[] { Island() });
            foreach (RoadCrossing c in again.Crossings.Where(BridgeStructures.HasDeck))
                Assert.Null(RoadLocationProtection.StructureEnters(BridgeStructures.EveryPieceItMayLay(c, _land), Array.Empty<RoadLocationProtection.Targets>(), out _));
        }
        catch (InvalidOperationException refused)
        {
            Assert.True(refused.Message.Contains(RoadNetworkGenerator.StructureInAnotherLocation) || refused.Message.Contains(RoadNetworkGenerator.DeckWithNoKit)
                || refused.Message.Contains(RoadNetworkGenerator.TrenchRoadRefusal),
                refused.Message);
        }
    }

    /// <summary>
    /// The players' side of the same question (NetworkGround: a zone the network goes round, whole zones): a road whose
    /// bridge's body is laid in a zone it goes round is refused by the one list of a road's refusals as planned
    /// (RoadNetworkGenerator.BuiltRoadRefusal: a manual road, an approach trial, a staircase end's carried road) and banned by
    /// generation's crossing search (RoadNetworkGenerator.InAnotherLocation), as its road's line is kept out of it. Before, that
    /// list asked no zone of a structure, and only the manual road's final check read the bridge, in the one kit it would be
    /// built in and not at all while it waited for its ground.
    /// </summary>
    [Fact]
    public void ABridgeWhoseBodyIsInAZoneTheNetworkGoesRoundIsRefusedByTheOneRefusalList()
    {
        RoadNetworkGenerator.RoadPlan plan = Planned();
        RoadCrossing crossing = Assert.Single(plan.Crossings, BridgeStructures.HasDeck);
        // Out of the zone, it is refused only for its deck no kit stands in (the support rule refuses this plateau's railed
        // bridges, so the solve itself is empty).
        // The one list now refuses this road first for the trench its approach cuts to the deck (a ridge or trench is asked
        // before a structure's location or kit), so the list's verdict is the trench; the location verdict is still its own.
        Assert.StartsWith(RoadNetworkGenerator.TrenchRoadRefusal, RoadNetworkGenerator.BuiltRoadRefusal(plan.Plans!, plan.Crossings, out _));
        Assert.Null(RoadNetworkGenerator.InAnotherLocation(plan.Plans!, plan.Crossings, out _, out _));
        // A piece of any kit the crossing may be laid with: the one question reads every one of them.
        StructurePiece piece = BridgeStructures.EveryPieceItMayLay(crossing, _land).First();
        Vector2s zone = ZoneSystem.GetZone(piece.Position);
        Assert.NotEqual(ZoneSystem.GetZone(new Vector3(Marks[0].x, 0f, Marks[0].y)), zone);
        Assert.NotEqual(ZoneSystem.GetZone(new Vector3(Marks[1].x, 0f, Marks[1].y)), zone);

        using (NetworkCutoff.AvoidWhilePlanning(new HashSet<Vector2s> { zone }, 0f))
        {
            string? why = RoadNetworkGenerator.BuiltRoadRefusal(plan.Plans!, plan.Crossings, out _);
            Assert.NotNull(why);
            Assert.StartsWith(RoadNetworkGenerator.TrenchRoadRefusal, why);
            Assert.Same(crossing, RoadNetworkGenerator.InAnotherLocation(plan.Plans!, plan.Crossings, out _, out _));
        }
    }

    /// <summary>
    /// The another-location verdict reads the bridge on the ground as its road would be stored (docs/OWNERS.md, the refusals of
    /// a road as planned: one ground for every crossing check, RoadTerrainModifier.AsStored over what the road writes, as the
    /// no-kit verdict reads it): the road's own earthwork at the bridge's banks moves its end steps, so its pieces stand on
    /// vertices they do not stand on over the network as it stands. A location on such a vertex refuses the road for its bridge
    /// in the one list of a road's refusals (RoadNetworkGenerator.BuiltRoadRefusal), before its kit is asked; on the parent
    /// the verdict was read without the road's earthwork, missed the location, and the road was refused only for its kit.
    /// </summary>
    [Fact]
    public void ABridgeIsReadInAnotherLocationOnTheGroundAsItsRoadWouldBeStored()
    {
        RoadNetworkGenerator.RoadPlan plan = Planned();
        RoadCrossing crossing = Assert.Single(plan.Crossings, BridgeStructures.HasDeck);
        HashSet<(int, int)> Under()
        {
            var set = new HashSet<(int, int)>();
            foreach (StructurePiece piece in BridgeStructures.EveryPieceItMayLay(crossing, _land))
                foreach (var v in StructureGround.VerticesUnder(piece.Position)) set.Add(v);
            return set;
        }
        HashSet<(int, int)> bare = Under(), stored;
        // The road's own pieces as it would store them: their earthwork at the banks in the ground the solve reads.
        using (RoadTerrainModifier.AsStored(plan.Plans!.SelectMany(p => RoadSpatialGrid.PointsOf(p, intoLocation: false)).ToList(),
                   new List<(RoadSpatialGrid.RoadPoint, float)>()))
            stored = Under();
        var only = stored.Except(bare).OrderBy(v => v.Item1).ThenBy(v => v.Item2).ToList();
        Assert.NotEmpty(only);
        var vertex = new Vector2(only[0].Item1, only[0].Item2);
        _out.WriteLine(FormattableString.Invariant($"{only.Count} vertices under the bridge only on the stored ground; the location at ({vertex.x}, {vertex.y})"));

        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(vertex, Small) });
        Assert.Null(RoadNetworkGenerator.InAnotherLocation(plan.Plans!, plan.Crossings, out _, out _));
        // The one list now refuses this road first for the trench its approach cuts to the deck (a ridge or trench is asked
        // before a structure's location or kit), so the list's verdict is the trench; the location verdict is still its own.
        string? why = RoadNetworkGenerator.BuiltRoadRefusal(plan.Plans!, plan.Crossings, out _);
        Assert.NotNull(why);
        Assert.StartsWith(RoadNetworkGenerator.TrenchRoadRefusal, why);
    }
}
