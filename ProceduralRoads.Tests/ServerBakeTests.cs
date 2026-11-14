using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Road terrain written by the server for clients without the mod: which
/// zones, what to do with each, and the rule that a zone is stamped as
/// carrying its roads only once its compiler has saved them. The Unity side
/// (temporary terrain, ghost init) is ServerTerrainBake and is exercised in
/// game; everything it decides is decided here.
/// </summary>
public class ServerBakeTests
{
    private const int Version = 12345;
    private const long Me = 1;

    private static List<ServerBakePlanner.Compiler> Compilers(params ServerBakePlanner.Compiler[] compilers) =>
        new(compilers);

    private static ServerBakePlanner.Action Decide(bool generated, bool loadedHere,
        List<ServerBakePlanner.Compiler> compilers, int version = Version, bool beforeNetwork = false) =>
        ServerBakePlanner.Decide(version, generated, beforeNetwork, loadedHere, compilers, Me);

    [Fact]
    public void NoNetworkMeansNothingToWrite()
    {
        Assert.Equal(ServerBakePlanner.Action.NoNetwork, Decide(true, false, Compilers(), version: 0));
    }

    [Fact]
    public void AnUngeneratedZoneIsLeftToItsGeneration()
    {
        Assert.Equal(ServerBakePlanner.Action.LeaveToGeneration, Decide(false, false, Compilers()));
        Assert.Equal(ServerBakePlanner.Action.LeaveToGeneration,
            Decide(false, false, Compilers(new ServerBakePlanner.Compiler(0, 0, false))));
    }

    [Fact]
    public void AZoneLoadedHereIsLeftToTheLiveZoneHooks()
    {
        // Nothing saved yet: the zone-spawn hook makes its one compiler.
        Assert.Equal(ServerBakePlanner.Action.LeaveToLiveZone, Decide(true, true, Compilers()));
    }

    /// <summary>
    /// A loaded zone is not evidence its terrain was written. The live write is
    /// an Awake postfix that returns without writing when another peer owns the
    /// compiler, and no later ownership transfer makes Awake run again -- so a
    /// stale compiler in a loaded zone must stay pending, not be counted done.
    /// </summary>
    [Fact]
    public void AStaleCompilerInALoadedZoneIsNeverCountedAsDone()
    {
        // Owned by a peer standing there: wait for them, whether or not it is loaded here.
        Assert.Equal(ServerBakePlanner.Action.WaitForOwner,
            Decide(true, true, Compilers(new ServerBakePlanner.Compiler(0, 7, ownerActiveHere: true))));

        // Ours, or nobody's, and the zone is live: write through the live compiler
        // rather than raising a second one on a temporary terrain.
        Assert.Equal(ServerBakePlanner.Action.WriteLiveCompiler,
            Decide(true, true, Compilers(new ServerBakePlanner.Compiler(0, 0, false))));
        Assert.Equal(ServerBakePlanner.Action.WriteLiveCompiler,
            Decide(true, true, Compilers(new ServerBakePlanner.Compiler(Version - 1, Me, ownerActiveHere: true))));
        // A live compiler still owned by another peer waits for actual release.
        Assert.Equal(ServerBakePlanner.Action.WaitForOwner,
            Decide(true, true, Compilers(new ServerBakePlanner.Compiler(0, 7, ownerActiveHere: false))));

        // Already carrying this network: nothing to do, loaded or not.
        Assert.Equal(ServerBakePlanner.Action.AlreadyCurrent,
            Decide(true, true, Compilers(new ServerBakePlanner.Compiler(Version, 0, false, current: true))));
        // Two compilers are left alone, loaded here or saved.
        foreach (bool loadedHere in new[] { true, false })
            Assert.Equal(ServerBakePlanner.Action.DuplicateCompilers,
                Decide(true, loadedHere, Compilers(new ServerBakePlanner.Compiler(0, 0, false),
                    new ServerBakePlanner.Compiler(0, 0, false))));
    }

    [Fact]
    public void AGeneratedZoneWithoutACompilerGetsItsOneCompiler()
    {
        Assert.Equal(ServerBakePlanner.Action.CreateCompiler, Decide(true, false, Compilers()));
    }

    [Fact]
    public void AStampedCompilerIsLeftAloneAndAnOldStampIsRewritten()
    {
        Assert.Equal(ServerBakePlanner.Action.AlreadyCurrent,
            Decide(true, false, Compilers(new ServerBakePlanner.Compiler(Version, 0, false, current: true))));
        Assert.Equal(ServerBakePlanner.Action.WriteSavedCompiler,
            Decide(true, false, Compilers(new ServerBakePlanner.Compiler(Version - 1, 0, false))));
    }

    /// <summary>
    /// Stamp 0 is what every compiler of a 1.4.4 world reads as (that build never stamped), and what a player's own
    /// digging makes. In a zone generated before the network it is theirs; only a zone generated with the network in
    /// place, whose compiler simply has not been written yet, is written.
    /// </summary>
    [Fact]
    public void AnUnstampedCompilerIsWrittenOnlyOutsideTheCutoff()
    {
        Assert.Equal(ServerBakePlanner.Action.GeneratedBeforeNetwork,
            Decide(true, false, Compilers(new ServerBakePlanner.Compiler(0, 0, false)), beforeNetwork: true));
        Assert.Equal(ServerBakePlanner.Action.WriteSavedCompiler,
            Decide(true, false, Compilers(new ServerBakePlanner.Compiler(0, 0, false)), beforeNetwork: false));
        // Nor is a compiler made for one, loaded here or not, nor a live one written.
        Assert.Equal(ServerBakePlanner.Action.GeneratedBeforeNetwork, Decide(true, false, Compilers(), beforeNetwork: true));
        Assert.Equal(ServerBakePlanner.Action.GeneratedBeforeNetwork,
            Decide(true, true, Compilers(new ServerBakePlanner.Compiler(0, Me, false)), beforeNetwork: true));
        Assert.Equal(ServerBakePlanner.RepairOutcome.Resolved,
            ServerBakePlanner.ResolveRepair(ServerBakePlanner.Action.GeneratedBeforeNetwork, ServerBakePlanner.WriteReport.NotAttempted));
    }

