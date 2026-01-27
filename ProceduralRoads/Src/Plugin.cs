using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace ProceduralRoads
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    public class ProceduralRoadsPlugin : BaseUnityPlugin
    {
        internal const string ModName = "ProceduralRoads";
        internal const string ModVersion = "1.3.1";
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
        public static ConfigEntry<string> CustomLocations = null!;
        public static ConfigEntry<int> IslandRoadPercentage = null!;

        public void Awake()
        {
            // Register the metadata prefab with Jotunn FIRST - must happen before ZNetScene.Awake
            RegisterMetadataPrefab();
            
            bool saveOnSet = Config.SaveOnConfigSet;
            Config.SaveOnConfigSet = false;

            // Initialize configuration
            RoadWidth = Config.Bind("Roads", "RoadWidth", 4f,
                new ConfigDescription("Width of generated roads in meters",
                    new AcceptableValueRange<float>(2f, 10f)));

            IslandRoadPercentage = Config.Bind("Roads", "IslandRoadPercentage", 50,
                new ConfigDescription("Percentage of islands that will have roads generated (0-100). " +
                    "Islands are selected by size (largest first).",
                    new AcceptableValueRange<int>(0, 100)));

            CustomLocations = Config.Bind("Locations", "CustomLocations", "",
                "Comma-separated list of location names to include in road generation. " +
                "Use this for locations added by Expand World Data or other mods. " +
                "Example: Runestone_Boars,Runestone_Greydwarfs,MerchantCamp");

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

        /// <summary>
        /// Register the metadata prefab with Jotunn's PrefabManager.
        /// This creates an empty, invisible GameObject that will be used to store road data.
        /// Must be called before ZNetScene.Awake so the prefab is registered in time.
        /// </summary>
        private void RegisterMetadataPrefab()
        {
            // Create an empty GameObject - no mesh, no collider, completely invisible
            var prefab = new GameObject(RoadNetworkGenerator.MetadataPrefabName);
            
            // Add ZNetView for ZDO creation and networking
            var nview = prefab.AddComponent<ZNetView>();
            nview.m_persistent = true;
            
            // Wrap in CustomPrefab and register with Jotunn
            var customPrefab = new CustomPrefab(prefab, false);
            PrefabManager.Instance.AddPrefab(customPrefab);
            
            ProceduralRoadsLogger.LogDebug($"Registered metadata prefab: {RoadNetworkGenerator.MetadataPrefabName}");
        }

        private static void ApplyConfiguration()
        {
            RoadNetworkGenerator.RoadWidth = RoadWidth.Value;
            RoadNetworkGenerator.IslandRoadPercentage = IslandRoadPercentage.Value;
            // CustomLocations is parsed at generation time to preserve API registrations
        }

        /// <summary>
        /// Parse the CustomLocations config string into a list of location names.
        /// Called at generation time to merge with API-registered locations.
        /// </summary>
        public static HashSet<string> GetConfigLocationNames()
        {
            var result = new HashSet<string>();
            
            if (string.IsNullOrWhiteSpace(CustomLocations.Value))
                return result;

            string[] locationNames = CustomLocations.Value.Split(',');
            foreach (string name in locationNames)
            {
                string trimmed = name.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    result.Add(trimmed);
                }
            }
            
            return result;
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