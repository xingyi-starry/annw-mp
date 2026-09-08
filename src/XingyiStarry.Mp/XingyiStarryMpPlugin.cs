using System;
using System.IO;
using System.Collections.Generic;
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
    public const string PluginVersion = "0.3.0";

    private Harmony? harmony;
    private HostSession? host;
    private ClientSession? client;
    private LobbyOverlay? overlay;
    private ConfigEntry<int>? defaultPort;
    private ConfigEntry<string>? defaultAddress;
    private ConfigEntry<string>? displayName;
    private string gameFingerprint = "";
    private string contentFingerprint = "";
    private ulong nextRequestId;
    private readonly OperationQueue operations = new OperationQueue();
    private readonly GameCommandExecutor executor = new GameCommandExecutor();
    private Guid? replayOperationId;
    private byte[]? replayComputedHash;
    private OperationEndPayload? replayEnd;
    private readonly RandomTape hostRandomTape = new RandomTape();
    private readonly RandomTape replayRandomTape = new RandomTape();
    private OperationBeginPayload? replayBegin;
    private SnapshotManifest? loadingSnapshot;
    private readonly Dictionary<string, PeerConnection> requestPeers = new Dictionary<string, PeerConnection>(StringComparer.Ordinal);

    internal static XingyiStarryMpPlugin? Instance { get; private set; }
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

    private void Awake()
    {
        Instance = this;
        defaultPort = Config.Bind("Network", "Port", ProtocolConstants.DefaultPort, "创建或加入房间使用的 TCP 端口。");
        defaultAddress = Config.Bind("Network", "Address", "127.0.0.1", "默认加入的局域网主机地址。");
        displayName = Config.Bind("Network", "DisplayName", Environment.UserName, "局域网房间内显示的名称。");
        overlay = new LobbyOverlay(this);
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
        host?.Pump(message => Logger.LogWarning(message), OnHostCommand);
        client?.Pump(message => { Status = message; Logger.LogWarning(message); }, OnAuthorityFrame, OnSnapshotReceived);
        client?.Tick();
        NativeLobbyPanel.Tick(this);
        NativeSkirmishLobby.Tick(this);
        if (client?.ClientId is not null) Status = $"已握手，ClientId={client.ClientId:N}，已验证帧={client.VerifiedFrameId}";
        if (host is not null && host.MatchId is null && host.Room.CanStart && GS_Battle.self is not null && GS_Battle.self.game_running)
        {
            var journalDirectory = Path.Combine(Paths.ConfigPath, "XingyiStarryMp", "journals");
            if (SS_ANNW_Game.start_game_setting?.players is not null) host.BindRuntimePlayerIndices(SS_ANNW_Game.start_game_setting.players);
            host.StartMatch(Path.Combine(journalDirectory, Guid.NewGuid().ToString("N") + ".xmpj"));
            ApplyRoomSeatModes(host.Room.Snapshot());
            var initialStateHash = GameStateSerializer.ComputeStateHash();
            var initialFrame = host.AppendAndBroadcast(AuthorityFrameType.TurnPhase, ProtocolCodec.EncodeInt64((long)TurnPhase.SeatTurnReady));
            host.SetLatestSnapshot(GameStateSerializer.CaptureSnapshot(), initialStateHash, initialFrame);
            host.BroadcastRoom();
            InputGate.MultiplayerActive = true; InputGate.LocalSeatMayAct = true;
            Status = "主机权威对局已启动";
        }
        if (client?.IsCaughtUp == true && client.ClientId is Guid localClient && GS_Battle.self is not null)
        {
            var seat = client.Room?.Seats.Find(value => value.ClientId == localClient);
            InputGate.LocalSeatMayAct = seat is not null && seat.PlayerIndex == GS_Battle.self.current_co_index && !seat.AiControlled;
        }
        if (host?.MatchId is not null && GS_Battle.self is not null)
        {
            var localSeat = host.Room.Seats.First(value => value.ClientId == host.LocalHostClientId);
            InputGate.LocalSeatMayAct = localSeat.PlayerIndex == GS_Battle.self.current_co_index && !localSeat.AiControlled;
            if (!executor.IsBusy) ApplyRoomSeatModes(host.Room.Snapshot());
        }
        if (loadingSnapshot is not null && GS_Battle.self is not null && GS_Battle.self.game_running)
        {
            var actual = GameStateSerializer.ComputeStateHash();
            if (!AuthorityHashChain.FixedEquals(actual, loadingSnapshot.StateHash))
            {
                Status = "快照加载后的状态摘要不一致"; InputGate.MultiplayerActive = true; InputGate.LocalSeatMayAct = false;
            }
            else
            {
                Status = $"快照帧 {loadingSnapshot.FrameId} 已恢复，正在追赶日志";
                InputGate.MultiplayerActive = true; InputGate.LocalSeatMayAct = false; _ = client?.RequestHistoryAsync();
            }
            loadingSnapshot = null;
        }
        PumpOperations();
        if (Input.GetKeyDown(KeyCode.F8)) ToggleLobby();
    }

    private void OnGUI() => overlay?.Draw();
    internal void ToggleLobby() { if (overlay is not null) overlay.Visible = !overlay.Visible; }

    internal void SyncLobbyDraft(UI_MENU_LevelSelect_InfoSkm info, string mapId)
    {
        if (host is null || host.MatchId.HasValue || info?.group is null || string.IsNullOrEmpty(mapId)) return;
        host.UpdateLobbyDraft(mapId, info.txt_info_title?.text ?? mapId, info.dd_fow.value, info.dd_condition.value, info.dd_quickStart.value,
            info.group.GenerateData(), displayName?.Value ?? Environment.UserName);
    }

    internal bool AllowNativeSkirmishStart(UI_MENU_LevelSelect_InfoSkm info, string mapId)
    {
        if (!NativeSkirmishLobby.Active) return true;
        if (client is not null) { Status = "只有主机可以开始对局"; return false; }
        if (host is null) { Status = "请先创建房间"; return false; }
        SyncLobbyDraft(info, mapId);
        if (!host.Room.CanStart) { Status = "尚有真人席位未认领或未准备"; return false; }
        return true;
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
        if (client is null) return;
        if (frame.FrameType == AuthorityFrameType.OperationBegin)
        {
            var begin = ProtocolCodec.DecodeOperationBegin(frame.Payload);
            if (replayOperationId.HasValue) { Status = "同步暂停：收到重叠操作"; return; }
            replayOperationId = begin.OperationId; replayComputedHash = null; replayEnd = null;
            replayBegin = begin;
        }
        else if (frame.FrameType == AuthorityFrameType.Resolution)
        {
            var resolution = ProtocolCodec.DecodeResolution(frame.Payload);
            if (replayBegin is null || resolution.OperationId != replayBegin.OperationId) { Status = "同步暂停：结算帧不属于当前操作"; return; }
            replayRandomTape.BeginReplay(resolution.RandomRecords); GameplayRandom.ActiveTape = replayRandomTape;
            executor.Start(replayBegin.Command, ExecutionOrigin.ClientReplay,
                hash => { try { replayRandomTape.EndReplay(); replayComputedHash = hash; } catch (Exception ex) { Status = "随机回放失败：" + ex.Message; } finally { GameplayRandom.ActiveTape = null; } TryCommitReplay(); },
                ex => { GameplayRandom.ActiveTape = null; Status = "回放失败：" + ex.Message; });
        }
        else if (frame.FrameType == AuthorityFrameType.OperationEnd)
        {
            replayEnd = ProtocolCodec.DecodeOperationEnd(frame.Payload); TryCommitReplay();
        }
        client.MarkApplied(frame.FrameId);
    }

    private void PumpOperations()
    {
        if (host is null || !host.MatchId.HasValue || !operations.TryBegin(out var request) || request is null) return;
        var validation = executor.Validate(request.Command);
        if (validation is not null) { Logger.LogWarning("Command rejected: " + validation); operations.Complete(); return; }
        var operationId = Guid.NewGuid();
        var beginFrame = host.AppendAndBroadcast(AuthorityFrameType.OperationBegin, ProtocolCodec.EncodeOperationBegin(new OperationBeginPayload { OperationId = operationId, SeatId = request.SeatId, RequestId = request.RequestId, Round = request.Round, Command = request.Command }));
        if (requestPeers.TryGetValue(RequestKey(request), out var requestPeer)) { host.Accept(requestPeer, request.RequestId, beginFrame.FrameId); requestPeers.Remove(RequestKey(request)); }
        hostRandomTape.BeginRecording(); GameplayRandom.ActiveTape = hostRandomTape;
        executor.Start(request.Command, ExecutionOrigin.HostAuthority,
            stateHash => {
                var records = hostRandomTape.EndRecording(); GameplayRandom.ActiveTape = null;
                var resolution = new ResolutionPayload { OperationId = operationId, StageId = 0, SettlementOrdinal = 0 };
                foreach (var record in records) resolution.RandomRecords.Add(record);
                host.AppendAndBroadcast(AuthorityFrameType.Resolution, ProtocolCodec.EncodeResolution(resolution));
                var endFrame = host.AppendAndBroadcast(AuthorityFrameType.OperationEnd, ProtocolCodec.EncodeOperationEnd(new OperationEndPayload { OperationId = operationId, StateHash = stateHash }));
                host.SetLatestSnapshot(GameStateSerializer.CaptureSnapshot(), stateHash, endFrame); operations.Complete(); },
            ex => { GameplayRandom.ActiveTape = null; Logger.LogError(ex); operations.Complete(); });
    }
    private static string RequestKey(CommandRequest request) => request.ClientId.ToString("N") + ":" + request.RequestId;

    private void TryCommitReplay()
    {
        if (!replayOperationId.HasValue || replayComputedHash is null || replayEnd is null) return;
        if (replayEnd.OperationId != replayOperationId.Value || !AuthorityHashChain.FixedEquals(replayComputedHash, replayEnd.StateHash))
        {
            Status = "状态摘要不一致，正在请求完整同步"; InputGate.LocalSeatMayAct = false; _ = client?.RequestSnapshotAsync(); return;
        }
        replayOperationId = null; replayBegin = null; replayComputedHash = null; replayEnd = null;
    }

    private void OnSnapshotReceived(SnapshotManifest manifest, byte[] snapshot)
    {
        try
        {
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

    internal void Disconnect()
    {
        host?.Dispose(); host = null; client?.Dispose(); client = null;
        InputGate.MultiplayerActive = false; InputGate.LocalSeatMayAct = false; Status = "未连接";
    }

    private void OnDestroy()
    {
        Disconnect(); harmony?.UnpatchSelf(); harmony = null; Instance = null;
    }
}
