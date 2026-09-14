using System.Runtime.CompilerServices;
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
    }

    private static readonly ConditionalWeakTable<UnitData, StateSlot> States = new ConditionalWeakTable<UnitData, StateSlot>();

    public static bool TryEnterAwaitingAuthority(GameCommand command)
    {
        foreach (var id in command.UnitIds)
        {
            var unit = Resolve(id);
            if (unit is not null && GetState(unit) != MultiplayerUnitState.Ready) return false;
        }

        var deadline = Time.realtimeSinceStartup + 3f;
        foreach (var id in command.UnitIds)
        {
            var unit = Resolve(id);
            if (unit is null) continue;
            var slot = States.GetOrCreateValue(unit);
            slot.State = MultiplayerUnitState.AwaitingAuthority;
            slot.AwaitingUntil = deadline;
        }
        return true;
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
        }
    }

    private static MultiplayerUnitState GetState(UnitData unit)
    {
        if (!States.TryGetValue(unit, out var slot)) return MultiplayerUnitState.Ready;
        if (slot.State == MultiplayerUnitState.AwaitingAuthority && Time.realtimeSinceStartup >= slot.AwaitingUntil)
        {
            slot.State = MultiplayerUnitState.Ready;
            slot.AwaitingUntil = 0f;
        }
        return slot.State;
    }

    private static UnitData? Resolve(long id)
    {
        if (id < int.MinValue || id > int.MaxValue) return null;
        return GS_Battle.self?.all_unit?.GetUnitByID((int)id);
    }
}
