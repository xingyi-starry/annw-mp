using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Globalization;
using XingyiStarry.Mp.Infrastructure;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Session;

internal sealed class HostSession : IDisposable
{
    private readonly IHostTransport network;
    private readonly Dictionary<Guid, ClientRecord> clientsByConnection = new Dictionary<Guid, ClientRecord>();
    private readonly Dictionary<Guid, ClientRecord> clientsById = new Dictionary<Guid, ClientRecord>();
    private readonly Queue<SeatControlChanged> pendingSeatChanges = new Queue<SeatControlChanged>();
    private readonly Queue<ChatEvent> localChats = new Queue<ChatEvent>();
    private readonly Queue<PingEvent> localPings = new Queue<PingEvent>();
    private readonly HostSocialState social = new HostSocialState();
    private readonly string pluginVersion;
    private readonly string gameFingerprint;
    private readonly string contentFingerprint;
    private AuthorityJournal? journal;
    private SnapshotManifest? latestSnapshotManifest;
    private byte[]? latestCompressedSnapshot;

    public RoomState Room { get; }
    public Guid? MatchId { get; private set; }
    public Guid LocalHostClientId { get; }
    public bool ConnectionLost { get; private set; }
    public string ConnectionError { get; private set; } = "";
    public IReadOnlyList<ChatEvent> ChatHistory => social.ChatsFor(LocalHostClientId);

    public HostSession(int port, string pluginVersion, string gameFingerprint, string contentFingerprint, string hostName, int maxParticipants = 4)
        : this(new NetworkHost(port), pluginVersion, gameFingerprint, contentFingerprint, hostName, null, null, maxParticipants)
    {
    }

    public HostSession(IHostTransport network, string pluginVersion, string gameFingerprint, string contentFingerprint, string hostName,
        Guid? localHostClientId = null, Guid? roomId = null, int maxParticipants = 4)
    {
        this.pluginVersion = pluginVersion; this.gameFingerprint = gameFingerprint; this.contentFingerprint = contentFingerprint;
        LocalHostClientId = localHostClientId ?? Guid.NewGuid();
        this.network = network;
        Room = new RoomState(roomId, maxParticipants);
        Room.ReplaceSeats(Array.Empty<SeatInfo>());
        Room.AddHost(LocalHostClientId, hostName);
    }

    public void Start() => network.Start();

    public void Pump(Action<SessionLogLevel, string> log, Action<CommandRequest, IRemotePeer> commandReceived,
        Action<RoomSnapshot, IRemotePeer> lobbyDraftReceived, Action<string> participantLeft)
    {
        while (network.TryDequeueError(out var error))
        {
            log(SessionLogLevel.Warning, "Network host: " + error);
            if (UsesRelay) { ConnectionLost = true; ConnectionError = error?.Message ?? "中继连接已关闭"; }
        }
        while (network.TryDequeue(out var inbound) && inbound is not null)
        {
            try { Handle(inbound, commandReceived, lobbyDraftReceived, participantLeft); }
            catch (Exception ex) { log(SessionLogLevel.Warning, "Rejected packet: " + ex.Message); }
        }
        var now = DateTime.UtcNow;
        foreach (var client in clientsByConnection.Values.Distinct().ToArray())
        {
            var connectionClosed = client.Peer == null || !client.Peer.IsConnected;
            if (!client.Finalized && !client.Reconnecting && connectionClosed)
            {
                if (client.JoinedMatch && MatchId.HasValue)
                {
                    client.Reconnecting = true; client.ReconnectDeadlineUtc = now.AddSeconds(15);
                    Room.MarkReconnecting(client.ClientId, true); BroadcastRoom();
                    PublishSystem(SystemEventKind.Disconnected, client.DisplayName + " 连接中断，正在等待重连。");
                    log(SessionLogLevel.Info, "Client reconnect grace started: " + client.DisplayName);
                }
                else FinalizeClient(client, "退出了房间。", participantLeft, message => log(SessionLogLevel.Info, message));
            }
            if (!client.Finalized && client.Reconnecting && now >= client.ReconnectDeadlineUtc)
                FinalizeClient(client, "重连超时，席位已由 AI 接管。", participantLeft, message => log(SessionLogLevel.Info, message));
        }
    }

