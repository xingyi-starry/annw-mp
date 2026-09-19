using System.Collections.Generic;
using ANNW;
using HarmonyLib;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Game;

[HarmonyPatch(typeof(ANNW_Keyboard_Input), "Update")]
internal static class KeyboardInputScopePatch
{
    internal static bool Active { get; private set; }
    private static void Prefix() => Active = true;
    private static void Finalizer() => Active = false;
}

[HarmonyPatch(typeof(FUI_WorldCursor), "ConfirmAtCursor")]
internal static class KeyboardCursorConfirmPatch
{
    private static bool Prefix() => !InputGate.MultiplayerActive || InputGate.MaySubmit;
}

[HarmonyPatch(typeof(FUI_WorldCursor), "CancelAtCursor")]
internal static class KeyboardCursorCancelPatch
{
    private static bool Prefix() => !InputGate.MultiplayerActive || InputGate.MaySubmit;
}

[HarmonyPatch(typeof(InputKeybinding), nameof(InputKeybinding.IsActionKeyPressed))]
internal static class KeyBindingCommandCapturePatch
{
    private static void Postfix(InputAction action, ref bool __result)
    {
        if (!__result || !KeyboardInputScopePatch.Active || !InputGate.MultiplayerActive) return;
        if (action == InputAction.SelfDestroy)
        {
            if (!InputGate.MaySubmit) __result = false;
            return;
        }
        if (action != InputAction.StandByAndNext && action != InputAction.ToggleStandby &&
            action != InputAction.ToggleSleep && action != InputAction.SetUnitToStay) return;

        __result = false;
        if (!InputGate.MaySubmit) return;
        var units = LocalCommandUnits.Filter(GS_Battle.self.selected_units, action == InputAction.SetUnitToStay);
        if (units.Count == 0) { XingyiStarryMpPlugin.Instance?.NotifyNoEligibleUnits(); return; }
        var ids = new long[units.Count];
        for (var i = 0; i < units.Count; i++) ids[i] = units[i].unit_id;

        if (action == InputAction.SetUnitToStay)
        {
            XingyiStarryMpPlugin.Instance?.SubmitCommand(new GameCommand { Kind = CommandKind.Stay, UnitIds = ids });
            return;
        }

        var sleep = action == InputAction.ToggleSleep;
        var uniform = true;
        var current = sleep ? units[0].sleeping : units[0].skipping;
        for (var i = 1; i < units.Count; i++)
        {
            var state = sleep ? units[i].sleeping : units[i].skipping;
            if (state != current) { uniform = false; break; }
        }
        XingyiStarryMpPlugin.Instance?.SubmitCommand(new GameCommand
        {
            Kind = sleep ? CommandKind.ToggleSleep : CommandKind.ToggleStandby,
            UnitIds = ids,
            DesiredToggleState = uniform && !current
        });
    }
}
