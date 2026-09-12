using System;
using System.Collections.Generic;
using System.Linq;
using ANNW;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Ui;

internal static class PublicLobbyPanel
{
    private const string RootName = "XingyiStarryMp_PublicLobby";
    private static UI_MENU_MainMenu? menu;
    private static UI_MENU_POP_SkirmishSelect? hostPage;
    private static GameObject? root;
    private static RectTransform? roomContent;
    private static TMP_InputField? nameInput;
    private static TMP_InputField? roomNameInput;
    private static TMP_InputField? createPasswordInput;
    private static TMP_InputField? joinPasswordInput;
    private static TextMeshProUGUI? selectedRoomText;
    private static TextMeshProUGUI? status;
    private static Button? joinButton;
    private static RelayRoomInfo? selectedRoom;
    private static readonly List<GameObject> hiddenNativeObjects = new();
    private static IReadOnlyList<RelayRoomInfo> lastRooms = Array.Empty<RelayRoomInfo>();
    private static bool busy;
    private static bool enteringRoom;
    private static bool waitingForPage;
    private static float nextRefresh;
    private static int viewGeneration;

    internal static void Open(UI_MENU_MainMenu source)
    {
        DestroyView();
        menu = source;
        source.pop_skirmish.SetActive(false);
        source.OnBtn_SkirmishNew();
        waitingForPage = true;
        TryAttachToNativePage();
    }

    private static void TryAttachToNativePage()
    {
        hostPage = Resources.FindObjectsOfTypeAll<UI_MENU_POP_SkirmishSelect>()
            .FirstOrDefault(value => value != null && value.gameObject.activeInHierarchy);
        if (hostPage == null || !Build(hostPage.transform)) return;
        waitingForPage = false;
        enteringRoom = false;
        nextRefresh = 0f;
        root!.SetActive(true);
        root.transform.SetAsLastSibling();
    }

    internal static void Tick(XingyiStarryMpPlugin plugin)
    {
        if (root == null)
        {
            if (waitingForPage) TryAttachToNativePage();
            return;
        }
        if (!root.activeInHierarchy) return;
        if (enteringRoom && (plugin.IsHost || plugin.LocalClientId.HasValue))
        {
            EnterConfiguration();
            return;
        }
        if (status != null && enteringRoom) status.text = plugin.Status;
        if (enteringRoom && !plugin.IsHost && !plugin.IsClient && plugin.Status.IndexOf("失败", StringComparison.Ordinal) >= 0)
        {
            enteringRoom = false;
            nextRefresh = 0f;
        }
        if (!busy && !enteringRoom && Time.unscaledTime >= nextRefresh) RefreshRooms();
    }

    private static bool Build(Transform parent)
    {
        if (!GameUiKit.Ensure()) return false;
        HideNativePageObjects(parent);
        var full = GameUiKit.Rect(RootName, parent);
        GameUiKit.Stretch(full);
        root = full.gameObject;
        full.gameObject.AddComponent<Image>().color = new Color(0.11f, 0.045f, 0.008f, 0.82f);

        BuildHeader(full);
        var grid = GameUiKit.Rect("Grid", full);
        grid.anchorMin = Vector2.zero;
        grid.anchorMax = Vector2.one;
        grid.offsetMin = new Vector2(22f, 20f);
        grid.offsetMax = new Vector2(-22f, -86f);

        var left = GameUiKit.Rect("RoomsColumn", grid);
        left.anchorMin = new Vector2(0f, 0f);
        left.anchorMax = new Vector2(0.66f, 1f);
        left.offsetMin = Vector2.zero;
        left.offsetMax = new Vector2(-9f, 0f);
        GameUiKit.Panel(left, new Color(0.12f, 0.055f, 0.012f, 0.82f));
        BuildRoomColumn(left);

        var right = GameUiKit.Rect("ActionsColumn", grid);
        right.anchorMin = new Vector2(0.66f, 0f);
        right.anchorMax = Vector2.one;
        right.offsetMin = new Vector2(9f, 0f);
        right.offsetMax = Vector2.zero;
        GameUiKit.Panel(right, new Color(0.12f, 0.055f, 0.012f, 0.82f));
        BuildActionColumn(right);
        root.SetActive(false);
        return true;
    }

