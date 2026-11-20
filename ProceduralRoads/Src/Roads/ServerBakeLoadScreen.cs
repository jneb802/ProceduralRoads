namespace ProceduralRoads;

/// <summary>
/// When, and for how long a frame, the server terrain bake runs while the host's loading screen is up. Before the
/// host's player first spawns, the bake owes work and the queue still moves, the spawn is held and each frame gives the
/// bake <see cref="HeldBudgetMs"/> instead of the in-world slice: the first load's terrain writes happen behind the
/// loading screen instead of as stutter in the world. A frame-sized slice, not one blocking drain, so the loading screen
/// still animates and the game's own zone building goes on between slices. The hold ends when the bake owes nothing more:
/// the queue is done (it always ends: a zone whose terrain the game never builds is given up after
/// ServerTerrainBake.MaxNotReadyTries), or the queue paused on an error. Once the player is in the world it is let go for
/// good, and the in-world slices take what is left; a new network starts it afresh. A zone that is not ready is no sign of
/// a stuck queue: its first try only asks the game's terrain builder, which builds one zone after another.
/// </summary>
public sealed class ServerBakeLoadScreen
{
    /// <summary>Main-thread time a frame gives the bake while the loading screen holds the spawn: about 30 frames a
    /// second for the loading screen's animation.</summary>
    public const double HeldBudgetMs = 33.0;

    /// <summary>The hold let go: the player spawned.</summary>
    public bool Released { get; private set; }

    /// <summary>Whether the spawn is held for the bake: the host's first spawn is still to come and the bake owes work.</summary>
    public bool Holds(bool beforeFirstSpawn, bool workOwed) => beforeFirstSpawn && workOwed && !Released;

    /// <summary>The main-thread time this frame's slice may use.</summary>
    public static double SliceBudgetMs(bool held, double inWorldMs) => held ? HeldBudgetMs : inWorldMs;

    /// <summary>The loading screen's line while the spawn is held: zones written of those that take writing.</summary>
    public static string Text(int written, int total) =>
        total > 0 ? $"Writing road terrain: {written:N0} / {total:N0} zones" : "Writing road terrain…";

    /// <summary>The one owner of server work (docs/OWNERS.md).</summary>
    public static float Fraction(int written, int total) =>
        total > 0 ? System.Math.Min(1f, System.Math.Max(0f, (float)written / total)) : 0f;

    /// <summary>The player spawned (the game went on without the hold, or after it): the in-world slices take over.</summary>
    public void Release() => Released = true;

    /// <summary>A new network (or the world going away): the next first spawn may be held again.</summary>
    public void Clear() => Released = false;
}
