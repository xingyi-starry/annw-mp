using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ANNW;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Ui;

internal static class NativeSkirmishLobby
{
    private const string RootName = "XingyiStarryMp_RoomControls";
    private static readonly Dictionary<Selectable, bool> originalInteractable = new Dictionary<Selectable, bool>();
    private static UI_MENU_POP_SkirmishSelect? screen;
    private static UI_MENU_LevelSelect_InfoSkm? info;
    private static GameObject? roomRoot;
    private static RectTransform? seatsRow;
    private static TextMeshProUGUI? statusText;
    private static int lastAppliedRevision = -1;
    private static int lastSeatSignature;
    private static int lastDraftSignature;
    private static float nextRefresh;

    internal static bool Active { get; private set; }

    internal static void Enter(UI_MENU_MainMenu menu)
    {
        CleanupUi();
        Active = true; lastAppliedRevision = -1; lastSeatSignature = 0; lastDraftSignature = 0;
        menu.pop_skirmish.SetActive(false); menu.OnBtn_SkirmishNew();
    }

    internal static void Ensure(UI_MENU_POP_SkirmishSelect value)
    {
        if (!Active || value?.info_skirmish == null) return;
        screen = value; info = value.info_skirmish;
        if (GameUiKit.Ensure()) BuildRoomControls();
    }

    internal static void OnInfoRendered(UI_MENU_LevelSelect_InfoSkm value)
    {
        if (!Active) return;
        info = value;
        var plugin = XingyiStarryMpPlugin.Instance; var mapId = CurrentMapId();
        if (plugin?.IsHost == true && !string.IsNullOrEmpty(mapId))
        {
            plugin.SyncLobbyDraft(value, mapId);
            lastDraftSignature = DraftSignature(value, mapId);
        }
    }

