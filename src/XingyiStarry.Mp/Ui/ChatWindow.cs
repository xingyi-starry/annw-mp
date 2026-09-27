using System;
using System.Linq;
using UnityEngine;
using XingyiStarry.Mp.Game;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Ui;

internal static class ChatWindow
{
    private const int WindowId = 0x584D5043;
    private const float DefaultWidth = 250f;
    private const float DefaultHeight = 350f;
    private const float MinWidth = 220f;
    private const float MinHeight = 220f;
    private const float MaxWidth = 720f;
    private const float MaxHeight = 800f;
    private const float CollapsedHeight = 30f;
    private const float TitleWidth = 112f;
    private const float HeaderTop = 3f;
    private const float HeaderHeight = 24f;
    private const float HeaderGap = 4f;
    private static Rect window = new Rect(18f, 300f, DefaultWidth, DefaultHeight);
    private static Vector2 expandedSize = new Vector2(DefaultWidth, DefaultHeight);
    private static Vector2 chatScroll;
    private static Vector2 rosterScroll;
    private static bool positioned;
    private static bool visible;
    private static bool collapsed;
    private static bool rosterSelected;
    private static bool scrollToBottom;
    private static bool titlePressed;
    private static float titleDragDistance;
    private static Vector2 pendingWindowDrag;
    private static bool resizePressed;
    private static Vector2 pendingWindowResize;
    private static GUIStyle? messageStyle;
    private static GUIStyle? inputStyle;
    private static GUIStyle? compactButtonStyle;
    private static string input = "";
    private static ChatChannel channel;
    private static ulong lastSeq;

    internal static bool IsPointerOver { get; private set; }
    internal static bool IsTyping { get; private set; }
    internal static bool IsPointerOverNow
    {
        get
        {
            if (!visible) return false;
            var pointer = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            return window.Contains(pointer);
        }
    }

    internal static void Tick(XingyiStarryMpPlugin plugin)
    {
        visible = plugin.IsHost || plugin.IsClient;
        if (!visible)
        {
            IsPointerOver = false; IsTyping = false; return;
        }
        if (!positioned)
        {
            window.y = Mathf.Max(18f, Screen.height - DefaultHeight - 18f); positioned = true;
        }
        var pointer = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        IsPointerOver = window.Contains(pointer);
        var history = plugin.CurrentChatHistory;
        var nextSeq = history.Count == 0 ? 0 : history.Max(value => value.SocialSeq);
        if (nextSeq != lastSeq) { lastSeq = nextSeq; scrollToBottom = true; }
    }

    internal static void Draw(XingyiStarryMpPlugin plugin)
    {
        if (!visible) return;
        window.width = expandedSize.x; window.height = collapsed ? CollapsedHeight : expandedSize.y;
        window.x = Mathf.Clamp(window.x, 0f, Mathf.Max(0f, Screen.width - window.width));
        window.y = Mathf.Clamp(window.y, 0f, Mathf.Max(0f, Screen.height - window.height));
        window = GUI.Window(WindowId, window, _ => DrawWindow(plugin), "");
        window.position += pendingWindowDrag;
        pendingWindowDrag = Vector2.zero;
        if (pendingWindowResize != Vector2.zero)
        {
            expandedSize.x = Mathf.Clamp(expandedSize.x + pendingWindowResize.x, MinWidth,
                Mathf.Min(MaxWidth, Mathf.Max(MinWidth, Screen.width - window.x)));
            expandedSize.y = Mathf.Clamp(expandedSize.y + pendingWindowResize.y, MinHeight,
                Mathf.Min(MaxHeight, Mathf.Max(MinHeight, Screen.height - window.y)));
            pendingWindowResize = Vector2.zero;
            window.width = expandedSize.x;
            if (!collapsed) window.height = expandedSize.y;
        }
    }

