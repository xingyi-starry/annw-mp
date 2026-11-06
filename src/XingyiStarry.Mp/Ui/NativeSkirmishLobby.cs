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
    private static string? hostedMapData;
    private static string? hostedMapPath;
    private static string? hostedMapDisplayName;
    private static ulong hostedWorkshopId;
    private static bool restoringRoom;

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
        if (!Active || restoringRoom) return;
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
                previewError = "${ui_invalid_host_map_preview}" + ex.Message;
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
        if (selected is null) throw new InvalidDataException("The game could not read the map preview.");
        var preview = new DynOb();
        preview.SetKey("terrain", selected.GetKey_Obj("terrain"));
        preview.SetKey("commander", selected.GetKey_Obj("commander"));
        if (selected.HasKey("units")) preview.SetKey("units", selected.GetKey_Obj("units"));
        if (selected.HasKey("lock_fow_setting")) preview.SetKey("lock_fow_setting", selected.GetKey_Enum("lock_fow_setting", FOW_Type.None));
        if (selected.HasKey("lock_win_condition")) preview.SetKey("lock_win_condition", selected.GetKey_Enum("lock_win_condition", SkirmishWinCondition.None));
        if (selected.HasKey("lock_quick_start")) preview.SetKey("lock_quick_start", selected.GetKey_Enum("lock_quick_start", QuickStartSetting.None));
        if (selected.HasKey("expose_diff_flags")) preview.SetKey("expose_diff_flags", selected.GetKey_Int("expose_diff_flags"));
        var plain = Encoding.UTF8.GetBytes(preview.ToString());
        if (plain.Length > ProtocolConstants.MaxExpandedMapPreviewBytes) throw new InvalidDataException("Map preview data is too large.");
        var compressed = SnapshotCodec.Compress(plain);
        if (compressed.Length > ProtocolConstants.MaxMapPreviewBytes) throw new InvalidDataException("Compressed map preview data is too large.");
        cachedUserMap = userMap; cachedMapPreview = compressed;
        return compressed;
    }

    internal static void ShowMapError(string message) => previewError = message;

    internal static void MarkStartingMatch()
    {
        if (XingyiStarryMpPlugin.Instance?.IsHost == true && XingyiStarryMpPlugin.Instance.CurrentRoom?.SavedGame != true && info != null)
        {
            hostedMapData = (AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_ob").GetValue(info) as DynOb)?.ToString();
            hostedMapPath = AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_map").GetValue(info) as string;
            hostedMapDisplayName = AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_display_name").GetValue(info) as string;
            hostedWorkshopId = (ulong)(AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_workshop_map_id").GetValue(info) ?? 0UL);
        }
        CleanupUi();
    }

    internal static void ForgetHostedMap()
    { hostedMapData = null; hostedMapPath = null; hostedMapDisplayName = null; hostedWorkshopId = 0; }

    internal static void ReturnToRoom(SS_ANNW_Menu menu, RoomSnapshot room)
    {
        CleanupUi(); Active = true; restoringRoom = true;
        try
        {
            menu.screen_main.pop_skirmish.SetActive(false);
            menu.screen_skirmish.ShowAsNormal();
            Ensure(menu.screen_skirmish);
            if (info == null) throw new InvalidOperationException("The room configuration screen is unavailable.");
            info.gameObject.SetActive(true);
            if (room.SavedGame)
            {
                info.group.gameObject.SetActive(false);
                MpLocalizedText.Set(info.txt_info_title, room.MapTitle);
            }
            else if (XingyiStarryMpPlugin.Instance?.IsHost == true)
            {
                // Restore the original full map source, never the reduced network preview.
                if (hostedMapData is not null)
                {
                    var map = DynOb.Parse(hostedMapData) as DynOb ?? throw new InvalidDataException("The hosted map could not be restored.");
                    AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_ob").SetValue(info, map);
                    AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_ob_name").SetValue(info, room.MapId);
                    AccessTools.Method(typeof(UI_MENU_LevelSelect_InfoSkm), "RenderForOb").Invoke(info, new object[] { map });
                    AccessTools.Method(typeof(UI_MENU_LevelSelect_InfoSkm), "ReadLocks").Invoke(info, new object[] { map });
                    AccessTools.Method(typeof(UI_MENU_LevelSelect_InfoSkm), "SetUpOptions").Invoke(info, Array.Empty<object>());
                }
                else if (!string.IsNullOrEmpty(hostedMapPath)) info.Render(hostedMapPath, hostedMapDisplayName, hostedWorkshopId);
                else throw new InvalidOperationException("The hosted map source is unavailable.");
                MpLocalizedText.Set(info.txt_info_title, room.MapTitle);
                info.dd_fow.SetValueWithoutNotify(room.FowType); info.dd_condition.SetValueWithoutNotify(room.WinCondition);
                info.dd_quickStart.SetValueWithoutNotify(room.QuickStart); ApplyDifficulty(info, room.Difficulty);
                ApplySeatSettings(room);
                lastDraftSignature = DraftSignature(info, CurrentMapId());
            }
            else ApplyGuestDraft(room);
            nextRefresh = 0f;
        }
        finally { restoringRoom = false; }
    }

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
        statusText = GameUiKit.Text(statusHost, "Text", "${ui_select_a_map}", 16f, TextAlignmentOptions.Center);
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
        if (preview is null) throw new InvalidDataException("Unable to parse the host map preview.");
        info.gameObject.SetActive(true);
        AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_ob")?.SetValue(info, preview);
        AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_ob_name")?.SetValue(info, room.MapId);
        AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "selected_map")?.SetValue(info, null);
        if (info.txt_info_title != null) MpLocalizedText.Set(info.txt_info_title, room.MapTitle);
        AccessTools.Method(typeof(UI_MENU_LevelSelect_InfoSkm), "RenderForOb")?.Invoke(info, new object[] { preview });
        AccessTools.Method(typeof(UI_MENU_LevelSelect_InfoSkm), "ReadLocks")?.Invoke(info, new object[] { preview });
        AccessTools.Method(typeof(UI_MENU_LevelSelect_InfoSkm), "SetUpOptions")?.Invoke(info, Array.Empty<object>());
        info.dd_fow.SetValueWithoutNotify(room.FowType); info.dd_condition.SetValueWithoutNotify(room.WinCondition); info.dd_quickStart.SetValueWithoutNotify(room.QuickStart);
        ApplyDifficulty(info, room.Difficulty);
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
        // Native GenerateData uses these flags independently of the selected commander index.
        AccessTools.Field(typeof(UI_SKM_PlayerSetting), "cur_selected_co_is_map_preset").SetValue(item, seat.KeepMapCommander);
        AccessTools.Field(typeof(UI_SKM_PlayerSetting), "cur_map_co_name").SetValue(item, seat.KeepMapCommander ? seat.CommanderId : null);
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
        if (selection == 0 && !seat.KeepMapCommander)
        {
            item.icon_icon.gameObject.SetActive(false); item.txt_co.text = LAN.Get("UI_NoCO");
        }
        else if (selection == 1 && !seat.KeepMapCommander)
        {
            item.icon_icon.gameObject.SetActive(false); item.txt_co.text = LAN.Get("UI_RdCO");
        }
        else
        {
            item.icon_icon.gameObject.SetActive(true);
            item.icon_icon.sprite = PrebuildIconDic.self.GetSprite("co", seat.CommanderId + "_1");
            item.txt_co.text = (AccessTools.Method(typeof(LAN), "GetCOName")?.Invoke(null, new object[] { seat.CommanderId }) as string ?? seat.CommanderId)
                + (seat.KeepMapCommander ? "*" : "");
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
        if (plugin.IsClient || room?.SavedGame == true)
        {
            Disable(info.dd_fow); Disable(info.dd_condition); Disable(info.dd_quickStart);
            if (info.btn_diffSel != null)
                foreach (var selectable in info.btn_diffSel.GetComponentsInChildren<Selectable>(true)) Disable(selectable);
            if (screen.pool_maps != null) foreach (var button in screen.pool_maps.GetComponentsInChildren<Button>(true)) Disable(button);
        }
        Store(screen.btn_confirm);
        var localParticipant = plugin.LocalIdentityId is Guid participantId ? room?.Participants.Find(value => value.ClientId == participantId) : null;
        var canJoinRunning = plugin.IsClient && room?.MatchStarted == true && localParticipant?.Admission == ParticipantAdmission.JoinSelection;
        var selectedRunningSeat = canJoinRunning && plugin.LocalIdentityId is Guid localId &&
            room!.Seats.Any(value => value.ClientId == localId && value.PendingActivation);
        var canStart = plugin.IsHost && plugin.CanStartHostedRoom;
        screen.btn_confirm.interactable = canStart || canJoinRunning;
        SetConfirmLabel(canJoinRunning ? (selectedRunningSeat ? "${ui_take_over_and_join}" : "${ui_join_as_spectator}") : plugin.IsClient ? "${ui_waiting_for_host}" : "${ui_start_multiplayer_match}");
    }

    private static void RebuildSeats(XingyiStarryMpPlugin plugin, RoomSnapshot? room)
    {
        if (actionsRow == null) return;
        for (var index = actionsRow.childCount - 1; index >= 0; index--) UnityEngine.Object.Destroy(actionsRow.GetChild(index).gameObject);
        RebuildRowActions(plugin, room);
        var localParticipant = plugin.LocalIdentityId is Guid id ? room?.Participants.Find(value => value.ClientId == id) : null;
        var cancel = GameUiKit.Button(actionsRow, "CancelSeat", "${ui_release_seat}", () => XingyiStarryMpPlugin.Instance?.ReleaseSeat());
        cancel.interactable = plugin.CanReleaseSeat;
        if (room?.MatchStarted != true)
        {
            var ready = GameUiKit.Button(actionsRow, "Ready", localParticipant?.Ready == true ? "${ui_cancel_ready}" : "${ui_ready_up}", () => XingyiStarryMpPlugin.Instance?.ToggleReady());
            ready.interactable = plugin.CanSetReady;
        }
    }

    private static void RebuildRowActions(XingyiStarryMpPlugin plugin, RoomSnapshot? room)
    {
        var items = Items();
        var selectableSeats = room?.Seats.Where(value => !value.Defeated && (room.SavedGame || room.MatchStarted || value.OriginallyHuman)).OrderBy(value => value.LobbySlotIndex).ToList() ?? new List<SeatInfo>();
        if (room?.SavedGame == true || selectableSeats.Any(value => value.LobbySlotIndex < 0 || value.LobbySlotIndex >= items.Count))
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
            if (label != null) MpLocalizedText.Set(label, SeatLabel(seat!, room));
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
            GameUiKit.Text(labelHost, "Text", $"P{seat.PlayerIndex + 1}${{ui_team_3}}{seat.Team} · {seat.DisplayName}", 18f, TextAlignmentOptions.MidlineLeft);
            var slot = seat.LobbySlotIndex; var button = GameUiKit.Button(row, "Select", SeatLabel(seat, plugin.CurrentRoom), () => XingyiStarryMpPlugin.Instance?.ClaimSeat(slot));
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

        var button = GameUiKit.Button(slot.transform, "XingyiStarryMp_SelectSeat", "${ui_select}",
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
        if (previewError.Length != 0) { MpLocalizedText.Set(statusText, previewError); return; }
        var localId = plugin.LocalIdentityId;
        var local = localId is Guid seatOwner ? room?.Seats.Find(value => value.ClientId == seatOwner) : null;
        var participant = localId is Guid participantId ? room?.Participants.Find(value => value.ClientId == participantId) : null;
        if (room == null || string.IsNullOrEmpty(room.MapId)) MpLocalizedText.Set(statusText, plugin.IsHost ? "${ui_select_a_map_and_configure_human_ai_seats}" : "${ui_waiting_for_the_host_to_select_a_map}");
        else if (local == null) MpLocalizedText.Set(statusText, room.MatchStarted ? "${ui_select_a_seat_on_the_right_to_take}" : $"${{ui_no_seat_selected_spectator_when_the_match_starts}}{(participant?.Ready == true ? "${ui_ready}" : "${ui_not_ready}")}");
        else if (room.MatchStarted) MpLocalizedText.Set(statusText, $"${{ui_selected}}{local.DisplayName} · P{local.LobbySlotIndex + 1}${{ui_join_to_synchronize}}");
        else MpLocalizedText.Set(statusText, $"{local.DisplayName} · P{local.LobbySlotIndex + 1} · {(participant?.Ready == true ? "${ui_ready}" : "${ui_not_ready}")}");
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
            if (room != null) foreach (var participant in room.Participants)
            {
                value = value * 31 + participant.ClientId.GetHashCode(); value = value * 31 + (participant.Ready ? 1 : 0);
                value = value * 31 + (int)participant.Admission; value = value * 31 + (participant.SeatId?.GetHashCode() ?? 0);
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
            var value = mapId.GetHashCode(); value = value * 31 + target.dd_fow.value; value = value * 31 + target.dd_condition.value; value = value * 31 + target.dd_quickStart.value; value = value * 31 + GetSelectedDifficulty(target);
            foreach (var item in Items())
            {
                value = value * 31 + (item.is_open ? 1 : 0);
                foreach (var dropdown in item.GetComponentsInChildren<TMP_Dropdown>(true)) value = value * 31 + dropdown.value;
                value = value * 31 + (int)(AccessTools.Field(typeof(UI_SKM_PlayerSetting), "cur_selected_co")?.GetValue(item) ?? 1);
                value = value * 31 + ((bool)(AccessTools.Field(typeof(UI_SKM_PlayerSetting), "cur_selected_co_is_map_preset")?.GetValue(item) ?? false) ? 1 : 0);
                value = value * 31 + ((AccessTools.Field(typeof(UI_SKM_PlayerSetting), "cur_selected_co_sk")?.GetValue(item) as SD_ANNW_SKILL)?.name?.GetHashCode() ?? 0);
                if (AccessTools.Field(typeof(UI_SKM_PlayerSetting), "cur_selected_co_ps")?.GetValue(item) is System.Collections.IEnumerable passives)
                    foreach (var passive in passives) value = value * 31 + ((passive as SD_ANNW_PS)?.name?.GetHashCode() ?? 0);
            }
            return value;
        }
    }

    internal static int GetSelectedDifficulty(UI_MENU_LevelSelect_InfoSkm target) =>
        (int)(AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "cur_selected_diff")?.GetValue(target) ?? 30);

    private static void ApplyDifficulty(UI_MENU_LevelSelect_InfoSkm target, int difficulty)
    {
        AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "cur_selected_diff")?.SetValue(target, difficulty);
        AccessTools.Method(typeof(UI_MENU_LevelSelect_InfoSkm), "RefreshDiffSelBtn")?.Invoke(target, Array.Empty<object>());
    }

    private static void Disable(Selectable selectable) { Store(selectable); selectable.interactable = false; }
    private static void Store(Selectable selectable) { if (selectable != null && !originalInteractable.ContainsKey(selectable)) originalInteractable.Add(selectable, selectable.interactable); }

    private static void CleanupUi()
    {
        Active = false;
        if (info?.txt_info_title != null) MpLocalizedText.Clear(info.txt_info_title);
        foreach (var pair in originalInteractable) if (pair.Key != null) pair.Key.interactable = pair.Value;
        originalInteractable.Clear();
        if (screen?.btn_confirm != null)
        {
            var label = screen.btn_confirm.GetComponentInChildren<TMP_Text>(true);
            if (label != null) MpLocalizedText.Clear(label);
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

    private static string SeatLabel(SeatInfo seat, RoomSnapshot? room) => seat.Defeated ? "${ui_defeated}" : !seat.Connected ? "${ui_select}" :
        $"{seat.DisplayName}{(room?.Participants.Find(value => value.ClientId == seat.ClientId)?.Ready == true ? " ✓" : "")}";

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
        var text = screen.btn_confirm.GetComponentInChildren<TextMeshProUGUI>(true);
        if (text == null) return;
        MpLocalizedText.Set(text, value);
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.enableAutoSizing = true;
        text.fontSizeMin = 10f;
        text.fontSizeMax = 30f;
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

[HarmonyPatch(typeof(UI_MENU_LevelSelect_InfoSkm), nameof(UI_MENU_LevelSelect_InfoSkm.Render), new[] { typeof(string), typeof(string), typeof(ulong) })]
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
