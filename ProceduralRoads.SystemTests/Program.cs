using ProceduralRoads.SystemTests;
using Valheim.Testing.Game;

// The toolkit's pinned dedicated-server runner (PinnedServerRun) with the Roads plan, modes and scenarios. The runner
// owns the lifecycle: plan checks, fixture copies, provenance, the owned session and its startup events, teardown,
// the report and the result banner.
return await PinnedServerRun.MainAsync(args, new PinnedServerRunOptions<RunPlan>
{
    Name = "roads-system-test",
    ReadPlan = path => { var plan = RunPlan.Read(path); plan.Validate(); return plan; },
    SessionCapability = RunPlan.SessionCapability,
    SessionTokenVariable = RunPlan.SessionTokenVariable,
    PrepareModes = ["prepare-bridge", "prepare-terrain"],
    ModeScenarios = new Dictionary<string, string[]> { ["prepare-bridge"] = ["bridge-respawn"], ["prepare-terrain"] = ["terrain-persistence"] },
    CheckMode = (mode, plan) =>
    {
        if (mode == "run" && plan.Scenario == "terrain-persistence" && plan.Client == null)
            throw new ArgumentException("A terrain-persistence run measures from a client: add the client section, or prepare the fixture with prepare-terrain.");
    },
    Provenance = (plan, provenance) =>
    {
        if (plan.Scenario == "terrain-persistence") provenance["paintProfile"] = PersistentTerrainScenario.ProfileName(PersistentTerrainScenario.WidthFor(plan));
        if (plan.Client != null) provenance["clientMode"] = plan.Client.Mode;
    },
    Scenario = run => run.Mode switch
    {
        "prepare-terrain" => Sync(() => PersistentTerrainScenario.Prepare(run.Server, run.Report, run.Output, PersistentTerrainScenario.WidthFor(run.Plan))),
        "prepare-bridge" => RoadsScenarios.PrepareBridgeZones(run.Server, run.Plan.Expected, run.Report, run.Cancellation),
        _ => run.Plan.Scenario switch
        {
            "terrain-calibration" => Sync(() => TerrainCalibrationScenario.Run(run.Server, run.Report, run.Output)),
            "terrain-persistence" => Sync(() => TerrainClientScenario.Run(run.Plan, run.Server, run.Session.Restart,
                () =>
                {
                    var client = ClientSession.Open(run.Plan.Client!, run.Output, run.Cancellation);
                    run.Logs.AddRange(client.Logs); // Scanned with the server's at teardown, after the scenario stops the client.
                    return client;
                },
                server => OwnedServerSession.WaitUntilJoinable(server, RunPlan.SessionCapability, TimeSpan.FromSeconds(run.Plan.StartupSeconds), run.Cancellation),
                run.Report, run.Output, run.Cancellation)),
            "empty-save" => RoadsScenarios.EmptyNetworkReplacesOld(run.Server, () => run.Server.Execute("road_generate"), run.Session.Restart, run.Report, cancellation: run.Cancellation),
            _ => RoadsScenarios.BridgeAppendSurvivesRespawn(run.Server, () => run.Server.Execute(run.Plan.Append), run.Session.Restart, run.Plan.Expected, run.Report, run.Cancellation),
        },
    },
});

static Task Sync(Action scenario) { scenario(); return Task.CompletedTask; }