    private void Handle(InboundEnvelope inbound, Action<CommandRequest, IRemotePeer> commandReceived,
        Action<RoomSnapshot, IRemotePeer> lobbyDraftReceived, Action<string> participantLeft)
    {
        if (inbound.Envelope.Type == MessageType.ResumeSession)
        {
            HandleResume(inbound.Peer, ProtocolCodec.DecodeResumeSession(inbound.Envelope.Payload)); return;
        }
        if (inbound.Envelope.Type == MessageType.Hello)
        {
            HandleHello(inbound.Peer, ProtocolCodec.DecodeHello(inbound.Envelope.Payload), participantLeft); return;
        }
        if (!clientsByConnection.TryGetValue(inbound.Peer.ConnectionId, out var client)) throw new InvalidDataException("Handshake required.");
        if (client.Finalized || client.Reconnecting) throw new InvalidDataException("Session must be resumed before sending game messages.");
        switch (inbound.Envelope.Type)
        {
            case MessageType.Heartbeat: _ = network.SendAsync(inbound.Peer, MessageType.Heartbeat, ProtocolCodec.EncodeInt64(DateTime.UtcNow.Ticks)); break;
            case MessageType.ClaimSeat:
            if (!Room.TryClaimSeat(client.ClientId, client.DisplayName, checked((int)ProtocolCodec.DecodeInt64(inbound.Envelope.Payload)), out _, out var claimReason))
                {
                    _ = network.SendAsync(inbound.Peer, MessageType.Reject, ProtocolCodec.EncodeString(claimReason)); return;
                }
                BroadcastRoom(); break;
            case MessageType.ReleaseSeat:
                if (client.JoinedMatch) throw new InvalidDataException("Active players cannot release their seat.");
                if (Room.ReleaseSeat(client.ClientId)) BroadcastRoom();
                break;
            case MessageType.JoinMatchRequest:
                if (!Room.MatchStarted || !MatchId.HasValue) throw new InvalidDataException("Match has not started.");
                var join = ProtocolCodec.DecodeJoinMatchRequest(inbound.Envelope.Payload);
                if (!Room.AdmitToMatch(client.ClientId, join.SeatId, out var spectator, out var joinReason))
                { _ = network.SendAsync(inbound.Peer, MessageType.Reject, ProtocolCodec.EncodeString(joinReason)); return; }
                client.JoinedMatch = true;
                _ = network.SendAsync(inbound.Peer, MessageType.JoinMatchAccepted,
                    ProtocolCodec.EncodeJoinMatchAccepted(new JoinMatchAccepted { SeatId = join.SeatId, Spectator = spectator }));
                var admittedSeat = join.SeatId.HasValue ? Room.Seats.FirstOrDefault(value => value.SeatId == join.SeatId.Value) : null;
                PublishSystem(spectator ? SystemEventKind.EnteredSpectator : SystemEventKind.EnteredPlayer,
                    client.DisplayName + "加入了房间，" + (spectator ? "观战者。" : $"位置{(admittedSeat?.LobbySlotIndex ?? -1) + 1}。"));
                BroadcastRoom();
                _ = SendSnapshotAsync(inbound.Peer);
                break;
            case MessageType.LeaveSession:
                FinalizeClient(client, "退出了房间。", participantLeft, _ => { });
                inbound.Peer.Close();
                break;
            case MessageType.SetReady: Room.SetReady(client.ClientId, ProtocolCodec.DecodeInt64(inbound.Envelope.Payload) != 0); BroadcastRoom(); break;
            case MessageType.LobbyDraftChange:
                if (MatchId.HasValue || Room.MatchStarted) throw new InvalidDataException("Cannot modify the lobby after match start.");
                lobbyDraftReceived(ProtocolCodec.DecodeRoom(inbound.Envelope.Payload), inbound.Peer); break;
            case MessageType.CommandRequest:
                if (!client.JoinedMatch) throw new InvalidDataException("Client has not joined the active match.");
                var request = ProtocolCodec.DecodeCommandRequest(inbound.Envelope.Payload);
                request.ClientId = client.ClientId;
                commandReceived(request, inbound.Peer); break;
            case MessageType.HistoryRequest:
                if (!client.JoinedMatch) throw new InvalidDataException("Client has not joined the active match.");
                SendHistory(inbound.Peer, ProtocolCodec.DecodeInt64(inbound.Envelope.Payload)); break;
            case MessageType.SnapshotRequest:
                if (!client.JoinedMatch) throw new InvalidDataException("Client has not joined the active match.");
                _ = SendSnapshotAsync(inbound.Peer); break;
            case MessageType.ChatSend:
                PublishChat(client.ClientId, ProtocolCodec.DecodeChatSend(inbound.Envelope.Payload)); break;
            case MessageType.PingSend:
                var ping = ProtocolCodec.DecodePingSend(inbound.Envelope.Payload);
                if (!GameTileData.IsValid(new Inctor2(ping.TileX, ping.TileY))) throw new InvalidDataException("标点不在有效地图格上。");
                PublishPing(client.ClientId, ping); break;
            case MessageType.SocialSyncRequest:
                _ = SendSocialSyncAsync(client, ProtocolCodec.DecodeSocialSyncRequest(inbound.Envelope.Payload)); break;
            default: throw new InvalidDataException("Message is not valid in this host state.");
        }
    }

