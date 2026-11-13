using System;
using System.Collections;
using ANNW;
using XingyiStarry.Mp.Protocol;
using XingyiStarry.Mp.Session;

namespace XingyiStarry.Mp.Game;

internal sealed class GameCommandExecutor
{
    public bool IsBusy { get; private set; }
    private int generation;

    public void Reset() { generation++; IsBusy = false; ExecutionContext.SuppressAiDecision = false; AuthoritativeAutoGuide.Reset(); AttackSettlements.Reset(); }

    public string? Validate(GameCommand command)
    {
        using var localFlags = LocalUnitIntent.CanonicalFlags();
        if (GS_Battle.self is null || !GS_Battle.self.game_running) return "Battle is not running.";
        var resolvedError = CommandRules.ValidateResolvedActions(command);
        if (resolvedError is not null) return resolvedError;
        if (IsBusy || GS_Battle.self.unit_busy && command.Kind != CommandKind.ResolvedUnitActions) return "${notice_another_operation_is_running}";
        // The planning endpoint already ran the native planner. Execute its concrete
        // batch without re-querying action eligibility, movement, targets or costs.
        if (command.Kind == CommandKind.ResolvedUnitActions) return null;
        if (command.Kind == CommandKind.DebugAddResources || command.Kind == CommandKind.DebugFillSkill)
        {
            if (command.DebugPlayerIndex < -1 || command.DebugPlayerIndex >= GS_Battle.self.all_player.players.Count)
                return "Debug player index is invalid.";
            if (command.Kind == CommandKind.DebugAddResources &&
                (command.DebugMetalDelta < 0 || command.DebugPowerDelta < 0 || command.DebugMetalDelta == 0 && command.DebugPowerDelta == 0))
                return "Debug resource delta is invalid.";
            return null;
        }
        if (command.Kind == CommandKind.AiSkill)
        {
            if (!GS_Battle.self.cur_player.is_ai || GS_Battle.self.cur_player.co_data?.skill is null) return "Current AI player cannot cast a skill.";
            if (GameTileData.Get(new Inctor2(command.TargetX, command.TargetY)) is null) return "AI skill target is invalid.";
            return null;
        }
        if (command.Kind == CommandKind.Surrender)
        {
            if (command.TargetX < 0 || command.TargetX >= GS_Battle.self.all_player.players.Count) return "Surrender player index is invalid.";
            if (GS_Battle.self.all_player.players[command.TargetX].defeated) return "${notice_player_has_already_been_defeated}";
            return null;
        }
        if (command.Kind == CommandKind.EndTurn || command.Kind == CommandKind.TurnAdvance || command.Kind == CommandKind.UndoMove || command.Kind == CommandKind.Skill) return null;
        if (command.UnitIds.Length == 0) return "Command contains no units.";
        foreach (var id in command.UnitIds)
        {
            if (id < int.MinValue || id > int.MaxValue) return "Unit id is outside the game range.";
            var unit = GS_Battle.self.all_unit.GetUnitByID((int)id);
            if (unit is null) return "${ui_unit_does_not_exist}" + id;
            if (unit.player != GS_Battle.self.cur_player) return "${ui_unit_is_not_owned_by_the_current_player}" + id;
            if (command.Kind == CommandKind.Move && unit.moved)
                return "${ui_unit_cannot_move_again_this_turn}" + id;
        }
        if (command.Kind == CommandKind.Move && (command.UnitTargetXs.Length != command.UnitIds.Length || command.UnitTargetYs.Length != command.UnitIds.Length)) return "Move target count mismatch.";
        if (command.Kind == CommandKind.SelfDestruct)
        {
            if (command.UnitIds.Length != 1) return "Self-destruct must contain exactly one unit.";
            var unit = GS_Battle.self.all_unit.GetUnitByID((int)command.UnitIds[0]);
            if (unit.dying || !GameAPI.self.CanDoSelfDestruct(unit)) return "${notice_original_self_destruct_validation_rejected_the_command}";
        }
        if (command.Kind == CommandKind.AiUnitAction)
        {
            if (!GS_Battle.self.cur_player.is_ai) return "Current player is not AI controlled.";
            if (command.UnitIds.Length != 1 || command.UnitTargetXs.Length != 1 || command.UnitTargetYs.Length != 1) return "AI action shape is invalid.";
            if (command.AiActionType < (int)UnitActionType.MOVE || command.AiActionType > (int)UnitActionType.ACTION_UNLOAD) return "AI action type is invalid.";
            if (GameTileData.Get(new Inctor2(command.TargetX, command.TargetY)) is null) return "AI action target is invalid.";
        }
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
                if (action is null) return "${notice_unit_does_not_have_the_requested_action}";
                if (unit.actioned && !action.AlwaysCanDo) return "${ui_unit_has_already_acted_this_turn}" + id;
                if (!string.IsNullOrEmpty(command.TemplateId)) action.train_template = UnitTemplate.Acquire(command.TemplateId);
                if (action.CanDoAction(tile) != REASON_CANTDO.OK || !action.CanAfford(tile)) return "${notice_original_action_validation_rejected_the_command}";
            }
        }
        if (command.Kind == CommandKind.EquipmentMoveAction)
        {
            if (command.UnitIds.Length != 1) return "Equipment move action must contain exactly one unit.";
            var unit = GS_Battle.self.all_unit.GetUnitByID((int)command.UnitIds[0]);
            if (unit.actioned || unit.moved) return "${ui_unit_cannot_perform_another_move_action_this_turn}" + command.UnitIds[0];
            if (ExplicitMoveAction.Resolve(command) is null) return "${notice_original_action_validation_rejected_the_command}";
        }
        if (command.Kind == CommandKind.BuildWithMove)
        {
            if (command.UnitIds.Length != 1) return "Build-with-move must contain exactly one unit.";
            var unit = GS_Battle.self.all_unit.GetUnitByID((int)command.UnitIds[0]);
            if (unit.actioned || unit.moved) return "${ui_unit_cannot_build_after_moving_or_acting_this}" + command.UnitIds[0];
            if (ExplicitMoveAction.Resolve(command) is null) return "${notice_original_action_validation_rejected_the_command}";
        }
        return null;
    }

    public void Start(GameCommand command, ExecutionOrigin origin, Action completed, Action<Exception> failed)
    {
        var validation = Validate(command);
        if (validation is not null) { failed(new InvalidOperationException(validation)); return; }
        LocalUnitIntent.BeginAuthority();
        IsBusy = true;
        GameController.self.StartCoroutine(Run(command, origin, completed, failed), "XingyiStarryMpOperation");
    }

    private IEnumerator Run(GameCommand command, ExecutionOrigin origin, Action completed, Action<Exception> failed)
    {
        var executionGeneration = generation;
        IEnumerator inner;
        var tracksClientUnits = origin == ExecutionOrigin.ClientReplay;
        if (tracksClientUnits) MultiplayerUnitStates.BeginAuthorityExecution(command);
        using (ExecutionContext.Enter(origin)) inner = Create(command);
        using var stack = new CoroutineStack(inner);
        while (true)
        {
            if (executionGeneration != generation) yield break;
            bool more; object? current = null;
            try
            {
                using (ExecutionContext.Enter(origin))
                {
                    AttackSettlements.Current?.ThrowIfFailed();
                    more = stack.MoveNext();
                    AttackSettlements.Current?.ThrowIfFailed();
                    if (!more && AttackSettlements.Current?.HasPending == true) { more = true; current = 0f; }
                    else if (!more) AttackSettlements.Current?.CheckComplete();
                    if (more && current is null) current = stack.Current;
                }
            }
            catch (Exception ex)
            {
                if (tracksClientUnits) MultiplayerUnitStates.EndAuthorityExecution(command);
                IsBusy = false; failed(ex); yield break;
            }
            if (!more) break;
            yield return current;
        }
        if (tracksClientUnits) MultiplayerUnitStates.EndAuthorityExecution(command);
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
            case CommandKind.EndTurn: return ExecuteEndTurnOnly(GS_Battle.self.cur_player);
            case CommandKind.ResolvedUnitActions: return AuthoritativeAutoGuide.Execute(command);
            case CommandKind.DebugAddResources: return ExecuteDebugAddResources(command);
            case CommandKind.DebugFillSkill: return ExecuteDebugFillSkill(command);
            case CommandKind.AiUnitAction: return ExecuteAiUnitAction(command);
            case CommandKind.AiSkill: return ExecuteAiSkill(command);
            case CommandKind.TurnAdvance: return ExecuteTurnAdvance();
            case CommandKind.Surrender: return ExecuteSurrender(command);
            case CommandKind.ToggleStandby: return ExecuteToggleState(command, sleep: false);
            case CommandKind.ToggleSleep: return ExecuteToggleState(command, sleep: true);
            case CommandKind.SelfDestruct: return ExecuteSelfDestruct(command);
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

    private static IEnumerator ExecuteAiUnitAction(GameCommand command)
    {
        var unit = GS_Battle.self.all_unit.GetUnitByID((int)command.UnitIds[0]);
        var type = (UnitActionType)command.AiActionType;
        var action = type == UnitActionType.MOVE ? null : unit.GetAction((ActionCate)command.ActionCategory);
        if (type != UnitActionType.MOVE && action is null) throw new InvalidOperationException("AI action is no longer available.");
        var aiAction = new UT_UnitAction
        {
            type = type, unit = unit, original_pos = unit.pos,
            move_pos = new Inctor2(command.UnitTargetXs[0], command.UnitTargetYs[0]),
            action_pos = new Inctor2(command.TargetX, command.TargetY), action = action,
            create_tp = string.IsNullOrEmpty(command.TemplateId) ? null : UnitTemplate.Acquire(command.TemplateId),
            unload_unit = command.PassengerUnitId == 0 ? null : GS_Battle.self.all_unit.GetUnitByID((int)command.PassengerUnitId)
        };
        yield return unit.unit_ai.ExecuteAction(aiAction, command.DesiredToggleState);
    }

    internal static UT_UnitAction ResolveUnitAction(ResolvedUnitAction step)
    {
        var unit = GS_Battle.self.all_unit.GetUnitByID((int)step.UnitId);
        return new UT_UnitAction
        {
            unit = unit, type = (UnitActionType)step.Type, original_pos = unit.pos,
            move_pos = new Inctor2(step.MoveX, step.MoveY), action_pos = new Inctor2(step.TargetX, step.TargetY),
            action = step.Type == ResolvedActionType.Move ? null : unit.GetAction((ActionCate)step.ActionCategory),
            create_tp = step.TemplateId.Length == 0 ? null : UnitTemplate.Acquire(step.TemplateId),
            unload_unit = step.PassengerUnitId == 0 ? null : GS_Battle.self.all_unit.GetUnitByID((int)step.PassengerUnitId)
        };
    }

    private static IEnumerator ExecuteAiSkill(GameCommand command)
    {
        yield return GS_Battle.self.cur_player.co_data.proc_CastSkill(
            GameTileData.Get(new Inctor2(command.TargetX, command.TargetY)), command.DesiredToggleState);
    }

    private static IEnumerator ExecuteSurrender(GameCommand command)
    {
        var player = GS_Battle.self.all_player.players[command.TargetX];
        player.DoWipeOut();
        yield break;
    }

    private static IEnumerator ExecuteEndTurnOnly(Player player)
    {
        player.ending_turn = true;
        player.effect_host.OnTurnEnd();
        foreach (var unit in new System.Collections.Generic.List<UnitData>(player.units)) unit.EndTurn();
        player.last_camera_pos = SingletonMono<SS_ANNW_Game>.self.cam_control.transform.position;
        player.last_camera_zoom = SingletonMono<SS_ANNW_Game>.self.cam_control.zoom_distance;
        if (!GS_Battle.self.functions.Querry(GAME_FUNCTION.NoResLimit))
        {
            if (player.metal > player.storage)
                HarmonyLib.AccessTools.Method(player.statics.GetType(), "RecordResLost")?.Invoke(player.statics, new object[] { player.metal - player.storage });
            player.metal = UnityEngine.Mathf.Min(player.metal, player.storage);
            player.power = UnityEngine.Mathf.Min(player.power, player.storage);
        }
        if (!player.is_ai)
        {
            GS_Battle.self.undo_move.ClearUndoableMoveList();
            UX_Manager.self.ClearUnitSelection();
        }
        yield return GS_Battle.self.OnCOEndTurn(player);
        player.ending_turn = false;
    }

    private static IEnumerator ExecuteTurnAdvance()
    {
        var battle = GS_Battle.self;
        var previousPlayer = battle.cur_player;
        while (true)
        {
            battle.current_co_index++;
            if (battle.current_co_index >= battle.all_player.players.Count)
            {
                if (battle.turns == 0)
                {
                    BattleEventBus.self.TriggerBeforeFirstTurn();
                    BattleEventBus.self.TriggerTurnStarted(0);
                }
                battle.turns++;
                battle.current_co_index = 0;
                HarmonyLib.AccessTools.Method(battle.all_unit.GetType(), "ClearDeadUnits")?.Invoke(battle.all_unit, null);
                battle.last_died_unit = null; battle.last_died_unit_pos = Inctor2.Zero; battle.last_levelup_unit = null;
                BattleEventBus.self.TriggerTurnStarted(battle.turns);
                HarmonyLib.AccessTools.Method(typeof(GS_Battle), "CaptureAllTurnSnaps")?.Invoke(battle, null);
            }
            var candidate = battle.all_player.players[battle.current_co_index];
            if (candidate.fraction != Fraction.NEUTRAL && !candidate.defeated) break;
            if (candidate.defeated && (candidate.units.Count > 0 ||
                (int)(HarmonyLib.AccessTools.Method(typeof(Player), "GetRifCount").Invoke(candidate, null) ?? 0) > 0))
            {
                if (AttackSettlements.Current?.Replaying != true)
                {
                    yield return candidate.proc_DescendResidualUnits();
                    AttackSettlements.Current?.Emit(new Protocol.Wire.SettlementEvent
                    {
                        CleanupCompleted = new Protocol.Wire.CleanupCompletedEvent { PlayerIndex = candidate.index }
                    });
                }
            }
        }

        battle.cur_player = battle.all_player.players[battle.current_co_index];
        // EndTurn and TurnAdvance are separate authoritative operations. A renderer can
        // consume EndTurn's dirty flag while cur_player still points at the old player,
        // after ending_turn and actioned have already been reset. Invalidate that UI
        // again at the actual ownership change; StartPlayerTurn redraws the new side.
        if (previousPlayer is not null)
            foreach (var unit in previousPlayer.units) unit.Event_ReDraw?.Invoke(false);
        var start = GameController.self.StartPlayerTurn(battle.cur_player);
        while (true)
        {
            bool more; object? current = null;
            try
            {
                ExecutionContext.SuppressAiDecision = true;
                more = start.MoveNext();
                if (more) current = start.Current;
            }
            finally { ExecutionContext.SuppressAiDecision = false; }
            if (!more) break;
            yield return current;
        }
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

    private static IEnumerator ExecuteSelfDestruct(GameCommand command)
    {
        var unit = GS_Battle.self.all_unit.GetUnitByID((int)command.UnitIds[0]);
        var die = HarmonyLib.AccessTools.Method(typeof(UnitData), "Die");
        if (die is null) throw new MissingMethodException(typeof(UnitData).FullName, "Die");
        die.Invoke(unit, new object?[] { DieReason.DELETE, 0f, null, null, true, null });
        // DELETE starts the native death coroutine. Wait for its state-changing DescendUnit
        // before the authority captures a safe snapshot or admits the next command.
        while (unit.dying && !unit.dead) yield return 0f;
    }

    private static IEnumerator ExecuteMove(GameCommand command)
    {
        UXM_MovePath.ClearMovePathInfos();
        var ordered = UXM_MovePath.GetSortedMoveUnits(); var infos = UXM_MovePath.GetMovePathInfos();
        for (var i = 0; i < command.UnitIds.Length; i++)
        {
            var unit = GS_Battle.self.all_unit.GetUnitByID((int)command.UnitIds[i]);
            var target = new Inctor2(command.UnitTargetXs[i], command.UnitTargetYs[i]);
            var goal = target;
            var info = new MovePathInfo
            {
                unit = unit, from = HexLogic.OffsetToQubic(unit.pos), to = HexLogic.OffsetToQubic(target), cost_remain = unit.move.value,
                goal = goal, goal_clipped = false, goal_reachable = true, forced_stay = false
            };
            var getMovePath = HarmonyLib.AccessTools.Method(typeof(UnitData), "GetMovePath");
            info.path_ipos = (System.Collections.Generic.List<Inctor2>)getMovePath.Invoke(unit, new object?[] { target, null, info, false });
            ordered.Add(unit); infos.Add(unit, info);
        }
        yield return UX_Manager.self.proc_UnitsDoMove();
    }

    private static IEnumerator ExecuteToggleState(GameCommand command, bool sleep)
    {
        var players = new System.Collections.Generic.HashSet<Player>();
        foreach (var unit in ResolveUnits(command))
        {
            if (sleep)
            {
                unit.sleeping = command.DesiredToggleState;
                unit.skipping = false;
            }
            else
            {
                unit.skipping = command.DesiredToggleState;
                unit.sleeping = false;
            }
            HarmonyLib.AccessTools.Method(typeof(UnitData), "ReDraw")?.Invoke(unit, new object[] { false });
            if (unit.player is not null) players.Add(unit.player);
        }
        var updateCount = HarmonyLib.AccessTools.Method(typeof(Player), "UpdateUnactionedUnitCount");
        foreach (var player in players) updateCount?.Invoke(player, null);
        yield break;
    }

    private static IEnumerator ExecuteEquipmentAction(GameCommand command)
    {
        var units = ResolveUnits(command); var target = GameTileData.Get(new Inctor2(command.TargetX, command.TargetY));
        if (!string.IsNullOrEmpty(command.TemplateId)) GS_Battle.self.ux_unit_template = UnitTemplate.Acquire(command.TemplateId);
        yield return UX_Manager.self.proc_UnitsDoEqAction(target, units);
    }

    private static IEnumerator ExecuteEquipmentMoveAction(GameCommand command)
    {
        yield return ExplicitMoveAction.ExecuteEquipment(command);
    }

    private static IEnumerator ExecuteBuildWithMove(GameCommand command)
    {
        var unit = GS_Battle.self.all_unit.GetUnitByID((int)command.UnitIds[0]); var target = GameTileData.Get(new Inctor2(command.TargetX, command.TargetY));
        var op = ExplicitMoveAction.Resolve(command) ?? throw new InvalidOperationException("Explicit build move is no longer valid.");
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
    private static IEnumerator Unsupported(CommandKind kind) { throw new NotSupportedException("Command adapter is not implemented: " + kind); }

}
