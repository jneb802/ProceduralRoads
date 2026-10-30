using System;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public partial class SitesFirstTests
{
    /// <summary>One reader of the ground a placed place leaves (PlacedSites.GroundAt): the terrain writer reads a place
    /// placed first from its preload, as the finished-ground predictor does, and rebuilds from the saved placement (its
    /// hook) only a place with no preload. Before, the writer rebuilt every place itself.</summary>
    [Fact]
    public void TheWriterReadsAPlacedPlacesGroundFromItsPreloadAsThePredictorDoes()
    {
        Func<Vector2, float?>? hook = RoadTerrainModifier.PlacedGround;
        try
        {
            RoadTerrainModifier.PlacedGround = _ => 999f;
            var inside = new Vector2(3f, 2f);
            // Not placed first: no preload, the hook's rebuild.
            Assert.Null(PlacedSites.GroundAt(inside));
            Assert.Equal(999f, RoadTerrainModifier.PlacedBaseAt(inside));
            PlacedSite cave = Cave();
            PlacedSites.Ready(cave);
            float preload = cave.Ground(inside.x, inside.y);
            Assert.Equal(preload, PlacedSites.GroundAt(inside));
            Assert.Equal(preload, RoadTerrainModifier.PlacedBaseAt(inside));
            Assert.Equal(preload, RoadTerrainModifier.FinishedGround((x, z) => _world.GetHeight(x, z))(3, 2));
            // Outside every footprint there is no place's ground.
            Assert.Null(PlacedSites.GroundAt(new Vector2(500f, 500f)));
        }
        finally { RoadTerrainModifier.PlacedGround = hook; }
    }
}
