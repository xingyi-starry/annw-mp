using System;
using System.Collections;
using System.Collections.Generic;
using ANNW;
using HarmonyLib;
using XingyiStarry.Mp.Protocol;
using XingyiStarry.Mp.Session;

namespace XingyiStarry.Mp.Game;

internal static class AuthoritativeAutoGuide
{
    private static Exception? batchFailure;
    private static readonly Dictionary<UnitData, bool> batchSkipping = new Dictionary<UnitData, bool>();
    private static OrderedActionBatch? movementOrder;
    internal static void Reset() { batchFailure = null; batchSkipping.Clear(); movementOrder = null; }

    internal static bool TryGetSkipping(UnitData unit, out bool skipping) => batchSkipping.TryGetValue(unit, out skipping);
    internal static bool OrdersMovement(UnitData unit) => movementOrder?.Contains(unit.unit_id) == true;

    internal static IEnumerator FinishMovement(UnitData unit, IEnumerator animation)
    {
        yield return animation;
        // All source positions must first be vacated. Commit arrival and movement effects
        // in the transmitted order, independent of per-client animation timing/frame rate.
        while (movementOrder is not null && !movementOrder.MayFinish(unit.unit_id)) yield return 0f;
    }

    internal static GameCommand ResolveBatch(List<UT_UnitAction> batch)
    {
        var command = new GameCommand { Kind = CommandKind.ResolvedUnitActions, UnitIds = new long[batch.Count] };
        for (var index = 0; index < batch.Count; index++)
        {
            var action = batch[index];
            command.UnitIds[index] = action.unit.unit_id;
            command.ResolvedActions.Add(new ResolvedUnitAction
            {
                UnitId = action.unit.unit_id, Type = (ResolvedActionType)action.type,
                MoveX = action.move_pos.x, MoveY = action.move_pos.y, TargetX = action.action_pos.x, TargetY = action.action_pos.y,
                ActionCategory = action.action is null ? (int)ActionCate.NONE : (int)action.action.sd_action.cate,
                TemplateId = action.create_tp?.sd_unit?.name ?? "", PassengerUnitId = action.unload_unit?.unit_id ?? 0,
                SkipAnimations = false
            });
        }
        return command;
    }

    internal static IEnumerator Execute(GameCommand command)
    {
        var battle = GS_Battle.self;
        var wasGuiding = battle.is_auto_guiding;
        var wasBusy = battle.unit_busy;
        var wasCanceled = battle.auto_guide_canceled;
        Reset();
        if (command.ResolvedActions.Count > 1) movementOrder = new OrderedActionBatch(command.UnitIds);
        battle.is_auto_guiding = true; battle.unit_busy = true; battle.auto_guide_canceled = false;
        BattleEventBus.self.TriggerUnitBusyChanged(true);
        try
        {
            var batch = new List<UT_UnitAction>();
            foreach (var step in command.ResolvedActions)
            {
                var action = GameCommandExecutor.ResolveUnitAction(step);
                batch.Add(action); batchSkipping.Add(action.unit, step.SkipAnimations);
            }
            // This method handles undo grouping, temporary occupation and the native pure-move batch.
            // It executes the supplied actions; it never calls GetAIAction.
            yield return (IEnumerator)AccessTools.Method(typeof(AutoGuideController), "StartAutoGuideBatch").Invoke(
                SingletonMono<SS_ANNW_Game>.self.auto_guide, new object[] { batch, false });
            if (batchFailure is not null) throw new InvalidOperationException("Resolved native batch failed.", batchFailure);
        }
        finally
        {
            batchFailure = null;
            batchSkipping.Clear();
            movementOrder = null;
            battle.is_auto_guiding = wasGuiding; battle.unit_busy = wasBusy; battle.auto_guide_canceled = wasCanceled;
            AccessTools.Method(typeof(Player), "UpdateUnactionedUnitCount").Invoke(battle.cur_player, null);
            BattleEventBus.self.TriggerUnitBusyChanged(wasBusy);
        }
    }

    internal static IEnumerator GuardAction(AutoGuideController controller, UT_UnitAction action, IEnumerator original)
    {
        var order = movementOrder;
        order?.Start(action.unit.unit_id);
        using var stack = new CoroutineStack(original);
        try
        {
            while (true)
            {
                bool more;
                try
                {
                    using (ExecutionContext.Enter(ExecutionOrigin.NestedOriginal)) more = stack.MoveNext();
                }
                catch (Exception ex)
                {
                    batchFailure ??= ex;
                    ((List<UT_UnitAction>)AccessTools.Field(typeof(AutoGuideController), "active_batch").GetValue(controller)).Remove(action);
                    yield break;
                }
                if (!more) yield break;
                yield return stack.Current;
            }
        }
        finally { order?.Complete(action.unit.unit_id); }
    }
}

[HarmonyPatch(typeof(UnitData), nameof(UnitData.proc_MoveAnimation))]
internal static class ResolvedBatchMovementOrderPatch
{
    private static void Postfix(UnitData __instance, ref IEnumerator __result)
    {
        if (InputGate.MultiplayerActive && AuthoritativeAutoGuide.OrdersMovement(__instance))
            __result = AuthoritativeAutoGuide.FinishMovement(__instance, __result);
    }
}

[HarmonyPatch(typeof(AutoGuideController), "StartAutoGuideBatch")]
internal static class ResolveAutoGuideBatchPatch
{
    private static bool Prefix(List<UT_UnitAction> batch, ref IEnumerator __result)
    {
        var plugin = XingyiStarryMpPlugin.Instance;
        if (!InputGate.MultiplayerActive || ExecutionContext.IsAuthoritativeExecution) return true;
        if (plugin?.LocalAutoGuidePlanning == true) __result = plugin.SubmitLocalAutoGuideBatch(batch);
        else __result = Empty();
        return false;
    }
    private static IEnumerator Empty() { yield break; }
}

[HarmonyPatch(typeof(AutoGuideController), "proc_BatchActionSingle")]
internal static class ResolvedAutoGuideActionPatch
{
    private static void Postfix(AutoGuideController __instance, UT_UnitAction action, ref IEnumerator __result)
    {
        if (InputGate.MultiplayerActive && ExecutionContext.IsAuthoritativeExecution)
            __result = AuthoritativeAutoGuide.GuardAction(__instance, action, __result);
    }
}
