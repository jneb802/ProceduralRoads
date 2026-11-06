using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Vines on black marble staircases, hung as the Dvergr viaduct hangs its corbels' (DvergrViaduct.CorbelVine): a curtain
/// down one cornice column of a walking piece at the viaduct's chance, a vine on each course still laid, none in the
/// ground, none in the headroom over a walked surface, never read by the support rule.
/// </summary>
public class StairVineTests
{
    private static readonly StairKit Marble = StairKit.BlackMarble;

    /// <summary>Ground rising to the west a metre a metre from x = -1, flat at 4 east of it.</summary>
    private static float Slope(float x, float z) => x > -1f ? 4f : 4f + (-1f - x);

    /// <summary>A flight of <paramref name="n"/> black marble stairs climbing +z from (0, 10, 0) along the slope, a stair a
    /// rise up and a run on.</summary>
    private static List<StructurePiece> Flight(int n) => Enumerable.Range(0, n).Select(i => new StructurePiece
    {
        Kind = StructurePieceKind.StairStep, Prefab = Marble.Stair, Position = new Vector3(0f, 10f + i, 1f + 2f * i), YawDegrees = 180f,
    }).ToList();

    private static Heightmap.Biome Mistlands(float x, float z) => Heightmap.Biome.Mistlands;

    private static List<StructurePiece> Vines(IReadOnlyList<StructurePiece> walking, out List<StructurePiece> supports,
        Func<float, float, Heightmap.Biome>? biomeAt = null)
    {
        var vines = new List<StructurePiece>();
        supports = StairSupport.Lay(Marble, walking, Slope, (x, z) => Slope(x, z), vines: vines, biomeAt: biomeAt ?? Mistlands);
        return vines;
    }

    [Fact]
    public void ACorbelsVineIsTheViaductsVanillaOne()
    {
        // Mistlands_Viaduct1's middle pier on a deck heading +z (DvergrViaduct.PierVines): the corbel at du 5, v -0.5, 3 m
        // under the deck, turned 270, carries the vine at du 5.55, 2.96 m under it, turned 180, pitched 26 degrees, hanging.
        // On a deck heading 0, along is +z and v is -x.
        var corbel = new StructurePiece { Kind = StructurePieceKind.ViaductPier, Prefab = DvergrViaduct.Corbel, Position = new Vector3(0.5f, -3f, 5f), YawDegrees = 270f };
        StructurePiece vine = DvergrViaduct.CorbelVine(corbel);
        Assert.Equal(DvergrViaduct.Vine, vine.Prefab);
        Assert.Equal(StructurePieceKind.Flair, vine.Kind);
        Assert.Equal(5.55f, vine.Position.z, 3);
        Assert.Equal(0.5f, vine.Position.x, 3);
        Assert.Equal(-2.96f, vine.Position.y, 3);
        Assert.Equal(180f, vine.YawDegrees, 3);
        Assert.Equal(-26f, vine.PitchDegrees, 3);
        Assert.Equal(180f, vine.RollDegrees, 3);
        // The corbel on the pier's other side (du -5, turned 90) carries curtain 2's vine at du -5.55, turned 0.
        StructurePiece other = DvergrViaduct.CorbelVine(new StructurePiece { Prefab = DvergrViaduct.Corbel, Position = new Vector3(0.5f, -3f, -5f), YawDegrees = 90f });
        Assert.Equal(-5.55f, other.Position.z, 3);
        Assert.Equal(0f, other.YawDegrees, 3);
    }

