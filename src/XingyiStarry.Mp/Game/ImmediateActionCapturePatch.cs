using System.Collections.Generic;
using ANNW;
using HarmonyLib;
using UnityEngine.EventSystems;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Game;

[HarmonyPatch(typeof(UI_ActionBtn), nameof(UI_ActionBtn.OnClick))]
internal static class ImmediateActionCapturePatch
{
    private static readonly HashSet<ActionCate> Immediate = new HashSet<ActionCate>
    {
        ActionCate.SHIELD_GEN, ActionCate.OVERDRIVE, ActionCate.SKIP, ActionCate.SLEEP
    };

    private static bool Prefix(UI_ActionBtn __instance, PointerEventData eventData)
    {
        if (InputGate.ShouldRunOriginal) return true;
        if (__instance.cate == ActionCate.AUTO_GUIDE)
        {
            AutoGuideCapturePatches.SubmitSelected(); return false;
        }
        if (!Immediate.Contains(__instance.cate)) return true;
        var units = LocalCommandUnits.Filter(GS_Battle.self.sel_group_units); var ids = new long[units.Count]; var xs = new int[units.Count]; var ys = new int[units.Count];
        if (units.Count == 0) { XingyiStarryMpPlugin.Instance?.NotifyNoEligibleUnits(); return false; }
        for (var i = 0; i < units.Count; i++) { ids[i] = units[i].unit_id; xs[i] = units[i].pos.x; ys[i] = units[i].pos.y; }
        var switchField = AccessTools.Field(typeof(UI_ActionBtn), "switch_state");
        var currentState = switchField is not null && (bool)switchField.GetValue(__instance);
        XingyiStarryMpPlugin.Instance?.SubmitCommand(new GameCommand { Kind = CommandKind.Action, UnitIds = ids, UnitTargetXs = xs, UnitTargetYs = ys, TargetX = xs.Length > 0 ? xs[0] : 0, TargetY = ys.Length > 0 ? ys[0] : 0, ActionCategory = (int)__instance.cate, DesiredToggleState = !currentState });
        return false;
    }
}