    /// <summary>
    /// A zone the network protects (explored before it, in the modes that protect explored land) keeps everything: the
    /// vegetation pass owes it nothing, a stump or log across an old road in it too. A zone generated with the network,
    /// and not yet cleared for it, is still owed its clearing.
    /// </summary>
    [Fact]
    public void EverythingInAProtectedZoneIsKept()
    {
        Assert.False(ServerBakePlanner.VegetationOwed(generated: true, cleared: false, excluded: true));
        Assert.True(ServerBakePlanner.VegetationOwed(generated: true, cleared: false, excluded: false));
        Assert.False(ServerBakePlanner.VegetationOwed(generated: true, cleared: true, excluded: false));
        Assert.False(ServerBakePlanner.VegetationOwed(generated: false, cleared: false, excluded: false));
    }

    /// <summary>The bake's assessment of a zone's clearing, which its pass acts on and road_bake reports: stopped, protected,
    /// not generated, completed, owed and waiting for players are distinct answers; a zone that owes nothing asks neither
    /// for its work nor for players, and a player near a zone with nothing in reach does not hold it.</summary>
    [Fact]
    public void TheClearingAssessmentTellsProtectedCompletedOwedWaitingAndStoppedApart()
    {
        int asked = 0;
        bool Ask(bool answer) { asked++; return answer; }
        ServerBakePlanner.Clearing Assess(bool stopped = false, bool generated = true, bool cleared = false, bool excluded = false,
            bool work = true, bool near = false) =>
            ServerBakePlanner.AssessClearing(stopped, generated, cleared, excluded, () => Ask(work), () => Ask(near));
        Assert.Equal(ServerBakePlanner.Clearing.Stopped, Assess(stopped: true));
        Assert.Equal(ServerBakePlanner.Clearing.Protected, Assess(excluded: true));
        Assert.Equal(ServerBakePlanner.Clearing.Protected, Assess(excluded: true, generated: false));
        Assert.Equal(ServerBakePlanner.Clearing.NotGenerated, Assess(generated: false));
        Assert.Equal(ServerBakePlanner.Clearing.Completed, Assess(cleared: true, near: true));
        Assert.Equal(0, asked);
        Assert.Equal(ServerBakePlanner.Clearing.Owed, Assess());
        Assert.Equal(ServerBakePlanner.Clearing.WaitingForPlayer, Assess(near: true));
        Assert.Equal(ServerBakePlanner.Clearing.Owed, Assess(work: false, near: true));
    }

    /// <summary>The stump and the log found on a paved road in an already generated zone, and one clear of it.</summary>
    [Fact]
    public void AnExistingStumpOnTheRoadIsRemovedAndALogAcrossItTurnedWhileOneClearIsLeft()
    {
        var road = Enumerable.Range(-60, 121).Select(x => new VegetationClearing.RoadCentre(new Vector2(x, 0f), 2f)).ToList();
        var none = new VegetationClearing.ZoneAreas(new List<ZoneSystem.ClearArea>());
        var nobody = new List<VegetationClearing.Footprint>();
        int stump = "stubbe".GetStableHashCode(), log = "FirTree_oldLog".GetStableHashCode();
        var vegetation = new HashSet<int> { stump, log };
        Assert.True(VegetationClearing.IsLying(stump) && VegetationClearing.IsLying(log));
        Assert.Equal(VegetationClearing.Lying.Remove, VegetationClearing.Decide(stump, new Vector2(10f, 0.5f), false, vegetation, none, nobody, nobody,
            road, 1f, null, 90f, out _));
        Assert.Equal(VegetationClearing.Lying.Turned, VegetationClearing.Decide(log, new Vector2(0f, 5f), false, vegetation, none, nobody, nobody,
            road, 1f, null, 0f, out _));
        Assert.Equal(VegetationClearing.Lying.Clear, VegetationClearing.Decide(log, new Vector2(0f, 30f), false, vegetation, none, nobody, nobody,
            road, 1f, null, 0f, out _));
    }

    /// <summary>The road onto a Black Forest crypt's door, at the points the network lays it 0 to 6 m from a stump and a log the
    /// game grew there before the road (its zone is placed before its roads), 4 m wide, at the height the game reads.</summary>
    private static readonly List<VegetationClearing.RoadCentre> CryptRoad = new[]
    {
        (3918.40f, -4715.78f), (3918.78f, -4714.85f), (3919.17f, -4713.93f), (3919.55f, -4713.00f), (3919.93f, -4712.08f),
        (3920.31f, -4711.16f), (3920.70f, -4710.23f), (3921.08f, -4709.31f), (3921.46f, -4708.38f), (3921.84f, -4707.46f),
        (3922.23f, -4706.54f), (3922.61f, -4705.61f), (3922.99f, -4704.69f),
    }.Select(p => new VegetationClearing.RoadCentre(new Vector2(p.Item1, p.Item2), 2f, 71.675f)).ToList();

    /// <summary>
    /// A stump and a fallen log the game grew on a crypt's road inside the crypt's exterior radius (18 m; the crypt clears no
    /// area, so the game grows vegetation there), in a zone the game generated before the road: neither is one of the
    /// crypt's own parts, so each is judged as generation judges it. Each stands on the road whichever way it is turned, so
    /// both are removed. Where the crypt's parts are not known, or one stands at a part's spot, the bake cannot tell it from
    /// the crypt and leaves it.
    /// </summary>
    [Fact]
    public void AStumpAndALogOnACryptsRoadThatAreNotTheCryptsOwnAreJudgedAsAnywhere()
    {
        int stump = "stubbe".GetStableHashCode(), log = "FirTree_oldLog".GetStableHashCode();
        var vegetation = new HashSet<int> { stump, log };
        var none = new VegetationClearing.ZoneAreas(new List<ZoneSystem.ClearArea>());
        var nobody = new List<VegetationClearing.Footprint>();
        var stumpAt = new Vector2(3920.108f, -4710.460f);
        var logAt = new Vector2(3920.145f, -4712.148f);
        var stumpLaid = new VegetationClearing.Lean(0f, 0f, 72.097f);
        var logLaid = new VegetationClearing.Lean(340.8f, 2.6f, 72.328f);
        List<VegetationClearing.Footprint> Crypt(System.Func<Vector3, bool?>? partAt) =>
            new() { new(new Vector2(3914.0f, -4726.4f), 18f, partAt) };
        VegetationClearing.Lying Judge(int prefab, Vector2 at, float yaw, VegetationClearing.Lean laid, List<VegetationClearing.Footprint> crypt) =>
            VegetationClearing.Decide(prefab, at, false, vegetation, none, crypt, nobody, CryptRoad, 1f, null, yaw, out _, laid);

        var notItsOwn = Crypt(_ => false);
        Assert.Equal(VegetationClearing.Lying.Remove, Judge(stump, stumpAt, 0f, stumpLaid, notItsOwn));
        Assert.Equal(VegetationClearing.Lying.Remove, Judge(log, logAt, 223f, logLaid, notItsOwn));
        // The crypt's parts not known (its prefab unread), or a part standing there: the crypt's.
        Assert.Equal(VegetationClearing.Lying.Clear, Judge(stump, stumpAt, 0f, stumpLaid, Crypt(null)));
        Assert.Equal(VegetationClearing.Lying.Clear, Judge(stump, stumpAt, 0f, stumpLaid, Crypt(_ => null)));
        Assert.Equal(VegetationClearing.Lying.Clear, Judge(log, logAt, 223f, logLaid,
            Crypt(p => Vector3.Distance(p, new Vector3(logAt.x, 72.328f, logAt.y)) < 0.1f)));
    }