    [Fact]
    public void EveryVineHangsOnACorniceLaidOverTheGroundAndUnderTheWalk()
    {
        List<StructurePiece> walking = Flight(24);
        List<StructurePiece> vines = Vines(walking, out List<StructurePiece> supports);
        Assert.NotEmpty(vines);
        List<StructurePiece> cornices = supports.Where(p => p.Prefab == "blackmarble_out_1").ToList();
        foreach (StructurePiece v in vines)
        {
            Assert.Contains(cornices, c => Vector3.Distance(DvergrViaduct.CorbelVine(c).Position, v.Position) < 0.001f
                && Mathf.Abs(DvergrViaduct.CorbelVine(c).YawDegrees - v.YawDegrees) < 0.01f);
            Assert.True(v.Position.y >= Slope(v.Position.x, v.Position.z), $"vine at {v.Position} in the ground");
            // Its leaves (1.26 m over it at most) stay under the walking surface of the stair over it.
            StructurePiece over = walking.Single(s => Mathf.Abs(v.Position.z - s.Position.z) <= 1f);
            Assert.True(v.Position.y + 1.26f < over.Position.y + 0.75f, $"vine at {v.Position} over the walk of the stair at {over.Position}");
            // Hanging off the open, downhill side: its local +z into the stone, west.
            Vector3 into = StructurePlacement.Rotation(v) * new Vector3(0f, 0f, 1f);
            Assert.True(into.x < -0.8f, $"vine at {v.Position} faces {into}");
        }
    }

    [Fact]
    public void ACurtainIsOneColumnOfAPieceAtTheViaductsChanceTheSameOnEveryLaying()
    {
        List<StructurePiece> walking = Flight(40);
        List<StructurePiece> vines = Vines(walking, out _);
        // One column a piece: its upper half's, at z 1.5 m along it.
        var curtains = vines.Select(v => walking.Single(s => Mathf.Abs(v.Position.z - s.Position.z) <= 1f)).Distinct().ToList();
        Assert.All(vines, v => Assert.Equal(1.5f, v.Position.z % 2f, 2));
        Assert.NotEmpty(curtains);
        // The curtain's roll: the viaduct's 40 %, per piece.
        int there = Enumerable.Range(0, 2000).Count(i => StairWear.Roll(new Vector3(i * 2f, 10f + i, -i * 2f), StairWear.RoleVine) < DvergrViaduct.VineCurtainChance);
        Assert.InRange(there, 720, 880);
        // Each curtain hangs a vine on every course of its column whose vine's origin is out of the ground.
        foreach (StructurePiece s in curtains)
        {
            var mine = vines.Where(v => Mathf.Abs(v.Position.z - s.Position.z) <= 1f).OrderByDescending(v => v.Position.y).ToList();
            for (int i = 1; i < mine.Count; i++) Assert.Equal(2f, mine[i - 1].Position.y - mine[i].Position.y, 3);
        }
        string Sig(List<StructurePiece> ps) => string.Join("|", ps.Select(p => FormattableString.Invariant($"{p.Position.x:F3},{p.Position.y:F3},{p.Position.z:F3}/{p.YawDegrees:F1}")));
        Assert.Equal(Sig(vines), Sig(Vines(Flight(40), out _)));
    }

    [Fact]
    public void ACurtainReachesDownToTheGround()
    {
        // As vanilla's Mistlands Dvergr structures drape theirs to the grass: the lowest vine of a curtain whose origin is
        // out of the ground is hung, however near the ground it stands; none has its origin in it.
        List<StructurePiece> walking = Flight(40);
        List<StructurePiece> vines = Vines(walking, out List<StructurePiece> supports);
        Assert.Contains(vines, v => v.Position.y - Slope(v.Position.x, v.Position.z) < DvergrViaduct.VineGroundClear);
        var hung = new HashSet<Vector3>(vines.Select(v => v.Position));
        var curtains = vines.Select(v => walking.Single(s => Mathf.Abs(v.Position.z - s.Position.z) <= 1f)).Distinct().ToList();
        foreach (StructurePiece c in supports.Where(p => p.Prefab == "blackmarble_out_1"))
        {
            StructurePiece vine = DvergrViaduct.CorbelVine(c);
            if (!curtains.Any(s => Mathf.Abs(vine.Position.z - s.Position.z) <= 0.6f && vine.Position.z > s.Position.z)) continue;
            bool outOfGround = vine.Position.y >= Slope(vine.Position.x, vine.Position.z);
            Assert.True(outOfGround == hung.Contains(vine.Position), $"cornice at {c.Position}: vine at {vine.Position} hung {hung.Contains(vine.Position)}");
        }
    }

