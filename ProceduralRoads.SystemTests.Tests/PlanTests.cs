using ProceduralRoads.SystemTests;
using Valheim.Testing.Game;
using Xunit;

// Roads' own plan rules. The rules every pinned server plan follows (strict pins, the executable, the isolated save
// root, the session token, Doorstop variables, unknown fields) are the toolkit's and tested there.
public class PlanTests
{
    internal static RunPlan Valid() => new()
    {
        Scenario = "empty-save", Runtime = new() { Source = Path.GetTempPath(), Sha256 = new() { ["server.exe"] = new('a', 64) } },
        World = new() { Source = Path.GetTempPath(), Sha256 = new() { ["worlds_local/test.db"] = new('b', 64) } },
        Executable = "valheim_server.exe", Arguments = ["-batchmode", "-nographics", "-savedir", "{world}"],
        Pins = new() { ["worlduid"] = "123", ["warpalicious.ProceduralRoads"] = new('1', 32), ["valheimCLI.valheimCLI"] = new('2', 32), ["testing.proceduralroads.adapter"] = new('3', 32) },
        RuntimePins = new() { Game = new('4', 64), BepInExCore = new('5', 64), Patchers = new('6', 64) },
    };
    [Fact] public void TheRoadsPluginsAreRequiredWithExactPins()
    {
        foreach (string plugin in RunPlan.RequiredPlugins)
        {
            var p = Valid(); p.Pins.Remove(plugin); Assert.Throws<ArgumentException>(p.Validate);
            p = Valid(); p.Pins[plugin] = "any"; Assert.Throws<ArgumentException>(p.Validate);
        }
        Valid().Validate();
    }
    [Fact] public void AnUnknownScenarioIsRefused() { var p = Valid(); p.Scenario = "bridge"; Assert.Throws<ArgumentException>(p.Validate); }
    [Fact] public void TerrainFixtureRequiresExplicitEnablementAndNoBridgeMutation()
    {
        var plan = Valid(); plan.Scenario = "terrain-calibration";
        Assert.Throws<ArgumentException>(plan.Validate);
        plan.Environment["ROADS_TEST_TERRAIN_CALIBRATION"] = "1"; plan.Validate();
        plan.Append = "road_path 1,2 3,4";
        Assert.Throws<ArgumentException>(plan.Validate);
    }
    [Theory][InlineData("paved")][InlineData("Dirt-Fade")][InlineData("")][InlineData("dirt-fade ")]
    public void PaintProfileMustBeAbsentOrDirtFade(string profile)
    {
        var p = Valid(); p.Scenario = "terrain-persistence"; p.Environment["ROADS_TEST_PERSISTENT_TERRAIN"] = "1"; p.Validate();
        p.Environment["ROADS_TEST_PAINT_PROFILE"] = "dirt-fade"; p.Validate();
        p.Environment["ROADS_TEST_PAINT_PROFILE"] = profile; Assert.Throws<ArgumentException>(p.Validate);
    }
    [Fact] public void PaintProfileKeyCannotDifferOnlyInCase()
    {
        var p = Valid(); p.Environment["roads_test_paint_profile"] = "dirt-fade"; Assert.Throws<ArgumentException>(p.Validate);
    }
    [Theory][InlineData("road_generate")][InlineData("road_path 1,2")][InlineData("road_path 1,2 NaN,3")][InlineData("road_path 1,2 3,4\nquit")]
    public void BridgePlanRefusesMalformedOrDifferentMutation(string command)
    { var p = Valid(); p.Scenario = "bridge-respawn"; p.Append = command; Assert.Throws<ArgumentException>(p.Validate); }
    [Fact] public void BridgeFixtureNeedsIndependentTransforms()
    {
        var p = Valid(); p.Scenario = "bridge-respawn"; p.Append = "road_path 1,2 3,4";
        Assert.Throws<ArgumentException>(p.Validate);
        p.Expected = [new(0, 0, [new(1, [0, 0, 0], [0, 0, 0, 1])])]; p.Validate();
        p.Expected.Add(p.Expected[0]); Assert.Throws<ArgumentException>(p.Validate);
    }
}
