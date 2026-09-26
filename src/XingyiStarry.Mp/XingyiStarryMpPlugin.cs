using System;
using System.IO;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using ANNW;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using XingyiStarry.Mp.Game;
using XingyiStarry.Mp.Infrastructure;
using XingyiStarry.Mp.Protocol;
using XingyiStarry.Mp.Session;
using XingyiStarry.Mp.Ui;

namespace XingyiStarry.Mp;

[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInProcess("AnnW.exe")]
public sealed class XingyiStarryMpPlugin : BaseUnityPlugin
{
    public const string PluginId = "xingyistarry.mp";
    public const string PluginName = "XingyiStarry MP";
    public const string PluginVersion = "0.8.0";

    private Harmony? harmony;
    private HostSession? host;
    private ClientSession? client;
    private ConfigEntry<int>? defaultPort;
    private ConfigEntry<string>? defaultAddress;
    private ConfigEntry<string>? displayName;
    private ConfigEntry<string>? relayEndpoint;
    private string gameFingerprint = "";
    private string contentFingerprint = "";
    private ulong nextRequestId;
    private readonly OperationQueue operations = new OperationQueue();
    private readonly Queue<AuthorityFrame> pendingAuthorityFrames = new Queue<AuthorityFrame>();
    private readonly List<long> replayFrameIds = new List<long>();
    private readonly GameCommandExecutor executor = new GameCommandExecutor();
    private Guid? replayOperationId;
    private bool replayExecutionCompleted;
    private bool replayExecutionStarted;
    private bool replayPreludeStarted;
    private bool replayPreludeCompleted;
    private OperationEndPayload? replayEnd;
    private readonly RandomTape hostRandomTape = new RandomTape();
    private readonly RandomTape replayRandomTape = new RandomTape();
    private OperationBeginPayload? replayBegin;
    private SnapshotManifest? loadingSnapshot;
    private GS_Battle? snapshotPreviousBattle;
    private int snapshotLoadGeneration;
    private bool hostStartAuthorized;
    private bool nativeHostBattleStarted;
    private bool hostBootstrapAttempted;
    private bool hostShutdownPending;
    private string pendingNativeNotice = "";
    private POP_General? fastReconnectPopup;
    private readonly Dictionary<TMP_Text, string> fastReconnectButtonLabels = new Dictionary<TMP_Text, string>();
    private readonly Dictionary<Localized_Txt, bool> fastReconnectButtonLocalizers = new Dictionary<Localized_Txt, bool>();
    private float nativeNoticeEarliest;
    private int perspectivePlayerIndex = -1;
    private bool previousLocalTurnOwned;
    private bool? pendingMatchEnd;
    private bool applyingAuthorityMatchEnd;
    private bool turnAdvanceRunning;
    private bool authorityAiOperationActive;
    private bool fastReconnectRunning;
    private bool fastReconnectCancelRequested;
    private int fastReconnectAttempt;
    private float fastReconnectStartedAt;
    private bool forcedDisconnectedEndTurnPending;
    private bool forcedDisconnectedEndTurnRunning;
    private string savedGamePath = "";
    private int connectionGeneration;
    private readonly Dictionary<string, IRemotePeer> requestPeers = new Dictionary<string, IRemotePeer>(StringComparer.Ordinal);
    private readonly Dictionary<ulong, (GameCommand Command, long Token)> pendingClientCommands = new Dictionary<ulong, (GameCommand, long)>();

    public static XingyiStarryMpPlugin? Instance { get; private set; }
    internal string Status { get; private set; } = "未连接";
    internal bool IsMatchStarting => hostStartAuthorized || client?.MatchStarting == true;
    internal bool CanClaimSeat => !IsMatchStarting && CurrentRoom?.Seats.Exists(s => !s.Defeated && (CurrentRoom.SavedGame || CurrentRoom.MatchStarted || s.OriginallyHuman) && (!s.Connected || s.ClientId == LocalIdentityId)) == true;
    internal bool CanSetReady => !IsMatchStarting && CurrentRoom?.MatchStarted != true && LocalIdentityId is Guid id && CurrentRoom?.Seats.Find(s => s.ClientId == id) is not null;
    internal bool CanReleaseSeat => !IsMatchStarting && LocalIdentityId is Guid id && CurrentRoom?.Seats.Find(s => s.ClientId == id) is not null && client?.JoinedMatch != true;
    internal bool CanStartHostedRoom => host?.Room.CanStart == true;
    internal bool IsHost => host is not null;
    internal bool IsClient => client is not null;
    internal bool CanManualResync => client?.ClientId.HasValue == true && client.SnapshotRequested == false &&
        client.IsCaughtUp && client.AppliedFrameId == client.VerifiedFrameId && pendingAuthorityFrames.Count == 0 &&
        loadingSnapshot is null && !executor.IsBusy && !replayOperationId.HasValue && !fastReconnectRunning &&
        InputGate.MultiplayerActive && GS_Battle.self?.game_running == true;
    internal bool CanMultiplayerSave => InputGate.MultiplayerActive && GS_Battle.self?.game_running == true && GS_Battle.self.turns >= 1 &&
        !GS_Battle.self.functions.Querry(GAME_FUNCTION.NoSave) && !fastReconnectRunning &&
        loadingSnapshot is null && !executor.IsBusy && !authorityAiOperationActive && !turnAdvanceRunning &&
        !forcedDisconnectedEndTurnPending && !forcedDisconnectedEndTurnRunning && !pendingMatchEnd.HasValue &&
        (host?.MatchId.HasValue == true || client?.IsCaughtUp == true && client.AppliedFrameId == client.VerifiedFrameId && pendingAuthorityFrames.Count == 0 && !replayOperationId.HasValue);
    internal bool ShouldCaptureHostAi => host?.MatchId.HasValue == true && GS_Battle.self?.game_running == true && GS_Battle.self.cur_player?.is_ai == true;
    internal bool ShouldSuppressClientAi => client?.IsCaughtUp == true && InputGate.MultiplayerActive;
    internal int ConfiguredPort { get => defaultPort?.Value ?? ProtocolConstants.DefaultPort; set { if (defaultPort is not null) defaultPort.Value = value; } }
    internal string ConfiguredAddress { get => defaultAddress?.Value ?? "127.0.0.1"; set { if (defaultAddress is not null) defaultAddress.Value = value; } }
    internal string ConfiguredDisplayName { get => displayName?.Value ?? Environment.UserName; set { if (displayName is not null) displayName.Value = value; } }
    internal string ConfiguredRelayEndpoint => relayEndpoint?.Value ?? "60.205.147.182:24555";
    internal RoomSnapshot? CurrentRoom => host?.Room.Snapshot() ?? client?.Room;
    internal Guid? LocalClientId => client?.ClientId;
    internal Guid? LocalIdentityId => host?.LocalHostClientId ?? client?.ClientId;
    public bool IsAuthorityHostBattleActive => host?.MatchId.HasValue == true && GS_Battle.self?.game_running == true;
    internal bool LocalOwnsCurrentTurn
    {
        get
        {
            var local = GetLocalDisplayPlayer();
            return local is not null && GS_Battle.self?.cur_player == local &&
                   CurrentRoom?.Seats.Find(value => value.PlayerIndex == local.index)?.AiControlled != true;
        }
    }

    public bool TrySubmitDebugResources(int playerIndex, int metalDelta, int powerDelta, out string reason)
    {
        if (!CanSubmitHostDebug(playerIndex, out reason)) return false;
        if (metalDelta < 0 || powerDelta < 0 || metalDelta == 0 && powerDelta == 0)
        { reason = "资源增加量必须是正整数。"; return false; }
        EnqueueHostDebug(new GameCommand { Kind = CommandKind.DebugAddResources, DebugPlayerIndex = playerIndex, DebugMetalDelta = metalDelta, DebugPowerDelta = powerDelta });
        reason = "调试资源指令已进入主机权威队列。"; return true;
    }

    public bool TrySubmitDebugFillSkill(int playerIndex, out string reason)
    {
        if (!CanSubmitHostDebug(playerIndex, out reason)) return false;
        EnqueueHostDebug(new GameCommand { Kind = CommandKind.DebugFillSkill, DebugPlayerIndex = playerIndex });
        reason = "充满技能指令已进入主机权威队列。"; return true;
    }

    private bool CanSubmitHostDebug(int playerIndex, out string reason)
    {
        if (!IsAuthorityHostBattleActive || host is null || GS_Battle.self?.all_player?.players is null)
        { reason = "只有联机战斗中的主机可以使用调试指令。"; return false; }
        if (playerIndex < -1 || playerIndex >= GS_Battle.self.all_player.players.Count)
        { reason = "玩家索引无效。"; return false; }
        reason = ""; return true;
    }

    private void EnqueueHostDebug(GameCommand command)
    {
        if (host is null || GS_Battle.self is null) return;
        operations.Enqueue(new CommandRequest
        {
            ClientId = host.LocalHostClientId,
            RequestId = ++nextRequestId,
            SeatId = host.LocalHostSeatId,
            Round = GS_Battle.self.turns,
            AppliedFrameId = 0,
            Command = command
        });
    }

