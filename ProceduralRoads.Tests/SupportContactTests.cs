using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Terrain contact, one owner (docs/OWNERS.md, terrain contact: <see cref="SupportGraph.Against"/>): the support rule grounds a
/// piece whose box, grown <see cref="SupportGraph.Reach"/> as the game grows it, meets the game's terrain mesh anywhere over
/// its footprint, read exactly (<see cref="SupportGraph.OnTerrain"/>), not at samples that can pass between the mesh's vertices.
/// </summary>
public sealed class SupportContactTests
{
    /// <summary>Flat finished ground at 0 but one vertex, at (5, 5), raised a metre: a spike the mesh comes to only there.</summary>
    private static float Spike(int x, int z) => x == 5 && z == 5 ? 1f : 0f;

    private static StructurePiece Floor(string prefab, float bottom) => new()
    {
        Kind = StructurePieceKind.StairSupport, Prefab = prefab, Position = new Vector3(5f, bottom + 0.5f, 5f), YawDegrees = 0f,
    };

    /// <summary>
    /// A 2 x 2 floor alone over the spike, its grown underside 0.1 m under the spike's top: the mesh meets its grown box at the
    /// spike's vertex, its middle, which a grid of samples across its grown footprint (2.3 m, 0.46 m apart) passes between
    /// (its nearest samples read the mesh 0.77 m up). Grounded, it stands at its material's full support, on the staircase's
    /// reading (StairSupport.Holding) and the viaduct's (ViaductSupport.Of) alike; 0.15 m higher it is over the ground and falls.
    /// </summary>
    [Theory]
    [InlineData("stone_floor_2x2")]
    [InlineData("blackmarble_floor")]
    public void APieceTheMeshMeetsBetweenTheOldSamplesIsGrounded(string prefab)
    {
        Assert.True(BuriedPieces.DataOf(prefab, out BuriedPieces.Measured data) && data.Material is not null);
        SupportGraph.Material material = data.Material!.Value;
        float meets = 1f - 0.1f + SupportGraph.Reach, over = meets + SupportGraph.Reach + 0.05f;
        var grounding = new ViaductSupport.Grounding(Spike);
        foreach (var (bottom, stands) in new[] { (meets, true), (over, false) })
        {
            var laid = new List<StructurePiece> { Floor(prefab, bottom) };
            ViaductSupport.Held stair = Assert.Single(StairSupport.Holding(laid, Spike));
            ViaductSupport.Held viaduct = Assert.Single(ViaductSupport.Of(grounding, laid));
            Assert.Equal(stands ? material.Max : 0f, stair.Support);
            Assert.Equal(stair.Support, viaduct.Support);
            Assert.Equal(!stands, stair.Falls);
        }
    }
}
