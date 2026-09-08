using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace XingyiStarry.Mp.Game;

[HarmonyPatch]
internal static class GameplayRandomRangePatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        return Methods(typeof(UnitData), "Hurt", "Die")
            .Concat(Methods(typeof(GameTileData), "CreateWreck"))
            .Concat(Methods(typeof(Player), "AutoSetCmdPos"));
    }

    private static IEnumerable<MethodBase> Methods(Type type, params string[] names) => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(method => names.Contains(method.Name));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var floatRange = AccessTools.Method(typeof(UnityEngine.Random), nameof(UnityEngine.Random.Range), new[] { typeof(float), typeof(float) });
        var intRange = AccessTools.Method(typeof(UnityEngine.Random), nameof(UnityEngine.Random.Range), new[] { typeof(int), typeof(int) });
        var prefix = (__originalMethod.DeclaringType?.Name ?? "") + __originalMethod.Name;
        var floatReplacement = AccessTools.Method(typeof(GameplayRandom), prefix + "Float");
        var intReplacement = AccessTools.Method(typeof(GameplayRandom), prefix + "Int");
        foreach (var instruction in instructions)
        {
            if (!instruction.Calls(floatRange) && !instruction.Calls(intRange)) { yield return instruction; continue; }
            instruction.operand = instruction.Calls(floatRange) ? floatReplacement : intReplacement;
            yield return instruction;
        }
    }
}

[HarmonyPatch(typeof(UnitEffect_ShareExp), nameof(UnitEffect_ShareExp.PreAddExp))]
internal static class ShareExperienceShufflePatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var replacement = AccessTools.Method(typeof(GameplayRandom), nameof(GameplayRandom.ShuffleInctor2));
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(ShuffleFunction) && method.Name == "Shuffle")
            {
                instruction.operand = replacement;
                yield return instruction;
            }
            else yield return instruction;
        }
    }
}
