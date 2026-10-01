using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Preparation is a boundary, not a hint. Once it is closed, a lookup that
/// misses must give up and say so rather than build shared state from an
/// island worker.
/// </summary>
public class PreparationBoundaryTests : System.IDisposable
{
    public void Dispose()
    {
        LocationLevelling.Unseal();
        RoadSiteProtection.Source = null;
        RoadSiteProtection.Reset();
    }

    private static IEnumerable<RoadSiteProtection.Footprint> OneSite() =>
        new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), 32f) };

    [Fact]
    public void PrimingBeforeTheBoundaryBuildsTheIndex()
    {
        LocationLevelling.Unseal();
        RoadSiteProtection.Reset();
        int built = 0;
        RoadSiteProtection.Source = () => { built++; return OneSite(); };

        RoadSiteProtection.Prime();
        LocationLevelling.Seal();

        Assert.Equal(1, built);
        // The site it was primed with is protected, and asking does not
        // rebuild anything.
        Assert.True(RoadSiteProtection.Contains(new Vector2(0f, 0f)));
        Assert.Equal(1, built);
        Assert.Equal(0, LocationLevelling.MissesAfterSealing);
    }

    [Fact]
    public void NoSourceAtAllIsNotReportedAsAGapInPreparation()
    {
        // Ensure() has always tolerated having no footprint source: it means
        // there are no footprints. Treating that as a missed lookup wrote one
        // error per ask -- 21,531 of them in a single suite run.
        LocationLevelling.Unseal();
        RoadSiteProtection.Reset();
        RoadSiteProtection.Source = null;
        LocationLevelling.Seal();

        for (int i = 0; i < 100; i++)
            Assert.False(RoadSiteProtection.Contains(new Vector2(i, 0f)),
                "no source at all means no footprints, and must not refuse every route");

        Assert.Equal(0, LocationLevelling.MissesAfterSealing);
        Assert.False(RoadSiteProtection.InstalledButUnprepared);
    }

    [Fact]
    public void AnUnpreparedSourceIsToldApartFromNoSourceAtAll()
    {
        // The distinction the whole guard turns on. Same sealed state, same
        // lookup, opposite answers -- because one is a world without
        // footprints and the other is a world whose footprints were lost.
        LocationLevelling.Unseal();
        RoadSiteProtection.Reset();
        RoadSiteProtection.Source = null;
        LocationLevelling.Seal();
        Assert.False(RoadSiteProtection.InstalledButUnprepared);
        Assert.False(RoadSiteProtection.Contains(new Vector2(0f, 0f)));
        Assert.False(LocationLevelling.PreparationMissedHere);

        LocationLevelling.Unseal();
        RoadSiteProtection.Reset();
        RoadSiteProtection.Source = OneSite;
        LocationLevelling.Seal();
        Assert.True(RoadSiteProtection.InstalledButUnprepared);
        LocationLevelling.BeginRoad();
        Assert.True(RoadSiteProtection.Contains(new Vector2(0f, 0f)));
        // And the road being built knows, so it can be refused rather than
        // built through something it could not see.
        Assert.True(LocationLevelling.PreparationMissedHere);
    }

    [Fact]
    public void APreparedIndexIsNotTreatedAsUnprepared()
    {
        LocationLevelling.Unseal();
        RoadSiteProtection.Reset();
        RoadSiteProtection.Source = OneSite;
        RoadSiteProtection.Prime();
        LocationLevelling.Seal();
        LocationLevelling.BeginRoad();

        Assert.False(RoadSiteProtection.InstalledButUnprepared);
        Assert.True(RoadSiteProtection.Contains(new Vector2(0f, 0f)), "the primed footprint is not protected");
        Assert.False(RoadSiteProtection.Contains(new Vector2(4000f, 0f)), "ground far from any site reads as blocked");
        Assert.False(LocationLevelling.PreparationMissedHere);
        Assert.Equal(0, LocationLevelling.MissesAfterSealing);
    }

    [Fact]
    public void AMissingSubjectIsNamedOnceHoweverOftenItIsAskedFor()
    {
        LocationLevelling.Unseal();
        RoadSiteProtection.Reset();
        RoadSiteProtection.Source = OneSite;
        LocationLevelling.Seal();

        for (int i = 0; i < 100; i++) RoadSiteProtection.Contains(new Vector2(i, 0f));

        // Every ask is counted -- that is the scale of the gap -- but the
        // subject is named once.
        Assert.Equal(100, LocationLevelling.MissesAfterSealing);
    }

    [Fact]
    public void AfterTheBoundaryAMissBuildsNothingAndIsCounted()
    {
        // Preparation never ran -- the gap this exists to catch. Before the
        // boundary this silently invoked the source on whichever worker asked
        // first and filled a shared dictionary from it.
        LocationLevelling.Unseal();
        RoadSiteProtection.Reset();
        int built = 0;
        RoadSiteProtection.Source = () => { built++; return OneSite(); };

        LocationLevelling.Seal();
        bool blocks = RoadSiteProtection.Contains(new Vector2(0f, 0f));

        Assert.Equal(0, built);
        // Footprints exist and cannot be consulted, so everything is treated
        // as in the way and the routes concerned are refused. Answering "clear"
        // would run roads through every POI on the world.
        Assert.True(blocks, "an index that exists but was never built reported the world as clear");
        Assert.True(LocationLevelling.MissesAfterSealing > 0,
            "a lookup after the boundary was not counted, so nobody would hear about it");
    }

    [Fact]
    public void TheIndexCannotBeClearedWhileWorkersAreReadingIt()
    {
        LocationLevelling.Unseal();
        RoadSiteProtection.Reset();
        RoadSiteProtection.Source = OneSite;
        RoadSiteProtection.Prime();
        LocationLevelling.Seal();

        // The source is taken away first, so a rebuild is impossible: the only
        // way the footprint can still be there afterwards is that the clear
        // was refused. Without this the test passed either way, because a
        // cleared index simply rebuilt itself from the source on the next ask.
        RoadSiteProtection.Source = null;
        RoadSiteProtection.Reset();   // refused

        Assert.True(RoadSiteProtection.Contains(new Vector2(0f, 0f)),
            "the index was cleared while it was sealed, which is what island workers read");

        // And once the workers are done, it clears as usual.
        LocationLevelling.Unseal();
        RoadSiteProtection.Reset();
        Assert.False(RoadSiteProtection.Contains(new Vector2(0f, 0f)));
    }

    [Fact]
    public void SealingZeroesTheCountSoOneGenerationDoesNotInheritAnother()
    {
        LocationLevelling.Unseal();
        RoadSiteProtection.Reset();
        // A source that was never primed: the gap. No source at all is an
        // ordinary state and is deliberately not reported.
        RoadSiteProtection.Source = OneSite;
        LocationLevelling.Seal();
        RoadSiteProtection.Contains(new Vector2(0f, 0f));
        Assert.True(LocationLevelling.MissesAfterSealing > 0);

        LocationLevelling.Unseal();
        LocationLevelling.Seal();
        Assert.Equal(0, LocationLevelling.MissesAfterSealing);
    }
}