    private void HandleHello(IRemotePeer peer, HelloMessage hello, Action<string> participantNotice)
    {
        if (hello.ProtocolVersion != ProtocolConstants.Version || hello.PluginVersion != pluginVersion || hello.GameFingerprint != gameFingerprint || hello.ContentFingerprint != contentFingerprint)
        {
            _ = RejectHandshakeAsync(peer, "Version or content fingerprint mismatch."); return;
        }
        var cleanName = (hello.DisplayName ?? "").Trim();
        if (cleanName.Length == 0 || cleanName.Length > 32) { _ = RejectHandshakeAsync(peer, "用户名长度必须为 1–32 个字符。"); return; }
        var clientId = UsesRelay ? peer.ConnectionId : Guid.NewGuid();
        if (!Room.TryAddParticipant(clientId, cleanName, out var participantReason))
        { _ = RejectHandshakeAsync(peer, participantReason); return; }
        var record = new ClientRecord { ClientId = clientId, DisplayName = cleanName, Peer = peer };
        clientsByConnection[peer.ConnectionId] = record;
        clientsById[record.ClientId] = record;
        var mode = MatchId.HasValue ? WelcomeMode.JoinSelection : WelcomeMode.Lobby;
        _ = network.SendAsync(peer, MessageType.Welcome, ProtocolCodec.EncodeWelcome(new WelcomeMessage { ClientId = record.ClientId, RoomId = Room.RoomId, MatchId = MatchId, LatestFrameId = journal?.Frames.Count ?? 0, Mode = mode }));
        var room = Room.Snapshot(); room.MatchId = MatchId;
        _ = network.SendAsync(peer, MessageType.RoomState, ProtocolCodec.EncodeRoom(room));
        var notice = cleanName + "加入了房间。";
        PublishSystem(SystemEventKind.Joined, notice);
        participantNotice(notice);
        BroadcastRoom();
    }

    private void HandleResume(IRemotePeer peer, ResumeSessionRequest request)
    {
        if (!MatchId.HasValue || request.RoomId != Room.RoomId || request.MatchId != MatchId.Value ||
            !clientsById.TryGetValue(request.ClientId, out var record) || !record.Reconnecting || record.Finalized ||
            DateTime.UtcNow >= record.ReconnectDeadlineUtc)
        {
            _ = network.SendAsync(peer, MessageType.ResumeSessionRejected, ProtocolCodec.EncodeString("快速重连身份不存在或已经过期。")); return;
        }
        if (record.Peer is not null) clientsByConnection.Remove(record.Peer.ConnectionId);
        record.Peer = peer; record.Reconnecting = false;
        Room.MarkReconnecting(record.ClientId, false);
        clientsByConnection[peer.ConnectionId] = record;
        _ = network.SendAsync(peer, MessageType.ResumeSessionAccepted, ProtocolCodec.EncodeResumeSessionAccepted(new ResumeSessionAccepted
            { RoomId = Room.RoomId, MatchId = MatchId.Value, ClientId = record.ClientId, LatestFrameId = journal?.Frames.Count ?? 0 }));
        var room = Room.Snapshot(); room.MatchId = MatchId;
        _ = network.SendAsync(peer, MessageType.RoomState, ProtocolCodec.EncodeRoom(room));
        _ = SendSnapshotAsync(peer);
        PublishSystem(SystemEventKind.Reconnected, record.DisplayName + " 已重新连接。");
        BroadcastRoom();
    }

