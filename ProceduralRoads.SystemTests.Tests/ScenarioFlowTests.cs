using ProceduralRoads.SystemTests;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

public class ScenarioFlowTests
{
    private static readonly PlacedObject Expected = new(10, [1, 2, 3], [0, 0, 0, 1]);
    private static readonly ZoneExpectation[] Zones = [new(0, 0, [Expected])];
    [Fact] public async Task PreparationObservesGenerationBeforeSavingAndNeverAppends()
    {
        var fake = new Transport { UngeneratedReads = 1 }; using var actor = Actor(fake);
        await RoadsScenarios.PrepareBridgeZones(actor, Zones, new("prepare"));
        Assert.Equal(2, fake.Commands.Count(x => x.StartsWith("cli_call ZoneSystem.instance.IsZoneGenerated")));
        Assert.Single(fake.Commands.Where(x => x.StartsWith("cli_call ZoneSystem.instance.CreateGhostZones")));
        Assert.Equal("cli_save", fake.Commands.Last());
        Assert.DoesNotContain(fake.Commands, x => x.StartsWith("road_path") || x == "road_bridges respawn");
    }
    [Fact] public async Task CancelledPreparationDoesNotGenerateOrSave()
    {
        var fake = new Transport(); using var actor = Actor(fake); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => RoadsScenarios.PrepareBridgeZones(actor, Zones, new("prepare"), stop.Token));
        Assert.DoesNotContain(fake.Commands, x => x.StartsWith("cli_call") || x == "cli_save");
    }
    [Fact] public async Task BridgeChecksBeforeAndAfterReloadAndIssuesEachMutationOnce()
    {
        var fake = new Transport(); using var actor = Actor(fake); var report = new ScenarioReport("bridge");
        var after = new Transport { Loaded = true };
        await RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => actor.Execute("road_path 1,2 3,4"), () => Actor(after), Zones, report);
        Assert.True(report.Passed); Assert.Equal(1, fake.Commands.Count(x => x.StartsWith("road_path")));
        Assert.Equal(1, fake.Commands.Count(x => x == "road_bridges respawn")); Assert.Equal(1, fake.Commands.Count(x => x == "cli_save"));
        Assert.Contains("cli_extension roads.testing/bridge-zone 0 0", after.Commands);
    }
    [Fact] public async Task ExistingPendingWorkCannotStandInForOurAppend()
    {
        var fake = new Transport { Pending = 1 }; using var actor = Actor(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => actor.Execute("road_path 1,2 3,4"), () => throw new Exception(), Zones, new("bridge")));
        Assert.DoesNotContain(fake.Commands, x => x.StartsWith("road_path"));
    }
    [Fact] public async Task IncompleteCensusStopsBeforeSave()
    {
        var fake = new Transport { CensusComplete = false }; using var actor = Actor(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => actor.Execute("road_path 1,2 3,4"), () => throw new Exception(), Zones, new("bridge")));
        Assert.DoesNotContain("cli_save", fake.Commands);
    }
    [Fact] public async Task RegenerationAfterRestartDoesNotCountAsPersistence()
    {
        var fake = new Transport(); using var actor = Actor(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => actor.Execute("road_path 1,2 3,4"), () => Actor(new Transport()), Zones, new("bridge")));
    }
    [Fact] public async Task MissingPieceAfterRestartFailsEvenWhenQueueIsEmpty()
    {
        var fake = new Transport(); using var actor = Actor(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => actor.Execute("road_path 1,2 3,4"), () => Actor(new Transport { Loaded = true, Missing = true }), Zones, new("bridge")));
    }
    [Fact] public async Task CancellationBeforeAppendIssuesNoMutation()
    {
        var fake = new Transport(); using var actor = Actor(fake); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => actor.Execute("road_path 1,2 3,4"), () => throw new Exception(), Zones, new("bridge"), stop.Token));
        Assert.DoesNotContain(fake.Commands, x => x.StartsWith("road_path"));
    }
    [Fact] public async Task UnknownMutationOutcomeIsNotRetried()
    {
        var fake = new Transport { LoseAppendReply = true }; using var actor = Actor(fake);
        await Assert.ThrowsAsync<IOException>(() => RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => actor.Execute("road_path 1,2 3,4"), () => throw new Exception(), Zones, new("bridge")));
        Assert.Equal(1, fake.Commands.Count(x => x.StartsWith("road_path"))); Assert.DoesNotContain("road_bridges respawn", fake.Commands);
    }
    [Fact] public async Task EmptyGenerationWaitsForCompletionRatherThanSavingTransientZero()
    {
        var fake = new Transport(); using var actor = Actor(fake);
        await RoadsScenarios.EmptyNetworkReplacesOld(actor, () => { fake.Cells = 0; fake.IncompleteReads = 1; }, () => Actor(new Transport { Loaded = true, Cells = 0 }), new("empty"));
        Assert.True(fake.ReadsAfterGenerate >= 2); Assert.Contains("cli_save", fake.Commands);
    }
    [Fact] public async Task GenerationTimeoutDoesNotSaveOrRestart()
    {
        var fake = new Transport(); using var actor = Actor(fake); bool restart = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoadsScenarios.EmptyNetworkReplacesOld(actor, () => { fake.Cells = 0; fake.IncompleteReads = int.MaxValue; }, // never completes, so only the timeout can end the wait
            () => { restart = true; return Actor(new Transport()); }, new("empty"), TimeSpan.FromMilliseconds(10)));
        Assert.False(restart); Assert.DoesNotContain("cli_save", fake.Commands);
    }
    [Fact] public async Task BridgeWithoutAConfirmedSaveNeverRestarts()
    {
        var fake = new Transport { SaveRefused = true }; using var actor = Actor(fake); bool restart = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => actor.Execute("road_path 1,2 3,4"),
            () => { restart = true; return Actor(new Transport { Loaded = true }); }, Zones, new("bridge")));
        Assert.False(restart); Assert.Equal(1, fake.Commands.Count(x => x == "cli_save"));
    }
    [Fact] public async Task PreparationWithoutAConfirmedSaveFails()
    {
        var fake = new Transport { SaveRefused = true }; using var actor = Actor(fake); var report = new ScenarioReport("prepare");
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoadsScenarios.PrepareBridgeZones(actor, Zones, report));
        Assert.False(report.Passed);
    }
    [Fact] public async Task ACensusOfAnotherZoneStopsBeforeSave()
    {
        var fake = new Transport { CensusZoneOffset = 1 }; using var actor = Actor(fake);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => actor.Execute("road_path 1,2 3,4"), () => throw new Exception(), Zones, new("bridge")));
        Assert.Contains("different zone", error.Message); Assert.DoesNotContain("cli_save", fake.Commands);
    }
    [Fact] public async Task AQueueThatNeverDrainsDoesNotSaveOrRestart()
    {
        var fake = new Transport { NeverDrains = true }; using var actor = Actor(fake); bool restart = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => actor.Execute("road_path 1,2 3,4"),
            () => { restart = true; return Actor(new Transport { Loaded = true }); }, Zones, new("bridge"), drainTimeout: TimeSpan.FromMilliseconds(10)));
        Assert.False(restart); Assert.DoesNotContain("cli_save", fake.Commands); Assert.Equal(1, fake.Commands.Count(x => x == "road_bridges respawn"));
    }
    private static GameActor Actor(Transport fake) => fake.Script.Actor();
    // The server and Roads adapter as the toolkit's scripted transport: the flags are read as each command arrives.
    private sealed class Transport
    {
        public bool Loaded, Missing, LoseAppendReply, CensusComplete = true, SaveRefused, NeverDrains;
        public int CensusZoneOffset;
        public int UngeneratedReads;
        public int Pending, Cells = 4, IncompleteReads, ReadsAfterGenerate;
        public ScriptedTransport Script { get; }
        public IReadOnlyList<string> Commands => Script.Commands;
        public Transport() => Script = new ScriptedTransport()
            .OnPrefix("road_path", _ => { Pending = 1; if (LoseAppendReply) throw new IOException("Outcome unknown"); return ScriptedTransport.Ok("OK: appended"); })
            .On("road_bridges respawn", _ => { if (!NeverDrains) Pending = 0; return ScriptedTransport.Ok("OK: respawn"); })
            .OnPrefix("cli_call ZoneSystem.instance.IsZoneGenerated", _ => ScriptedTransport.Ok(UngeneratedReads-- > 0 ? "VALUE false" : "VALUE true"))
            .OnPrefix("cli_call ZoneSystem.instance.CreateGhostZones", _ => ScriptedTransport.Ok("OK"))
            .On("cli_save", _ => SaveRefused ? ScriptedTransport.Failed("ERROR: save failed") : ScriptedTransport.Ok("OK: SAVE saveNumber=2"))
            .Extension("roads.testing", "bridge-zone", _ => new { source = "zdo-store", complete = CensusComplete, zoneX = CensusZoneOffset, zoneZ = 0,
                pieces = Missing ? Array.Empty<object>() : new object[] { new { prefabHash = 10, position = Expected.Position, rotation = Expected.Rotation } } })
            .Extension("roads.testing", "network", _ =>
            {
                if (Cells == 0) ReadsAfterGenerate++;
                return new { source = "roads-memory", complete = IncompleteReads-- <= 0, cells = Cells, points = Cells, crossings = Cells, pendingZones = Pending, loadedFromSave = Loaded };
            });
    }
}
