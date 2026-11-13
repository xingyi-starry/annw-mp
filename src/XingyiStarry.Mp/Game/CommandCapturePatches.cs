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
        if (!InputGate.MaySubmit) { __result = Empty(); return false; }
        var moving = new List<UnitData>();
        var plans = new List<MovePathInfo>();
        var staying = new List<UnitData>();
        foreach (var unit in units)
        {
            var info = UXM_MovePath.AcquireMovePathInfo(unit);
            var target = HexLogic.QubicToOffset(info.to);
            if (target == unit.pos) staying.Add(unit);
            else { moving.Add(unit); plans.Add(info); }
        }
        if (moving.Count == 0) return true; // Entire original same-cell setup chain.
        foreach (var unit in units)
        {
            var info = UXM_MovePath.AcquireMovePathInfo(unit);
            // Preserve native goal semantics at the input owner. Goals never enter the wire.
            if (info.goal_clipped && info.goal_reachable && !info.forced_stay)
                AccessTools.Method(typeof(UnitData), "SetMoveGoal").Invoke(unit, new object[] { info.goal });
            else AccessTools.Method(typeof(UnitData), "ClearMoveGoal").Invoke(unit, new object[] { false });
        }
        foreach (var unit in staying) unit.DoMove(unit.pos);
        var ids = new long[moving.Count]; var xs = new int[moving.Count]; var ys = new int[moving.Count];
        for (var i = 0; i < moving.Count; i++)
        {
            var target = HexLogic.QubicToOffset(plans[i].to);
            ids[i] = moving[i].unit_id; xs[i] = target.x; ys[i] = target.y;
        }
        XingyiStarryMpPlugin.Instance?.SubmitCommand(new GameCommand
        {
            Kind = CommandKind.Move, UnitIds = ids, UnitTargetXs = xs, UnitTargetYs = ys
        });
        __result = Empty(); return false;
    }
    private static IEnumerator Empty() { yield break; }
}
