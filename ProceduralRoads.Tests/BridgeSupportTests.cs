using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The wooden support ladder, pinned against the decompiled rule and against
/// the one reading taken in game.
///
/// These are not regression tests over our own arithmetic. Each number here is
/// either transcribed from <c>WearNTear.GetMaterialProperties</c> or measured
/// on a real piece, so if Valheim changes the rule these fail and say so,
/// which is the whole reason the model is a table and not a magic number.
/// </summary>
public class BridgeSupportTests
{
    /// <summary>Straight from the decompiled material table for Wood. If a
    /// patch moves these, every height in this file moves with them.</summary>
    [Fact]
    public void TheWoodMaterialConstantsAreTheOnesVanillaUses()
    {
        Assert.Equal(100f, BridgeSupport.MaxSupport);
        Assert.Equal(10f, BridgeSupport.MinSupport);
        Assert.Equal(0.125f, BridgeSupport.VerticalLoss);
        Assert.Equal(0.2f, BridgeSupport.HorizontalLoss);
    }

    /// <summary>MEASURED IN GAME: a wood_pole2
    /// with two poles of support beneath it read <c>support=54.39</c>. The
    /// model says 100 * (1 - 0.125 * 2.1)^2 = 54.3906. This is the test that
    /// says the transcription is right, and it is the only one of these the
    /// game has actually confirmed.</summary>
    [Fact]
    public void TheThirdPoleMatchesTheSupportMeasuredInGame()
    {
        Assert.Equal(54.39f, BridgeSupport.ColumnSupport(3, BridgeLayout.PostSegment), 2);
    }

    /// <summary>Also measured in game: readings of 9.95 and 8.35 reported
    /// <c>HaveSupport=false</c> while 13.49 and 54.39 reported true. The
    /// threshold sits between, at 10.</summary>
    [Theory]
    [InlineData(54.39f, true)]
    [InlineData(13.49f, true)]
    [InlineData(9.95f, false)]
    [InlineData(8.35f, false)]
    public void TheMeasuredHeldFlagsBracketMinSupport(float support, bool held)
    {
        Assert.Equal(held, support >= BridgeSupport.MinSupport);
    }

    /// <summary>Support decays geometrically, so the ceiling is a height and
    /// not a piece count: no amount of extra structure underneath raises it.
    /// Eight 2 m poles stand; the ninth is below MinSupport.</summary>
    [Fact]
    public void AColumnOfTwoMetrePolesRunsOutAtEight()
    {
        Assert.Equal(8, BridgeSupport.MaxPoles(BridgeLayout.PostSegment));
        Assert.True(BridgeSupport.ColumnSupport(8, BridgeLayout.PostSegment) >= BridgeSupport.MinSupport);
        Assert.True(BridgeSupport.ColumnSupport(9, BridgeLayout.PostSegment) < BridgeSupport.MinSupport);
    }

    /// <summary>
    /// The ceiling is a MEASUREMENT, and these are the readings behind it.
    ///
    /// Taken in game on bridges this mod generated. Two wood_beam on one
    /// crossing read 9.99 and 9.72 against a
    /// minimum of 10 and were failing, standing 18.6 and 18.8 m below their
    /// deck; a beam of the same bridge 17.1 m below its deck read 12.99 and
    /// held. If a Valheim patch moves the rule, this is the test that should
    /// stop being true.
    /// </summary>
    [Theory]
    [InlineData(17.1f, false)]   // beam read 12.99, held
    [InlineData(18.6f, true)]    // beam read  9.99, failing
    [InlineData(18.8f, true)]    // beam read  9.72, failing
    public void TheCeilingSitsBetweenTheColumnThatHeldAndTheOneThatFailed(float columnHeight, bool shouldBeOverTheCeiling)
    {
        Assert.Equal(shouldBeOverTheCeiling, columnHeight > BridgeSupport.MaxPierHeight());
    }

    /// <summary>
    /// The derivation predicted 13.2 m and the game said 18.4 m. Keeping the
    /// gap in a test stops anyone "simplifying" the measured constant back into
    /// the formula that produced it.
    ///
    /// A bridge is a lattice, not a column: stations every 2 m tied by beams
    /// and deck plates, and UpdateSupport averages two support points more
    /// than 100 degrees apart instead of taking the worse. That is worth about
    /// two poles of height, and it is why spans work in this game.
    /// </summary>
    [Fact]
    public void TheMeasuredCeilingStandsWellAboveWhatAFreeColumnWouldGive()
    {
        float freeColumn = 0f;
        for (int n = 1; n <= 20; n++)
        {
            if (BridgeSupport.ColumnSupport(n, BridgeLayout.PostSegment) >= BridgeSupport.MinSupport)
                freeColumn = BridgeLayout.PostSegment * n;
        }
        // 18 m measured against 16 m for a bare column that carries nothing,
        // and against 13.2 m for the same column once it has to hold a deck up
        // - which was the prediction, and was five metres short.
        Assert.True(BridgeSupport.MaxPierHeight() >= freeColumn,
            $"measured ceiling {BridgeSupport.MaxPierHeight():F1} m is BELOW the {freeColumn:F1} m "
            + "a bare column reaches. A lattice cannot do worse than the column inside it, so "
            + "either the measurement or the column model has moved.");
    }

    /// <summary>A shorter pole segment loses less per joint but pays the 0.1 m
    /// distance margin more often, so shrinking the segment does NOT buy
    /// height. Anyone tempted to fix a tall bridge by using more, smaller
    /// pieces should read this test instead of trying it.</summary>
    [Fact]
    public void SmallerPiecesDoNotBuyHeight()
    {
        float twoMetre = (BridgeSupport.MaxPoles(2f) - 1) * 2f;
        float oneMetre = (BridgeSupport.MaxPoles(1f) - 1) * 1f;
        Assert.True(oneMetre <= twoMetre + 1f,
            $"1 m poles reached {oneMetre:F1} m against {twoMetre:F1} m for 2 m poles");
    }
}
