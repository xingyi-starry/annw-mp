using System.Runtime.CompilerServices;
using System.Threading;
using ANNW;
using UnityEngine;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Game;

internal enum MultiplayerUnitState
{
    Ready,
    AwaitingAuthority,
    AuthorityExecution
}

internal static class MultiplayerUnitStates
{
    private sealed class StateSlot
    {
        public MultiplayerUnitState State;
        public float AwaitingUntil;
        public long AttemptToken;
    }

    private static readonly ConditionalWeakTable<UnitData, StateSlot> States = new ConditionalWeakTable<UnitData, StateSlot>();

    private static long nextAttemptToken;

    public static bool TryEnterAwaitingAuthority(GameCommand command, out long token)
    {
        token = 0;
        foreach (var id in command.UnitIds)
        {
            var unit = Resolve(id);
            if (unit is not null && GetState(unit) != MultiplayerUnitState.Ready) return false;
        }

        token = Interlocked.Increment(ref nextAttemptToken);
        var deadline = Time.realtimeSinceStartup + 3f;
        foreach (var id in command.UnitIds)
        {
            var unit = Resolve(id);
            if (unit is null) continue;
            var slot = States.GetOrCreateValue(unit);
            slot.State = MultiplayerUnitState.AwaitingAuthority;
            slot.AwaitingUntil = deadline;
            slot.AttemptToken = token;
        }
        return true;
    }

    public static void RejectAwaitingAuthority(GameCommand command, long token)
    {
        foreach (var id in command.UnitIds)
        {
            var unit = Resolve(id);
            if (unit is null || !States.TryGetValue(unit, out var slot) ||
                slot.State != MultiplayerUnitState.AwaitingAuthority || slot.AttemptToken != token) continue;
            slot.State = MultiplayerUnitState.Ready;
            slot.AwaitingUntil = 0f;
            slot.AttemptToken = 0;
        }
    }

    public static void BeginAuthorityExecution(GameCommand command)
    {
        foreach (var id in command.UnitIds)
        {
            var unit = Resolve(id);
            if (unit is null) continue;
            var slot = States.GetOrCreateValue(unit);
            slot.State = MultiplayerUnitState.AuthorityExecution;
            slot.AwaitingUntil = 0f;
            slot.AttemptToken = 0;
        }
    }

    public static void EndAuthorityExecution(GameCommand command)
    {
        foreach (var id in command.UnitIds)
        {
            var unit = Resolve(id);
            if (unit is null || !States.TryGetValue(unit, out var slot)) continue;
            slot.State = MultiplayerUnitState.Ready;
            slot.AwaitingUntil = 0f;
            slot.AttemptToken = 0;
        }
    }

    private static MultiplayerUnitState GetState(UnitData unit)
    {
        if (!States.TryGetValue(unit, out var slot)) return MultiplayerUnitState.Ready;
        if (slot.State == MultiplayerUnitState.AwaitingAuthority && Time.realtimeSinceStartup >= slot.AwaitingUntil)
        {
            slot.State = MultiplayerUnitState.Ready;
            slot.AwaitingUntil = 0f;
            slot.AttemptToken = 0;
        }
        return slot.State;
    }

    private static UnitData? Resolve(long id)
    {
        if (id < int.MinValue || id > int.MaxValue) return null;
        return GS_Battle.self?.all_unit?.GetUnitByID((int)id);
    }
}
