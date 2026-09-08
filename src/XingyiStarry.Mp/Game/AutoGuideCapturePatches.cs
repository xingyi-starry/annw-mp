using System.Collections.Generic;
using HarmonyLib;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Game;

[HarmonyPatch(typeof(AutoGuideController), nameof(AutoGuideController.TryAutoCommandSelectedUnits))]
internal static class AutoGuideCapturePatches
{
    private static bool Prefix()
    {
        if (InputGate.ShouldRunOriginal) return true;
        SubmitSelected();
        return false;
    }

    internal static void SubmitSelected() => Submit(GS_Battle.self?.selected_units);

    internal static void Submit(IEnumerable<UnitData>? units)
    {
        if (units is null) return;
        var ids = new List<long>();
        foreach (var unit in units)
            if (unit is not null && unit.player == GS_Battle.self.cur_player && !unit.actioned && !unit.in_animation && unit.unit_ai is not null)
                ids.Add(unit.unit_id);
        if (ids.Count != 0)
            XingyiStarryMpPlugin.Instance?.SubmitCommand(new GameCommand { Kind = CommandKind.AutoGuideStart, UnitIds = ids.ToArray() });
    }
}

[HarmonyPatch(typeof(AutoGuideController), nameof(AutoGuideController.TryAutoCommandUnactedUnits))]
internal static class AutoGuideUnactedCapturePatch
{
    private static bool Prefix()
    {
        if (InputGate.ShouldRunOriginal) return true;
        AutoGuideCapturePatches.Submit(GS_Battle.self?.cur_player?.unactioned_units);
        return false;
    }
}
