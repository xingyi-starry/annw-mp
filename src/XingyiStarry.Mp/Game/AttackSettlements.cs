using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ANNW;
using HarmonyLib;
using XingyiStarry.Mp.Protocol;
using XingyiStarry.Mp.Session;

namespace XingyiStarry.Mp.Game;

// Only native detached attack cells are intercepted. Damage/effects still use Action_Attack.
internal sealed class AttackSettlements
{
    internal static AttackSettlements? Current { get; private set; }
    private static Batch? steppingBatch;
    private static Death? steppingDeath;
    private static bool steppingCell;
    private static bool applyingDeath;
    private readonly Action<AttackSettlement>? publish;
    private readonly Queue<AttackSettlement> events = new Queue<AttackSettlement>();
    private int nextBatch;
    private int nextEvent = 1;
    private int activeBatches;
    private Exception? failure;
    private bool canceled;
    internal bool InputComplete { get; set; }
    private bool Replaying => publish is null;

    private AttackSettlements(Action<AttackSettlement>? publish) => this.publish = publish;
    internal static void BeginRecording(Action<AttackSettlement> publish) { Reset(); Current = new AttackSettlements(publish); }
    internal static void BeginReplay() { Reset(); Current = new AttackSettlements(null); }
    internal static void Reset() { if (Current is not null) Current.canceled = true; Current = null; }

    internal void Append(int ordinal, AttackSettlement value)
    {
        if (!Replaying || InputComplete || ordinal != nextEvent++) throw new InvalidOperationException("Attack settlement sequence mismatch.");
        events.Enqueue(value);
    }

    internal void CheckComplete()
    {
        ThrowIfFailed();
        if (activeBatches != 0 || events.Count != 0) throw new InvalidOperationException("Attack settlements remain unfinished.");
    }

    internal void ThrowIfFailed()
    {
        if (failure is not null) throw new InvalidOperationException("Detached attack coroutine failed.", failure);
    }

    internal static bool IsDetachedAttack(ActionData? action) => action?.GetType() == typeof(Action_Attack) &&
        (action.sd_action.mul_tar > 0 || action.sd_action.traj == TrajShape.PARREL);

    // Other composite commands retain buffered playback until their unaudited random prelude is available.
    internal static bool CanPlayLive(GameCommand command)
    {
        if (command.UnitIds.Length != 1 || GS_Battle.self?.all_unit is null) return false;
        var unit = GS_Battle.self.all_unit.GetUnitByID((int)command.UnitIds[0]);
        if (unit is null) return false;
        ActionData? action = null;
        if (command.Kind == CommandKind.EquipmentAction)
            action = unit.eq.GetActionOpAt(new Inctor2(command.TargetX, command.TargetY))?.action;
        else if (command.Kind == CommandKind.Action)
            action = unit.GetAction((ActionCate)command.ActionCategory);
        else if (command.Kind == CommandKind.AiUnitAction && command.AiActionType == (int)UnitActionType.ACTION_NORMAL &&
                 command.UnitTargetXs.Length == 1 && command.UnitTargetYs.Length == 1 &&
                 command.UnitTargetXs[0] == unit.pos.x && command.UnitTargetYs[0] == unit.pos.y)
            action = unit.GetAction((ActionCate)command.ActionCategory);
        else if (command.Kind == CommandKind.ResolvedUnitActions && command.ResolvedActions.Count == 1)
        {
            var step = command.ResolvedActions[0];
            if (step.Type == ResolvedActionType.Action && step.MoveX == unit.pos.x && step.MoveY == unit.pos.y)
                action = unit.GetAction((ActionCate)step.ActionCategory);
        }
        return IsDetachedAttack(action);
    }

    private AttackSettlement Event(Batch batch, AttackSettlementKind kind) => new AttackSettlement
    {
        Kind = kind, BatchId = batch.Id, UnitId = batch.Action.owner.unit_id, ActionName = batch.Action.sd_action.name
    };

    private IEnumerator RunBatch(ActionData action, IEnumerator original)
    {
        var batch = new Batch(this, ++nextBatch, action);
        activeBatches++;
        using var stack = new CoroutineStack(original);
        try
        {
            if (!Replaying) publish!(Event(batch, AttackSettlementKind.BatchStarted));
            while (!canceled)
            {
                ThrowIfFailed();
                if (Replaying)
                {
                    Pump(batch);
                    if (!batch.Started) { yield return 0f; continue; }
                }
                bool more;
                var previous = steppingBatch;
                try
                {
                    steppingBatch = batch;
                    more = stack.MoveNext();
                }
                finally { steppingBatch = previous; }
                if (!more) break;
                yield return stack.Current;
            }
            if (canceled) yield break;
            batch.NativeFinished = true;
            if (Replaying)
            {
                while (!canceled && (!batch.Ended || !InputComplete)) { ThrowIfFailed(); Pump(batch); yield return 0f; }
            }
            else
            {
                // Large-unit death coroutines contain gameplay writes after their explosion animation.
                while (!canceled && batch.PendingDeaths != 0) { ThrowIfFailed(); yield return 0f; }
                ThrowIfFailed();
                if (!canceled) publish!(Event(batch, AttackSettlementKind.BatchEnded));
            }
        }
        finally { batch.Closed = true; activeBatches--; }
    }

