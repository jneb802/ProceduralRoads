using System;

namespace ProceduralRoads;

/// <summary>Balance a synchronous asset read, including a load that throws after acquiring its reference.</summary>
internal static class TemporaryAssetRead
{
    internal static T Read<T>(Func<uint> referenceCount, Action load, Action release, Func<T> read)
    {
        uint before = referenceCount();
        bool returned = false;
        try
        {
            load();
            returned = true;
            return read();
        }
        finally
        {
            // The game's loader increments before loading. On an exception,
            // observe the increment rather than releasing somebody else's hold.
            if (returned || referenceCount() > before)
                release();
        }
    }
}
