using System.Collections.Generic;

namespace ProceduralRoads;

/// <summary>
/// Public integration surface for other mods that want their world locations
/// considered as road endpoints.
/// </summary>
public static class ProceduralRoadsAPI
{
    public const int DefaultCustomLocationPriority = 80;
    public const int MinLocationPriority = 0;
    public const int MaxLocationPriority = 100;

    /// <summary>
    /// Register a location prefab name as a road endpoint using the default custom priority.
    /// </summary>
    public static void RegisterLocation(string locationName)
    {
        RoadNetworkGenerator.RegisterLocation(locationName);
    }

    /// <summary>
    /// Register a location prefab name as a road endpoint with an explicit priority.
    /// Higher priority endpoints are preferred when an island has many possible endpoints.
    /// </summary>
    public static void RegisterLocation(string locationName, int priority)
    {
        RoadNetworkGenerator.RegisterLocation(locationName, priority);
    }

    /// <summary>
    /// Register multiple location prefab names as road endpoints using a shared priority.
    /// </summary>
    public static void RegisterLocations(IEnumerable<string> locationNames, int priority = DefaultCustomLocationPriority)
    {
        if (locationNames == null)
            return;

        foreach (string locationName in locationNames)
        {
            RegisterLocation(locationName, priority);
        }
    }

    /// <summary>
    /// Remove a location prefab name from the registered road endpoints.
    /// </summary>
    public static void UnregisterLocation(string locationName)
    {
        RoadNetworkGenerator.UnregisterLocation(locationName);
    }

    /// <summary>
    /// Return all registered custom road endpoints and their priorities.
    /// </summary>
    public static IReadOnlyDictionary<string, int> GetRegisteredLocationPriorities()
    {
        return RoadNetworkGenerator.GetRegisteredLocationPriorities();
    }
}
