using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

[Collection("RoadStatics")]
public class SharedCrossingValidityTests
{
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedBridgeReplacesInvalidShallowCandidateOrChain(bool chain)
    {
        var registry = (List<RoadCrossing>)RoadNetworkGenerator.GetRoadCrossings();
        var savedRegistry = registry.ToArray();
        var savedWorld = WorldGenerator.instance;
        WorldGenerator.instance = new FlatWorld();
        registry.Clear();
        try
        {
            PlainRoads.WithModDefaults(() =>
            {
                var bridge = RoadCrossing.Between(new Vector2(0, 0), new Vector2(0, 120),
                    27, new Vector2(0, 60), 100, CrossingKind.Bridge);
                registry.Add(bridge);
                var first = Shallow(6, 0, chain ? 60 : 120, invalid: true);
                first.FromIndex = 2;
                first.ToIndex = chain ? 5 : 8;
                var crossings = new List<RoadCrossing> { first };
                if (chain)
                {
                    var second = Shallow(6, 60, 120, invalid: false);
                    second.FromIndex = 5;
                    second.ToIndex = 8;
                    crossings.Add(second);
                }

                RoadNetworkGenerator.SnapToExistingCrossings(crossings);

                var shared = Assert.Single(crossings);
                Assert.True(shared.Shared);
                Assert.Equal(CrossingKind.Bridge, shared.Kind);
                Assert.Equal(bridge.FromBank, shared.FromBank);
                Assert.Equal(bridge.ToBank, shared.ToBank);
                Assert.Equal(2, shared.FromIndex);
                Assert.Equal(8, shared.ToIndex);
                Assert.False(shared.Shallow);
                Assert.False(shared.Invalid);
                Assert.Null(shared.ShallowNote);
                Assert.Equal(0, RoadNetworkGenerator.InvalidShallows(crossings, null));
            });
        }
        finally
        {
            registry.Clear(); registry.AddRange(savedRegistry);
            WorldGenerator.instance = savedWorld;
        }
    }

    [Fact]
    public void AdoptingAShallowCrossingKeepsItsOwnClassificationAndRejection()
    {
        var target = Shallow(0, 0, 120, invalid: true);
        var candidate = RoadCrossing.Between(new Vector2(2, 120), new Vector2(2, 0),
            27, new Vector2(2, 60), 100, CrossingKind.Bridge);
        candidate.SnapTo(target);
        Assert.Equal(target.ToBank, candidate.FromBank);
        Assert.Equal(target.FromBank, candidate.ToBank);
        Assert.True(candidate.Shallow);
        Assert.True(candidate.Invalid);
        Assert.Equal(target.ShallowNote, candidate.ShallowNote);
        Assert.Equal(1, RoadNetworkGenerator.InvalidShallows(new List<RoadCrossing> { candidate }, null));

        target.Invalid = false;
        candidate.SnapTo(target);
        Assert.True(candidate.Shallow);
        Assert.False(candidate.Invalid);
        Assert.Equal(FordStyle.Raise, candidate.Style);
    }

    private static RoadCrossing Shallow(float x, float from, float to, bool invalid)
    {
        var crossing = RoadCrossing.Between(new Vector2(x, from), new Vector2(x, to),
            29, new Vector2(x, (from + to) / 2), 0, CrossingKind.Ford, FordStyle.Raise);
        crossing.Shallow = true;
        crossing.Invalid = invalid;
        crossing.ShallowNote = "original shallow classification";
        return crossing;
    }
}
