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
        var data = GS_Battle.self; var ids = new long[temp.Count];
        for (var i = 0; i < temp.Count; i++) ids[i] = temp[i].unit_id;
        XingyiStarryMpPlugin.Instance?.SubmitCommand(new GameCommand { Kind = CommandKind.Action, UnitIds = ids, TargetX = lt.pos.x, TargetY = lt.pos.y, ActionCategory = (int)data.ux_action_cate, TemplateId = data.ux_unit_template?.sd_unit?.name ?? "", PassengerUnitId = data.ux_unload_unit?.unit_id ?? 0 });
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
        var units = UXM_MovePath.GetSortedMoveUnits();
        if (units.Count == 0) units = GS_Battle.self.selected_units;
        var ids = new long[units.Count]; var xs = new int[units.Count]; var ys = new int[units.Count];
        for (var i = 0; i < units.Count; i++)
        {
            var info = UXM_MovePath.AcquireMovePathInfo(units[i]); var target = HexLogic.QubicToOffset(info.to);
            ids[i] = units[i].unit_id; xs[i] = target.x; ys[i] = target.y;
        }
        XingyiStarryMpPlugin.Instance?.SubmitCommand(new GameCommand { Kind = CommandKind.Move, UnitIds = ids, UnitTargetXs = xs, UnitTargetYs = ys });
        __result = Empty(); return false;
    }
    private static IEnumerator Empty() { yield break; }
}
