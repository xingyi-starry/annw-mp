using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using XingyiStarry.Mp.Infrastructure;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Session;

internal sealed class ClientSession : IDisposable
{
    private readonly IClientTransport network;
    private readonly SortedDictionary<long, AuthorityFrame> received = new SortedDictionary<long, AuthorityFrame>();
    private readonly SortedDictionary<long, AuthorityFrame> pendingVerification = new SortedDictionary<long, AuthorityFrame>();
    private Guid? matchId;
    private byte[] verifiedHash = new byte[AuthorityHashChain.HashLength];
    private DateTime lastHeartbeatUtc = DateTime.MinValue;
    private SnapshotManifest? snapshotManifest;
    private SnapshotAssembler? snapshotAssembler;
    private bool hasSnapshotAnchor;
    private bool snapshotRequested;
    private string connectedHost = "";
    private int connectedPort;
    private WelcomeMode welcomeMode;
    private Guid sessionRoomId;
    public Guid? ClientId { get; private set; }
    public long VerifiedFrameId { get; private set; }
    public long AppliedFrameId { get; private set; }
    public RoomSnapshot? Room { get; private set; }
    public bool IsCaughtUp { get; private set; }
    public bool SnapshotRequested => snapshotRequested;
    public bool ConnectionLost { get; private set; }
    public bool TerminalSessionEnded { get; private set; }
    public string ConnectionError { get; private set; } = "";
    public Guid? MatchId => matchId;
    public bool JoinedMatch { get; private set; }
    public bool RequiresJoinSelection => welcomeMode == WelcomeMode.JoinSelection && !JoinedMatch;
    public bool CanFastReconnect => !TerminalSessionEnded && ClientId.HasValue && matchId.HasValue && JoinedMatch;
    public int ReconnectAcceptedGeneration { get; private set; }

    public ClientSession() : this(new NetworkClient()) { }
    public ClientSession(IClientTransport network) => this.network = network;

    public async Task ConnectAsync(string host, int port, HelloMessage hello)
    {
        connectedHost = host; connectedPort = port;
        await network.ConnectAsync(host, port).ConfigureAwait(false);
        await network.SendAsync(MessageType.Hello, ProtocolCodec.EncodeHello(hello)).ConfigureAwait(false);
    }

