using System;
using System.Collections.Generic;

namespace ProceduralRoads;

/// <summary>What one clearance box asks the physics for: the part of the rock pass the tests compile.</summary>
public static partial class RoadRockProbe
{
    /// <summary>
    /// The colliders the rock pass judges for one clearance box (RoadRockProbe.Judge), into <paramref name="into"/> (cleared
    /// first): those in the box itself (<paramref name="query"/> asked with false), then a Location's stakes the game snaps to the
    /// ground found in the box's column (<see cref="RockClearance.Column"/> over <see cref="RockClearances.Band"/> of
    /// <paramref name="grounds"/>, asked with true: only those stakes, LocationRock.ClearedOnEitherGround). The stakes stand where
    /// the spawn mode dropped them, on the unlevelled zone (Ghost) or the levelled one (Full), up to 17 m apart at the same x and
    /// z: found by x and z in the box's footprint between the two grounds, the one in the way goes wherever it landed. A box with
    /// no band (no Location's levelling under it, or a walk over the band, as a deck's) asks for no column; a box under the band
    /// (a deep cut) asks for it, so a stake left hanging over the cut is cleared.
    /// </summary>
    internal static void Candidates<T>(RockClearance box, RockClearances.GroundsAt grounds, Func<RockClearance, bool, List<T>> query, List<T> into)
    {
        into.Clear();
        into.AddRange(query(box, false));
        if (RockClearances.Band(box, grounds, out float low, out float high)) into.AddRange(query(box.Column(low, high), true));
    }

    /// <summary>Whether the rock of prefab <paramref name="objectPrefab"/> (its prefab name) at <paramref name="at"/> is one of the
    /// parts of the Location <paramref name="location"/> standing at <paramref name="root"/> turned <paramref name="turn"/>, for the
    /// rock pass (KeptOnApproach): the one part matcher (LocationPrefabLevelling.IsPart), asked with the object's own prefab, as the
    /// reader and the bake ask it.</summary>
    internal static bool? PartOfLocation(string location, UnityEngine.Vector3 root, UnityEngine.Quaternion turn, UnityEngine.Vector3 at, string objectPrefab) =>
        LocationPrefabLevelling.IsPart(location, root, turn, at, objectPrefab.GetStableHashCode());
}
