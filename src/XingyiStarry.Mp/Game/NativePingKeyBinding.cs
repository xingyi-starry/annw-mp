using System;
using System.Collections.Generic;
using ANNW;
using HarmonyLib;
using UnityEngine;

namespace XingyiStarry.Mp.Game;

internal static class NativePingKeyBinding
{
    internal static readonly InputAction Action = (InputAction)1000;

    internal static KeyCode Current
    {
        get
        {
            try { return InputKeybinding.GetKeyboardBinding(Action); }
            catch { return KeyCode.R; }
        }
    }

    internal static void Ensure(KeyCode configured)
    {
        _ = InputKeybinding.GetKeyboardBinding(InputAction.Back);
        var defaults = (Dictionary<InputAction, KeyCode>?)AccessTools.Field(typeof(InputKeybinding), "_kbDefault")?.GetValue(null);
        var current = (Dictionary<InputAction, KeyCode>?)AccessTools.Field(typeof(InputKeybinding), "_keyboard")?.GetValue(null);
        if (defaults is null || current is null) throw new InvalidOperationException("无法注册原版地图标点按键。");
        defaults[Action] = KeyCode.R;
        current[Action] = configured;
    }
}

[HarmonyPatch(typeof(UI_POP_KeyBinding), "BuildList")]
internal static class NativePingKeyBindingListPatch
{
    private static void Postfix(UI_POP_KeyBinding __instance)
    {
        if (__instance.pool_unit is null) return;
        var item = __instance.pool_unit.AcquireItem().GetComponent<UI_KeyBindingItem>();
        var callback = AccessTools.Method(typeof(UI_POP_KeyBinding), "OnItemClick");
        item.Setup(NativePingKeyBinding.Action, value => callback?.Invoke(__instance, new object[] { value }));
        if (AccessTools.Field(typeof(UI_POP_KeyBinding), "_items")?.GetValue(__instance) is List<UI_KeyBindingItem> items)
            items.Add(item);
    }
}

[HarmonyPatch(typeof(UI_KeyBindingItem), nameof(UI_KeyBindingItem.Refresh))]
internal static class NativePingKeyBindingLabelPatch
{
    private static void Postfix(UI_KeyBindingItem __instance)
    {
        if (__instance.action != NativePingKeyBinding.Action) return;
        if (__instance.text_name is not null) __instance.text_name.text = "地图标点";
        if (__instance.tip_action is not null) __instance.tip_action.default_tip = "向同队玩家标记地图位置";
    }
}

[HarmonyPatch(typeof(InputKeybinding), nameof(InputKeybinding.SetKeyboardBinding))]
internal static class NativePingKeyBindingChangedPatch
{
    private static void Postfix(InputAction action, KeyCode key, bool __result)
    {
        if (__result && action == NativePingKeyBinding.Action)
            XingyiStarryMpPlugin.Instance?.UpdateConfiguredPingKey(key);
    }
}

[HarmonyPatch(typeof(InputKeybinding), nameof(InputKeybinding.Unbind))]
internal static class NativePingKeyBindingUnbindPatch
{
    private static void Postfix(InputAction action)
    {
        if (action == NativePingKeyBinding.Action)
            XingyiStarryMpPlugin.Instance?.UpdateConfiguredPingKey(KeyCode.None);
    }
}

[HarmonyPatch(typeof(InputKeybinding), nameof(InputKeybinding.SetToDefault))]
internal static class NativePingKeyBindingDefaultPatch
{
    private static void Postfix(InputAction action)
    {
        if (action == NativePingKeyBinding.Action)
            XingyiStarryMpPlugin.Instance?.UpdateConfiguredPingKey(KeyCode.R);
    }
}

[HarmonyPatch(typeof(InputKeybinding), nameof(InputKeybinding.ResetAll))]
internal static class NativePingKeyBindingResetPatch
{
    private static void Postfix()
    {
        InputKeybinding.SetKeyboardBinding(NativePingKeyBinding.Action, KeyCode.R);
    }
}