    /// <summary>A Black Forest road by a ruin, 4 m wide: the points the offline network lays there, moved 1.6 m east and 2.5 m
    /// north to where the game's network runs (through a player standing on it at (3453.3, -4895.5)).</summary>
    private static readonly List<VegetationClearing.RoadCentre> RuinRoad = new[]
    {
        (3442.51f, -4887.45f), (3443.34f, -4887.92f), (3444.15f, -4888.41f), (3444.94f, -4888.94f), (3445.70f, -4889.51f),
        (3446.44f, -4890.11f), (3447.15f, -4890.73f), (3447.84f, -4891.39f), (3448.49f, -4892.08f), (3449.12f, -4892.80f),
        (3449.71f, -4893.54f), (3450.27f, -4894.30f), (3450.80f, -4895.09f), (3451.19f, -4895.71f), (3451.58f, -4896.32f),
        (3451.98f, -4896.94f), (3452.53f, -4897.71f), (3453.15f, -4898.43f), (3453.85f, -4899.07f), (3454.61f, -4899.64f),
        (3455.42f, -4900.13f), (3456.29f, -4900.53f), (3457.18f, -4900.84f), (3458.11f, -4901.05f), (3458.72f, -4901.30f),
        (3459.34f, -4901.55f),
    }.Select(p => new VegetationClearing.RoadCentre(new Vector2(p.Item1, p.Item2), 2f, 47.43f)).ToList();

    /// <summary>
    /// Seen in game: a fallen fir lying over most of a road, saved at (3454.030, 47.675, -4892.286) with rotation
    /// (-10.0, 66.0, 15.7), in a zone generated with the road. The game lays every fallen log and stump on the ground's tilt
    /// (its entries' m_chanceToUseGroundTilt is 1), which turns its length a quarter turn from the yaw it drew: it drew 336
    /// and lies at 66. Generation judged it at 336, lying along the road beside it, and left it; the zone was recorded
    /// cleared, so the bake, which reads it at 66 across the road, never saw it. Generation now judges the heading it will
    /// lie at, so it gives the bake's verdict, and the yaw it hands back lays it where the bake would turn it.
    /// </summary>
    [Fact]
    public void GenerationJudgesALogLaidOnTheGroundsTiltAtTheHeadingItWillLieAt()
    {
        System.Func<Vector3, Heightmap.Biome> forest = _ => Heightmap.Biome.BlackForest;
        const string name = "FirTree_oldLog";
        int log = name.GetStableHashCode();
        var at = new Vector2(3454.030f, -4892.286f);
        var laid = new VegetationClearing.Lean(350f, 15.7f, 47.675f);
        var none = new VegetationClearing.ZoneAreas(new List<ZoneSystem.ClearArea>());
        var nobody = new List<VegetationClearing.Footprint>();
        const float drawn = 336f, lies = 66f;
        Assert.Equal(lies, (drawn + RoadClearAreaManager.GroundTiltTurn) % 360f, 3);

        // The bake reads it as it lies, across the road.
        VegetationClearing.Lying baked = VegetationClearing.Decide(log, at, false, new HashSet<int> { log }, none, nobody, nobody,
            RuinRoad, 1f, forest, lies, out float bakeTurned, laid);
        Assert.NotEqual(VegetationClearing.Lying.Clear, baked);

        // Generation, handed the yaw PlaceVegetation drew, judges it at the same heading and gives the same verdict.
        float yaw = drawn;
        bool notPlaced = RoadClearAreaManager.OnRoad(none, RuinRoad, new Vector3(at.x, laid.Origin, at.y), name, 1f, forest, ref yaw,
            new VegetationClearing.Lean(0f, 0f, laid.Origin), groundTilt: true);
        Assert.Equal(baked == VegetationClearing.Lying.Remove, notPlaced);
        if (!notPlaced)
        {
            float liesAt = (yaw + RoadClearAreaManager.GroundTiltTurn) % 360f;
            Assert.Equal(bakeTurned, liesAt, 3);
            Assert.Equal(VegetationClearing.Lying.Clear, VegetationClearing.Decide(log, at, false, new HashSet<int> { log }, none, nobody,
                nobody, RuinRoad, 1f, forest, liesAt, out _, laid));
        }

        // Its drawn yaw read as its heading, as before: along the road, left where the game lays it across.
        float asDrawn = drawn;
        Assert.False(RoadClearAreaManager.OnRoad(none, RuinRoad, new Vector3(at.x, laid.Origin, at.y), name, 1f, forest, ref asDrawn,
            new VegetationClearing.Lean(0f, 0f, laid.Origin)));
        Assert.Equal(drawn, asDrawn);
    }

