using ANNW;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace XingyiStarry.Mp.Ui;

internal static class MainMenuEntry
{
    private const string ObjectName = "XingyiStarryMp_LobbyButton";
    private const string VersionSuffix = "xingyistarry-mp-v" + XingyiStarryMpPlugin.PluginVersion;

    public static void Ensure(UI_MENU_MainMenu menu)
    {
        EnsureVersionText();
        if (menu?.pop_skirmish is null || FindDeep(menu.pop_skirmish.transform, ObjectName) is not null) return;
        var buttons = menu.pop_skirmish.GetComponentsInChildren<Button>(true);
        if (buttons.Length == 0) return;
        var clone = Object.Instantiate(buttons[0].gameObject, buttons[0].transform.parent, false);
        clone.name = ObjectName; clone.SetActive(true);
        if (clone.transform is RectTransform rect && buttons[0].transform is RectTransform source)
            rect.anchoredPosition = source.anchoredPosition + new Vector2(0, -60);
        var label = clone.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label is not null) label.text = "联机遭遇战";
        foreach (var localized in clone.GetComponentsInChildren<Localized_Txt>(true)) Object.Destroy(localized);
        var button = clone.GetComponent<Button>() ?? clone.GetComponentInChildren<Button>(true);
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(new UnityAction(() => NativeLobbyPanel.Open(menu)));
    }

    public static void EnsureVersionText()
    {
        var label = SingletonMono<SS_ANNW_Menu>.self?.txt_version;
        if (label is null) return;
        var gameVersion = Singleton<GS_Overall>.self?.version_name ?? label.text;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Overflow;
        label.text = gameVersion + "    " + VersionSuffix;
    }

    private static Transform? FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        for (var i = 0; i < root.childCount; i++) { var found = FindDeep(root.GetChild(i), name); if (found is not null) return found; }
        return null;
    }
}

[HarmonyPatch(typeof(SS_ANNW_Menu), nameof(SS_ANNW_Menu.OnAwakeInit))]
internal static class MenuVersionPatch
{
    private static void Postfix() => MainMenuEntry.EnsureVersionText();
}

[HarmonyPatch(typeof(UI_MENU_MainMenu), nameof(UI_MENU_MainMenu.Show))]
internal static class MainMenuShowPatch
{
    private static void Postfix(UI_MENU_MainMenu __instance) => MainMenuEntry.Ensure(__instance);
}

[HarmonyPatch(typeof(UI_MENU_MainMenu), nameof(UI_MENU_MainMenu.OnBtn_Skirmish))]
internal static class MainMenuSkirmishPatch
{
    private static void Postfix(UI_MENU_MainMenu __instance) => MainMenuEntry.Ensure(__instance);
}
