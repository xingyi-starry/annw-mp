using System.Collections;
using System.Collections.Generic;
using ANNW;
using HarmonyLib;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Game;

[HarmonyPatch(typeof(UX_Manager), nameof(UX_Manager.proc_UnitsDoEqAction))]
internal static class EquipmentActionCapturePatch
{
    private static bool Prefix(GameTileData lt, List<UnitData> temp, ref IEnumerator __result)
    {
        if (InputGate.ShouldRunOriginal) return true;
        var units = LocalCommandUnits.Filter(temp);
        if (units.Count == 0) XingyiStarryMpPlugin.Instance?.NotifyNoEligibleUnits();
        else XingyiStarryMpPlugin.Instance?.SubmitCommand(Command(units, CommandKind.EquipmentAction, lt));
        __result = Empty(); return false;
    }
    internal static GameCommand Command(List<UnitData> units, CommandKind kind, GameTileData target)
    {
        var ids = new long[units.Count]; for (var i = 0; i < ids.Length; i++) ids[i] = units[i].unit_id;
        return new GameCommand { Kind = kind, UnitIds = ids, TargetX = target.pos.x, TargetY = target.pos.y, TemplateId = GS_Battle.self.ux_unit_template?.sd_unit?.name ?? "" };
    }
    internal static IEnumerator Empty() { yield break; }
}

[HarmonyPatch(typeof(UX_Manager), nameof(UX_Manager.proc_UnitsDoEqMoveOp))]
internal static class EquipmentMoveCapturePatch
{
    private static bool Prefix(GameTileData lt, UnitData unit, ref IEnumerator __result)
    {
        if (InputGate.ShouldRunOriginal) return true;
        if (!LocalCommandUnits.Owned(unit) || unit.moved || unit.building || unit.actioned)
        { XingyiStarryMpPlugin.Instance?.NotifyNoEligibleUnits(); __result = EquipmentActionCapturePatch.Empty(); return false; }
        var command = EquipmentActionCapturePatch.Command(new List<UnitData> { unit }, CommandKind.EquipmentMoveAction, lt);
        var op = unit.eq.GetMoveOpAt(lt.pos);
        if (op?.move_pos is Inctor2 move)
        {
            command.UnitTargetXs = new[] { move.x };
            command.UnitTargetYs = new[] { move.y };
        }
        if (op?.action?.sd_action is not null) command.ActionCategory = (int)op.action.sd_action.cate;
        unit.in_animation = false;
        XingyiStarryMpPlugin.Instance?.SubmitCommand(command);
        __result = EquipmentActionCapturePatch.Empty(); return false;
    }
}

[HarmonyPatch(typeof(UX_Manager), nameof(UX_Manager.proc_BuildWithMove))]
internal static class BuildWithMoveCapturePatch
{
    private static bool Prefix(GameTileData lt, UnitData unit, OpData op, ref IEnumerator __result)
    {
        if (InputGate.ShouldRunOriginal) return true;
        if (!LocalCommandUnits.CanMove(unit) || unit.actioned)
        { XingyiStarryMpPlugin.Instance?.NotifyNoEligibleUnits(); __result = EquipmentActionCapturePatch.Empty(); return false; }
        var command = EquipmentActionCapturePatch.Command(new List<UnitData> { unit }, CommandKind.BuildWithMove, lt);
        if (op.move_pos is Inctor2 move)
        {
            command.UnitTargetXs = new[] { move.x };
            command.UnitTargetYs = new[] { move.y };
        }
        XingyiStarryMpPlugin.Instance?.SubmitCommand(command);
        __result = EquipmentActionCapturePatch.Empty(); return false;
    }
}

[HarmonyPatch(typeof(UX_Manager), nameof(UX_Manager.proc_SkillDoAction))]
internal static class SkillCapturePatch
{
    private static bool Prefix(GameTileData lt, ref IEnumerator __result)
    {
        if (InputGate.ShouldRunOriginal) return true;
        XingyiStarryMpPlugin.Instance?.SubmitCommand(new GameCommand { Kind = CommandKind.Skill, TargetX = lt.pos.x, TargetY = lt.pos.y, ActionId = GS_Battle.self.cur_player.co_data.skill?.name ?? "" });
        __result = EquipmentActionCapturePatch.Empty(); return false;
    }
}
