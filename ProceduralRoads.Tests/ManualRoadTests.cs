using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public class ManualRoadTests : IDisposable
{
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0; width = 0; }
    }
    private readonly WorldGenerator world = new FlatWorld();
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithNetwork(server: true);
    public ManualRoadTests()
    {
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld();
        _scope.WithWorld(world).WithZdos().WithZoneSystem(); RoadNetworkGenerator.MarkLocationsReady();
        Heightmap.Registered = null; RoadGrade.Configured = RoadConstants.DefaultMaxRoadGrade;
        RoadNetworkGenerator.RoadWidth = RoadConstants.DefaultRoadWidth;
        RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
    }
    public void Dispose()
    {
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld();
        _scope.Dispose();
    }
    private static Island Group(int xmin = -64, int xmax = 64)
    {
        var i = new Island { Id = 1, CellSize = 8, WorldOffset = 0,
            Min = new Vector2(xmin * 8, -512), Max = new Vector2(xmax * 8, 512) };
        for (int x = xmin; x <= xmax; x++) for (int z = -64; z <= 64; z++) i.Cells.Add(new Vector2Int(x, z));
        return i;
    }
    private RoadSpatialGrid.PlannedPath Line(float z, float start = -24, float end = 24)
    {
        var plan = RoadSpatialGrid.PlanRoadPath(new List<Vector2> { new(start, z), new(end, z) }, 4, world);
        Assert.NotNull(plan); return plan!;
    }
    private int BaseRoad(float z = -16)
    {
        RoadSpatialGrid.Commit(Line(z)); RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkGenerator.MarkRoadsLoadedFromZDO(); return RoadSpatialGrid.RoadNetworkVersion;
    }
    private void Add(float z) => RoadSpatialGrid.CommitAppend(new[] { Line(z) }, RoadSpatialGrid.RoadNetworkVersion);

    /// <summary>A join aims at no point on a staircase: there is no road on it to join.</summary>
    [Fact] public void AJoinNeverAimsAtAPointOnAStaircase()
    {
        BaseRoad();
        StairStructures.Replace(new List<StairRun> { new StairRun { From = new Vector2(-8f, -16f), To = new Vector2(8f, -16f), Width = 4f } });
        Assert.Contains(RoadSpatialGrid.RoadTargets(_ => true), p => Math.Abs(p.x) < 4f);
        var targets = RoadNetworkGenerator.JoinTargets(_ => true);
        Assert.DoesNotContain(targets, p => Math.Abs(p.x) < 8f);
        Assert.Contains(targets, p => Math.Abs(p.x) > 12f);
    }

    [Fact] public void CoordinatesUseInvariantXZAndCanBeCopiedBack()
    {
        using var overrides = StaticOverride.Keep(() => CultureInfo.CurrentCulture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        var d = new ManualRoadDraft(); d.Add(ManualRoadDraft.ParsePoint("-120.5", "450.25")); d.Add(new Vector2(0, -80));
        Assert.Equal("road_path -120.5,450.25 0,-80", d.Command);
        Assert.Equal(d.Points, ManualRoadDraft.ParsePath(d.Command.Split(' ').Skip(1)));
    }
    /// <summary>A command given a point as its first two words (road_regen_island, road_bridge_repairs) reads it as every typed
    /// coordinate is read (ManualRoadDraft.ParsePoint). Read in the current culture, "120.5" on a de-DE system was 1205 ('.' is
    /// its group separator), and NaN or a point outside the world went through.</summary>
    [Fact] public void ACommandsTypedPointIsReadAsEveryTypedCoordinate()
    {
        using var overrides = StaticOverride.Keep(() => CultureInfo.CurrentCulture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        var console = new Terminal();
        Vector2? Typed(string line, out bool ok)
        {
            ok = ManualRoadCommands.TryTypedPoint(new Terminal.ConsoleEventArgs(line, console), "Usage: road_regen_island <x> <z>", out Vector2? point);
            return point;
        }
        Assert.Equal(new Vector2(120.5f, -40.25f), Typed("road_regen_island 120.5 -40.25", out bool ok));
        Assert.True(ok);
        Assert.Equal(new Vector2(120.5f, -40.25f), Typed("road_regen_island 120.5 -40.25 force", out ok));
        Assert.True(ok);
        // No point given: the command acts where the player stands.
        Assert.Null(Typed("road_regen_island", out ok)); Assert.True(ok);
        Assert.Null(Typed("road_regen_island force", out ok)); Assert.True(ok);
        foreach (string line in new[] { "road_regen_island NaN 0", "road_regen_island 20000 0", "road_regen_island 120,5 0" })
        {
            int from = console.Output.Count;
            Assert.Null(Typed(line, out ok));
            Assert.False(ok);
            Assert.Equal(new[] { "Usage: road_regen_island <x> <z>: Coordinates must be finite X,Z values inside the playable world." },
                console.Output.Skip(from));
        }
    }
    [Theory]
    [InlineData("NaN", "0")][InlineData("Infinity", "2")][InlineData("10000", "0")][InlineData("2,3", "4")][InlineData("a", "b")]
    public void InvalidCoordinatesAreRefused(string x, string z) => Assert.Throws<ArgumentException>(() => ManualRoadDraft.ParsePoint(x,z));
    [Fact] public void DraftLimitsDuplicatesAndClearAreLocal()
    {
        var a = ManualRoads.Draft("a"); var b = ManualRoads.Draft("b");
        a.Add(new Vector2(10,20)); b.Add(new Vector2(50,60));
        Assert.Throws<ArgumentException>(() => a.Add(new Vector2(10.1f,20)));
        Assert.True(a.Undo()); Assert.False(a.Undo());
        for (int i=0;i<32;i++) a.Add(new Vector2(i*2,0));
        Assert.Throws<ArgumentException>(()=>a.Add(new Vector2(100,0)));
        a.Clear(); Assert.Single(b.Points);
        ManualRoads.Reset(); Assert.Empty(ManualRoads.Draft("b").Points);
    }
    [Theory]
    [InlineData("1,2")][InlineData("1,2 1,2")][InlineData("1,2,3 4,5")]
    public void BadPathsFailBeforePlanning(string path) => Assert.Throws<ArgumentException>(()=>ManualRoadDraft.ParsePath(path.Split(' ')));

    [Fact] public void AppendKeepsOldRecordsAndPersistsHistoryInTheSameBlob()
    {
        int original = BaseRoad(); var before = RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(0,0)).Where(p=>p.addition==0).ToArray();
        Add(16); int appended = RoadSpatialGrid.RoadNetworkVersion;
        var bytes = RoadSpatialGrid.SerializeAllRoadPoints()!;
        Assert.Equal(4, BitConverter.ToInt32(bytes,0)); Assert.Equal(8, BitConverter.ToInt32(bytes,4));
        RoadSpatialGrid.Clear(); Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(bytes));
        Assert.Equal(appended,RoadSpatialGrid.RoadNetworkVersion);
        var all = RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(0,0));
        Assert.Equal(before,all.Where(p=>p.addition==0));
        Assert.All(RoadSpatialGrid.PendingPoints(all,original),p=>Assert.Equal(1,p.addition));
        Assert.NotEmpty(RoadSpatialGrid.PendingPoints(all,original));
        Assert.Empty(RoadSpatialGrid.PendingPoints(all,appended));
    }
    [Fact] public void TwoOfflineAppendsOnlyApplyWhatEachCompilerMissed()
    {
        int a=BaseRoad(); Add(0); int b=RoadSpatialGrid.RoadNetworkVersion; Add(16);
        var all=RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(0,0));
        Assert.Equal(new[]{1,2},RoadSpatialGrid.PendingPoints(all,a).Select(p=>p.addition).Distinct().OrderBy(x=>x));
        Assert.All(RoadSpatialGrid.PendingPoints(all,b),p=>Assert.Equal(2,p.addition));
        Assert.Equal(all,RoadSpatialGrid.PendingPoints(all,0));
        Assert.Equal(all,RoadSpatialGrid.PendingPoints(all,123456));
    }
    [Fact] public void OrdinaryNetworksSaveInTheOneCurrentFormat()
    {
        BaseRoad(); var before=RoadSpatialGrid.SerializeAllRoadPoints()!;
        Assert.Equal(4,BitConverter.ToInt32(before,0)); Assert.Equal(8,BitConverter.ToInt32(before,4));
        RoadSpatialGrid.Clear(); Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(before));
        Assert.Equal(before,RoadSpatialGrid.SerializeAllRoadPoints());
    }
    [Fact] public void TruncatedAppendHistoryCannotReplaceTheCurrentNetwork()
    {
        BaseRoad(); Add(16); var before=RoadSpatialGrid.SerializeAllRoadPoints()!;
        Assert.False(RoadSpatialGrid.DeserializeAllRoadPoints(before.Take(before.Length-1).ToArray()));
        Assert.Equal(before,RoadSpatialGrid.SerializeAllRoadPoints());
    }
    /// <summary>A zone no road point reaches owes no road write: its terrain is settled with no compiler at all (a
    /// structure-only zone, a bridge's mid-span), so the rock pass and the carve's fragment ground alike may judge it
    /// (RoadRockClearing.TerrainWritten); a road's zone waits for its compiler and its write.</summary>
    [Fact] public void AZoneNoRoadReachesIsSettledWithoutACompilerAndARoadZoneWaitsForItsWrite()
    {
        BaseRoad(); var road=new Vector2s(0,0); var roadless=new Vector2s(5,5);
        Assert.Empty(RoadSpatialGrid.GetRoadPointsInZone(roadless));
        Assert.True(RoadTerrainModifier.TerrainSettled(ZoneSystem.GetZonePos(roadless)));
        Assert.False(RoadTerrainModifier.TerrainSettled(ZoneSystem.GetZonePos(road)));
        var hm=_scope.RegisterHeightmap(road); RoadTerrainModifier.OnTerrainCompilerReady(hm.m_terrainComp!);
        Assert.True(RoadTerrainModifier.CarriesCurrentRoads(hm.m_terrainComp!));
        Assert.True(RoadTerrainModifier.TerrainSettled(ZoneSystem.GetZonePos(road)));
    }
    [Fact] public void UnchangedZoneIsNotRewrittenAfterAnAppendElsewhere()
    {
        BaseRoad(); var hm=_scope.RegisterHeightmap(new Vector2s(0,0));
        RoadTerrainModifier.OnTerrainCompilerReady(hm.m_terrainComp!);
        int saves=hm.m_terrainComp!.SaveCount;
        Add(200); RoadTerrainModifier.OnTerrainCompilerReady(hm.m_terrainComp);
        Assert.Equal(saves,hm.m_terrainComp.SaveCount); Assert.True(RoadTerrainModifier.CarriesCurrentRoads(hm.m_terrainComp));
    }
    [Fact] public void AZoneStaysClearedAfterAnAppendElsewhereAndIsOwedOnceOneReachesIt()
    {
        int version=BaseRoad(); var zone=new Vector2s(0,0);
        VegetationClearing.MarkCleared(zone,version);
        Add(200);
        Assert.True(VegetationClearing.IsCleared(zone,RoadSpatialGrid.RoadNetworkVersion));
        Add(16);
        Assert.False(VegetationClearing.IsCleared(zone,RoadSpatialGrid.RoadNetworkVersion));
        // It owes the clearing of the roads appended since it was cleared, not the older roads' again.
        int since=VegetationClearing.OwedSince(zone);
        Assert.InRange(since,0,RoadSpatialGrid.AppendCount-1);
        Assert.Contains(RoadSpatialGrid.GetRoadPointsInZone(zone),q=>q.addition>since);
        Assert.Contains(RoadSpatialGrid.GetRoadPointsInZone(zone),q=>q.addition<=since);
        VegetationClearing.MarkCleared(zone,RoadSpatialGrid.RoadNetworkVersion);
        Assert.Equal(-1,VegetationClearing.OwedSince(zone));
    }
    /// <summary>What a zone owes after an append reached it (the roads appended since it was cleared) is saved with the
    /// cleared zones, so a reload before the zone is cleared does not clear round its older roads again.</summary>
    [Fact] public void AZoneOwedOnlyTheAppendedRoadsOwesOnlyThemAfterAReload()
    {
        int version=BaseRoad(); var zone=new Vector2s(0,0);
        VegetationClearing.MarkCleared(zone,version);
        Add(200); Add(16);
        int since=VegetationClearing.OwedSince(zone);
        Assert.InRange(since,0,RoadSpatialGrid.AppendCount-1);
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadNetworkPersistence.SetSavedRecord("ProceduralRoads_GlobalData",RoadSpatialGrid.SerializeAllRoadPoints()!);
        RoadNetworkGenerator.SaveClearedZones();
        RoadNetworkGenerator.Reset(); Assert.Equal(-1,VegetationClearing.OwedSince(zone));
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData());
        Assert.Equal(since,VegetationClearing.OwedSince(zone));
        Assert.False(VegetationClearing.IsCleared(zone,RoadSpatialGrid.RoadNetworkVersion));
    }
    /// <summary>Two appends into a cleared zone before any bake or save: it owes both from the first, as soon as each is
    /// published, through a save and reload; and a clearing done on what was read before the second append certifies the
    /// first only.</summary>
    [Fact] public void TwoAppendsIntoAClearedZoneStayOwedThroughAReloadAndAnEarlierClearingCertifiesOnlyTheFirst()
    {
        int version=BaseRoad(); var zone=new Vector2s(0,0);
        VegetationClearing.MarkCleared(zone,version);
        Add(16); int first=RoadSpatialGrid.RoadNetworkVersion;
        Assert.Equal(0,VegetationClearing.OwedSince(zone));
        Add(8);
        Assert.Equal(0,VegetationClearing.OwedSince(zone));
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadNetworkPersistence.SetSavedRecord("ProceduralRoads_GlobalData",RoadSpatialGrid.SerializeAllRoadPoints()!);
        RoadNetworkGenerator.SaveClearedZones();
        RoadNetworkGenerator.Reset(); Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData());
        Assert.Equal(0,VegetationClearing.OwedSince(zone));
        Assert.False(VegetationClearing.IsCleared(zone,RoadSpatialGrid.RoadNetworkVersion));
        VegetationClearing.MarkCleared(zone,first);
        Assert.Equal(1,VegetationClearing.OwedSince(zone));
        Assert.False(VegetationClearing.IsCleared(zone,RoadSpatialGrid.RoadNetworkVersion));
        VegetationClearing.MarkCleared(zone,RoadSpatialGrid.RoadNetworkVersion);
        Assert.Equal(-1,VegetationClearing.OwedSince(zone));
        Assert.True(VegetationClearing.IsCleared(zone,RoadSpatialGrid.RoadNetworkVersion));
    }
    /// <summary>A zone whose clearing is unknown (the saved record could not be read) is not cleared again for the roads it
    /// had, but a road appended into it later is cleared there, its own roads only.</summary>
    [Fact] public void AZoneWithAnUnknownClearingIsOwedTheRoadsAppendedIntoItOnly()
    {
        BaseRoad(); var zone=new Vector2s(0,0); var elsewhere=new Vector2s(5,5);
        ZoneSystem.instance!.Generated.Add(zone); ZoneSystem.instance.Generated.Add(elsewhere);
        int appends=RoadSpatialGrid.AppendCount;
        VegetationClearing.HistoryUnread();
        Assert.True(VegetationClearing.IsCleared(zone,RoadSpatialGrid.RoadNetworkVersion));
        Add(200);
        Assert.True(VegetationClearing.IsCleared(zone,RoadSpatialGrid.RoadNetworkVersion));
        Add(16);
        Assert.False(VegetationClearing.IsCleared(zone,RoadSpatialGrid.RoadNetworkVersion));
        Assert.True(VegetationClearing.IsCleared(elsewhere,RoadSpatialGrid.RoadNetworkVersion));
        Assert.Equal(appends,VegetationClearing.OwedSince(zone));
        Assert.Contains(RoadSpatialGrid.GetRoadPointsInZone(zone),q=>q.addition>appends);
        Assert.Contains(RoadSpatialGrid.GetRoadPointsInZone(zone),q=>q.addition<=appends);
        VegetationClearing.MarkCleared(zone,RoadSpatialGrid.RoadNetworkVersion);
        Assert.True(VegetationClearing.IsCleared(zone,RoadSpatialGrid.RoadNetworkVersion));
        Assert.Equal(-1,VegetationClearing.OwedSince(zone));
    }

    /// <summary>A zone with no road of its own whose trees or logs a road reaches (RoadClearAreaManager.GetOrCreateRoadCentres
    /// gathers points VegetationClearing.TreeReach past it) is owed clearing as the road's own zones are: the whole network's
    /// zone set holds it (RoadClearAreaManager.ZonesReached, zone for zone as the road centres gather), and an append whose
    /// road comes that near a cleared one owes it again (RoadSpatialGrid.ZonesChangedSince, ZoneChangedSince, the clearing
    /// record). Before, only the zones a road writes terrain into were, so a tree next door with its trunk over the road was
    /// never judged by the bake.</summary>
    [Fact] public void AZoneOnlyTheRoadsTreeReachTakesInIsOwedItsClearing()
    {
        // The road centres are cached per zone for the world: none left from another network.
        RoadClearAreaManager.ClearCache();
        int version=BaseRoad();
        // The road at z -16: zone (0,-1) (centre z -64) holds none of its terrain, but its trees reach it.
        var below=new Vector2s(0,-1);
        Assert.Empty(RoadSpatialGrid.GetRoadPointsInZone(below));
        Assert.NotEmpty(RoadClearAreaManager.GetOrCreateRoadCentres(below));
        HashSet<Vector2s> reached=RoadClearAreaManager.ZonesReached();
        Assert.Contains(below,reached);
        for(int x=-6;x<=6;x++) for(int y=-4;y<=4;y++)
        {
            var zone=new Vector2s(x,y);
            Assert.Equal(RoadClearAreaManager.RoadCentresSince(zone,-1).Count>0,reached.Contains(zone));
        }
        // A cleared zone (0,1) (centre z 64): a road appended at z 110 writes none of its terrain, but its trees reach that road.
        var above=new Vector2s(0,1);
        ZoneSystem.instance!.Generated.Add(above);
        VegetationClearing.MarkCleared(above,RoadSpatialGrid.RoadNetworkVersion);
        Assert.True(VegetationClearing.IsCleared(above,RoadSpatialGrid.RoadNetworkVersion));
        int appends=RoadSpatialGrid.AppendCount;
        Add(110);
        Assert.DoesNotContain(RoadSpatialGrid.GetRoadPointsInZone(above),q=>q.addition>appends);
        Assert.Contains(above,RoadSpatialGrid.ZonesChangedSince(version)!);
        Assert.True(RoadSpatialGrid.ZoneChangedSince(above,version));
        Assert.False(VegetationClearing.IsCleared(above,RoadSpatialGrid.RoadNetworkVersion));
        Assert.Equal(appends,VegetationClearing.OwedSince(above));
        Assert.NotEmpty(RoadClearAreaManager.RoadCentresSince(above,appends).Where(c=>float.IsNaN(c.Bottom)));
    }
    [Fact] public void AnAppendOwesOnlyTheZonesItReachesAndAnotherNetworkOwesAll()
    {
        int version=BaseRoad(); Add(200);
        var owed=RoadSpatialGrid.ZonesChangedSince(version)!;
        Assert.Contains(new Vector2s(0,3),owed); Assert.DoesNotContain(new Vector2s(0,0),owed);
        Assert.Empty(RoadSpatialGrid.ZonesChangedSince(RoadSpatialGrid.RoadNetworkVersion)!);
        Assert.Null(RoadSpatialGrid.ZonesChangedSince(version+1));
    }
    [Fact] public void SharedZoneKeepsPlayerEditsOnOldRoadAndOffTheAdditionAfterReload()
    {
        int original=BaseRoad(); var zone=new Vector2s(0,0);
        var hm=_scope.RegisterHeightmap(zone); var tc=hm.m_terrainComp!;
        RoadTerrainModifier.OnTerrainCompilerReady(tc);
        int oldRoad=16*65+32, untouched=32*65+32, newRoad=48*65+32;
        tc.m_levelDelta[oldRoad]=3.25f; tc.m_smoothDelta[oldRoad]=.75f;
        tc.m_levelDelta[untouched]=-2.5f; tc.m_smoothDelta[untouched]=.5f;
        tc.m_levelDelta[newRoad]=-3f;
        Add(16); var bytes=RoadSpatialGrid.SerializeAllRoadPoints()!;
        RoadSpatialGrid.Clear(); Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(bytes));
        RoadTerrainModifier.OnTerrainCompilerReady(tc);
        Assert.Equal(3.25f,tc.m_levelDelta[oldRoad]); Assert.Equal(.75f,tc.m_smoothDelta[oldRoad]);
        Assert.Equal(-2.5f,tc.m_levelDelta[untouched]); Assert.Equal(.5f,tc.m_smoothDelta[untouched]);
        Assert.Equal(0,tc.m_levelDelta[newRoad],3);
        int saves=tc.SaveCount; RoadTerrainModifier.OnTerrainCompilerReady(tc); Assert.Equal(saves,tc.SaveCount);
        Assert.True(RoadTerrainModifier.CarriesCurrentRoads(tc));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ServerBakePreservesOldRoadEditsWhenAnAdditionIsInThisZoneOrElsewhere(bool elsewhere)
    {
        BaseRoad(); var zone = new Vector2s(0, 0);
        var hm = _scope.RegisterHeightmap(zone); var tc = hm.m_terrainComp!;
        RoadTerrainModifier.OnTerrainCompilerReady(tc);
        int oldRoad = 16 * 65 + 32, newRoad = 48 * 65 + 32;
        tc.m_levelDelta[oldRoad] = 3.25f; tc.m_smoothDelta[oldRoad] = .75f;
        tc.m_levelDelta[newRoad] = -3f;
        int saves = tc.SaveCount;
        Add(elsewhere ? 200 : 16);
        int applied = tc.m_nview.GetZDO().GetInt(RoadTerrainModifier.AppliedVersionHash);
        var saved = new[] { new ServerBakePlanner.Compiler(applied, 999, true,
            RoadTerrainModifier.CarriesCurrentRoads(zone, applied)) };
        Assert.Equal(elsewhere ? ServerBakePlanner.Action.AlreadyCurrent : ServerBakePlanner.Action.WaitForOwner,
            ServerBakePlanner.Decide(RoadSpatialGrid.RoadNetworkVersion, true, false, false, saved, 1));
        hm.AutoRebuild = false;
        RoadTerrainModifier.BakeRoadTerrain(zone, hm, tc);
        Assert.Equal(3.25f, tc.m_levelDelta[oldRoad]);
        Assert.Equal(.75f, tc.m_smoothDelta[oldRoad]);
        Assert.Equal(elsewhere ? -3f : 0f, tc.m_levelDelta[newRoad], 3);
        Assert.Equal(saves + (elsewhere ? 0 : 1), tc.SaveCount);
        Assert.True(RoadTerrainModifier.CarriesCurrentRoads(tc));
        RoadTerrainModifier.BakeRoadTerrain(zone, hm, tc);
        Assert.Equal(saves + (elsewhere ? 0 : 1), tc.SaveCount);
    }

    /// <summary>An appended road written into a zone that already carries the old one must leave
    /// the same heights as a zone written with both at once: written from the new points alone, two
    /// zones disagreed along their shared edge by 0.43 m, a tear.</summary>
    [Fact] public void AnAppendedRoadIsWrittenAsAFullWriteWouldWhereItReaches()
    {
        BaseRoad(-16f); var zone = new Vector2s(0, 0);
        var hm = Heightmap.CreateForZone(zone); Heightmap.Registered = hm; var tc = hm.m_terrainComp!;
        RoadTerrainModifier.OnTerrainCompilerReady(tc);
        // A second road 6 m away, close enough that the two blend.
        var raised = Line(-10f); for (int i = 0; i < raised.Heights.Count; i++) raised.Heights[i] += 1.5f;
        RoadSpatialGrid.CommitAppend(new[] { raised }, RoadSpatialGrid.RoadNetworkVersion);
        RoadTerrainModifier.OnTerrainCompilerReady(tc);

        var fresh = Heightmap.CreateForZone(zone); Heightmap.Registered = fresh; var full = fresh.m_terrainComp!;
        RoadTerrainModifier.OnTerrainCompilerReady(full);

        int compared = 0;
        for (int i = 0; i < tc.m_levelDelta.Length; i++)
        {
            if (!tc.m_modifiedHeight[i] && !full.m_modifiedHeight[i]) continue;
            compared++;
            Assert.True(Mathf.Abs(tc.m_levelDelta[i] - full.m_levelDelta[i]) < 0.01f,
                $"vertex {i}: appended {tc.m_levelDelta[i]:F3}, full {full.m_levelDelta[i]:F3}");
        }
        Assert.True(compared > 0);
    }

    [Fact] public void BlendBoundaryIncludesTheNeighbouringZone()
    {
        int old=BaseRoad(); Add(32); // Zone edge, road influences both sides.
        Assert.NotEmpty(RoadSpatialGrid.PendingPoints(RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(0,0)),old));
        Assert.NotEmpty(RoadSpatialGrid.PendingPoints(RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(0,1)),old));
    }
    [Fact] public void StalePlanCannotPublishAndDraftSurvives()
    {
        BaseRoad(); var draft=ManualRoads.Draft("host");draft.Add(new Vector2(0,40));draft.Add(new Vector2(80,40));
        var plan=ManualRoads.Prepare(draft.Points,false,new[]{Group()}); Add(16);
        var before=RoadSpatialGrid.SerializeAllRoadPoints();
        Assert.Throws<InvalidOperationException>(()=>ManualRoads.Commit(plan));
        Assert.Equal(before,RoadSpatialGrid.SerializeAllRoadPoints()); Assert.Equal(2,draft.Points.Count); Assert.False(ManualRoads.Busy);
    }
    [Fact] public void WorldUnloadInvalidatesAPlanEvenIfTheNetworkHashIsTheSame()
    {
        BaseRoad(); var plan=ManualRoads.Prepare(new[]{new Vector2(0,40),new Vector2(80,40)},false,new[]{Group()});
        ManualRoads.Reset(); Assert.Throws<InvalidOperationException>(()=>ManualRoads.Commit(plan));
    }
    [Fact] public void OrderedWaypointsArePlannedAsOneContinuousProfile()
    {
        var points=new[]{new Vector2(-80,0),new Vector2(0,40),new Vector2(80,0)};
        var plan=ManualRoads.Prepare(points,false,new[]{Group()});
        Assert.Single(plan.Paths); Assert.False(RoadSpatialGrid.IsInitialized);
        foreach(var p in points) Assert.Contains(plan.Paths[0].Points,q=>Vector2.Distance(p,q)<8);
        Assert.True(RoadGrade.SteepestStep(plan.Paths[0].Points,plan.Paths[0].Heights)<=.351f);
        ManualRoads.Commit(plan);
        Assert.True(RoadNetworkGenerator.NetworkDirty); Assert.Equal(1,RoadSpatialGrid.AppendCount);
        RoadNetworkGenerator.SaveGlobalRoadData(); var bytes=RoadSpatialGrid.SerializeAllRoadPoints();
        RoadNetworkGenerator.Reset(); Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData()); Assert.Equal(bytes,RoadSpatialGrid.SerializeAllRoadPoints());
    }
    /// <summary>A manual road is stored by the generated road's own loop: each point at its own width (a switchback's
    /// turns), and a point inside the footprint of the location it was planned to into that location.</summary>
    [Fact] public void AManualRoadKeepsItsPointWidthsAndIsStoredIntoAFootprintAsAGeneratedRoadIs()
    {
        BaseRoad();
        RoadLocationProtection.Set(new[]{new RoadLocationProtection.Footprint(new Vector2(100,40),6)});
        // A location on the preload list (RoadLocationProtection.Enterable): only such a location is stored into.
        PlacedLocations.Preload(new Vector2(100,40));
        var points=new List<Vector2>{new(88,40),new(94,40),new(100,40),new(106,40),new(112,40)};
        var plan=new RoadSpatialGrid.PlannedPath(points,new List<float>{40,40,40,40,40},new List<RoadPointDebugInfo>(),4,24,false)
            {Widths=new List<float>{4,4,7,4,4},Locations=new RoadLocationProtection.Targets(null,new Vector2(100,40))};
        RoadSpatialGrid.CommitAppend(new[]{plan},RoadSpatialGrid.RoadNetworkVersion);
        var stored=RoadSpatialGrid.SnapshotAllRoadPoints().Where(p=>p.addition==1).ToList();
        Assert.Contains(stored,p=>p.p==new Vector2(100,40)&&p.w==7f&&p.intoLocation&&p.location==new Vector2(100,40));
        Assert.Contains(stored,p=>p.p==new Vector2(88,40)&&p.w==4f&&!p.intoLocation);
        PlacedLocations.Reset();
    }
    /// <summary>The zones a manual road writes are read off the append it commits: its cuts and the other roads' points it
    /// raises are in them, not only its own points.</summary>
    [Fact] public void TheZonesAManualRoadWritesIncludeItsCutsAndTheRoadsItRaises()
    {
        var append=new RoadNetworkGenerator.ManualAppend();
        append.Ground.Raised.Add((new RoadSpatialGrid.RoadPoint(new Vector2(500,500),4,40),41f));
        append.Ground.Cuts.Add(new Vector3(-500,30,-500));
        append.StairCuts.Add(new Vector3(500,30,-500));
        append.WithFills.Add(Line(0));
        var zones=ManualRoads.ZonesReached(append,new List<RoadCrossing>());
        foreach(var at in new[]{new Vector3(500,0,500),new Vector3(-500,0,-500),new Vector3(500,0,-500),new Vector3(0,0,0)})
            Assert.Contains(ZoneSystem.GetZone(at),zones);
    }
    /// <summary>A manual road's stair cut writes the zones that gather it and no others (RoadTerrainModifier.StairCutReach and the
    /// pad the zone gather keeps, RoadSpatialGrid.GetRoadPointsInZone): a cut 4.7 m inside a zone is gathered by the zone next to
    /// it, so that zone is written; one 8 m inside is not, where the road-width reach (ZoneReach, over 10 m) marked it.</summary>
    [Fact] public void AStairCutWritesTheZonesThatGatherIt()
    {
        float reach=RoadTerrainModifier.StairCutReach+RoadTerrainModifier.CutGatherPad;
        Assert.Equal(reach,RoadTerrainModifier.CutReach(StairLayout.CutWidth)+RoadTerrainModifier.CutGatherPad);
        Vector2s Zone(float x)=>ZoneSystem.GetZone(new Vector3(x,0f,0f));
        HashSet<Vector2s> Written(float x)
        {
            var append=new RoadNetworkGenerator.ManualAppend();
            append.StairCuts.Add(new Vector3(x,30f,0f));
            return ManualRoads.ZonesReached(append,new List<RoadCrossing>());
        }
        float near=32f+reach-0.05f, far=32f+8f;
        Assert.Contains(Zone(near),Written(near));
        Assert.Contains(Zone(near-reach),Written(near));
        Assert.NotEqual(Zone(near),Zone(near-reach));
        Assert.Contains(Zone(far),Written(far));
        Assert.DoesNotContain(Zone(31f),Written(far));
        Assert.True(RoadSpatialGrid.ZoneReach(StairLayout.CutWidth)>8f,"the road-width reach no longer exceeds the cut's");
    }
    [Fact] public void ProtectedWaypointIsRefusedWithoutExemptingItsOwnFootprint()
    {
        RoadLocationProtection.Set(new[]{new RoadLocationProtection.Footprint(new Vector2(0,0),20)});
        Assert.Throws<ArgumentException>(()=>ManualRoads.Prepare(new[]{new Vector2(0,0),new Vector2(80,0)},false,new[]{Group()}));
        Assert.False(RoadSpatialGrid.IsInitialized); Assert.False(ManualRoads.Busy);
    }
    [Fact] public void ConnectRequiresAnExistingNetworkOnTheSourceIsland()
    {
        BaseRoad();
        Assert.Throws<InvalidOperationException>(()=>ManualRoads.Prepare(new[]{new Vector2(160,0)},true,new[]{Group(16,32)}));
    }
    [Fact] public void ConnectionUsesTheSourceIslandEvenIfAnotherRoadIsCloser()
    {
        BaseRoad(); Add(80);
        Assert.True(RoadSpatialGrid.TryGetRoadWithin(new Vector2(0,60),100,out var near,p=>p.y<0));
        Assert.True(near.y<0);
        var finder=new RoadPathfinder(world);
        var path=finder.FindPathToNetwork(new Vector2(0,60), RoadPathfinder.CellSize,()=>RoadNetworkGenerator.Reached(new Vector2(0,60),p=>p.y<0),new[]{new Vector2(0,-16)},p=>p.y<0);
        Assert.NotNull(path); Assert.True(path![path.Count-1].y<0);
    }
    [Fact] public void AlreadyConnectedIsANoop()
    {
        BaseRoad(); var before=RoadSpatialGrid.SerializeAllRoadPoints();
        var plan=ManualRoads.Prepare(new[]{new Vector2(0,-16)},true,new[]{Group()});
        Assert.True(plan.AlreadyConnected); ManualRoads.Commit(plan); Assert.Equal(before,RoadSpatialGrid.SerializeAllRoadPoints());
    }
    [Fact] public void RealConnectPlansAndAppendsAJoin()
    {
        BaseRoad();var plan=ManualRoads.Prepare(new[]{new Vector2(80,80)},true,new[]{Group()});
        Assert.False(plan.AlreadyConnected);Assert.NotEmpty(plan.Paths);ManualRoads.Commit(plan);Assert.Equal(1,RoadSpatialGrid.AppendCount);
    }
    [Fact] public void AnEmittedPieceIsNotRecreatedIfItIsDestroyedBeforeTheBatchCompletes()
    {
        var zone = new Vector2s(0,0);
        var emitted = new StructurePiece { Prefab="wood_floor", Position=new Vector3(0,40,0) };
        var waiting = new StructurePiece { Prefab="wood_floor", Position=new Vector3(2,40,0) };
        StructureAppendQueue.Enqueue(new Dictionary<Vector2s,List<StructurePiece>> { { zone,new(){emitted,waiting} } });
        StructureAppendQueue.Acknowledge(zone,StructureAppendQueue.Key(emitted));
        var saved=StructureAppendQueue.Serialize(); StructureAppendQueue.Reset(); StructureAppendQueue.Load(saved);
        Assert.Single(StructureAppendQueue.ForZone(zone)!);
        Assert.Equal(StructureAppendQueue.Key(waiting),StructureAppendQueue.Key(StructureAppendQueue.ForZone(zone)![0]));
    }

    [Fact] public void PendingBridgeQueueSurvivesReloadAndDeduplicatesRepeatedEnqueue()
    {
        var zone=new Vector2s(0,0);var piece=new StructurePiece {Prefab="wood_floor",Position=new Vector3(0,40,0),RollDegrees=15,HealthFraction=.5f};
        var additions=new Dictionary<Vector2s,List<StructurePiece>>{{zone,new(){piece}}};
        StructureAppendQueue.Enqueue(additions);StructureAppendQueue.Enqueue(additions);Assert.Single(StructureAppendQueue.ForZone(zone)!);
        var data=StructureAppendQueue.Serialize();StructureAppendQueue.Reset();StructureAppendQueue.Load(data);
        Assert.Equal(StructureAppendQueue.Key(piece),StructureAppendQueue.Key(StructureAppendQueue.ForZone(zone)![0]));
        Assert.Throws<EndOfStreamException>(()=>StructureAppendQueue.Load(data.Take(data.Length-1).ToArray()));
        Assert.Equal(data,StructureAppendQueue.Serialize());
        // Completing marks only the queue's own work done: the zone is recorded spawned by its placement once its plan is out.
        StructureAppendQueue.Complete(zone);Assert.False(StructurePlans.IsSpawned(zone));Assert.Empty(StructureAppendQueue.Zones);
    }
}