    /// <summary>
    /// The same vegetation the game grew by a location's road, judged by generation (RoadClearAreaManager.OnRoad, the candidate
    /// PlaceVegetation draws) and by the bake (VegetationClearing.Decide, the saved object in a zone generated before its
    /// roads), with the clear areas each builds for the zone (the location's own first, then the roads'), the same road points
    /// and the same spot: a stump and a fallen log on the road (live numbers by a crypt's door), a bush on the road, a swamp
    /// tree whose trunk reaches it, a boulder the rock clearing takes there, a rock it leaves, and a bush, a tree and a log
    /// inside the location's footprint but off the road. Once by a crypt, which clears no area, once by a location that does.
    /// A stump or fallen log is drawn a quarter turn from where it lies, as the game lays it on the ground's tilt: generation
    /// is handed the drawn yaw, the bake the saved one. The verdicts are the same (removed, turned to lie at the same heading,
    /// or left). Off the road nothing is cleared by the bake;
    /// generation keeps the location's own clear area empty only by the game's own test, as without the mod.
    /// </summary>
    [Fact]
    public void TheBakeJudgesVegetationByALocationsRoadAsGenerationDoes()
    {
        const Heightmap.Biome swamp = Heightmap.Biome.Swamp;
        System.Func<Vector3, Heightmap.Biome> biomeAt = _ => swamp;
        var centre = new Vector2(3914.0f, -4726.4f);
        const float radius = 18f;
        var entries = new (string prefab, Vector2 at, float yaw, VegetationClearing.Lean laid, bool onRoad)[]
        {
            ("stubbe", new Vector2(3920.108f, -4710.460f), 0f, new VegetationClearing.Lean(0f, 0f, 72.097f), true),
            ("FirTree_oldLog", new Vector2(3920.145f, -4712.148f), 223f, new VegetationClearing.Lean(340.8f, 2.6f, 72.328f), true),
            ("Bush01_0", new Vector2(3919.2f, -4713.9f), 40f, new VegetationClearing.Lean(0f, 0f, 71.7f), true),
            ("SwampTree1", new Vector2(3917.07f, -4711.98f), 10f, new VegetationClearing.Lean(0f, 0f, 71.7f), true),
            ("rock4_forest", new Vector2(3918.8f, -4714.85f), 0f, new VegetationClearing.Lean(0f, 0f, 71.7f), true),
            ("rock_mistlands1", new Vector2(3918.8f, -4714.85f), 0f, new VegetationClearing.Lean(0f, 0f, 71.7f), true),
            // A fallen log across the road inside the crypt's radius, which a turn takes off it.
            ("FirTree_oldLog", new Vector2(3913f, -4715f), 90f, new VegetationClearing.Lean(0f, 0f, 71.7f), true),
            ("Bush01_0", new Vector2(3908.0f, -4730.0f), 0f, new VegetationClearing.Lean(0f, 0f, 70f), false),
            ("SwampTree1", new Vector2(3906.0f, -4722.0f), 0f, new VegetationClearing.Lean(0f, 0f, 70f), false),
            ("FirTree_oldLog", new Vector2(3910.0f, -4734.0f), 90f, new VegetationClearing.Lean(0f, 0f, 70f), false),
        };
        Assert.True(RoadRockPolicy.ClearingTakes("rock4_forest", swamp));
        Assert.False(RoadRockPolicy.ClearingTakes("rock_mistlands1", swamp));
        var vegetation = new HashSet<int>(entries.Select(e => e.prefab.GetStableHashCode()));
        var nobody = new List<VegetationClearing.Footprint>();
        // The location's parts are known, and none of these is one.
        var location = new List<VegetationClearing.Footprint> { new(centre, radius, _ => false) };
        foreach (bool clearsArea in new[] { false, true })
        {
            // As ZoneSystem.PlaceLocations adds it, then the roads' as RoadClearAreaManager builds them.
            VegetationClearing.ZoneAreas Areas()
            {
                var areas = new List<ZoneSystem.ClearArea>();
                if (clearsArea) areas.Add(new ZoneSystem.ClearArea(new Vector3(centre.x, 70f, centre.y), radius));
                int firstRoad = areas.Count;
                foreach (VegetationClearing.RoadCentre point in CryptRoad)
                    areas.Add(new ZoneSystem.ClearArea(new Vector3(point.Centre.x, 0f, point.Centre.y),
                        2f * point.HalfWidth * RoadConstants.VegetationClearMultiplier));
                return new VegetationClearing.ZoneAreas(areas, firstRoad, areas.Count);
            }
            foreach (var (prefab, at, yaw, laid, onRoad) in entries)
            {
                string where = $"{prefab} at ({at.x}, {at.y}), location clears its area {clearsArea}";
                Assert.True((at - centre).magnitude < radius, where);
                // The game lays a fallen log or stump on the ground's tilt, a quarter turn from the yaw it drew: generation is
                // handed the drawn yaw, the bake reads the saved one it lies at.
                bool groundTilt = VegetationClearing.IsLying(prefab.GetStableHashCode());
                float drawn = groundTilt ? (yaw - RoadClearAreaManager.GroundTiltTurn + 360f) % 360f : yaw;
                float generated = drawn;
                bool notPlaced = RoadClearAreaManager.OnRoad(Areas(), CryptRoad, new Vector3(at.x, laid.Origin, at.y), prefab, 1f, biomeAt,
                    ref generated, laid, groundTilt: groundTilt);
                float liesAt = groundTilt ? generated + RoadClearAreaManager.GroundTiltTurn : generated;
                VegetationClearing.Lying baked = VegetationClearing.Decide(prefab.GetStableHashCode(), at, false, vegetation, Areas(), location,
                    nobody, CryptRoad, 1f, biomeAt, yaw, out float turned, laid);
                // The road's verdict, by the one rule both ask.
                VegetationClearing.Lying road = VegetationClearing.Judge(prefab.GetStableHashCode(), new Vector3(at.x, laid.Origin, at.y), 1f,
                    yaw, laid, Areas(), CryptRoad, biomeAt, out float judged);
                Assert.Equal(road, baked);
                Assert.Equal(judged, turned, 3);
                if (!onRoad)
                {
                    // Off the road the bake clears nothing, by a crypt or by a location that clears its area.
                    Assert.True(baked == VegetationClearing.Lying.Clear, where);
                    // Generation places it by a crypt; by a location that clears its area the game's own test keeps it out.
                    Assert.True(notPlaced == clearsArea, where);
                    Assert.True(clearsArea || generated == drawn, where);
                    continue;
                }
                // On the road generation leaves out what the bake removes, and turns what it turns; inside a location's own clear
                // area the game keeps everything out besides (a rock the rock clearing takes there is left to it by the bake).
                bool gameKeepsOut = clearsArea && VegetationClearing.InGameArea(Areas(), at);
                Assert.True(notPlaced == (gameKeepsOut || baked == VegetationClearing.Lying.Remove), $"{where}: generation {notPlaced}, bake {baked}");
                if (!notPlaced) Assert.True(Mathf.Abs(((liesAt - turned) % 360f + 540f) % 360f - 180f) < 0.001f, $"{where}: generation lies at {liesAt}, bake {turned}");
            }
        }
        // What each is, by the road (by the crypt): the stump, the log, the bush on the road, the tree reaching it and the rock
        // the rock clearing leaves go; the rock it takes is its own; the second log is turned off the road, as generation
        // turns it (its turns may lie on the crypt's ground, which clears no area: generation keeps none there); the rest stay.
        var expected = new[]
        {
            VegetationClearing.Lying.Remove, VegetationClearing.Lying.Remove, VegetationClearing.Lying.Remove, VegetationClearing.Lying.Remove,
            VegetationClearing.Lying.Clear, VegetationClearing.Lying.Remove, VegetationClearing.Lying.Turned,
            VegetationClearing.Lying.Clear, VegetationClearing.Lying.Clear, VegetationClearing.Lying.Clear,
        };
        var crypt = new VegetationClearing.ZoneAreas(new List<ZoneSystem.ClearArea>(CryptRoad.Select(point =>
            new ZoneSystem.ClearArea(new Vector3(point.Centre.x, 0f, point.Centre.y), 2f * point.HalfWidth * RoadConstants.VegetationClearMultiplier))));
        for (int i = 0; i < entries.Length; i++)
            Assert.True(expected[i] == VegetationClearing.Decide(entries[i].prefab.GetStableHashCode(), entries[i].at, false, vegetation, crypt,
                location, nobody, CryptRoad, 1f, biomeAt, entries[i].yaw, out _, entries[i].laid), $"{entries[i].prefab} at {entries[i].at}");
    }

