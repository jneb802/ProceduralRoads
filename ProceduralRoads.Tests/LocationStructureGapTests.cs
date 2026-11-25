using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// An approach onto a placed location keeps its own body off the location's built pieces (<see cref="LocationRock.Built"/>): what the
/// walker would hit, the piece's box against the walker's (LocationRock.InTheWay over the ground), no gap of its own past that; on the
/// way in to its own door (<see cref="PlacedLocationShape.InDoorCorridor"/>) the entrance's clearance. Refused with a reason of its own
/// where it comes too near. A Location's stakes are no obstacle (LocationRock.ClearedForAWay). From a
/// Mistlands Dvergr boss entrance placed at (-3328, 49.5, -5760) turned 225, as read in game: its door landing at
/// (-3345.0, -5774.0), at the foot of its own black marble stairs (pieces turned 195), whose approach was refused at a 3 m gap
/// with no room for the landing ("grade: no room for a staircase foot's level landing").
/// </summary>
public class LocationStructureGapTests
{
    private const string Prefab = "Mistlands_DvergrBossEntrance1";
    private static readonly Vector2 Centre = new(-3328f, -5760f);
    private const float Yaw = 225f, Level = 49.5f, Exterior = 32f, PieceYaw = 195f;
    /// <summary>Its main flat, as its prefab levels it (offset x, z, height, radius, smoothing radius and power).</summary>
    private static readonly LevelOp Flat = new(0f, 6.5f, 1f, 15f, false, 24f, 3f);
    private static readonly Vector2 DoorLanding = new(-3345.0f, -5774.0f);

    /// <summary>The entrance's own pieces nearest its door landing, where the game placed them (x, y, z), each a 2 m piece's box.</summary>
    private static readonly (string name, Vector3 at)[] Pieces =
    {
        ("blackmarble_stair", new Vector3(-3343.91f, 49.50f, -5774.50f)),
        ("blackmarble_2x2x2", new Vector3(-3343.91f, 48.50f, -5774.50f)),
        ("blackmarble_stair", new Vector3(-3342.50f, 49.50f, -5775.91f)),
        ("blackmarble_2x2x2", new Vector3(-3342.50f, 48.50f, -5775.91f)),
        ("blackmarble_stair", new Vector3(-3342.50f, 50.50f, -5773.08f)),
        ("blackmarble_stair", new Vector3(-3341.08f, 50.50f, -5774.50f)),
        ("blackmarble_2x2x2", new Vector3(-3344.06f, 52.50f, -5770.02f)),
        ("blackmarble_base_2", new Vector3(-3344.92f, 50.50f, -5770.52f)),
    };

    private static List<LocationRock> Built(bool built = true)
    {
        var rocks = new List<LocationRock>();
        foreach (var (name, at) in Pieces)
            rocks.Add(new LocationRock(new Vector2(at.x, at.z), 1f, 1f, PieceYaw, at.y - 1f, at.y + 1f, name, built: built));
        return rocks;
    }

    private static PlacedLocationShape Shape(List<LocationRock> rocks) =>
        PlacedLocationShape.Of(Centre, Yaw, Flat, Exterior, rocks, (_, _) => Level, false, Level, Prefab);

    /// <summary>A point in the location's own frame (x across, z out of its front), in the world.</summary>
    private static Vector2 At(float x, float z) => Centre + LocationRock.World(new Vector2(x, z), Yaw);

    /// <summary>What a staircase piece at <paramref name="p"/> runs into, as its approach's layouts are held (ApproachBounds.Kept,
    /// a filler's reach further off).</summary>
    private static string Stair(PlacedLocationShape shape, Vector2 p) =>
        shape.Refusal(p, RoadStairRuns.FillerReach, RoadStairRuns.Width * 0.5f + RoadStairRuns.FillerReach);

    /// <summary>What a road onto the landing at <paramref name="p"/> runs into, as RoadLocationEntry holds it.</summary>
    private static string Road(PlacedLocationShape shape, Vector2 p, float width = 4f) =>
        shape.Refusal(p, RoadLocationEntry.InsideReach(width), width * 0.5f + RoadLocationEntry.HugGap, width * 0.5f + PlacedLocationShape.BodyGap);

    [Fact]
    public void TheDoorLandingAndItsStairsAreAsReadInGame()
    {
        PlacedLocationShape shape = Shape(Built());
        Assert.InRange(Vector2.Distance(shape.Landing, DoorLanding), 0f, 0.1f);
        Assert.Equal(Pieces.Length, shape.Body.Count);
        // Its own bottom stair stands well within the gap of the landing: kept off there too, no staircase or road could land.
        Assert.InRange(shape.Body[0].Distance(DoorLanding), 0f, 0.5f);
    }

