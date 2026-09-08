using System;
using System.Collections;
using ANNW;
using XingyiStarry.Mp.Protocol;
using XingyiStarry.Mp.Session;

namespace XingyiStarry.Mp.Game;

internal sealed class GameCommandExecutor
{
    public bool IsBusy { get; private set; }

    public void Reset() { IsBusy = false; ExecutionContext.DetachedAuthoritativeExecution = false; }

    public string? Validate(GameCommand command)
    {
        if (GS_Battle.self is null || !GS_Battle.self.game_running) return "Battle is not running.";
        if (IsBusy || GS_Battle.self.unit_busy) return "Another operation is running.";
        if (command.Kind == CommandKind.DebugAddResources || command.Kind == CommandKind.DebugFillSkill)
        {
            if (command.DebugPlayerIndex < -1 || command.DebugPlayerIndex >= GS_Battle.self.all_player.players.Count)
                return "Debug player index is invalid.";
            if (command.Kind == CommandKind.DebugAddResources &&
                (command.DebugMetalDelta < 0 || command.DebugPowerDelta < 0 || command.DebugMetalDelta == 0 && command.DebugPowerDelta == 0))
                return "Debug resource delta is invalid.";
            return null;
        }
        if (command.Kind == CommandKind.EndTurn || command.Kind == CommandKind.UndoMove || command.Kind == CommandKind.Skill || command.Kind == CommandKind.AutoGuideCancel) return null;
        if (command.UnitIds.Length == 0) return "Command contains no units.";
        foreach (var id in command.UnitIds)
        {
            if (id < int.MinValue || id > int.MaxValue) return "Unit id is outside the game range.";
            var unit = GS_Battle.self.all_unit.GetUnitByID((int)id);
            if (unit is null) return "Unit does not exist: " + id;
            if (unit.player != GS_Battle.self.cur_player) return "Unit is not owned by the current player: " + id;
        }
        if (command.Kind == CommandKind.Move && (command.UnitTargetXs.Length != command.UnitIds.Length || command.UnitTargetYs.Length != command.UnitIds.Length)) return "Move target count mismatch.";
        if (command.Kind == CommandKind.Action)
        {
            for (var index = 0; index < command.UnitIds.Length; index++)
            {
                var id = command.UnitIds[index];
                var perUnitTarget = command.UnitTargetXs.Length == command.UnitIds.Length && command.UnitTargetYs.Length == command.UnitIds.Length;
                var tile = GameTileData.Get(new Inctor2(perUnitTarget ? command.UnitTargetXs[index] : command.TargetX, perUnitTarget ? command.UnitTargetYs[index] : command.TargetY));
                if (tile is null) return "Target tile is invalid.";
                var unit = GS_Battle.self.all_unit.GetUnitByID((int)id);
                var action = unit.GetAction((ActionCate)command.ActionCategory);
                if (action is null) return "Unit does not have the requested action.";
                if (!string.IsNullOrEmpty(command.TemplateId)) action.train_template = UnitTemplate.Acquire(command.TemplateId);
                if (action.CanDoAction(tile) != REASON_CANTDO.OK || !action.CanAfford(tile)) return "Original action validation rejected the command.";
            }
        }
        return null;
    }

    public void Start(GameCommand command, ExecutionOrigin origin, Action completed, Action<Exception> failed)
    {
        var validation = Validate(command);
        if (validation is not null) { failed(new InvalidOperationException(validation)); return; }
        IsBusy = true;
        GameController.self.StartCoroutine(Run(command, origin, completed, failed), "XingyiStarryMpOperation");
    }

    public void StartEquipmentMovePrelude(GameCommand command, ExecutionOrigin origin, Action completed, Action<Exception> failed)
    {
        var validation = Validate(command);
        if (validation is not null) { failed(new InvalidOperationException(validation)); return; }
        IsBusy = true;
        GameController.self.StartCoroutine(RunInner(ExecuteEquipmentMovePrelude(command), origin, completed, failed), "XingyiStarryMpMovePrelude");
    }

    public void StartEquipmentMoveResolution(GameCommand command, ExecutionOrigin origin, Action completed, Action<Exception> failed)
    {
        if (IsBusy) { failed(new InvalidOperationException("Another operation is running.")); return; }
        IsBusy = true;
        GameController.self.StartCoroutine(RunInner(ExecuteEquipmentMoveResolution(command), origin, completed, failed), "XingyiStarryMpMoveResolution");
    }

