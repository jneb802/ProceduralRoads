using System.Globalization;
using System.Text.Json;
using Valheim.Testing.Game;

namespace ProceduralRoads.SystemTests;

// Callers supply an OWNED disposable session. Restart must wait for completion,
// reconnect and verify the same fixture and plugin pins before returning.
public static class RoadsScenarios
{
    private static Observation Network(GameActor server, Capability capability) => server.ObserveComplete(capability, "roads-memory");
    private static void Empty(Observation network)
    {
        foreach (string field in new[] { "cells", "points", "crossings", "pendingZones" })
            if (network.Data.GetProperty(field).GetInt32() != 0) throw new InvalidOperationException("Network is not empty: " + field);
    }
    public static async Task EmptyNetworkReplacesOld(GameActor server, Action regenerateEmptyOnce, Func<GameActor> restartOwnedSession, ScenarioReport report, TimeSpan? generationTimeout = null, CancellationToken cancellation = default)
    {
        var capability = server.RequireCapability("roads.testing/network");
        report.Step("nonempty starting fixture", () =>
        {
            if (Network(server, capability).Data.GetProperty("cells").GetInt32() <= 0) throw new InvalidOperationException("Fixture must contain a previous network.");
        });
        cancellation.ThrowIfCancellationRequested();
        report.Step("generate empty network once", regenerateEmptyOnce);
        await report.StepAsync("generation completed", () => Check.Eventually(() => server.Observe(capability), n => n.Source == "roads-memory" && n.Complete,
            generationTimeout ?? TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(500), cancellation), "Empty generation did not complete correctly.");
        report.Step("empty before save", () => Empty(Network(server, capability)));
        cancellation.ThrowIfCancellationRequested();
        report.Step("confirmed save", () => server.SaveConfirmed());
        GameActor? reloaded = null;
        try
        {
            report.Step("restart owned fixture", () => reloaded = restartOwnedSession());
            report.Step("empty network loaded from save", () =>
            {
                var after = Network(reloaded!, reloaded!.RequireCapability("roads.testing/network")); Empty(after);
                if (!after.Data.GetProperty("loadedFromSave").GetBoolean()) throw new InvalidOperationException("Observed regeneration, not persistence.");
            });
        }
        finally { if (reloaded != null) report.Step("disconnect reloaded actor", reloaded.Dispose); }
    }

    public static async Task BridgeAppendSurvivesRespawn(GameActor server, Action appendOnce, Func<GameActor> restartOwnedSession,
        IReadOnlyList<ZoneExpectation> expected, ScenarioReport report, CancellationToken cancellation = default, TimeSpan? drainTimeout = null)
    {
        if (expected.Count == 0 || expected.All(x => x.Pieces.Count == 0)) throw new ArgumentException("Freeze expected bridge identities independently before the run.");
        if (expected.Select(x => (x.X, x.Z)).Distinct().Count() != expected.Count) throw new ArgumentException("Duplicate zone expectations.");
        var capability = server.RequireCapability("roads.testing/network");
        report.Step("no previous pending append", () =>
        {
            if (Network(server, capability).Data.GetProperty("pendingZones").GetInt32() != 0)
                throw new InvalidOperationException("Fixture already has pending work; cannot attribute this race to the append.");
        });
        cancellation.ThrowIfCancellationRequested();
        report.Step("append once", appendOnce);
        report.Step("append is still pending", () =>
        {
            if (Network(server, capability).Data.GetProperty("pendingZones").GetInt32() == 0)
                throw new InvalidOperationException("Fixture did not exercise the race: append drained before respawn.");
        });
        report.Step("respawn while pending", () => server.Execute("road_bridges respawn"));
        // Observations only. No repeated append/respawn/save while polling.
        await report.StepAsync("append queue drained", () => Check.Eventually(() => Network(server, capability), n => n.Data.GetProperty("pendingZones").GetInt32() == 0,
            drainTimeout ?? TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1), cancellation), "Queue did not drain.");
        report.Step("independent piece census", () => CompareZones(server, expected));
        cancellation.ThrowIfCancellationRequested();
        report.Step("confirmed save", () => server.SaveConfirmed());
        GameActor? reloaded = null;
        try
        {
            report.Step("restart owned fixture", () => reloaded = restartOwnedSession());
            report.Step("pieces persist after restart", () =>
            {
                var network = Network(reloaded!, reloaded!.RequireCapability("roads.testing/network"));
                if (!network.Data.GetProperty("loadedFromSave").GetBoolean() || network.Data.GetProperty("pendingZones").GetInt32() != 0)
                    throw new InvalidOperationException("Restart regenerated roads or still has pending bridge work.");
                CompareZones(reloaded!, expected);
            });
        }
        finally { if (reloaded != null) report.Step("disconnect reloaded actor", reloaded.Dispose); }
    }
    // Explicit fixture authoring, never an acceptance result; append/respawn are never issued here.
    public static async Task PrepareBridgeZones(GameActor server, IReadOnlyList<ZoneExpectation> zones, ScenarioReport report, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        await ZonePreparation.EnsureGeneratedAsync(server, zones.Select(zone => (zone.X, zone.Z)), report, TimeSpan.FromSeconds(60), cancellation: cancellation);
        cancellation.ThrowIfCancellationRequested();
        report.Step("confirmed preparation save", () => server.SaveConfirmed());
    }
    public static void CompareZones(GameActor server, IReadOnlyList<ZoneExpectation> expected)
    {
        var capability = server.RequireCapability("roads.testing/bridge-zone");
        foreach (var zone in expected)
        {
            var observed = server.Observe(capability, zone.X.ToString(CultureInfo.InvariantCulture), zone.Z.ToString(CultureInfo.InvariantCulture));
            observed.RequireComplete("zdo-store");
            if (observed.Data.GetProperty("zoneX").GetInt32() != zone.X || observed.Data.GetProperty("zoneZ").GetInt32() != zone.Z) throw new InvalidOperationException("Census returned a different zone.");
            var pieces = observed.Data.GetProperty("pieces").EnumerateArray().Select(value => new PlacedObject(value.GetProperty("prefabHash").GetInt32(),
                value.GetProperty("position").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
                value.GetProperty("rotation").EnumerateArray().Select(x => x.GetDouble()).ToArray())).ToArray();
            TransformMatch.Match(pieces, zone.Pieces, positionTolerance: .05, angleToleranceDegrees: 1);
        }
    }
}
public sealed record ZoneExpectation(int X, int Z, IReadOnlyList<PlacedObject> Pieces);
