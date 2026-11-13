using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The server terrain bake's two schedules: frame-sized slices while the host's loading screen holds its first spawn,
/// the in-world slice once the player is in the world, and when the hold lets go (ServerBakeLoadScreen; the Unity side
/// is ServerTerrainBake and RoadGeneration_Patch).
/// </summary>
public class ServerBakeLoadScreenTests
{
    private const double InWorldMs = 8.0;

    [Fact]
    public void AHeldFrameGivesTheBakeAWholeFrameAndTheWorldItsSlice()
    {
        Assert.Equal(ServerBakeLoadScreen.HeldBudgetMs, ServerBakeLoadScreen.SliceBudgetMs(held: true, InWorldMs));
        Assert.Equal(InWorldMs, ServerBakeLoadScreen.SliceBudgetMs(held: false, InWorldMs));
        // Larger than the world's slice, and short enough that the loading screen still animates.
        Assert.True(ServerBakeLoadScreen.HeldBudgetMs > InWorldMs);
        Assert.True(ServerBakeLoadScreen.HeldBudgetMs <= 1000.0 / 30 + 0.5);
    }

    /// <summary>The generation's loading bar reads this fraction too (RoadGeneration_Patch.UpdateProgress): done over total,
    /// and none when nothing is owed.</summary>
    [Theory]
    [InlineData(0, 0, 0f)]
    [InlineData(0, 5, 0f)]
    [InlineData(1, 4, 0.25f)]
    [InlineData(1, 3, 0.33333334f)]
    [InlineData(7, 40, 0.175f)]
    [InlineData(3, 3, 1f)]
    public void TheFractionIsTheDoneOverTheTotal(int done, int total, float fraction) =>
        Assert.Equal(fraction, ServerBakeLoadScreen.Fraction(done, total));

    [Fact]
    public void OnlyTheFirstSpawnWithWorkOwedIsHeld()
    {
        var hold = new ServerBakeLoadScreen();
        Assert.True(hold.Holds(beforeFirstSpawn: true, workOwed: true));
        Assert.False(hold.Holds(beforeFirstSpawn: true, workOwed: false));   // the queue is done
        Assert.False(hold.Holds(beforeFirstSpawn: false, workOwed: true));   // in the world, or a dedicated server
    }

    [Fact]
    public void ThePlayerSpawningSwitchesToTheInWorldSlicesForWhatIsLeft()
    {
        var hold = new ServerBakeLoadScreen();
        Assert.True(hold.Holds(true, true));
        hold.Release();
        Assert.False(hold.Holds(true, true));
        Assert.Equal(InWorldMs, ServerBakeLoadScreen.SliceBudgetMs(hold.Holds(false, true), InWorldMs));
    }

    /// <summary>
    /// Found in game: every zone's first try only asks the game's terrain builder, which builds one after another, so the
    /// first passes over a fresh queue find nothing ready. A rule that let go on such a pass let go after 327 ms with 145
    /// zones left. Zones not ready are owed work like any other: the hold ends only when the bake owes nothing (the queue
    /// gives a zone up after its own limit of tries) or the player is in the world.
    /// </summary>
    [Fact]
    public void ZonesStillBuildingKeepTheLoadingScreenHeld()
    {
        var hold = new ServerBakeLoadScreen();
        for (int frame = 0; frame < 100; frame++)
            Assert.True(hold.Holds(beforeFirstSpawn: true, workOwed: true));
        Assert.False(hold.Holds(beforeFirstSpawn: true, workOwed: false));
        Assert.False(hold.Released);
    }

    [Fact]
    public void ANewNetworkHoldsTheNextFirstSpawnAgain()
    {
        var hold = new ServerBakeLoadScreen();
        hold.Release();
        hold.Clear();
        Assert.True(hold.Holds(true, true));
    }

    /// <summary>
    /// The switch end to end, on 400 zones that take 3 ms each, the game ready to spawn the player after 20 frames. Held,
    /// whole frames drain the queue first and nothing is left for the world; not held (today's schedule), 8 ms slices
    /// write 60 zones behind the loading screen and leave 340 to the world.
    /// </summary>
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 340)]
    public void HeldFramesDrainTheQueueAndTheWorldSlicesTheRest(bool holdEnabled, int expectedZonesAfterSpawn)
    {
        const double zoneMs = 3.0;
        const int framesTheGameNeeds = 20;
        var hold = new ServerBakeLoadScreen();
        int pending = 400, frame = 0, zonesAfterSpawn = 0;
        bool spawned = false;
        while (pending > 0)
        {
            bool held = hold.Holds(beforeFirstSpawn: holdEnabled && !spawned, workOwed: pending > 0);
            if (!spawned && !held && frame >= framesTheGameNeeds) { spawned = true; hold.Release(); }
            double budget = ServerBakeLoadScreen.SliceBudgetMs(held, InWorldMs), used = 0;
            int done = 0;
            while (pending > 0 && used < budget) { used += zoneMs; pending--; done++; }
            if (spawned) zonesAfterSpawn += done;
            frame++;
        }
        Assert.Equal(expectedZonesAfterSpawn, zonesAfterSpawn);
    }

    [Fact]
    public void TheLoadingScreenSaysHowManyZonesAreWritten()
    {
        Assert.Equal("Writing road terrain…", ServerBakeLoadScreen.Text(0, 0));
        Assert.Equal(0f, ServerBakeLoadScreen.Fraction(0, 0));
        Assert.Equal("Writing road terrain: 37 / 144 zones", ServerBakeLoadScreen.Text(37, 144));
        Assert.Equal(0.25f, ServerBakeLoadScreen.Fraction(36, 144));
        Assert.Equal(1f, ServerBakeLoadScreen.Fraction(150, 144));
    }
}