    private static void BuildHeader(RectTransform full)
    {
        var header = GameUiKit.Rect("Header", full);
        header.anchorMin = new Vector2(0f, 1f);
        header.anchorMax = Vector2.one;
        header.pivot = new Vector2(0.5f, 1f);
        header.sizeDelta = new Vector2(0f, 72f);
        header.anchoredPosition = Vector2.zero;
        var layout = header.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(22, 22, 14, 10);
        layout.spacing = 16f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = true;
        NativeButton(header, "Back", "返回", Close, 118f);
        var titleHost = GameUiKit.Rect("Title", header);
        titleHost.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        GameUiKit.Text(titleHost, "Text", "联机遭遇战", 34f, TextAlignmentOptions.MidlineLeft);
    }

    private static void BuildRoomColumn(RectTransform column)
    {
        var body = GameUiKit.Rect("Body", column);
        GameUiKit.Stretch(body);
        body.offsetMin = new Vector2(14f, 14f);
        body.offsetMax = new Vector2(-14f, -14f);
        var heading = GameUiKit.Rect("Heading", body);
        heading.anchorMin = new Vector2(0f, 1f);
        heading.anchorMax = Vector2.one;
        heading.pivot = new Vector2(0.5f, 1f);
        heading.sizeDelta = new Vector2(0f, 46f);
        heading.anchoredPosition = Vector2.zero;
        var headingLayout = heading.gameObject.AddComponent<HorizontalLayoutGroup>();
        headingLayout.spacing = 10f;
        headingLayout.childControlWidth = true;
        headingLayout.childControlHeight = true;
        headingLayout.childForceExpandWidth = false;
        headingLayout.childForceExpandHeight = true;
        var title = GameUiKit.Rect("Title", heading);
        title.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        GameUiKit.Text(title, "Text", "公共房间", 25f, TextAlignmentOptions.MidlineLeft);
        NativeButton(heading, "Refresh", "刷新", RefreshRooms, 120f);

        var viewport = GameUiKit.Rect("RoomViewport", body);
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = Vector2.zero;
        viewport.offsetMax = new Vector2(0f, -56f);
        GameUiKit.Panel(viewport, new Color(0.045f, 0.018f, 0.004f, 0.72f));
        viewport.gameObject.AddComponent<RectMask2D>();
        var content = GameUiKit.Rect("RoomContent", viewport);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero;
        content.sizeDelta = Vector2.zero;
        var contentLayout = content.gameObject.AddComponent<VerticalLayoutGroup>();
        contentLayout.padding = new RectOffset(8, 8, 8, 8);
        contentLayout.spacing = 7f;
        contentLayout.childControlWidth = true;
        contentLayout.childControlHeight = true;
        contentLayout.childForceExpandWidth = true;
        contentLayout.childForceExpandHeight = false;
        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var scroll = viewport.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        roomContent = content;
    }

    private static void BuildActionColumn(RectTransform column)
    {
        var body = GameUiKit.Rect("Body", column);
        GameUiKit.Stretch(body);
        body.offsetMin = new Vector2(16f, 14f);
        body.offsetMax = new Vector2(-16f, -14f);
        var vertical = body.gameObject.AddComponent<VerticalLayoutGroup>();
        vertical.spacing = 8f;
        vertical.childControlWidth = true;
        vertical.childControlHeight = true;
        vertical.childForceExpandWidth = true;
        vertical.childForceExpandHeight = false;

        AddText(body, "玩家", 24f, 38f, TextAlignmentOptions.MidlineLeft);
        nameInput = AddInputRow(body, "用户名", XingyiStarryMpPlugin.Instance?.ConfiguredDisplayName ?? Environment.UserName, "输入用户名");
        AddSeparator(body);
        AddText(body, "创建房间", 24f, 38f, TextAlignmentOptions.MidlineLeft);
        roomNameInput = AddInputRow(body, "房间名", (XingyiStarryMpPlugin.Instance?.ConfiguredDisplayName ?? Environment.UserName) + " 的房间", "输入房间名");
        createPasswordInput = AddInputRow(body, "密码", "", "可选");
        createPasswordInput.contentType = TMP_InputField.ContentType.Password;
        NativeButton(body, "Create", "创建房间", CreateRoom);
        AddSeparator(body);
        AddText(body, "加入房间", 24f, 38f, TextAlignmentOptions.MidlineLeft);
        selectedRoomText = AddText(body, "请先在左侧选择一个房间", 16f, 58f, TextAlignmentOptions.TopLeft);
        joinPasswordInput = AddInputRow(body, "密码", "", "有密码时输入");
        joinPasswordInput.contentType = TMP_InputField.ContentType.Password;
        joinButton = NativeButton(body, "Join", "加入所选房间", JoinSelected);
        joinButton.interactable = false;
        AddSeparator(body);
        NativeButton(body, "Lan", "局域网联机", OpenLan);
        var spacer = GameUiKit.Rect("Spacer", body);
        spacer.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1f;
        status = AddText(body, "正在读取公共房间", 16f, 48f, TextAlignmentOptions.BottomLeft);
        RefreshSelectedRoom();
    }