    internal static void Tick(XingyiStarryMpPlugin plugin)
    {
        if (!Active || screen == null || info == null || Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + 0.2f;
        var mapId = CurrentMapId();
        if (plugin.IsHost && !string.IsNullOrEmpty(mapId))
        {
            var signature = DraftSignature(info, mapId);
            if (signature != lastDraftSignature) { lastDraftSignature = signature; plugin.SyncLobbyDraft(info, mapId); }
        }
        var room = plugin.CurrentRoom;
        if (plugin.IsClient && room != null) ApplyGuestDraft(room);
        ApplyPermissions(plugin, room, mapId);
        var seatSignature = SeatSignature(room, plugin.LocalIdentityId);
        if (seatSignature != lastSeatSignature) { lastSeatSignature = seatSignature; RebuildSeats(plugin, room); }
        RefreshStatus(plugin, room);
    }

    internal static string CurrentMapId()
    {
        if (info == null) return "";
        var selectedOb = AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_ob")?.GetValue(info);
        if (selectedOb != null) return AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_ob_name")?.GetValue(info) as string ?? "";
        var path = AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_map")?.GetValue(info) as string;
        return string.IsNullOrEmpty(path) ? "" : Path.GetFileNameWithoutExtension(path);
    }

    internal static void MarkStartingMatch() => CleanupUi();

    internal static void OnPageLeaving()
    {
        if (!Active) return;
        XingyiStarryMpPlugin.Instance?.Disconnect();
        CleanupUi();
    }

    internal static void TerminateFromRemote()
    {
        var currentScreen = screen;
        CleanupUi();
        if (currentScreen != null && currentScreen.gameObject.activeInHierarchy) currentScreen.Hide();
    }

    private static void BuildRoomControls()
    {
        if (info == null || roomRoot != null) return;
        var old = info.transform.Find(RootName); if (old != null) UnityEngine.Object.Destroy(old.gameObject);
        var root = GameUiKit.Rect(RootName, info.transform);
        root.anchorMin = new Vector2(0f, 0f); root.anchorMax = new Vector2(1f, 0f); root.pivot = new Vector2(0.5f, 0f);
        root.sizeDelta = new Vector2(-20f, 104f); root.anchoredPosition = new Vector2(0f, 62f);
        GameUiKit.Panel(root, new Color(0.12f, 0.065f, 0.025f, 0.94f));
        var vertical = root.gameObject.AddComponent<VerticalLayoutGroup>();
        vertical.padding = new RectOffset(8, 8, 6, 6); vertical.spacing = 5f; vertical.childControlWidth = true; vertical.childControlHeight = true; vertical.childForceExpandWidth = true; vertical.childForceExpandHeight = false;
        var statusHost = GameUiKit.Rect("Status", root); statusHost.gameObject.AddComponent<LayoutElement>().preferredHeight = 27f;
        statusText = GameUiKit.Text(statusHost, "Text", "请选择地图", 16f, TextAlignmentOptions.Center);
        seatsRow = GameUiKit.Rect("Seats", root); seatsRow.gameObject.AddComponent<LayoutElement>().preferredHeight = 54f;
        var horizontal = seatsRow.gameObject.AddComponent<HorizontalLayoutGroup>();
        horizontal.spacing = 7f; horizontal.childControlWidth = true; horizontal.childControlHeight = true; horizontal.childForceExpandWidth = true; horizontal.childForceExpandHeight = true;
        roomRoot = root.gameObject;
    }

    private static void ApplyGuestDraft(RoomSnapshot room)
    {
        if (info == null || screen == null || room.DraftRevision == lastAppliedRevision || string.IsNullOrEmpty(room.MapId)) return;
        var asset = Resources.Load<TextAsset>("Skirmish/" + room.MapId);
        if (asset == null) return;
        info.gameObject.SetActive(true);
        screen.OnSelectMap(asset);
        info.dd_fow.SetValueWithoutNotify(room.FowType); info.dd_condition.SetValueWithoutNotify(room.WinCondition); info.dd_quickStart.SetValueWithoutNotify(room.QuickStart);
        var items = Items();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index]; var seat = room.Seats.Find(value => value.LobbySlotIndex == index);
            if ((seat != null) != item.is_open) item.OnBtnSwitch();
            if (seat == null) continue;
            item.ForceSetControl(seat.Controller); item.ForceSetTeam(seat.Team); item.ForceSetColor(seat.Color); item.ForceSetPos(seat.PositionRandom ? -1 : seat.Position);
            SetNearest(item.dd_res, DataUtils.SkirmishResMulOptions, seat.ResourceMultiplier);
            SetNearest(item.dd_ai_intell, DataUtils.SkirmishAIIntelOptions, seat.AiIntelligence);
        }
        lastAppliedRevision = room.DraftRevision;
    }

    private static void ApplyPermissions(XingyiStarryMpPlugin plugin, RoomSnapshot? room, string mapId)
    {
        if (info == null || screen == null) return;
        if (plugin.IsClient)
        {
            Disable(info.dd_fow); Disable(info.dd_condition); Disable(info.dd_quickStart);
            foreach (var item in Items()) foreach (var selectable in item.GetComponentsInChildren<Selectable>(true)) Disable(selectable);
            if (screen.pool_maps != null) foreach (var button in screen.pool_maps.GetComponentsInChildren<Button>(true)) Disable(button);
        }
        Store(screen.btn_confirm);
        var canStart = plugin.IsHost && !string.IsNullOrEmpty(mapId) && room != null && room.Seats.Any(value => value.OriginallyHuman) && room.Seats.Where(value => value.OriginallyHuman).All(value => value.Connected && value.Ready);
        screen.btn_confirm.interactable = canStart;
        SetConfirmLabel(plugin.IsClient ? "等待主机开始" : "开始联机对局");
    }

    private static void RebuildSeats(XingyiStarryMpPlugin plugin, RoomSnapshot? room)
    {
        if (seatsRow == null) return;
        for (var index = seatsRow.childCount - 1; index >= 0; index--) UnityEngine.Object.Destroy(seatsRow.GetChild(index).gameObject);
        var humans = room?.Seats.Where(value => value.OriginallyHuman).OrderBy(value => value.LobbySlotIndex).ToList() ?? new List<SeatInfo>();
        if (humans.Count == 0)
        {
            var none = GameUiKit.Button(seatsRow, "NoSeats", plugin.IsHost ? "请将至少一个玩家设置为“人类”" : "等待主机设置真人席位", () => { }); none.interactable = false;
        }
        foreach (var seat in humans)
        {
            var slot = seat.LobbySlotIndex;
            var button = GameUiKit.Button(seatsRow, "Seat" + slot, SeatLabel(seat), () => XingyiStarryMpPlugin.Instance?.ClaimSeat(slot));
            button.interactable = !seat.Connected || seat.ClientId == plugin.LocalIdentityId;
        }
        var local = plugin.LocalIdentityId is Guid id ? room?.Seats.Find(value => value.ClientId == id) : null;
        var ready = GameUiKit.Button(seatsRow, "Ready", local?.Ready == true ? "取消准备" : "准备", () => XingyiStarryMpPlugin.Instance?.ToggleReady());
        ready.interactable = local != null;
    }

    private static void RefreshStatus(XingyiStarryMpPlugin plugin, RoomSnapshot? room)
    {
        if (statusText == null) return;
        var local = plugin.LocalIdentityId is Guid id ? room?.Seats.Find(value => value.ClientId == id) : null;
        if (room == null || string.IsNullOrEmpty(room.MapId)) statusText.text = plugin.IsHost ? "请选择地图并设置 Human / AI" : "等待主机选择地图";
        else if (local == null) statusText.text = "选择一个未占用的真人席位；已占用席位会置灰";
        else statusText.text = $"{local.DisplayName} · P{local.LobbySlotIndex + 1} · {(local.Ready ? "已准备" : "未准备")}";
    }

    private static int SeatSignature(RoomSnapshot? room, Guid? localId)
    {
        unchecked
        {
            var value = room?.DraftRevision ?? 0; value = value * 31 + (localId?.GetHashCode() ?? 0);
            if (room != null) foreach (var seat in room.Seats)
            {
                value = value * 31 + seat.LobbySlotIndex; value = value * 31 + (seat.OriginallyHuman ? 1 : 0); value = value * 31 + (seat.Connected ? 1 : 0);
                value = value * 31 + (seat.Ready ? 1 : 0); value = value * 31 + (seat.ClientId?.GetHashCode() ?? 0); value = value * 31 + seat.DisplayName.GetHashCode();
            }
            return value;
        }
    }

    private static List<UI_SKM_PlayerSetting> Items()
    {
        if (info?.group == null) return new List<UI_SKM_PlayerSetting>();
        return AccessTools.Method(typeof(UI_SKM_PlayerSettingGroup), "GetItems")?.Invoke(info.group, Array.Empty<object>()) as List<UI_SKM_PlayerSetting> ?? new List<UI_SKM_PlayerSetting>();
    }

    private static int DraftSignature(UI_MENU_LevelSelect_InfoSkm target, string mapId)
    {
        unchecked
        {
            var value = mapId.GetHashCode(); value = value * 31 + target.dd_fow.value; value = value * 31 + target.dd_condition.value; value = value * 31 + target.dd_quickStart.value;
            foreach (var item in Items())
            {
                value = value * 31 + (item.is_open ? 1 : 0);
                foreach (var dropdown in item.GetComponentsInChildren<TMP_Dropdown>(true)) value = value * 31 + dropdown.value;
            }
            return value;
        }
    }

    private static void Disable(Selectable selectable) { Store(selectable); selectable.interactable = false; }
    private static void Store(Selectable selectable) { if (selectable != null && !originalInteractable.ContainsKey(selectable)) originalInteractable.Add(selectable, selectable.interactable); }

    private static void CleanupUi()
    {
        Active = false;
        foreach (var pair in originalInteractable) if (pair.Key != null) pair.Key.interactable = pair.Value;
        originalInteractable.Clear();
        if (screen?.btn_confirm != null)
        {
            var localized = screen.btn_confirm.GetComponentInChildren<Localized_Txt>(true);
            if (localized != null) { localized.enabled = true; localized.RenderLocalizedContent(); }
        }
        if (roomRoot != null) UnityEngine.Object.Destroy(roomRoot);
        roomRoot = null; seatsRow = null; statusText = null; screen = null; info = null;
        lastAppliedRevision = -1; lastSeatSignature = 0; lastDraftSignature = 0;
    }

    private static string SeatLabel(SeatInfo seat) => !seat.Connected ? $"P{seat.LobbySlotIndex + 1} 空闲" : $"P{seat.LobbySlotIndex + 1} {seat.DisplayName}{(seat.Ready ? " ✓" : "")}";

    private static void SetNearest(TMP_Dropdown dropdown, float[] values, float target)
    {
        if (dropdown == null || values.Length == 0) return;
        var best = 0; var distance = float.MaxValue;
        for (var index = 0; index < values.Length; index++) { var next = Mathf.Abs(values[index] - target); if (next < distance) { distance = next; best = index; } }
        dropdown.SetValueWithoutNotify(best);
    }

    private static void SetConfirmLabel(string value)
    {
        if (screen?.btn_confirm == null) return;
        var localized = screen.btn_confirm.GetComponentInChildren<Localized_Txt>(true); if (localized != null) localized.enabled = false;
        var text = screen.btn_confirm.GetComponentInChildren<TextMeshProUGUI>(true); if (text != null) text.text = value;
    }
}

