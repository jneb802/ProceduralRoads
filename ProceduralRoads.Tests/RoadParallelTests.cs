using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace ProceduralRoads.Tests;

public sealed class RoadParallelTests
{
    private static FieldInfo Count(string name) => typeof(RoadParallel).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void SpareAndTakeSpareAgreeForEveryWorkerStateAndShareOneDecision()
    {
        int configured = RoadParallel.Configured;
        int left = (int)Count("s_left").GetValue(null)!;
        int waiting = (int)Count("s_waiting").GetValue(null)!;
        int ahead = (int)Count("s_ahead").GetValue(null)!;
        try
        {
            foreach (int workers in new[] { 1, 2, 4 })
                for (int islands = 0; islands <= workers + 1; islands++)
                    for (int waits = 0; waits <= 1; waits++)
                        for (int active = 0; active <= workers; active++)
                        {
                            RoadParallel.Configured = workers;
                            RoadParallel.Islands(islands);
                            Count("s_waiting").SetValue(null, waits);
                            Count("s_ahead").SetValue(null, active);
                            int spare = RoadParallel.Spare;
                            bool taken = RoadParallel.TakeSpare();
                            Assert.Equal(spare > 0, taken);
                            if (islands != 1) Assert.Equal(0, spare);
                            if (taken)
                            {
                                Assert.Equal(spare - 1, RoadParallel.Spare);
                                RoadParallel.ReturnSpare();
                            }
                            Assert.Equal(spare, RoadParallel.Spare);
                            Assert.Equal(active, (int)Count("s_ahead").GetValue(null)!);
                        }
        }
        finally
        {
            RoadParallel.Configured = configured;
            Count("s_left").SetValue(null, left);
            Count("s_waiting").SetValue(null, waiting);
            Count("s_ahead").SetValue(null, ahead);
        }
        string code = OwnerLiteralTests.Code(File.ReadAllText(Path.Combine(OwnersDocTests.RepoRoot(),
            "ProceduralRoads", "Src", "Roads", "RoadParallel.cs")));
        Assert.Single(Regex.Matches(code, @"IslandWorkers\s*-\s*\(").Cast<Match>());
    }
}