    private static void AddSeparator(Transform parent)
    {
        var separator = GameUiKit.Rect("Separator", parent);
        separator.gameObject.AddComponent<LayoutElement>().preferredHeight = 2f;
        separator.gameObject.AddComponent<Image>().color = new Color(0.8f, 0.35f, 0.05f, 0.65f);
    }

    private static TextMeshProUGUI AddText(Transform parent, string value, float size, float height, TextAlignmentOptions alignment)
    {
        var row = GameUiKit.Rect("Text", parent);
        row.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
        return GameUiKit.Text(row, "Value", value, size, alignment);
    }

    private static TMP_InputField AddInputRow(Transform parent, string label, string value, string placeholderValue)
    {
        var row = GameUiKit.Rect(label, parent);
        var rowLayout = row.gameObject.AddComponent<LayoutElement>();
        rowLayout.minHeight = 40f;
        rowLayout.preferredHeight = 40f;
        rowLayout.flexibleHeight = 0f;
        var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 10f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        var labelRoot = GameUiKit.Rect("Label", row);
        var labelLayout = labelRoot.gameObject.AddComponent<LayoutElement>();
        labelLayout.preferredWidth = 88f;
        labelLayout.flexibleWidth = 0f;
        GameUiKit.Text(labelRoot, "Text", label, 18f, TextAlignmentOptions.MidlineRight);
        return CompactInput(row, "XingyiStarryMp_" + label + row.GetInstanceID(), value, placeholderValue);
    }

    private static TMP_InputField CompactInput(Transform parent, string name, string value, string placeholderValue)
    {
        var rect = GameUiKit.Rect(name, parent);
        GameUiKit.Panel(rect, new Color(0.18f, 0.075f, 0.012f, 0.96f));
        var viewport = GameUiKit.Rect("Text Area", rect);
        GameUiKit.Stretch(viewport);
        viewport.offsetMin = new Vector2(12f, 4f);
        viewport.offsetMax = new Vector2(-12f, -4f);
        viewport.gameObject.AddComponent<RectMask2D>();
        var placeholder = GameUiKit.Text(viewport, "Placeholder", placeholderValue, 18f, TextAlignmentOptions.MidlineLeft);
        placeholder.color = new Color(0.72f, 0.45f, 0.2f, 0.7f);
        var text = GameUiKit.Text(viewport, "Text", value, 18f, TextAlignmentOptions.MidlineLeft);
        var input = rect.gameObject.AddComponent<TMP_InputField>();
        input.textViewport = viewport;
        input.textComponent = text;
        input.placeholder = placeholder;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.text = value;
        var inputLayout = rect.gameObject.AddComponent<LayoutElement>();
        inputLayout.minHeight = 40f;
        inputLayout.preferredHeight = 40f;
        inputLayout.flexibleHeight = 0f;
        inputLayout.flexibleWidth = 1f;
        return input;
    }

