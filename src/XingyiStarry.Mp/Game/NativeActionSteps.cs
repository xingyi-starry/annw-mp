using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ANNW;
using HarmonyLib;
using XingyiStarry.Mp.Protocol.Wire;
using XingyiStarry.Mp.Session;

namespace XingyiStarry.Mp.Game;

// Intercept the virtual settlement calls made by the native action coroutines,
// rather than patching every ActionData subclass or rebuilding their bodies.
internal static class NativeActionSteps
{
    internal static float Prepare(ActionData action, GameTileData tile)
    {
        var operation = AttackSettlements.Current;
        if (operation is null || !ExecutionContext.IsAuthoritativeExecution) return action.PreDoActionCell(tile);
        if (operation.Replaying && NativeActionPlayback.IsPresenting) return 0f;
        var value = new SettlementEvent { SourceUnitId = action.owner?.unit_id ?? 0, PlayerIndex = action.player.index,
            ActionPreparation = new ActionPreparationEvent { ActionName = action.sd_action.name, TargetX = tile.pos.x, TargetY = tile.pos.y } };
        if (!operation.Enter(value, out var scope)) return 0f;
        using (scope)
        {
            try { return action.PreDoActionCell(tile); }
            catch (Exception ex) { scope!.Fail(ex); throw; }
        }
    }
    internal static void Cell(ActionData action, GameTileData tile, Inctor2? origin, float data)
    {
        var operation = AttackSettlements.Current;
        if (operation is null || !ExecutionContext.IsAuthoritativeExecution) { action.DoActionCell(tile, origin, data); return; }
        if (operation.Replaying && NativeActionPlayback.IsPresenting) return;
        var cell = new AttackCellEvent { ActionName = action.sd_action.name, TargetX = tile.pos.x, TargetY = tile.pos.y,
            Data = data, ActionType = action.GetType().Name };
        if (origin.HasValue) { cell.OriginX = origin.Value.x; cell.OriginY = origin.Value.y; }
        var value = new SettlementEvent { SourceUnitId = action.owner?.unit_id ?? 0, PlayerIndex = action.player.index, AttackCell = cell };
        if (!operation.Enter(value, out var scope)) return;
        using (scope)
        {
            try
            {
                var expected = scope!.Value.AttackCell;
                action.DoActionCell(GameTileData.Get(new Inctor2(expected.TargetX, expected.TargetY)),
                    expected.HasOriginX ? new Inctor2(expected.OriginX, expected.OriginY) : (Inctor2?)null, expected.Data);
            }
            catch (Exception ex) { scope!.Fail(ex); throw; }
        }
    }
    internal static Dictionary<Inctor2, ActionPreviewInfo> Targets(ActionData action, Inctor2 position, UnitData attacker, Inctor2? origin, bool preview)
    {
        var operation = AttackSettlements.Current;
        var scope = operation?.ActiveCall;
        if (operation is null || scope?.Value.AttackCell is not { } cell || preview)
            return action.GetDamListPerEffCell(position, attacker, origin, preview);
        if (!operation.Replaying)
        {
            var original = action.GetDamListPerEffCell(position, attacker, origin, preview);
            foreach (var pair in original)
            {
                var item = new AttackTargetResult { Position = new ActionMovePosition { X = pair.Key.x, Y = pair.Key.y }, Damage = pair.Value.value };
                if (pair.Value.target_sheild is { } shield) item.Shield = new AttackShieldRecipient { CasterUnitId = shield.caster_unit.unit_id };
                else item.Unit = new AttackUnitRecipient { UnitId = pair.Value.target_unit.unit_id };
                cell.Targets.Add(item);
            }
            return original;
        }
        // Native damage filtering can consume effects or choose an escape before Hurt.
        // Replay those host callbacks before returning the host's ordered target list.
        scope.ApplyTargetFilters();
        var targets = new Dictionary<Inctor2, ActionPreviewInfo>();
        foreach (var item in cell.Targets)
        {
            if (item.Position is null || !item.HasDamage) throw new System.IO.InvalidDataException("Incomplete native attack target.");
            var pos = new Inctor2(item.Position.X, item.Position.Y);
            var info = new ActionPreviewInfo { pos = pos, value = item.Damage, type = ActionPreviewInfoType.DAMAGE };
            if (item.Shield is { } selected)
            {
                var caster = GS_Battle.self.all_unit.GetUnitByID(checked((int)selected.CasterUnitId));
                info.target_sheild = new FieldShield { caster_unit = caster, gtd = GameTileData.Get(pos) };
            }
            else if (item.Unit is { } selectedUnit) info.target_unit = GS_Battle.self.all_unit.GetUnitByID(checked((int)selectedUnit.UnitId));
            else throw new System.IO.InvalidDataException("Attack target has no recipient.");
            targets.Add(pos, info);
        }
        return targets;
    }
    internal static void ActionEnd(Action? callback, ActionData action)
    {
        var operation = AttackSettlements.Current;
        if (operation is null || !ExecutionContext.IsAuthoritativeExecution) { callback?.Invoke(); return; }
        if (operation.Replaying && NativeActionPlayback.IsPresenting) return;
        var value = new SettlementEvent { SourceUnitId = action.owner?.unit_id ?? 0, PlayerIndex = action.player.index,
            ActionCallbacks = new ActionCallbacksEvent { ActionName = action.sd_action.name } };
        if (!operation.Enter(value, out var scope)) return;
        using (scope)
        {
            try { callback?.Invoke(); }
            catch (Exception ex) { scope!.Fail(ex); throw; }
        }
    }
    internal static void Actioned(BattleEventBus bus, UnitData unit, ActionCate category)
    { if (AttackSettlements.Current?.Replaying != true || !NativeActionPlayback.IsPresenting) bus.TriggerUnitActioned(unit, category); }
}

