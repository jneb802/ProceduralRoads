// Roads-only addition to the Harmony doubles (Valheim.Testing.Doubles 0.4.0 has the attributes and AccessTools, not the transpiler
// types): HarmonyX 2.9.0's CodeInstruction as far as the PlaceLocations transpiler uses it (an opcode and an operand, and Calls),
// so the transpiler's own code runs here on an instruction list a test writes.
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace HarmonyLib;

public class CodeInstruction
{
    public OpCode opcode;
    public object? operand;
    public List<Label> labels = new();

    public CodeInstruction(OpCode opcode, object? operand = null)
    {
        this.opcode = opcode;
        this.operand = operand;
    }

    public override string ToString() => $"{opcode} {operand}";
}

public static class CodeInstructionExtensions
{
    /// <summary>Whether the instruction calls <paramref name="method"/> (call or callvirt), as HarmonyX's.</summary>
    public static bool Calls(this CodeInstruction code, MethodInfo method) =>
        (code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) && Equals(code.operand, method);
}
