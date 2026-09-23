using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    private static RectTransform? roomRect;
    private static RectTransform? actionsRow;
    private static readonly Dictionary<UI_SKM_PlayerSetting, SeatSlotAugmentation> rowAugmentations = new Dictionary<UI_SKM_PlayerSetting, SeatSlotAugmentation>();
    private static TextMeshProUGUI? statusText;
    private static GameObject? syntheticSeatRoot;
    private static int lastAppliedRevision = -1;
    private static int lastSeatSignature;
    private static int lastDraftSignature;
    private static float nextRefresh;
    private static byte[]? cachedMapPreview;
    private static bool cachedUserMap;
    private static string previewError = "";

    internal static bool Active { get; private set; }
    internal static UI_MENU_LevelSelect_InfoSkm? CurrentInfo => info;

    internal static void Enter(UI_MENU_MainMenu menu)
    {
        CleanupUi();
        Active = true; lastAppliedRevision = -1; lastSeatSignature = 0; lastDraftSignature = 0;
        menu.pop_skirmish.SetActive(false); menu.OnBtn_SkirmishNew();
    }

    internal static void EnterExisting(UI_MENU_POP_SkirmishSelect value)
    {
        CleanupUi();
        Active = true; lastAppliedRevision = -1; lastSeatSignature = 0; lastDraftSignature = 0;
        Ensure(value);
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
        cachedMapPreview = null; previewError = "";
        info = value;
        var plugin = XingyiStarryMpPlugin.Instance; var mapId = CurrentMapId();
        if (plugin?.IsHost == true && plugin.CurrentRoom?.SavedGame != true && !string.IsNullOrEmpty(mapId))
        {
            plugin.SyncLobbyDraft(value, mapId);
            lastDraftSignature = DraftSignature(value, mapId);
        }
    }

    internal static void Tick(XingyiStarryMpPlugin plugin)
    {
        if (!Active || screen == null || info == null || Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + 0.2f;
        if (roomRect != null) PositionAboveNativeFooter(roomRect);
        var mapId = CurrentMapId();
        var room = plugin.CurrentRoom;
        if (plugin.IsHost && room?.SavedGame != true && !string.IsNullOrEmpty(mapId))
        {
            var signature = DraftSignature(info, mapId);
            if (signature != lastDraftSignature) { lastDraftSignature = signature; plugin.SyncLobbyDraft(info, mapId); }
        }
        if (room != null && (plugin.IsClient || room.SavedGame))
        {
            try { ApplyGuestDraft(room); }
            catch (Exception ex)
            {
                previewError = "主机地图预览无效：" + ex.Message;
                lastAppliedRevision = room.DraftRevision;
                info.gameObject.SetActive(false);
                Debug.LogError("[XingyiStarry MP] " + previewError);
            }
        }
        if (plugin.IsClient && room?.MatchStarted != true && room?.SavedGame != true && !string.IsNullOrEmpty(mapId))
        {
            var signature = DraftSignature(info, mapId);
            if (signature != lastDraftSignature) { lastDraftSignature = signature; plugin.SyncLobbyDraft(info, mapId); }
        }
        ApplyPermissions(plugin, room, mapId);
        EnsureMountedSeatActions();
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

    internal static byte[] CaptureMapPreview(UI_MENU_LevelSelect_InfoSkm value, out bool userMap)
    {
        if (cachedMapPreview is not null) { userMap = cachedUserMap; return cachedMapPreview; }
        var selected = AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_ob")?.GetValue(value) as DynOb;
        userMap = selected is null;
        if (userMap)
        {
            var path = AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_map")?.GetValue(value) as string;
            selected = Singleton<BattleAndMapFileSystem>.self.ReadFileWithMeta_Local(path ?? "");
        }
        if (selected is null) throw new InvalidDataException("原版未能读取地图预览。");
        var preview = new DynOb();
        preview.SetKey("terrain", selected.GetKey_Obj("terrain"));
        preview.SetKey("commander", selected.GetKey_Obj("commander"));
        if (selected.HasKey("units")) preview.SetKey("units", selected.GetKey_Obj("units"));
        if (selected.HasKey("lock_fow_setting")) preview.SetKey("lock_fow_setting", selected.GetKey_Enum("lock_fow_setting", FOW_Type.None));
        if (selected.HasKey("lock_win_condition")) preview.SetKey("lock_win_condition", selected.GetKey_Enum("lock_win_condition", SkirmishWinCondition.None));
        if (selected.HasKey("lock_quick_start")) preview.SetKey("lock_quick_start", selected.GetKey_Enum("lock_quick_start", QuickStartSetting.None));
        var plain = Encoding.UTF8.GetBytes(preview.ToString());
        if (plain.Length > ProtocolConstants.MaxExpandedMapPreviewBytes) throw new InvalidDataException("地图预览数据过大。");
        var compressed = SnapshotCodec.Compress(plain);
        if (compressed.Length > ProtocolConstants.MaxMapPreviewBytes) throw new InvalidDataException("地图预览压缩数据过大。");
        cachedUserMap = userMap; cachedMapPreview = compressed;
        return compressed;
    }

    internal static void ShowMapError(string message) => previewError = message;

    internal static void MarkStartingMatch() => CleanupUi();

    internal static void RestoreRowsForPoolReset()
    {
        foreach (var item in rowAugmentations.Keys.ToArray()) RestoreRow(item);
        lastSeatSignature = 0;
    }

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
        root.sizeDelta = new Vector2(-20f, 104f);
        PositionAboveNativeFooter(root);
        GameUiKit.Panel(root, new Color(0.12f, 0.065f, 0.025f, 0.94f));
        var vertical = root.gameObject.AddComponent<VerticalLayoutGroup>();
        vertical.padding = new RectOffset(8, 8, 6, 6); vertical.spacing = 5f; vertical.childControlWidth = true; vertical.childControlHeight = true; vertical.childForceExpandWidth = true; vertical.childForceExpandHeight = false;
        var statusHost = GameUiKit.Rect("Status", root); statusHost.gameObject.AddComponent<LayoutElement>().preferredHeight = 27f;
        statusText = GameUiKit.Text(statusHost, "Text", "请选择地图", 16f, TextAlignmentOptions.Center);
        actionsRow = GameUiKit.Rect("Actions", root); actionsRow.gameObject.AddComponent<LayoutElement>().preferredHeight = 44f;
        var horizontal = actionsRow.gameObject.AddComponent<HorizontalLayoutGroup>();
        horizontal.spacing = 7f; horizontal.childControlWidth = true; horizontal.childControlHeight = true; horizontal.childForceExpandWidth = true; horizontal.childForceExpandHeight = true;
        roomRect = root; roomRoot = root.gameObject;
    }

    private static void PositionAboveNativeFooter(RectTransform root)
    {
        if (info == null || screen?.btn_confirm == null) { root.anchoredPosition = new Vector2(0f, 68f); return; }
        var infoRect = info.transform as RectTransform;
        var confirmRect = screen.btn_confirm.transform as RectTransform;
        if (infoRect == null || confirmRect == null) { root.anchoredPosition = new Vector2(0f, 68f); return; }
        var corners = new Vector3[4]; confirmRect.GetWorldCorners(corners);
        var footerTop = float.MinValue;
        foreach (var corner in corners) footerTop = Mathf.Max(footerTop, infoRect.InverseTransformPoint(corner).y);
        var bottomOffset = footerTop - infoRect.rect.yMin + 6f;
        root.anchoredPosition = new Vector2(0f, Mathf.Max(0f, bottomOffset));
    }

    private static void ApplyGuestDraft(RoomSnapshot room)
    {
        if (info == null || screen == null || room.DraftRevision == lastAppliedRevision || string.IsNullOrEmpty(room.MapId)) return;
        if (room.MapPreview.Length == 0) return;
        var bytes = SnapshotCodec.Decompress(room.MapPreview, ProtocolConstants.MaxExpandedMapPreviewBytes);
        var preview = DynOb.Parse(Encoding.UTF8.GetString(bytes)) as DynOb;
        if (preview is null) throw new InvalidDataException("无法解析主机地图预览。");
        info.gameObject.SetActive(true);
        AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_ob")?.SetValue(info, preview);
        AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_ob_name")?.SetValue(info, room.MapId);
        AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_map")?.SetValue(info, null);
        if (info.txt_info_title != null) info.txt_info_title.text = room.MapTitle;
        AccessTools.Method(typeof(UI_MENU_LevelSelect_InfoSkm), "RenderForOb")?.Invoke(info, new object[] { preview });
        AccessTools.Method(typeof(UI_MENU_LevelSelect_InfoSkm), "ReadLocks")?.Invoke(info, new object[] { preview });
        AccessTools.Method(typeof(UI_MENU_LevelSelect_InfoSkm), "SetUpOptions")?.Invoke(info, Array.Empty<object>());
        info.dd_fow.SetValueWithoutNotify(room.FowType); info.dd_condition.SetValueWithoutNotify(room.WinCondition); info.dd_quickStart.SetValueWithoutNotify(room.QuickStart);
        ApplySeatSettings(room);
        previewError = "";
        lastAppliedRevision = room.DraftRevision;
        lastDraftSignature = DraftSignature(info, room.MapId);
    }

    internal static bool ApplyRemoteSeatDraft(RoomSnapshot draft)
    {
        if (!Active || info == null || draft.Seats.Select(value => value.LobbySlotIndex).Distinct().Count() != draft.Seats.Count) return false;
        var items = Items();
        if (draft.Seats.Any(value => value.LobbySlotIndex < 0 || value.LobbySlotIndex >= items.Count)) return false;
        ApplySeatSettings(draft);
        lastDraftSignature = DraftSignature(info, CurrentMapId());
        return true;
    }

    private static void ApplySeatSettings(RoomSnapshot draft)
    {
        var items = Items();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index]; var seat = draft.Seats.Find(value => value.LobbySlotIndex == index);
            if ((seat != null) != item.is_open) item.OnBtnSwitch();
            if (seat == null) continue;
            item.ForceSetControl(seat.Controller); item.ForceSetTeam(seat.Team); item.ForceSetColor(seat.Color); item.ForceSetPos(seat.PositionRandom ? -1 : seat.Position);
            SetNearest(item.dd_res, DataUtils.SkirmishResMulOptions, seat.ResourceMultiplier);
            SetNearest(item.dd_ai_intell, DataUtils.SkirmishAIIntelOptions, seat.AiIntelligence);
            ApplyCommander(item, seat);
        }
        EnsureMountedSeatActions();
    }

    private static void ApplyCommander(UI_SKM_PlayerSetting item, SeatInfo seat)
    {
        var selectedField = AccessTools.Field(typeof(UI_SKM_PlayerSetting), "cur_selected_co");
        var skillField = AccessTools.Field(typeof(UI_SKM_PlayerSetting), "cur_selected_co_sk");
        var passivesField = AccessTools.Field(typeof(UI_SKM_PlayerSetting), "cur_selected_co_ps");
        var namesField = AccessTools.Field(typeof(UI_SKM_PlayerSetting), "list_hero_name");
        var selection = seat.CommanderMode;
        if (seat.CommanderMode == 2)
        {
            var names = namesField?.GetValue(item) as List<string>;
            var index = names?.IndexOf(seat.CommanderId) ?? -1;
            selection = index >= 0 ? index + 2 : 1;
            skillField?.SetValue(item, string.IsNullOrEmpty(seat.SkillId) ? null : SDBase<SD_ANNW_SKILL>.Get(seat.SkillId));
            var passives = new List<SD_ANNW_PS>();
            foreach (var id in seat.PassiveIds)
            {
                var passive = SDBase<SD_ANNW_PS>.Get(id); if (passive is not null) passives.Add(passive);
            }
            passivesField?.SetValue(item, passives);
        }
        else
        {
            skillField?.SetValue(item, null); passivesField?.SetValue(item, null);
        }
        selectedField?.SetValue(item, selection);
        if (selection == 0)
        {
            item.icon_icon.gameObject.SetActive(false); item.txt_co.text = LAN.Get("UI_NoCO");
        }
        else if (selection == 1)
        {
            item.icon_icon.gameObject.SetActive(false); item.txt_co.text = LAN.Get("UI_RdCO");
        }
        else
        {
            item.icon_icon.gameObject.SetActive(true);
            item.icon_icon.sprite = PrebuildIconDic.self.GetSprite("co", seat.CommanderId + "_1");
            item.txt_co.text = AccessTools.Method(typeof(LAN), "GetCOName")?.Invoke(null, new object[] { seat.CommanderId }) as string ?? seat.CommanderId;
        }
    }

    private static void ApplyPermissions(XingyiStarryMpPlugin plugin, RoomSnapshot? room, string mapId)
    {
        if (info == null || screen == null) return;
        if (plugin.IsMatchStarting)
        {
            foreach (var selectable in info.GetComponentsInChildren<Selectable>(true)) Disable(selectable);
            if (screen.pool_maps != null) foreach (var button in screen.pool_maps.GetComponentsInChildren<Button>(true)) Disable(button);
            Store(screen.btn_confirm); screen.btn_confirm.interactable = false;
            return;
        }
        if (plugin.IsClient)
        {
            Disable(info.dd_fow); Disable(info.dd_condition); Disable(info.dd_quickStart);
            if (screen.pool_maps != null) foreach (var button in screen.pool_maps.GetComponentsInChildren<Button>(true)) Disable(button);
        }
        Store(screen.btn_confirm);
        var canJoinRunning = plugin.IsClient && room?.MatchStarted == true && plugin.LocalIdentityId is Guid localId &&
            room.Seats.Any(value => value.ClientId == localId && value.PendingActivation);
        var canStart = plugin.IsHost && (room?.SavedGame == true ? plugin.CanStartHostedRoom : !string.IsNullOrEmpty(mapId) && room != null && room.Seats.Any(value => value.OriginallyHuman) && room.Seats.Where(value => value.OriginallyHuman).All(value => value.Connected && value.Ready));
        screen.btn_confirm.interactable = canStart || canJoinRunning;
        SetConfirmLabel(canJoinRunning ? "加入游戏" : plugin.IsClient ? "等待主机开始" : "开始联机对局");
    }

    private static void RebuildSeats(XingyiStarryMpPlugin plugin, RoomSnapshot? room)
    {
        if (actionsRow == null) return;
        for (var index = actionsRow.childCount - 1; index >= 0; index--) UnityEngine.Object.Destroy(actionsRow.GetChild(index).gameObject);
        RebuildRowActions(plugin, room);
        var local = plugin.LocalIdentityId is Guid id ? room?.Seats.Find(value => value.ClientId == id) : null;
        var cancel = GameUiKit.Button(actionsRow, "CancelSeat", "取消选择", () => XingyiStarryMpPlugin.Instance?.ReleaseSeat());
        cancel.interactable = plugin.CanReleaseSeat;
        if (room?.MatchStarted != true)
        {
            var ready = GameUiKit.Button(actionsRow, "Ready", local?.Ready == true ? "取消准备" : "准备", () => XingyiStarryMpPlugin.Instance?.ToggleReady());
            ready.interactable = local != null;
        }
    }

    private static void RebuildRowActions(XingyiStarryMpPlugin plugin, RoomSnapshot? room)
    {
        var items = Items();
        var selectableSeats = room?.Seats.Where(value => !value.Defeated && (room.SavedGame || room.MatchStarted || value.OriginallyHuman)).OrderBy(value => value.LobbySlotIndex).ToList() ?? new List<SeatInfo>();
        if (selectableSeats.Any(value => value.LobbySlotIndex < 0 || value.LobbySlotIndex >= items.Count))
        {
            foreach (var item in rowAugmentations.Keys.ToArray()) RestoreRow(item);
            RebuildSyntheticRows(plugin, selectableSeats); return;
        }
        if (syntheticSeatRoot != null) { UnityEngine.Object.Destroy(syntheticSeatRoot); syntheticSeatRoot = null; }
        var alive = new HashSet<UI_SKM_PlayerSetting>(items.Where(value => value != null));
        foreach (var stale in rowAugmentations.Keys.Where(value => value == null || !alive.Contains(value)).ToArray()) RestoreRow(stale);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index]; if (item == null) continue;
            var seat = room?.Seats.Find(value => value.LobbySlotIndex == index);
            var selectable = seat is not null && !seat.Defeated && (room?.SavedGame == true || room?.MatchStarted == true || seat.OriginallyHuman);
            if (!selectable) { RestoreRow(item); continue; }
            if (!rowAugmentations.TryGetValue(item, out var augmentation)) augmentation = MountInNativeSlot(item);
            EnsureMounted(augmentation);
            var label = augmentation.Button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null) label.text = SeatLabel(seat!);
            augmentation.Button.interactable = plugin.CanClaimSeat && (!seat!.Connected || seat.ClientId == plugin.LocalIdentityId);
        }
    }

    private static void RebuildSyntheticRows(XingyiStarryMpPlugin plugin, IReadOnlyList<SeatInfo> seats)
    {
        if (info == null) return;
        if (syntheticSeatRoot != null) UnityEngine.Object.Destroy(syntheticSeatRoot);
        var root = GameUiKit.Rect("XingyiStarryMp_SavedSeatRows", info.transform);
        root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f); root.pivot = new Vector2(0.5f, 0.5f);
        root.sizeDelta = new Vector2(720f, Mathf.Min(520f, 24f + seats.Count * 54f)); root.anchoredPosition = new Vector2(0f, 25f);
        GameUiKit.Panel(root, new Color(0.12f, 0.055f, 0.012f, 0.96f));
        var vertical = root.gameObject.AddComponent<VerticalLayoutGroup>(); vertical.padding = new RectOffset(12, 12, 12, 12);
        vertical.spacing = 6f; vertical.childControlWidth = true; vertical.childControlHeight = true; vertical.childForceExpandWidth = true; vertical.childForceExpandHeight = false;
        foreach (var seat in seats)
        {
            var row = GameUiKit.Rect("Seat" + seat.LobbySlotIndex, root); row.gameObject.AddComponent<LayoutElement>().preferredHeight = 48f;
            var horizontal = row.gameObject.AddComponent<HorizontalLayoutGroup>(); horizontal.spacing = 8f; horizontal.childControlWidth = true; horizontal.childControlHeight = true; horizontal.childForceExpandWidth = false; horizontal.childForceExpandHeight = true;
            var labelHost = GameUiKit.Rect("Label", row); labelHost.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            GameUiKit.Text(labelHost, "Text", $"P{seat.PlayerIndex + 1} · 队伍 {seat.Team} · {seat.DisplayName}", 18f, TextAlignmentOptions.MidlineLeft);
            var slot = seat.LobbySlotIndex; var button = GameUiKit.Button(row, "Select", SeatLabel(seat), () => XingyiStarryMpPlugin.Instance?.ClaimSeat(slot));
            var element = button.gameObject.AddComponent<LayoutElement>(); element.preferredWidth = 132f; element.minWidth = 104f;
            button.interactable = plugin.CanClaimSeat && (!seat.Connected || seat.ClientId == plugin.LocalIdentityId);
        }
        syntheticSeatRoot = root.gameObject;
    }

    private static SeatSlotAugmentation MountInNativeSlot(UI_SKM_PlayerSetting item)
    {
        // The native row has a fixed AI-intelligence column between resource multiplier and team.
        // Human rows merely hide that column; re-use it instead of inserting another layout child.
        // In particular, never re-parent UI_SKM_PlayerSetting: it is owned by SimplePool and its
        // parent VerticalLayoutGroup uses the prefab's own fixed height.
        var slot = item.dd_ai_intell;
        var wasActive = slot.gameObject.activeSelf;
        var wasEnabled = slot.enabled;
        var wasInteractable = slot.interactable;
        slot.gameObject.SetActive(true);
        slot.enabled = false;
        slot.interactable = false;

        var button = GameUiKit.Button(slot.transform, "XingyiStarryMp_SelectSeat", "选择",
            () => XingyiStarryMpPlugin.Instance?.ClaimSeat(IndexOfItem(item)));
        var buttonRect = button.transform as RectTransform;
        if (buttonRect != null) GameUiKit.Stretch(buttonRect);
        var buttonElement = button.GetComponent<LayoutElement>() ?? button.gameObject.AddComponent<LayoutElement>();
        buttonElement.ignoreLayout = true;
        var result = new SeatSlotAugmentation(item, slot, button, wasActive, wasEnabled, wasInteractable);
        rowAugmentations[item] = result;
        return result;
    }

    private static void EnsureMountedSeatActions()
    {
        foreach (var augmentation in rowAugmentations.Values) EnsureMounted(augmentation);
    }

    private static void EnsureMounted(SeatSlotAugmentation augmentation)
    {
        if (augmentation.Slot == null || augmentation.Button == null) return;
        // ForceSetControl is invoked while applying a remote draft and restores the native
        // Human/AI visibility of this slot. Reassert our overlay after every UI refresh.
        augmentation.Slot.gameObject.SetActive(true);
        augmentation.Slot.enabled = false;
        augmentation.Slot.interactable = false;
        augmentation.Button.gameObject.SetActive(true);
        augmentation.Button.transform.SetAsLastSibling();
    }

    private static int IndexOfItem(UI_SKM_PlayerSetting item) => Items().IndexOf(item);

    private static void RestoreRow(UI_SKM_PlayerSetting? item)
    {
        if (item == null || !rowAugmentations.TryGetValue(item, out var value)) return;
        rowAugmentations.Remove(item);
        if (value.Button != null) UnityEngine.Object.Destroy(value.Button.gameObject);
        if (value.Slot != null)
        {
            value.Slot.enabled = value.WasEnabled;
            value.Slot.interactable = value.WasInteractable;
            value.Slot.gameObject.SetActive(value.WasActive);
        }
    }

    private static void RefreshStatus(XingyiStarryMpPlugin plugin, RoomSnapshot? room)
    {
        if (statusText == null) return;
        if (previewError.Length != 0) { statusText.text = previewError; return; }
        var local = plugin.LocalIdentityId is Guid id ? room?.Seats.Find(value => value.ClientId == id) : null;
        if (room == null || string.IsNullOrEmpty(room.MapId)) statusText.text = plugin.IsHost ? "请选择地图并设置 Human / AI" : "等待主机选择地图";
        else if (local == null) statusText.text = room.MatchStarted ? "选择右侧可接管席位，然后点击加入游戏" : "选择席位行右侧按钮；已占用席位会置灰";
        else if (room.MatchStarted) statusText.text = $"已选择 {local.DisplayName} · P{local.LobbySlotIndex + 1}，点击加入游戏后开始同步";
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
                value = value * 31 + (seat.Defeated ? 1 : 0); value = value * 31 + (seat.PendingActivation ? 1 : 0);
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
                value = value * 31 + (int)(AccessTools.Field(typeof(UI_SKM_PlayerSetting), "cur_selected_co")?.GetValue(item) ?? 1);
                value = value * 31 + ((AccessTools.Field(typeof(UI_SKM_PlayerSetting), "cur_selected_co_sk")?.GetValue(item) as SD_ANNW_SKILL)?.name?.GetHashCode() ?? 0);
                if (AccessTools.Field(typeof(UI_SKM_PlayerSetting), "cur_selected_co_ps")?.GetValue(item) is System.Collections.IEnumerable passives)
                    foreach (var passive in passives) value = value * 31 + ((passive as SD_ANNW_PS)?.name?.GetHashCode() ?? 0);
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
        foreach (var item in rowAugmentations.Keys.ToArray()) RestoreRow(item);
        if (syntheticSeatRoot != null) UnityEngine.Object.Destroy(syntheticSeatRoot);
        if (roomRoot != null) UnityEngine.Object.Destroy(roomRoot);
        roomRoot = null; roomRect = null; actionsRow = null; statusText = null; syntheticSeatRoot = null; screen = null; info = null;
        lastAppliedRevision = -1; lastSeatSignature = 0; lastDraftSignature = 0;
        cachedMapPreview = null; previewError = "";
    }

    private static string SeatLabel(SeatInfo seat) => seat.Defeated ? "已战败" : !seat.Connected ? "选择" : $"{seat.DisplayName}{(seat.Ready ? " ✓" : "")}";

    private sealed class SeatSlotAugmentation
    {
        public UI_SKM_PlayerSetting Item { get; }
        public TMP_Dropdown Slot { get; }
        public Button Button { get; }
        public bool WasActive { get; }
        public bool WasEnabled { get; }
        public bool WasInteractable { get; }
        public SeatSlotAugmentation(UI_SKM_PlayerSetting item, TMP_Dropdown slot, Button button,
            bool wasActive, bool wasEnabled, bool wasInteractable)
        { Item = item; Slot = slot; Button = button; WasActive = wasActive; WasEnabled = wasEnabled; WasInteractable = wasInteractable; }
    }

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

[HarmonyPatch(typeof(UI_SKM_PlayerSettingGroup), nameof(UI_SKM_PlayerSettingGroup.SetupForMap))]
internal static class NativeSkirmishSeatPoolResetPatch
{
    private static void Prefix() => NativeSkirmishLobby.RestoreRowsForPoolReset();
}

[HarmonyPatch(typeof(UI_MENU_LevelSelect_InfoSkm), nameof(UI_MENU_LevelSelect_InfoSkm.StartLevel))]
internal static class NativeSkirmishStartPatch
{
    private static bool Prefix(UI_MENU_LevelSelect_InfoSkm __instance) => XingyiStarryMpPlugin.Instance?.AllowNativeSkirmishStart(__instance, NativeSkirmishLobby.CurrentMapId()) ?? true;
}