public class ManualRoadFailureTests : IDisposable
{
    private sealed class Cliff : WorldGenerator
    {
        public override float GetHeight(float x,float z)=>x<64 ? 40 : 180;
        public override void GetRiverWeight(float x,float z,out float weight,out float width){weight=0;width=0;}
    }
    private readonly ValheimWorldScope _world=new ValheimWorldScope().WithWorld(new Cliff()).WithZdos().WithZoneSystem().WithNetwork(server: true);
    public ManualRoadFailureTests()
    {
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld();
        RoadNetworkGenerator.MarkLocationsReady();RoadGrade.Configured=.35f;RoadNetworkGenerator.RoadWidth=4;
        RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
    }
    public void Dispose()
    {
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld();_world.Dispose();
    }
    private static Island Island()
    {
        var i=new Island {Id=1,Min=new Vector2(-1024,-1024),Max=new Vector2(1024,1024),CellSize=8,WorldOffset=0};
        for(int x=-128;x<=128;x++)for(int z=-128;z<=128;z++)i.Cells.Add(new Vector2Int(x,z));return i;
    }
    [Fact] public void AFailingSecondLegPublishesNeitherTheFirstLegNorBridgeMetadata()
    {
        var d=ManualRoads.Draft("host");d.Add(new Vector2(-80,0));d.Add(new Vector2(-16,0));d.Add(new Vector2(160,0));
        var error=Assert.Throws<InvalidOperationException>(()=>ManualRoads.Prepare(d.Points,false,new[]{Island()}));
        Assert.Contains("Leg 2",error.Message);Assert.False(RoadSpatialGrid.IsInitialized);
        Assert.Empty(RoadNetworkGenerator.GetRoadCrossings());Assert.Empty(StructureAppendQueue.Zones);Assert.Equal(3,d.Points.Count);Assert.False(ManualRoads.Busy);
    }
    [Fact] public void OwnershipDefersTheAdditionUntilTheCompilerCanBeWritten()
    {
        var world=WorldGenerator.instance!;
        RoadSpatialGrid.AddRoadPath(new List<Vector2>{new(-24,-16),new(24,-16)},4,world);RoadSpatialGrid.FinalizeRoadNetwork();
        var hm=_world.RegisterHeightmap(new Vector2s(0,0));var tc=hm.m_terrainComp!;
        RoadTerrainModifier.OnTerrainCompilerReady(tc);int saves=tc.SaveCount;
        var plan=RoadSpatialGrid.PlanRoadPath(new List<Vector2>{new(-24,16),new(24,16)},4,world)!;
        RoadSpatialGrid.CommitAppend(new[]{plan},RoadSpatialGrid.RoadNetworkVersion);
        tc.m_nview.GetZDO().SetOwner(9999);RoadTerrainModifier.ApplyAdditionsToLoadedZones();Assert.Equal(saves,tc.SaveCount);
        tc.m_nview.ClaimOwnership();RoadTerrainModifier.ApplyAdditionsToLoadedZones();Assert.Equal(saves+1,tc.SaveCount);
    }
    [Fact] public void ADeferredRebuildCoalescesTwoAdditionsWithoutForgettingEither()
    {
        var world=WorldGenerator.instance!;var zone=new Vector2s(0,0);
        RoadSpatialGrid.AddRoadPath(new List<Vector2>{new(-24,-16),new(24,-16)},4,world);RoadSpatialGrid.FinalizeRoadNetwork();
        var hm=_world.RegisterHeightmap(zone);var tc=hm.m_terrainComp!;
        RoadTerrainModifier.OnTerrainCompilerReady(tc);hm.AutoRebuild=false;
        foreach(float z in new[]{0f,16f})
        {
            var plan=RoadSpatialGrid.PlanRoadPath(new List<Vector2>{new(-24,z),new(24,z)},4,world)!;
            RoadSpatialGrid.CommitAppend(new[]{plan},RoadSpatialGrid.RoadNetworkVersion);
            RoadTerrainModifier.OnTerrainCompilerReady(tc);
        }
        hm.RebuildTerrain();Assert.True(tc.m_modifiedHeight[32*65+32]);Assert.True(tc.m_modifiedHeight[48*65+32]);Assert.True(RoadTerrainModifier.CarriesCurrentRoads(tc));
    }
}

