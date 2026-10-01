using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public class CrossingRegistryTests
{
    [Fact]
    public void MatchingCrossingsReadUnderTheSameGateAsWriters()
    {
        RoadNetworkGenerator.Reset();
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var gate = typeof(RoadNetworkGenerator).GetField("m_recordGate", flags)!.GetValue(null)!;
        var registry = (List<RoadCrossing>)typeof(RoadNetworkGenerator)
            .GetField("m_roadCrossings", flags)!.GetValue(null)!;
        var existing = new RoadCrossing {
            FromBank = new Vector2(0, 0), ToBank = new Vector2(40, 0),
            Center = new Vector2(20, 0), Direction = new Vector2(1, 0), Width = 40
        };
        var candidate = new RoadCrossing {
            FromBank = new Vector2(1, 0), ToBank = new Vector2(41, 0),
            Center = new Vector2(21, 0), Direction = new Vector2(1, 0), Width = 40
        };
        lock (gate) registry.Add(existing);
        IEnumerable<RoadCrossing> Candidates()
        {
            // Checks the actual production reader, without relying on a race
            // happening to occur during a stress loop.
            Assert.True(Monitor.IsEntered(gate), "crossing lookup is not protected from concurrent appends");
            yield return candidate;
        }
        try
        {
            RoadNetworkGenerator.SnapToExistingCrossings(Candidates());
            Assert.Equal(existing.FromBank, candidate.FromBank);
            Assert.Equal(existing.ToBank, candidate.ToBank);
        }
        finally { RoadNetworkGenerator.Reset(); }
    }
}
