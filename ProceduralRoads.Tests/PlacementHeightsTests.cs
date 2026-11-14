using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The saved placement heights, and the single door out of them.
/// </summary>
public class PlacementHeightsTests : System.IDisposable
{
    public void Dispose()
    {
        LocationLevelling.Unseal();
        // A read refused while sealed marks this thread's road (PreparationMissedHere, thread-static); the next test
        // run on this thread would plan as a road preparation missed.
        LocationLevelling.PreparationMissedHere = false;
        LocationPlacementHeights.Source = null;
        LocationPlacementHeights.Clear();
        LocationLevelling.ResetPlacements = null;
    }

    private static IEnumerable<(Vector2, float)> OnePlacement() =>
        new[] { (new Vector2(100f, 200f), 41.5f) };

    private static void Fill()
    {
        LocationLevelling.Unseal();
        LocationPlacementHeights.Source = OnePlacement;
        LocationPlacementHeights.Clear();
        LocationPlacementHeights.Read();
    }

    [Fact]
    public void TheInstalledResetCallbackIsRefusedWhileSealed()
    {
        // The regression. Install() used to hand LocationLevelling a lambda
        // that cleared the dictionary directly, so guarding Reset() guarded
        // nothing: this callback could still empty the store under the island
        // workers reading it.
        Fill();
        LocationLevelling.ResetPlacements = LocationPlacementHeights.Clear;
        Assert.Equal(41.5f, LocationPlacementHeights.At(new Vector2(100f, 200f)));

        LocationLevelling.Seal();
        LocationLevelling.ResetPlacements.Invoke();

        Assert.True(LocationPlacementHeights.HasBeenRead,
            "the installed callback emptied the store while roads were being generated");
        Assert.Equal(41.5f, LocationPlacementHeights.At(new Vector2(100f, 200f)));
    }

    [Fact]
    public void TheSameCallbackClearsOnceTheWorkersAreDone()
    {
        Fill();
        LocationLevelling.ResetPlacements = LocationPlacementHeights.Clear;
        LocationLevelling.Seal();
        LocationLevelling.ResetPlacements.Invoke();   // refused
        LocationLevelling.Unseal();

        LocationPlacementHeights.Source = null;
        LocationLevelling.ResetPlacements.Invoke();   // allowed

        Assert.False(LocationPlacementHeights.HasBeenRead);
        Assert.Null(LocationPlacementHeights.At(new Vector2(100f, 200f)));
    }

    [Fact]
    public void ReadingIsRefusedAfterTheBoundaryAndCountedAgainstTheRoad()
    {
        LocationLevelling.Unseal();
        LocationPlacementHeights.Source = OnePlacement;
        LocationPlacementHeights.Clear();

        LocationLevelling.Seal();
        LocationLevelling.BeginRoad();
        Assert.Null(LocationPlacementHeights.At(new Vector2(100f, 200f)));

        Assert.False(LocationPlacementHeights.HasBeenRead);
        Assert.True(LocationLevelling.MissesAfterSealing > 0);
        Assert.True(LocationLevelling.PreparationMissedHere,
            "a road that could not read a saved height was not marked, so it would be built anyway");
    }

    /// <summary>A location's saved root is its own placement's, found at its centre exactly (decision 6d: the game stands a
    /// location's proxy at its centre and saves its position losslessly): a location whose own placement is not saved is not
    /// given the placement of a location beside it, here 0.4 m off across a zone's edge (zones meet at x = 96). Before, any
    /// saved placement within half a metre was taken for it.</summary>
    [Fact]
    public void ALocationIsNeverGivenTheSavedRootOfTheLocationBesideIt()
    {
        LocationLevelling.Unseal();
        LocationPlacementHeights.Source = () => new[] { (new Vector2(96.2f, 200f), 41.5f) };
        LocationPlacementHeights.Clear();
        LocationPlacementHeights.Read();
        Assert.Equal(41.5f, LocationPlacementHeights.At(new Vector2(96.2f, 200f)));
        Assert.Null(LocationPlacementHeights.At(new Vector2(95.8f, 200f)));
    }
}