    private void Awake()
    {
        Instance = this;
        defaultPort = Config.Bind("Network", "Port", ProtocolConstants.DefaultPort, "创建或加入房间使用的 TCP 端口。");
        defaultAddress = Config.Bind("Network", "Address", "127.0.0.1", "默认加入的局域网主机地址。");
        displayName = Config.Bind("Network", "DisplayName", Environment.UserName, "局域网房间内显示的名称。");
        relayEndpoint = Config.Bind("Relay", "Endpoint", "60.205.147.182:24555", "公共联机使用的中继服务器地址，格式为 host:port。");
        try
        {
            var assemblyCSharp = Path.Combine(Paths.ManagedPath, "Assembly-CSharp.dll");
            var firstPass = Path.Combine(Paths.ManagedPath, "Assembly-CSharp-firstpass.dll");
            gameFingerprint = Fingerprint.FileSha256(assemblyCSharp);
            contentFingerprint = Fingerprint.Combined(assemblyCSharp, firstPass);
            var missing = GameCompatibility.Probe();
            if (missing.Count != 0) throw new InvalidOperationException("游戏版本不兼容，缺少方法: " + string.Join(", ", missing));
            if (HasOldLanPlugin()) throw new InvalidOperationException("检测到 AnnW.LanMp；请勿同时加载两个联机插件。");
            harmony = new Harmony(PluginId); harmony.PatchAll(typeof(XingyiStarryMpPlugin).Assembly);
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Game={gameFingerprint}");
        }
        catch (Exception ex)
        {
            Status = "初始化失败：" + ex.Message; Logger.LogError(ex);
        }
    }