    public void StartBuildMoveResolution(GameCommand command, ExecutionOrigin origin, Action completed, Action<Exception> failed)
    {
        if (IsBusy) { failed(new InvalidOperationException("Another operation is running.")); return; }
        IsBusy = true;
        GameController.self.StartCoroutine(RunInner(ExecuteBuildMoveResolution(command), origin, completed, failed), "XingyiStarryMpBuildResolution");
    }

    private IEnumerator Run(GameCommand command, ExecutionOrigin origin, Action completed, Action<Exception> failed)
    {
        IEnumerator inner;
        var detachedAuthority = command.Kind == CommandKind.AutoGuideStart;
        if (detachedAuthority) ExecutionContext.DetachedAuthoritativeExecution = true;
        using (ExecutionContext.Enter(origin)) inner = Create(command);
        while (true)
        {
            bool more; object? current = null;
            try
            {
                using (ExecutionContext.Enter(origin))
                {
                    more = inner.MoveNext();
                    if (more) current = inner.Current;
                }
            }
            catch (Exception ex) { if (detachedAuthority) ExecutionContext.DetachedAuthoritativeExecution = false; IsBusy = false; failed(ex); yield break; }
            if (!more) break;
            yield return current;
        }
        if (detachedAuthority) ExecutionContext.DetachedAuthoritativeExecution = false;
        IsBusy = false;
        completed();
    }

    private IEnumerator RunInner(IEnumerator inner, ExecutionOrigin origin, Action completed, Action<Exception> failed)
    {
        while (true)
        {
            bool more; object? current = null;
            try
            {
                using (ExecutionContext.Enter(origin))
                {
                    more = inner.MoveNext();
                    if (more) current = inner.Current;
                }
            }
            catch (Exception ex) { IsBusy = false; failed(ex); yield break; }
            if (!more) break;
            yield return current;
        }
        IsBusy = false;
        completed();
    }

    private static IEnumerator Create(GameCommand command)
    {
        switch (command.Kind)
        {
            case CommandKind.Action: return ExecuteAction(command);
            case CommandKind.Move: return ExecuteMove(command);
            case CommandKind.EquipmentAction: return ExecuteEquipmentAction(command);
            case CommandKind.EquipmentMoveAction: return ExecuteEquipmentMoveAction(command);
            case CommandKind.BuildWithMove: return ExecuteBuildWithMove(command);
            case CommandKind.Skill: return UX_Manager.self.proc_SkillDoAction(GameTileData.Get(new Inctor2(command.TargetX, command.TargetY)));
            case CommandKind.UndoMove: return ExecuteUndo();
            case CommandKind.EndTurn: return GameController.self.EndPlayerTurn(GS_Battle.self.cur_player);
            case CommandKind.AutoGuideStart: return ExecuteAutoGuide(command);
            case CommandKind.AutoGuideCancel: return ExecuteAutoGuideCancel();
            case CommandKind.DebugAddResources: return ExecuteDebugAddResources(command);
            case CommandKind.DebugFillSkill: return ExecuteDebugFillSkill(command);
            default: return Unsupported(command.Kind);
        }
    }

    private static IEnumerator ExecuteDebugAddResources(GameCommand command)
    {
        foreach (var player in DebugPlayers(command.DebugPlayerIndex))
        {
            player.metal = SaturatingAdd(player.metal, command.DebugMetalDelta);
            player.power = SaturatingAdd(player.power, command.DebugPowerDelta);
            player.Event_MetalPowerChange?.Invoke();
        }
        yield break;
    }

    private static IEnumerator ExecuteDebugFillSkill(GameCommand command)
    {
        foreach (var player in DebugPlayers(command.DebugPlayerIndex))
            if (player.co_data?.skill is not null) player.co_data.energy = player.co_data.energy_max;
        yield break;
    }

    private static System.Collections.Generic.IEnumerable<Player> DebugPlayers(int playerIndex)
    {
        if (playerIndex >= 0) { yield return GS_Battle.self.all_player.players[playerIndex]; yield break; }
        foreach (var player in GS_Battle.self.all_player.players) if (player is not null) yield return player;
    }

    private static int SaturatingAdd(int value, int delta)
    {
        var result = (long)value + delta;
        return result > int.MaxValue ? int.MaxValue : (int)result;
    }