    private void Pump(Batch batch)
    {
        while (events.Count != 0)
        {
            var value = events.Peek();
            if (value.BatchId != batch.Id) throw new InvalidOperationException("Attack batch id mismatch.");
            if (value.Kind == AttackSettlementKind.BatchStarted)
            {
                if (batch.Started || value.UnitId != batch.Action.owner.unit_id || value.ActionName != batch.Action.sd_action.name)
                    throw new InvalidOperationException("Attack batch identity mismatch.");
                events.Dequeue(); batch.Started = true;
            }
            else if (!batch.Started || batch.Ended) throw new InvalidOperationException("Attack event outside its batch.");
            else if (value.Kind == AttackSettlementKind.Cell)
            {
                if (value.UnitId != batch.Action.owner.unit_id || value.ActionName != batch.Action.sd_action.name)
                    throw new InvalidOperationException("Attack cell identity mismatch.");
                var pos = new Inctor2(value.TargetX, value.TargetY);
                // A local impact is not permission to settle. Wait only for launch, never for local flight time.
                // A prior authoritative kill may also make the native local launcher skip this cell.
                if (!batch.NativeFinished && !batch.Launched.Contains(pos)) return;
                var tile = GameTileData.Get(pos) ?? throw new InvalidOperationException("Attack settlement tile is missing.");
                events.Dequeue();
                var previous = steppingBatch;
                try
                {
                    steppingBatch = batch;
                    using (ExecutionContext.Enter(ExecutionOrigin.ClientReplay))
                        batch.Action.DoActionCell(tile, new Inctor2(value.OriginX, value.OriginY), value.Data);
                }
                finally { steppingBatch = previous; }
            }
            else if (value.Kind == AttackSettlementKind.Death)
            {
                if (!batch.Deaths.TryGetValue(value.UnitId, out var death) || death.Committed)
                    throw new InvalidOperationException("Attack death event has no pending victim.");
                events.Dequeue();
                var previous = steppingBatch;
                try
                {
                    steppingBatch = batch;
                    applyingDeath = true;
                    using (ExecutionContext.Enter(ExecutionOrigin.ClientReplay))
                    {
                        GameAPI.self.DescendUnit(death.Victim);
                        death.Victim.TriggerDieEvent(death.Reason, death.Attacker, death.Killer);
                    }
                    death.Committed = true; batch.PendingDeaths--;
                }
                finally { applyingDeath = false; steppingBatch = previous; }
            }
            else if (value.Kind == AttackSettlementKind.BatchEnded)
            {
                if (batch.PendingDeaths != 0) throw new InvalidOperationException("Attack ended before its death events.");
                if (!batch.NativeFinished) return;
                events.Dequeue(); batch.Ended = true;
                return;
            }
            else throw new InvalidOperationException("Unknown attack settlement event.");
        }
        if (InputComplete && !batch.Ended) throw new InvalidOperationException("Attack settlement log ended before its batch.");
    }

    private static IEnumerator Guard(IEnumerator original, Batch batch, bool cell, Death? death = null)
    {
        using var stack = new CoroutineStack(original);
        while (!batch.Owner.canceled && (!batch.Closed || death?.Committed == true) && batch.Owner.failure is null)
        {
            bool more;
            var previousBatch = steppingBatch; var previousCell = steppingCell; var previousDeath = steppingDeath;
            try
            {
                steppingBatch = batch; steppingCell = cell; steppingDeath = death;
                using (ExecutionContext.Enter(batch.Owner.Replaying ? ExecutionOrigin.ClientReplay : ExecutionOrigin.HostAuthority))
                    more = stack.MoveNext();
            }
            catch (Exception ex) { batch.Owner.failure ??= ex; yield break; }
            finally { steppingBatch = previousBatch; steppingCell = previousCell; steppingDeath = previousDeath; }
            if (!more) yield break;
            yield return stack.Current;
        }
    }

    internal static void WrapBatch(ActionData action, ref IEnumerator result)
    {
        if (Current is { } operation && ExecutionContext.IsAuthoritativeExecution && IsDetachedAttack(action)) result = operation.RunBatch(action, result);
    }

    internal static void WrapCell(GameTileData target, ref IEnumerator result)
    {
        if (steppingBatch is not { } batch) return;
        batch.Launched.Add(target.pos);
        result = Guard(result, batch, true);
    }

