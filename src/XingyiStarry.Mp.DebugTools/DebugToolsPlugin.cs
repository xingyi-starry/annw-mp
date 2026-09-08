using System;
using ANNW;
using BepInEx;
using UnityEngine;

namespace XingyiStarry.Mp.DebugTools;

[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(XingyiStarryMpPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
[BepInProcess("AnnW.exe")]
public sealed class DebugToolsPlugin : BaseUnityPlugin
{
    public const string PluginId = "xingyistarry.mp.debugtools";
    public const string PluginName = "XingyiStarry MP Debug Tools";
    public const string PluginVersion = "0.1.0";

    private Rect window = new Rect(40f, 60f, 720f, 160f);
    private Vector2 scroll;
    private bool visible;
    private string amountText = "1000";
    private string status = "F8 打开或关闭；操作由主机广播到所有客机。";

    private bool HostBattleActive => XingyiStarryMpPlugin.Instance?.IsAuthorityHostBattleActive == true &&
                                     GS_Battle.self?.all_player?.players is not null;

    private void Update()
    {
        if (!HostBattleActive)
        {
            visible = false;
            return;
        }

        if (Input.GetKeyDown(KeyCode.F8)) visible = !visible;
    }

    private void OnGUI()
    {
        if (!visible || !HostBattleActive) return;
        window.height = Mathf.Min(Screen.height - 80f, 178f + GS_Battle.self.all_player.players.Count * 34f);
        window = GUI.Window(724556, window, DrawWindow, "XingyiStarry MP - 主机调试面板");
    }

    private void DrawWindow(int id)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("每次增加", GUILayout.Width(70f));
        amountText = GUILayout.TextField(amountText, 10, GUILayout.Width(100f));
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("所有玩家 +资源", GUILayout.Width(125f))) SubmitResources(-1, true, true);
        if (GUILayout.Button("所有玩家充满技能", GUILayout.Width(140f))) SubmitFillSkill(-1);
        GUILayout.EndHorizontal();

        GUILayout.Space(5f);
        scroll = GUILayout.BeginScrollView(scroll);
        foreach (var player in GS_Battle.self.all_player.players)
        {
            if (player is null) continue;
            GUILayout.BeginHorizontal();
            GUILayout.Label(PlayerLabel(player), GUILayout.Width(230f));
            GUILayout.Label($"金属 {player.metal}  电力 {player.power}", GUILayout.Width(180f));
            if (GUILayout.Button("+金属", GUILayout.Width(65f))) SubmitResources(player.index, true, false);
            if (GUILayout.Button("+电力", GUILayout.Width(65f))) SubmitResources(player.index, false, true);
            if (GUILayout.Button("+资源", GUILayout.Width(65f))) SubmitResources(player.index, true, true);
            GUI.enabled = player.co_data?.skill is not null;
            if (GUILayout.Button("充满技能", GUILayout.Width(80f))) SubmitFillSkill(player.index);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();

        GUILayout.Label(status);
        if (GUILayout.Button("关闭")) visible = false;
        GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
    }

    private static string PlayerLabel(Player player)
    {
        var controller = player.is_ai ? "AI" : "真人";
        var commander = player.co_data?.sd_commander?.name;
        return string.IsNullOrEmpty(commander)
            ? $"P{player.index + 1} · {player.fraction} · {controller}"
            : $"P{player.index + 1} · {player.fraction} · {controller} · {commander}";
    }

    private int ParseAmount()
    {
        if (!int.TryParse(amountText, out var amount) || amount <= 0)
            throw new InvalidOperationException("增加量必须是正整数。");
        return amount;
    }

    private void SubmitResources(int playerIndex, bool metal, bool power)
    {
        try
        {
            var amount = ParseAmount();
            XingyiStarryMpPlugin.Instance!.TrySubmitDebugResources(playerIndex, metal ? amount : 0, power ? amount : 0, out status);
        }
        catch (Exception ex) { status = ex.Message; }
    }

    private void SubmitFillSkill(int playerIndex)
    {
        XingyiStarryMpPlugin.Instance!.TrySubmitDebugFillSkill(playerIndex, out status);
    }
}
