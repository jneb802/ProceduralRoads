using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

[Collection("RoadStatics")]
public class SavedRecordOrderTests
{
    private static readonly BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;

    private static RoadCrossing Crossing(float x, float y, float length) =>
        RoadCrossing.Between(new Vector2(x, y), new Vector2(x + length, y), 27f, new Vector2(x + length / 2f, y), 10f, CrossingKind.Bridge);

    /// <summary>Islands commit their roads in whatever order they finish, so the saved records must not depend on it:
    /// the same roads committed in two orders save the same bytes.</summary>
    [Fact]
    public void CrossingsAndStartPointsSaveTheSameBytesWhicheverIslandCommittedFirst()
    {
        var crossings = (List<RoadCrossing>)typeof(RoadNetworkGenerator).GetField("m_roadCrossings", Flags)!.GetValue(null)!;
        var starts = (List<(Vector2 position, string label)>)typeof(RoadNetworkGenerator).GetField("m_roadStartPoints", Flags)!.GetValue(null)!;
        RoadNetworkGenerator.Reset();
        try
        {
            RoadCrossing[] roads = { Crossing(300f, 10f, 40f), Crossing(-200f, 50f, 30f), Crossing(-200f, -60f, 35f), Crossing(900f, 5f, 20f) };
            (Vector2, string)[] pins = { (new Vector2(300f, 10f), "A"), (new Vector2(-200f, 50f), "B"), (new Vector2(-200f, -60f), "C"), (new Vector2(900f, 5f), "D") };

            byte[][] saved = new byte[2][];
            for (int run = 0; run < 2; run++)
            {
                crossings.Clear(); starts.Clear();
                int[] order = run == 0 ? new[] { 0, 1, 2, 3 } : new[] { 3, 2, 0, 1 };
                foreach (int i in order) { crossings.Add(roads[i]); starts.Add(pins[i]); }
                RoadNetworkGenerator.SortRecordsCanonical();
                saved[run] = RoadNetworkPersistence.SerializeRoadCrossings(crossings)
                    .Concat(RoadNetworkPersistence.SerializeRoadStartPoints(starts)).ToArray();
            }
            Assert.Equal(saved[0], saved[1]);
            Assert.Equal(new[] { -200f, -200f, 300f, 900f }, crossings.Select(c => c.FromBank.x));
            Assert.Equal(new[] { "C", "B", "A", "D" }, starts.Select(s => s.label));
        }
        finally { RoadNetworkGenerator.Reset(); }
    }
}