[HarmonyPatch]
internal static class NativeActionStepCallsPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var name in new[] { "DoAction_Normal", "DoAction_MultiTarget", "DoAction_Parallel", "DoAction_Field", "DoAction_Skill", "proc_DoActionCell", "DoActionAni" })
            yield return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(ActionData), name));
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        foreach (var instruction in instructions)
        {
            string? replacement = null;
            if (instruction.Calls(AccessTools.Method(typeof(ActionData), nameof(ActionData.PreDoActionCell)))) replacement = nameof(NativeActionSteps.Prepare);
            else if (instruction.Calls(AccessTools.Method(typeof(ActionData), nameof(ActionData.DoActionCell)))) replacement = nameof(NativeActionSteps.Cell);
            else if (instruction.Calls(AccessTools.Method(typeof(Action), nameof(Action.Invoke))))
            {
                var load = new CodeInstruction(OpCodes.Ldarg_0);
                instruction.MoveLabelsTo(load);
                load.blocks.AddRange(instruction.blocks); instruction.blocks.Clear();
                yield return load;
                yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(__originalMethod.DeclaringType, "<>4__this")
                    ?? throw new MissingFieldException("Native action iterator has no action reference."));
                replacement = nameof(NativeActionSteps.ActionEnd);
            }
            else if (instruction.Calls(AccessTools.Method(typeof(BattleEventBus), nameof(BattleEventBus.TriggerUnitActioned)))) replacement = nameof(NativeActionSteps.Actioned);
            if (replacement is not null) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(NativeActionSteps), replacement); }
            yield return instruction;
        }
    }
}

[HarmonyPatch(typeof(Action_Attack), nameof(Action_Attack.DoActionCell))]
internal static class NativeAttackTargetsPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original = AccessTools.Method(typeof(ActionData), "GetDamListPerEffCell");
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(original)) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(NativeActionSteps), nameof(NativeActionSteps.Targets)); }
            yield return instruction;
        }
    }
}
