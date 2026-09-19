using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Game;

[HarmonyPatch(typeof(UX_Manager), "proc_UnitsDoAction")]
internal static class UnitsDoActionCapturePatch
{
    private static bool Prefix(GameTileData lt, List<UnitData> temp, ref IEnumerator __result)
    {
        if (InputGate.ShouldRunOriginal) return true;
        var data = GS_Battle.self; var units = LocalCommandUnits.Filter(temp);
        units.RemoveAll(unit => unit.GetAction(data.ux_action_cate) is not ActionData action || unit.actioned && !action.AlwaysCanDo);
        if (units.Count == 0) XingyiStarryMpPlugin.Instance?.NotifyNoEligibleUnits();
        else
        {
            var ids = new long[units.Count];
            for (var i = 0; i < units.Count; i++) ids[i] = units[i].unit_id;
            XingyiStarryMpPlugin.Instance?.SubmitCommand(new GameCommand { Kind = CommandKind.Action, UnitIds = ids, TargetX = lt.pos.x, TargetY = lt.pos.y, ActionCategory = (int)data.ux_action_cate, TemplateId = data.ux_unit_template?.sd_unit?.name ?? "", PassengerUnitId = data.ux_unload_unit?.unit_id ?? 0 });
        }
        __result = Empty(); return false;
    }
    private static IEnumerator Empty() { yield break; }
}

[HarmonyPatch(typeof(UX_Manager), nameof(UX_Manager.proc_UnitsDoMove))]
internal static class UnitsDoMoveCapturePatch
{
    private static bool Prefix(ref IEnumerator __result)
    {
        if (InputGate.ShouldRunOriginal) return true;
        var ordered = UXM_MovePath.GetSortedMoveUnits();
        var units = LocalCommandUnits.Filter(ordered.Count == 0 ? GS_Battle.self.selected_units : ordered, move: true);
        if (units.Count == 0)
        {
            XingyiStarryMpPlugin.Instance?.NotifyNoEligibleUnits();
            __result = Empty(); return false;
        }
        var ids = new long[units.Count]; var xs = new int[units.Count]; var ys = new int[units.Count];
        for (var i = 0; i < units.Count; i++)
        {
            var info = UXM_MovePath.AcquireMovePathInfo(units[i]); var target = HexLogic.QubicToOffset(info.to);
            ids[i] = units[i].unit_id; xs[i] = target.x; ys[i] = target.y;
        }
        var firstInfo = units.Count > 0 ? UXM_MovePath.AcquireMovePathInfo(units[0]) : null;
        XingyiStarryMpPlugin.Instance?.SubmitCommand(new GameCommand
        {
            Kind = CommandKind.Move, UnitIds = ids, UnitTargetXs = xs, UnitTargetYs = ys,
            TargetX = firstInfo?.goal.x ?? 0, TargetY = firstInfo?.goal.y ?? 0,
            DesiredToggleState = units.Exists(unit => UXM_MovePath.AcquireMovePathInfo(unit).forced_stay)
        });
        __result = Empty(); return false;
    }
    private static IEnumerator Empty() { yield break; }
}