    [Theory]
    [InlineData("rock4_forest", false, true)]
    [InlineData("rock4_forest", true, false)]
    [InlineData("cliff_mistlands1", false, true)]
    [InlineData("silvervein", false, true)]
    [InlineData("MineRock_Tin", false, false)]
    [InlineData("Beech1", false, false)]
    public void TheRockPassBringsAliveOnlyClearableRocksNoPlayerMade(string prefab, bool playerCreated, bool brought) =>
        Assert.Equal(brought, ServerBakePlanner.BringsRockAlive(prefab, playerCreated, new Dictionary<string, List<string>>()));

    /// <summary>A rock mined or carved earlier is saved under its fractured copy's name; the bake still brings it alive, so a
    /// road added later carves it (a player's own is still left).</summary>
    [Theory]
    [InlineData("rock4_copper_frac", false, true)]
    [InlineData("rock4_copper_frac", true, false)]
    [InlineData("rock1_mountain_frac", false, true)]
    [InlineData("Beech1_frac", false, false)]
    public void TheRockPassBringsAliveAFracturedRockToo(string prefab, bool playerCreated, bool brought)
    {
        var fracturedFrom = new Dictionary<string, List<string>>
        {
            ["rock4_copper_frac"] = new() { "rock4_copper" },
            ["rock1_mountain_frac"] = new() { "rock1_mountain" },
        };
        Assert.Equal(brought, ServerBakePlanner.BringsRockAlive(prefab, playerCreated, fracturedFrom));
    }

    [Fact]
    public void APlayerInTheZoneKeepsTheirCompilerUntilTheyLeave()
    {
        // Owned by a connected peer standing in the zone: wait.
        Assert.Equal(ServerBakePlanner.Action.WaitForOwner,
            Decide(true, false, Compilers(new ServerBakePlanner.Compiler(0, 7, ownerActiveHere: true))));
        // Their owner gone or elsewhere: write it.
        Assert.Equal(ServerBakePlanner.Action.WriteSavedCompiler,
            Decide(true, false, Compilers(new ServerBakePlanner.Compiler(0, 7, ownerActiveHere: false))));
        // Ours already: write it.
        Assert.Equal(ServerBakePlanner.Action.WriteSavedCompiler,
            Decide(true, false, Compilers(new ServerBakePlanner.Compiler(0, Me, ownerActiveHere: true))));
        // Already current is current whoever owns it.
        Assert.Equal(ServerBakePlanner.Action.AlreadyCurrent,
            Decide(true, false, Compilers(new ServerBakePlanner.Compiler(Version, 7, ownerActiveHere: true, current: true))));
    }

