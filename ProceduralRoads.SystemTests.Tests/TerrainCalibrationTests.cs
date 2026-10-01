using System.Text.Json;
using ProceduralRoads.SystemTests;
using ProceduralRoads.TestFixtures;
using Xunit;

public class TerrainCalibrationTests
{
    private static JsonElement Data(Action<List<Dictionary<string, object>>>? edit = null, bool cleanup = true)
    {
        var samples = new List<Dictionary<string, object>>();
        foreach (var stage in new[] { "baseline" }.Concat(FlatRoadFixture.Stages))
            for (int zone = 0; zone < 2; zone++)
                foreach (var sample in FlatRoadFixture.Samples(zone)) samples.Add(new()
                {
                    ["stage"] = stage, ["zoneOffset"] = zone, ["x"] = sample.x, ["z"] = sample.z,
                    ["height"] = FlatRoadFixture.Expected(stage, sample.z), ["colliderHeight"] = FlatRoadFixture.Expected(stage, sample.z)
                });
        edit?.Invoke(samples);
        return JsonSerializer.SerializeToElement(new { source = "declared-platform-real-compiler", fixture = FlatRoadFixture.Id, complete = true, temporaryObjectsReleased = cleanup, samples });
    }
    [Fact] public void EveryDeclaredSampleIncludingBothBoundaryCopiesMustAgree()
    {
        var rows = TerrainCalibrationScenario.Compare(Data());
        Assert.Equal(100, rows.Count); Assert.All(rows, x => Assert.True(x.Passed));
        Assert.Equal(50, rows.Count(x => x.X == 32));
    }
    [Theory] [InlineData("height")] [InlineData("colliderHeight")]
    public void EitherLayerCanDisagreeAndKeepsItsResidual(string field)
    {
        var rows = TerrainCalibrationScenario.Compare(Data(samples => samples[21][field] = 64f));
        Assert.Single(rows.Where(x => !x.Passed));
    }
    [Fact] public void SameCountWithWrongIdentityDoesNotPass() => Assert.Throws<InvalidOperationException>(() =>
        TerrainCalibrationScenario.Compare(Data(samples => samples[1] = samples[0])));
    [Fact] public void MissingSampleDoesNotPass() => Assert.Throws<InvalidOperationException>(() =>
        TerrainCalibrationScenario.Compare(Data(samples => samples.RemoveAt(0))));
    [Fact] public void FailedCleanupDoesNotPass() => Assert.Throws<InvalidOperationException>(() => TerrainCalibrationScenario.Compare(Data(cleanup: false)));
}
