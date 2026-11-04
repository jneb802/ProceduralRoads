// Roads-only stand-ins; the game-type doubles come from the Valheim.Testing.Doubles source package.

namespace ProceduralRoads
{
    /// <summary>Shim for the plugin class; logger plus empty config surface.</summary>
    public static class ProceduralRoadsPlugin
    {
        public static BepInEx.Logging.ManualLogSource ProceduralRoadsLogger { get; } = new();

        /// <summary>Stand-in for the [Locations] CustomLocations config entry; tests set it.</summary>
        public static System.Collections.Generic.List<string> ConfigLocationNames { get; set; } = new();

        public static System.Collections.Generic.List<string> GetConfigLocationNames() => new(ConfigLocationNames);
    }
}
