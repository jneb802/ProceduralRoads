using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A Location's own parts, pinned: the one part matcher (<see cref="LocationPrefabLevelling.IsPart"/>) asked the same way by the
/// reader (<see cref="PlacedLocationReader.Classify"/>, every branch), the rock pass (<see cref="RoadRockProbe.PartOfLocation"/>) and
/// the bake's vegetation (<see cref="PlacedLocationReader.PartOfPlaced"/>): only a part of the object's own prefab counts, and a part
/// the game snaps to the ground is matched on x and z only (<see cref="LocationPrefabLevelling.PartOffset"/>). The prediction's
/// record (<see cref="PlacedLocationReader.Predicted"/>, the one record builder <see cref="PlacedLocationReader.Record"/>), and the
/// rock pass's column for a Location's snapping stakes (<see cref="RoadRockProbe.Candidates"/>).
///
/// What runs here and what does not: the matcher, the classifier, the bake's part test, the record builder, the prediction's
/// assembly and the rock pass's queries are the mod's own code. The game's draw (LocationPrefabLevelling.PlacedOps, loading the
/// prefab), the rocks' boxes from their colliders (PlacedLocationReader.OwnRocks) and the physics queries are not: they need the
/// game, and the in-game check (seededturn DESIGN.md) covers them.
/// </summary>
public class LocationPartsTests : IDisposable
{
    private const string Crypt = "Crypt4";
    private const string Stone = "Pickable_StoneRock";
    private const string Boulder = "rock4_forest";
    private const string Stakes = "dvergrprops_wood_stakewall";
    private static readonly int ProxyHash = "LocationProxy".GetStableHashCode();
    private static readonly Vector3 Root = new(100f, 50f, 200f);
    private static readonly Quaternion Turn = Quaternion.Euler(0f, 90f, 0f);
    private static readonly Vector3 BoulderLocal = new(4f, 0.5f, 2f), StakesLocal = new(-6f, 1f, 3f);

    private readonly ValheimWorldScope _scope = new();

    public LocationPartsTests()
    {
        _scope.WithWorld(new WorldGenerator()).WithZoneSystem().WithZdos().WithNetwork(server: true);
        LocationPrefabLevelling.ForgetParts();
        LocationPrefabLevelling.SetParts(Crypt, new[]
        {
            new LocationPrefabLevelling.Part(BoulderLocal, Boulder.GetStableHashCode(), null),
            new LocationPrefabLevelling.Part(StakesLocal, Stakes.GetStableHashCode(), 0.1f),
        });
    }

    public void Dispose()
    {
        LocationPrefabLevelling.ForgetParts();
        PlacedLocationReader.Reset();
        _scope.Dispose();
    }

    private static Vector3 Spot(Vector3 local) => Root + Turn * local;

    /// <summary>
    /// A vegetation stone standing within the match radius of one of a crypt's part spots is not the crypt's: not for the matcher,
    /// not for the reader (a clearable stone there is the world's, a rock-named one is not the Location's own rock), not for the
    /// rock pass, not for the bake's vegetation. The part's own prefab at the same spot is the crypt's for all of them. Before,
    /// the reader's clearable branch, the rock pass and the bake matched on position alone (the Crypt4 stone on Run61).
    /// </summary>
    [Fact]
    public void AVegetationStoneAtAPartSpotIsNotAPartForTheReaderTheRockPassOrTheBake()
    {
        Vector3 at = Spot(BoulderLocal) + new Vector3(0.2f, 0.1f, -0.2f);
        int stone = Stone.GetStableHashCode(), boulder = Boulder.GetStableHashCode();
        // The matcher.
        Assert.False(LocationPrefabLevelling.IsPart(Crypt, Root, Turn, at, stone));
        Assert.True(LocationPrefabLevelling.IsPart(Crypt, Root, Turn, at, boulder));
        Assert.False(LocationPrefabLevelling.IsPart(Crypt, Root, Turn, at + new Vector3(0.6f, 0f, 0f), boulder));
        Assert.Null(LocationPrefabLevelling.IsPart("NotReplayed", Root, Turn, at, boulder));
        // The reader: the stone as a clearable rock, and as a rock-named part of a kind no road clears.
        Func<bool?> Asks(int prefab) => () => LocationPrefabLevelling.IsPart(Crypt, Root, Turn, at, prefab);
        Assert.Equal(PlacedLocationReader.Own.World, PlacedLocationReader.Classify(Stone, clearable: true, wearNTear: false, Asks(stone)));
        Assert.Equal(PlacedLocationReader.Own.None, PlacedLocationReader.Classify(Stone, clearable: false, wearNTear: false, Asks(stone)));
        Assert.Equal(PlacedLocationReader.Own.Rock, PlacedLocationReader.Classify(Boulder, clearable: true, wearNTear: false, Asks(boulder)));
        Assert.Equal(PlacedLocationReader.Own.OwnRock, PlacedLocationReader.Classify(Boulder, clearable: false, wearNTear: false, Asks(boulder)));
        // Where the Location's parts are not known, a clearable boulder is its rock, as before.
        Assert.Equal(PlacedLocationReader.Own.Rock, PlacedLocationReader.Classify(Stone, clearable: true, wearNTear: false, () => null));
        // The rock pass.
        Assert.False(RoadRockProbe.PartOfLocation(Crypt, Root, Turn, at, Stone));
        Assert.True(RoadRockProbe.PartOfLocation(Crypt, Root, Turn, at, Boulder));
        // The bake's vegetation, at the crypt's saved placement (its LocationProxy).
        var centre = new Vector2(Root.x, Root.z);
        Vector2s zone = ZoneSystem.GetZone(Root);
        ZDO proxy = ZDOMan.instance!.CreateNewZDO(Root, ProxyHash);
        proxy.SetRotation(Turn);
        Assert.False(PlacedLocationReader.PartOfPlaced(zone, centre, Crypt, placed: true, at, stone));
        Assert.True(PlacedLocationReader.PartOfPlaced(zone, centre, Crypt, placed: true, at, boulder));
        Assert.False(PlacedLocationReader.PartOfPlaced(zone, centre, Crypt, placed: false, at, boulder));
    }

