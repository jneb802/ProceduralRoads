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

    [Fact]
    public void APlacementIsFoundBelowConsolePrecision()
    {
        Fill();
        // Stored and generated coordinates differ in the last digits.
        Assert.Equal(41.5f, LocationPlacementHeights.At(new Vector2(100.3f, 200.3f)));
        Assert.Null(LocationPlacementHeights.At(new Vector2(101.5f, 200f)));
    }
}
