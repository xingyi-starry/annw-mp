using HarmonyLib;
using System.Collections;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace XingyiStarry.Mp.Game;

[HarmonyPatch]
internal static class AutoGuideCapturePatches
{
    private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(AutoGuideController), nameof(AutoGuideController.TryAutoCommandSelectedUnits));
        yield return AccessTools.Method(typeof(AutoGuideController), nameof(AutoGuideController.TryAutoCommandUnactedUnits));
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var factory = AccessTools.Method(typeof(AutoGuideController), "proc_AutoCommand");
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(factory))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(AutoGuideCapturePatches), nameof(StartPlanner));
            }
            yield return instruction;
        }
    }
    private static IEnumerator StartPlanner(AutoGuideController controller, List<UnitData> units, bool forSelection)
    {
        var native = (IEnumerator)AccessTools.Method(typeof(AutoGuideController), "proc_AutoCommand").Invoke(controller, new object[] { units, forSelection });
        return InputGate.MultiplayerActive && XingyiStarryMpPlugin.Instance is { } plugin ? plugin.BeginLocalAutoGuide(native) : native;
    }
    private static bool Prefix() => !InputGate.MultiplayerActive || InputGate.MaySubmit;
}
