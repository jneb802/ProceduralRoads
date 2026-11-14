using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A structure on ground not known is a bug under the preload-list rule (a structure stands only in a location on the preload
/// list, whose ground the preload published): it lays nothing and says so as an error, once, naming the structure, the location
/// and the zone (StructureGround.NotLaid). Nothing waits for it. The fixture: the synthetic river world with a temple and an altar either side, one road
/// between them over a bridge, generated as the game generates; then a location whose ground is not read is put round a piece
/// of the bridge, as a network read back beside a location not on the list would have it.
/// </summary>
public sealed class StructureUnknownGroundTests : IDisposable
{
    // The river with its land held low enough that its railed bridge stands (BridgeTests.HeldRiver): the support rule
    // refuses the plateau's bridge, and this test is about a bridge on ground not known.
    private readonly SyntheticWorld _land = BridgeTests.HeldRiverWorld();
    private readonly ValheimWorldScope _world;

    public StructureUnknownGroundTests()
    {
        _world = new ValheimWorldScope().WithWorld(_land);
        _world.WithZoneSystem();
        foreach ((string name, float x, float z, float radius) in new[] { ("StartTemple", -200f, 0f, 25f), ("Eikthyrnir", 300f, 0f, 10f) })
            TestLocations.Add(ZoneSystem.instance!, new ZoneSystem.LocationInstance
            {
                m_location = new ZoneSystem.ZoneLocation { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(name), m_exteriorRadius = radius },
                m_position = new Vector3(x, _land.GetHeight(x, z), z),
            });
        _world.WithZdos().WithNetwork(server: true);
        RoadNetworkGenerator.Reset();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        PlacedLocations.Reset();
        RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
        _world.Dispose();
    }

    [Fact]
    public void AStructureOnGroundNotKnownLaysNothingAndSaysSoOnceAsAnError()
    {
        RoadNetworkGenerator.GenerateRoads(force: true);
        RoadCrossing bridge = Assert.Single(BridgeLayout.DistinctSites(RoadNetworkGenerator.GetRoadCrossings()), BridgeStructures.HasDeck);
        List<StructurePiece> pieces = BridgeStructures.Solve(bridge, _land, _land.GetSeed());
        StructurePiece piece = pieces.FirstOrDefault(p => p.Kind == StructurePieceKind.Post) ?? pieces[0];
        var centre = new Vector2(piece.Position.x, piece.Position.z);
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(centre, 2f) });

        // The doubles' log writes each line to the console with its level ("[ERROR] ..."): read there.
        TextWriter console = Console.Out;
        var written = new StringWriter();
        try
        {
            Console.SetOut(written);
            Assert.Empty(BridgeStructures.Solve(bridge, _land, _land.GetSeed()));
            Assert.Empty(BridgeStructures.Solve(bridge, _land, _land.GetSeed()));
        }
        finally { Console.SetOut(console); }
        string at = FormattableString.Invariant($"({bridge.FromBank.x:F1}, {bridge.FromBank.y:F1})");
        Vector2s zone = ZoneSystem.GetZone(piece.Position);
        string said = Assert.Single(written.ToString().Split('\n'), line => line.Contains("not laid") && line.Contains(at));
        Assert.StartsWith("[ERROR", said);
        Assert.Contains("not known", said);
        Assert.Contains(FormattableString.Invariant($"zone ({zone.x}, {zone.y})"), said);
        Assert.Contains(FormattableString.Invariant($"({centre.x:F1}, {centre.y:F1})"), said);
    }
}