    [Fact]
    public void NoVineHangsIntoTheHeadroomOverAWalkedSurface()
    {
        List<StructurePiece> walking = Flight(40);
        List<StructurePiece> vines = Vines(walking, out _);
        StructurePiece top = vines.OrderByDescending(v => v.Position.y).First();
        StructurePiece stair = walking.Single(s => Mathf.Abs(top.Position.z - s.Position.z) <= 1f);
        // A floor laid on past the stair's far end with its walk 0.8 m under the stair's underside, into whose headroom the
        // top vine's leaves reach past the stair's end (an odd layout, laid by hand): that vine is not hung.
        var floor = new StructurePiece
        {
            Kind = StructurePieceKind.StairFloor, Prefab = Marble.Floor, YawDegrees = 0f,
            Position = new Vector3(0f, stair.Position.y - 0.8f - Marble.FloorTop, stair.Position.z + 2f),
        };
        var with = new List<StructurePiece>(walking.Where(s => s.Position.z < stair.Position.z + 1.5f)) { floor };
        List<StructurePiece> hung = Vines(with, out List<StructurePiece> supports);
        // Its cornice still laid: the vine is left out for the headroom alone.
        Assert.Contains(supports, c => c.Prefab == "blackmarble_out_1" && Vector3.Distance(DvergrViaduct.CorbelVine(c).Position, top.Position) < 0.001f);
        Assert.DoesNotContain(hung, v => Vector3.Distance(v.Position, top.Position) < 0.001f);
    }

    [Fact]
    public void AStaircaseClimbingOutOfTheMistlandsIntoTheMountainsIsBareUpThere()
    {
        // Vanilla's vines grow only in the Meadows and the Mistlands (DvergrViaduct.VineBiomes): the flight's upper part,
        // past the middle of its vines, stands in the Mountains.
        List<StructurePiece> walking = Flight(40);
        List<StructurePiece> mist = Vines(walking, out _);
        float line = mist.Select(v => v.Position.z).OrderBy(z => z).ElementAt(mist.Count / 2) - 0.1f;
        Heightmap.Biome Biome(float x, float z) => z > line ? Heightmap.Biome.Mountain : Heightmap.Biome.Mistlands;
        List<StructurePiece> split = Vines(walking, out _, Biome);
        Assert.Contains(mist, v => v.Position.z > line);
        Assert.Contains(split, v => v.Position.z <= line);
        Assert.DoesNotContain(split, v => v.Position.z > line);
        // Below the line, the same vines as an all-Mistlands flight.
        Assert.Equal(mist.Where(v => v.Position.z <= line).Select(v => v.Position), split.Select(v => v.Position));
        // The Meadows grow them too (vanilla's woodhouses), any other biome none.
        Assert.NotEmpty(Vines(walking, out _, (x, z) => Heightmap.Biome.Meadows));
        foreach (Heightmap.Biome b in new[] { Heightmap.Biome.Mountain, Heightmap.Biome.BlackForest, Heightmap.Biome.Plains, Heightmap.Biome.DeepNorth, Heightmap.Biome.AshLands, Heightmap.Biome.Swamp })
            Assert.Empty(Vines(walking, out _, (x, z) => b));
    }

    [Fact]
    public void TheSupportRuleNeverReadsAVineAndStoneHasNone()
    {
        List<StructurePiece> walking = Flight(12);
        List<StructurePiece> vines = Vines(walking, out List<StructurePiece> supports);
        Assert.NotEmpty(vines);
        var all = walking.Concat(supports).Concat(vines).ToList();
        Assert.DoesNotContain(StairSupport.Holding(all, Slope, (x, z) => Slope(x, z)), h => h.Piece.Kind == StructurePieceKind.Flair);
        // The same supports with vines asked for or not.
        Assert.Equal(supports.Count, StairSupport.Lay(Marble, walking, Slope, (x, z) => Slope(x, z)).Count);
        // Stone lays no cornices: no vines.
        var stone = walking.Select(s => new StructurePiece { Kind = s.Kind, Prefab = StairKit.Stone.Stair, Position = s.Position, YawDegrees = s.YawDegrees }).ToList();
        var none = new List<StructurePiece>();
        StairSupport.Lay(StairKit.Stone, stone, Slope, (x, z) => Slope(x, z), vines: none, biomeAt: Mistlands);
        Assert.Empty(none);
        // No biome to read: none hung.
        StairSupport.Lay(Marble, walking, Slope, (x, z) => Slope(x, z), vines: none);
        Assert.Empty(none);
    }
}