    internal static bool AllowCell() => !steppingCell || steppingBatch?.Owner.Replaying != true;

    internal static void RecordCell(Action_Attack action, GameTileData target, Inctor2? origin, float data)
    {
        if (!steppingCell || steppingBatch is not { } batch || batch.Owner.Replaying) return;
        if (batch.Action != action || !origin.HasValue) throw new InvalidOperationException("Detached attack cell context mismatch.");
        var value = batch.Owner.Event(batch, AttackSettlementKind.Cell);
        value.TargetX = target.pos.x; value.TargetY = target.pos.y;
        value.OriginX = origin.Value.x; value.OriginY = origin.Value.y; value.Data = data;
        batch.Owner.publish!(value);
    }

    internal static void WrapDeath(UnitData victim, DieReason reason, UnitData attacker, Player killer, ref IEnumerator result)
    {
        if (steppingBatch is not { } batch || victim.template.chassis.size <= 1 || reason != DieReason.COMBAT) return;
        var death = new Death(batch, victim, reason, attacker, killer);
        batch.Deaths.Add(victim.unit_id, death); batch.PendingDeaths++;
        result = Guard(result, batch, false, death);
    }

    internal static bool AllowDeath() => applyingDeath || steppingDeath?.Batch.Owner.Replaying != true;
    internal static void RecordDeath(UnitData victim)
    {
        if (steppingDeath is not { } death || death.Victim != victim || death.Batch.Owner.Replaying || death.Committed) return;
        death.Committed = true; death.Batch.PendingDeaths--;
        var value = death.Batch.Owner.Event(death.Batch, AttackSettlementKind.Death);
        value.UnitId = death.Victim.unit_id;
        death.Batch.Owner.publish!(value);
    }

    private sealed class Batch
    {
        internal readonly AttackSettlements Owner;
        internal readonly int Id;
        internal readonly ActionData Action;
        internal readonly HashSet<Inctor2> Launched = new HashSet<Inctor2>();
        internal readonly Dictionary<long, Death> Deaths = new Dictionary<long, Death>();
        internal int PendingDeaths;
        internal bool Started, NativeFinished, Ended, Closed;
        internal Batch(AttackSettlements owner, int id, ActionData action) { Owner = owner; Id = id; Action = action; }
    }

    private sealed class Death
    {
        internal readonly Batch Batch;
        internal readonly UnitData Victim;
        internal readonly DieReason Reason;
        internal readonly UnitData Attacker;
        internal readonly Player Killer;
        internal bool Committed;
        internal Death(Batch batch, UnitData victim, DieReason reason, UnitData attacker, Player killer)
        { Batch = batch; Victim = victim; Reason = reason; Attacker = attacker; Killer = killer; }
    }
}

[HarmonyPatch]
internal static class DetachedAttackBatchPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ActionData), "DoAction_MultiTarget");
        yield return AccessTools.Method(typeof(ActionData), "DoAction_Parallel");
    }
    private static void Postfix(ActionData __instance, ref IEnumerator __result) => AttackSettlements.WrapBatch(__instance, ref __result);
}

[HarmonyPatch(typeof(ActionData), "proc_DoActionCell")]
internal static class DetachedAttackCellCoroutinePatch
{
    private static void Postfix(GameTileData gtd, ref IEnumerator __result) => AttackSettlements.WrapCell(gtd, ref __result);
}

[HarmonyPatch(typeof(Action_Attack), nameof(Action_Attack.DoActionCell))]
internal static class DetachedAttackCellSettlementPatch
{
    private static bool Prefix() => AttackSettlements.AllowCell();
    private static void Postfix(Action_Attack __instance, GameTileData gtd, Inctor2? move_pos, float data) =>
        AttackSettlements.RecordCell(__instance, gtd, move_pos, data);
}

[HarmonyPatch(typeof(GameAPI), "proc_UnitDeathAnimation")]
internal static class DetachedAttackDeathCoroutinePatch
{
    private static void Postfix(UnitData victim, DieReason reason, UnitData attacker, Player killer, ref IEnumerator __result) =>
        AttackSettlements.WrapDeath(victim, reason, attacker, killer, ref __result);
}

[HarmonyPatch(typeof(GameAPI), nameof(GameAPI.DescendUnit))]
internal static class DetachedAttackDescendPatch
{
    private static bool Prefix() => AttackSettlements.AllowDeath();
}

[HarmonyPatch(typeof(UnitData), nameof(UnitData.TriggerDieEvent))]
internal static class DetachedAttackDeathEventPatch
{
    private static bool Prefix() => AttackSettlements.AllowDeath();
    private static void Postfix(UnitData __instance) => AttackSettlements.RecordDeath(__instance);
}
