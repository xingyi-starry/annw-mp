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
    private readonly NetworkHost network;
    private readonly Dictionary<Guid, ClientRecord> clientsByConnection = new Dictionary<Guid, ClientRecord>();
    private readonly string pluginVersion;
    private readonly string gameFingerprint;
    private readonly string contentFingerprint;
    private AuthorityJournal? journal;
    private SnapshotManifest? latestSnapshotManifest;
    private byte[]? latestCompressedSnapshot;

    public RoomState Room { get; } = new RoomState();
    public Guid? MatchId { get; private set; }
    public Guid LocalHostClientId { get; }

    public HostSession(int port, string pluginVersion, string gameFingerprint, string contentFingerprint, string hostName)
    {
        this.pluginVersion = pluginVersion; this.gameFingerprint = gameFingerprint; this.contentFingerprint = contentFingerprint;
        LocalHostClientId = Guid.NewGuid();
        network = new NetworkHost(port);
        Room.ReplaceSeats(Array.Empty<SeatInfo>());
    }

    public void Start() => network.Start();

    public void Pump(Action<string> log, Action<CommandRequest, PeerConnection> commandReceived,
        Action<RoomSnapshot, PeerConnection> lobbyDraftReceived, Action<string> participantLeft)
    {
        while (network.TryDequeueError(out var error)) log("Network host: " + error);
        while (network.TryDequeue(out var inbound) && inbound is not null)
        {
            try { Handle(inbound, commandReceived, lobbyDraftReceived); }
            catch (Exception ex) { log("Rejected packet: " + ex.Message); }
        }
        var now = DateTime.UtcNow;
        foreach (var pair in clientsByConnection)
        {
            var client = pair.Value;
            var connectionClosed = client.Peer == null || !client.Peer.IsConnected;
            if (!client.Disconnected && (connectionClosed || now - client.LastSeenUtc > TimeSpan.FromSeconds(10)))
            {
                client.Disconnected = true;
                if (Room.MarkDisconnected(client.ClientId)) BroadcastRoom();
                var notice = client.DisplayName + " 已退出联机。";
                _ = network.BroadcastAsync(MessageType.ParticipantNotice, ProtocolCodec.EncodeString(notice));
                participantLeft(notice);
                log((connectionClosed ? "Client disconnected: " : "Client timed out: ") + client.DisplayName);
            }
        }
    }

    private void Handle(InboundEnvelope inbound, Action<CommandRequest, PeerConnection> commandReceived,
        Action<RoomSnapshot, PeerConnection> lobbyDraftReceived)
    {
        if (inbound.Envelope.Type == MessageType.Hello)
        {
            HandleHello(inbound.Peer, ProtocolCodec.DecodeHello(inbound.Envelope.Payload)); return;
        }
        if (!clientsByConnection.TryGetValue(inbound.Peer.ConnectionId, out var client)) throw new InvalidDataException("Handshake required.");
        client.LastSeenUtc = DateTime.UtcNow;
        switch (inbound.Envelope.Type)
        {
            case MessageType.Heartbeat: _ = network.SendAsync(inbound.Peer, MessageType.Heartbeat, ProtocolCodec.EncodeInt64(DateTime.UtcNow.Ticks)); break;
            case MessageType.ClaimSeat:
                if (!Room.TryClaimHumanSeat(client.ClientId, client.DisplayName, checked((int)ProtocolCodec.DecodeInt64(inbound.Envelope.Payload)), out _, out var claimReason))
                {
                    _ = network.SendAsync(inbound.Peer, MessageType.Reject, ProtocolCodec.EncodeString(claimReason)); return;
                }
                BroadcastRoom(); break;
            case MessageType.SetReady: Room.SetReady(client.ClientId, ProtocolCodec.DecodeInt64(inbound.Envelope.Payload) != 0); BroadcastRoom(); break;
            case MessageType.LobbyDraftChange:
                if (MatchId.HasValue || Room.MatchStarted) throw new InvalidDataException("Cannot modify the lobby after match start.");
                lobbyDraftReceived(ProtocolCodec.DecodeRoom(inbound.Envelope.Payload), inbound.Peer); break;
            case MessageType.CommandRequest:
                var request = ProtocolCodec.DecodeCommandRequest(inbound.Envelope.Payload);
                if (request.ClientId != client.ClientId) throw new InvalidDataException("Client identity mismatch.");
                commandReceived(request, inbound.Peer); break;
            case MessageType.HistoryRequest: SendHistory(inbound.Peer, ProtocolCodec.DecodeInt64(inbound.Envelope.Payload)); break;
            case MessageType.SnapshotRequest: _ = SendSnapshotAsync(inbound.Peer); break;
            default: throw new InvalidDataException("Message is not valid in this host state.");
        }
    }

    private void HandleHello(PeerConnection peer, HelloMessage hello)
    {
        if (hello.ProtocolVersion != ProtocolConstants.Version || hello.PluginVersion != pluginVersion || hello.GameFingerprint != gameFingerprint || hello.ContentFingerprint != contentFingerprint)
        {
            _ = RejectHandshakeAsync(peer, "Version or content fingerprint mismatch."); return;
        }
        var cleanName = (hello.DisplayName ?? "").Trim();
        if (cleanName.Length == 0 || cleanName.Length > 32) { _ = RejectHandshakeAsync(peer, "用户名长度必须为 1–32 个字符。"); return; }
        var record = new ClientRecord { ClientId = Guid.NewGuid(), DisplayName = cleanName, LastSeenUtc = DateTime.UtcNow, Peer = peer };
        clientsByConnection[peer.ConnectionId] = record;
        _ = network.SendAsync(peer, MessageType.Welcome, ProtocolCodec.EncodeWelcome(new WelcomeMessage { ClientId = record.ClientId, RoomId = Room.RoomId, MatchId = MatchId, LatestFrameId = journal?.Frames.Count ?? 0 }));
        var room = Room.Snapshot(); room.MatchId = MatchId;
        _ = network.SendAsync(peer, MessageType.RoomState, ProtocolCodec.EncodeRoom(room));
    }

    private async Task RejectHandshakeAsync(PeerConnection peer, string reason)
    {
        try { await network.SendAsync(peer, MessageType.Reject, ProtocolCodec.EncodeString(reason)).ConfigureAwait(false); }
        finally { peer.Close(); }
    }

    public void StartMatch(string journalPath)
    {
        Room.Start(); MatchId = Guid.NewGuid(); journal = new AuthorityJournal(MatchId.Value, journalPath);
    }

    public Guid LocalHostSeatId => Room.Seats.Single(value => value.ClientId == LocalHostClientId).SeatId;

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
        var fingerprint = new StringBuilder().Append(draft.MapId).Append('|').Append(draft.FowType).Append('|').Append(draft.WinCondition).Append('|').Append(draft.QuickStart);
        for (var index = 0; index < players.Count; index++)
        {
            var player = players[index]; var intent = draft.Seats.Find(value => value.LobbySlotIndex == index);
            fingerprint.Append('|').Append(player.exist).Append(',').Append((int)player.controller).Append(',').Append((int)player.team).Append(',').Append((int)player.color)
                .Append(',').Append(player.pos_ind).Append(',').Append(player.pos_random).Append(',').Append(player.res_percent.ToString("R", CultureInfo.InvariantCulture))
                .Append(',').Append(player.ai_interlligence.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(intent?.CommanderMode ?? 1)
                .Append(',').Append(intent?.CommanderId ?? "").Append(',').Append(intent?.SkillId ?? "");
            if (intent is not null) foreach (var passive in intent.PassiveIds) fingerprint.Append(',').Append(passive);
        }
        if (Room.SyncDraft(draft.MapId, draft.MapTitle, draft.FowType, draft.WinCondition, draft.QuickStart, fingerprint.ToString(), values)) BroadcastRoom();
    }

    public void BindRuntimePlayerIndices(IReadOnlyList<SGS_Player> players) => Room.BindRuntimePlayerIndices(players);

    public bool ClaimLocalSeat(int lobbySlotIndex, string displayName, out string reason)
    {
        var result = Room.TryClaimHumanSeat(LocalHostClientId, displayName, lobbySlotIndex, out _, out reason);
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
        _ = network.BroadcastAsync(MessageType.AuthorityFrame, ProtocolCodec.EncodeAuthorityFrame(frame));
        return frame;
    }

    public void BroadcastRoom()
    {
        var snapshot = Room.Snapshot(); snapshot.MatchId = MatchId;
        _ = network.BroadcastAsync(MessageType.RoomState, ProtocolCodec.EncodeRoom(snapshot));
    }
    public Task BroadcastSessionEndedAsync(string reason) =>
        network.BroadcastAsync(MessageType.SessionEnded, ProtocolCodec.EncodeString(reason));
    public void Accept(PeerConnection peer, ulong requestId, long frameId) => _ = network.SendAsync(peer, MessageType.CommandAccepted, ProtocolCodec.EncodeCommandResponse(new CommandResponse { RequestId = requestId, AuthorityFrameId = frameId }));
    public void Reject(PeerConnection peer, ulong requestId, string reason) => _ = network.SendAsync(peer, MessageType.CommandRejected, ProtocolCodec.EncodeCommandResponse(new CommandResponse { RequestId = requestId, AuthorityFrameId = 0, Reason = reason }));

    public bool Authorize(CommandRequest request, int currentPlayerIndex, int currentRound, out string reason)
    {
        if (request.Command.Kind == CommandKind.DebugAddResources || request.Command.Kind == CommandKind.DebugFillSkill)
        { reason = "Debug commands can only originate on the authority host."; return false; }
        if (!MatchId.HasValue || !Room.MatchStarted) { reason = "Match has not started."; return false; }
        var seat = Room.Seats.FirstOrDefault(value => value.SeatId == request.SeatId);
        if (seat is null || !seat.Connected || seat.ClientId != request.ClientId) { reason = "Client does not own the requested seat."; return false; }
        if (seat.AiControlled) { reason = "Seat is currently controlled by AI."; return false; }
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

    private async Task SendSnapshotAsync(PeerConnection peer)
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

    private void SendHistory(PeerConnection peer, long after)
    {
        if (journal is null) return;
        _ = SendHistoryAsync(peer, after);
    }

    private async Task SendHistoryAsync(PeerConnection peer, long after)
    {
        if (journal is null) return;
        foreach (var frame in journal.After(after)) await network.SendAsync(peer, MessageType.AuthorityFrame, ProtocolCodec.EncodeAuthorityFrame(frame)).ConfigureAwait(false);
        await network.SendAsync(peer, MessageType.HistoryComplete, ProtocolCodec.EncodeInt64(journal.Frames.Count)).ConfigureAwait(false);
    }

    public void Dispose() { journal?.Dispose(); network.Dispose(); }

    private sealed class ClientRecord
    {
        public Guid ClientId { get; set; }
        public string DisplayName { get; set; } = "";
        public DateTime LastSeenUtc { get; set; }
        public bool Disconnected { get; set; }
        public PeerConnection? Peer { get; set; }
    }
}