public class ManualBridgeTests : IDisposable
{
    private readonly ValheimWorldScope _world=new ValheimWorldScope().WithWorld(new BridgeTests.WideRiverWorld()).WithZdos().WithZoneSystem().WithNetwork(server: true);
    public ManualBridgeTests()
    {
        RoadNetworkGenerator.Reset();RoadTerrainModifier.ResetForWorld();
        RoadNetworkGenerator.MarkLocationsReady();RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
        RoadGrade.Configured=.35f;RoadNetworkGenerator.RoadWidth=4;
    }
    public void Dispose()
    {
        RoadNetworkGenerator.Reset();_world.Dispose();
    }
    [Fact] public void FullRespawnDiscardsAppendProgressButKeepsTheCompletePlan()
    {
        var island=new Island {Id=1,CellSize=8,WorldOffset=0,Min=new Vector2(-216,-120),Max=new Vector2(216,120)};
        for(int x=-27;x<=27;x++)for(int z=-15;z<=15;z++)island.Cells.Add(new Vector2Int(x,z));
        var plan=ManualRoads.Prepare(new[]{new Vector2(-160,0),new Vector2(160,0)},false,new[]{island});
        ManualRoads.Commit(plan);
        var zone=StructureAppendQueue.Zones.First(z=>StructureAppendQueue.ForZone(z)!.Count>1);
        int fullCount=StructurePlans.PlannedPieceCount(zone);
        var first=StructureAppendQueue.ForZone(zone)![0];
        StructureAppendQueue.Acknowledge(zone,StructureAppendQueue.Key(first));
        Assert.True(StructureAppendQueue.ForZone(zone)!.Count<fullCount);
        // Ordinary retry/reload keeps acknowledgements: do not resurrect damage.
        var pending=StructureAppendQueue.Serialize(); StructureAppendQueue.Load(pending);
        Assert.DoesNotContain(StructureAppendQueue.ForZone(zone)!,p=>StructureAppendQueue.Key(p)==StructureAppendQueue.Key(first));
        StructurePlans.MarkSpawned(zone);
        // The real clear/respawn path calls these two methods before spawning.
        StructurePlans.InvalidatePlans(); StructurePlans.ForgetSpawned();
        Assert.Null(StructureAppendQueue.ForZone(zone));
        Assert.False(StructurePlans.IsSpawned(zone));
        Assert.Equal(fullCount,StructurePlans.PlannedPieceCount(zone));
        // Persisting and restoring the queue cannot reinstate the stale remainder.
        var after=StructureAppendQueue.Serialize(); StructureAppendQueue.Load(after);
        Assert.Empty(StructureAppendQueue.Zones);
    }

