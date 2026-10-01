using ProceduralRoads.SystemTests;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// The client terrain-persistence scenario against scripted server and client transports: no game. The fakes answer
// from the prepared plans, so the tests pin the scenario's order and refusals, not the game's terrain.
public sealed class TerrainClientScenarioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roads-client-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _events = [];
    private static readonly HeightExpectation Support = new(100, -40, 42.5f);
    public TerrainClientScenarioTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private RunPlan Plan(HeightExpectation? support = null, bool withSupport = true)
    {
        var height = new SurfacePlan { ExpectedFrom = "test profile", Tolerance = .05f, Samples = [new(100, -40, 42.5f), new(100, -37, 41f)], Support = withSupport ? support ?? Support : null };
        var paint = new PaintPlan { ExpectedFrom = "test paint", Tolerance = .01f, Samples = [new(100, -40, 1, 0, 0, .3f)] };
        string h = Path.Combine(_dir, "height.json"), p = Path.Combine(_dir, "paint.json");
        height.Write(h); paint.Write(p);
        var plan = PlanTests.Valid(); plan.Scenario = "terrain-persistence"; plan.Environment["ROADS_TEST_PERSISTENT_TERRAIN"] = "1";
        plan.Client = new ClientRunPlan
        {
            Mode = "attach", Port = 5591, Character = "roadtest", Join = "127.0.0.1:2476", PasswordVariable = "ROADS_TEST_JOIN_PASSWORD",
            Pins = new() { ["valheimCLI.valheimCLI"] = new('a', 32), ["warpalicious.ProceduralRoads"] = "absent", ["warpalicious.More_World_Locations_AIO"] = "absent" },
            ArrivalSeconds = 10, JoinSeconds = 10,
        };
        plan.HeightPlan = new() { Source = h, Sha256 = WorldFixture.Hash(h) }; plan.PaintPlan = new() { Source = p, Sha256 = WorldFixture.Hash(p) };
        plan.Validate();
        return plan;
    }

    private ScriptedTransport Server(string name) => new ScriptedTransport()
        .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0"))
        .OnPrefix("cli_teleport_peer ", _ => { _events.Add(name + ":teleport"); return ScriptedTransport.Ok("OK: asked peer 1 to teleport to 100.0,43.0,-40.0"); })
        .On("cli_save", _ => { _events.Add(name + ":save"); return ScriptedTransport.Ok("OK: SAVE saveNumber=" + _events.Count); });

    // A client whose ground and paint match the plans, except where the test says otherwise after the restart.
    private ScriptedTransport Client(float? heightAfterRestart = null)
    {
        bool joined = false, restarted = false;
        object State() => joined
            ? new { source = "session-state", complete = true, phase = "world-present", worldUid = "123", worldPresent = true, worldReady = true, server = false, dedicated = false, localPlayer = true, playerReady = true, saving = false, loadError = false, connectionStatus = "Connected" }
            : new { source = "session-state", complete = true, phase = "menu", worldUid = (string?)null, worldPresent = false, worldReady = false, server = false, dedicated = false, localPlayer = false, playerReady = false, saving = false, loadError = false, connectionStatus = "None" };
        return new ScriptedTransport()
            .Extension("valheim.session", "state", _ => State())
            .Extension("valheim.session", "join", _ => { joined = true; _events.Add("client:join"); return new { source = "session-join", complete = true, action = "join" }; }, readOnly: false)
            .Extension("valheim.session", "leave", _ => { joined = false; restarted = true; _events.Add("client:leave"); return new { source = "session-leave", complete = true, action = "leave" }; }, readOnly: false)
            .Extension("valheim.world", "terrain-surface", a =>
            {
                float x = float.Parse(a[0]), z = float.Parse(a[1]);
                float h = z == -40 ? 42.5f : 41f;
                if (restarted && heightAfterRestart is float wrong && z == -37) h = wrong;
                return new { source = "loaded-terrain-surface", complete = true, units = "metres", x, z, height = h, colliderHeight = h };
            })
            .Extension("valheim.world", "terrain-paint", a => new { source = "loaded-terrain-paint", complete = true, units = "rgba01", x = float.Parse(a[0]), z = float.Parse(a[1]), r = 1f, g = 0f, b = 0f, a = .3f })
            .Extension("valheim.world", "player-support", _ => new { source = "local-player-support", complete = true, x = 100f, y = 42.5f, z = -40f, speed = 0f, grounded = true, flying = false, attached = false, dead = false, teleporting = false, units = "metres" })
            .On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True"))
            // The toolkit's arrival skips a first-spawn intro first; this character has spawned before.
            .OnPrefix("cli_skip_intro", _ => ScriptedTransport.Ok("OK: skipped=False profileFirstSpawn=False position=0.0,40.00,0.0 ms=3"))
            .On("devcommands", _ => ScriptedTransport.Ok((devcommands = !devcommands) ? "Dev commands: True" : "Dev commands: False"));
    }
    private bool devcommands;

    private (ScenarioReport Report, Exception? Error) Run(RunPlan plan, ScriptedTransport client, ScriptedTransport first, ScriptedTransport second,
        Action<GameActor>? waitUntilJoinable = null)
    {
        var report = new ScenarioReport("roads-client-test");
        using var server = first.Actor("server");
        try
        {
            TerrainClientScenario.Run(plan, server, () => { _events.Add("restart"); return second.Actor("server2"); },
                () => { _events.Add("client:open"); return ClientSession.Attach(plan.Client!, _dir, client); },
                waitUntilJoinable ?? (actor => _events.Add(actor.Name + ":joinable")), report, _dir);
            return (report, null);
        }
        catch (Exception error) { return (report, error); }
    }

    [Fact] public void BothRoundsMeasureAroundAConfirmedSaveARestartAndARejoin()
    {
        var client = Client();
        var (report, error) = Run(Plan(), client, Server("server1"), Server("server2"));
        Assert.Null(error); Assert.True(report.Passed);
        Assert.True(devcommands); // The toolkit's join turned them on before each join.
        Assert.Equal(new[] { "client:open", "server:joinable", "client:join", "server1:teleport", "server1:save", "client:leave", "restart",
            "server2:joinable", "client:join", "server2:teleport", "client:leave" }, _events);
        Assert.Equal("detach from the operator's client", report.Steps[^1].Name);
        foreach (var round in new[] { "first", "after-restart" })
            foreach (var file in new[] { "arrival", "surfaces", "paint", "support" })
                Assert.True(File.Exists(Path.Combine(_dir, $"{round}-{file}.json")), $"{round}-{file}.json");
        Assert.True(File.Exists(Path.Combine(_dir, "client-commands.jsonl")));
    }

    [Fact] public void AServerThatNeverAcceptsConnectionsStopsBeforeTheJoinAndStillClosesTheClient()
    {
        var client = Client();
        var (report, error) = Run(Plan(), client, Server("server1"), Server("server2"), _ => throw new WaitTimeoutException("server accepting game connections", TimeSpan.FromSeconds(1), null));
        Assert.IsType<WaitTimeoutException>(error);
        Assert.Equal("first: the server accepts game connections", Assert.Single(report.Steps, s => !s.Passed).Name);
        Assert.DoesNotContain("client:join", _events);
        Assert.Equal("detach from the operator's client", report.Steps[^1].Name);
    }

    [Fact] public void TerrainThatChangedAcrossTheRestartFailsThatRound()
    {
        var (report, error) = Run(Plan(), Client(heightAfterRestart: 40f), Server("server1"), Server("server2"));
        Assert.NotNull(error); Assert.False(report.Passed);
        var failed = Assert.Single(report.Steps, s => !s.Passed);
        Assert.Equal("after-restart: client heightmap and its own collider match the prepared profile", failed.Name);
        Assert.DoesNotContain(report.Steps, s => s.Name == "after-restart: the player stands on the dry road core");
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void AMissingOrWetSupportPointIsRefusedBeforeTheClientIsTouched(bool wet)
    {
        var plan = wet ? Plan(support: new(100, -40, 31f)) : Plan(withSupport: false);
        var client = Client();
        var (report, error) = Run(plan, client, Server("server1"), Server("server2"));
        Assert.NotNull(error);
        Assert.Equal("prepared client plans, unchanged since pinned", Assert.Single(report.Steps).Name);
        Assert.Empty(client.Commands); Assert.Empty(_events);
    }

    [Fact] public void APlanFileChangedAfterPinningIsRefused()
    {
        var plan = Plan();
        File.AppendAllText(plan.PaintPlan!.Source, " ");
        var client = Client();
        var (report, error) = Run(plan, client, Server("server1"), Server("server2"));
        Assert.Contains("changed after it was pinned", error!.Message); Assert.Empty(client.Commands);
    }

    [Fact] public void TheClientMustBeStockAndPinnedExactly()
    {
        var plan = Plan();
        Assert.Contains("worlduid=123", plan.Client!.WorldExpectations(plan.WorldUid));
        Assert.DoesNotContain("worlduid", plan.Client.MenuExpectations);
        plan.Client.Pins.Remove("warpalicious.More_World_Locations_AIO"); Assert.Throws<ArgumentException>(plan.Validate);
        plan.Client.Pins["warpalicious.More_World_Locations_AIO"] = "absent"; plan.Client.Pins["warpalicious.ProceduralRoads"] = new('1', 32);
        Assert.Throws<ArgumentException>(plan.Validate);
        plan.Client.Pins["warpalicious.ProceduralRoads"] = "absent"; plan.Client.Pins["some.mod"] = "any"; Assert.Throws<ArgumentException>(plan.Validate);
        plan.Client.Pins.Remove("some.mod"); plan.Client.Pins["worlduid"] = "123"; Assert.Throws<ArgumentException>(plan.Validate);
        plan.Client.Pins.Remove("worlduid"); plan.Client.Character = "two words"; Assert.Throws<ArgumentException>(plan.Validate);
        plan.Client.Character = "roadtest"; var paint = plan.PaintPlan; plan.PaintPlan = null; Assert.Throws<ArgumentException>(plan.Validate);
        plan.PaintPlan = paint; plan.Client.Mode = ""; Assert.Throws<ArgumentException>(plan.Validate);
        plan.Client.Mode = "attach"; plan.Scenario = "empty-save"; plan.Environment.Clear();
        Assert.Contains("are for the terrain-persistence scenario", Assert.Throws<ArgumentException>(plan.Validate).Message);
        plan.Scenario = "terrain-persistence"; plan.Environment["ROADS_TEST_PERSISTENT_TERRAIN"] = "1"; plan.Validate();
        plan.Client = null; Assert.Contains("without a client", Assert.Throws<ArgumentException>(plan.Validate).Message);
    }

    [Fact] public void TheSupportPointIsTheHighestDryRoadCoreVertex()
    {
        var rows = new[]
        {
            new PersistentSample(0, 10, 5, 40, 41.2f, 0, 0), new PersistentSample(1, 18, 5, 40, 41.9f, 0, 0),
            new PersistentSample(1, 18, 8, 40, 44f, 0, 0), // Off the road's line: never the support point.
        };
        Assert.Equal(new HeightExpectation(18, 5, 41.9f), PersistentTerrainScenario.DrySupport(rows, 5));
        var wet = new[] { new PersistentSample(0, 10, 5, 29, 31.2f, 0, 0) };
        Assert.StartsWith("No genuinely dry support point", Assert.Throws<InvalidOperationException>(() => PersistentTerrainScenario.DrySupport(wet, 5)).Message);
    }
}