    [Fact]
    public void TheDoorLandingIsReachedThroughItsCorridor()
    {
        PlacedLocationShape shape = Shape(Built());
        Assert.True(shape.InDoorCorridor(shape.Door!.Value, 0.5f));
        Assert.Equal("", Stair(shape, DoorLanding));
        Assert.Equal("", Road(shape, DoorLanding));
        // The last stretch onto it from the entrance side, within the network's reach of the landing.
        Vector2 out4 = DoorLanding + shape.Front * 4f;
        Assert.Equal("", Stair(shape, out4));
        Assert.Equal("", Road(shape, out4));
        // A staircase's landing region (ApproachBounds) holds it.
        PlacedLocationShape.LandingRegion region = shape.ConnectedLanding((_, _) => Level, PlacedLocationShape.LevelGroundClearance);
        Assert.True(region.Contains(DoorLanding));
    }

    [Fact]
    public void TheCorridorIsTheLandingsReachOnTheEntranceSideOnly()
    {
        PlacedLocationShape shape = Shape(Built());
        Assert.True(shape.InDoorCorridor(shape.Door!.Value + shape.Front * (RoadNetworkGenerator.LandingReach - 0.1f), 0.5f));
        Assert.False(shape.InDoorCorridor(shape.Door!.Value + shape.Front * (RoadNetworkGenerator.LandingReach + 0.1f), 0.5f));
        // Never nearer the location than its landing: up beside its own stairs.
        Assert.False(shape.InDoorCorridor(shape.Door!.Value - shape.Front * 0.5f, 0.5f));
        // A location without a door has none.
        PlacedLocationShape doorless = PlacedLocationShape.Of(Centre, Yaw, Flat, Exterior, Built(), (_, _) => Level, false, Level, "Mistlands_Excavation3");
        Assert.False(doorless.InDoorCorridor(doorless.Landing));
    }

    [Fact]
    public void AStairHuggingTheStairsOutsideTheCorridorIsKeptOffByItsOwnBody()
    {
        PlacedLocationShape shape = Shape(Built());
        // Beside the entrance's stairs, just behind the landing's line, 2.5 m out from the side stair's middle.
        Vector2 beside = At(-3.5f, 21f);
        Assert.False(shape.InDoorCorridor(beside));
        string why = Stair(shape, beside);
        Assert.StartsWith($"too near {Prefab}'s built pieces (", why);
        Assert.Contains("blackmarble_stair", why);
        Assert.StartsWith($"too near {Prefab}'s built pieces (", Road(shape, beside));
        // The edge: the walker's own body (a staircase piece's half width and a filler's reach) kept off the nearest piece, no gap past it.
        LocationRock side = shape.Body[2];
        float half = RoadStairRuns.Width * 0.5f + RoadStairRuns.FillerReach;
        Vector2 across = LocationRock.World(new Vector2(-1f, 0f), Yaw);
        Vector2 Off(float edge) { Vector2 p = side.At; while (side.Distance(p) < edge) p += across * 0.01f; return p; }
        Assert.Contains("built pieces", Stair(shape, Off(half - 0.1f)));
        Assert.DoesNotContain("built pieces", Stair(shape, Off(half + 0.1f)));
        // A road's edge keeps its body gap (PlacedLocationShape.BodyGap) off it: its half width and that, no further.
        float road = 2f + PlacedLocationShape.BodyGap;
        Assert.Contains("built pieces", Road(shape, Off(road - 0.1f)));
        Assert.DoesNotContain("built pieces", Road(shape, Off(road + 0.1f)));
    }

    /// <summary>A Location's built pieces are kept off at any height: an end may stand next to a light post, never under a piece over
    /// its head. Its stakes (a Dvergr stake wall, LocationRock.ClearedForAWay) are no obstacle at any height: they are not in its body,
    /// and the rock pass removes those a road or staircase goes through (RoadRockHit.Judged).</summary>
    [Fact]
    public void ALocationsStakesAreNoObstacleAtAnyHeightItsOtherPiecesAre()
    {
        Vector2 at = At(-8f, 26f);
        LocationRock Over(string name, float low) => new(at, 1f, 1f, 0f, Level + low, Level + low + 1f, name, built: true);
        string Road(PlacedLocationShape shape) => shape.Refusal(at, RoadLocationEntry.InsideReach(4f), 2f + RoadLocationEntry.HugGap,
            2f + PlacedLocationShape.BodyGap);
        foreach (float low in new[] { 0f, 3f })
        {
            PlacedLocationShape stakes = Shape(new List<LocationRock> { Over("dvergrprops_wood_stakewall", low) });
            Assert.Empty(stakes.Body);
            Assert.DoesNotContain("built pieces", Road(stakes));
            Assert.DoesNotContain("built pieces", Stair(stakes, at));
        }
        Assert.Contains("built pieces", Road(Shape(new List<LocationRock> { Over("dverger_light_post", 3f) })));
        Assert.Contains("built pieces", Road(Shape(new List<LocationRock> { Over("dvergrprops_wood_wall", 0f) })));
    }