    [Fact] public void AManualRiverCrossingQueuesNewPiecesInAnAlreadySpawnedZoneAndKeepsOldObjects()
    {
        var island=new Island {Id=1,CellSize=8,WorldOffset=0,Min=new Vector2(-216,-120),Max=new Vector2(216,120)};
        for(int x=-27;x<=27;x++)for(int z=-15;z<=15;z++)island.Cells.Add(new Vector2Int(x,z));
        var plan=ManualRoads.Prepare(new[]{new Vector2(-160,0),new Vector2(160,0)},false,new[]{island});
        Assert.NotEmpty(plan.NewCrossings);
        var planned=StructureAppendQueue.Prepare(plan.NewCrossings);Assert.NotEmpty(planned);
        foreach(var zone in planned.Keys)StructurePlans.MarkSpawned(zone);
        var existing=ZDOMan.instance!.CreateNewZDO(new Vector3(0,40,0),"wood_floor".GetStableHashCode());
        existing.Set(StructurePlans.MarkerHash,1);
        int oldCount=ZDOMan.instance.CountWithPrefab("wood_floor");
        ManualRoads.Commit(plan);RoadNetworkGenerator.SaveGlobalRoadData();
        Assert.Equal(oldCount,ZDOMan.instance.CountWithPrefab("wood_floor"));
        Assert.NotEmpty(StructureAppendQueue.Zones);
        var queue=StructureAppendQueue.Serialize();
        RoadNetworkGenerator.Reset();Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData());
        Assert.Equal(queue,StructureAppendQueue.Serialize());Assert.Equal(oldCount,ZDOMan.instance.CountWithPrefab("wood_floor"));
        foreach(var zone in StructureAppendQueue.Zones)Assert.True(StructurePlans.IsSpawned(zone));
    }
}