    private static IEnumerator ExecuteAction(GameCommand command)
    {
        for (var index = 0; index < command.UnitIds.Length; index++)
        {
            var id = command.UnitIds[index];
            var unit = GS_Battle.self.all_unit.GetUnitByID((int)id);
            var action = unit.GetAction((ActionCate)command.ActionCategory);
            var perUnitTarget = command.UnitTargetXs.Length == command.UnitIds.Length && command.UnitTargetYs.Length == command.UnitIds.Length;
            var target = GameTileData.Get(new Inctor2(perUnitTarget ? command.UnitTargetXs[index] : command.TargetX, perUnitTarget ? command.UnitTargetYs[index] : command.TargetY));
            if (action.is_sub_action && action.IsEnabledAsFunction() == command.DesiredToggleState) continue;
            if (!string.IsNullOrEmpty(command.TemplateId)) action.train_template = UnitTemplate.Acquire(command.TemplateId);
            if (command.PassengerUnitId != 0) GS_Battle.self.ux_unload_unit = GS_Battle.self.all_unit.GetUnitByID((int)command.PassengerUnitId);
            yield return GameController.self.ExecuteAction(unit, (ActionCate)command.ActionCategory, target);
        }
    }

    private static IEnumerator ExecuteMove(GameCommand command)
    {
        GS_Battle.self.selected_units.Clear(); UXM_MovePath.ClearMovePathInfos();
        var ordered = UXM_MovePath.GetSortedMoveUnits(); var infos = UXM_MovePath.GetMovePathInfos();
        for (var i = 0; i < command.UnitIds.Length; i++)
        {
            var unit = GS_Battle.self.all_unit.GetUnitByID((int)command.UnitIds[i]);
            var target = new Inctor2(command.UnitTargetXs[i], command.UnitTargetYs[i]);
            var info = new MovePathInfo { unit = unit, from = HexLogic.OffsetToQubic(unit.pos), to = HexLogic.OffsetToQubic(target), cost_remain = unit.move.value };
            var getMovePath = HarmonyLib.AccessTools.Method(typeof(UnitData), "GetMovePath");
            info.path_ipos = (System.Collections.Generic.List<Inctor2>)getMovePath.Invoke(unit, new object?[] { target, null, info, false });
            GS_Battle.self.selected_units.Add(unit); ordered.Add(unit); infos.Add(unit, info);
        }
        yield return UX_Manager.self.proc_UnitsDoMove();
    }

    private static IEnumerator ExecuteEquipmentAction(GameCommand command)
    {
        var units = ResolveUnits(command); var target = GameTileData.Get(new Inctor2(command.TargetX, command.TargetY));
        if (!string.IsNullOrEmpty(command.TemplateId)) GS_Battle.self.ux_unit_template = UnitTemplate.Acquire(command.TemplateId);
        yield return UX_Manager.self.proc_UnitsDoEqAction(target, units);
    }

    private static IEnumerator ExecuteEquipmentMoveAction(GameCommand command)
    {
        var unit = GS_Battle.self.all_unit.GetUnitByID((int)command.UnitIds[0]);
        yield return UX_Manager.self.proc_UnitsDoEqMoveOp(GameTileData.Get(new Inctor2(command.TargetX, command.TargetY)), unit);
    }

    private static IEnumerator ExecuteBuildWithMove(GameCommand command)
    {
        var unit = GS_Battle.self.all_unit.GetUnitByID((int)command.UnitIds[0]); var target = GameTileData.Get(new Inctor2(command.TargetX, command.TargetY));
        var op = new OpData();
        if (command.UnitTargetXs.Length == 1 && command.UnitTargetYs.Length == 1)
            op.move_pos = new Inctor2(command.UnitTargetXs[0], command.UnitTargetYs[0]);
        GS_Battle.self.ux_unit_template = UnitTemplate.Acquire(command.TemplateId);
        yield return UX_Manager.self.proc_BuildWithMove(target, unit, op);
    }

