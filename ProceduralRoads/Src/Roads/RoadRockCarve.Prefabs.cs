using UnityEngine;

namespace ProceduralRoads;

public static partial class RoadRockCarve
{
    /// <summary>The prefab an object was made from, read from its ZDO when it has one (MineRock5.Awake renames a fractured
    /// rock's GameObject, "___MineRock5 m_meshFilter"), else by its object's name; null where the scene has none. The one
    /// way the rock pass names a rock: its policy name (<see cref="PrefabName"/>) and the box a location keeps it by
    /// (RoadRockProbe.KeptOnApproach) alike.</summary>
    public static GameObject? Prefab(GameObject root, ZNetView? view)
    {
        if (ZNetScene.instance == null) return null;
        if (view != null && view.IsValid() && ZNetScene.instance.GetPrefab(view.GetZDO().GetPrefab()) is { } saved) return saved;
        return ZNetScene.instance.GetPrefab(Utils.GetPrefabName(root));
    }

    /// <summary>The name of the prefab an object was made from (<see cref="Prefab"/>), its object's name where the scene has
    /// no such prefab.</summary>
    public static string PrefabName(GameObject root, ZNetView? view) => Prefab(root, view)?.name ?? Utils.GetPrefabName(root);
}
