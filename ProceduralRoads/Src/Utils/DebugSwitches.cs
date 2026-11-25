using System;
using BepInEx.Logging;

namespace ProceduralRoads;

/// <summary>
/// Switches that exist for development and validation rather than for play.
///
/// They are read from the environment rather than bound into the config file.
/// A config key cannot be taken back once it has been written to a user's
/// disk: renaming it, moving it or removing it leaves the old key sitting
/// there, and a setting that only ever mattered to whoever was debugging the
/// mod is not worth that. An environment variable costs the user nothing,
/// appears nowhere, and disappears when it is no longer set.
///
/// Every switch is named PROCEDURALROADS_&lt;NAME&gt; and defaults to the
/// behaviour the mod has without it, so an unset environment is ordinary play.
/// </summary>
internal static class DebugSwitches
{
    private const string Prefix = "PROCEDURALROADS_";

    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;

    /// <summary>
    /// A boolean switch. "1", "true", "yes" and "on" turn it on; "0", "false",
    /// "no" and "off" turn it off; anything else is reported and ignored, so a
    /// typo does not silently change how the mod behaves.
    /// </summary>
    internal static bool Flag(string name, bool fallback)
    {
        string variable = Prefix + name;
        string? value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        switch (value.Trim().ToLowerInvariant())
        {
            case "1": case "true": case "yes": case "on":
                Log.LogInfo($"{variable}=on");
                return true;
            case "0": case "false": case "no": case "off":
                Log.LogInfo($"{variable}=off");
                return false;
            default:
                Log.LogWarning($"{variable} is '{value}', which is neither on nor off; using {fallback}");
                return fallback;
        }
    }
}
