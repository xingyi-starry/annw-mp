using System;
using System.Collections;
using ANNW;
using XingyiStarry.Mp.Protocol;
using XingyiStarry.Mp.Session;

namespace XingyiStarry.Mp.Game;

internal sealed class GameCommandExecutor
{
    public bool IsBusy { get; private set; }

    public string? Validate(GameCommand command)
    {
        if (GS_Battle.self is null || !GS_Battle.self.game_running) return "Battle is not running.";
        if (IsBusy || GS_Battle.self.unit_busy) return "Another operation is running.";
        if (command.Kind == CommandKind.EndTurn || command.Kind == CommandKind.UndoMove || command.Kind == CommandKind.Skill || command.Kind == CommandKind.AutoGuideStart || command.Kind == CommandKind.AutoGuideCancel) return null;
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
                if (action.CanDoAction(tile) != REASON_CANTDO.OK || !action.CanAfford(tile)) return "Original action validation rejected the command.";
            }
        }
        return null;
    }

    public void Start(GameCommand command, ExecutionOrigin origin, Action<byte[]> completed, Action<Exception> failed)
    {
        var validation = Validate(command);
        if (validation is not null) { failed(new InvalidOperationException(validation)); return; }
        IsBusy = true;
        GameController.self.StartCoroutine(Run(command, origin, completed, failed), "XingyiStarryMpOperation");
    }

    private IEnumerator Run(GameCommand command, ExecutionOrigin origin, Action<byte[]> completed, Action<Exception> failed)
    {
        IEnumerator inner;
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
            catch (Exception ex) { IsBusy = false; failed(ex); yield break; }
            if (!more) break;
            yield return current;
        }
        IsBusy = false;
        completed(ComputeStateHash());
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
            case CommandKind.AutoGuideStart: return ExecuteAutoGuide();
            case CommandKind.AutoGuideCancel: return ExecuteAutoGuideCancel();
            default: return Unsupported(command.Kind);
        }
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
        var op = unit.eq.GetMoveOpAt(target.pos);
        if (op is null) throw new InvalidOperationException("Build move operation is no longer valid.");
        GS_Battle.self.ux_unit_template = UnitTemplate.Acquire(command.TemplateId);
        yield return UX_Manager.self.proc_BuildWithMove(target, unit, op);
    }

    private static System.Collections.Generic.List<UnitData> ResolveUnits(GameCommand command)
    {
        var result = new System.Collections.Generic.List<UnitData>(command.UnitIds.Length);
        foreach (var id in command.UnitIds) result.Add(GS_Battle.self.all_unit.GetUnitByID((int)id));
        return result;
    }

    private static IEnumerator ExecuteUndo() { GS_Battle.self.undo_move.UndoLastMove(); yield return 0f; }
    private static IEnumerator ExecuteAutoGuide() { SingletonMono<SS_ANNW_Game>.self.auto_guide.TryAutoCommandSelectedUnits(); yield return 0f; }
    private static IEnumerator ExecuteAutoGuideCancel() { GS_Battle.self.auto_guide_canceled = true; yield return 0f; }
    private static IEnumerator Unsupported(CommandKind kind) { throw new NotSupportedException("Command adapter is not implemented: " + kind); }

    private static byte[] ComputeStateHash()
    {
        return GameStateSerializer.ComputeStateHash();
    }
}
