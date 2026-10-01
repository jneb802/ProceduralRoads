using Valheim.Testing.Game;

namespace ProceduralRoads.SystemTests;

/// <summary>
/// The maintained end-to-end terrain and paint persistence check, run against a world that prepare-terrain wrote and
/// saved. A stock client (ValheimCLI only; Roads and MWL pinned absent), launched by the runner or attached to, joins the
/// owned server once it accepts game connections, is protected, arrives on the fixture's dry support point, and measures
/// heights and colliders, paint, and stationary support against the prepared plans, whose expectations came from native
/// pre-write inputs, never from these readings. Then a confirmed save, the client leaves, the server restarts, the client
/// rejoins and everything is measured again. Every step must pass: a prepared fixture or a paint pass never stands in for
/// grounding, and the restart round never reuses the first round's readings. The client is closed in every outcome: an
/// owned client's process is stopped, an attached one is left running.
/// </summary>
public static class TerrainClientScenario
{
    public static void Run(RunPlan runPlan, GameActor server, Func<GameActor> restartOwnedServer, Func<ClientSession> openClient,
        Action<GameActor> waitUntilJoinable, ScenarioReport report, string output, CancellationToken cancellation = default)
    {
        var plan = runPlan.Client ?? throw new ArgumentException("A terrain-persistence run needs its client.");
        SurfacePlan? height = null; PaintPlan? paint = null;
        report.Step("prepared client plans, unchanged since pinned", () =>
        {
            height = SurfacePlan.Read(runPlan.HeightPlan!.Verified());
            paint = PaintPlan.Read(runPlan.PaintPlan!.Verified());
            if (height.Support is not { } support) throw new InvalidOperationException("The prepared height plan declares no support point; prepare the fixture again.");
            if (support.Height < PersistentTerrainScenario.SeaLevel + PersistentTerrainScenario.DryMargin)
                throw new InvalidOperationException($"The support point at {support.Height:F2} m is not genuinely dry.");
        });
        // The client rounds are the toolkit's (ClientRounds): join (protected), arrive on the support point, then these
        // measurements; between the rounds a confirmed save, the client leaves and only the owned server restarts.
        new ClientRounds
        {
            Client = plan, WorldUid = runPlan.WorldUid, Report = report, Output = output, WaitUntilJoinable = waitUntilJoinable,
            RestartServer = restartOwnedServer, Arrival = height!.Support, ArriveStep = "arrive on the dry support point", Cancellation = cancellation,
            OpenStep = plan.Owned ? "launch the owned client to its menu: exactly the declared plugins, Roads and MWL absent"
                : "attach to the operator's client at its menu: exactly the declared plugins, Roads and MWL absent",
        }.Run(server, openClient, round => Measure(round, height!, paint!));
    }

    private static void Measure(ClientRound round, SurfacePlan height, PaintPlan paint)
    {
        round.Step("client heightmap and its own collider match the prepared profile", () =>
        {
            var readings = SurfaceProbe.Compare(round.Client, height.ExpectedFrom, height.Samples, height.Tolerance);
            string file = round.Write("surfaces", readings);
            if (readings.Any(r => !r.Passed)) throw new InvalidOperationException($"{readings.Count(r => !r.Passed)} of {readings.Count} surface samples differ; see {Path.GetFileName(file)}.");
        });
        round.Step("client paint matches the prepared core and verge", () =>
        {
            var readings = PaintProbe.Compare(round.Client, paint.ExpectedFrom, paint.Samples, paint.Tolerance);
            string file = round.Write("paint", readings);
            if (readings.Any(r => !r.Passed)) throw new InvalidOperationException($"{readings.Count(r => !r.Passed)} of {readings.Count} paint samples differ; see {Path.GetFileName(file)}.");
        });
        round.Step("the player stands on the dry road core", () =>
        {
            try { round.Write("support", PlayerPlacement.RequireSupported(round.Client, height.Support!)); }
            catch (SupportException error) { round.Write("support", error.Readings); throw; }
        });
    }
}
