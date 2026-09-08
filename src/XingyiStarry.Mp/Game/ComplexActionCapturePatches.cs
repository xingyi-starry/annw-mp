using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Game;

[HarmonyPatch(typeof(UX_Manager), nameof(UX_Manager.proc_UnitsDoEqAction))]
internal static class EquipmentActionCapturePatch
{
    private static bool Prefix(GameTileData lt, List<UnitData> temp, ref IEnumerator __result)
    {
        if (InputGate.ShouldRunOriginal) return true;
        XingyiStarryMpPlugin.Instance?.SubmitCommand(Command(temp, CommandKind.EquipmentAction, lt)); __result = Empty(); return false;
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
        XingyiStarryMpPlugin.Instance?.SubmitCommand(EquipmentActionCapturePatch.Command(new List<UnitData> { unit }, CommandKind.EquipmentMoveAction, lt));
        __result = EquipmentActionCapturePatch.Empty(); return false;
    }
}

[HarmonyPatch(typeof(UX_Manager), nameof(UX_Manager.proc_BuildWithMove))]
internal static class BuildWithMoveCapturePatch
{
    private static bool Prefix(GameTileData lt, UnitData unit, ref IEnumerator __result)
    {
        if (InputGate.ShouldRunOriginal) return true;
        XingyiStarryMpPlugin.Instance?.SubmitCommand(EquipmentActionCapturePatch.Command(new List<UnitData> { unit }, CommandKind.BuildWithMove, lt));
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
