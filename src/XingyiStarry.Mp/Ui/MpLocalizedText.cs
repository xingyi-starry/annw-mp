using System;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace XingyiStarry.Mp.Ui;

// Keep the invariant source, including while hidden. Changing language does not rebuild
// panels or touch input fields, selected rooms, passwords, or in-flight requests.
internal sealed class MpLocalizedText : MonoBehaviour
{
    private TMP_Text? label;
    private string source = "";
    private Func<string>? render;

    internal static void Set(TMP_Text target, string value)
    {
        var binding = target.GetComponent<MpLocalizedText>() ?? target.gameObject.AddComponent<MpLocalizedText>();
        binding.label = target;
        binding.source = value;
        binding.render = null;
        binding.enabled = true;
        binding.Render();
    }

    internal static void Bind(TMP_Text target, Func<string> render)
    {
        var binding = target.GetComponent<MpLocalizedText>() ?? target.gameObject.AddComponent<MpLocalizedText>();
        binding.label = target;
        binding.render = render;
        binding.enabled = true;
        binding.Render();
    }

    internal static void Clear(TMP_Text target)
    {
        var binding = target.GetComponent<MpLocalizedText>();
        if (binding == null) return;
        binding.enabled = false;
        binding.label = null;
        binding.source = "";
        binding.render = null;
    }

    private void OnEnable() { MpText.LanguageChanged += Render; Render(); }
    private void OnDisable() => MpText.LanguageChanged -= Render;
    private void Render()
    {
        if (label != null) label.text = render != null ? render() : MpText.Render(source);
    }
}

// Observe initialization without forcing LAN/SuperSD to initialize during BepInEx Awake.
[HarmonyPatch(typeof(LAN), "Init")]
internal static class MultiplayerLanguageInitPatch
{
    private static void Postfix(LAN __instance) => MpText.SetLanguage(__instance.cur_language == LocalizedLanguage.zh_CN);
}

[HarmonyPatch(typeof(LAN), nameof(LAN.ChangeLanguage))]
internal static class MultiplayerLanguageChangePatch
{
    private static void Postfix(LAN __instance) => MpText.SetLanguage(__instance.cur_language == LocalizedLanguage.zh_CN);
}