    private bool HasOldLanPlugin()
    {
        foreach (var plugin in BepInEx.Bootstrap.Chainloader.PluginInfos.Keys)
            if (plugin.IndexOf("annw.lanmp", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private void Update()
    {
        host?.Pump(LogSessionEvent, OnHostCommand, OnHostLobbyDraft, ShowParticipantNotice);
        PumpHostSeatChanges();
        client?.Pump(LogSessionEvent, OnClientCommandResponse, OnAuthorityFrame, OnSnapshotReceived, ShowParticipantNotice);
        if (host?.ConnectionLost == true)
        {
            var reason = string.IsNullOrWhiteSpace(host.ConnectionError) ? "与公共中继服务器的连接已中断。" : host.ConnectionError;
            host.Dispose(); host = null; InputGate.MultiplayerActive = false; InputGate.LocalSeatMayAct = false;
            Status = "公共联机会话已结束：" + reason; pendingNativeNotice = reason; nativeNoticeEarliest = Time.unscaledTime + 0.25f;
            if (GS_Battle.self?.game_running == true && SingletonMono<SS_ANNW_Game>.self is not null) SingletonMono<SS_ANNW_Game>.self.DoQuitOut();
            else NativeSkirmishLobby.TerminateFromRemote();
        }
        PumpAuthorityFrames();
        if (client?.ConnectionLost == true && !fastReconnectRunning)
        {
            if (client.CanFastReconnect && GS_Battle.self?.game_running == true)
                StartCoroutine(RunFastReconnect());
            else
            {
                var reason = client.ConnectionError;
                var resultNotice = client.TerminalSessionEnded && !string.IsNullOrWhiteSpace(reason)
                    ? reason
                    : "与主机失去连接。";
                ExitDisconnectedClient(reason, resultNotice);
            }
        }
        client?.Tick();
        NativeLobbyPanel.Tick(this);
        PublicLobbyPanel.Tick(this);
        NativeSkirmishLobby.Tick(this);
        if (client?.ClientId is not null && !fastReconnectRunning) Status = $"已握手，ClientId={client.ClientId:N}，已验证帧={client.VerifiedFrameId}";
        InputGate.LocalSeatMayAct = false;
        if (!fastReconnectRunning && loadingSnapshot is null && client?.IsCaughtUp == true && client.AppliedFrameId == client.VerifiedFrameId &&
            !executor.IsBusy && !replayOperationId.HasValue && client.ClientId is Guid localClient && GS_Battle.self?.game_running == true)
        {
            var seat = client.Room?.Seats.Find(value => value.ClientId == localClient);
            InputGate.LocalSeatMayAct = seat is not null && seat.PlayerIndex == GS_Battle.self.current_co_index && !seat.AiControlled;
        }
        if (host?.MatchId is not null && GS_Battle.self is not null)
        {
            host.RefreshRuntimeSeats(GS_Battle.self.all_player.players);
            var localSeat = host.Room.Seats.FirstOrDefault(value => value.ClientId == host.LocalHostClientId);
            InputGate.LocalSeatMayAct = localSeat is not null && localSeat.PlayerIndex == GS_Battle.self.current_co_index && !localSeat.AiControlled;
            if (!executor.IsBusy) ApplyRoomSeatModes(host.Room.Snapshot());
        }
        ApplyLocalPerspectiveAndControl();
        if (InputGate.MultiplayerActive && GS_Battle.self is not null) GS_Battle.self.editor_enabled = false;
        var pauseMenu = SingletonMono<SS_ANNW_Game>.self?.ui?.pop_pause_menu;
        if (pauseMenu is not null && pauseMenu.gameObject.activeSelf) MultiplayerPauseMenuPatch.Refresh(pauseMenu);
        PumpOperations();
        TryShowNativeNotice();
    }

    private IEnumerator RunFastReconnect()
    {
        if (client is null) yield break;
        fastReconnectRunning = true; fastReconnectCancelRequested = false; fastReconnectAttempt = 0; fastReconnectStartedAt = Time.realtimeSinceStartup;
        InputGate.LocalSeatMayAct = false;
        var acceptedGeneration = client.ReconnectAcceptedGeneration;
        var schedule = new[] { 0f, 3f, 7f };
        for (var index = 0; index < schedule.Length && client is not null && !fastReconnectCancelRequested; index++)
        {
            while (!fastReconnectCancelRequested && Time.realtimeSinceStartup - fastReconnectStartedAt < schedule[index]) yield return 0f;
            if (fastReconnectCancelRequested) break;
            fastReconnectAttempt = index + 1; Status = $"正在快速重连（{fastReconnectAttempt}/3）";
            ShowFastReconnectPopup();
            Task? attempt = null;
            try { attempt = client.ReconnectAttemptAsync(++nextRequestId); }
            catch (Exception ex) { Logger.LogWarning($"Reconnect attempt {fastReconnectAttempt}/3 could not start: {ex.Message}"); }
            var deadline = Time.realtimeSinceStartup + 3f;
            while (!fastReconnectCancelRequested && client is not null && Time.realtimeSinceStartup < deadline &&
                   (attempt is not null && !attempt.IsCompleted || client.ReconnectAcceptedGeneration == acceptedGeneration))
            {
                client.Pump(LogSessionEvent, OnClientCommandResponse, OnAuthorityFrame, OnSnapshotReceived, ShowParticipantNotice);
                if (client.ReconnectAcceptedGeneration != acceptedGeneration) break;
                if (attempt?.IsFaulted == true || client.TerminalSessionEnded) break;
                yield return 0f;
            }
            if (client is not null && client.ReconnectAcceptedGeneration != acceptedGeneration)
            {
                HideFastReconnectPopup(); fastReconnectRunning = false; Status = "快速重连成功，正在同步最新状态"; yield break;
            }
            if (client?.TerminalSessionEnded == true || IsTerminalReconnectError(attempt?.Exception?.GetBaseException()))
            {
                HideFastReconnectPopup(); fastReconnectRunning = false;
                var terminalReason = !string.IsNullOrWhiteSpace(client?.ConnectionError)
                    ? client.ConnectionError
                    : "联机会话已结束。";
                ExitDisconnectedClient(terminalReason, terminalReason);
                yield break;
            }
            if (attempt?.IsFaulted == true) Logger.LogWarning($"Reconnect attempt {fastReconnectAttempt}/3 failed: {attempt.Exception?.GetBaseException().Message}");
        }
        if (fastReconnectCancelRequested)
        {
            HideFastReconnectPopup(); fastReconnectRunning = false;
            ExitDisconnectedClient("已取消重连。");
            yield break;
        }
        var reason = client?.ConnectionError ?? "快速重连失败";
        HideFastReconnectPopup(); fastReconnectRunning = false;
        ExitDisconnectedClient(string.IsNullOrWhiteSpace(reason) ? "快速重连三次均失败。" : reason, "与主机失去连接。");
    }

    private void ExitDisconnectedClient(string reason, string resultNotice = "")
    {
        client?.Dispose(); client = null;
        pendingAuthorityFrames.Clear(); pendingClientCommands.Clear(); ResetReplayState(); executor.Reset(); GameplayRandom.ActiveTape = null;
        loadingSnapshot = null; snapshotPreviousBattle = null;
        InputGate.MultiplayerActive = false; InputGate.LocalSeatMayAct = false; Status = "联机会话已结束：" + reason;
        ShowBattleMessage(string.IsNullOrWhiteSpace(reason) ? "与联机主机的连接已中断。" : reason);
        if (!string.IsNullOrWhiteSpace(resultNotice))
        {
            pendingNativeNotice = resultNotice;
            nativeNoticeEarliest = Time.unscaledTime + 0.25f;
        }
        if (GS_Battle.self?.game_running == true && SingletonMono<SS_ANNW_Game>.self is not null) SingletonMono<SS_ANNW_Game>.self.DoQuitOut();
        else NativeSkirmishLobby.TerminateFromRemote();
    }

    private static bool IsTerminalReconnectError(Exception? error) => error is not null &&
        error.Message.IndexOf("room or match is not resumable", StringComparison.OrdinalIgnoreCase) >= 0;

    private void ShowFastReconnectPopup()
    {
        var popup = UI_Floater.self?.pop_general;
        if (popup is null) return;
        var text = $"正在重连中（{fastReconnectAttempt}/3）...";
        if (fastReconnectPopup != popup)
        {
            fastReconnectPopup = popup;
            popup.ShowAsGeneral(text, CancelFastReconnect);
            ConfigureFastReconnectCancelButton(popup);
        }
        else popup.txt_content.text = text;
    }

    private void ConfigureFastReconnectCancelButton(POP_General popup)
    {
        // ShowAsGeneral supplies a confirm action and a secondary close button. Re-purpose the
        // confirm action as the one explicit cancel button and hide the redundant secondary one.
        if (popup.btn_close is not null) popup.btn_close.SetActive(false);
        foreach (var button in popup.panel.GetComponentsInChildren<Button>(true))
        {
            if (!button.gameObject.activeInHierarchy || popup.btn_close is not null &&
                (button.gameObject == popup.btn_close || button.transform.IsChildOf(popup.btn_close.transform))) continue;
            foreach (var localized in button.GetComponentsInChildren<Localized_Txt>(true))
            {
                if (!fastReconnectButtonLocalizers.ContainsKey(localized)) fastReconnectButtonLocalizers.Add(localized, localized.enabled);
                localized.enabled = false;
            }
            foreach (var label in button.GetComponentsInChildren<TMP_Text>(true))
            {
                if (!fastReconnectButtonLabels.ContainsKey(label)) fastReconnectButtonLabels.Add(label, label.text);
                label.text = "取消";
            }
        }
    }

    private void CancelFastReconnect()
    {
        if (!fastReconnectRunning) return;
        fastReconnectCancelRequested = true;
        Status = "正在退出已断开的联机会话";
    }

    private void HideFastReconnectPopup()
    {
        if (fastReconnectPopup is not null) fastReconnectPopup.Hide();
        foreach (var pair in fastReconnectButtonLabels) if (pair.Key is not null) pair.Key.text = pair.Value;
        foreach (var pair in fastReconnectButtonLocalizers)
        {
            if (pair.Key is null) continue;
            pair.Key.enabled = pair.Value;
            if (pair.Value) pair.Key.RenderLocalizedContent();
        }
        fastReconnectButtonLabels.Clear(); fastReconnectButtonLocalizers.Clear();
        fastReconnectPopup = null;
    }

    internal bool SyncLobbyDraft(UI_MENU_LevelSelect_InfoSkm info, string mapId)
    {
        if (IsMatchStarting || (host is null && client is null) || CurrentRoom?.SavedGame == true || host?.MatchId.HasValue == true || info?.group is null || string.IsNullOrEmpty(mapId)) return false;
        var players = info.group.GenerateData();
        var draft = CreateLobbyDraft(mapId, info, players);
        if (host is not null)
        {
            try { draft.MapPreview = NativeSkirmishLobby.CaptureMapPreview(info, out var userMap); draft.UserMap = userMap; }
            catch (Exception ex) { Status = "地图预览同步失败：" + ex.Message; NativeSkirmishLobby.ShowMapError(Status); Logger.LogWarning(ex); return false; }
            host.UpdateLobbyDraft(draft, players);
            return true;
        }
        else if (client is not null)
            _ = client.SendLobbyDraftAsync(draft);
        return true;
    }

    private static RoomSnapshot CreateLobbyDraft(string mapId, UI_MENU_LevelSelect_InfoSkm info, IReadOnlyList<SGS_Player> players)
    {
        var draft = new RoomSnapshot { MapId = mapId, MapTitle = info.txt_info_title?.text ?? mapId, FowType = info.dd_fow.value, WinCondition = info.dd_condition.value, QuickStart = info.dd_quickStart.value, Difficulty = NativeSkirmishLobby.GetSelectedDifficulty(info) };
        for (var index = 0; index < players.Count; index++)
        {
            var player = players[index]; if (!player.exist) continue;
            var human = player.controller == PlayerControl.Human;
            var seat = new SeatInfo
            {
                LobbySlotIndex = index,
                OriginallyHuman = human,
                Controller = (int)player.controller,
                Team = (int)player.team,
                Color = (int)player.color,
                Position = player.pos_ind,
                PositionRandom = player.pos_random,
                ResourceMultiplier = player.res_percent,
                AiIntelligence = player.ai_interlligence
            };
            var items = AccessTools.Method(typeof(UI_SKM_PlayerSettingGroup), "GetItems")?.Invoke(info.group, Array.Empty<object>()) as List<UI_SKM_PlayerSetting>;
            var item = items is not null && index < items.Count ? items[index] : null;
            var selection = item is null ? 1 : (int)(AccessTools.Field(typeof(UI_SKM_PlayerSetting), "cur_selected_co")?.GetValue(item) ?? 1);
            seat.CommanderMode = selection <= 1 ? selection : 2;
            if (seat.CommanderMode == 2)
            {
                seat.CommanderId = player.sd_co ?? "";
                seat.SkillId = player.skill?.name ?? "";
                if (player.ps_list is not null) foreach (var passive in player.ps_list) if (passive is not null) seat.PassiveIds.Add(passive.name);
            }
            draft.Seats.Add(seat);
        }
        return draft;
    }

    private void OnHostLobbyDraft(RoomSnapshot draft, IRemotePeer peer)
    {
        if (host is null || host.MatchId.HasValue || !NativeSkirmishLobby.ApplyRemoteSeatDraft(draft))
        {
            _ = peer.SendAsync(new Envelope { Type = MessageType.Reject, Payload = ProtocolCodec.EncodeString("当前无法修改遭遇战席位设置。") });
            return;
        }
        var currentInfo = NativeSkirmishLobby.CurrentInfo;
        var mapId = NativeSkirmishLobby.CurrentMapId();
        if (currentInfo is not null && !string.IsNullOrEmpty(mapId)) SyncLobbyDraft(currentInfo, mapId);
    }

    private void ShowParticipantNotice(string notice)
    {
        Logger.LogInfo(notice);
        if (!ShowBattleMessage(notice)) Status = notice;
    }

    private void PumpHostSeatChanges()
    {
        if (host is null) return;
        if (host.MatchId.HasValue && (executor.IsBusy || authorityAiOperationActive || turnAdvanceRunning)) return;
        while (host.TryDequeueSeatChange(out var change) && change is not null)
        {
            if (host.MatchId.HasValue)
                host.AppendAndBroadcast(AuthorityFrameType.SeatChanged, ProtocolCodec.EncodeSeatControlChanged(change));
            ApplyRoomSeatModes(host.Room.Snapshot()); host.BroadcastRoom();
            if (GS_Battle.self?.game_running == true && GS_Battle.self.current_co_index == change.PlayerIndex)
                forcedDisconnectedEndTurnPending = true;
        }
        if (forcedDisconnectedEndTurnPending && !forcedDisconnectedEndTurnRunning && !executor.IsBusy &&
            !authorityAiOperationActive && !turnAdvanceRunning && GS_Battle.self?.game_running == true)
            StartCoroutine(RunForcedDisconnectedEndTurn());
    }

    private IEnumerator RunForcedDisconnectedEndTurn()
    {
        forcedDisconnectedEndTurnRunning = true; forcedDisconnectedEndTurnPending = false;
        try { yield return RunHostAiEndTurn(); }
        finally { forcedDisconnectedEndTurnRunning = false; }
    }

    private static bool ShowBattleMessage(string notice)
    {
        var messages = SingletonMono<SS_ANNW_Game>.self?.ui?.messages;
        if (GS_Battle.self?.game_running != true || messages is null) return false;
        messages.AddMessage("", notice); return true;
    }

    internal bool AllowNativeSkirmishStart(UI_MENU_LevelSelect_InfoSkm info, string mapId)
    {
        if (!NativeSkirmishLobby.Active) return true;
        if (client is not null)
        {
            if (CurrentRoom?.MatchStarted == true) JoinSelectedMatch();
            else Status = "只有主机可以开始对局";
            return false;
        }
        if (host is null) { Status = "请先创建房间"; return false; }
        if (host.Room.SavedGame)
        {
            if (!host.Room.CanStart) { Status = "主机需选择席位，且所有已选席玩家必须准备"; return false; }
            StartSavedGame(); return false;
        }
        if (!SyncLobbyDraft(info, mapId) || host.Room.MapId != mapId || host.Room.MapPreview.Length == 0)
        { Status = "主机尚未同步当前地图预览"; return false; }
        if (!host.Room.CanStart) { Status = "尚有真人席位未认领或未准备"; return false; }
        host.BroadcastMatchStarting(); hostStartAuthorized = true;
        NativeSkirmishLobby.MarkStartingMatch();
        return true;
    }

    private void StartSavedGame()
    {
        if (host is null || string.IsNullOrWhiteSpace(savedGamePath)) { Status = "未找到待加载的联机存档"; return; }
        host.BroadcastMatchStarting(); hostStartAuthorized = true; nativeHostBattleStarted = true; NativeSkirmishLobby.MarkStartingMatch();
        Status = "正在加载联机存档";
        Singleton<BattleAndMapFileSystem>.self.DoLoadSkirmish(savedGamePath);
    }

    internal void OnNativeStartGameFormal()
    {
        if (host is null || !hostStartAuthorized) return;
        nativeHostBattleStarted = true;
        Status = "联机战局已进入原版正式启动阶段";
    }

    internal void OnNativeStartPlayerTurnCompleted(int playerIndex)
    {
        if (!nativeHostBattleStarted || hostBootstrapAttempted || host is null || host.MatchId.HasValue) return;
        if (GS_Battle.self is null || !GS_Battle.self.game_running || GS_Battle.self.current_co_index != playerIndex) return;
        Logger.LogInfo($"Initial player turn initialization completed at player {playerIndex}; creating authority anchor.");
        BootstrapHostBattle();
    }

    internal void OnNativeLoadedGameResumed()
    {
        if (client is not null && loadingSnapshot is not null && GS_Battle.self is not null &&
            !ReferenceEquals(GS_Battle.self, snapshotPreviousBattle) && GS_Battle.self.game_running)
        {
            var loaded = loadingSnapshot;
            Logger.LogInfo($"Snapshot load generation {snapshotLoadGeneration} resumed on a new battle instance at frame {loaded.FrameId}.");
            loadingSnapshot = null;
            snapshotPreviousBattle = null;
            GS_Battle.self.editor_enabled = false;
            Status = $"快照帧 {loaded.FrameId} 已恢复，正在追赶日志";
            if (!client.IsCaughtUp) _ = client.RequestHistoryAsync();
        }
        if (!nativeHostBattleStarted || !hostStartAuthorized || hostBootstrapAttempted || host is null || host.MatchId.HasValue || !host.Room.SavedGame) return;
        BootstrapHostBattle();
        var currentSeat = host.Room.Seats.FirstOrDefault(value => value.PlayerIndex == GS_Battle.self?.current_co_index);
        if (currentSeat?.AiControlled == true) forcedDisconnectedEndTurnPending = true;
    }

    internal void OnNativeBattleLeft()
    {
        if (host is null && client is null) return;
        Logger.LogInfo("Native battle left; closing multiplayer session.");
        if (host is not null && !hostShutdownPending)
        {
            hostShutdownPending = true;
            StartCoroutine(NotifyGuestsAndCloseHost());
            return;
        }
        Disconnect();
    }

    private IEnumerator NotifyGuestsAndCloseHost()
    {
        var closingHost = host;
        var task = closingHost?.BroadcastSessionEndedAsync("主机已退出联机战斗。");
        var deadline = Time.realtimeSinceStartup + 1f;
        while (task is not null && !task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
        Disconnect();
    }

    private void BootstrapHostBattle()
    {
        hostBootstrapAttempted = true;
        try
        {
            if (host is null) return;
            var players = SS_ANNW_Game.start_game_setting?.players ?? throw new InvalidOperationException("联机遭遇战缺少原版 StartGameSetting.players。");
            host.BindRuntimePlayerIndices(players);
            var journalDirectory = Path.Combine(Paths.ConfigPath, "XingyiStarryMp", "journals");
            host.StartMatch(Path.Combine(journalDirectory, Guid.NewGuid().ToString("N") + ".xmpj"));
            ApplyRoomSeatModes(host.Room.Snapshot());
            GS_Battle.self.editor_enabled = false;
            var initialFrame = host.AppendAndBroadcast(AuthorityFrameType.TurnPhase, ProtocolCodec.EncodeInt64((long)TurnPhase.SeatTurnReady));
            host.SetLatestSnapshot(GameStateSerializer.CaptureSnapshot(), initialFrame);
            host.BroadcastRoom();
            InputGate.MultiplayerActive = true;
            InputGate.LocalSeatMayAct = false;
            Status = "主机权威对局已启动；初始快照已发布";
        }
        catch (Exception ex)
        {
            Status = "联机战局初始化失败：" + ex.Message;
            Logger.LogError(ex);
        }
    }

    internal void Host(int port)
    {
        Disconnect();
        try
        {
            var name = NormalizeDisplayName();
            host = new HostSession(port, PluginVersion, gameFingerprint, contentFingerprint, name); host.Start();
            Status = $"正在主持 0.0.0.0:{port}";
        }
        catch (Exception ex) { Disconnect(); Status = "创建失败：" + ex.Message; Logger.LogError(ex); }
    }

    internal void HostFromSave(int port, string path)
    {
        Disconnect();
        try
        {
            var save = Singleton<BattleAndMapFileSystem>.self.ReadFileWithMeta_Local(path) ?? throw new InvalidDataException("无法读取遭遇战存档。");
            if (!save.HasKey("startGameSetting")) throw new InvalidDataException("该文件不是可联机的遭遇战存档。");
            var settings = StartGameSetting.LoadOb(save.GetKey_Obj("startGameSetting"));
            var name = NormalizeDisplayName(); host = new HostSession(port, PluginVersion, gameFingerprint, contentFingerprint, name);
            host.ConfigureSavedGame(save, settings); host.Start(); savedGamePath = path; Status = "已从存档创建局域网房间";
        }
        catch (Exception ex) { Disconnect(); Status = "从存档创建失败：" + ex.Message; Logger.LogError(ex); }
    }

    internal async void HostPublic(string roomName, string password)
    {
        Disconnect();
        var generation = connectionGeneration;
        try
        {
            var name = NormalizeDisplayName(); var roomId = Guid.NewGuid();
            var registration = new RelayRegisterRoomRequest
            {
                RequestId = ++nextRequestId,
                RoomId = roomId,
                RoomName = string.IsNullOrWhiteSpace(roomName) ? name + " 的房间" : roomName.Trim(),
                HostName = name,
                Password = password ?? "",
                PluginVersion = PluginVersion,
                GameFingerprint = gameFingerprint,
                ContentFingerprint = contentFingerprint
            };
            Status = "正在连接公共中继服务器";
            var transport = await RelayHostTransport.ConnectAsync(ConfiguredRelayEndpoint, registration);
            if (generation != connectionGeneration) { transport.Dispose(); return; }
            host = new HostSession(transport, PluginVersion, gameFingerprint, contentFingerprint, name,
                transport.HostClientId, roomId);
            host.Start();
            Status = "公共房间已创建";
        }
        catch (Exception ex)
        {
            if (generation != connectionGeneration) return;
            Disconnect(); Status = "创建公共房间失败：" + ex.Message; Logger.LogError(ex);
        }
    }

    internal async void HostPublicFromSave(string roomName, string password, string path)
    {
        Disconnect(); var generation = connectionGeneration;
        try
        {
            var save = Singleton<BattleAndMapFileSystem>.self.ReadFileWithMeta_Local(path) ?? throw new InvalidDataException("无法读取遭遇战存档。");
            if (!save.HasKey("startGameSetting")) throw new InvalidDataException("该文件不是可联机的遭遇战存档。");
            var settings = StartGameSetting.LoadOb(save.GetKey_Obj("startGameSetting"));
            var name = NormalizeDisplayName(); var roomId = Guid.NewGuid();
            var registration = new RelayRegisterRoomRequest
            {
                RequestId = ++nextRequestId,
                RoomId = roomId,
                RoomName = string.IsNullOrWhiteSpace(roomName) ? name + " 的房间" : roomName.Trim(),
                HostName = name,
                Password = password ?? "",
                PluginVersion = PluginVersion,
                GameFingerprint = gameFingerprint,
                ContentFingerprint = contentFingerprint
            };
            Status = "正在从存档创建公共房间";
            var transport = await RelayHostTransport.ConnectAsync(ConfiguredRelayEndpoint, registration);
            if (generation != connectionGeneration) { transport.Dispose(); return; }
            host = new HostSession(transport, PluginVersion, gameFingerprint, contentFingerprint, name, transport.HostClientId, roomId);
            host.ConfigureSavedGame(save, settings); host.Start(); savedGamePath = path; Status = "已从存档创建公共房间";
        }
        catch (Exception ex)
        {
            if (generation != connectionGeneration) return;
            Disconnect(); Status = "从存档创建公共房间失败：" + ex.Message; Logger.LogError(ex);
        }
    }

    internal async void Join(string address, int port)
    {
        Disconnect();
        try
        {
            NormalizeDisplayName();
            client = new ClientSession(); Status = $"正在连接 {address}:{port}";
            await client.ConnectAsync(address, port, CreateHello());
            Status = $"已连接 {address}:{port}，等待握手";
        }
        catch (Exception ex) { Disconnect(); Status = "连接失败：" + ex.Message; Logger.LogError(ex); }
    }

    internal async void JoinPublic(Guid roomId, string password)
    {
        Disconnect();
        var generation = connectionGeneration;
        try
        {
            NormalizeDisplayName(); var endpoint = RelayEndpoint.Parse(ConfiguredRelayEndpoint);
            var connectingClient = new ClientSession(new RelayClientTransport(roomId, password, ++nextRequestId));
            client = connectingClient;
            Status = "正在加入公共房间";
            await connectingClient.ConnectAsync(endpoint.Host, endpoint.Port, CreateHello());
            if (generation != connectionGeneration) { connectingClient.Dispose(); return; }
            Status = "已连接公共房间，等待主机握手";
        }
        catch (Exception ex)
        {
            if (generation != connectionGeneration) return;
            Disconnect(); Status = "加入公共房间失败：" + ex.Message; Logger.LogError(ex);
        }
    }

    internal Task<IReadOnlyList<RelayRoomInfo>> ListPublicRoomsAsync() =>
        RelayDirectoryClient.ListRoomsAsync(ConfiguredRelayEndpoint, ++nextRequestId);
    internal bool IsPublicRoomCompatible(RelayRoomInfo room) => room.PluginVersion == PluginVersion &&
        room.GameFingerprint == gameFingerprint && room.ContentFingerprint == contentFingerprint;

    private HelloMessage CreateHello() => new HelloMessage
    {
        PluginVersion = PluginVersion,
        GameFingerprint = gameFingerprint,
        ContentFingerprint = contentFingerprint,
        DisplayName = NormalizeDisplayName()
    };

    private string NormalizeDisplayName()
    {
        var value = (displayName?.Value ?? Environment.UserName).Trim();
        if (value.Length == 0) throw new InvalidOperationException("请输入用户名。");
        if (value.Length > 32) throw new InvalidOperationException("用户名不能超过 32 个字符。");
        if (displayName is not null) displayName.Value = value;
        return value;
    }

    internal void SubmitSimpleCommand(CommandKind kind)
    {
        if (client?.ClientId is not Guid clientId) { Status = "尚未完成客机握手"; return; }
        var request = new CommandRequest { ClientId = clientId, RequestId = ++nextRequestId, AppliedFrameId = client.AppliedFrameId, Command = new GameCommand { Kind = kind } };
        _ = client.SendCommandAsync(request);
    }

    internal void ClaimSeat(int lobbySlotIndex)
    {
        if (IsMatchStarting) return;
        if (host is not null)
        {
            if (!host.ClaimLocalSeat(lobbySlotIndex, NormalizeDisplayName(), out var reason)) Status = reason;
            return;
        }
        if (client is not null) _ = client.ClaimSeatAsync(lobbySlotIndex);
    }
    internal void ReleaseSeat()
    {
        if (IsMatchStarting) return;
        if (host is not null) { if (host.Room.ReleaseSeat(host.LocalHostClientId)) host.BroadcastRoom(); return; }
        if (client is not null) _ = client.ReleaseSeatAsync();
    }

    internal void JoinSelectedMatch()
    {
        if (client?.ClientId is not Guid clientId || client.Room?.MatchStarted != true) return;
        var seat = client.Room.Seats.Find(value => value.ClientId == clientId && value.Connected && value.PendingActivation);
        if (seat is null) { Status = "请先选择可接管席位"; return; }
        Status = "正在加入对局并同步状态"; _ = client.JoinMatchAsync(seat.SeatId);
    }
    internal void ToggleReady()
    {
        if (IsMatchStarting) return;
        if (LocalIdentityId is not Guid id) return;
        var seat = CurrentRoom?.Seats.Find(s => s.ClientId == id);
        if (seat is null) return;
        if (host is not null) host.SetLocalReady(!seat.Ready);
        else if (client is not null) _ = client.SetReadyAsync(!seat.Ready);
    }

    internal void SubmitCommand(GameCommand command)
    {
        if (InputGate.MultiplayerActive && !InputGate.MaySubmit)
        {
            Status = "当前不是你的操作回合";
            return;
        }
        if (host is not null)
        {
            operations.Enqueue(new CommandRequest { ClientId = host.LocalHostClientId, RequestId = ++nextRequestId, SeatId = host.LocalHostSeatId, Round = GS_Battle.self?.turns ?? 0, AppliedFrameId = host.MatchId.HasValue ? 0 : -1, Command = command });
            return;
        }
        if (client?.ClientId is not Guid clientId) { Status = "尚未连接权威主机"; return; }
        if (!MultiplayerUnitStates.TryEnterAwaitingAuthority(command, out var token)) { Status = "该单位正在等待主机响应"; ShowBattleMessage(Status); return; }
        var seatId = client.Room?.Seats.Find(s => s.ClientId == clientId)?.SeatId ?? Guid.Empty;
        var requestId = ++nextRequestId;
        pendingClientCommands[requestId] = (command, token);
        _ = client.SendCommandAsync(new CommandRequest { ClientId = clientId, RequestId = requestId, SeatId = seatId, Round = GS_Battle.self?.turns ?? 0, AppliedFrameId = client.AppliedFrameId, Command = command });
    }

    internal void NotifyNoEligibleUnits()
    {
        const string notice = "所选单位当前无法执行该操作。";
        if (!ShowBattleMessage(notice)) Status = notice;
    }

    private void LogSessionEvent(SessionLogLevel level, string message)
    {
        if (level == SessionLogLevel.Warning) Logger.LogWarning(message);
        else Logger.LogInfo(message);
    }

    private void OnClientCommandResponse(CommandResponse response, bool accepted)
    {
        if (!pendingClientCommands.TryGetValue(response.RequestId, out var pending)) return;
        pendingClientCommands.Remove(response.RequestId);
        if (accepted) return;
        MultiplayerUnitStates.RejectAwaitingAuthority(pending.Command, pending.Token);
        var notice = "操作未执行：" + response.Reason;
        Logger.LogWarning($"Command rejected source=remote request={response.RequestId} kind={pending.Command.Kind} round={GS_Battle.self?.turns ?? 0} target={pending.Command.TargetX},{pending.Command.TargetY} reason={response.Reason}");
        if (!ShowBattleMessage(notice)) Status = notice;
    }

    internal void SubmitSurrender()
    {
        if (!InputGate.MultiplayerActive || GS_Battle.self is null) return;
        if (host is not null)
        {
            var seat = host.Room.Seats.FirstOrDefault(value => value.ClientId == host.LocalHostClientId);
            if (seat is null) { Status = "主机尚未拥有真人席位"; return; }
            operations.Enqueue(new CommandRequest
            {
                ClientId = host.LocalHostClientId,
                RequestId = ++nextRequestId,
                SeatId = seat.SeatId,
                Round = GS_Battle.self.turns,
                AppliedFrameId = 0,
                Command = new GameCommand { Kind = CommandKind.Surrender, TargetX = seat.PlayerIndex }
            });
            return;
        }
        if (client?.ClientId is not Guid clientId) { Status = "尚未连接权威主机"; return; }
        var owned = client.Room?.Seats.Find(value => value.ClientId == clientId);
        if (owned is null) { Status = "尚未拥有真人席位"; return; }
        _ = client.SendCommandAsync(new CommandRequest
        {
            ClientId = clientId,
            RequestId = ++nextRequestId,
            SeatId = owned.SeatId,
            Round = GS_Battle.self.turns,
            AppliedFrameId = client.AppliedFrameId,
            Command = new GameCommand { Kind = CommandKind.Surrender, TargetX = owned.PlayerIndex }
        });
    }

    private void OnHostCommand(CommandRequest request, IRemotePeer peer)
    {
        Logger.LogInfo($"Received command {request.Command.Kind} request={request.RequestId} from {request.ClientId}");
        var reason = "Host battle is not available.";
        if (host is null || GS_Battle.self is null || !host.Authorize(request, GS_Battle.self.current_co_index, GS_Battle.self.turns, out reason))
        {
            host?.Reject(peer, request.RequestId, reason); return;
        }
        if (request.Command.Kind == CommandKind.Surrender)
        {
            var seat = host.Room.Seats.First(value => value.SeatId == request.SeatId);
            request.Command.TargetX = seat.PlayerIndex;
        }
        if (!operations.Enqueue(request)) { host.Reject(peer, request.RequestId, "Duplicate request."); return; }
        requestPeers[RequestKey(request)] = peer;
    }

    private void OnAuthorityFrame(AuthorityFrame frame)
    {
        Logger.LogDebug($"Verified authority frame {frame.FrameId} ({frame.FrameType})");
        pendingAuthorityFrames.Enqueue(frame);
    }

    private void PumpAuthorityFrames()
    {
        if (client is null || loadingSnapshot is not null) return;
        while (pendingAuthorityFrames.Count != 0)
        {
            var frame = pendingAuthorityFrames.Peek();
            if (frame.FrameType == AuthorityFrameType.OperationBegin && replayOperationId.HasValue) return;
            if (frame.FrameType == AuthorityFrameType.MatchEnded && replayOperationId.HasValue) return;
            if ((frame.FrameType == AuthorityFrameType.Resolution || frame.FrameType == AuthorityFrameType.OperationFailed) &&
                replayPreludeStarted && !replayPreludeCompleted) return;
            pendingAuthorityFrames.Dequeue();
            try { ApplyAuthorityFrame(frame); }
            catch (Exception ex) { AbortReplay("权威帧执行失败：" + ex.Message); return; }
        }
    }

    private void ApplyAuthorityFrame(AuthorityFrame frame)
    {
        if (client is null) return;
        if (frame.FrameType == AuthorityFrameType.OperationBegin)
        {
            var begin = ProtocolCodec.DecodeOperationBegin(frame.Payload);
            replayOperationId = begin.OperationId; replayExecutionCompleted = false; replayEnd = null;
            replayBegin = begin; replayFrameIds.Add(frame.FrameId);
            if (begin.Command.Kind == CommandKind.BuildWithMove)
                Logger.LogDebug($"Client BuildWithMove received Begin frame={frame.FrameId} op={begin.OperationId:N} time={Time.realtimeSinceStartup:F3}");
            if (begin.Command.Kind == CommandKind.EquipmentMoveAction) StartClientEquipmentMovePrelude();
            else if (CanReplayAtBegin(begin.Command)) StartClientReplay(Array.Empty<RandomRecord>());
        }
        else if (frame.FrameType == AuthorityFrameType.Resolution)
        {
            var resolution = ProtocolCodec.DecodeResolution(frame.Payload);
            if (replayBegin is null || resolution.OperationId != replayBegin.OperationId) { AbortReplay("结算帧不属于当前操作"); return; }
            replayFrameIds.Add(frame.FrameId);
            if (replayBegin.Command.Kind == CommandKind.EquipmentMoveAction)
            {
                if (replayExecutionStarted)
                {
                    if (resolution.RandomRecords.Count != 0) { AbortReplay("即时移动后动作产生了未预期的随机结果"); return; }
                }
                else StartClientEquipmentMoveResolution(resolution.RandomRecords);
            }
            else if (replayExecutionStarted)
            {
                if (resolution.RandomRecords.Count != 0) { AbortReplay("即时回放操作产生了未预期的随机结果"); return; }
            }
            else StartClientReplay(resolution.RandomRecords);
        }
        else if (frame.FrameType == AuthorityFrameType.OperationEnd)
        {
            replayEnd = ProtocolCodec.DecodeOperationEnd(frame.Payload); replayFrameIds.Add(frame.FrameId); TryCommitReplay();
        }
        else if (frame.FrameType == AuthorityFrameType.OperationFailed)
        {
            var failed = ProtocolCodec.DecodeOperationFailed(frame.Payload);
            if (replayBegin is null || failed.OperationId != replayBegin.OperationId) { AbortReplay("失败帧不属于当前操作"); return; }
            replayFrameIds.Add(frame.FrameId);
            Logger.LogWarning("Authority operation failed: " + failed.Reason);
            Status = "主机未能执行操作：" + failed.Reason;
            pendingMatchEnd = null;
            foreach (var frameId in replayFrameIds) client.MarkApplied(frameId);
            if (GS_Battle.self is not null) GS_Battle.self.unit_busy = false;
            ResetReplayState();
        }
        else if (frame.FrameType == AuthorityFrameType.SeatChanged)
        {
            var changed = ProtocolCodec.DecodeSeatControlChanged(frame.Payload);
            if (GS_Battle.self?.all_player?.players is not null && changed.PlayerIndex >= 0 && changed.PlayerIndex < GS_Battle.self.all_player.players.Count)
                GS_Battle.self.all_player.players[changed.PlayerIndex].is_ai = changed.AiControlled;
            client.MarkApplied(frame.FrameId);
        }
        else if (frame.FrameType == AuthorityFrameType.MatchEnded)
        {
            client.MarkMatchEnded();
            client.MarkApplied(frame.FrameId);
            var localVictory = pendingMatchEnd ?? (ProtocolCodec.DecodeInt64(frame.Payload) != 0);
            pendingMatchEnd = null;
            ApplyAuthorityMatchEnd(localVictory);
        }
        else client.MarkApplied(frame.FrameId);
    }

    private static bool CanReplayAtBegin(GameCommand command) =>
        command.Kind == CommandKind.Move || command.Kind == CommandKind.UndoMove || command.Kind == CommandKind.AutoGuideCancel ||
        command.Kind == CommandKind.DebugAddResources || command.Kind == CommandKind.DebugFillSkill ||
        command.Kind == CommandKind.Surrender || command.Kind == CommandKind.BuildWithMove ||
        command.Kind == CommandKind.ToggleStandby || command.Kind == CommandKind.ToggleSleep || command.Kind == CommandKind.Stay ||
        command.Kind == CommandKind.Action && (IsKnownNonRandomBuildAction(command.ActionCategory) || command.ActionCategory == (int)ActionCate.TRAIN) ||
        (command.Kind == CommandKind.Skill || command.Kind == CommandKind.AiSkill) && IsKnownNonRandomSkill();

    private static bool IsKnownNonRandomBuildAction(int actionCategory) =>
        actionCategory == (int)ActionCate.BUILD || actionCategory == (int)ActionCate.HELP_BUILD ||
        actionCategory == (int)ActionCate.QUICK_BUILD_MINER;

    private static bool IsKnownNonRandomEquipmentMoveAction(GameCommand command) =>
        command.Kind == CommandKind.EquipmentMoveAction &&
        IsKnownNonRandomBuildAction(command.ActionCategory);

    private static bool IsKnownNonRandomSkill()
    {
        var data = GS_Battle.self?.cur_player?.co_data;
        if (data?.skill_action is null) return false;
        if (!IsKnownNonRandomSkillAction(data.skill_action)) return false;
        if (data.skill_actions is not null)
            foreach (var action in data.skill_actions)
                if (action is not null && !IsKnownNonRandomSkillAction(action)) return false;
        return true;
    }

    private static bool IsKnownNonRandomSkillAction(ActionData action)
    {
        var type = action.GetType();
        return type == typeof(Skill_EffectCast) || type == typeof(Skill_RepairAndEffect) ||
               type == typeof(Skill_Reactivate) || type == typeof(Skill_Split);
    }

    private void StartClientReplay(IEnumerable<RandomRecord> records)
    {
        if (replayBegin is null || replayExecutionStarted) return;
        var commandKind = replayBegin.Command.Kind;
        var operationId = replayBegin.OperationId;
        var startedAt = Time.realtimeSinceStartup;
        replayExecutionStarted = true;
        if (commandKind == CommandKind.BuildWithMove)
            Logger.LogDebug($"Client BuildWithMove execution started op={operationId:N} time={startedAt:F3}");
        replayRandomTape.BeginReplay(records); GameplayRandom.ActiveTape = replayRandomTape;
        executor.Start(replayBegin.Command, ExecutionOrigin.ClientReplay,
            () =>
            {
                try
                {
                    replayRandomTape.EndReplay(); replayExecutionCompleted = true;
                    if (commandKind == CommandKind.BuildWithMove)
                        Logger.LogDebug($"Client BuildWithMove execution completed op={operationId:N} elapsed={Time.realtimeSinceStartup - startedAt:F3}s");
                }
                catch (Exception ex) { AbortReplay("随机回放失败：" + ex.Message); }
                finally { GameplayRandom.ActiveTape = null; }
                TryCommitReplay();
            },
            ex => { GameplayRandom.ActiveTape = null; AbortReplay("回放失败：" + ex.Message); });
    }

    private void StartClientEquipmentMovePrelude()
    {
        if (replayBegin is null || replayPreludeStarted) return;
        replayPreludeStarted = true;
        replayRandomTape.BeginReplay(Array.Empty<RandomRecord>()); GameplayRandom.ActiveTape = replayRandomTape;
        executor.StartEquipmentMovePrelude(replayBegin.Command, ExecutionOrigin.ClientReplay,
            () =>
            {
                try { replayRandomTape.EndReplay(); replayPreludeCompleted = true; }
                catch (Exception ex) { AbortReplay("移动前段回放失败：" + ex.Message); }
                finally { GameplayRandom.ActiveTape = null; }
                if (replayBegin is not null && IsKnownNonRandomEquipmentMoveAction(replayBegin.Command))
                    StartClientEquipmentMoveResolution(Array.Empty<RandomRecord>());
            },
            ex => { GameplayRandom.ActiveTape = null; AbortReplay("移动前段回放失败：" + ex.Message); });
    }

    private void StartClientEquipmentMoveResolution(IEnumerable<RandomRecord> records)
    {
        if (replayBegin is null || replayExecutionStarted || !replayPreludeCompleted) return;
        replayExecutionStarted = true;
        replayRandomTape.BeginReplay(records); GameplayRandom.ActiveTape = replayRandomTape;
        executor.StartEquipmentMoveResolution(replayBegin.Command, ExecutionOrigin.ClientReplay,
            () => { try { replayRandomTape.EndReplay(); replayExecutionCompleted = true; } catch (Exception ex) { AbortReplay("随机回放失败：" + ex.Message); } finally { GameplayRandom.ActiveTape = null; } TryCommitReplay(); },
            ex => { GameplayRandom.ActiveTape = null; AbortReplay("移动后动作回放失败：" + ex.Message); });
    }

    private void PumpOperations()
    {
        if (host is null || !host.MatchId.HasValue || executor.IsBusy || authorityAiOperationActive || !operations.TryBegin(out var request) || request is null) return;
        if (request.Command.Kind == CommandKind.Move) NormalizeQueuedMove(request.Command);
        var validation = executor.Validate(request.Command);
        if (validation is not null)
        {
            var source = request.ClientId == host.LocalHostClientId ? "local" : "remote";
            Logger.LogWarning($"Command rejected source={source} request={request.RequestId} kind={request.Command.Kind} round={request.Round} target={request.Command.TargetX},{request.Command.TargetY} reason={validation}");
            if (requestPeers.TryGetValue(RequestKey(request), out var rejectedPeer))
            {
                host.Reject(rejectedPeer, request.RequestId, validation);
                requestPeers.Remove(RequestKey(request));
            }
            else if (source == "local")
            {
                var notice = "操作未执行：" + validation;
                if (!ShowBattleMessage(notice)) Status = notice;
            }
            operations.Complete(); return;
        }
        var operationId = Guid.NewGuid();
        Logger.LogDebug($"Authority operation begin source={(request.ClientId == host.LocalHostClientId ? "local" : "remote")} request={request.RequestId} kind={request.Command.Kind} op={operationId:N} round={request.Round} target={request.Command.TargetX},{request.Command.TargetY}");
        var beginFrame = host.AppendAndBroadcast(AuthorityFrameType.OperationBegin, ProtocolCodec.EncodeOperationBegin(new OperationBeginPayload { OperationId = operationId, SeatId = request.SeatId, RequestId = request.RequestId, Round = request.Round, Command = request.Command }));
        if (requestPeers.TryGetValue(RequestKey(request), out var requestPeer)) { host.Accept(requestPeer, request.RequestId, beginFrame.FrameId); requestPeers.Remove(RequestKey(request)); }
        hostRandomTape.BeginRecording(); GameplayRandom.ActiveTape = hostRandomTape;
        executor.Start(request.Command, ExecutionOrigin.HostAuthority,
            () =>
            {
                var records = hostRandomTape.EndRecording(); GameplayRandom.ActiveTape = null;
                var resolution = new ResolutionPayload { OperationId = operationId, StageId = 0, SettlementOrdinal = 0 };
                foreach (var record in records) resolution.RandomRecords.Add(record);
                host.AppendAndBroadcast(AuthorityFrameType.Resolution, ProtocolCodec.EncodeResolution(resolution));
                var endFrame = host.AppendAndBroadcast(AuthorityFrameType.OperationEnd, ProtocolCodec.EncodeOperationEnd(new OperationEndPayload { OperationId = operationId }));
                host.SetLatestSnapshot(GameStateSerializer.CaptureSnapshot(), endFrame);
                Logger.LogDebug($"Authority operation end kind={request.Command.Kind} op={operationId:N} frame={endFrame.FrameId}");
                operations.Complete();
                if (FlushPendingMatchEnd()) return;
                if (request.Command.Kind == CommandKind.EndTurn ||
                    request.Command.Kind == CommandKind.Surrender && request.Command.TargetX == GS_Battle.self.current_co_index)
                    GameController.self.StartCoroutine(RunHostTurnAdvance(), "XingyiStarryMpTurnAdvance");
            },
            ex =>
            {
                GameplayRandom.ActiveTape = null; Logger.LogError(ex);
                if (GS_Battle.self is not null)
                {
                    GS_Battle.self.unit_busy = false;
                    BattleEventBus.self?.TriggerUnitBusyChanged(isBusy: false);
                }
                host.AppendAndBroadcast(AuthorityFrameType.OperationFailed, ProtocolCodec.EncodeOperationFailed(new OperationFailedPayload { OperationId = operationId, Reason = ex.Message }));
                operations.Complete();
            });
    }

    private static void NormalizeQueuedMove(GameCommand command)
    {
        if (GS_Battle.self?.all_unit is null) return;
        MoveCommandFilter.TryFilter(command, id =>
        {
            if (id < int.MinValue || id > int.MaxValue) return MoveUnitDecision.Reject;
            var unit = GS_Battle.self.all_unit.GetUnitByID((int)id);
            if (unit is null || unit.player != GS_Battle.self.cur_player) return MoveUnitDecision.Reject;
            return unit.moved || unit.in_animation || unit.building ? MoveUnitDecision.Skip : MoveUnitDecision.Include;
        });
    }

    internal IEnumerator RunHostAiCommand(GameCommand command)
    {
        if (host is null || !host.MatchId.HasValue) yield break;
        while (executor.IsBusy || authorityAiOperationActive) yield return 0f;
        authorityAiOperationActive = true;
        try
        {
            var validation = executor.Validate(command);
            if (validation is not null) { Logger.LogError("AI command rejected: " + validation); yield break; }
            var operationId = Guid.NewGuid();
            Logger.LogDebug($"Authority AI operation begin kind={command.Kind} op={operationId:N} player={GS_Battle.self.current_co_index} round={GS_Battle.self.turns}");
            var seat = host.Room.Seats.FirstOrDefault(value => value.PlayerIndex == GS_Battle.self.current_co_index);
            host.AppendAndBroadcast(AuthorityFrameType.OperationBegin, ProtocolCodec.EncodeOperationBegin(new OperationBeginPayload
            {
                OperationId = operationId,
                SeatId = seat?.SeatId ?? Guid.Empty,
                RequestId = ++nextRequestId,
                Round = GS_Battle.self.turns,
                Command = command
            }));
            hostRandomTape.BeginRecording(); GameplayRandom.ActiveTape = hostRandomTape;
            var finished = false; Exception? failure = null;
            executor.Start(command, ExecutionOrigin.HostAuthority, () => finished = true, ex => { failure = ex; finished = true; });
            while (!finished) yield return 0f;
            if (failure is not null)
            {
                GameplayRandom.ActiveTape = null; Logger.LogError(failure);
                host.AppendAndBroadcast(AuthorityFrameType.OperationFailed, ProtocolCodec.EncodeOperationFailed(
                    new OperationFailedPayload { OperationId = operationId, Reason = failure.Message }));
                yield break;
            }
            var records = hostRandomTape.EndRecording(); GameplayRandom.ActiveTape = null;
            var resolution = new ResolutionPayload { OperationId = operationId, StageId = 0, SettlementOrdinal = 0 };
            foreach (var record in records) resolution.RandomRecords.Add(record);
            host.AppendAndBroadcast(AuthorityFrameType.Resolution, ProtocolCodec.EncodeResolution(resolution));
            var end = host.AppendAndBroadcast(AuthorityFrameType.OperationEnd,
                ProtocolCodec.EncodeOperationEnd(new OperationEndPayload { OperationId = operationId }));
            host.SetLatestSnapshot(GameStateSerializer.CaptureSnapshot(), end);
            Logger.LogDebug($"Authority AI operation end kind={command.Kind} op={operationId:N} frame={end.FrameId}");
            FlushPendingMatchEnd();
        }
        finally
        {
            GameplayRandom.ActiveTape = null;
            authorityAiOperationActive = false;
        }
    }

    internal IEnumerator RunHostAiEndTurn()
    {
        yield return RunHostAiCommand(new GameCommand { Kind = CommandKind.EndTurn });
        if (!turnAdvanceRunning && host?.MatchId.HasValue == true && GS_Battle.self?.game_running == true)
            yield return RunHostTurnAdvance();
    }

    private IEnumerator RunHostTurnAdvance()
    {
        if (turnAdvanceRunning || host is null || !host.MatchId.HasValue) yield break;
        turnAdvanceRunning = true;
        try
        {
            while (host is not null && host.MatchId.HasValue && GS_Battle.self?.game_running == true)
            {
                yield return WaitForHostScripts();
                if (GS_Battle.self?.game_running != true) break;
                PrepareNextSeatActivation();
                yield return RunHostAiCommand(new GameCommand { Kind = CommandKind.TurnAdvance });
                if (pendingMatchEnd.HasValue || GS_Battle.self?.game_running != true) break;
                var player = GS_Battle.self.cur_player;
                if (!player.is_ai) break;
                yield return player.ai.OnStartTurn_DoTurn();
                if (pendingMatchEnd.HasValue || GS_Battle.self?.game_running != true) break;
                yield return RunHostAiCommand(new GameCommand { Kind = CommandKind.EndTurn });
                if (pendingMatchEnd.HasValue || GS_Battle.self?.game_running != true) break;
            }
        }
        finally { turnAdvanceRunning = false; }
    }

    private void PrepareNextSeatActivation()
    {
        if (host?.MatchId.HasValue != true || GS_Battle.self?.all_player?.players is null) return;
        var players = GS_Battle.self.all_player.players;
        if (players.Count == 0) return;
        var next = GS_Battle.self.current_co_index;
        for (var checkedPlayers = 0; checkedPlayers < players.Count; checkedPlayers++)
        {
            next++; if (next >= players.Count) next = 0;
            var player = players[next];
            if (player.fraction == Fraction.NEUTRAL || player.defeated) continue;
            var seat = host.Room.Seats.FirstOrDefault(value => value.PlayerIndex == next && value.PendingActivation);
            if (seat is null || !host.Room.ActivatePendingClaim(next)) return;
            var changed = new SeatControlChanged { SeatId = seat.SeatId, PlayerIndex = next, AiControlled = false, Reason = "玩家将在本回合开始接管席位。" };
            host.AppendAndBroadcast(AuthorityFrameType.SeatChanged, ProtocolCodec.EncodeSeatControlChanged(changed));
            ApplyRoomSeatModes(host.Room.Snapshot()); host.BroadcastRoom();
            return;
        }
    }

    private IEnumerator WaitForHostScripts()
    {
        var started = Time.realtimeSinceStartup;
        var warned = false;
        while (GS_Battle.self?.game_running == true && GS_Battle.self.script_processing)
        {
            if (!warned && Time.realtimeSinceStartup - started >= 5f)
            {
                warned = true;
                Logger.LogWarning("Authority turn advance is waiting for host GameRule processing to finish.");
            }
            yield return 0f;
        }
    }

    internal bool InterceptNativeMatchEnd(bool victory)
    {
        if (!InputGate.MultiplayerActive || applyingAuthorityMatchEnd) return true;
        if (client is not null) { pendingMatchEnd = victory; return false; }
        if (host?.MatchId.HasValue != true) return true;
        pendingMatchEnd = victory;
        if (!executor.IsBusy) FlushPendingMatchEnd();
        return false;
    }

    private bool FlushPendingMatchEnd()
    {
        if (!pendingMatchEnd.HasValue || host?.MatchId.HasValue != true) return false;
        var victory = pendingMatchEnd.Value;
        pendingMatchEnd = null;
        host.AppendAndBroadcast(AuthorityFrameType.MatchEnded, ProtocolCodec.EncodeInt64(victory ? 1 : 0));
        ApplyAuthorityMatchEnd(victory);
        return true;
    }

    private void ApplyAuthorityMatchEnd(bool victory)
    {
        if (SingletonMono<SS_ANNW_Game>.self is null) return;
        applyingAuthorityMatchEnd = true;
        try { AccessTools.Method(typeof(SS_ANNW_Game), "EndGame")?.Invoke(SingletonMono<SS_ANNW_Game>.self, new object[] { victory }); }
        finally { applyingAuthorityMatchEnd = false; }
    }

    internal static GameCommand CreateAiUnitCommand(UnitAI unitAi, UtilityItem utility, bool skipping)
    {
        var action = (UT_UnitAction)utility;
        return new GameCommand
        {
            Kind = CommandKind.AiUnitAction,
            UnitIds = new long[] { unitAi.owner.unit_id },
            UnitTargetXs = new[] { action.move_pos.x },
            UnitTargetYs = new[] { action.move_pos.y },
            TargetX = action.action_pos.x,
            TargetY = action.action_pos.y,
            AiActionType = (int)action.type,
            ActionCategory = action.action is null ? (int)ActionCate.NONE : (int)action.action.sd_action.cate,
            TemplateId = action.create_tp?.sd_unit?.name ?? "",
            PassengerUnitId = action.unload_unit?.unit_id ?? 0,
            DesiredToggleState = skipping
        };
    }

    internal static IEnumerator WaitForAuthorityAiTurnEnd(int playerIndex)
    {
        while (InputGate.MultiplayerActive && GS_Battle.self?.game_running == true && GS_Battle.self.current_co_index == playerIndex)
            yield return 0f;
    }
    private static string RequestKey(CommandRequest request) => request.ClientId.ToString("N") + ":" + request.RequestId;

    private void TryCommitReplay()
    {
        if (!replayOperationId.HasValue || !replayExecutionCompleted || replayEnd is null) return;
        if (replayEnd.OperationId != replayOperationId.Value)
        {
            AbortReplay("操作结束帧与当前回放不匹配"); return;
        }
        Logger.LogDebug($"Client replay committed operation {replayOperationId.Value:N}.");
        if (client is not null)
            foreach (var frameId in replayFrameIds) client.MarkApplied(frameId);
        ResetReplayState();
    }

    private void AbortReplay(string reason)
    {
        var firstUnused = replayRandomTape.FirstUnused;
        Logger.LogWarning($"Replay aborted op={replayOperationId?.ToString("N") ?? "none"} kind={replayBegin?.Command.Kind.ToString() ?? "none"} frames={replayFrameIds.FirstOrDefault()}..{replayFrameIds.LastOrDefault()} first_unused={firstUnused?.CallSite ?? "none"}/{firstUnused?.Ordinal.ToString() ?? "none"}: {reason}");
        Status = reason + "，正在请求完整同步";
        pendingMatchEnd = null;
        InputGate.LocalSeatMayAct = false;
        pendingAuthorityFrames.Clear(); ResetReplayState();
        _ = client?.RequestSnapshotAsync();
    }

    internal void RequestManualResync()
    {
        if (!CanManualResync)
        {
            Status = client?.SnapshotRequested == true ? "完整同步已经在进行中" : "当前不能重新同步";
            return;
        }
        Logger.LogWarning($"Manual full synchronization requested at verified={client!.VerifiedFrameId} applied={client.AppliedFrameId} replay={replayOperationId?.ToString("N") ?? "none"} executorBusy={executor.IsBusy}.");
        Status = "已手动请求完整同步，正在等待主机快照";
        InputGate.LocalSeatMayAct = false;
        pendingMatchEnd = null;
        pendingAuthorityFrames.Clear();
        ResetReplayState();
        _ = client.RequestSnapshotAsync();
    }

    private void ResetReplayState()
    {
        replayOperationId = null; replayBegin = null; replayExecutionStarted = false; replayExecutionCompleted = false;
        replayPreludeStarted = false; replayPreludeCompleted = false; replayEnd = null; replayFrameIds.Clear();
    }

    private void OnSnapshotReceived(SnapshotManifest manifest, byte[] snapshot)
    {
        if (loadingSnapshot is not null)
        {
            Logger.LogWarning($"Ignoring overlapping snapshot {manifest.SnapshotId:N} while load generation {snapshotLoadGeneration} is pending.");
            return;
        }
        try
        {
            Logger.LogInfo($"Applying snapshot id={manifest.SnapshotId:N} frame={manifest.FrameId}; abandoning old executorBusy={executor.IsBusy} replay={replayOperationId?.ToString("N") ?? "none"}.");
            ResetReplayState(); pendingAuthorityFrames.Clear(); pendingClientCommands.Clear(); executor.Reset(); GameplayRandom.ActiveTape = null;
            client?.AcceptSnapshotAnchor(manifest); loadingSnapshot = manifest;
            snapshotPreviousBattle = GS_Battle.self;
            snapshotLoadGeneration++;
            InputGate.MultiplayerActive = true; InputGate.LocalSeatMayAct = false;
            Status = $"已校验快照 {manifest.SnapshotId:N}，正在重建场景"; Logger.LogInfo($"{Status}; generation={snapshotLoadGeneration}");
            GameSnapshotLoader.Load(snapshot);
        }
        catch (Exception ex) { Status = "快照加载失败：" + ex.Message; Logger.LogError(ex); ShowBattleMessage(Status); }
    }

    private static void ApplyRoomSeatModes(RoomSnapshot room)
    {
        if (GS_Battle.self?.all_player?.players is null) return;
        foreach (var seat in room.Seats)
            if (seat.PlayerIndex >= 0 && seat.PlayerIndex < GS_Battle.self.all_player.players.Count)
                GS_Battle.self.all_player.players[seat.PlayerIndex].is_ai = seat.AiControlled;
    }

    internal void OnSeatTurnStarting(int playerIndex)
    {
        // Runtime seat activation is emitted before TurnAdvance so SeatChanged remains outside an operation.
    }

    internal Player? GetLocalDisplayPlayer()
    {
        if (!InputGate.MultiplayerActive || GS_Battle.self?.all_player?.players is null || LocalIdentityId is not Guid identity) return null;
        var seat = CurrentRoom?.Seats.Find(value => value.ClientId == identity);
        if (seat is null || seat.PlayerIndex < 0 || seat.PlayerIndex >= GS_Battle.self.all_player.players.Count) return null;
        return GS_Battle.self.all_player.players[seat.PlayerIndex];
    }

    internal string? GetSeatDisplayName(Player player)
    {
        var seat = CurrentRoom?.Seats.Find(value => value.PlayerIndex == player.index && value.Connected);
        return seat is null || string.IsNullOrWhiteSpace(seat.DisplayName) ? null : seat.DisplayName;
    }

    private void ApplyLocalPerspectiveAndControl()
    {
        if (!InputGate.MultiplayerActive || GS_Battle.self is null) return;
        var localPlayer = GetLocalDisplayPlayer();
        var mayAct = InputGate.LocalSeatMayAct && localPlayer is not null;
        var localTurnOwned = localPlayer is not null && GS_Battle.self.cur_player == localPlayer &&
            CurrentRoom?.Seats.Find(value => value.PlayerIndex == localPlayer.index)?.AiControlled != true;
        if (!localTurnOwned && GS_Battle.self.selected_units.Count != 0 && UX_Manager.self is not null)
            UX_Manager.self.ClearUnitSelection();
        previousLocalTurnOwned = localTurnOwned;
        GS_Battle.self.is_player_in_control = mayAct;
        var mouse = SingletonMono<SS_ANNW_Game>.self?.mouse_input;
        if (mouse is not null && localPlayer is not null && GS_Battle.self.cur_player != localPlayer && !GS_Battle.self.cur_player.is_ai)
            mouse.free_mode = true;
        if (localPlayer is null) return;
        GS_Battle.self.last_human_player = localPlayer;
        if (perspectivePlayerIndex != localPlayer.index)
        {
            perspectivePlayerIndex = localPlayer.index;
            BattleEventBus.self?.TriggerFOWDirty();
        }
    }

    private void TryShowNativeNotice()
    {
        if (pendingNativeNotice.Length == 0 || Time.unscaledTime < nativeNoticeEarliest || GS_Battle.self?.game_running == true) return;
        var popup = UI_Floater.self?.pop_general;
        if (popup is null) return;
        var message = pendingNativeNotice; pendingNativeNotice = "";
        popup.ShowAsSimple(message);
    }

    internal void Disconnect()
    {
        connectionGeneration++;
        host?.Dispose(); host = null; client?.Dispose(); client = null;
        pendingAuthorityFrames.Clear(); pendingClientCommands.Clear(); ResetReplayState(); executor.Reset(); GameplayRandom.ActiveTape = null;
        loadingSnapshot = null; snapshotPreviousBattle = null;
        hostStartAuthorized = false; nativeHostBattleStarted = false; hostBootstrapAttempted = false; hostShutdownPending = false; perspectivePlayerIndex = -1; previousLocalTurnOwned = false;
        pendingMatchEnd = null; applyingAuthorityMatchEnd = false; turnAdvanceRunning = false; authorityAiOperationActive = false;
        forcedDisconnectedEndTurnPending = false; forcedDisconnectedEndTurnRunning = false; fastReconnectRunning = false; fastReconnectCancelRequested = false; savedGamePath = "";
        HideFastReconnectPopup();
        InputGate.MultiplayerActive = false; InputGate.LocalSeatMayAct = false; Status = "未连接";
    }

    private void OnDestroy()
    {
        Disconnect(); harmony?.UnpatchSelf(); harmony = null; Instance = null;
    }
}
