// Roads' part of the WearNTear double (Valheim.Testing.Doubles): the shown health and the visual update a piece placed
// part worn is brought up to (StructurePlacement.ShowHealth), read by reflection as in the game. A test can make that
// update fail, as a step of the mod's own after the piece's object is out.
public partial class WearNTear
{
    private float m_healthPercentage = 1f;
    /// <summary>The health the piece shows.</summary>
    internal float ShownHealth => m_healthPercentage;
    /// <summary>While set, the visual update throws.</summary>
    internal static bool FailVisual;
    private void UpdateVisual(bool triggerEffects)
    {
        if (FailVisual) throw new System.InvalidOperationException("the piece failed after it was placed");
    }
}