    private void FinalizeClient(ClientRecord client, string suffix, Action<string> participantLeft, Action<string> log)
    {
        if (client.Finalized) return;
        client.Finalized = true; client.Reconnecting = false;
        var seat = Room.Seats.FirstOrDefault(value => value.ClientId == client.ClientId && value.Connected);
        if (seat is not null)
        {
            pendingSeatChanges.Enqueue(new SeatControlChanged { SeatId = seat.SeatId, PlayerIndex = seat.PlayerIndex, AiControlled = true, Reason = suffix });
            Room.FinalizeDisconnected(client.ClientId);
        }
        Room.RemoveParticipant(client.ClientId);
        clientsById.Remove(client.ClientId);
        foreach (var key in clientsByConnection.Where(value => ReferenceEquals(value.Value, client)).Select(value => value.Key).ToArray()) clientsByConnection.Remove(key);
        var notice = client.DisplayName + suffix;
        PublishSystem(seat is null ? SystemEventKind.Left : SystemEventKind.AiTakeover, notice);
        participantLeft(notice); log(notice);
    }

    public bool TryDequeueSeatChange(out SeatControlChanged? change)
    {
        if (pendingSeatChanges.Count != 0) { change = pendingSeatChanges.Dequeue(); return true; }
        change = null; return false;
    }

    public bool TryDequeueChat(out ChatEvent? message)
    {
        if (localChats.Count != 0) { message = localChats.Dequeue(); return true; }
        message = null; return false;
    }
    public bool TryDequeuePing(out PingEvent? message)
    {
        if (localPings.Count != 0) { message = localPings.Dequeue(); return true; }
        message = null; return false;
    }

    public bool SendLocalChat(ChatChannel channel, string text, out string reason)
    {
        try { PublishChat(LocalHostClientId, new ChatSend { Channel = channel, Text = text }); reason = ""; return true; }
        catch (Exception ex) { reason = ex.Message; return false; }
    }
    public bool SendLocalPing(int tileX, int tileY, out string reason)
    {
        try
        {
            if (!GameTileData.IsValid(new Inctor2(tileX, tileY))) throw new InvalidDataException("标点不在有效地图格上。");
            PublishPing(LocalHostClientId, new PingSend { TileX = tileX, TileY = tileY }); reason = ""; return true;
        }
        catch (Exception ex) { reason = ex.Message; return false; }
    }

    private async Task RejectHandshakeAsync(IRemotePeer peer, string reason)
    {
        try { await network.SendAsync(peer, MessageType.Reject, ProtocolCodec.EncodeString(reason)).ConfigureAwait(false); }
        finally { peer.Close(); }
    }

    public void StartMatch(string journalPath)
    {
        Room.Start(); MatchId = Guid.NewGuid(); journal = new AuthorityJournal(MatchId.Value, journalPath);
        foreach (var client in clientsById.Values)
        {
            var participant = Room.FindParticipant(client.ClientId);
            client.JoinedMatch = participant?.Admission == ParticipantAdmission.Player || participant?.Admission == ParticipantAdmission.Spectator;
        }
    }

    public Guid? LocalHostSeatId => Room.Seats.FirstOrDefault(value => value.ClientId == LocalHostClientId)?.SeatId;