    [Fact]
    public void TheZonesWithRoadPointsAreExactlyTheZonesThatHaveThem()
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        using var scope = new ValheimWorldScope().WithWorld(world);
        try
        {
            RoadSpatialGrid.Clear();
            // A 4 m diagonal road, and a 2 m road running along the border
            // between zone rows 0 and 1 (z = 32), so points sit at every kind
            // of zone edge.
            var diagonal = new List<Vector2>();
            for (float t = -150f; t <= 150f; t += 10f)
                diagonal.Add(new Vector2(t, t * 0.7f + 5f));
            RoadSpatialGrid.AddRoadPath(diagonal, 4f, world);
            var border = new List<Vector2>();
            for (float x = -100f; x <= 100f; x += 10f)
                border.Add(new Vector2(x, 32f));
            RoadSpatialGrid.AddRoadPath(border, 2f, world);
            RoadSpatialGrid.FinalizeRoadNetwork();

            HashSet<Vector2s> zones = RoadSpatialGrid.GetZonesWithRoadPoints();
            Assert.NotEmpty(zones);
            Assert.All(zones, z =>
            {
                Assert.InRange(z.x, -6, 6);
                Assert.InRange(z.y, -6, 6);
            });
            for (int y = -6; y <= 6; y++)
            {
                for (int x = -6; x <= 6; x++)
                {
                    var zone = new Vector2s(x, y);
                    Assert.Equal(RoadSpatialGrid.GetRoadPointsInZone(zone).Count > 0, zones.Contains(zone));
                }
            }
            // The border road lies in both rows.
            Assert.Contains(new Vector2s(0, 0), zones);
            Assert.Contains(new Vector2s(0, 1), zones);
        }
        finally
        {
            RoadSpatialGrid.Clear();
        }
    }

    [Fact]
    public void AZoneWhoseCompilerWillNotSaveIsNotStamped()
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        using var scope = new ValheimWorldScope().WithWorld(world).WithZdos();
        try
        {
            RoadSpatialGrid.Clear();
            var path = new List<Vector2>();
            for (float x = -40f; x <= 40f; x += 8f)
                path.Add(new Vector2(x, 0f));
            RoadSpatialGrid.AddRoadPath(path, 4f, world);
            RoadSpatialGrid.FinalizeRoadNetwork();
            var zone = new Vector2s(0, 0);
            List<RoadSpatialGrid.RoadPoint> points = RoadSpatialGrid.GetRoadPointsInZone(zone);
            Heightmap hm = scope.RegisterHeightmap(zone, 64);
            TerrainComp tc = hm.m_terrainComp!;
            tc.m_nview.GetZDO().SetOwner(2); // another peer's

            // The explicit path writes the arrays whatever it is given; the
            // game refuses the save, and a stamp now would be a lie.
            RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, tc);
            Assert.Equal(0, tc.SaveCount);
            Assert.Null(tc.m_nview.GetZDO().GetByteArray(ZDOVars.s_TCData));
            Assert.False(RoadTerrainModifier.CarriesCurrentRoads(tc));

            // Once it is ours the same write saves, and only then is stamped.
            tc.m_nview.GetZDO().SetOwner(ZDOMan.instance.m_sessionID);
            RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, tc);
            Assert.Equal(1, tc.SaveCount);
            Assert.NotNull(tc.m_nview.GetZDO().GetByteArray(ZDOVars.s_TCData));
            Assert.True(RoadTerrainModifier.CarriesCurrentRoads(tc));
        }
        finally
        {
            RoadSpatialGrid.Clear();
        }
    }

    /// <summary>
    /// A failed ghost write is the zone's only chance unless something keeps it:
    /// vanilla finishes generating it, so it never takes the new-zone path
    /// again, and the queue has already passed it by.
    /// </summary>
    // ---- the scheduler: when a zone comes round again, and how many tries ----
    //
    // These drive the real scheduler with a fake clock, because the bug they
    // guard against was an interaction rather than a rule: a failed write made
    // the ledger ready for its next poll AND entered a separate ten-second wait
    // list. When the budget ran out the ledger dropped the zone, the surviving
    // wait entry queued it again, and a zone with no entry was treated as
    // having unlimited retries. Work carried on after "given up", forever.

    private const float Delay = 10f;

    /// <summary>
    /// One look at a zone, through the mapping ServerTerrainBake.Resolve calls
    /// (GhostRepairLedger.Answer over the real ResolveRepair). Returns whether it
    /// is worth coming back.
    /// </summary>
    private static bool Apply(GhostRepairLedger ledger, Vector2s zone, ServerBakePlanner.Action action,
        ServerBakePlanner.WriteReport report, float now) =>
        ledger.Answer(zone, ServerBakePlanner.ResolveRepair(action, report), now, Delay) == GhostRepairLedger.Answered.Kept;

    [Fact]
    public void AZoneWhoseGhostWriteFailedIsWrittenFromTheQueueInstead()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(4, -9);
        ledger.Record(zone);
        Assert.True(ledger.Holds(zone));

        // Still generating: nothing to hand over yet, and it is not forgotten.
        Assert.Empty(ledger.TakeReady(_ => false, 0f));
        Assert.Equal(1, ledger.Count);

        // Generated: it goes to the queue, and stays watched until the write
        // actually lands -- being handed over is not the same as fixed.
        Assert.Equal(new[] { zone }, ledger.TakeReady(_ => true, 0f));
        Assert.True(ledger.IsQueued(zone));

        // The queue wrote it: nothing left to watch, at any later time.
        ledger.Succeeded(zone);
        Assert.False(ledger.Holds(zone));
        Assert.Empty(ledger.TakeReady(_ => true, 1000f));
    }

    /// <summary>
    /// The budget belongs to failed WRITES, not to the clock. A zone can sit in
    /// the queue through a backlog or a terrain build; polling it must cost
    /// nothing and must never hand over a second copy.
    /// </summary>
    [Fact]
    public void PollingSpendsNothingAndNeverQueuesTheZoneTwice()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(4, -9);
        ledger.Record(zone);

        int queued = 0;
        for (int poll = 0; poll < 12; poll++)
            queued += ledger.TakeReady(_ => true, poll).Count;

        Assert.Equal(1, queued);
        Assert.True(ledger.Holds(zone));
        Assert.Equal(0, ledger.FailedWrites(zone));
    }

    /// <summary>
    /// Three failed live writes, then the clock run past every deadline any of
    /// them scheduled. Nothing may come back.
    /// </summary>
    [Fact]
    public void ThreeFailedLiveWritesStopForGoodEvenAfterEveryOldDeadlinePasses()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(7, 7);
        ledger.Record(zone);

        int writes = 0;
        float now = 0f;
        bool worthRetrying = true;
        while (worthRetrying && writes < 10)
        {
            List<Vector2s> ready = ledger.TakeReady(_ => true, now);
            if (ready.Count == 0)
            {
                now += 1f;
                continue;
            }
            Assert.Equal(new[] { zone }, ready);
            writes++;
            worthRetrying = Apply(ledger, zone, ServerBakePlanner.Action.WriteLiveCompiler,
                ServerBakePlanner.WriteReport.Failed, now);
        }

        Assert.Equal(GhostRepairLedger.MaxFailedWrites, writes);
        Assert.False(worthRetrying);
        Assert.False(ledger.Holds(zone));

        // Long past every deadline the failures ever set: still nothing.
        for (float later = now; later <= now + 1000f; later += 10f)
            Assert.Empty(ledger.TakeReady(_ => true, later));
    }

    /// <summary>
    /// A live write failing in an ordinary bake -- no ghost failure before it --
    /// used to have no ledger entry, and absence from the ledger was treated as
    /// permission to retry for ever. The first failure starts the budget.
    /// </summary>
    [Fact]
    public void AnOrdinaryFailedWriteGetsABudgetFromItsFirstFailure()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(3, 3);
        Assert.False(ledger.Holds(zone));

        Assert.True(Apply(ledger, zone, ServerBakePlanner.Action.WriteLiveCompiler,
            ServerBakePlanner.WriteReport.Failed, 0f));
        Assert.True(ledger.Holds(zone));
        Assert.Equal(1, ledger.FailedWrites(zone));

        Assert.True(Apply(ledger, zone, ServerBakePlanner.Action.WriteLiveCompiler,
            ServerBakePlanner.WriteReport.Failed, Delay));
        Assert.False(Apply(ledger, zone, ServerBakePlanner.Action.WriteLiveCompiler,
            ServerBakePlanner.WriteReport.Failed, Delay * 2));
        Assert.False(ledger.Holds(zone));
        Assert.Empty(ledger.TakeReady(_ => true, 1000f));
    }

    [Fact]
    public void TwoFailuresThenSuccessIsNotRevivedByTheClock()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(5, 5);
        ledger.Record(zone);

        Assert.True(Apply(ledger, zone, ServerBakePlanner.Action.WriteLiveCompiler,
            ServerBakePlanner.WriteReport.Failed, 0f));
        Assert.True(Apply(ledger, zone, ServerBakePlanner.Action.WriteLiveCompiler,
            ServerBakePlanner.WriteReport.Failed, Delay));

        // It works on the third go. The deadlines the failures left behind must
        // not bring the finished job back.
        Assert.True(Apply(ledger, zone, ServerBakePlanner.Action.WriteLiveCompiler,
            ServerBakePlanner.WriteReport.Written, Delay * 2));
        Assert.False(ledger.Holds(zone));
        for (float later = 0f; later <= 1000f; later += 10f)
            Assert.Empty(ledger.TakeReady(_ => true, later));
    }

    /// <summary>
    /// The production sequence, not the ledger alone: ProcessZone runs vegetation
    /// whenever terrain returns Done -- and a TERMINAL terrain failure returns
    /// Done. So on a generated, uncleared zone with a player standing near it,
    /// vegetation deferred the zone immediately after terrain gave up, and the
    /// deferral recreated the entry the give-up had just removed, with the count
    /// back at zero. Terrain then started again with a fresh three, for ever.
    /// Terrain's terminal state has to survive other work deferring the zone.
    /// </summary>
    [Fact]
    public void VegetationDeferralCannotRestartAnExhaustedTerrainBudget()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(8, 8);
        int writes = 0;
        float now = 0f;

        for (int round = 1; round <= 7; round++)
        {
            // ProcessTerrain refuses once terrain has given up -- the zone is
            // still here, but not for terrain.
            if (!ledger.HasGivenUp(zone))
            {
                writes++;
                ledger.FailedWrite(zone, now, Delay);
            }
            // ProcessVegetation: generated, uncleared, a peer nearby.
            ledger.Defer(zone, now, Delay);
            now += Delay;
        }

        Assert.Equal(GhostRepairLedger.MaxFailedWrites, writes);
        Assert.True(ledger.HasGivenUp(zone));

        // The peer leaves: the zone is handed over once more, terrain still
        // refuses, and vegetation finally clears. Something has to retire the
        // entry vegetation's own Defer created, or the zone stays pending for
        // the rest of the session and the bake never reports itself finished --
        // while terrain's give-up must survive that retirement.
        Assert.Equal(new[] { zone }, ledger.TakeReady(_ => true, now));
        Assert.True(ledger.HasGivenUp(zone));
        ledger.PendingWorkDone(zone);

        Assert.Equal(0, ledger.Count);
        Assert.True(ledger.HasGivenUp(zone));
        for (float later = now; later <= now + 1000f; later += 50f)
            Assert.Empty(ledger.TakeReady(_ => true, later));
        Assert.Equal(GhostRepairLedger.MaxFailedWrites, writes);
    }

    /// <summary>
    /// The already-cleared exit matters as much as the clearing one: a zone
    /// whose vegetation was done long ago still arrives here, and it must not
    /// leave a pending entry behind either.
    /// </summary>
    [Fact]
    public void VegetationThatWasAlreadyDoneAlsoRetiresThePendingEntry()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(11, 11);
        for (int i = 0; i < GhostRepairLedger.MaxFailedWrites; i++)
            ledger.FailedWrite(zone, i * Delay, Delay);
        ledger.Defer(zone, 0f, Delay);
        Assert.Equal(1, ledger.Count);

        ledger.PendingWorkDone(zone);

        Assert.Equal(0, ledger.Count);
        // Retiring the work is not terrain getting well again.
        Assert.True(ledger.HasGivenUp(zone));
    }

    /// <summary>
    /// Vegetation finishing must not cancel a retry TERRAIN is still owed.
    ///
    /// This is the ordinary order, not a corner: ProcessTerrain returns Done
    /// after a NONTERMINAL failed write -- a live compiler that took nothing,
    /// terrain the game never built, an ordinary failed write -- and
    /// ProcessZone runs the vegetation pass immediately afterwards, on the very
    /// entry that now carries terrain's retry. Retiring it left the zone
    /// neither held, nor given up, nor counted, so the bake reported
    /// "0 deferred, 0 pending" with the road unwritten.
    /// </summary>
    [Fact]
    public void VegetationCompletionDoesNotCancelTheFirstTerrainRetry()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(3, 4);

        Assert.False(ledger.FailedWrite(zone, 0f, Delay));
        Assert.Equal(1, ledger.Count);

        // The vegetation pass finds this zone already cleared and retires it.
        ledger.PendingWorkDone(zone);

        Assert.Equal(1, ledger.Count);
        Assert.True(ledger.Holds(zone));
        Assert.False(ledger.HasGivenUp(zone));
        Assert.Equal(1, ledger.FailedWrites(zone));
        Assert.Single(ledger.TakeReady(_ => true, Delay * 10f));
    }

    /// <summary>
    /// The same on the SECOND failure, and through the other exit -- the one
    /// where vegetation actually cleared on this pass rather than finding the
    /// work already done. A zone one attempt from its bound is exactly the one
    /// that must not lose its last try.
    /// </summary>
    [Fact]
    public void VegetationCompletionDoesNotCancelTheSecondTerrainRetry()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(5, 6);

        Assert.False(ledger.FailedWrite(zone, 0f, Delay));
        Assert.Single(ledger.TakeReady(_ => true, Delay * 2f));
        Assert.False(ledger.FailedWrite(zone, Delay * 2f, Delay));
        Assert.Equal(2, ledger.FailedWrites(zone));

        ledger.PendingWorkDone(zone);

        Assert.Equal(1, ledger.Count);
        Assert.False(ledger.HasGivenUp(zone));
        Assert.Equal(2, ledger.FailedWrites(zone));
        Assert.Single(ledger.TakeReady(_ => true, Delay * 10f));
    }

    /// <summary>
    /// And the refusal must not over-reach: an entry with no spent attempt is
    /// vegetation's own deferral, which is precisely what PendingWorkDone
    /// exists to close. Without this the bake would never report itself
    /// finished, which is the bug the method was added for.
    /// </summary>
    [Fact]
    public void VegetationsOwnDeferralIsStillRetiredWhenTerrainOwesNothing()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(7, 8);

        ledger.Defer(zone, 0f, Delay);
        Assert.Equal(1, ledger.Count);

        ledger.PendingWorkDone(zone);

        Assert.Equal(0, ledger.Count);
        Assert.False(ledger.HasGivenUp(zone));
        Assert.Empty(ledger.TakeReady(_ => true, Delay * 10f));
    }

    [Fact]
    public void ANewNetworkOrAnOutrightResolutionLiftsTheGiveUp()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(9, 9);
        for (int i = 0; i < GhostRepairLedger.MaxFailedWrites; i++)
            ledger.FailedWrite(zone, 0f, Delay);
        Assert.True(ledger.HasGivenUp(zone));

        // road_bake again / a new network version.
        ledger.Clear();
        Assert.False(ledger.HasGivenUp(zone));

        // And an explicit resolution does the same for one zone.
        for (int i = 0; i < GhostRepairLedger.MaxFailedWrites; i++)
            ledger.FailedWrite(zone, 0f, Delay);
        Assert.True(ledger.HasGivenUp(zone));
        ledger.Succeeded(zone);
        Assert.False(ledger.HasGivenUp(zone));
    }

    [Fact]
    public void ADuplicateCompilerRepairIsEndedRatherThanLeftQueued()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(6, 6);
        ledger.Record(zone);
        ledger.TakeReady(_ => true, 0f);

        Assert.False(Apply(ledger, zone, ServerBakePlanner.Action.DuplicateCompilers,
            ServerBakePlanner.WriteReport.NotAttempted, 0f));
        Assert.False(ledger.Holds(zone));
        Assert.Empty(ledger.TakeReady(_ => true, 1000f));
    }

    // ---- what the queue owes a zone it is repairing ----
    //
    // The ledger alone was not enough: the queue has paths that end without
    // writing anything, and a repair that leaves one of them unanswered stays
    // marked as queued for ever -- never handed over again, never given up.

    private static ServerBakePlanner.RepairOutcome Outcome(ServerBakePlanner.Action action,
        ServerBakePlanner.WriteReport report = ServerBakePlanner.WriteReport.NotAttempted) =>
        ServerBakePlanner.ResolveRepair(action, report);

    /// <summary>A write that failed (the game would not save) spends the zone's budget and is given up at the last.</summary>
    [Fact]
    public void AWriteThatFailedSpendsTheBudgetAndIsGivenUp()
    {
        var ledger = new GhostRepairLedger();
        float now = 0f;
        // A write that failed (the game would not save) spends the budget, as before.
        ServerBakePlanner.WriteReport failed = ServerBakePlanner.ReportOf(false, RoadTerrainModifier.WriteOutcome.SaveRefused);
        Assert.Equal(ServerBakePlanner.WriteReport.Failed, failed);
        Assert.Equal(ServerBakePlanner.WriteReport.Failed, ServerBakePlanner.ReportOf(false, RoadTerrainModifier.WriteOutcome.None));
        var broken = new Vector2s(4, -2);
        ledger.Record(broken);
        bool kept = true;
        for (int i = 0; i < GhostRepairLedger.MaxFailedWrites; i++, now += Delay)
        {
            Assert.Contains(broken, ledger.TakeReady(_ => true, now));
            kept = Apply(ledger, broken, ServerBakePlanner.Action.WriteSavedCompiler, failed, now);
        }
        Assert.False(kept);
        Assert.True(ledger.HasGivenUp(broken));
    }

    [Fact]
    public void WaitingForAPrerequisiteCostsTheZoneNothing()
    {
        // An owner standing in the zone, a compiler not up yet, terrain the
        // game has not built: all of them mean "come back", not "that failed".
        Assert.Equal(ServerBakePlanner.RepairOutcome.Waiting, Outcome(ServerBakePlanner.Action.WaitForOwner));
        Assert.Equal(ServerBakePlanner.RepairOutcome.Waiting, Outcome(ServerBakePlanner.Action.WriteLiveCompiler));
        Assert.Equal(ServerBakePlanner.RepairOutcome.Waiting, Outcome(ServerBakePlanner.Action.WriteSavedCompiler));
    }

    [Fact]
    public void AFailedWriteIsAFailedWriteWhicheverCompilerItWas()
    {
        Assert.Equal(ServerBakePlanner.RepairOutcome.FailedWrite,
            Outcome(ServerBakePlanner.Action.WriteLiveCompiler, ServerBakePlanner.WriteReport.Failed));
        Assert.Equal(ServerBakePlanner.RepairOutcome.FailedWrite,
            Outcome(ServerBakePlanner.Action.WriteSavedCompiler, ServerBakePlanner.WriteReport.Failed));
        Assert.Equal(ServerBakePlanner.RepairOutcome.FailedWrite,
            Outcome(ServerBakePlanner.Action.CreateCompiler, ServerBakePlanner.WriteReport.Failed));
    }

    [Fact]
    public void AWriteOrANoOpEndsTheRepairAndSoDoesWorkThatIsNotOurs()
    {
        Assert.Equal(ServerBakePlanner.RepairOutcome.Resolved,
            Outcome(ServerBakePlanner.Action.WriteLiveCompiler, ServerBakePlanner.WriteReport.Written));
        Assert.Equal(ServerBakePlanner.RepairOutcome.Resolved,
            Outcome(ServerBakePlanner.Action.WriteLiveCompiler, ServerBakePlanner.WriteReport.NothingToWrite));
        // Handed to the generation or live-zone hooks, or already carrying the
        // roads: the repair queue has nothing left to do about it.
        Assert.Equal(ServerBakePlanner.RepairOutcome.Resolved, Outcome(ServerBakePlanner.Action.LeaveToGeneration));
        Assert.Equal(ServerBakePlanner.RepairOutcome.Resolved, Outcome(ServerBakePlanner.Action.LeaveToLiveZone));
        Assert.Equal(ServerBakePlanner.RepairOutcome.Resolved, Outcome(ServerBakePlanner.Action.AlreadyCurrent));
        Assert.Equal(ServerBakePlanner.RepairOutcome.Resolved, Outcome(ServerBakePlanner.Action.NoNetwork));
    }

    /// <summary>
    /// An owner that holds the compiler far longer than the wait: every round
    /// defers the zone again, none of them spends a write attempt, and the
    /// queue is never handed a second copy. (The three-failure sequence has its
    /// own fake-clock regression above.)
    /// </summary>
    [Fact]
    public void AnOwnerHeldLongerThanTheWaitSpendsNothing()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(7, 7);
        ledger.Record(zone);

        // An owner keeps hold of the compiler for far longer than the wait.
        // Every round defers it again; none of them costs an attempt, and the
        // queue is never given a second copy of the zone.
        float now = 0f;
        int handedOver = 0;
        for (int round = 0; round < 5; round++)
        {
            handedOver += ledger.TakeReady(_ => true, now).Count;
            Assert.True(Apply(ledger, zone, ServerBakePlanner.Action.WaitForOwner,
                ServerBakePlanner.WriteReport.NotAttempted, now));
            Assert.Equal(0, ledger.FailedWrites(zone));
            now += Delay * 1.5f;
        }
        Assert.Equal(5, handedOver);
        Assert.True(ledger.Holds(zone));

        // The owner finally leaves and the write lands: nothing is left behind.
        ledger.TakeReady(_ => true, now);
        Assert.True(Apply(ledger, zone, ServerBakePlanner.Action.WriteLiveCompiler,
            ServerBakePlanner.WriteReport.Written, now));
        Assert.False(ledger.Holds(zone));
    }
}