    /// <summary>A part the game snaps to the ground (a Dvergr stake wall: SnapToGround.SnappAll after SpawnLocation) is matched on
    /// x and z only, at any height (the Ghost and the Full ground, up to 17 m apart); every other part in all three axes.</summary>
    [Fact]
    public void ASnappingPartMatchesOnXAndZOnly()
    {
        int stakes = Stakes.GetStableHashCode(), boulder = Boulder.GetStableHashCode();
        foreach (float dy in new[] { 0f, 17f, -17f })
        {
            Assert.True(LocationPrefabLevelling.IsPart(Crypt, Root, Turn, Spot(StakesLocal) + new Vector3(0f, dy, 0f), stakes));
            Assert.Equal(0f, LocationPrefabLevelling.PartOffset(new LocationPrefabLevelling.Part(StakesLocal, stakes, 0.1f), Root, Turn,
                Spot(StakesLocal) + new Vector3(0.1f, dy, 0f)).y);
        }
        Assert.False(LocationPrefabLevelling.IsPart(Crypt, Root, Turn, Spot(StakesLocal) + new Vector3(0.6f, 0f, 0f), stakes));
        Assert.True(LocationPrefabLevelling.IsPart(Crypt, Root, Turn, Spot(BoulderLocal), boulder));
        Assert.False(LocationPrefabLevelling.IsPart(Crypt, Root, Turn, Spot(BoulderLocal) + new Vector3(0f, 0.6f, 0f), boulder));
    }

