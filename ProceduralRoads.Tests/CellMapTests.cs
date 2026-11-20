using System.Collections.Generic;
using Xunit;
namespace ProceduralRoads.Tests;

/// <summary>A search's cell map and set answer every lookup, insertion, removal and clear as the dictionary and hash set
/// with RoadGridComparer did: random operations on both, with growth, removals in long probe runs and clears between
/// rounds, keys clustered and far apart (approach states use the high bits).</summary>
public class CellMapTests
{
    [Fact]
    public void ACellMapAnswersAsTheDictionaryItReplaces()
    {
        var random = new System.Random(3);
        var map = new CellMap<float>();
        var set = new CellSet();
        for (int round = 0; round < 30; round++)
        {
            var reference = new Dictionary<Vector2i, float>(RoadGridComparer.Instance);
            var members = new HashSet<Vector2i>(RoadGridComparer.Instance);
            map.Clear();
            set.Clear();
            int spread = round % 3 == 0 ? 6 : round % 3 == 1 ? 60 : 1 << 20;
            for (int op = 0; op < 20000; op++)
            {
                var p = new Vector2i(random.Next(-spread, spread) + (random.Next(4) == 0 ? 1 << 24 : 0), random.Next(-spread, spread));
                switch (random.Next(6))
                {
                    case 0: case 1:
                        float v = random.Next(1000) * 0.25f;
                        map[p] = v; reference[p] = v;
                        Assert.Equal(members.Add(p), set.Add(p));
                        break;
                    case 2:
                        Assert.Equal(reference.Remove(p), map.Remove(p));
                        Assert.Equal(members.Remove(p), set.Remove(p));
                        break;
                    default:
                        Assert.Equal(reference.TryGetValue(p, out float a), map.TryGetValue(p, out float b));
                        Assert.Equal(a, b);
                        Assert.Equal(reference.ContainsKey(p), map.ContainsKey(p));
                        Assert.Equal(members.Contains(p), set.Contains(p));
                        break;
                }
                Assert.Equal(reference.Count, map.Count);
                Assert.Equal(members.Count, set.Count);
            }
            foreach (var kv in reference) Assert.Equal(kv.Value, map[kv.Key]);
            Assert.Throws<KeyNotFoundException>(() => map[new Vector2i(int.MaxValue, int.MinValue)]);
        }
    }

    /// <summary>A probe run that wraps past the last slot to the first: removing its head pulls the wrapped entries back,
    /// and every key is still found (backward shift across the table's end).</summary>
    [Fact]
    public void RemovalPullsBackAProbeRunThatWrapsPastTheEnd()
    {
        var map = new CellMap<int>(16);
        int last = map.Slots - 1;
        var homeLast = new List<Vector2i>();
        for (int x = 0; homeLast.Count < 5 && x < 1_000_000; x++)
        {
            var p = new Vector2i(x, 7);
            if (map.Home(CellMap<int>.KeyOf(p)) == last) homeLast.Add(p);
        }
        Assert.Equal(5, homeLast.Count);
        // Five keys at home in the last slot of sixteen: they fill it and wrap into slots 0..3 (five of sixteen: no growth).
        for (int i = 0; i < 5; i++) map[homeLast[i]] = i;
        Assert.Equal(16, map.Slots);
        Assert.True(map.Remove(homeLast[0]));
        for (int i = 1; i < 5; i++) Assert.Equal(i, map[homeLast[i]]);
        Assert.False(map.ContainsKey(homeLast[0]));
        Assert.True(map.Remove(homeLast[2]));
        Assert.Equal(1, map[homeLast[1]]); Assert.Equal(3, map[homeLast[3]]); Assert.Equal(4, map[homeLast[4]]);
        Assert.Equal(3, map.Count);
    }
}
