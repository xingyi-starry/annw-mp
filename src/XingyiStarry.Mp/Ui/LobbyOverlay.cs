using System;
using UnityEngine;

namespace XingyiStarry.Mp.Ui;

internal sealed class LobbyOverlay
{
    private readonly XingyiStarryMpPlugin plugin;
    private Rect window = new Rect(40, 60, 430, 290);
    private string address = "127.0.0.1";
    private string port = Protocol.ProtocolConstants.DefaultPort.ToString();
    public bool Visible { get; set; }

    public LobbyOverlay(XingyiStarryMpPlugin plugin) => this.plugin = plugin;
    public void Draw() { if (Visible) window = GUI.Window(724555, window, DrawWindow, "XingyiStarry MP - 联机遭遇战"); }

    private void DrawWindow(int id)
    {
        GUILayout.Label("主机权威局域网房间");
        GUILayout.BeginHorizontal(); GUILayout.Label("地址", GUILayout.Width(55)); address = GUILayout.TextField(address); GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal(); GUILayout.Label("端口", GUILayout.Width(55)); port = GUILayout.TextField(port); GUILayout.EndHorizontal();
        GUILayout.Space(8);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("创建房间")) plugin.Host(ParsePort());
        if (GUILayout.Button("加入房间")) plugin.Join(address, ParsePort());
        if (GUILayout.Button("断开")) plugin.Disconnect();
        GUILayout.EndHorizontal();
        GUILayout.Space(8); GUILayout.Label(plugin.Status);
        GUILayout.Label("席位请在遭遇战配置页选择");
        if (plugin.CanSetReady && GUILayout.Button("准备 / 取消准备")) plugin.ToggleReady();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("关闭")) Visible = false;
        GUI.DragWindow(new Rect(0, 0, 10000, 24));
    }

    private int ParsePort() => int.TryParse(port, out var parsed) && parsed is > 0 and <= 65535 ? parsed : Protocol.ProtocolConstants.DefaultPort;
}