    private static Button NativeButton(Transform parent, string name, string label, UnityEngine.Events.UnityAction action, float width = 0f, float height = 40f)
    {
        var template = hostPage?.btn_confirm;
        if (template == null) return GameUiKit.Button(parent, name, label, action, width);
        var instance = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
        instance.name = name;
        instance.SetActive(true);
        foreach (var localized in instance.GetComponentsInChildren<Localized_Txt>(true))
        {
            localized.enabled = false;
            UnityEngine.Object.Destroy(localized);
        }
        var button = instance.GetComponent<Button>() ?? instance.GetComponentInChildren<Button>(true);
        if (button == null)
        {
            UnityEngine.Object.Destroy(instance);
            return GameUiKit.Button(parent, name, label, action, width);
        }
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(action);
        var text = instance.GetComponentInChildren<TextMeshProUGUI>(true);
        if (text != null)
        {
            text.text = label;
            text.fontSize = 19f;
            text.enableAutoSizing = true;
            text.fontSizeMin = 12f;
            text.fontSizeMax = 19f;
        }
        var element = instance.GetComponent<LayoutElement>() ?? instance.AddComponent<LayoutElement>();
        element.ignoreLayout = false;
        element.minHeight = height;
        element.preferredHeight = height;
        element.flexibleHeight = 0f;
        if (width > 0f)
        {
            element.minWidth = width;
            element.preferredWidth = width;
            element.flexibleWidth = 0f;
        }
        else
        {
            element.minWidth = 0f;
            element.preferredWidth = 0f;
            element.flexibleWidth = 1f;
        }
        return button;
    }

    private static bool ApplyName()
    {
        var plugin = XingyiStarryMpPlugin.Instance;
        var value = nameInput?.text.Trim() ?? "";
        if (plugin == null || value.Length == 0 || value.Length > 32)
        {
            if (status != null) status.text = "用户名长度必须为 1–32 个字符";
            return false;
        }
        plugin.ConfiguredDisplayName = value;
        return true;
    }

    private static void CreateRoom()
    {
        if (busy || enteringRoom || !ApplyName()) return;
        var name = roomNameInput?.text.Trim() ?? "";
        var password = createPasswordInput?.text ?? "";
        if (name.Length == 0 || name.Length > 48)
        {
            if (status != null) status.text = "房间名长度必须为 1–48 个字符";
            return;
        }
        if (password.Length > 64)
        {
            if (status != null) status.text = "房间密码不能超过 64 个字符";
            return;
        }
        enteringRoom = true;
        XingyiStarryMpPlugin.Instance?.HostPublic(name, password);
    }

    private static async void RefreshRooms()
    {
        if (busy || enteringRoom) return;
        var plugin = XingyiStarryMpPlugin.Instance;
        if (plugin == null) return;
        var generation = viewGeneration;
        busy = true;
        nextRefresh = Time.unscaledTime + 5f;
        if (status != null) status.text = "正在读取公共房间";
        try
        {
            var rooms = await plugin.ListPublicRoomsAsync();
            if (generation != viewGeneration || root == null) return;
            Rebuild(rooms);
            if (status != null) status.text = "公共房间列表已更新";
        }
        catch (Exception ex)
        {
            if (generation == viewGeneration && status != null) status.text = "读取房间失败：" + ex.Message;
        }
        finally
        {
            if (generation == viewGeneration) busy = false;
        }
    }

    private static void Rebuild(IReadOnlyList<RelayRoomInfo> rooms)
    {
        var plugin = XingyiStarryMpPlugin.Instance;
        if (roomContent == null || plugin == null) return;
        lastRooms = rooms;
        for (var index = roomContent.childCount - 1; index >= 0; index--)
            UnityEngine.Object.Destroy(roomContent.GetChild(index).gameObject);

        selectedRoom = selectedRoom == null ? null : rooms.FirstOrDefault(value => value.RoomId == selectedRoom.RoomId);
        RefreshSelectedRoom();
        if (rooms.Count == 0)
        {
            var empty = NativeButton(roomContent, "Empty", "当前没有公共房间", () => { }, 0f, 44f);
            empty.interactable = false;
            return;
        }

        foreach (var room in rooms)
        {
            var captured = room;
            var lockText = room.HasPassword ? "  [密码]" : "";
            var map = string.IsNullOrWhiteSpace(room.MapTitle) ? "尚未选择地图" : room.MapTitle;
            var state = room.Status == RelayRoomStatus.Waiting ? "等待中" : room.Status == RelayRoomStatus.Playing ? "游戏中" : "已关闭";
            var selected = selectedRoom?.RoomId == room.RoomId ? "▶  " : "";
            var label = plugin.IsPublicRoomCompatible(room)
                ? $"{selected}{room.RoomName}{lockText}    {map}    {room.ConnectedPlayers}/{room.HumanSeats}    {room.HostName}    {state}"
                : $"{room.RoomName}    版本不兼容";
            var button = NativeButton(roomContent, "Room_" + room.RoomId.ToString("N"), label, () => SelectRoom(captured), 0f, 46f);
            button.interactable = room.Status == RelayRoomStatus.Waiting && plugin.IsPublicRoomCompatible(room);
        }
    }

