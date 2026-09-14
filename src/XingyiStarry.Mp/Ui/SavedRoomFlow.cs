using System;
using ANNW;
using HarmonyLib;

namespace XingyiStarry.Mp.Ui;

internal static class SavedRoomFlow
{
    private static Action<string>? selected;
    private static Action? closed;
    private static bool closingAfterSelection;

    internal static void Open(UI_MENU_MainMenu menu, Action<string> onSelected, Action onClosed)
    {
        selected = onSelected ?? throw new ArgumentNullException(nameof(onSelected));
        closed = onClosed ?? throw new ArgumentNullException(nameof(onClosed));
        menu.OnBtn_SkirmishLoad();
    }

    internal static bool TryConsume(UI_POP_SaveLoadPanel panel)
    {
        if (selected is null) return false;
        var item = AccessTools.Field(typeof(UI_POP_SaveLoadPanel), "selected_item")?.GetValue(panel) as LocalFileItem;
        if (item is null || string.IsNullOrWhiteSpace(item.path)) return true;
        var callback = selected; selected = null; closed = null;
        closingAfterSelection = true;
        try { panel.Hide(); }
        finally { closingAfterSelection = false; }
        callback(item.path); return true;
    }

    internal static void OnHidden()
    {
        if (closingAfterSelection || selected is null) return;
        selected = null; var callback = closed; closed = null; callback?.Invoke();
    }
}

[HarmonyPatch(typeof(UI_POP_SaveLoadPanel), nameof(UI_POP_SaveLoadPanel.OnBtn_Load))]
internal static class SavedRoomLoadSelectionPatch
{
    private static bool Prefix(UI_POP_SaveLoadPanel __instance) => !SavedRoomFlow.TryConsume(__instance);
}

[HarmonyPatch(typeof(UI_Stackable), nameof(UI_Stackable.Hide))]
internal static class SavedRoomLoadClosedPatch
{
    private static void Postfix(UI_Stackable __instance)
    {
        if (__instance is UI_POP_SaveLoadPanel) SavedRoomFlow.OnHidden();
    }
}
