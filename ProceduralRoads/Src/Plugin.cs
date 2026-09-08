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
        internal const string ModVersion = "1.4.3";
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
        public static ConfigEntry<bool> GenerateRoadsOnLoad = null!;
        public static ConfigEntry<int> PathfindingMaxIterations = null!;
        public static ConfigEntry<int> MaxLocationsPerIsland = null!;
        public static ConfigEntry<bool> FordsEnabled = null!;
        public static ConfigEntry<float> FordWadeWeight = null!;
        public static ConfigEntry<float> FordRaiseWeight = null!;
        public static ConfigEntry<float> FordSpanWeight = null!;
        public static ConfigEntry<bool> BridgesEnabled = null!;
        public static ConfigEntry<float> BridgeCostFixed = null!;
        public static ConfigEntry<float> BridgeCostPerMeter = null!;

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

            PathfindingMaxIterations = Config.Bind("Roads", "PathfindingMaxIterations", 10000,
                new ConfigDescription("Maximum number of iterations for each road segment's pathfinding algorithm. " +
                    "Higher values will generate more roads but increase generation time. " +
                    "Lower values will speed up generation time but cause less roads to generate.",
                    new AcceptableValueRange<int>(1000, 100000)));

            MaxLocationsPerIsland = Config.Bind("Roads", "MaxLocationsPerIsland", 12,
                new ConfigDescription("Maximum number of locations that can be connected by roads on a single island. " +
                    "Higher values allow more roads on large islands.",
                    new AcceptableValueRange<int>(2, 30)));

            FordsEnabled = Config.Bind("Fords", "Enabled", false,
                "PROTOTYPE, off by default. Roads may ford knee-deep rivers: the pathfinder can jump a river up to 48 m wide " +
                "whose water is no deeper than 0.8 m, between near-level banks, and swamp roads wade their shallows. " +
                "A ford is waded (the road painted through the water at the ground's height) or raised (the road leveled " +
                "up above the water) by the weights below. Decides where roads go when a network is generated.");

            FordWadeWeight = Config.Bind("Fords", "WadeWeight", RoadConstants.DefaultFordStyleWeight,
                new ConfigDescription("Relative odds that a ford is WADED, the road painted through the shallows at ground height " +
                    "(offered only where the water is ankle deep, always in swamps). 0 disables the style; with equal weights each site picks evenly among the styles it allows.",
                    new AcceptableValueRange<float>(0f, 100f)));

            FordRaiseWeight = Config.Bind("Fords", "RaiseWeight", RoadConstants.DefaultFordStyleWeight,
                new ConfigDescription("Relative odds that a ford is RAISED, the road leveled up through the shallows. " +
                    "Always allowed, and used whenever no other style is.",
                    new AcceptableValueRange<float>(0f, 100f)));

            FordSpanWeight = Config.Bind("Fords", "SpanWeight", RoadConstants.DefaultFordStyleWeight,
                new ConfigDescription("With Bridges/Enabled: relative odds that a ford is SPANNED by a short low footbridge with a step at each end " +
                    "(offered only where the crossing is at least 6 m wide).",
                    new AcceptableValueRange<float>(0f, 100f)));

            BridgesEnabled = Config.Bind("Bridges", "Enabled", false,
                "PROTOTYPE, off by default. Roads may cross rivers too long or too deep to ford on wooden bridges: the pathfinder can jump " +
                "a river (up to 128 m, between near-level banks) at the cost below, the water is left unpaved, and a ruined wooden " +
                "bridge built from vanilla pieces (post pairs and crossbeams down to the riverbed, a plank deck, a stair at each end, " +
                "a gap left open over the deepest water so boats still pass) is spawned when the zone generates. No bridges in or to " +
                "the Mistlands yet (its terrain is too steep and jagged; a later pass). Decides where roads go when a network is " +
                "generated; a world generated with bridges keeps them. Bridges are dear, so they show up mostly where a river has no " +
                "way around; raise PathfindingMaxIterations to let the pathfinder find them on large islands.");

            BridgeCostFixed = Config.Bind("Bridges", "CostFixed", RoadConstants.DefaultBridgeCostFixed,
                new ConfigDescription("Pathfinding cost of a bridge, fixed part (with Bridges/Enabled). " +
                    "For scale: easy ground costs about 1 per metre of road, rough or steep ground 1000-2000 per 8 m cell. " +
                    "Lower = more bridges, higher = roads go around instead.",
                    new AcceptableValueRange<float>(0f, 1000000f)));

            BridgeCostPerMeter = Config.Bind("Bridges", "CostPerMeter", RoadConstants.DefaultBridgeCostPerMeter,
                new ConfigDescription("Pathfinding cost of a bridge per metre of span, on top of CostFixed. " +
                    "Makes long bridges dearer than short ones.",
                    new AcceptableValueRange<float>(0f, 10000f)));

            GenerateRoadsOnLoad = Config.Bind("Debug", "GenerateRoadsOnLoad", true,
                "Generate the road network when a world without persisted roads loads. Off, the world stays " +
                "road-free until the road_generate or road_regen_island console command asks; for validation " +
                "loops that iterate on one island. Leave on for normal play.");

            CustomLocations = Config.Bind("Locations", "CustomLocations", "",
                "Comma-separated list of location names to include in road generation. " +
                "Use this for locations added by Expand World Data or other mods. " +
                "Example: Runestone_Boars,Runestone_Greydwarfs,MerchantCamp");

            // Apply config to road generator
            ApplyConfiguration();

            Assembly assembly = Assembly.GetExecutingAssembly();
            _harmony.PatchAll(assembly);
            SetupWatcher();

            Analytics.Init(Config, ModGUID, ModVersion);


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
            RoadNetworkGenerator.GenerateOnLoad = GenerateRoadsOnLoad.Value;
            RoadNetworkGenerator.MaxLocationsPerIsland = MaxLocationsPerIsland.Value;
            RoadPathfinder.MaxIterations = PathfindingMaxIterations.Value;
            RoadPathfinder.FordsEnabled = FordsEnabled.Value;
            RoadCrossingDetector.SetFordStyleWeights(FordWadeWeight.Value, FordRaiseWeight.Value, FordSpanWeight.Value);
            RoadPathfinder.BridgesEnabled = BridgesEnabled.Value;
            RoadPathfinder.ConfiguredBridgeCostFixed = BridgeCostFixed.Value;
            RoadPathfinder.ConfiguredBridgeCostPerMeter = BridgeCostPerMeter.Value;
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