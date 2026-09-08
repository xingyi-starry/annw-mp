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
    private const string SeatButtonName = "XingyiStarryMp_Seat";
    private static UI_MENU_POP_SkirmishSelect? screen;
    private static UI_MENU_LevelSelect_InfoSkm? info;
    private static Button? readyButton;
    private static TextMeshProUGUI? statusText;
    private static int lastAppliedRevision = -1;
    private static int lastRenderedRevision = -1;
    private static int lastDraftSignature;
    private static float nextRefresh;

    internal static bool Active { get; private set; }

    internal static void Enter(UI_MENU_MainMenu menu)
    {
        Active = true; lastAppliedRevision = -1; lastRenderedRevision = -1; lastDraftSignature = 0;
        menu.pop_skirmish.SetActive(false); menu.OnBtn_SkirmishNew();
    }

    internal static void Ensure(UI_MENU_POP_SkirmishSelect value)
    {
        if (!Active || value?.info_skirmish == null) return;
        screen = value; info = value.info_skirmish;
        if (!GameUiKit.Ensure()) return;
        BuildFooterControls();
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
        if (room != null && (room.DraftRevision != lastRenderedRevision || SeatLabelsChanged(room)))
        {
            RebuildSeatButtons(plugin, room); lastRenderedRevision = room.DraftRevision;
        }
        RefreshFooter(plugin, room);
    }

    internal static string CurrentMapId()
    {
        if (info == null) return "";
        var selectedOb = AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_ob")?.GetValue(info);
        if (selectedOb != null) return AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_ob_name")?.GetValue(info) as string ?? "";
        var path = AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_map")?.GetValue(info) as string;
        return string.IsNullOrEmpty(path) ? "" : Path.GetFileNameWithoutExtension(path);
    }

    private static void BuildFooterControls()
    {
        if (screen == null || readyButton != null) return;
        var confirm = screen.btn_confirm; if (confirm == null) return;
        readyButton = GameUiKit.Button(confirm.transform.parent, "XingyiStarryMp_Ready", "准备", () => XingyiStarryMpPlugin.Instance?.ToggleReady(), 150f);
        if (readyButton.transform is RectTransform readyRect && confirm.transform is RectTransform confirmRect)
        {
            readyRect.anchorMin = confirmRect.anchorMin; readyRect.anchorMax = confirmRect.anchorMax; readyRect.pivot = confirmRect.pivot;
            readyRect.sizeDelta = confirmRect.sizeDelta; readyRect.anchoredPosition = confirmRect.anchoredPosition + new Vector2(-Mathf.Max(165f, confirmRect.rect.width + 12f), 0f);
        }
        var statusHost = GameUiKit.Rect("XingyiStarryMp_Status", confirm.transform.parent);
        if (confirm.transform is RectTransform source)
        {
            statusHost.anchorMin = source.anchorMin; statusHost.anchorMax = source.anchorMax; statusHost.pivot = source.pivot;
            statusHost.sizeDelta = new Vector2(460f, 36f); statusHost.anchoredPosition = source.anchoredPosition + new Vector2(-235f, 52f);
        }
        statusText = GameUiKit.Text(statusHost, "Text", "请选择地图和席位", 17f, TextAlignmentOptions.MidlineRight);
    }

    private static void ApplyGuestDraft(RoomSnapshot room)
    {
        if (info == null || room.DraftRevision == lastAppliedRevision || string.IsNullOrEmpty(room.MapId)) return;
        var asset = Resources.Load<TextAsset>("Skirmish/" + room.MapId);
        if (asset != null)
        {
            info.Render(asset); info.dd_fow.SetValueWithoutNotify(room.FowType); info.dd_condition.SetValueWithoutNotify(room.WinCondition); info.dd_quickStart.SetValueWithoutNotify(room.QuickStart);
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
        }
        lastAppliedRevision = room.DraftRevision; lastRenderedRevision = -1;
    }

    private static void ApplyPermissions(XingyiStarryMpPlugin plugin, RoomSnapshot? room, string mapId)
    {
        if (info == null || screen == null) return;
        var host = plugin.IsHost;
        info.dd_fow.interactable = host; info.dd_condition.interactable = host; info.dd_quickStart.interactable = host;
        foreach (var item in Items())
        {
            foreach (var selectable in item.GetComponentsInChildren<Selectable>(true))
                if (selectable.gameObject.name != SeatButtonName) selectable.interactable = host;
        }
        if (screen.pool_maps != null)
            foreach (var button in screen.pool_maps.GetComponentsInChildren<Button>(true)) button.interactable = host;
        var canStart = plugin.IsHost && !string.IsNullOrEmpty(mapId) && room != null && room.Seats.Any(value => value.OriginallyHuman) && room.Seats.Where(value => value.OriginallyHuman).All(value => value.Connected && value.Ready);
        screen.btn_confirm.interactable = canStart;
        SetLabel(screen.btn_confirm, plugin.IsClient ? "等待主机开始" : "开始联机对局");
    }

    private static void RebuildSeatButtons(XingyiStarryMpPlugin plugin, RoomSnapshot room)
    {
        var localId = plugin.LocalIdentityId; var items = Items();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index]; var old = item.transform.Find(SeatButtonName);
            if (old != null) UnityEngine.Object.Destroy(old.gameObject);
            var seat = room.Seats.Find(value => value.LobbySlotIndex == index);
            if (seat == null || !item.is_open) continue;
            var slot = index;
            var button = GameUiKit.Button(item.transform, SeatButtonName, SeatLabel(seat), () => XingyiStarryMpPlugin.Instance?.ClaimSeat(slot), 142f);
            button.interactable = seat.OriginallyHuman && (!seat.Connected || seat.ClientId == localId);
            button.transform.SetAsLastSibling();
        }
    }

    private static bool SeatLabelsChanged(RoomSnapshot room)
    {
        var items = Items();
        foreach (var seat in room.Seats)
        {
            if (seat.LobbySlotIndex < 0 || seat.LobbySlotIndex >= items.Count) continue;
            var button = items[seat.LobbySlotIndex].transform.Find(SeatButtonName)?.GetComponent<Button>();
            var text = button?.GetComponentInChildren<TextMeshProUGUI>(true)?.text;
            if (text != SeatLabel(seat)) return true;
        }
        return false;
    }

    private static string SeatLabel(SeatInfo seat)
    {
        if (!seat.OriginallyHuman) return "原版 AI";
        if (!seat.Connected) return "空闲席位 · 点击入座";
        return seat.DisplayName + (seat.Ready ? "  ✓" : "  未准备");
    }

    private static void RefreshFooter(XingyiStarryMpPlugin plugin, RoomSnapshot? room)
    {
        var local = plugin.LocalIdentityId is Guid id ? room?.Seats.Find(value => value.ClientId == id) : null;
        if (readyButton != null)
        {
            readyButton.gameObject.SetActive(local != null); readyButton.interactable = local != null;
            GameUiKit.SetLabel(readyButton, local?.Ready == true ? "取消准备" : "准备");
        }
        if (statusText != null)
        {
            if (room == null) statusText.text = plugin.Status;
            else if (local == null) statusText.text = "点击玩家行末尾的“空闲席位”选择位置";
            else statusText.text = $"当前：{local.DisplayName} / P{local.LobbySlotIndex + 1}　{(local.Ready ? "已准备" : "未准备")}";
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

    private static void SetNearest(TMP_Dropdown dropdown, float[] values, float target)
    {
        if (dropdown == null || values.Length == 0) return;
        var best = 0; var distance = float.MaxValue;
        for (var i = 0; i < values.Length; i++) { var next = Mathf.Abs(values[i] - target); if (next < distance) { distance = next; best = i; } }
        dropdown.SetValueWithoutNotify(best);
    }

    private static void SetLabel(Button button, string value)
    {
        var localized = button.GetComponentInChildren<Localized_Txt>(true); if (localized != null) localized.enabled = false;
        var text = button.GetComponentInChildren<TextMeshProUGUI>(true); if (text != null) text.text = value;
    }
}

[HarmonyPatch(typeof(UI_MENU_POP_SkirmishSelect), nameof(UI_MENU_POP_SkirmishSelect.Show))]
internal static class NativeSkirmishShowPatch
{
    private static void Postfix(UI_MENU_POP_SkirmishSelect __instance) => NativeSkirmishLobby.Ensure(__instance);
}

[HarmonyPatch(typeof(UI_MENU_LevelSelect_InfoSkm), nameof(UI_MENU_LevelSelect_InfoSkm.StartLevel))]
internal static class NativeSkirmishStartPatch
{
    private static bool Prefix(UI_MENU_LevelSelect_InfoSkm __instance) => XingyiStarryMpPlugin.Instance?.AllowNativeSkirmishStart(__instance, NativeSkirmishLobby.CurrentMapId()) ?? true;
}
