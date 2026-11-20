using System.Collections.Generic;

namespace ProceduralRoads;

/// <summary>
/// A map from a search state (<see cref="Vector2i"/>) to a value, for a route search's working sets
/// (RoadPathfinder.SearchState): open addressing on the packed coordinates, linear probing, deletion by backward shift, and
/// cleared in constant time by a stamp. It answers every lookup, insertion and removal as a
/// <see cref="Dictionary{TKey, TValue}"/> with <see cref="RoadGridComparer"/> does (the same keys are present with the same
/// values); it is never enumerated, so no order can differ. On the game's runtime it saves the dictionary's interface calls
/// to the comparer and its entry bookkeeping on each of the many lookups a search makes per cell.
/// </summary>
internal sealed class CellMap<T>
{
    private long[] m_keys;
    private T[] m_values;
    private int[] m_stamps;
    private int m_stamp = 1, m_count, m_mask, m_shift;

    /// <summary>A map of <paramref name="capacity"/> slots to start with (a power of two; it doubles at half full).</summary>
    public CellMap(int capacity = 256)
    {
        int size = 16;
        while (size < capacity) size *= 2;
        m_keys = new long[size]; m_values = new T[size]; m_stamps = new int[size];
        SetSize(size);
    }

    private void SetSize(int size)
    {
        m_mask = size - 1;
        int bits = 0;
        while ((1 << bits) < size) bits++;
        m_shift = 64 - bits;
    }

    public int Count => m_count;

    // The smallest helpers inlined: the game's runtime inlines a method only under a few bytes of IL unless asked, and a
    // search makes millions of these lookups.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static long Key(Vector2i p) => ((long)p.x << 32) | (uint)p.y;

    /// <summary>A key's home slot: the top bits of its Fibonacci product, which depend on every bit of the key.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal int Home(long key) => (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> m_shift);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private int Find(long key)
    {
        int i = Home(key);
        while (m_stamps[i] == m_stamp)
        {
            if (m_keys[i] == key) return i;
            i = (i + 1) & m_mask;
        }
        return ~i;
    }

    public void Clear()
    {
        m_count = 0;
        if (++m_stamp == int.MaxValue)
        {
            System.Array.Clear(m_stamps, 0, m_stamps.Length);
            m_stamp = 1;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool ContainsKey(Vector2i p) => Find(Key(p)) >= 0;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(Vector2i p, out T value)
    {
        int i = Find(Key(p));
        if (i >= 0) { value = m_values[i]; return true; }
        value = default!;
        return false;
    }

    public T this[Vector2i p]
    {
        get
        {
            int i = Find(Key(p));
            if (i < 0) throw new KeyNotFoundException($"The given key '{p}' was not present in the search's map.");
            return m_values[i];
        }
        set
        {
            long key = Key(p);
            int i = Find(key);
            if (i >= 0) { m_values[i] = value; return; }
            if ((m_count + 1) * 2 > m_keys.Length)
            {
                Grow();
                i = Find(key);
            }
            i = ~i;
            m_keys[i] = key;
            m_values[i] = value;
            m_stamps[i] = m_stamp;
            m_count++;
        }
    }

    /// <summary>Adds <paramref name="p"/> with <paramref name="value"/> where it is not held yet (one probe); false where it is.</summary>
    public bool TryAdd(Vector2i p, T value)
    {
        long key = Key(p);
        int i = Find(key);
        if (i >= 0) return false;
        if ((m_count + 1) * 2 > m_keys.Length)
        {
            Grow();
            i = Find(key);
        }
        i = ~i;
        m_keys[i] = key;
        m_values[i] = value;
        m_stamps[i] = m_stamp;
        m_count++;
        return true;
    }

    /// <summary>The packed key of a cell (for tests of the table's layout).</summary>
    internal static long KeyOf(Vector2i p) => Key(p);

    /// <summary>The slots the table has now (for tests of the table's layout).</summary>
    internal int Slots => m_keys.Length;

    public bool Remove(Vector2i p)
    {
        int i = Find(Key(p));
        if (i < 0) return false;
        // Backward shift: pull later entries of the probe run back over the hole so every key stays reachable.
        int hole = i, j = i;
        while (true)
        {
            j = (j + 1) & m_mask;
            if (m_stamps[j] != m_stamp) break;
            int home = Home(m_keys[j]);
            // Move j into the hole when its home is not cyclically within (hole, j].
            bool between = hole <= j ? (home > hole && home <= j) : (home > hole || home <= j);
            if (!between)
            {
                m_keys[hole] = m_keys[j];
                m_values[hole] = m_values[j];
                hole = j;
            }
        }
        m_stamps[hole] = 0;
        m_values[hole] = default!;
        m_count--;
        return true;
    }

    private void Grow()
    {
        long[] keys = m_keys; T[] values = m_values; int[] stamps = m_stamps; int stamp = m_stamp;
        int size = keys.Length * 2;
        m_keys = new long[size]; m_values = new T[size]; m_stamps = new int[size]; SetSize(size); m_stamp = 1; m_count = 0;
        for (int k = 0; k < keys.Length; k++)
        {
            if (stamps[k] != stamp) continue;
            int i = ~Find(keys[k]);
            m_keys[i] = keys[k]; m_values[i] = values[k]; m_stamps[i] = 1; m_count++;
        }
    }
}

/// <summary>A set of search states with <see cref="CellMap{T}"/>'s layout: the same membership as a
/// <see cref="HashSet{T}"/> with <see cref="RoadGridComparer"/>, never enumerated.</summary>
internal sealed class CellSet
{
    private readonly CellMap<bool> m_map;
    public CellSet(int capacity = 256) { m_map = new CellMap<bool>(capacity); }
    public int Count => m_map.Count;
    public void Clear() => m_map.Clear();
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool Contains(Vector2i p) => m_map.ContainsKey(p);
    public bool Add(Vector2i p) => m_map.TryAdd(p, true);
    public bool Remove(Vector2i p) => m_map.Remove(p);
}