    /// <summary>
    /// The owner's decisions, 24 Nov: a Location's stake wall the game snaps to the ground (SnapToGround,
    /// LocationRock.ClearedOnEitherGround) lands on the unlevelled zone in Ghost mode and on the levelled one in Full mode, up to 17 m
    /// apart at the same x and z. It stays cleared for a way (LocationRock.ClearedForAWay): never an obstacle to planning, at the Full
    /// height or the Ghost one, and the rock pass finds the one in a way by its x and z in the walk's box's footprint between the two
    /// grounds, with the walker's headroom over them (RockClearances.Band, RockClearance.Column), and removes it whole, wherever it
    /// landed. Nothing over or under that band is looked for. A stake that does not snap, or a boulder, is looked for in the box itself.
    /// </summary>
    [Fact]
    public void ASnappingStakeWallInAWayIsClearedAtTheGhostAndTheFullHeightAndIsNoObstacle()
    {
        const string stakes = "dvergrprops_wood_stakewall";
        Vector2 at = At(-8f, 26f);
        string Road(PlacedLocationShape shape) => shape.Refusal(at, RoadLocationEntry.InsideReach(4f), 2f + RoadLocationEntry.HugGap,
            2f + PlacedLocationShape.BodyGap);
        // A road's box over the levelled ground at the stakes, turned with the road.
        RockClearance box = RoadBox(at);
        // Full: on the levelled ground; Ghost: 17 m over it or under it (the zone's ground before the levelling).
        foreach (float ghost in new[] { 0f, 17f, -17f })
        {
            PlacedLocationShape shape = Shape(new List<LocationRock> { new(at, 1f, 1f, 0f, Level + ghost, Level + ghost + 1f, stakes, built: true) });
            Assert.Empty(shape.Body);
            Assert.DoesNotContain("built pieces", Road(shape));
            Assert.DoesNotContain("built pieces", Stair(shape, at));
            Assert.True(RockClearances.Band(box, Grounds(Level, Level + ghost), out float low, out float high));
            Assert.Equal(Mathf.Min(Level, Level + ghost), low);
            Assert.Equal(Mathf.Max(Level, Level + ghost), high);
            RockClearance column = box.Column(low, high);
            var stake = new Vector3(at.x, Level + ghost + 0.5f, at.y);
            Assert.True(Holds(column, stake), $"the stakes {ghost} m off the levelled ground are not in the box's column");
            if (ghost != 0f) Assert.False(Holds(box, stake), "the box itself reaches the Ghost height: the column pins nothing");
            // The band and the walker's headroom over it, no more: a stake past either ground's headroom is not looked for.
            Assert.Equal(RockClearances.Top(high), column.Top);
            Assert.Equal(RockClearances.Bottom(low), column.Centre.y - column.Half.y);
            Assert.False(Holds(column, new Vector3(at.x, RockClearances.Top(high) + 0.5f, at.y)));
            Assert.False(Holds(column, new Vector3(at.x, low - 0.5f, at.y)));
            // The column is the box's footprint, no wider: stakes beside the way are not taken.
            Assert.False(Holds(column, new Vector3(at.x + 4f, Level, at.y + 4f)));
            Assert.Equal(box.Turn, column.Turn);
            Assert.Equal(box.Half.x, column.Half.x);
            Assert.Equal(box.Half.z, column.Half.z);
        }
        // Only a Location's stakes that snap are looked for on either ground; found, they are removed whole.
        Assert.True(LocationRock.ClearedOnEitherGround(stakes, snaps: true));
        Assert.False(LocationRock.ClearedOnEitherGround(stakes, snaps: false));
        Assert.False(LocationRock.ClearedOnEitherGround("rock4_coast", snaps: true));
        Assert.False(LocationRock.ClearedOnEitherGround("dvergrprops_wood_wall", snaps: true));
        RoadRockHit hit = RoadRockHit.Judged(stakes, 20f, new[] { stakes }, new HashSet<string>(), mist: true, cliffs: false,
            locationChild: true, inLocationArea: true, keptOnApproach: null, piece: false, hasView: true, validView: true, owned: true);
        Assert.True(hit.Clears);
        Assert.Equal(RoadRockPolicy.Taking.RemoveWhole, RoadRockPolicy.HowTaken(hit.Clears, hit.DeckTakes, false, hit.Size, true, hit.LocationStakes));
    }