    public void Pump(Action<SessionLogLevel, string> log, Action<CommandResponse, bool> commandResponse,
        Action<AuthorityFrame> frameReceived, Action<SnapshotManifest, byte[]> snapshotReceived, Action<string> noticeReceived)
    {
        while (network.TryDequeueError(out var error))
        {
            ConnectionLost = true; ConnectionError = error?.Message ?? "连接已关闭"; log(SessionLogLevel.Warning, "Network client: " + ConnectionError);
        }
        while (network.TryDequeue(out var envelope) && envelope is not null)
        {
            try
            {
                switch (envelope.Type)
                {
                    case MessageType.Welcome:
                        var welcome = ProtocolCodec.DecodeWelcome(envelope.Payload); ClientId = welcome.ClientId; sessionRoomId = welcome.RoomId; matchId = welcome.MatchId; welcomeMode = welcome.Mode;
                        JoinedMatch = welcome.Mode == WelcomeMode.ActiveMatch;
                        if (JoinedMatch && matchId.HasValue) _ = RequestSnapshotAsync();
                        break;
                    case MessageType.JoinMatchAccepted:
                        JoinedMatch = true; welcomeMode = WelcomeMode.ActiveMatch; IsCaughtUp = false;
                        break;
                    case MessageType.ResumeSessionAccepted:
                        var resumed = ProtocolCodec.DecodeResumeSessionAccepted(envelope.Payload);
                        if (ClientId != resumed.ClientId || matchId != resumed.MatchId) throw new InvalidDataException("快速重连身份响应不匹配。");
                        ConnectionLost = false; ConnectionError = ""; ReconnectAcceptedGeneration++; IsCaughtUp = false;
                        break;
                    case MessageType.ResumeSessionRejected:
                        ConnectionError = ProtocolCodec.DecodeString(envelope.Payload);
                        break;
                    case MessageType.AuthorityFrame:
                        var frame = ProtocolCodec.DecodeAuthorityFrame(envelope.Payload);
                        if (!hasSnapshotAnchor) { if (!matchId.HasValue) matchId = frame.MatchId; break; }
                        AcceptFrame(frame, frameReceived); break;
                    case MessageType.RoomState:
                        var room = ProtocolCodec.DecodeRoom(envelope.Payload);
                        var newlyStarted = room.MatchStarted && room.MatchId.HasValue && !hasSnapshotAnchor && !snapshotRequested;
                        Room = room;
                        var ownsSeat = ClientId.HasValue && room.Seats.Exists(value => value.Connected && value.ClientId == ClientId.Value);
                        if (newlyStarted && welcomeMode != WelcomeMode.JoinSelection && ownsSeat)
                        { matchId = room.MatchId; JoinedMatch = true; snapshotRequested = true; _ = RequestSnapshotAsync(); }
                        else if (newlyStarted && !ownsSeat)
                        { matchId = room.MatchId; welcomeMode = WelcomeMode.JoinSelection; JoinedMatch = false; }
                        break;
                    case MessageType.HistoryComplete:
                        var latestFrameId = ProtocolCodec.DecodeInt64(envelope.Payload);
                        IsCaughtUp = VerifiedFrameId >= latestFrameId;
                        log(SessionLogLevel.Info, $"History complete latest={latestFrameId} verified={VerifiedFrameId} applied={AppliedFrameId} caughtUp={IsCaughtUp}.");
                        break;
                    case MessageType.SnapshotManifest:
                        snapshotManifest = ProtocolCodec.DecodeSnapshotManifest(envelope.Payload); snapshotAssembler = new SnapshotAssembler(snapshotManifest);
                        log(SessionLogLevel.Info, $"Snapshot manifest id={snapshotManifest.SnapshotId:N} frame={snapshotManifest.FrameId} chunks={snapshotManifest.ChunkCount} compressed={snapshotManifest.CompressedLength}.");
                        break;
                    case MessageType.SnapshotChunk:
                        if (snapshotAssembler is null) throw new InvalidDataException("Snapshot chunk arrived before its manifest.");
                        snapshotAssembler.Add(ProtocolCodec.DecodeSnapshotChunk(envelope.Payload)); break;
                    case MessageType.SnapshotComplete:
                        if (snapshotManifest is null || snapshotAssembler is null || ProtocolCodec.DecodeGuid(envelope.Payload) != snapshotManifest.SnapshotId) throw new InvalidDataException("Snapshot completion identity mismatch.");
                        var compressed = snapshotAssembler.Finish(); var snapshot = SnapshotCodec.Decompress(compressed, ProtocolConstants.MaxSnapshotBytes);
                        log(SessionLogLevel.Info, $"Snapshot transfer complete id={snapshotManifest.SnapshotId:N} frame={snapshotManifest.FrameId} bytes={snapshot.Length}.");
                        snapshotReceived(snapshotManifest, snapshot); snapshotManifest = null; snapshotAssembler = null; break;
                    case MessageType.Reject: throw new InvalidDataException(ProtocolCodec.DecodeString(envelope.Payload));
                    case MessageType.CommandRejected:
                        commandResponse(ProtocolCodec.DecodeCommandResponse(envelope.Payload), false); break;
                    case MessageType.CommandAccepted:
                        commandResponse(ProtocolCodec.DecodeCommandResponse(envelope.Payload), true); break;
                    case MessageType.SessionEnded:
                        TerminalSessionEnded = true;
                        ConnectionError = ProtocolCodec.DecodeString(envelope.Payload);
                        ConnectionLost = true;
                        break;
                    case MessageType.ParticipantNotice:
                        noticeReceived(ProtocolCodec.DecodeString(envelope.Payload));
                        break;
                }
            }
            catch (Exception ex)
            {
                log(SessionLogLevel.Warning, "Client synchronization paused: " + ex.Message);
                if (envelope.Type == MessageType.AuthorityFrame) _ = RequestSnapshotAsync();
            }
        }
    }

    public void Tick()
    {
        if (!network.IsConnected || !ClientId.HasValue) return;
        if (DateTime.UtcNow - lastHeartbeatUtc < TimeSpan.FromSeconds(2)) return;
        lastHeartbeatUtc = DateTime.UtcNow;
        _ = network.SendAsync(MessageType.Heartbeat, ProtocolCodec.EncodeInt64(lastHeartbeatUtc.Ticks));
    }

