using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;

namespace ProceduralRoads
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class ProceduralRoadsPlugin : BaseUnityPlugin
    {
        internal const string ModName = "ProceduralRoads";
        internal const string ModVersion = "1.0.0";
        internal const string Author = "warpalicious";
        private const string ModGUID = Author + "." + ModName;
        private static string ConfigFileName = ModGUID + ".cfg";
        private static string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
        internal static string ConnectionError = "";
        private readonly Harmony _harmony = new(ModGUID);

        public static readonly ManualLogSource ProceduralRoadsLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);

        // Location Manager variables
        public Texture2D tex = null!;

        // Use only if you need them
        //private Sprite mySprite = null!;
        //private SpriteRenderer sr = null!;

        public enum Toggle
        {
            On = 1,
            Off = 0
        }

        // Configuration entries
        public static ConfigEntry<float> RoadWidth = null!;
        public static ConfigEntry<int> MaxRoadsFromSpawn = null!;
        public static ConfigEntry<float> MaxRoadLength = null!;
        public static ConfigEntry<bool> EnableRoads = null!;

        public void Awake()
        {
            bool saveOnSet = Config.SaveOnConfigSet;
            Config.SaveOnConfigSet = false;

            // Initialize configuration
            EnableRoads = Config.Bind("General", "EnableRoads", true,
                "Enable procedural road generation");
            RoadWidth = Config.Bind("Roads", "RoadWidth", 4f,
                new ConfigDescription("Width of generated roads in meters", 
                    new AcceptableValueRange<float>(2f, 10f)));
            MaxRoadsFromSpawn = Config.Bind("Roads", "MaxRoadsFromSpawn", 5,
                new ConfigDescription("Maximum number of roads to generate from spawn point",
                    new AcceptableValueRange<int>(1, 10)));
            MaxRoadLength = Config.Bind("Roads", "MaxRoadLength", 3000f,
                new ConfigDescription("Maximum road length in meters",
                    new AcceptableValueRange<float>(500f, 8000f)));

            // Apply config to road generator
            ApplyConfiguration();

            Assembly assembly = Assembly.GetExecutingAssembly();
            _harmony.PatchAll(assembly);
            SetupWatcher();

            ProceduralRoadsLogger.LogInfo($"{ModName} v{ModVersion} loaded - Procedural roads enabled");

            if (saveOnSet)
            {
                Config.SaveOnConfigSet = saveOnSet;
                Config.Save();
            }
        }

        private static void ApplyConfiguration()
        {
            RoadNetworkGenerator.RoadWidth = RoadWidth.Value;
            RoadNetworkGenerator.MaxRoadsFromSpawn = MaxRoadsFromSpawn.Value;
            RoadNetworkGenerator.MaxRoadLength = MaxRoadLength.Value;
        }

        private void OnDestroy()
        {
            Config.Save();
        }

        private void SetupWatcher()
        {
            FileSystemWatcher watcher = new(Paths.ConfigPath, ConfigFileName);
            watcher.Changed += ReadConfigValues;
            watcher.Created += ReadConfigValues;
            watcher.Renamed += ReadConfigValues;
            watcher.IncludeSubdirectories = true;
            watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
            watcher.EnableRaisingEvents = true;
        }

        private void ReadConfigValues(object sender, FileSystemEventArgs e)
        {
            if (!File.Exists(ConfigFileFullPath)) return;
            try
            {
                ProceduralRoadsLogger.LogDebug("ReadConfigValues called");
                Config.Reload();
                ApplyConfiguration();
            }
            catch
            {
                ProceduralRoadsLogger.LogError($"There was an issue loading your {ConfigFileName}");
                ProceduralRoadsLogger.LogError("Please check your config entries for spelling and format!");
            }
        }


        #region ConfigOptions

        private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description)
        {
            return Config.Bind(group, name, value, description);
        }

        private ConfigEntry<T> config<T>(string group, string name, T value, string description)
        {
            return config(group, name, value, new ConfigDescription(description));
        }

        private class ConfigurationManagerAttributes
        {
            [UsedImplicitly] public int? Order = null!;
            [UsedImplicitly] public bool? Browsable = null!;
            [UsedImplicitly] public string? Category = null!;
            [UsedImplicitly] public Action<ConfigEntryBase>? CustomDrawer = null!;
        }

        class AcceptableShortcuts : AcceptableValueBase
        {
            public AcceptableShortcuts() : base(typeof(KeyboardShortcut))
            {
            }

            public override object Clamp(object value) => value;
            public override bool IsValid(object value) => true;

            public override string ToDescriptionString() =>
                "# Acceptable values: " + string.Join(", ", UnityInput.Current.SupportedKeyCodes);
        }

        #endregion
    }

    public static class KeyboardExtensions
    {
        public static bool IsKeyDown(this KeyboardShortcut shortcut)
        {
            return shortcut.MainKey != KeyCode.None && Input.GetKeyDown(shortcut.MainKey) &&
                   shortcut.Modifiers.All(Input.GetKey);
        }

        public static bool IsKeyHeld(this KeyboardShortcut shortcut)
        {
            return shortcut.MainKey != KeyCode.None && Input.GetKey(shortcut.MainKey) &&
                   shortcut.Modifiers.All(Input.GetKey);
        }
    }
}