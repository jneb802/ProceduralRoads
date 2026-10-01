using ProceduralRoads.SystemTests;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

public class PilotTests
{
    // Transform matching itself is tested in the toolkit (MatchingTests).
    private static PlacedObject Piece(int prefab = 1) => new(prefab, [1, 0, 3], [0, 0, 0, 1]);
    [Fact] public async Task EmptySaveScenarioConfirmsReloadAndSave()
    {
        var fake = new Fake(); using var actor = Actor(fake); var report = new ScenarioReport("empty");
        await RoadsScenarios.EmptyNetworkReplacesOld(actor, () => fake.Cells = 0, () => Actor(new Fake { Cells = 0, Loaded = true }), report);
        Assert.True(report.Passed); Assert.Equal(1, fake.Saves);
    }
    [Fact] public async Task OldNetworkReturningFailsPersistence()
    {
        var fake = new Fake(); using var actor = Actor(fake); var report = new ScenarioReport("empty");
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoadsScenarios.EmptyNetworkReplacesOld(actor, () => fake.Cells = 0, () => Actor(new Fake { Loaded = true }), report));
        Assert.False(report.Passed);
    }
    [Fact] public async Task UnconfirmedSaveCannotTriggerRestart()
    {
        var fake = new Fake { ConfirmSave = false }; using var actor = Actor(fake); bool restarted = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoadsScenarios.EmptyNetworkReplacesOld(actor, () => fake.Cells = 0, () => { restarted = true; return Actor(new Fake()); }, new("empty")));
        Assert.False(restarted);
    }
    [Fact] public async Task AlreadyDrainedAppendCannotPassRaceTest()
    {
        var fake = new Fake(); using var actor = Actor(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoadsScenarios.BridgeAppendSurvivesRespawn(actor, () => {}, () => Actor(new Fake()), [new(0, 0, [Piece()])], new("bridge")));
        Assert.DoesNotContain("road_bridges respawn", fake.Commands);
    }
    private static GameActor Actor(Fake fake) => fake.Script.Actor("server", "cli_expect world=fixture plugin=0123456789abcdef0123456789abcdef");
    // The server and Roads adapter as the toolkit's scripted transport: the fields are read as each command arrives.
    private sealed class Fake
    {
        public int Cells = 10; public bool Loaded, ConfirmSave = true;
        public ScriptedTransport Script { get; }
        public IReadOnlyList<string> Commands => Script.Commands;
        public int Saves => Script.Count("cli_save");
        public Fake() => Script = new ScriptedTransport()
            .On("cli_save", _ => ScriptedTransport.Ok(ConfirmSave ? "OK: SAVE saveNumber=2" : "Saving.."))
            .Extension("roads.testing", "network", _ => new { source = "roads-memory", complete = true, cells = Cells, points = Cells * 2, crossings = Cells, pendingZones = 0, loadedFromSave = Loaded });
    }
}
