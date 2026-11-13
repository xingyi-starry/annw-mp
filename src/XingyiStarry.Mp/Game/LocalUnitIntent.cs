using System;
using System.Collections.Generic;
using HarmonyLib;
using XingyiStarry.Mp.Session;

namespace XingyiStarry.Mp.Game;

// Native selection owns reversible same-cell setup. Only its action flags must
// be hidden from authority; movement goals remain local planner input.
internal static class LocalUnitIntent
{
    private readonly struct Flags
    {
        internal readonly bool Moved, Actioned, SamePosition;
        internal Flags(UnitData unit) { Moved = unit.moved; Actioned = unit.actioned; SamePosition = unit.moved_to_same_pos; }
        internal void Apply(UnitData unit) { unit.moved = Moved; unit.actioned = Actioned; unit.moved_to_same_pos = SamePosition; }
    }
    private static readonly Dictionary<UnitData, Flags> setups = new Dictionary<UnitData, Flags>();

    internal static void Remember(UnitData unit, Inctor2 target, bool resetAction)
    {
        if (!InputGate.MultiplayerActive || ExecutionContext.IsAuthoritativeExecution || resetAction ||
            target != unit.pos || unit.moved || !LocalCommandUnits.Owned(unit)) return;
        if (!setups.ContainsKey(unit)) setups.Add(unit, new Flags(unit));
    }

    internal static void SelectionCleared()
    {
        foreach (var unit in new List<UnitData>(setups.Keys))
            if (!unit.moved_to_same_pos || unit.dead) setups.Remove(unit);
    }

    internal static IDisposable CanonicalFlags() => new FlagScope();
    private sealed class FlagScope : IDisposable
    {
        private readonly Dictionary<UnitData, Flags> local = new Dictionary<UnitData, Flags>();
        internal FlagScope()
        {
            foreach (var pair in setups) { local.Add(pair.Key, new Flags(pair.Key)); pair.Value.Apply(pair.Key); }
        }
        public void Dispose() { foreach (var pair in local) pair.Value.Apply(pair.Key); }
    }

    internal static void BeginAuthority()
    {
        var players = new HashSet<Player>();
        foreach (var pair in setups)
        {
            pair.Value.Apply(pair.Key);
            pair.Key.Event_ReDraw?.Invoke(false);
            players.Add(pair.Key.player);
        }
        setups.Clear();
        foreach (var player in players) AccessTools.Method(typeof(Player), "UpdateUnactionedUnitCount").Invoke(player, null);
    }
    internal static void Reset() => setups.Clear();

    internal static void WriteCanonical(UnitData unit, DynOb save)
    {
        save.dic_values.Remove("move_goal");
        if (!setups.TryGetValue(unit, out var state)) return;
        save.SetKey("moved", state.Moved);
        save.SetKey("actioned", state.Actioned);
        save.SetKey("moved_to_same_pos", state.SamePosition);
    }
}

[HarmonyPatch(typeof(UnitData), nameof(UnitData.DoMoveWithAni))]
internal static class LocalSetupCapturePatch
{
    private static void Prefix(UnitData __instance, Inctor2 move_target, bool reset_action) =>
        LocalUnitIntent.Remember(__instance, move_target, reset_action);
}

[HarmonyPatch(typeof(UnitData), nameof(UnitData.DoMove))]
internal static class LocalSetupMoveCapturePatch
{
    private static void Prefix(UnitData __instance, Inctor2 move_target, bool reset_action) =>
        LocalUnitIntent.Remember(__instance, move_target, reset_action);
}

[HarmonyPatch(typeof(UX_Manager), nameof(UX_Manager.ClearUnitSelection))]
internal static class LocalSetupClearPatch
{
    private static void Postfix() { if (InputGate.MultiplayerActive) LocalUnitIntent.SelectionCleared(); }
}

[HarmonyPatch(typeof(UnitData), nameof(UnitData.SaveOb))]
internal static class LocalIntentSavePatch
{
    private static void Postfix(UnitData __instance, DynOb __result)
    {
        if (InputGate.MultiplayerActive || GameStateSerializer.CapturingSnapshot) LocalUnitIntent.WriteCanonical(__instance, __result);
    }
}

// UX's goal setter is input bookkeeping. During authoritative playback only
// the native movement itself may clear a goal on arrival.
[HarmonyPatch]
internal static class LocalMovementGoalPatch
{
    private static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(UX_Manager), nameof(UX_Manager.proc_UnitsDoMove)));
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var clear = AccessTools.Method(typeof(UnitData), "ClearMoveGoal");
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(clear))
            {
                instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(LocalMovementGoalPatch), nameof(ClearInputGoal));
            }
            yield return instruction;
        }
    }
    private static void ClearInputGoal(UnitData unit, bool silent)
    {
        if (!InputGate.MultiplayerActive || !ExecutionContext.IsAuthoritativeExecution)
            AccessTools.Method(typeof(UnitData), "ClearMoveGoal").Invoke(unit, new object[] { silent });
    }
}