    private static void SelectRoom(RelayRoomInfo room)
    {
        selectedRoom = room;
        if (joinPasswordInput != null) joinPasswordInput.text = "";
        Rebuild(lastRooms);
    }

    private static void RefreshSelectedRoom()
    {
        var plugin = XingyiStarryMpPlugin.Instance;
        if (selectedRoomText == null || joinButton == null || joinPasswordInput == null || plugin == null) return;
        if (selectedRoom == null)
        {
            selectedRoomText.text = "请先在左侧选择一个房间";
            joinPasswordInput.interactable = false;
            joinPasswordInput.text = "";
            joinButton.interactable = false;
            return;
        }
        var room = selectedRoom;
        var map = string.IsNullOrWhiteSpace(room.MapTitle) ? "尚未选择地图" : room.MapTitle;
        selectedRoomText.text = $"{room.RoomName}\n主机：{room.HostName}    地图：{map}";
        joinPasswordInput.interactable = room.HasPassword;
        if (!room.HasPassword) joinPasswordInput.text = "";
        joinButton.interactable = room.Status == RelayRoomStatus.Waiting && plugin.IsPublicRoomCompatible(room);
    }

    private static void JoinSelected()
    {
        var room = selectedRoom;
        if (room == null || busy || enteringRoom || !ApplyName()) return;
        var password = joinPasswordInput?.text ?? "";
        if (room.HasPassword && password.Length == 0)
        {
            if (status != null) status.text = "请输入所选房间的密码";
            return;
        }
        enteringRoom = true;
        XingyiStarryMpPlugin.Instance?.JoinPublic(room.RoomId, password);
    }

    private static void OpenLan()
    {
        var source = menu;
        var page = hostPage;
        DestroyView();
        if (page != null && page.gameObject.activeInHierarchy) page.Hide();
        if (source != null) NativeLobbyPanel.Open(source, true);
    }

    private static void EnterConfiguration()
    {
        var source = menu;
        var page = hostPage;
        DestroyView();
        menu = null;
        if (page != null) NativeSkirmishLobby.EnterExisting(page);
        else if (source != null) NativeSkirmishLobby.Enter(source);
    }

    private static void Close()
    {
        XingyiStarryMpPlugin.Instance?.Disconnect();
        var page = hostPage;
        DestroyView();
        menu = null;
        if (page != null && page.gameObject.activeInHierarchy) page.Hide();
    }

    private static void HideNativePageObjects(Transform page)
    {
        hiddenNativeObjects.Clear();
        for (var index = 0; index < page.childCount; index++)
        {
            var child = page.GetChild(index).gameObject;
            if (!child.activeSelf) continue;
            hiddenNativeObjects.Add(child);
            child.SetActive(false);
        }
    }

    private static void RestoreNativePageObjects()
    {
        foreach (var item in hiddenNativeObjects)
            if (item != null) item.SetActive(true);
        hiddenNativeObjects.Clear();
    }

    private static void DestroyView()
    {
        viewGeneration++;
        if (root != null)
        {
            root.SetActive(false);
            UnityEngine.Object.Destroy(root);
        }
        RestoreNativePageObjects();
        root = null;
        hostPage = null;
        roomContent = null;
        nameInput = null;
        roomNameInput = null;
        createPasswordInput = null;
        joinPasswordInput = null;
        selectedRoomText = null;
        status = null;
        joinButton = null;
        selectedRoom = null;
        lastRooms = Array.Empty<RelayRoomInfo>();
        busy = false;
        enteringRoom = false;
        waitingForPage = false;
    }
}
