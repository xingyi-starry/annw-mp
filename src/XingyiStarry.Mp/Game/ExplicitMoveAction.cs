using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ANNW;
using HarmonyLib;
using XingyiStarry.Mp.Protocol;
using XingyiStarry.Mp.Session;

namespace XingyiStarry.Mp.Game;

internal static class ExplicitMoveAction
{
    private static OpData? executing;

    internal static IEnumerator ExecuteEquipment(GameCommand command)
    {
        var op = Resolve(command) ?? throw new InvalidOperationException("Explicit move action is no longer valid.");
        if (command.TemplateId.Length != 0) op.action.train_template = UnitTemplate.Acquire(command.TemplateId);
        using var native = new CoroutineStack(UX_Manager.self.proc_UnitsDoEqMoveOp(
            GameTileData.Get(new Inctor2(command.TargetX, command.TargetY)), op.action.owner));
        while (true)
        {
            bool more;
            var previous = executing; executing = op;
            try { more = native.MoveNext(); }
            finally { executing = previous; }
            if (!more) yield break;
            yield return native.Current;
        }
    }

    // Supply only this command's validated choice at the UX iterator's query.
    // Native movement, aiming, waits, action and UX cleanup remain unchanged.
    internal static OpData? Query(EqData eq, Inctor2 target) =>
        executing?.action.owner.eq == eq && executing.action_pos == target ? executing : eq.GetMoveOpAt(target);

    internal static OpData? Resolve(GameCommand command)
    {
        var unit = GS_Battle.self.all_unit.GetUnitByID((int)command.UnitIds[0]);
        var move = new Inctor2(command.UnitTargetXs[0], command.UnitTargetYs[0]);
        var target = new Inctor2(command.TargetX, command.TargetY);
        if (unit.dead || unit.dying || unit.building || unit.in_animation || unit.actioned || unit.moved ||
            !GameTileData.IsValid(move) || !GameTileData.IsValid(target)) return null;
        if (move != unit.pos && !((List<Inctor2>)AccessTools.Method(typeof(UnitData), "GetMoveZone").Invoke(unit, new object[] { true, false, true })).Contains(move)) return null;
        if (GameTileData.Get(move).GetUnit() is { } occupant && occupant != unit) return null;
        var action = unit.GetAction(command.Kind == CommandKind.BuildWithMove ? ActionCate.BUILD : (ActionCate)command.ActionCategory);
        if (action is null) return null;
        var previous = action.train_template;
        try
        {
            if (command.TemplateId.Length != 0)
            {
                if (unit.wp_builder is null) return null;
                var template = UnitTemplateUtils.GetTemplateList(unit.template.sd_equip.build_list, unit.player).Find(t => t.sd_unit.name == command.TemplateId);
                // Native BD builders also expose the MEGA tab in UI_Part_BuildList.
                if (template is null && unit.template.sd_equip.build_list == "BD" && !GS_Battle.self.functions.Querry(GAME_FUNCTION.NoBuildMega))
                    template = UnitTemplateUtils.GetTemplateList("MEGA", unit.player).Find(t => t.sd_unit.name == command.TemplateId);
                if (template is null) return null;
                action.train_template = template;
            }
            else if (command.Kind == CommandKind.BuildWithMove) return null;
            var op = (OpData?)AccessTools.Method(typeof(ActionData), "GetOpInZone").Invoke(action, new object[] { move, target });
            if (op is null || !op.can_do || !op.can_afford) return null;
            op.move_pos = move;
            return op;
        }
        finally { action.train_template = previous; }
    }

}

[HarmonyPatch]
internal static class ExplicitEquipmentMoveQueryPatch
{
    private static MethodBase TargetMethod() => AccessTools.EnumeratorMoveNext(
        AccessTools.Method(typeof(UX_Manager), nameof(UX_Manager.proc_UnitsDoEqMoveOp)));
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var query = AccessTools.Method(typeof(EqData), nameof(EqData.GetMoveOpAt));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(query)) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(ExplicitMoveAction), nameof(ExplicitMoveAction.Query)); }
            yield return instruction;
        }
    }
}