    public void UpdateLobbyDraft(RoomSnapshot draft, IReadOnlyList<SGS_Player> players)
    {
        var values = new List<SeatInfo>();
        for (var slotIndex = 0; slotIndex < players.Count; slotIndex++)
        {
            var player = players[slotIndex];
            if (!player.exist) continue;
            var human = player.controller == PlayerControl.Human;
            values.Add(CreateSeat(player, slotIndex, human, draft.Seats.Find(value => value.LobbySlotIndex == slotIndex)));
        }
        var fingerprint = new StringBuilder().Append(draft.MapId).Append('|').Append(draft.FowType).Append('|').Append(draft.WinCondition).Append('|').Append(draft.QuickStart).Append('|').Append(draft.Difficulty);
        for (var index = 0; index < players.Count; index++)
        {
            var player = players[index]; var intent = draft.Seats.Find(value => value.LobbySlotIndex == index);
            fingerprint.Append('|').Append(player.exist).Append(',').Append((int)player.controller).Append(',').Append((int)player.team).Append(',').Append((int)player.color)
                .Append(',').Append(player.pos_ind).Append(',').Append(player.pos_random).Append(',').Append(player.res_percent.ToString("R", CultureInfo.InvariantCulture))
                .Append(',').Append(player.ai_interlligence.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(intent?.CommanderMode ?? 1)
                .Append(',').Append(intent?.CommanderId ?? "").Append(',').Append(intent?.SkillId ?? "");
            if (intent is not null) foreach (var passive in intent.PassiveIds) fingerprint.Append(',').Append(passive);
        }
        if (Room.SyncDraft(draft.MapId, draft.MapTitle, draft.FowType, draft.WinCondition, draft.QuickStart, draft.Difficulty,
            draft.UserMap, draft.MapPreview, fingerprint.ToString(), values)) BroadcastRoom();
    }

    public void ConfigureSavedGame(DynOb save, StartGameSetting settings)
    {
        if (settings.players is null || !save.HasKey("commander")) throw new InvalidDataException("存档缺少遭遇战席位信息。");
        var commander = save.GetKey_Obj("commander");
        var playerObjects = commander.GetKey_List(commander.HasKey("player_list") ? "player_list" : "co_list")
            .OfType<DynOb>().Where(value => value.GetKey_Enum("fraction", Fraction.TEAM1) != Fraction.NEUTRAL).ToList();
        if (playerObjects.Count == 0) throw new InvalidDataException("存档中没有可用玩家。");
        var seats = new List<SeatInfo>();
        for (var playerIndex = 0; playerIndex < playerObjects.Count; playerIndex++)
        {
            var runtime = playerObjects[playerIndex];
            var setting = settings.players.FirstOrDefault(value => value.exist && value.pos_ind == playerIndex) ??
                          settings.players.Where(value => value.exist).ElementAtOrDefault(playerIndex) ?? new SGS_Player { exist = true, pos_ind = playerIndex };
            var lobbySlotIndex = settings.players.IndexOf(setting);
            if (lobbySlotIndex < 0) lobbySlotIndex = playerIndex;
            var seat = new SeatInfo
            {
                SeatId = Guid.NewGuid(), LobbySlotIndex = lobbySlotIndex, PlayerIndex = playerIndex,
                DisplayName = "空闲席位", OriginallyHuman = !runtime.GetKey_Bool("ai"), Connected = false, Ready = false,
                AiControlled = true, Defeated = runtime.GetKey_Bool("wipe_out"), Controller = (int)setting.controller,
                Team = (int)setting.team, Color = (int)setting.color, Position = setting.pos_ind, PositionRandom = setting.pos_random,
                ResourceMultiplier = setting.res_percent, AiIntelligence = setting.ai_interlligence, CommanderId = setting.sd_co ?? "",
                CommanderMode = string.IsNullOrEmpty(setting.sd_co) ? 1 : 2, SkillId = setting.skill?.name ?? ""
            };
            if (setting.ps_list is not null) foreach (var passive in setting.ps_list) if (passive is not null) seat.PassiveIds.Add(passive.name);
            seats.Add(seat);
        }
        var map = save.GetKey_String("map_name"); if (string.IsNullOrWhiteSpace(map)) map = settings.filename ?? "已保存的遭遇战";
        Room.ConfigureSavedGame(map, map, (int)settings.fow_type, (int)settings.win_condition, (int)settings.quick_start, settings.diff, seats);
        BroadcastRoom();
    }

    public void BindRuntimePlayerIndices(IReadOnlyList<SGS_Player> players) => Room.BindRuntimePlayerIndices(players);

    public void RefreshRuntimeSeats(IReadOnlyList<Player> players)
    {
        var changed = false;
        foreach (var seat in Room.Seats)
        {
            if (seat.PlayerIndex < 0 || seat.PlayerIndex >= players.Count) continue;
            var defeated = players[seat.PlayerIndex].defeated;
            if (seat.Defeated == defeated) continue;
            Room.SetDefeated(seat.PlayerIndex, defeated); changed = true;
        }
        if (changed) BroadcastRoom();
    }

    public bool ClaimLocalSeat(int lobbySlotIndex, string displayName, out string reason)
    {
        var result = Room.TryClaimSeat(LocalHostClientId, displayName, lobbySlotIndex, out _, out reason);
        if (result) BroadcastRoom();
        return result;
    }

    public void SetLocalReady(bool ready) { Room.SetReady(LocalHostClientId, ready); BroadcastRoom(); }

    private static SeatInfo CreateSeat(SGS_Player player, int slotIndex, bool human, SeatInfo? intent)
    {
        var result = new SeatInfo
        {
            SeatId = Guid.NewGuid(), LobbySlotIndex = slotIndex, PlayerIndex = -1,
            DisplayName = human ? "空闲真人席位" : "原版 AI", OriginallyHuman = human, Connected = false, Ready = !human, AiControlled = !human,
            Controller = (int)player.controller, Team = (int)player.team, Color = (int)player.color, Position = player.pos_ind, PositionRandom = player.pos_random,
            ResourceMultiplier = player.res_percent, AiIntelligence = player.ai_interlligence, CommanderId = intent?.CommanderId ?? "",
            CommanderMode = intent?.CommanderMode ?? 1, SkillId = intent?.SkillId ?? ""
        };
        if (intent is not null) foreach (var passive in intent.PassiveIds) result.PassiveIds.Add(passive);
        return result;
    }

    public AuthorityFrame AppendAndBroadcast(AuthorityFrameType type, byte[] payload)
    {
        if (journal is null) throw new InvalidOperationException("Match has not started.");
        var frame = journal.Append(type, payload);
        _ = BroadcastToJoinedAsync(MessageType.AuthorityFrame, ProtocolCodec.EncodeAuthorityFrame(frame));
        return frame;
    }

    private Task BroadcastToJoinedAsync(MessageType type, byte[] payload)
    {
        var sends = clientsById.Values
            .Where(value => value.JoinedMatch && !value.Finalized && value.Peer is not null && value.Peer.IsConnected)
            .Select(value => network.SendAsync(value.Peer!, type, payload))
            .ToArray();
        return sends.Length == 0 ? Task.CompletedTask : Task.WhenAll(sends);
    }

    public void BroadcastRoom()
    {
        var snapshot = Room.Snapshot(); snapshot.MatchId = MatchId;
        _ = network.BroadcastAsync(MessageType.RoomState, ProtocolCodec.EncodeRoom(snapshot));
        if (network is RelayHostTransport relay)
        {
            _ = relay.UpdateRoomAsync(new RelayUpdateRoomRequest { RequestId = 0, RoomId = Room.RoomId,
                MapTitle = snapshot.MapTitle, ConnectedPlayers = snapshot.Participants.Count(value => value.Connected),
                HumanSeats = snapshot.Seats.Count(value => value.OriginallyHuman),
                Status = snapshot.MatchStarted ? RelayRoomStatus.Playing : RelayRoomStatus.Waiting,
                AvailableSeats = Room.AvailableSeatCount, MatchId = MatchId, MaxParticipants = Room.MaxParticipants });
        }
    }

    private void PublishChat(Guid clientId, ChatSend request)
    {
        var message = social.CreatePlayerChat(Room, clientId, request, out var audience);
        SendChatToAudience(message, audience);
    }

    private void PublishPing(Guid clientId, PingSend request)
    {
        var message = social.CreatePing(Room, clientId, request, out var audience);
        SendPingToAudience(message, audience);
    }

    private void PublishSystem(SystemEventKind kind, string text)
    {
        var message = social.CreateSystem(Room, kind, text, out var audience);
        SendChatToAudience(message, audience);
    }

    private void SendChatToAudience(ChatEvent message, IReadOnlyCollection<Guid> audience)
    {
        if (audience.Contains(LocalHostClientId)) localChats.Enqueue(message);
        var payload = ProtocolCodec.EncodeChatEvent(message);
        foreach (var target in clientsById.Values.Where(value => audience.Contains(value.ClientId) && !value.Finalized && value.Peer?.IsConnected == true))
            _ = network.SendAsync(target.Peer!, MessageType.ChatEvent, payload);
    }

    private void SendPingToAudience(PingEvent message, IReadOnlyCollection<Guid> audience)
    {
        if (audience.Contains(LocalHostClientId)) localPings.Enqueue(message);
        var payload = ProtocolCodec.EncodePingEvent(message);
        foreach (var target in clientsById.Values.Where(value => audience.Contains(value.ClientId) && !value.Finalized && value.Peer?.IsConnected == true))
            _ = network.SendAsync(target.Peer!, MessageType.PingEvent, payload);
    }

    private async Task SendSocialSyncAsync(ClientRecord client, ulong requestId)
    {
        var peer = client.Peer; if (peer is null) return;
        var messages = social.ChatsFor(client.ClientId); var chunkCount = (uint)((messages.Count + 63) / 64);
        var begin = new SocialSyncBegin { RequestId = requestId, Watermark = social.Watermark, ChunkCount = chunkCount };
        foreach (var participant in Room.Snapshot().Participants) begin.Participants.Add(participant);
        await network.SendAsync(peer, MessageType.SocialSyncBegin, ProtocolCodec.EncodeSocialSyncBegin(begin)).ConfigureAwait(false);
        for (var index = 0; index < chunkCount; index++)
        {
            var chunk = new SocialSyncChunk { RequestId = requestId, ChunkIndex = (uint)index };
            foreach (var message in messages.Skip(index * 64).Take(64)) chunk.Messages.Add(message);
            await network.SendAsync(peer, MessageType.SocialSyncChunk, ProtocolCodec.EncodeSocialSyncChunk(chunk)).ConfigureAwait(false);
        }
        await network.SendAsync(peer, MessageType.SocialSyncComplete,
            ProtocolCodec.EncodeSocialSyncComplete(new SocialSyncComplete { RequestId = requestId, Watermark = begin.Watermark })).ConfigureAwait(false);
    }
    public void BroadcastMatchStarting() =>
        _ = network.BroadcastAsync(MessageType.MatchStarting, Array.Empty<byte>());
    public bool UsesRelay => network is RelayHostTransport;
    public Task CloseRelayJoiningAsync(ulong requestId) => network is RelayHostTransport relay ? relay.CloseJoiningAsync(requestId) : Task.CompletedTask;
    public Task BroadcastSessionEndedAsync(string reason) =>
        network.BroadcastAsync(MessageType.SessionEnded, ProtocolCodec.EncodeString(reason));
    public void Accept(IRemotePeer peer, ulong requestId, long frameId) => _ = network.SendAsync(peer, MessageType.CommandAccepted, ProtocolCodec.EncodeCommandResponse(new CommandResponse { RequestId = requestId, AuthorityFrameId = frameId }));
    public void Reject(IRemotePeer peer, ulong requestId, string reason) => _ = network.SendAsync(peer, MessageType.CommandRejected, ProtocolCodec.EncodeCommandResponse(new CommandResponse { RequestId = requestId, AuthorityFrameId = 0, Reason = reason }));

    public bool Authorize(CommandRequest request, int currentPlayerIndex, int currentRound, out string reason)
    {
        if (request.Command.Kind == CommandKind.DebugAddResources || request.Command.Kind == CommandKind.DebugFillSkill)
        { reason = "Debug commands can only originate on the authority host."; return false; }
        if (!MatchId.HasValue || !Room.MatchStarted) { reason = "Match has not started."; return false; }
        var seat = Room.Seats.FirstOrDefault(value => value.SeatId == request.SeatId);
        if (seat is null || !seat.Connected || seat.ClientId != request.ClientId) { reason = "Client does not own the requested seat."; return false; }
        if (seat.AiControlled) { reason = "Seat is currently controlled by AI."; return false; }
        if (request.Command.Kind == CommandKind.Surrender) { reason = ""; return true; }
        if (seat.PlayerIndex != currentPlayerIndex) { reason = "It is not this seat's turn."; return false; }
        if (request.Round != currentRound) { reason = "Request belongs to another round."; return false; }
        reason = ""; return true;
    }

    public void SetLatestSnapshot(byte[] snapshot, AuthorityFrame frame)
    {
        var compressed = SnapshotCodec.Compress(snapshot);
        latestCompressedSnapshot = compressed;
        latestSnapshotManifest = new SnapshotManifest
        {
            SnapshotId = Guid.NewGuid(), MatchId = frame.MatchId, FrameId = frame.FrameId, FrameHash = (byte[])frame.Hash.Clone(),
            CompressedLength = compressed.Length, ChunkCount = compressed.Length == 0 ? 0 : (compressed.Length + ProtocolConstants.SnapshotChunkBytes - 1) / ProtocolConstants.SnapshotChunkBytes,
            ContentHash = SnapshotCodec.Hash(compressed)
        };
    }

    private async Task SendSnapshotAsync(IRemotePeer peer)
    {
        var manifest = latestSnapshotManifest; var compressed = latestCompressedSnapshot;
        if (manifest is null || compressed is null) { await network.SendAsync(peer, MessageType.Reject, ProtocolCodec.EncodeString("No safe-boundary snapshot is available.")).ConfigureAwait(false); return; }
        await network.SendAsync(peer, MessageType.SnapshotManifest, ProtocolCodec.EncodeSnapshotManifest(manifest)).ConfigureAwait(false);
        for (var index = 0; index < manifest.ChunkCount; index++)
        {
            var offset = index * ProtocolConstants.SnapshotChunkBytes; var length = Math.Min(ProtocolConstants.SnapshotChunkBytes, compressed.Length - offset);
            var data = new byte[length]; Buffer.BlockCopy(compressed, offset, data, 0, length);
            await network.SendAsync(peer, MessageType.SnapshotChunk, ProtocolCodec.EncodeSnapshotChunk(new SnapshotChunk { SnapshotId = manifest.SnapshotId, Index = index, Data = data })).ConfigureAwait(false);
        }
        await network.SendAsync(peer, MessageType.SnapshotComplete, ProtocolCodec.EncodeGuid(manifest.SnapshotId)).ConfigureAwait(false);
    }

    private void SendHistory(IRemotePeer peer, long after)
    {
        if (journal is null) return;
        var frames = journal.After(after).ToArray();
        var latestFrameId = frames.Length == 0 ? after : frames[frames.Length - 1].FrameId;
        _ = SendHistoryAsync(peer, frames, latestFrameId);
    }

    private async Task SendHistoryAsync(IRemotePeer peer, IReadOnlyList<AuthorityFrame> frames, long latestFrameId)
    {
        foreach (var frame in frames) await network.SendAsync(peer, MessageType.AuthorityFrame, ProtocolCodec.EncodeAuthorityFrame(frame)).ConfigureAwait(false);
        await network.SendAsync(peer, MessageType.HistoryComplete, ProtocolCodec.EncodeInt64(latestFrameId)).ConfigureAwait(false);
    }

    public void Dispose() { journal?.Dispose(); network.Dispose(); }

    private sealed class ClientRecord
    {
        public Guid ClientId { get; set; }
        public string DisplayName { get; set; } = "";
        public bool Reconnecting { get; set; }
        public DateTime ReconnectDeadlineUtc { get; set; }
        public bool Finalized { get; set; }
        public bool JoinedMatch { get; set; }
        public IRemotePeer? Peer { get; set; }
    }
}