[HarmonyPatch(typeof(UI_MENU_POP_SkirmishSelect), nameof(UI_MENU_POP_SkirmishSelect.Show))]
internal static class NativeSkirmishShowPatch
{
    private static void Postfix(UI_MENU_POP_SkirmishSelect __instance) => NativeSkirmishLobby.Ensure(__instance);
}

[HarmonyPatch(typeof(UI_MENU_POP_SkirmishSelect), nameof(UI_MENU_POP_SkirmishSelect.Hide))]
internal static class NativeSkirmishHidePatch
{
    private static void Prefix() => NativeSkirmishLobby.OnPageLeaving();
}

[HarmonyPatch(typeof(UI_MENU_LevelSelect_InfoSkm), nameof(UI_MENU_LevelSelect_InfoSkm.Render), new[] { typeof(TextAsset) })]
internal static class NativeSkirmishBuiltinRenderPatch
{
    private static void Postfix(UI_MENU_LevelSelect_InfoSkm __instance) => NativeSkirmishLobby.OnInfoRendered(__instance);
}

[HarmonyPatch(typeof(UI_MENU_LevelSelect_InfoSkm), nameof(UI_MENU_LevelSelect_InfoSkm.Render), new[] { typeof(string) })]
internal static class NativeSkirmishLocalRenderPatch
{
    private static void Postfix(UI_MENU_LevelSelect_InfoSkm __instance) => NativeSkirmishLobby.OnInfoRendered(__instance);
}

[HarmonyPatch(typeof(UI_MENU_LevelSelect_InfoSkm), nameof(UI_MENU_LevelSelect_InfoSkm.StartLevel))]
internal static class NativeSkirmishStartPatch
{
    private static bool Prefix(UI_MENU_LevelSelect_InfoSkm __instance) => XingyiStarryMpPlugin.Instance?.AllowNativeSkirmishStart(__instance, NativeSkirmishLobby.CurrentMapId()) ?? true;
}
