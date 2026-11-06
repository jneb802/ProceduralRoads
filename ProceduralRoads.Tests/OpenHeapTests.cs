using System.Collections.Generic;
using Xunit;
namespace ProceduralRoads.Tests;

/// <summary>The search's open heap, with an improved cell pushed again and its old entry dropped once the cell is
/// closed, takes cells in the order a sorted set holding one entry per cell (the old open set) does.</summary>
public class OpenHeapTests
{
    [Fact]
    public void CellsComeOutInTheOrderOfASortedSetWithOneEntryPerCell()
    {
        var random = new System.Random(7);
        for (int round = 0; round < 200; round++)
        {
            var heap = new RoadPathfinder.OpenHeap();
            var set = new SortedSet<(float priority, Vector2i pos)>(Comparer<(float priority, Vector2i pos)>.Create(RoadPathfinder.OpenHeap.Compare));
            var cost = new Dictionary<Vector2i, float>();
            var closed = new HashSet<Vector2i>();
            // Priorities on a coarse grid, so equal priorities (ties broken by x, then y) and equal keys are common.
            float Price() => random.Next(0, 40) * 0.5f;
            void Offer(Vector2i cell, float f)
            {
                if (closed.Contains(cell)) return;
                if (cost.TryGetValue(cell, out float old) && f >= old) return;
                if (cost.ContainsKey(cell)) set.Remove((old, cell));
                set.Add((f, cell));
                heap.Push((f, cell));
                cost[cell] = f;
            }
            for (int i = 0; i < 20; i++) Offer(new Vector2i(random.Next(-5, 5), random.Next(-5, 5)), Price());
            while (true)
            {
                bool open = heap.TrimClosed(closed);
                Assert.Equal(set.Count > 0, open);
                if (!open) break;
                Assert.Equal(set.Min, heap.Min);
                var taken = heap.Pop();
                Assert.Equal(set.Min, taken);
                set.Remove(set.Min);
                closed.Add(taken.pos);
                // Relaxing: some cells improve (pushed again), some are new; the price may also come out equal.
                for (int i = 0; i < random.Next(0, 6); i++)
                    Offer(new Vector2i(UnityEngine.Mathf.Clamp(taken.pos.x + random.Next(-2, 3), -6, 6), UnityEngine.Mathf.Clamp(taken.pos.y + random.Next(-2, 3), -6, 6)),
                        taken.priority + Price() - 4f);
            }
        }
    }
}
