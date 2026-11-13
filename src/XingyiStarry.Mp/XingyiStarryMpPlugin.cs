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
using UnityEngine.SceneManagement;
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
    public const string PluginVersion = "0.11.2";

    private Harmony? harmony;
    private HostSession? host;
    private ClientSession? client;
    private ConfigEntry<int>? defaultPort;
    private ConfigEntry<string>? defaultAddress;
    private ConfigEntry<string>? displayName;
    private ConfigEntry<string>? relayEndpoint;
    private ConfigEntry<KeyCode>? pingKey;
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
    private OperationEndPayload? replayEnd;
    private OperationBeginPayload? replayBegin;
    private SnapshotManifest? loadingSnapshot;
    private GS_Battle? snapshotPreviousBattle;
    private int snapshotLoadGeneration;
    private bool hostStartAuthorized;
    private bool nativeHostBattleStarted;
    private bool hostBootstrapAttempted;
    private bool hostShutdownPending;
    private bool matchEndedNormally;
    private bool lobbyReturnPending;
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
    internal bool LocalAutoGuidePlanning { get; private set; }
    private int plannerGeneration;
    private ulong? localAutoGuideRequest;
    private bool? localAutoGuideResult;
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
    internal string Status { get; private set; } = "${status_disconnected}";
    internal bool ConnectionFailed { get; private set; }
    internal bool IsMatchStarting => hostStartAuthorized || client?.MatchStarting == true;
    internal bool CanClaimSeat => !IsMatchStarting && LocalIdentityId is Guid claimId &&
        CurrentRoom?.Participants.Find(value => value.ClientId == claimId)?.Admission is ParticipantAdmission.Lobby or ParticipantAdmission.JoinSelection &&
        CurrentRoom?.Seats.Exists(s => !s.Defeated && (CurrentRoom.SavedGame || CurrentRoom.MatchStarted || s.OriginallyHuman) && (!s.Connected || s.ClientId == claimId)) == true;
    internal bool CanSetReady => !matchEndedNormally && !lobbyReturnPending && !IsMatchStarting && CurrentRoom?.MatchStarted != true && LocalIdentityId is Guid id &&
        CurrentRoom?.Participants.Find(value => value.ClientId == id) is not null;
    internal bool CanReleaseSeat => !IsMatchStarting && LocalIdentityId is Guid id && CurrentRoom?.Seats.Find(s => s.ClientId == id) is not null && client?.JoinedMatch != true;
    internal bool CanStartHostedRoom => !matchEndedNormally && !lobbyReturnPending && host?.Room.CanStart == true;
    internal bool IsHost => host is not null;
    internal bool IsClient => client is not null;
    internal bool IsSpectator => LocalIdentityId is Guid participantId &&
        CurrentRoom?.Participants.Find(value => value.ClientId == participantId)?.Admission == ParticipantAdmission.Spectator;
    internal bool CanManualResync => client?.ClientId.HasValue == true && client.SnapshotRequested == false &&
        client.IsCaughtUp && client.AppliedFrameId == client.VerifiedFrameId && pendingAuthorityFrames.Count == 0 &&
        loadingSnapshot is null && !executor.IsBusy && !replayOperationId.HasValue && !fastReconnectRunning &&
        InputGate.MultiplayerActive && GS_Battle.self?.game_running == true;
    internal bool CanMultiplayerSave => InputGate.MultiplayerActive && GS_Battle.self?.game_running == true && GS_Battle.self.turns >= 1 &&
        !GS_Battle.self.functions.Querry(GAME_FUNCTION.NoSave) && !fastReconnectRunning &&
        loadingSnapshot is null && !executor.IsBusy && !authorityAiOperationActive && !turnAdvanceRunning && !LocalAutoGuidePlanning &&
        !forcedDisconnectedEndTurnPending && !forcedDisconnectedEndTurnRunning && !pendingMatchEnd.HasValue &&
        (host?.MatchId.HasValue == true || client?.IsCaughtUp == true && client.AppliedFrameId == client.VerifiedFrameId && pendingAuthorityFrames.Count == 0 && !replayOperationId.HasValue);
    internal bool ShouldCaptureHostAi => host?.MatchId.HasValue == true && GS_Battle.self?.game_running == true && GS_Battle.self.cur_player?.is_ai == true;
    internal bool ShouldSuppressClientAi => client?.IsCaughtUp == true && InputGate.MultiplayerActive;
    internal int ConfiguredPort { get => defaultPort?.Value ?? ProtocolConstants.DefaultPort; set { if (defaultPort is not null) defaultPort.Value = value; } }
    internal string ConfiguredAddress { get => defaultAddress?.Value ?? "127.0.0.1"; set { if (defaultAddress is not null) defaultAddress.Value = value; } }
    internal string ConfiguredDisplayName { get => displayName?.Value ?? Environment.UserName; set { if (displayName is not null) displayName.Value = value; } }
    internal string ConfiguredRelayEndpoint => relayEndpoint?.Value ?? "60.205.147.182:24555";
    internal RoomSnapshot? CurrentRoom => host?.Room.Snapshot() ?? client?.Room;
    internal IReadOnlyCollection<ChatEvent> CurrentChatHistory => client?.ChatHistory ?? host?.ChatHistory ?? Array.Empty<ChatEvent>();
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
        { reason = "Resource increments must be positive integers."; return false; }
        EnqueueHostDebug(new GameCommand { Kind = CommandKind.DebugAddResources, DebugPlayerIndex = playerIndex, DebugMetalDelta = metalDelta, DebugPowerDelta = powerDelta });
        reason = "Resource command queued on the host."; return true;
    }

    public bool TrySubmitDebugFillSkill(int playerIndex, out string reason)
    {
        if (!CanSubmitHostDebug(playerIndex, out reason)) return false;
        EnqueueHostDebug(new GameCommand { Kind = CommandKind.DebugFillSkill, DebugPlayerIndex = playerIndex });
        reason = "Fill skill command queued on the host."; return true;
    }

    private bool CanSubmitHostDebug(int playerIndex, out string reason)
    {
        if (!IsAuthorityHostBattleActive || host is null || GS_Battle.self?.all_player?.players is null)
        { reason = "Only the host can use debug commands during a multiplayer battle."; return false; }
        if (playerIndex < -1 || playerIndex >= GS_Battle.self.all_player.players.Count)
        { reason = "Invalid player index."; return false; }
        reason = ""; return true;
    }

    private void EnqueueHostDebug(GameCommand command)
    {
        if (host is null || GS_Battle.self is null) return;
        operations.Enqueue(new CommandRequest
        {
            ClientId = host.LocalHostClientId,
            RequestId = ++nextRequestId,
            SeatId = host.LocalHostSeatId ?? Guid.Empty,
            Round = GS_Battle.self.turns,
            AppliedFrameId = 0,
            Command = command
        });
    }

    private void Awake()
    {
        Instance = this;
        MpText.SetLanguage(Application.systemLanguage == SystemLanguage.Chinese ||
            Application.systemLanguage == SystemLanguage.ChineseSimplified || Application.systemLanguage == SystemLanguage.ChineseTraditional);
        defaultPort = Config.Bind("Network", "Port", ProtocolConstants.DefaultPort, "TCP port used to host or join a room.");
        defaultAddress = Config.Bind("Network", "Address", "127.0.0.1", "Default address for direct connections.");
        displayName = Config.Bind("Network", "DisplayName", Environment.UserName, "Display name in multiplayer rooms.");
        relayEndpoint = Config.Bind("Relay", "Endpoint", "60.205.147.182:24555", "Public relay server address in host:port format.");
        pingKey = Config.Bind("Ping", "Key", KeyCode.R, "Key for sending team map pings in battle.");
        try
        {
            var assemblyCSharp = Path.Combine(Paths.ManagedPath, "Assembly-CSharp.dll");
            var firstPass = Path.Combine(Paths.ManagedPath, "Assembly-CSharp-firstpass.dll");
            gameFingerprint = Fingerprint.FileSha256(assemblyCSharp);
            contentFingerprint = Fingerprint.Combined(assemblyCSharp, firstPass);
            var missing = GameCompatibility.Probe();
            if (missing.Count != 0) throw new InvalidOperationException("Incompatible game version; missing methods: " + string.Join(", ", missing));
            if (HasOldLanPlugin()) throw new InvalidOperationException("AnnW.LanMp detected. Do not load both multiplayer plugins.");
            harmony = new Harmony(PluginId); harmony.PatchAll(typeof(XingyiStarryMpPlugin).Assembly);
            NativePingKeyBinding.Ensure(pingKey?.Value ?? KeyCode.R);
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Game={gameFingerprint}");
        }
        catch (Exception ex)
        {
            Status = "Initialization failed: " + ex.Message; Logger.LogError(ex);
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
        host?.Pump(LogSessionEvent, OnHostCommand, OnHostLobbyDraft, _ => { });
        PumpHostSeatChanges();
        client?.Pump(LogSessionEvent, OnClientCommandResponse, OnAuthorityFrame, OnSnapshotReceived, ShowParticipantNotice);
        PumpSocialEvents();
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
                    : "${notice_connection_to_the_host_was_lost}";
                ExitDisconnectedClient(reason, resultNotice);
            }
        }
        client?.Tick();
        TryRestoreRoomLobby();
        NativeLobbyPanel.Tick(this);
        PublicLobbyPanel.Tick(this);
        NativeSkirmishLobby.Tick(this);
        ChatWindow.Tick(this);
        if (pingKey is not null)
        {
            var boundPingKey = NativePingKeyBinding.Current;
            if (boundPingKey != pingKey.Value) pingKey.Value = boundPingKey;
        }
        PingFeature.Tick(this);
        SpectatorFeature.Tick(this);
        if (client?.ClientId is not null && !fastReconnectRunning) Status = $"Connected. ClientId={client.ClientId:N}, verified frame={client.VerifiedFrameId}";
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
            InputGate.LocalSeatMayAct = !LocalAutoGuidePlanning && localSeat is not null && localSeat.PlayerIndex == GS_Battle.self.current_co_index && !localSeat.AiControlled;
            if (!executor.IsBusy) ApplyRoomSeatModes(host.Room.Snapshot());
        }
        ApplyLocalPerspectiveAndControl();
        if (InputGate.MultiplayerActive && GS_Battle.self is not null) GS_Battle.self.editor_enabled = false;
        // Scene singletons retain managed references after Unity destroys their native objects.
        var game = SingletonMono<SS_ANNW_Game>.self;
        if (InputGate.MultiplayerActive && game != null && game.ui != null)
        {
            var pauseMenu = game.ui.pop_pause_menu;
            if (pauseMenu != null && pauseMenu.gameObject.activeSelf) MultiplayerPauseMenuPatch.Refresh(pauseMenu);
        }
        PumpOperations();
        TryShowNativeNotice();
    }

    private void OnGUI() => ChatWindow.Draw(this);

    private IEnumerator RunFastReconnect()
    {
        if (client is null) yield break;
        CancelLocalAutoGuide();
        fastReconnectRunning = true; fastReconnectCancelRequested = false; fastReconnectAttempt = 0; fastReconnectStartedAt = Time.realtimeSinceStartup;
        InputGate.LocalSeatMayAct = false;
        var acceptedGeneration = client.ReconnectAcceptedGeneration;
        var schedule = new[] { 0f, 3f, 7f };
        for (var index = 0; index < schedule.Length && client is not null && !fastReconnectCancelRequested; index++)
        {
            while (!fastReconnectCancelRequested && Time.realtimeSinceStartup - fastReconnectStartedAt < schedule[index]) yield return 0f;
            if (fastReconnectCancelRequested) break;
            fastReconnectAttempt = index + 1; Status = $"Reconnecting ({fastReconnectAttempt}/3)";
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
                HideFastReconnectPopup(); fastReconnectRunning = false; Status = "Reconnected. Synchronizing the latest state"; yield break;
            }
            if (client?.TerminalSessionEnded == true || IsTerminalReconnectError(attempt?.Exception?.GetBaseException()))
            {
                HideFastReconnectPopup(); fastReconnectRunning = false;
                var terminalReason = !string.IsNullOrWhiteSpace(client?.ConnectionError)
                    ? client.ConnectionError
                    : "${notice_the_multiplayer_session_has_ended}";
                ExitDisconnectedClient(terminalReason, terminalReason);
                yield break;
            }
            if (attempt?.IsFaulted == true) Logger.LogWarning($"Reconnect attempt {fastReconnectAttempt}/3 failed: {attempt.Exception?.GetBaseException().Message}");
        }
        if (fastReconnectCancelRequested)
        {
            HideFastReconnectPopup(); fastReconnectRunning = false;
            ExitDisconnectedClient("${notice_reconnection_cancelled}");
            yield break;
        }
        var reason = client?.ConnectionError ?? "${ui_reconnection_failed}";
        HideFastReconnectPopup(); fastReconnectRunning = false;
        ExitDisconnectedClient(string.IsNullOrWhiteSpace(reason) ? "${notice_all_three_reconnection_attempts_failed}" : reason, "${notice_connection_to_the_host_was_lost}");
    }

    private void ExitDisconnectedClient(string reason, string resultNotice = "")
    {
        Disconnect();
        Status = "Multiplayer session ended: " + reason;
        ShowBattleMessage(string.IsNullOrWhiteSpace(reason) ? "${notice_disconnected_from_the_multiplayer_host}" : reason);
        if (!string.IsNullOrWhiteSpace(resultNotice))
        {
            pendingNativeNotice = resultNotice;
            nativeNoticeEarliest = Time.unscaledTime + 0.25f;
        }
        var game = SingletonMono<SS_ANNW_Game>.self;
        if (game != null && game.isActiveAndEnabled && game.gameObject.scene == SceneManager.GetActiveScene()) game.DoQuitOut();
        else NativeSkirmishLobby.TerminateFromRemote();
    }

    private static bool IsTerminalReconnectError(Exception? error) => error is not null &&
        error.Message.IndexOf("room or match is not resumable", StringComparison.OrdinalIgnoreCase) >= 0;

    private void ShowFastReconnectPopup()
    {
        var popup = UI_Floater.self?.pop_general;
        if (popup is null) return;
        var text = MpText.Render($"${{ui_reconnecting}}{fastReconnectAttempt}/3)...");
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
                label.text = MpText.Render("${ui_cancel}");
            }
        }
    }

    private void CancelFastReconnect()
    {
        if (!fastReconnectRunning) return;
        fastReconnectCancelRequested = true;
        Status = "Leaving the disconnected multiplayer session";
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
            catch (Exception ex) { Status = "${ui_map_preview_synchronization_failed}" + ex.Message; NativeSkirmishLobby.ShowMapError(Status); Logger.LogWarning(ex); return false; }
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
            seat.CommanderMode = player.keep_map_co ? 2 : selection <= 1 ? selection : 2;
            seat.KeepMapCommander = player.keep_map_co;
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
            _ = peer.SendAsync(new Envelope { Message = Messages.Reject(ProtocolCodec.EncodeString("Skirmish seat settings cannot be changed right now.")) });
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
        if (host.MatchId.HasValue && (executor.IsBusy || authorityAiOperationActive || turnAdvanceRunning || LocalAutoGuidePlanning)) return;
        while (host.TryDequeueSeatChange(out var change) && change is not null)
        {
            if (host.MatchId.HasValue)
                host.AppendAndBroadcast(AuthorityMessages.SeatChanged(ProtocolCodec.EncodeSeatControlChanged(change)));
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
        if (GS_Battle.self?.game_running != true) return false;
        var game = SingletonMono<SS_ANNW_Game>.self;
        if (game == null || game.ui == null) return false;
        var messages = game.ui.messages;
        if (messages == null) return false;
        messages.AddMessage("", MpText.Render(notice)); return true;
    }

    internal bool AllowNativeSkirmishStart(UI_MENU_LevelSelect_InfoSkm info, string mapId)
    {
        if (!NativeSkirmishLobby.Active) return true;
        if (client is not null)
        {
            if (CurrentRoom?.MatchStarted == true) JoinSelectedMatch();
            else Status = "Only the host can start the match";
            return false;
        }
        if (host is null) { Status = "Create a room first"; return false; }
        if (host.Room.SavedGame)
        {
            if (!host.Room.CanStart) { Status = "The host must select a seat and all seated players must be ready"; return false; }
            StartSavedGame(); return false;
        }
        if (!SyncLobbyDraft(info, mapId) || host.Room.MapId != mapId || host.Room.MapPreview.Length == 0)
        { Status = "The host has not synchronized the map preview"; return false; }
        if (!host.Room.CanStart) { Status = "Some human seats are unclaimed or not ready"; return false; }
        host.BroadcastMatchStarting(); hostStartAuthorized = true;
        NativeSkirmishLobby.MarkStartingMatch();
        return true;
    }

    private void StartSavedGame()
    {
        if (host is null || string.IsNullOrWhiteSpace(savedGamePath)) { Status = "No multiplayer save selected"; return; }
        host.BroadcastMatchStarting(); hostStartAuthorized = true; nativeHostBattleStarted = true; NativeSkirmishLobby.MarkStartingMatch();
        Status = "Loading multiplayer save";
        Singleton<BattleAndMapFileSystem>.self.DoLoadSkirmish(savedGamePath);
    }

    internal void OnNativeStartGameFormal()
    {
        if (host is null || !hostStartAuthorized) return;
        nativeHostBattleStarted = true;
        Status = "Starting the multiplayer battle";
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
            Status = $"Snapshot frame {loaded.FrameId} restored. Catching up";
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
        if (matchEndedNormally)
        {
            ResetBattleState();
            lobbyReturnPending = true;
            return;
        }
        Logger.LogInfo("Native battle left; closing multiplayer session.");
        if (host is not null && !hostShutdownPending)
        {
            hostShutdownPending = true;
            StartCoroutine(NotifyGuestsAndCloseHost());
            return;
        }
        Disconnect();
    }

    private void TryRestoreRoomLobby()
    {
        if (!lobbyReturnPending || CurrentRoom?.MatchStarted != false || SceneManager.GetActiveScene().name != "ANNW_Menu") return;
        var menu = SingletonMono<SS_ANNW_Menu>.self;
        if (menu == null || !menu.isActiveAndEnabled || menu.screen_skirmish == null ||
            menu.gameObject.scene != SceneManager.GetActiveScene()) return;
        client?.ReturnToLobby();
        NativeSkirmishLobby.ReturnToRoom(menu, CurrentRoom!);
        lobbyReturnPending = false; matchEndedNormally = false;
    }

    private IEnumerator NotifyGuestsAndCloseHost()
    {
        var closingHost = host;
        var task = closingHost?.BroadcastSessionEndedAsync("${notice_host_left}");
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
            var players = SS_ANNW_Game.start_game_setting?.players ?? throw new InvalidOperationException("Multiplayer skirmish is missing StartGameSetting.players.");
            host.BindRuntimePlayerIndices(players);
            var journalDirectory = Path.Combine(Paths.ConfigPath, "XingyiStarryMp", "journals");
            host.StartMatch(Path.Combine(journalDirectory, Guid.NewGuid().ToString("N") + ".xmpj"));
            ApplyRoomSeatModes(host.Room.Snapshot());
            GS_Battle.self.editor_enabled = false;
            var initialFrame = host.AppendAndBroadcast(AuthorityMessages.TurnPhase(ProtocolCodec.EncodeTurnPhase(TurnPhase.SeatTurnReady)));
            host.SetLatestSnapshot(GameStateSerializer.CaptureSnapshot(), initialFrame);
            host.BroadcastRoom();
            InputGate.MultiplayerActive = true;
            InputGate.LocalSeatMayAct = false;
            Status = "Host battle started; initial snapshot published";
        }
        catch (Exception ex)
        {
            Status = "Multiplayer battle initialization failed: " + ex.Message;
            Logger.LogError(ex);
        }
    }

    internal async void HostPublic(string roomName, string password, int port, int maxParticipants = 4)
    {
        Disconnect();
        var generation = connectionGeneration;
        CompositeHostTransport? transport = null;
        try
        {
            var name = NormalizeDisplayName(); var roomId = Guid.NewGuid();
            var registration = new RelayRegisterRoomRequest
            {
                RequestId = ++nextRequestId,
                RoomId = roomId,
                RoomName = string.IsNullOrWhiteSpace(roomName) ? MpText.Render(name + "${ui_room_name_suffix}") : roomName.Trim(),
                HostName = name,
                Password = password ?? "",
                PluginVersion = PluginVersion,
                GameFingerprint = gameFingerprint,
                ContentFingerprint = contentFingerprint,
                MaxParticipants = maxParticipants
            };
            Status = $"${{ui_listening_on_0_0_0_0}}{port}${{ui_and_connecting_to_the_public_relay}}";
            var created = await CreateHostTransportAsync(port, registration, generation);
            transport = created.Transport;
            host = new HostSession(transport, PluginVersion, gameFingerprint, contentFingerprint, name,
                password ?? "", roomId: roomId, maxParticipants: maxParticipants);
            transport = null;
            host.Start();
            Status = created.RelayAvailable ? "Room created. Join via the public relay or direct connection" : "Room created. Relay unavailable; direct connections only";
        }
        catch (Exception ex)
        {
            transport?.Dispose();
            if (generation != connectionGeneration) return;
            Disconnect(); ConnectionFailed = true; Status = "${ui_failed_to_create_room}" + ex.Message; Logger.LogError(ex);
        }
    }

    internal async void HostPublicFromSave(string roomName, string password, int port, string path, int maxParticipants = 4)
    {
        Disconnect(); var generation = connectionGeneration;
        CompositeHostTransport? transport = null;
        try
        {
            var save = Singleton<BattleAndMapFileSystem>.self.ReadFileWithMeta_Local(path) ?? throw new InvalidDataException("${notice_unable_to_read_the_skirmish_save}");
            if (!save.HasKey("startGameSetting")) throw new InvalidDataException("${notice_this_file_is_not_a_multiplayer_compatible_skirmish}");
            var settings = StartGameSetting.LoadOb(save.GetKey_Obj("startGameSetting"));
            var name = NormalizeDisplayName(); var roomId = Guid.NewGuid();
            var registration = new RelayRegisterRoomRequest
            {
                RequestId = ++nextRequestId,
                RoomId = roomId,
                RoomName = string.IsNullOrWhiteSpace(roomName) ? MpText.Render(name + "${ui_room_name_suffix}") : roomName.Trim(),
                HostName = name,
                Password = password ?? "",
                PluginVersion = PluginVersion,
                GameFingerprint = gameFingerprint,
                ContentFingerprint = contentFingerprint,
                MaxParticipants = maxParticipants
            };
            Status = $"${{ui_listening_on_0_0_0_0}}{port}${{ui_and_creating_a_room_from_a_save}}";
            var created = await CreateHostTransportAsync(port, registration, generation);
            transport = created.Transport;
            host = new HostSession(transport, PluginVersion, gameFingerprint, contentFingerprint, name,
                password ?? "", roomId: roomId, maxParticipants: maxParticipants);
            transport = null;
            host.ConfigureSavedGame(save, settings); host.Start(); savedGamePath = path;
            Status = created.RelayAvailable ? "Room created from save. Join via the public relay or direct connection" : "Room created from save. Relay unavailable; direct connections only";
        }
        catch (Exception ex)
        {
            transport?.Dispose();
            if (generation != connectionGeneration) return;
            Disconnect(); ConnectionFailed = true; Status = "${ui_failed_to_create_room_from_save}" + ex.Message; Logger.LogError(ex);
        }
    }

    private async Task<(CompositeHostTransport Transport, bool RelayAvailable)> CreateHostTransportAsync(
        int port, RelayRegisterRoomRequest registration, int generation)
    {
        var direct = new NetworkHost(port);
        RelayHostTransport? relay = null;
        try
        {
            direct.Start();
            try { relay = await RelayHostTransport.ConnectAsync(ConfiguredRelayEndpoint, registration); }
            catch (Exception ex) { Logger.LogWarning("Relay registration failed; continuing with direct TCP hosting: " + ex.Message); }
            if (generation != connectionGeneration) throw new OperationCanceledException("A newer connection attempt replaced this host operation.");
            return (relay is null ? new CompositeHostTransport(direct) : new CompositeHostTransport(direct, relay), relay is not null);
        }
        catch
        {
            relay?.Dispose(); direct.Dispose(); throw;
        }
    }

    internal async void Join(string address, int port, string password)
    {
        Disconnect();
        try
        {
            NormalizeDisplayName();
            client = new ClientSession(); Status = $"${{status_connecting}}{address}:{port}";
            await client.ConnectAsync(address, port, CreateHello(password));
            Status = $"${{ui_connected_to}}{address}:{port}${{ui_waiting_for_handshake}}";
        }
        catch (Exception ex) { Disconnect(); ConnectionFailed = true; Status = "${ui_connection_failed}" + ex.Message; Logger.LogError(ex); }
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
            Status = "${ui_joining_public_room}";
            await connectingClient.ConnectAsync(endpoint.Host, endpoint.Port, CreateHello(password));
            if (generation != connectionGeneration) { connectingClient.Dispose(); return; }
            Status = "${ui_connected_to_public_room_waiting_for_host_handshake}";
        }
        catch (Exception ex)
        {
            if (generation != connectionGeneration) return;
            Disconnect(); ConnectionFailed = true; Status = "${ui_failed_to_join_public_room}" + ex.Message; Logger.LogError(ex);
        }
    }

    internal Task<IReadOnlyList<RelayRoomInfo>> ListPublicRoomsAsync() =>
        RelayDirectoryClient.ListRoomsAsync(ConfiguredRelayEndpoint, ++nextRequestId);
    internal bool IsPublicRoomCompatible(RelayRoomInfo room) => room.PluginVersion == PluginVersion &&
        room.GameFingerprint == gameFingerprint && room.ContentFingerprint == contentFingerprint;

    private HelloMessage CreateHello(string password = "") => new HelloMessage
    {
        PluginVersion = PluginVersion,
        GameFingerprint = gameFingerprint,
        ContentFingerprint = contentFingerprint,
        DisplayName = NormalizeDisplayName(),
        Password = password ?? ""
    };

    private string NormalizeDisplayName()
    {
        var value = (displayName?.Value ?? Environment.UserName).Trim();
        if (value.Length == 0) throw new InvalidOperationException("${notice_enter_a_username}");
        if (value.Length > 32) throw new InvalidOperationException("${notice_username_cannot_exceed_32_characters}");
        if (displayName is not null) displayName.Value = value;
        return value;
    }

    internal void SubmitSimpleCommand(CommandKind kind)
    {
        if (client?.ClientId is not Guid clientId) { Status = "Client handshake has not completed"; return; }
        var request = new CommandRequest { ClientId = clientId, RequestId = ++nextRequestId, AppliedFrameId = client.AppliedFrameId, Command = new GameCommand { Kind = kind } };
        _ = client.SendCommandAsync(request);
    }

    internal void SendChat(ChatChannel channel, string text)
    {
        if (host is not null)
        {
            if (!host.SendLocalChat(channel, text, out var reason)) Status = reason;
            return;
        }
        if (client is not null) _ = client.SendChatAsync(channel, text);
    }

    internal void SendPing(int tileX, int tileY)
    {
        if (IsSpectator) { Status = "Spectators cannot send map pings"; return; }
        if (host is not null)
        {
            if (!host.SendLocalPing(tileX, tileY, out var reason)) Status = reason;
            return;
        }
        if (client is not null) _ = client.SendPingAsync(tileX, tileY);
    }

    internal void UpdateConfiguredPingKey(KeyCode key)
    {
        if (pingKey is not null && pingKey.Value != key) pingKey.Value = key;
    }

    private void PumpSocialEvents()
    {
        while (host?.TryDequeueChat(out var hostChat) == true && hostChat is not null) ShowChatEvent(hostChat);
        while (client?.TryDequeueChat(out var clientChat) == true && clientChat is not null) ShowChatEvent(clientChat);
        while (host?.TryDequeuePing(out var hostPing) == true && hostPing is not null) PingFeature.Show(hostPing);
        while (client?.TryDequeuePing(out var clientPing) == true && clientPing is not null) PingFeature.Show(clientPing);
    }

    private void ShowChatEvent(ChatEvent message)
    {
        var native = SingletonMono<SS_ANNW_Game>.self?.ui?.messages;
        if (GS_Battle.self?.game_running != true || native is null) return;
        const string white = "#FFFFFFFF"; const string self = "#87CEEBFF"; const string team = "#2BFF44FF"; const string enemy = "#FF514BFF";
        var color = white;
        if (message.Kind == ChatKind.Player)
        {
            if (message.SenderClientId == LocalIdentityId) color = self;
            else if (!message.SenderIsSpectator)
            {
                var localSeat = LocalIdentityId is Guid id ? CurrentRoom?.Seats.Find(value => value.ClientId == id) : null;
                color = localSeat is not null && message.SenderTeam >= 0 && localSeat.Team == message.SenderTeam ? team : enemy;
            }
        }
        var title = message.Kind == ChatKind.System ? MpText.Render("${ui_system}") : EscapeRichText(message.SenderName);
        native.AddMessage($"<color={color}>{title}</color>", $"<color={color}>{EscapeRichText(message.Kind == ChatKind.System ? MpText.Render(message.Text) : message.Text)}</color>");
    }

    private static string EscapeRichText(string value) => (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

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
        Status = seat is null ? "Synchronizing as a spectator" : "Taking over seat and synchronizing";
        _ = client.JoinMatchAsync(seat?.SeatId);
    }
    internal void ToggleReady()
    {
        if (IsMatchStarting) return;
        if (LocalIdentityId is not Guid id) return;
        var participant = CurrentRoom?.Participants.Find(value => value.ClientId == id);
        if (participant is null) return;
        if (host is not null) host.SetLocalReady(!participant.Ready);
        else if (client is not null) _ = client.SetReadyAsync(!participant.Ready);
    }

    internal void SubmitCommand(GameCommand command)
    {
        if (!CommandRules.MayRequest(command.Kind)) { Status = "${notice_command_not_allowed}"; return; }
        if (InputGate.MultiplayerActive && !InputGate.MaySubmit)
        {
            Status = "It is not your turn";
            return;
        }
        SubmitCommandCore(command);
    }

    private ulong? SubmitCommandCore(GameCommand command)
    {
        if (host is not null)
        {
            if (host.LocalHostSeatId is not Guid localSeatId) { Status = "Spectators cannot submit game commands"; return null; }
            var hostRequestId = ++nextRequestId;
            operations.Enqueue(new CommandRequest { ClientId = host.LocalHostClientId, RequestId = hostRequestId, SeatId = localSeatId, Round = GS_Battle.self?.turns ?? 0, AppliedFrameId = host.MatchId.HasValue ? 0 : -1, Command = command });
            return hostRequestId;
        }
        if (client?.ClientId is not Guid clientId) { Status = "Not connected to the authority host"; return null; }
        if (!MultiplayerUnitStates.TryEnterAwaitingAuthority(command, out var token)) { Status = "${ui_this_unit_is_waiting_for_the_host}"; ShowBattleMessage(Status); return null; }
        var seatId = client.Room?.Seats.Find(s => s.ClientId == clientId)?.SeatId ?? Guid.Empty;
        var requestId = ++nextRequestId;
        pendingClientCommands[requestId] = (command, token);
        _ = client.SendCommandAsync(new CommandRequest { ClientId = clientId, RequestId = requestId, SeatId = seatId, Round = GS_Battle.self?.turns ?? 0, AppliedFrameId = client.AppliedFrameId, Command = command });
        return requestId;
    }


    internal void NotifyNoEligibleUnits()
    {
        const string notice = "${notice_the_selected_units_cannot_perform_this_action_right}";
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
        if (accepted)
        {
            return;
        }
        CompleteLocalAutoGuideBatch(response.RequestId, false);
        MultiplayerUnitStates.RejectAwaitingAuthority(pending.Command, pending.Token);
        var notice = "${ui_action_not_performed}" + response.Reason;
        Logger.LogWarning($"Command rejected source=remote request={response.RequestId} kind={pending.Command.Kind} round={GS_Battle.self?.turns ?? 0} target={pending.Command.TargetX},{pending.Command.TargetY} reason={response.Reason}");
        if (!ShowBattleMessage(notice)) Status = notice;
    }

    internal void SubmitSurrender()
    {
        if (!InputGate.MultiplayerActive || GS_Battle.self is null) return;
        if (host is not null)
        {
            var seat = host.Room.Seats.FirstOrDefault(value => value.ClientId == host.LocalHostClientId);
            if (seat is null) { Status = "The host has not claimed a human seat"; return; }
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
        if (client?.ClientId is not Guid clientId) { Status = "Not connected to the authority host"; return; }
        var owned = client.Room?.Seats.Find(value => value.ClientId == clientId);
        if (owned is null) { Status = "You have not claimed a human seat"; return; }
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
            if (frame.FrameType == AuthorityFrameType.Resolution && !NativeActionPlayback.MayApply(ProtocolCodec.DecodeResolution(frame.Payload).Event!)) return;
            pendingAuthorityFrames.Dequeue();
            try { ApplyAuthorityFrame(frame); }
            catch (Exception ex) { AbortReplay("Authority frame execution failed: " + ex.Message); return; }
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
            AttackSettlements.BeginReplay();
            StartClientReplay();
        }
        else if (frame.FrameType == AuthorityFrameType.Resolution)
        {
            var resolution = ProtocolCodec.DecodeResolution(frame.Payload);
            if (replayBegin is null || resolution.OperationId != replayBegin.OperationId) throw new InvalidDataException("Settlement operation mismatch.");
            replayFrameIds.Add(frame.FrameId);
            AttackSettlements.Current!.Apply(resolution.Event!);
        }
        else if (frame.FrameType == AuthorityFrameType.OperationEnd)
        {
            var end = ProtocolCodec.DecodeOperationEnd(frame.Payload);
            if (replayBegin is null || end.OperationId != replayBegin.OperationId)
                throw new InvalidDataException("Operation end does not match the active replay.");
            NativeActionPlayback.CompleteOperation();
            replayEnd = end; replayFrameIds.Add(frame.FrameId); TryCommitReplay();
        }
        else if (frame.FrameType == AuthorityFrameType.OperationFailed)
        {
            var failed = ProtocolCodec.DecodeOperationFailed(frame.Payload);
            if (replayBegin is null || failed.OperationId != replayBegin.OperationId) { AbortReplay("Failure frame does not belong to the current operation"); return; }
            replayFrameIds.Add(frame.FrameId);
            Logger.LogWarning("Authority operation failed: " + failed.Reason);
            Status = "${notice_authority_operation_failed}";
            pendingMatchEnd = null;
            foreach (var frameId in replayFrameIds) client.MarkApplied(frameId);
            if (GS_Battle.self is not null) GS_Battle.self.unit_busy = false;
            CancelLocalAutoGuide();
            executor.Reset();
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
            var localVictory = pendingMatchEnd ?? ProtocolCodec.DecodeMatchEnded(frame.Payload);
            pendingMatchEnd = null;
            ApplyAuthorityMatchEnd(localVictory);
        }
        else client.MarkApplied(frame.FrameId);
    }

    private void StartClientReplay()
    {
        if (replayBegin is null || replayExecutionStarted) return;
        replayExecutionStarted = true;
        executor.Start(replayBegin.Command, ExecutionOrigin.ClientReplay,
            () => { replayExecutionCompleted = true; TryCommitReplay(); },
            ex => AbortReplay("Replay failed: " + ex));
    }

    private void PumpOperations()
    {
        if (host is null || !host.MatchId.HasValue || executor.IsBusy || authorityAiOperationActive || !operations.TryBegin(out var request) || request is null) return;
        if (request.Command.Kind == CommandKind.Move) NormalizeQueuedMove(request.Command);
        var authorized = request.Command.Kind == CommandKind.DebugAddResources || request.Command.Kind == CommandKind.DebugFillSkill;
        string? validation;
        if (!authorized && !host.Authorize(request, GS_Battle.self.current_co_index, GS_Battle.self.turns, out var authorizationError))
            validation = authorizationError;
        else validation = executor.Validate(request.Command);
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
                var notice = "${ui_action_not_performed}" + validation;
                if (!ShowBattleMessage(notice)) Status = notice;
            }
            if (request.ClientId == host.LocalHostClientId) CompleteLocalAutoGuideBatch(request.RequestId, false);
            operations.Complete(); return;
        }
        var operationId = Guid.NewGuid();
        Logger.LogDebug($"Authority operation begin source={(request.ClientId == host.LocalHostClientId ? "local" : "remote")} request={request.RequestId} kind={request.Command.Kind} op={operationId:N} round={request.Round} target={request.Command.TargetX},{request.Command.TargetY}");
        var beginFrame = host.AppendAndBroadcast(AuthorityMessages.OperationBegin(ProtocolCodec.EncodeOperationBegin(new OperationBeginPayload { OperationId = operationId, SeatId = request.SeatId, RequestId = request.RequestId, Round = request.Round, Command = request.Command })));
        if (requestPeers.TryGetValue(RequestKey(request), out var requestPeer)) { host.Accept(requestPeer, request.RequestId, beginFrame.FrameId); requestPeers.Remove(RequestKey(request)); }
        BeginHostAttackSettlements(operationId);
        executor.Start(request.Command, ExecutionOrigin.HostAuthority,
            () =>
            {
                AttackSettlements.Reset();
                var endFrame = host.AppendAndBroadcast(AuthorityMessages.OperationEnd(ProtocolCodec.EncodeOperationEnd(new OperationEndPayload { OperationId = operationId })));
                host.SetLatestSnapshot(GameStateSerializer.CaptureSnapshot(), endFrame);
                Logger.LogDebug($"Authority operation end kind={request.Command.Kind} op={operationId:N} frame={endFrame.FrameId}");
                operations.Complete();
                if (request.ClientId == host.LocalHostClientId) CompleteLocalAutoGuideBatch(request.RequestId, true);
                if (FlushPendingMatchEnd()) return;
                if (request.Command.Kind == CommandKind.EndTurn ||
                    request.Command.Kind == CommandKind.Surrender && request.Command.TargetX == GS_Battle.self.current_co_index)
                    GameController.self.StartCoroutine(RunHostTurnAdvance(), "XingyiStarryMpTurnAdvance");
            },
            ex =>
            {
                AttackSettlements.Reset(); Logger.LogError(ex);
                if (GS_Battle.self is not null)
                {
                    GS_Battle.self.unit_busy = false;
                    BattleEventBus.self?.TriggerUnitBusyChanged(isBusy: false);
                }
                host.AppendAndBroadcast(AuthorityMessages.OperationFailed(ProtocolCodec.EncodeOperationFailed(new OperationFailedPayload { OperationId = operationId, Reason = ex.Message })));
                if (request.ClientId == host.LocalHostClientId) CompleteLocalAutoGuideBatch(request.RequestId, false);
                operations.Complete();
            });
    }

    private static void NormalizeQueuedMove(GameCommand command)
    {
        using var localFlags = LocalUnitIntent.CanonicalFlags();
        if (GS_Battle.self?.all_unit is null) return;
        MoveCommandFilter.TryFilter(command, id =>
        {
            if (id < int.MinValue || id > int.MaxValue) return MoveUnitDecision.Reject;
            var unit = GS_Battle.self.all_unit.GetUnitByID((int)id);
            if (unit is null || unit.player != GS_Battle.self.cur_player) return MoveUnitDecision.Reject;
            var index = Array.IndexOf(command.UnitIds, id);
            if (command.UnitTargetXs.Length == command.UnitIds.Length && command.UnitTargetYs.Length == command.UnitIds.Length &&
                unit.pos == new Inctor2(command.UnitTargetXs[index], command.UnitTargetYs[index])) return MoveUnitDecision.Skip;
            return unit.moved || unit.in_animation || unit.building ? MoveUnitDecision.Skip : MoveUnitDecision.Include;
        });
    }

    private void BeginHostAttackSettlements(Guid operationId)
    {
        AttackSettlements.BeginRecording(value => host!.AppendAndBroadcast(AuthorityMessages.Resolution(ProtocolCodec.EncodeResolution(new ResolutionPayload { OperationId = operationId, Event = value }))));
    }

    internal IEnumerator RunHostAiCommand(GameCommand command, Action<Exception>? onFailure = null)
    {
        if (host is null || !host.MatchId.HasValue) yield break;
        while (executor.IsBusy || authorityAiOperationActive) yield return 0f;
        authorityAiOperationActive = true;
        try
        {
            var validation = executor.Validate(command);
            if (validation is not null)
            {
                Logger.LogError("Authority command rejected: " + validation);
                onFailure?.Invoke(new InvalidOperationException(validation)); yield break;
            }
            var operationId = Guid.NewGuid();
            Logger.LogDebug($"Authority AI operation begin kind={command.Kind} op={operationId:N} player={GS_Battle.self.current_co_index} round={GS_Battle.self.turns}");
            var seat = host.Room.Seats.FirstOrDefault(value => value.PlayerIndex == GS_Battle.self.current_co_index);
            host.AppendAndBroadcast(AuthorityMessages.OperationBegin(ProtocolCodec.EncodeOperationBegin(new OperationBeginPayload
            {
                OperationId = operationId,
                SeatId = seat?.SeatId ?? Guid.Empty,
                RequestId = ++nextRequestId,
                Round = GS_Battle.self.turns,
                Command = command
            })));
                BeginHostAttackSettlements(operationId);
            var finished = false; Exception? failure = null;
            executor.Start(command, ExecutionOrigin.HostAuthority, () => finished = true, ex => { failure = ex; finished = true; });
            while (!finished) yield return 0f;
            if (failure is not null)
            {
                Logger.LogError(failure);
                host.AppendAndBroadcast(AuthorityMessages.OperationFailed(ProtocolCodec.EncodeOperationFailed(
                    new OperationFailedPayload { OperationId = operationId, Reason = failure.Message })));
                onFailure?.Invoke(failure);
                yield break;
            }
            AttackSettlements.Reset();
            var end = host.AppendAndBroadcast(AuthorityMessages.OperationEnd(ProtocolCodec.EncodeOperationEnd(new OperationEndPayload { OperationId = operationId })));
            host.SetLatestSnapshot(GameStateSerializer.CaptureSnapshot(), end);
            Logger.LogDebug($"Authority AI operation end kind={command.Kind} op={operationId:N} frame={end.FrameId}");
            FlushPendingMatchEnd();
        }
        finally
        {
            AttackSettlements.Reset();
            authorityAiOperationActive = false;
        }
    }

    internal IEnumerator BeginLocalAutoGuide(IEnumerator native)
    {
        LocalAutoGuidePlanning = true;
        return RunLocalAutoGuide(native, ++plannerGeneration, GS_Battle.self);
    }

    private IEnumerator RunLocalAutoGuide(IEnumerator native, int generation, GS_Battle battle)
    {
        using var stack = new CoroutineStack(native);
        try
        {
            while (LocalAutoGuidePlanning && generation == plannerGeneration && battle == GS_Battle.self &&
                battle.game_running && GetLocalDisplayPlayer() == battle.cur_player && !fastReconnectRunning && loadingSnapshot is null)
            {
                bool more;
                try { more = stack.MoveNext(); }
                catch (Exception ex) { Logger.LogError("Local auto-guide planning failed: " + ex); yield break; }
                if (!more) break;
                yield return stack.Current;
            }
        }
        finally
        {
            if (generation == plannerGeneration) { LocalAutoGuidePlanning = false; localAutoGuideRequest = null; localAutoGuideResult = null; }
            if (battle == GS_Battle.self)
            {
                SingletonMono<SS_ANNW_Game>.self?.auto_guide?.Cleanup();
                battle.is_auto_guiding = false; battle.unit_busy = executor.IsBusy;
                BattleEventBus.self?.TriggerUnitBusyChanged(battle.unit_busy);
            }
        }
    }

    internal IEnumerator SubmitLocalAutoGuideBatch(List<UT_UnitAction> batch)
    {
        var generation = plannerGeneration;
        var battle = GS_Battle.self;
        battle.cur_player.ClearTempMove();
        if (!LocalAutoGuidePlanning || GetLocalDisplayPlayer() != battle.cur_player ||
            (client is not null && (client.IsCaughtUp != true || client.AppliedFrameId != client.VerifiedFrameId)))
        { battle.auto_guide_canceled = true; yield break; }
        localAutoGuideResult = null;
        localAutoGuideRequest = SubmitCommandCore(AuthoritativeAutoGuide.ResolveBatch(batch));
        if (!localAutoGuideRequest.HasValue) { battle.auto_guide_canceled = true; yield break; }
        while (generation == plannerGeneration && LocalAutoGuidePlanning && !localAutoGuideResult.HasValue)
            yield return 0f;
        if (generation != plannerGeneration || localAutoGuideResult != true) battle.auto_guide_canceled = true;
        if (generation == plannerGeneration) { localAutoGuideRequest = null; localAutoGuideResult = null; }
    }

    private void CompleteLocalAutoGuideBatch(ulong requestId, bool success)
    {
        if (localAutoGuideRequest == requestId) localAutoGuideResult = success;
    }

    private void CancelLocalAutoGuide()
    {
        plannerGeneration++; LocalAutoGuidePlanning = false;
        localAutoGuideRequest = null; localAutoGuideResult = null;
        if (GS_Battle.self is not null) GS_Battle.self.auto_guide_canceled = true;
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
            var changed = new SeatControlChanged { SeatId = seat.SeatId, PlayerIndex = next, AiControlled = false, Reason = "The player will take over this seat at the start of this turn." };
            host.AppendAndBroadcast(AuthorityMessages.SeatChanged(ProtocolCodec.EncodeSeatControlChanged(changed)));
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
        host.AppendAndBroadcast(AuthorityMessages.MatchEnded(ProtocolCodec.EncodeMatchEnded(victory)));
        host.ReturnToLobby();
        ApplyAuthorityMatchEnd(victory);
        return true;
    }

    private void ApplyAuthorityMatchEnd(bool victory)
    {
        if (SingletonMono<SS_ANNW_Game>.self is null) return;
        matchEndedNormally = true;
        CancelLocalAutoGuide(); LocalUnitIntent.BeginAuthority();
        InputGate.LocalSeatMayAct = false;
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
            AbortReplay("Operation end frame does not match the current replay"); return;
        }
        try { AttackSettlements.Current?.CheckComplete(); }
        catch (Exception ex) { AbortReplay("Random replay failed: " + ex.Message); return; }
        Logger.LogDebug($"Client replay committed operation {replayOperationId.Value:N}.");
        if (client is not null)
            foreach (var frameId in replayFrameIds) client.MarkApplied(frameId);
        if (replayBegin is not null && replayBegin.SeatId == client?.Room?.Seats.Find(value => value.ClientId == client.ClientId)?.SeatId)
            CompleteLocalAutoGuideBatch(replayBegin.RequestId, true);
        ResetReplayState();
    }

    private void AbortReplay(string reason)
    {
        Logger.LogWarning($"Replay aborted op={replayOperationId?.ToString("N") ?? "none"} kind={replayBegin?.Command.Kind.ToString() ?? "none"} frames={replayFrameIds.FirstOrDefault()}..{replayFrameIds.LastOrDefault()}: {reason}");
        Status = "${notice_replay_resynchronizing}";
        pendingMatchEnd = null;
        InputGate.LocalSeatMayAct = false;
        CancelLocalAutoGuide();
        pendingAuthorityFrames.Clear(); executor.Reset(); ResetReplayState();
        _ = client?.RequestSnapshotAsync();
    }

    internal void RequestManualResync()
    {
        if (!CanManualResync)
        {
            Status = client?.SnapshotRequested == true ? "Full synchronization is already in progress" : "Cannot resynchronize right now";
            return;
        }
        CancelLocalAutoGuide();
        Logger.LogWarning($"Manual full synchronization requested at verified={client!.VerifiedFrameId} applied={client.AppliedFrameId} replay={replayOperationId?.ToString("N") ?? "none"} executorBusy={executor.IsBusy}.");
        Status = "Full synchronization requested. Waiting for the host snapshot";
        InputGate.LocalSeatMayAct = false;
        pendingMatchEnd = null;
        pendingAuthorityFrames.Clear();
        ResetReplayState();
        _ = client.RequestSnapshotAsync();
    }

    private void ResetReplayState()
    {
        replayOperationId = null; replayBegin = null; replayExecutionStarted = false; replayExecutionCompleted = false;
        replayEnd = null; replayFrameIds.Clear();
        AttackSettlements.Reset();
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
            CancelLocalAutoGuide(); LocalUnitIntent.Reset();
            ResetReplayState(); pendingAuthorityFrames.Clear(); pendingClientCommands.Clear(); executor.Reset();
            client?.AcceptSnapshotAnchor(manifest); loadingSnapshot = manifest;
            snapshotPreviousBattle = GS_Battle.self;
            snapshotLoadGeneration++;
            InputGate.MultiplayerActive = true; InputGate.LocalSeatMayAct = false;
            Status = $"Snapshot {manifest.SnapshotId:N} verified. Rebuilding scene"; Logger.LogInfo($"Snapshot {manifest.SnapshotId:N} verified; rebuilding scene; generation={snapshotLoadGeneration}");
            GameSnapshotLoader.Load(snapshot);
        }
        catch (Exception ex) { Status = "${ui_snapshot_loading_failed}" + ex.Message; Logger.LogError(ex); ShowBattleMessage(Status); }
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
        var mayAct = InputGate.LocalSeatMayAct && !LocalAutoGuidePlanning && localPlayer is not null;
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
        if (host is not null || client is not null || SceneManager.GetActiveScene().name != "ANNW_Menu") return;
        var menu = SingletonMono<SS_ANNW_Menu>.self;
        if (menu == null || !menu.isActiveAndEnabled || !menu.gameObject.scene.IsValid() ||
            menu.gameObject.scene != SceneManager.GetActiveScene()) return;
        var floater = UI_Floater.self;
        if (floater == null || floater.pop_general == null) return;
        floater.pop_general.ShowAsSimple(MpText.Render(pendingNativeNotice));
        pendingNativeNotice = "";
    }

    internal void Disconnect()
    {
        ConnectionFailed = false;
        connectionGeneration++;
        pendingNativeNotice = ""; nativeNoticeEarliest = 0f;
        CancelLocalAutoGuide(); operations.Reset(); requestPeers.Clear();
        host?.Dispose(); host = null; client?.Dispose(); client = null;
        ResetBattleState();
        matchEndedNormally = false; lobbyReturnPending = false;
        savedGamePath = "";
        NativeSkirmishLobby.ForgetHostedMap();
        ChatWindow.Reset(); PingFeature.Reset(); SpectatorFeature.Reset();
        Status = "${status_disconnected}";
    }

    private void ResetBattleState()
    {
        CancelLocalAutoGuide(); LocalUnitIntent.Reset();
        operations.Reset(); requestPeers.Clear();
        pendingAuthorityFrames.Clear(); pendingClientCommands.Clear(); ResetReplayState(); executor.Reset();
        loadingSnapshot = null; snapshotPreviousBattle = null;
        hostStartAuthorized = false; nativeHostBattleStarted = false; hostBootstrapAttempted = false; hostShutdownPending = false; perspectivePlayerIndex = -1; previousLocalTurnOwned = false;
        pendingMatchEnd = null; applyingAuthorityMatchEnd = false; turnAdvanceRunning = false; authorityAiOperationActive = false;
        forcedDisconnectedEndTurnPending = false; forcedDisconnectedEndTurnRunning = false; fastReconnectRunning = false; fastReconnectCancelRequested = false;
        HideFastReconnectPopup();
        PingFeature.Reset(); SpectatorFeature.Reset();
        InputGate.MultiplayerActive = false; InputGate.LocalSeatMayAct = false;
    }

    private void OnDestroy()
    {
        Disconnect(); harmony?.UnpatchSelf(); harmony = null; Instance = null;
    }
}
