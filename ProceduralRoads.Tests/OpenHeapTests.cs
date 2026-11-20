using System.Collections.Generic;
using Xunit;
namespace ProceduralRoads.Tests;

/// <summary>The search's open heap, with an improved cell pushed again and its old entry dropped once the cell is
/// closed, takes cells in the order a sorted set holding one entry per cell (the old open set) does.</summary>
public class OpenHeapTests
{
    /// <summary>The heap's order is float.CompareTo's on the priority (NaN below every number, -0 equal to 0, the
    /// infinities), then int's on x and on y: the same sign for every pair of these.</summary>
    [Fact]
    public void TheOrderIsFloatCompareToThenXThenY()
    {
        float[] priorities = { float.NaN, float.NegativeInfinity, -1e30f, -1f, -0f, 0f, 1e-45f, 1f, 3.5f, 1e30f, float.PositiveInfinity };
        int[] xs = { int.MinValue, -3, 0, 2, int.MaxValue };
        static int Old((float priority, Vector2i pos) a, (float priority, Vector2i pos) b)
        {
            int cmp = a.priority.CompareTo(b.priority);
            if (cmp != 0) return cmp;
            cmp = a.pos.x.CompareTo(b.pos.x);
            if (cmp != 0) return cmp;
            return a.pos.y.CompareTo(b.pos.y);
        }
        foreach (float pa in priorities) foreach (float pb in priorities)
            foreach (int xa in xs) foreach (int xb in xs)
                foreach (int ya in xs) foreach (int yb in xs)
                {
                    var a = (pa, new Vector2i(xa, ya));
                    var b = (pb, new Vector2i(xb, yb));
                    Assert.Equal(System.Math.Sign(Old(a, b)), System.Math.Sign(RoadPathfinder.OpenHeap.Compare(a, b)));
                }
    }

    [Fact]
    public void CellsComeOutInTheOrderOfASortedSetWithOneEntryPerCell()
    {
        var random = new System.Random(7);
        for (int round = 0; round < 200; round++)
        {
            var heap = new RoadPathfinder.OpenHeap();
            var set = new SortedSet<(float priority, Vector2i pos)>(Comparer<(float priority, Vector2i pos)>.Create(RoadPathfinder.OpenHeap.Compare));
            var cost = new Dictionary<Vector2i, float>();
            var closed = new CellSet();
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