    public Task ClaimSeatAsync(int lobbySlotIndex) => network.SendAsync(MessageType.ClaimSeat, ProtocolCodec.EncodeInt64(lobbySlotIndex));
    public Task ReleaseSeatAsync() => network.SendAsync(MessageType.ReleaseSeat, Array.Empty<byte>());
    public Task JoinMatchAsync(Guid seatId) => network.SendAsync(MessageType.JoinMatchRequest, ProtocolCodec.EncodeJoinMatchRequest(new JoinMatchRequest { SeatId = seatId }));
    public Task SetReadyAsync(bool ready) => network.SendAsync(MessageType.SetReady, ProtocolCodec.EncodeInt64(ready ? 1 : 0));
    public Task SendLobbyDraftAsync(RoomSnapshot draft) => network.SendAsync(MessageType.LobbyDraftChange, ProtocolCodec.EncodeRoom(draft));
    public Task RequestSnapshotAsync()
    {
        hasSnapshotAnchor = false; snapshotRequested = true; IsCaughtUp = false;
        return network.SendAsync(MessageType.SnapshotRequest, ProtocolCodec.EncodeInt64(AppliedFrameId));
    }
    public Task RequestHistoryAsync() => network.SendAsync(MessageType.HistoryRequest, ProtocolCodec.EncodeInt64(VerifiedFrameId));

    public async Task ReconnectAttemptAsync(ulong requestId)
    {
        if (!ClientId.HasValue || !matchId.HasValue) throw new InvalidOperationException("没有可恢复的联机会话。");
        await network.ReconnectAsync(connectedHost, connectedPort, sessionRoomId, ClientId.Value, matchId.Value, requestId).ConfigureAwait(false);
        await network.SendAsync(MessageType.ResumeSession, ProtocolCodec.EncodeResumeSession(new ResumeSessionRequest
            { RoomId = sessionRoomId, MatchId = matchId.Value, ClientId = ClientId.Value,
              AppliedFrameId = AppliedFrameId, VerifiedFrameId = VerifiedFrameId })).ConfigureAwait(false);
    }

    public Task LeaveAsync() => network.IsConnected ? network.SendAsync(MessageType.LeaveSession, Array.Empty<byte>()) : Task.CompletedTask;

    public void MarkMatchEnded() => TerminalSessionEnded = true;

    public void AcceptSnapshotAnchor(SnapshotManifest manifest)
    {
        if (matchId.HasValue && manifest.MatchId != matchId.Value) throw new InvalidDataException("Snapshot belongs to another match.");
        matchId = manifest.MatchId; VerifiedFrameId = manifest.FrameId; AppliedFrameId = manifest.FrameId;
        verifiedHash = (byte[])manifest.FrameHash.Clone(); received.Clear(); pendingVerification.Clear(); IsCaughtUp = false; hasSnapshotAnchor = true; snapshotRequested = false;
    }

    private void AcceptFrame(AuthorityFrame frame, Action<AuthorityFrame> frameReceived)
    {
        if (!matchId.HasValue) matchId = frame.MatchId;
        if (frame.MatchId != matchId.Value) throw new InvalidDataException("Authority frame belongs to another match.");
        if (frame.FrameId <= VerifiedFrameId) return;
        if (frame.FrameId > VerifiedFrameId + 65536) throw new InvalidDataException("Authority frame is too far ahead of the verified cursor.");
        if (pendingVerification.TryGetValue(frame.FrameId, out var duplicate))
        {
            if (!AuthorityHashChain.FixedEquals(duplicate.Hash, frame.Hash)) throw new InvalidDataException("Conflicting duplicate authority frame.");
            return;
        }
        if (pendingVerification.Count >= 65536) throw new InvalidDataException("Authority reorder buffer limit exceeded.");
        pendingVerification.Add(frame.FrameId, frame);
        while (pendingVerification.TryGetValue(VerifiedFrameId + 1, out var next))
        {
            AuthorityHashChain.VerifyNext(next, matchId.Value, VerifiedFrameId + 1, verifiedHash);
            pendingVerification.Remove(next.FrameId);
            received.Add(next.FrameId, next); VerifiedFrameId = next.FrameId; verifiedHash = (byte[])next.Hash.Clone();
            frameReceived(next);
        }
    }

    public void MarkApplied(long frameId)
    {
        if (frameId != AppliedFrameId + 1 || !received.ContainsKey(frameId)) throw new InvalidOperationException("Frames must be applied in order.");
        AppliedFrameId = frameId;
    }

    public Task SendCommandAsync(CommandRequest request)
    {
        if (!ClientId.HasValue || request.ClientId != ClientId.Value) throw new InvalidOperationException("Handshake has not completed.");
        return network.SendAsync(MessageType.CommandRequest, ProtocolCodec.EncodeCommandRequest(request));
    }

    public void Dispose()
    {
        try { if (network.IsConnected && ClientId.HasValue) LeaveAsync().Wait(TimeSpan.FromMilliseconds(250)); } catch { }
        network.Dispose();
    }
}