    internal static void Reset()
    {
        visible = false; collapsed = false; rosterSelected = false; scrollToBottom = false; input = "";
        channel = ChatChannel.Public; lastSeq = 0; chatScroll = Vector2.zero; rosterScroll = Vector2.zero;
        titlePressed = false; titleDragDistance = 0f; pendingWindowDrag = Vector2.zero;
        resizePressed = false; pendingWindowResize = Vector2.zero;
        expandedSize = new Vector2(DefaultWidth, DefaultHeight);
        IsPointerOver = false; IsTyping = false;
    }

    private static void DrawWindow(XingyiStarryMpPlugin plugin)
    {
        var innerWidth = window.width - 8f;
        GUILayout.BeginArea(new Rect(4f, HeaderTop, innerWidth, HeaderHeight));
        var titleRect = new Rect((innerWidth - TitleWidth) * 0.5f, 0f, TitleWidth, HeaderHeight);
        HandleTitleGesture(titleRect);
        GUI.Box(titleRect, collapsed ? "联机房间  ▶" : "联机房间  ▼", GUI.skin.button);
        GUILayout.EndArea();
        if (collapsed) { GUI.FocusControl(null); IsTyping = false; GUI.DragWindow(); return; }
        var contentTop = HeaderTop + HeaderHeight + HeaderGap;
        GUILayout.BeginArea(new Rect(4f, contentTop, innerWidth, window.height - contentTop - 18f));
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(!rosterSelected, "聊天", GUI.skin.button, GUILayout.Width(64f))) rosterSelected = false;
        GUILayout.FlexibleSpace();
        if (GUILayout.Toggle(rosterSelected, "玩家", GUI.skin.button, GUILayout.Width(64f))) rosterSelected = true;
        GUILayout.EndHorizontal();
        if (rosterSelected) { GUI.FocusControl(null); IsTyping = false; DrawRoster(plugin.CurrentRoom); }
        else DrawChat(plugin);
        GUILayout.EndArea();
        HandleResizeGesture(new Rect(window.width - 18f, window.height - 18f, 16f, 16f));
        GUI.DragWindow();
    }

    private static void HandleResizeGesture(Rect resizeRect)
    {
        GUI.Label(resizeRect, "◢");
        var current = Event.current;
        if (current.button != 0) return;
        if (current.type == EventType.MouseDown && resizeRect.Contains(current.mousePosition))
        {
            resizePressed = true; current.Use(); return;
        }
        if (current.type == EventType.MouseDrag && resizePressed)
        {
            pendingWindowResize += current.delta; current.Use(); return;
        }
        if (current.type != EventType.MouseUp || !resizePressed) return;
        resizePressed = false; current.Use();
    }

    private static void HandleTitleGesture(Rect titleRect)
    {
        var current = Event.current;
        if (current.button != 0) return;
        if (current.type == EventType.MouseDown && titleRect.Contains(current.mousePosition))
        {
            titlePressed = true; titleDragDistance = 0f; current.Use(); return;
        }
        if (current.type == EventType.MouseDrag && titlePressed)
        {
            pendingWindowDrag += current.delta;
            titleDragDistance += current.delta.magnitude;
            current.Use(); return;
        }
        if (current.type != EventType.MouseUp || !titlePressed) return;
        if (titleDragDistance < 3f && titleRect.Contains(current.mousePosition)) collapsed = !collapsed;
        titlePressed = false; titleDragDistance = 0f; current.Use();
    }

    private static void DrawChat(XingyiStarryMpPlugin plugin)
    {
        RefreshAdaptiveStyles();
        chatScroll = GUILayout.BeginScrollView(chatScroll, GUI.skin.box, GUILayout.ExpandHeight(true));
        foreach (var message in plugin.CurrentChatHistory.OrderBy(value => value.SocialSeq))
        {
            var prefix = message.Kind == ChatKind.System ? "[系统] " : message.Channel == ChatChannel.Team ? "[队内] " : "[公共] ";
            var text = message.Kind == ChatKind.System ? prefix + message.Text : prefix + message.SenderName + "：" + message.Text;
            var previous = GUI.contentColor;
            if (message.Kind == ChatKind.System) GUI.contentColor = new Color(0.85f, 0.85f, 0.85f);
            else if (message.SenderClientId == plugin.LocalIdentityId) GUI.contentColor = new Color(0.55f, 0.86f, 1f);
            GUILayout.Label(text, messageStyle!);
            GUI.contentColor = previous;
        }
        if (scrollToBottom && Event.current.type == EventType.Repaint) { chatScroll.y = float.MaxValue; scrollToBottom = false; }
        GUILayout.EndScrollView();

        var participant = plugin.LocalIdentityId is Guid id ? plugin.CurrentRoom?.Participants.Find(value => value.ClientId == id) : null;
        var canTeam = plugin.CurrentRoom?.MatchStarted == true && participant?.Admission == ParticipantAdmission.Player;
        if (!canTeam) channel = ChatChannel.Public;
        GUILayout.BeginHorizontal();
        GUI.enabled = canTeam;
        if (GUILayout.Button(channel == ChatChannel.Team ? "队内" : "公共", compactButtonStyle!, GUILayout.Width(58f)))
            channel = channel == ChatChannel.Public ? ChatChannel.Team : ChatChannel.Public;
        GUI.enabled = true;
        var enterPressed = GUI.GetNameOfFocusedControl() == "XingyiStarryMpChatInput" &&
            Event.current.type == EventType.KeyDown &&
            (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
        GUI.SetNextControlName("XingyiStarryMpChatInput");
        input = GUILayout.TextField(input, 512, inputStyle!, GUILayout.ExpandWidth(true));
        IsTyping = GUI.GetNameOfFocusedControl() == "XingyiStarryMpChatInput";
        var submit = GUILayout.Button("发送", compactButtonStyle!, GUILayout.Width(58f));
        if (enterPressed) { submit = true; Event.current.Use(); }
        if (submit) Submit(plugin);
        GUILayout.EndHorizontal();
    }

    private static void DrawRoster(RoomSnapshot? room)
    {
        RefreshAdaptiveStyles();
        rosterScroll = GUILayout.BeginScrollView(rosterScroll, GUI.skin.box, GUILayout.ExpandHeight(true));
        if (room is not null)
        {
            foreach (var participant in room.Participants.OrderByDescending(value => value.IsHost).ThenBy(value => value.DisplayName))
            {
                var seat = room.Seats.Find(value => value.ClientId == participant.ClientId);
                var state = !participant.Connected ? participant.Reconnecting ? "重连中" : "离线" : !room.MatchStarted
                    ? participant.Ready ? "已准备" : "未准备"
                    : participant.Admission == ParticipantAdmission.Spectator ? "观战者" : participant.Admission == ParticipantAdmission.JoinSelection ? "选择中" : seat?.Defeated == true ? "已战败" : "存活";
                var position = seat is null ? "未选位置" : $"位置 {seat.LobbySlotIndex + 1} · 队伍 {seat.Team + 1}";
                GUILayout.Label($"{(participant.IsHost ? "★" : "•")} {participant.DisplayName}    {position}    [{state}]", messageStyle!);
            }
        }
        GUILayout.EndScrollView();
    }

    private static void RefreshAdaptiveStyles()
    {
        var areaRatio = expandedSize.x * expandedSize.y / (DefaultWidth * DefaultHeight);
        var fontSize = Mathf.RoundToInt(Mathf.Clamp(13f * Mathf.Sqrt(areaRatio), 11f, 20f));
        messageStyle ??= new GUIStyle(GUI.skin.label) { wordWrap = true };
        inputStyle ??= new GUIStyle(GUI.skin.textField);
        compactButtonStyle ??= new GUIStyle(GUI.skin.button);
        messageStyle.fontSize = fontSize;
        inputStyle.fontSize = fontSize;
        compactButtonStyle.fontSize = Mathf.Clamp(fontSize, 11, 16);
    }

    private static void Submit(XingyiStarryMpPlugin plugin)
    {
        if (string.IsNullOrWhiteSpace(input)) return;
        plugin.SendChat(channel, input); input = ""; scrollToBottom = true;
        GUI.FocusControl("XingyiStarryMpChatInput");
    }
}
