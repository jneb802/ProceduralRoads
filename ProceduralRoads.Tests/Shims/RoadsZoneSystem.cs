// Roads' part of the ZoneSystem double (Valheim.Testing.Doubles): the game's set of generated zones, which the mod
// reads through the publicized assembly, is the double's Generated set.
public partial class ZoneSystem
{
    public System.Collections.Generic.HashSet<Vector2s> m_generatedZones => Generated;
}
