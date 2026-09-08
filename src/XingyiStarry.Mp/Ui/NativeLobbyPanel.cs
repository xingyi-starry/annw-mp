using System;
using ANNW;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace XingyiStarry.Mp.Ui;

internal static class NativeLobbyPanel
{
    private const string RootName = "XingyiStarryMp_NativeLobby";
    private static UI_MENU_MainMenu? menu;
    private static GameObject? root;
    private static TMP_InputField? nameInput;
    private static TMP_InputField? hostInput;
    private static TMP_InputField? portInput;
    private static TextMeshProUGUI? status;
    private static bool waitingForHandshake;

    internal static void Open(UI_MENU_MainMenu source)
    {
        menu = source; source.pop_skirmish.SetActive(false);
        if (!Build()) return;
        waitingForHandshake = false; root!.SetActive(true); root.transform.SetAsLastSibling();
        Refresh(XingyiStarryMpPlugin.Instance);
    }

    internal static void Tick(XingyiStarryMpPlugin plugin)
    {
        if (root == null || !root.activeInHierarchy) return;
        Refresh(plugin);
        if (waitingForHandshake && plugin.LocalClientId.HasValue) EnterConfiguration();
    }

    private static bool Build()
    {
        if (root != null) return true;
        if (!GameUiKit.Ensure() || UI_Floater.self == null) return false;
        var full = GameUiKit.Rect(RootName, UI_Floater.self.transform); GameUiKit.Stretch(full);
        root = full.gameObject;
        var dim = GameUiKit.Rect("Dim", full); GameUiKit.Stretch(dim); var dimImage = dim.gameObject.AddComponent<Image>(); dimImage.color = new Color(0f, 0f, 0f, 0.68f);
        var panel = GameUiKit.Rect("Panel", full); panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f); panel.pivot = new Vector2(0.5f, 0.5f); panel.sizeDelta = new Vector2(700f, 470f);
        GameUiKit.Panel(panel);
        var body = GameUiKit.Rect("Body", panel); GameUiKit.Stretch(body); body.offsetMin = new Vector2(36, 28); body.offsetMax = new Vector2(-36, -28);
        var vertical = body.gameObject.AddComponent<VerticalLayoutGroup>(); vertical.spacing = 10f; vertical.childControlWidth = true; vertical.childControlHeight = true; vertical.childForceExpandWidth = true; vertical.childForceExpandHeight = false;
        AddFixedText(body, "联机遭遇战", 32f, 50f, TextAlignmentOptions.Center);
        AddFixedText(body, "创建或连接房间后，双方共同进入遭遇战配置页选择席位。", 18f, 38f, TextAlignmentOptions.Center);
        nameInput = AddInputRow(body, "用户名", XingyiStarryMpPlugin.Instance?.ConfiguredDisplayName ?? Environment.UserName);
        hostInput = AddInputRow(body, "主机地址", XingyiStarryMpPlugin.Instance?.ConfiguredAddress ?? "127.0.0.1");
        portInput = AddInputRow(body, "端口", (XingyiStarryMpPlugin.Instance?.ConfiguredPort ?? Protocol.ProtocolConstants.DefaultPort).ToString());
        var row = GameUiKit.Rect("Actions", body); row.gameObject.AddComponent<LayoutElement>().preferredHeight = 48f;
        var horizontal = row.gameObject.AddComponent<HorizontalLayoutGroup>(); horizontal.spacing = 12f; horizontal.childControlHeight = true; horizontal.childControlWidth = true; horizontal.childForceExpandWidth = true;
        GameUiKit.Button(row, "Host", "创建房间", OnHost);
        GameUiKit.Button(row, "Join", "连接房间", OnJoin);
        GameUiKit.Button(row, "Close", "返回", Close);
        status = AddFixedText(body, "未连接", 17f, 48f, TextAlignmentOptions.Center);
        root.SetActive(false); return true;
    }

    private static TextMeshProUGUI AddFixedText(Transform parent, string value, float size, float height, TextAlignmentOptions alignment)
    {
        var host = GameUiKit.Rect("TextRow", parent); host.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
        return GameUiKit.Text(host, "Text", value, size, alignment);
    }

    private static TMP_InputField AddInputRow(Transform parent, string label, string value)
    {
        var row = GameUiKit.Rect(label, parent); row.gameObject.AddComponent<LayoutElement>().preferredHeight = 48f;
        var horizontal = row.gameObject.AddComponent<HorizontalLayoutGroup>(); horizontal.spacing = 12f; horizontal.childControlHeight = true; horizontal.childControlWidth = true; horizontal.childForceExpandWidth = true;
        var labelHost = GameUiKit.Rect("Label", row); var labelLayout = labelHost.gameObject.AddComponent<LayoutElement>(); labelLayout.minWidth = 120f; labelLayout.preferredWidth = 120f; labelLayout.flexibleWidth = 0f;
        GameUiKit.Text(labelHost, "Text", label, 21f, TextAlignmentOptions.MidlineRight);
        return GameUiKit.Input(row, "XingyiStarryMp_" + label, value);
    }

    private static void OnHost()
    {
        var plugin = ApplyInputs(); if (plugin == null) return;
        plugin.Host(plugin.ConfiguredPort);
        if (plugin.IsHost) EnterConfiguration();
    }

    private static void OnJoin()
    {
        var plugin = ApplyInputs(); if (plugin == null) return;
        waitingForHandshake = true; plugin.Join(plugin.ConfiguredAddress, plugin.ConfiguredPort);
    }

    private static XingyiStarryMpPlugin? ApplyInputs()
    {
        var plugin = XingyiStarryMpPlugin.Instance; if (plugin == null || nameInput == null || hostInput == null || portInput == null) return null;
        var name = nameInput.text.Trim(); if (name.Length == 0 || name.Length > 32) { if (status != null) status.text = "用户名长度必须为 1–32 个字符"; return null; }
        if (!int.TryParse(portInput.text, out var port) || port < 1 || port > 65535) { if (status != null) status.text = "端口必须在 1–65535 之间"; return null; }
        plugin.ConfiguredDisplayName = name; plugin.ConfiguredAddress = string.IsNullOrWhiteSpace(hostInput.text) ? "127.0.0.1" : hostInput.text.Trim(); plugin.ConfiguredPort = port;
        return plugin;
    }

    private static void EnterConfiguration()
    {
        waitingForHandshake = false; if (root != null) root.SetActive(false);
        if (menu != null) NativeSkirmishLobby.Enter(menu);
    }

    private static void Refresh(XingyiStarryMpPlugin? plugin) { if (status != null && plugin != null) status.text = plugin.Status; }
    private static void Close() { waitingForHandshake = false; XingyiStarryMpPlugin.Instance?.Disconnect(); if (root != null) root.SetActive(false); }
}
