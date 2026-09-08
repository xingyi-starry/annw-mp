using System;
using System.IO;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using ANNW;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
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
    public const string PluginVersion = "0.3.9";

    private Harmony? harmony;
    private HostSession? host;
    private ClientSession? client;
    private ConfigEntry<int>? defaultPort;
    private ConfigEntry<string>? defaultAddress;
    private ConfigEntry<string>? displayName;
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
    private bool hostStartAuthorized;
    private bool nativeHostBattleStarted;
    private bool hostBootstrapAttempted;
    private bool hostShutdownPending;
    private string pendingNativeNotice = "";
    private string pendingLiveNotice = "";
    private float nativeNoticeEarliest;
    private int perspectivePlayerIndex = -1;
    private bool previousLocalTurnOwned;
    private readonly Dictionary<string, PeerConnection> requestPeers = new Dictionary<string, PeerConnection>(StringComparer.Ordinal);

    public static XingyiStarryMpPlugin? Instance { get; private set; }
    internal string Status { get; private set; } = "未连接";
    internal bool CanClaimSeat => CurrentRoom?.Seats.Exists(s => s.OriginallyHuman && (!s.Connected || s.ClientId == LocalIdentityId)) == true;
    internal bool CanSetReady => LocalIdentityId is Guid id && CurrentRoom?.Seats.Find(s => s.ClientId == id) is not null;
    internal bool IsHost => host is not null;
    internal bool IsClient => client is not null;
    internal int ConfiguredPort { get => defaultPort?.Value ?? ProtocolConstants.DefaultPort; set { if (defaultPort is not null) defaultPort.Value = value; } }
    internal string ConfiguredAddress { get => defaultAddress?.Value ?? "127.0.0.1"; set { if (defaultAddress is not null) defaultAddress.Value = value; } }
    internal string ConfiguredDisplayName { get => displayName?.Value ?? Environment.UserName; set { if (displayName is not null) displayName.Value = value; } }
    internal RoomSnapshot? CurrentRoom => host?.Room.Snapshot() ?? client?.Room;
    internal Guid? LocalClientId => client?.ClientId;
    internal Guid? LocalIdentityId => host?.LocalHostClientId ?? client?.ClientId;
    public bool IsAuthorityHostBattleActive => host?.MatchId.HasValue == true && GS_Battle.self?.game_running == true;

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
        operations.Enqueue(new CommandRequest { ClientId = host.LocalHostClientId, RequestId = ++nextRequestId,
            SeatId = host.LocalHostSeatId, Round = GS_Battle.self.turns, AppliedFrameId = 0, Command = command });
    }

    private void Awake()
    {
        Instance = this;
        defaultPort = Config.Bind("Network", "Port", ProtocolConstants.DefaultPort, "创建或加入房间使用的 TCP 端口。");
        defaultAddress = Config.Bind("Network", "Address", "127.0.0.1", "默认加入的局域网主机地址。");
        displayName = Config.Bind("Network", "DisplayName", Environment.UserName, "局域网房间内显示的名称。");
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
        host?.Pump(message => Logger.LogWarning(message), OnHostCommand, OnHostLobbyDraft, ShowParticipantNotice);
        client?.Pump(message => { Status = message; Logger.LogWarning(message); }, OnAuthorityFrame, OnSnapshotReceived, ShowParticipantNotice);
        PumpAuthorityFrames();
        if (client?.ConnectionLost == true)
        {
            var reason = client.ConnectionError; client.Dispose(); client = null;
            pendingAuthorityFrames.Clear(); ResetReplayState(); executor.Reset(); GameplayRandom.ActiveTape = null;
            InputGate.MultiplayerActive = false; InputGate.LocalSeatMayAct = false; Status = "联机会话已结束：" + reason;
            pendingNativeNotice = string.IsNullOrWhiteSpace(reason) ? "与联机主机的连接已中断。" : reason;
            nativeNoticeEarliest = Time.unscaledTime + 0.25f;
            if (GS_Battle.self?.game_running == true && SingletonMono<SS_ANNW_Game>.self is not null)
                SingletonMono<SS_ANNW_Game>.self.DoQuitOut();
            else NativeSkirmishLobby.TerminateFromRemote();
        }
        client?.Tick();
        NativeLobbyPanel.Tick(this);
        NativeSkirmishLobby.Tick(this);
        if (client?.ClientId is not null) Status = $"已握手，ClientId={client.ClientId:N}，已验证帧={client.VerifiedFrameId}";
        InputGate.LocalSeatMayAct = false;
        if (client?.IsCaughtUp == true && client.AppliedFrameId == client.VerifiedFrameId &&
            !executor.IsBusy && !replayOperationId.HasValue && client.ClientId is Guid localClient && GS_Battle.self is not null)
        {
            var seat = client.Room?.Seats.Find(value => value.ClientId == localClient);
            InputGate.LocalSeatMayAct = seat is not null && seat.PlayerIndex == GS_Battle.self.current_co_index && !seat.AiControlled;
        }
        if (host?.MatchId is not null && GS_Battle.self is not null)
        {
            var localSeat = host.Room.Seats.FirstOrDefault(value => value.ClientId == host.LocalHostClientId);
            InputGate.LocalSeatMayAct = localSeat is not null && localSeat.PlayerIndex == GS_Battle.self.current_co_index && !localSeat.AiControlled;
            if (!executor.IsBusy) ApplyRoomSeatModes(host.Room.Snapshot());
        }
        ApplyLocalPerspectiveAndControl();
        if (loadingSnapshot is not null && GS_Battle.self is not null && GS_Battle.self.game_running)
        {
            Status = $"快照帧 {loadingSnapshot.FrameId} 已恢复，正在追赶日志";
            InputGate.MultiplayerActive = true; InputGate.LocalSeatMayAct = false; _ = client?.RequestHistoryAsync();
            loadingSnapshot = null;
        }
        PumpOperations();
        TryShowLiveNotice();
        TryShowNativeNotice();
    }

    internal void SyncLobbyDraft(UI_MENU_LevelSelect_InfoSkm info, string mapId)
    {
        if ((host is null && client is null) || host?.MatchId.HasValue == true || info?.group is null || string.IsNullOrEmpty(mapId)) return;
        var players = info.group.GenerateData();
        var draft = CreateLobbyDraft(mapId, info, players);
        if (host is not null)
            host.UpdateLobbyDraft(draft, players);
        else if (client is not null)
            _ = client.SendLobbyDraftAsync(draft);
    }

    private static RoomSnapshot CreateLobbyDraft(string mapId, UI_MENU_LevelSelect_InfoSkm info, IReadOnlyList<SGS_Player> players)
    {
        var draft = new RoomSnapshot { MapId = mapId, MapTitle = info.txt_info_title?.text ?? mapId, FowType = info.dd_fow.value, WinCondition = info.dd_condition.value, QuickStart = info.dd_quickStart.value };
        for (var index = 0; index < players.Count; index++)
        {
            var player = players[index]; if (!player.exist) continue;
            var human = player.controller == PlayerControl.Human;
            var seat = new SeatInfo { LobbySlotIndex = index, OriginallyHuman = human, Controller = (int)player.controller, Team = (int)player.team,
                Color = (int)player.color, Position = player.pos_ind, PositionRandom = player.pos_random, ResourceMultiplier = player.res_percent,
                AiIntelligence = player.ai_interlligence };
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

    private void OnHostLobbyDraft(RoomSnapshot draft, PeerConnection peer)
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
        var popup = UI_Floater.self?.pop_general;
        if (popup is not null) popup.ShowAsSimple(notice);
        else pendingLiveNotice = notice;
    }

    private void TryShowLiveNotice()
    {
        if (pendingLiveNotice.Length == 0 || UI_Floater.self?.pop_general is not { } popup) return;
        var notice = pendingLiveNotice; pendingLiveNotice = ""; popup.ShowAsSimple(notice);
    }

    internal bool AllowNativeSkirmishStart(UI_MENU_LevelSelect_InfoSkm info, string mapId)
    {
        if (!NativeSkirmishLobby.Active) return true;
        if (client is not null) { Status = "只有主机可以开始对局"; return false; }
        if (host is null) { Status = "请先创建房间"; return false; }
        SyncLobbyDraft(info, mapId);
        if (!host.Room.CanStart) { Status = "尚有真人席位未认领或未准备"; return false; }
        hostStartAuthorized = true;
        NativeSkirmishLobby.MarkStartingMatch();
        return true;
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

    private HelloMessage CreateHello() => new HelloMessage
    {
        PluginVersion = PluginVersion, GameFingerprint = gameFingerprint, ContentFingerprint = contentFingerprint,
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
        if (host is not null)
        {
            if (!host.ClaimLocalSeat(lobbySlotIndex, NormalizeDisplayName(), out var reason)) Status = reason;
            return;
        }
        if (client is not null) _ = client.ClaimSeatAsync(lobbySlotIndex);
    }
    internal void ToggleReady()
    {
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
        var seatId = client.Room?.Seats.Find(s => s.ClientId == clientId)?.SeatId ?? Guid.Empty;
        _ = client.SendCommandAsync(new CommandRequest { ClientId = clientId, RequestId = ++nextRequestId, SeatId = seatId, Round = GS_Battle.self?.turns ?? 0, AppliedFrameId = client.AppliedFrameId, Command = command });
    }

    private void OnHostCommand(CommandRequest request, PeerConnection peer)
    {
        Logger.LogInfo($"Received command {request.Command.Kind} request={request.RequestId} from {request.ClientId}");
        var reason = "Host battle is not available.";
        if (host is null || GS_Battle.self is null || !host.Authorize(request, GS_Battle.self.current_co_index, GS_Battle.self.turns, out reason))
        {
            host?.Reject(peer, request.RequestId, reason); return;
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
        if (client is null) return;
        while (pendingAuthorityFrames.Count != 0)
        {
            var frame = pendingAuthorityFrames.Peek();
            if (frame.FrameType == AuthorityFrameType.OperationBegin && replayOperationId.HasValue) return;
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
            if (begin.Command.Kind == CommandKind.EquipmentMoveAction || begin.Command.Kind == CommandKind.BuildWithMove) StartClientEquipmentMovePrelude();
            else if (CanReplayAtBegin(begin.Command)) StartClientReplay(Array.Empty<RandomRecord>());
        }
        else if (frame.FrameType == AuthorityFrameType.Resolution)
        {
            var resolution = ProtocolCodec.DecodeResolution(frame.Payload);
            if (replayBegin is null || resolution.OperationId != replayBegin.OperationId) { AbortReplay("结算帧不属于当前操作"); return; }
            replayFrameIds.Add(frame.FrameId);
            if (replayBegin.Command.Kind == CommandKind.EquipmentMoveAction || replayBegin.Command.Kind == CommandKind.BuildWithMove)
                StartClientEquipmentMoveResolution(resolution.RandomRecords);
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
            foreach (var frameId in replayFrameIds) client.MarkApplied(frameId);
            if (GS_Battle.self is not null) GS_Battle.self.unit_busy = false;
            ResetReplayState();
        }
        else client.MarkApplied(frame.FrameId);
    }

    private static bool CanReplayAtBegin(GameCommand command) =>
        command.Kind == CommandKind.Move || command.Kind == CommandKind.UndoMove || command.Kind == CommandKind.AutoGuideCancel ||
        command.Kind == CommandKind.DebugAddResources || command.Kind == CommandKind.DebugFillSkill;

    private void StartClientReplay(IEnumerable<RandomRecord> records)
    {
        if (replayBegin is null || replayExecutionStarted) return;
        replayExecutionStarted = true;
        replayRandomTape.BeginReplay(records); GameplayRandom.ActiveTape = replayRandomTape;
        executor.Start(replayBegin.Command, ExecutionOrigin.ClientReplay,
            () => { try { replayRandomTape.EndReplay(); replayExecutionCompleted = true; } catch (Exception ex) { AbortReplay("随机回放失败：" + ex.Message); } finally { GameplayRandom.ActiveTape = null; } TryCommitReplay(); },
            ex => { GameplayRandom.ActiveTape = null; AbortReplay("回放失败：" + ex.Message); });
    }

    private void StartClientEquipmentMovePrelude()
    {
        if (replayBegin is null || replayPreludeStarted) return;
        replayPreludeStarted = true;
        replayRandomTape.BeginReplay(Array.Empty<RandomRecord>()); GameplayRandom.ActiveTape = replayRandomTape;
        executor.StartEquipmentMovePrelude(replayBegin.Command, ExecutionOrigin.ClientReplay,
            () => { try { replayRandomTape.EndReplay(); replayPreludeCompleted = true; } catch (Exception ex) { AbortReplay("移动前段回放失败：" + ex.Message); } finally { GameplayRandom.ActiveTape = null; } },
            ex => { GameplayRandom.ActiveTape = null; AbortReplay("移动前段回放失败：" + ex.Message); });
    }

    private void StartClientEquipmentMoveResolution(IEnumerable<RandomRecord> records)
    {
        if (replayBegin is null || replayExecutionStarted || !replayPreludeCompleted) return;
        replayExecutionStarted = true;
        replayRandomTape.BeginReplay(records); GameplayRandom.ActiveTape = replayRandomTape;
        var start = replayBegin.Command.Kind == CommandKind.BuildWithMove
            ? new Action<GameCommand, ExecutionOrigin, Action, Action<Exception>>(executor.StartBuildMoveResolution)
            : executor.StartEquipmentMoveResolution;
        start(replayBegin.Command, ExecutionOrigin.ClientReplay,
            () => { try { replayRandomTape.EndReplay(); replayExecutionCompleted = true; } catch (Exception ex) { AbortReplay("随机回放失败：" + ex.Message); } finally { GameplayRandom.ActiveTape = null; } TryCommitReplay(); },
            ex => { GameplayRandom.ActiveTape = null; AbortReplay("移动后动作回放失败：" + ex.Message); });
    }

    private void PumpOperations()
    {
        if (host is null || !host.MatchId.HasValue || !operations.TryBegin(out var request) || request is null) return;
        var validation = executor.Validate(request.Command);
        if (validation is not null)
        {
            Logger.LogWarning("Command rejected: " + validation);
            if (requestPeers.TryGetValue(RequestKey(request), out var rejectedPeer))
            {
                host.Reject(rejectedPeer, request.RequestId, validation);
                requestPeers.Remove(RequestKey(request));
            }
            operations.Complete(); return;
        }
        var operationId = Guid.NewGuid();
        var beginFrame = host.AppendAndBroadcast(AuthorityFrameType.OperationBegin, ProtocolCodec.EncodeOperationBegin(new OperationBeginPayload { OperationId = operationId, SeatId = request.SeatId, RequestId = request.RequestId, Round = request.Round, Command = request.Command }));
        if (requestPeers.TryGetValue(RequestKey(request), out var requestPeer)) { host.Accept(requestPeer, request.RequestId, beginFrame.FrameId); requestPeers.Remove(RequestKey(request)); }
        hostRandomTape.BeginRecording(); GameplayRandom.ActiveTape = hostRandomTape;
        executor.Start(request.Command, ExecutionOrigin.HostAuthority,
            () => {
                var records = hostRandomTape.EndRecording(); GameplayRandom.ActiveTape = null;
                var resolution = new ResolutionPayload { OperationId = operationId, StageId = 0, SettlementOrdinal = 0 };
                foreach (var record in records) resolution.RandomRecords.Add(record);
                host.AppendAndBroadcast(AuthorityFrameType.Resolution, ProtocolCodec.EncodeResolution(resolution));
                var endFrame = host.AppendAndBroadcast(AuthorityFrameType.OperationEnd, ProtocolCodec.EncodeOperationEnd(new OperationEndPayload { OperationId = operationId }));
                host.SetLatestSnapshot(GameStateSerializer.CaptureSnapshot(), endFrame);
                operations.Complete();
            },
            ex => {
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
    private static string RequestKey(CommandRequest request) => request.ClientId.ToString("N") + ":" + request.RequestId;

    private void TryCommitReplay()
    {
        if (!replayOperationId.HasValue || !replayExecutionCompleted || replayEnd is null) return;
        if (replayEnd.OperationId != replayOperationId.Value)
        {
            AbortReplay("操作结束帧与当前回放不匹配"); return;
        }
        Logger.LogInfo($"Client replay committed operation {replayOperationId.Value:N}.");
        if (client is not null)
            foreach (var frameId in replayFrameIds) client.MarkApplied(frameId);
        ResetReplayState();
    }

    private void AbortReplay(string reason)
    {
        Logger.LogWarning(reason);
        Status = reason + "，正在请求完整同步";
        InputGate.LocalSeatMayAct = false;
        pendingAuthorityFrames.Clear(); ResetReplayState();
        _ = client?.RequestSnapshotAsync();
    }

    private void ResetReplayState()
    {
        replayOperationId = null; replayBegin = null; replayExecutionStarted = false; replayExecutionCompleted = false;
        replayPreludeStarted = false; replayPreludeCompleted = false; replayEnd = null; replayFrameIds.Clear();
    }

    private void OnSnapshotReceived(SnapshotManifest manifest, byte[] snapshot)
    {
        try
        {
            ResetReplayState(); pendingAuthorityFrames.Clear();
            client?.AcceptSnapshotAnchor(manifest); loadingSnapshot = manifest;
            InputGate.MultiplayerActive = true; InputGate.LocalSeatMayAct = false;
            Status = $"已校验快照 {manifest.SnapshotId:N}，正在重建场景"; Logger.LogInfo(Status);
            GameSnapshotLoader.Load(snapshot);
        }
        catch (Exception ex) { loadingSnapshot = null; Status = "快照加载失败：" + ex.Message; Logger.LogError(ex); }
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
        if (host is null || !host.MatchId.HasValue) return;
        if (host.Room.ActivatePendingClaim(playerIndex))
        {
            ApplyRoomSeatModes(host.Room.Snapshot()); host.BroadcastRoom();
            Logger.LogInfo("Returned seat control at turn boundary: player " + playerIndex);
        }
    }

    internal Player? GetLocalDisplayPlayer()
    {
        if (!InputGate.MultiplayerActive || GS_Battle.self?.all_player?.players is null || LocalIdentityId is not Guid identity) return null;
        var seat = CurrentRoom?.Seats.Find(value => value.ClientId == identity);
        if (seat is null || seat.PlayerIndex < 0 || seat.PlayerIndex >= GS_Battle.self.all_player.players.Count) return null;
        return GS_Battle.self.all_player.players[seat.PlayerIndex];
    }

    private void ApplyLocalPerspectiveAndControl()
    {
        if (!InputGate.MultiplayerActive || GS_Battle.self is null) return;
        var localPlayer = GetLocalDisplayPlayer();
        var mayAct = InputGate.LocalSeatMayAct && localPlayer is not null;
        var localTurnOwned = localPlayer is not null && GS_Battle.self.cur_player == localPlayer &&
            CurrentRoom?.Seats.Find(value => value.PlayerIndex == localPlayer.index)?.AiControlled != true;
        if (previousLocalTurnOwned && !localTurnOwned && UX_Manager.self is not null)
            UX_Manager.self.ClearUnitSelection();
        previousLocalTurnOwned = localTurnOwned;
        GS_Battle.self.is_player_in_control = mayAct;
        var mouse = SingletonMono<SS_ANNW_Game>.self?.mouse_input;
        if (mouse is not null && !mayAct) mouse.free_mode = true;
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
        host?.Dispose(); host = null; client?.Dispose(); client = null;
        pendingAuthorityFrames.Clear(); ResetReplayState(); executor.Reset(); GameplayRandom.ActiveTape = null;
        hostStartAuthorized = false; nativeHostBattleStarted = false; hostBootstrapAttempted = false; hostShutdownPending = false; perspectivePlayerIndex = -1; previousLocalTurnOwned = false;
        pendingLiveNotice = "";
        InputGate.MultiplayerActive = false; InputGate.LocalSeatMayAct = false; Status = "未连接";
    }

    private void OnDestroy()
    {
        Disconnect(); harmony?.UnpatchSelf(); harmony = null; Instance = null;
    }
}