    /// <summary>
    /// The prediction's record (PlacedLocationReader.Predict, after the game's draw): the terrain modifiers the draw kept, each drawn
    /// part at root + turn x its place turned by turn x its own turn (a snapping one on the ground under it plus its offset), handed
    /// to the one owner of a Location's own rocks over the read square, then the prefab's own static rocks; and the record built by
    /// the one builder, as a read is (the read square from ReadHalf, its rock radius and its rule for small rocks).
    /// </summary>
    [Fact]
    public void APredictionsRecordHoldsTheDrawsOpsPartsAndRocks()
    {
        var instance = new ZoneSystem.LocationInstance
        {
            m_location = new ZoneSystem.ZoneLocation { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(Crypt), m_exteriorRadius = 12f },
            m_position = new Vector3(Root.x, 0f, Root.z),
        };
        TestLocations.Add(ZoneSystem.instance!, instance);
        var centre = new Vector2(Root.x, Root.z);
        var ops = new List<LevelOp> { new(0f, 0f, 0.5f, 6f, false, 9f, 3f, 0) };
        var boulder = new GameObject(Boulder);
        var stakes = new GameObject(Stakes);
        Quaternion own = Quaternion.Euler(0f, 30f, 0f);
        var parts = new List<PlacedLocationReader.DrawnPart>
        {
            new(boulder, BoulderLocal, own, null, new Vector3(1.5f, 1.5f, 1.5f), null),
            new(stakes, StakesLocal, Quaternion.identity, null, null, 0.1f),
        };
        var prefabRocks = new List<LocationRock> { new(centre + new Vector2(2f, 0f), 1f, 1f, 0f, 50f, 52f, "Rock_3") };
        float Ground(float x, float z) => 40f + 0.01f * x;
        var handed = new List<PlacedLocationReader.SavedObject>();
        (int x0, int z0, int w, int h, Vector2 centre, float root, float yaw)? asked = null;
        var ownRock = new LocationRock(centre, 1f, 1f, 0f, 50f, 51f, Boulder);
        PlacedLocationRecord record = PlacedLocationReader.Predicted(centre, Crypt, Root, Turn, ops, parts, prefabRocks, Ground,
            (objects, x0, z0, w, h, c, r, y) =>
            {
                handed.AddRange(objects);
                asked = (x0, z0, w, h, c, r, y);
                return new List<LocationRock> { ownRock };
            });

        Assert.Equal(ops, record.Ops);
        Assert.NotSame(ops, record.Ops);
        Assert.Equal(Crypt, record.Prefab);
        Assert.Equal(Root.y, record.Root);
        Assert.Equal(90f, record.Yaw, 3);
        Assert.Equal(12f, record.RockRadius);
        Assert.False(record.KeepSmallRocks);
        float half = PlacedLocationReader.ReadHalf(centre, 12f);
        Assert.Equal(Mathf.FloorToInt(centre.x - half), record.X0);
        Assert.Equal(Mathf.FloorToInt(centre.y - half), record.Z0);
        int w = Mathf.CeilToInt(2f * half) + 1;
        Assert.Equal(w + 1, record.W);
        Assert.Equal(w + 1, record.H);
        Assert.Equal((record.X0, record.Z0, w, w, centre, Root.y, record.Yaw), asked!.Value);

        Assert.Equal(2, handed.Count);
        Assert.Same(boulder, handed[0].Prefab);
        Assert.Equal(Spot(BoulderLocal), handed[0].Position);
        Assert.Equal((Turn * own).eulerAngles.y, handed[0].Rotation.eulerAngles.y, 3);
        Assert.Equal(new Vector3(1.5f, 1.5f, 1.5f), handed[0].Scale);
        // The stake wall: its x and z from the prefab, its height the ground under it plus its offset (the Ghost ground).
        Vector3 stake = Spot(StakesLocal);
        Assert.Equal(stake.x, handed[1].Position.x);
        Assert.Equal(stake.z, handed[1].Position.z);
        Assert.Equal(Ground(stake.x, stake.z) + 0.1f, handed[1].Position.y, 4);

        Assert.Equal(new[] { ownRock, prefabRocks[0] }, record.Rocks);
    }

    /// <summary>The rock pass's queries for one clearance box (RoadRockProbe.Judge gathers its colliders here): the box itself on
    /// the candidate layers, then the box's column (its footprint at any height, RockClearance.Column) for a Location's snapping
    /// stakes only, both judged.</summary>
    [Fact]
    public void TheRockPassLooksInABoxsColumnForSnappingStakes()
    {
        var box = new RockClearance(new Vector3(10f, 60f, -4f), new Vector3(2f, 1f, 3f), Quaternion.Euler(0f, 30f, 0f), 59f,
            wholeOnly: false, keepsLocationRocks: true);
        var asked = new List<(RockClearance box, bool stakesOnly)>();
        var into = new List<string> { "left over" };
        RoadRockProbe.Candidates(box, (b, stakesOnly) =>
        {
            asked.Add((b, stakesOnly));
            return new List<string> { stakesOnly ? "stake wall" : "boulder" };
        }, into);
        Assert.Equal(new[] { "boulder", "stake wall" }, into);
        Assert.Equal(2, asked.Count);
        Assert.False(asked[0].stakesOnly);
        Assert.Equal(box.Centre, asked[0].box.Centre);
        Assert.Equal(box.Half, asked[0].box.Half);
        Assert.True(asked[1].stakesOnly);
        Assert.Equal(box.Column.Centre, asked[1].box.Centre);
        Assert.Equal(box.Column.Half, asked[1].box.Half);
        Assert.Equal(box.Turn, asked[1].box.Turn);
        Assert.Equal(RockClearances.AnyHeight, asked[1].box.Half.y);
        Assert.Equal(box.Half.x, asked[1].box.Half.x);
        Assert.Equal(box.Half.z, asked[1].box.Half.z);
    }
}