    /// <summary>
    /// The owner's decision, 24 Nov (the cap on the stake column): a viaduct's deck 15 m over a Location's ground does not reach the
    /// stakes standing on the ground under it, whether or not the Location levels the ground there: its walk stands over the band
    /// between the levelled and the unlevelled ground (RockClearances.Band), so the rock pass asks for no column and the stake stays.
    /// A road's box on that ground, at the same x and z, still finds it. Where no Location's levelling is known, no column is asked.
    /// </summary>
    [Fact]
    public void AStakeUnderADeckFifteenMetresUpIsNotClearedThroughTheDeck()
    {
        Vector2 at = At(-8f, 26f);
        float deck = Level + 15f;
        var deckBox = new RockClearance(new Vector3(at.x, RockClearances.Bottom(deck) + 1f, at.y), new Vector3(2f, 1f, 1f),
            Quaternion.Euler(0f, 30f, 0f), deck, wholeOnly: true, keepsLocationRocks: false);
        var stake = new Vector3(at.x, Level + 0.5f, at.y);
        foreach (float ghost in new[] { 0f, -10f, 10f })
        {
            Assert.False(RockClearances.Band(deckBox, Grounds(Level, Level + ghost), out _, out _), $"a deck reaches the stakes {ghost} m off");
            List<RockClearance> asked = Asked(deckBox, Grounds(Level, Level + ghost));
            Assert.Single(asked);
            Assert.Equal(deckBox.Centre, asked[0].Centre);
            Assert.False(Holds(asked[0], stake));
            // The road's box under the deck, on the levelled ground at the same x and z: the stake is found.
            List<RockClearance> road = Asked(RoadBox(at), Grounds(Level, Level + ghost));
            Assert.Equal(2, road.Count);
            Assert.True(Holds(road[1], stake));
        }
        // No Location's levelling known under the box: only the box itself.
        Assert.Single(Asked(RoadBox(at), (Vector2 _, out float l, out float u) => { l = u = float.NaN; return false; }));
    }

    private static RockClearance RoadBox(Vector2 at) =>
        new(new Vector3(at.x, RockClearances.Bottom(Level) + 1f, at.y), new Vector3(2f, 1f, RockClearances.ClearanceHalfLength),
            Quaternion.Euler(0f, 30f, 0f), Level, wholeOnly: false, keepsLocationRocks: true);

    private static RockClearances.GroundsAt Grounds(float levelled, float unlevelled) => (Vector2 _, out float l, out float u) =>
    {
        l = levelled;
        u = unlevelled;
        return true;
    };

    /// <summary>The boxes the rock pass asks the physics for at <paramref name="box"/> (RoadRockProbe.Candidates).</summary>
    private static List<RockClearance> Asked(RockClearance box, RockClearances.GroundsAt grounds)
    {
        var asked = new List<RockClearance>();
        RoadRockProbe.Candidates(box, grounds, (b, _) => { asked.Add(b); return new List<int>(); }, new List<int>());
        return asked;
    }

    private static bool Holds(RockClearance b, Vector3 p)
    {
        Vector3 local = Quaternion.Inverse(b.Turn) * (p - b.Centre);
        return Mathf.Abs(local.x) <= b.Half.x && Mathf.Abs(local.y) <= b.Half.y && Mathf.Abs(local.z) <= b.Half.z;
    }

    [Fact]
    public void ABoulderInThatPlaceKeepsOnlyItsOwnMargin()
    {
        // The same boxes as boulders round its mouth: kept off by the approach's margin alone, as before.
        PlacedLocationShape shape = Shape(Built(built: false));
        Assert.DoesNotContain("built pieces", Stair(shape, At(-3.5f, 21f)));
    }

    [Fact]
    public void APlacedLocationsFileKeepsWhichPiecesAreBuilt()
    {
        List<LocationRock> rocks = Built();
        rocks.AddRange(Built(built: false));
        var record = new PlacedLocationRecord
        {
            Prefab = Prefab, Centre = Centre, Yaw = Yaw, Root = Level, RockRadius = Exterior,
            Ops = new List<LevelOp> { Flat }, Rocks = rocks,
            X0 = -3400, Z0 = -5830, W = 145, H = 145,
        };
        List<PlacedLocationRecord> read = PlacedLocations.Read(PlacedLocations.Write(new[] { record }));
        Assert.True(read[0].Rocks[0].Built);
        Assert.False(read[0].Rocks[Pieces.Length].Built);
    }
}
