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
    private static bool? pendingCollapsed;
    private static bool? pendingRosterSelected;
    private static bool scrollToBottom;
    private static int gestureControlId;
    private static bool resetGestureCapture;
    private static float titleDragDistance;
    private static Vector2 pendingWindowDrag;
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
        if (resetGestureCapture) { ReleaseGestureCapture(); resetGestureCapture = false; }
        if (!visible) { ReleaseGestureCapture(); return; }
        // Keep the same control tree from Layout through input and Repaint. Applying
        // a tab/collapse change inside a mouse event can invalidate layout and control IDs.
        if (Event.current.type == EventType.Layout)
        {
            if (pendingCollapsed.HasValue) collapsed = pendingCollapsed.Value;
            if (pendingRosterSelected.HasValue) rosterSelected = pendingRosterSelected.Value;
            pendingCollapsed = null; pendingRosterSelected = null;
        }
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
        // Reset can run from Update; release native GUI capture in the next OnGUI.
        resetGestureCapture = true;
        pendingCollapsed = null; pendingRosterSelected = null;
        channel = ChatChannel.Public; lastSeq = 0; chatScroll = Vector2.zero; rosterScroll = Vector2.zero;
        titleDragDistance = 0f; pendingWindowDrag = Vector2.zero;
        pendingWindowResize = Vector2.zero;
        expandedSize = new Vector2(DefaultWidth, DefaultHeight);
        IsPointerOver = false; IsTyping = false;
    }

    private static void DrawWindow(XingyiStarryMpPlugin plugin)
    {
        // Allocate gesture IDs before the variable chat/roster controls so capture
        // remains stable when the page or its content changes.
        var titleControlId = GUIUtility.GetControlID(WindowId + 1, FocusType.Passive);
        var resizeControlId = GUIUtility.GetControlID(WindowId + 2, FocusType.Passive);
        if (Event.current.type == EventType.Layout && (collapsed || rosterSelected)) ReleaseChatFocus();
        var innerWidth = window.width - 8f;
        GUILayout.BeginArea(new Rect(4f, HeaderTop, innerWidth, HeaderHeight));
        var titleRect = new Rect((innerWidth - TitleWidth) * 0.5f, 0f, TitleWidth, HeaderHeight);
        HandleTitleGesture(titleRect, titleControlId);
        GUI.Box(titleRect, collapsed ? MpText.Render("${ui_room}") : MpText.Render("${ui_room_2}"), GUI.skin.button);
        GUILayout.EndArea();
        if (collapsed) { IsTyping = false; GUI.DragWindow(); return; }
        var contentTop = HeaderTop + HeaderHeight + HeaderGap;
        GUILayout.BeginArea(new Rect(4f, contentTop, innerWidth, window.height - contentTop - 18f));
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(!rosterSelected, MpText.Render("${ui_chat}"), GUI.skin.button, GUILayout.Width(64f)) && rosterSelected) pendingRosterSelected = false;
        GUILayout.FlexibleSpace();
        if (GUILayout.Toggle(rosterSelected, MpText.Render("${ui_players}"), GUI.skin.button, GUILayout.Width(64f)) && !rosterSelected) pendingRosterSelected = true;
        GUILayout.EndHorizontal();
        if (rosterSelected) { IsTyping = false; DrawRoster(plugin.CurrentRoom); }
        else DrawChat(plugin);
        GUILayout.EndArea();
        HandleResizeGesture(new Rect(window.width - 18f, window.height - 18f, 16f, 16f), resizeControlId);
        GUI.DragWindow();
    }

    private static void ReleaseChatFocus()
    {
        // Release only our text field, without changing focus during mouse gestures.
        if (GUI.GetNameOfFocusedControl() == "XingyiStarryMpChatInput") GUI.FocusControl(null);
        IsTyping = false;
    }

    private static void ReleaseGestureCapture()
    {
        if (gestureControlId == 0) return;
        if (GUIUtility.hotControl == gestureControlId) GUIUtility.hotControl = 0;
        gestureControlId = 0;
    }

    private static void HandleResizeGesture(Rect resizeRect, int controlId)
    {
        GUI.Label(resizeRect, "◢");
        var current = Event.current;
        var eventType = current.GetTypeForControl(controlId);
        if (eventType == EventType.Ignore && GUIUtility.hotControl == controlId) { ReleaseGestureCapture(); return; }
        if (current.button != 0) return;
        if (eventType == EventType.MouseDown && GUIUtility.hotControl == 0 && resizeRect.Contains(current.mousePosition))
        {
            GUIUtility.hotControl = controlId; gestureControlId = controlId; current.Use(); return;
        }
        if (GUIUtility.hotControl != controlId) return;
        if (eventType == EventType.MouseDrag)
        {
            pendingWindowResize += current.delta; current.Use(); return;
        }
        if (eventType != EventType.MouseUp) return;
        ReleaseGestureCapture(); current.Use();
    }

    private static void HandleTitleGesture(Rect titleRect, int controlId)
    {
        var current = Event.current;
        var eventType = current.GetTypeForControl(controlId);
        if (eventType == EventType.Ignore && GUIUtility.hotControl == controlId) { ReleaseGestureCapture(); titleDragDistance = 0f; return; }
        if (current.button != 0) return;
        if (eventType == EventType.MouseDown && GUIUtility.hotControl == 0 && titleRect.Contains(current.mousePosition))
        {
            GUIUtility.hotControl = controlId; gestureControlId = controlId; titleDragDistance = 0f; current.Use(); return;
        }
        if (GUIUtility.hotControl != controlId) return;
        if (eventType == EventType.MouseDrag)
        {
            pendingWindowDrag += current.delta;
            titleDragDistance += current.delta.magnitude;
            current.Use(); return;
        }
        if (eventType != EventType.MouseUp) return;
        if (titleDragDistance < 3f && titleRect.Contains(current.mousePosition)) pendingCollapsed = !collapsed;
        ReleaseGestureCapture(); titleDragDistance = 0f; current.Use();
    }

    private static void DrawChat(XingyiStarryMpPlugin plugin)
    {
        RefreshAdaptiveStyles();
        chatScroll = GUILayout.BeginScrollView(chatScroll, GUI.skin.box, GUILayout.ExpandHeight(true));
        foreach (var message in plugin.CurrentChatHistory.OrderBy(value => value.SocialSeq))
        {
            var prefix = message.Kind == ChatKind.System ? MpText.Render("${ui_system_2}") : message.Channel == ChatChannel.Team ? MpText.Render("${ui_team}") : MpText.Render("${ui_public}");
            var text = message.Kind == ChatKind.System ? prefix + MpText.Render(message.Text) : prefix + message.SenderName + MpText.Render("${ui_colon}") + message.Text;
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
        if (GUILayout.Button(channel == ChatChannel.Team ? MpText.Render("${ui_team_2}") : MpText.Render("${ui_public_2}"), compactButtonStyle!, GUILayout.Width(58f)))
            channel = channel == ChatChannel.Public ? ChatChannel.Team : ChatChannel.Public;
        GUI.enabled = true;
        var enterPressed = GUI.GetNameOfFocusedControl() == "XingyiStarryMpChatInput" &&
            Event.current.type == EventType.KeyDown &&
            (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
        GUI.SetNextControlName("XingyiStarryMpChatInput");
        input = GUILayout.TextField(input, 512, inputStyle!, GUILayout.ExpandWidth(true));
        IsTyping = GUI.GetNameOfFocusedControl() == "XingyiStarryMpChatInput";
        var submit = GUILayout.Button(MpText.Render("${ui_send}"), compactButtonStyle!, GUILayout.Width(58f));
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
                var state = !participant.Connected ? participant.Reconnecting ? "${ui_reconnecting_2}" : "${ui_offline}" : !room.MatchStarted
                    ? participant.Ready ? "${ui_ready}" : "${ui_not_ready}"
                    : participant.Admission == ParticipantAdmission.Spectator ? "${ui_spectator}" : participant.Admission == ParticipantAdmission.JoinSelection ? "${ui_selecting}" : seat?.Defeated == true ? "${ui_defeated}" : "${ui_alive}";
                var position = seat is null ? MpText.Render("${ui_no_seat_selected}") : MpText.Render($"${{ui_seat}}{seat.LobbySlotIndex + 1}${{ui_team_3}}{seat.Team + 1}");
                GUILayout.Label($"{(participant.IsHost ? "★" : "•")} {participant.DisplayName}    {position}    [{MpText.Render(state)}]", messageStyle!);
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