    private static IEnumerator ExecuteEquipmentMovePrelude(GameCommand command)
    {
        var unit = GS_Battle.self.all_unit.GetUnitByID((int)command.UnitIds[0]);
        GS_Battle.self.unit_busy = true;
        BattleEventBus.self.TriggerUnitBusyChanged(isBusy: true);
        if (command.UnitTargetXs.Length == 1 && command.UnitTargetYs.Length == 1)
        {
            var move = new Inctor2(command.UnitTargetXs[0], command.UnitTargetYs[0]);
            if (move != unit.pos) yield return unit.DoMoveWithAni(move);
        }
        unit.in_animation = true;
        UX_Manager.self.CheckUnitsAndSetUXState();
    }

    private static IEnumerator ExecuteEquipmentMoveResolution(GameCommand command)
    {
        var unit = GS_Battle.self.all_unit.GetUnitByID((int)command.UnitIds[0]);
        var target = GameTileData.Get(new Inctor2(command.TargetX, command.TargetY));
        var action = unit.GetAction((ActionCate)command.ActionCategory);
        if (action is null) throw new InvalidOperationException("Equipment move action is no longer available.");
        unit.Event_SetAiming?.Invoke(target.pos);
        unit.in_animation = true;
        yield return action.DoActionAni(target);
        unit.CheckAfterAction(action);
        yield return 0.05f;
        unit.in_animation = false;
        GS_Battle.self.unit_busy = false;
        BattleEventBus.self.TriggerUnitBusyChanged(isBusy: false);
        GS_Battle.self.ux_skill_action = null;
        HarmonyLib.AccessTools.Method(GS_Battle.self.cur_player.GetType(), "UpdateUnactionedUnitCount")?.Invoke(GS_Battle.self.cur_player, null);
        GS_Battle.self.undo_move.ClearUndoableMoveList();
        UX_Manager.self.CheckUnitsAndSetUXState();
    }

    private static IEnumerator ExecuteBuildMoveResolution(GameCommand command)
    {
        var unit = GS_Battle.self.all_unit.GetUnitByID((int)command.UnitIds[0]);
        var target = GameTileData.Get(new Inctor2(command.TargetX, command.TargetY));
        var action = unit.GetAction(ActionCate.BUILD);
        if (action is null) throw new InvalidOperationException("Build action is no longer available.");
        var template = UnitTemplate.Acquire(command.TemplateId);
        GS_Battle.self.ux_unit_template = template;
        GS_Battle.self.ux_state = UX_State.NONE;
        action.train_template = template;
        unit.Event_SetAiming?.Invoke(target.pos);
        unit.in_animation = true;
        yield return action.DoActionAni(target);
        unit.CheckAfterAction(action);
        yield return 0.05f;
        unit.in_animation = false;
        unit.build_planner?.ClearTemplate();
        GS_Battle.self.unit_busy = false;
        BattleEventBus.self.TriggerUnitBusyChanged(isBusy: false);
        GS_Battle.self.ux_skill_action = null;
        HarmonyLib.AccessTools.Method(GS_Battle.self.cur_player.GetType(), "UpdateUnactionedUnitCount")?.Invoke(GS_Battle.self.cur_player, null);
        GS_Battle.self.undo_move.ClearUndoableMoveList();
        UX_Manager.self.CheckUnitsAndSetUXState();
    }

    private static System.Collections.Generic.List<UnitData> ResolveUnits(GameCommand command)
    {
        var result = new System.Collections.Generic.List<UnitData>(command.UnitIds.Length);
        foreach (var id in command.UnitIds) result.Add(GS_Battle.self.all_unit.GetUnitByID((int)id));
        return result;
    }

    private static IEnumerator ExecuteUndo() { GS_Battle.self.undo_move.UndoLastMove(); yield return 0f; }
    private static IEnumerator ExecuteAutoGuide(GameCommand command)
    {
        var units = ResolveUnits(command);
        UnitSpatialSorter.SortUnitListWithHAC(units);
        var method = HarmonyLib.AccessTools.Method(typeof(AutoGuideController), "proc_AutoCommand");
        if (method is null) throw new MissingMethodException(typeof(AutoGuideController).FullName, "proc_AutoCommand");
        return (IEnumerator)method.Invoke(SingletonMono<SS_ANNW_Game>.self.auto_guide, new object[] { units, false });
    }
    private static IEnumerator ExecuteAutoGuideCancel() { GS_Battle.self.auto_guide_canceled = true; yield return 0f; }
    private static IEnumerator Unsupported(CommandKind kind) { throw new NotSupportedException("Command adapter is not implemented: " + kind); }

}
