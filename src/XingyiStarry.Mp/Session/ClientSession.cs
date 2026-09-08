using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using XingyiStarry.Mp.Infrastructure;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Session;

internal sealed class ClientSession : IDisposable
{
    private readonly NetworkClient network = new NetworkClient();
    private readonly SortedDictionary<long, AuthorityFrame> received = new SortedDictionary<long, AuthorityFrame>();
    private Guid? matchId;
    private byte[] verifiedHash = new byte[AuthorityHashChain.HashLength];
    private DateTime lastHeartbeatUtc = DateTime.MinValue;
    private SnapshotManifest? snapshotManifest;
    private SnapshotAssembler? snapshotAssembler;
    private bool hasSnapshotAnchor;
    private bool snapshotRequested;
    public Guid? ClientId { get; private set; }
    public long VerifiedFrameId { get; private set; }
    public long AppliedFrameId { get; private set; }
    public RoomSnapshot? Room { get; private set; }
    public bool IsCaughtUp { get; private set; }

    public async Task ConnectAsync(string host, int port, HelloMessage hello)
    {
        await network.ConnectAsync(host, port).ConfigureAwait(false);
        await network.SendAsync(MessageType.Hello, ProtocolCodec.EncodeHello(hello)).ConfigureAwait(false);
    }

    public void Pump(Action<string> log, Action<AuthorityFrame> frameReceived, Action<SnapshotManifest, byte[]> snapshotReceived)
    {
        while (network.TryDequeueError(out var error)) log("Network client: " + error);
        while (network.TryDequeue(out var envelope) && envelope is not null)
        {
            try
            {
                switch (envelope.Type)
                {
                    case MessageType.Welcome:
                        var welcome = ProtocolCodec.DecodeWelcome(envelope.Payload); ClientId = welcome.ClientId; matchId = welcome.MatchId;
                        if (matchId.HasValue) _ = RequestSnapshotAsync();
                        break;
                    case MessageType.AuthorityFrame:
                        var frame = ProtocolCodec.DecodeAuthorityFrame(envelope.Payload);
                        if (!hasSnapshotAnchor) { if (!matchId.HasValue) matchId = frame.MatchId; break; }
                        AcceptFrame(frame); frameReceived(frame); break;
                    case MessageType.RoomState:
                        var room = ProtocolCodec.DecodeRoom(envelope.Payload);
                        var newlyStarted = room.MatchStarted && room.MatchId.HasValue && !hasSnapshotAnchor && !snapshotRequested;
                        Room = room;
                        if (newlyStarted) { matchId = room.MatchId; snapshotRequested = true; _ = RequestSnapshotAsync(); }
                        break;
                    case MessageType.HistoryComplete: IsCaughtUp = ProtocolCodec.DecodeInt64(envelope.Payload) == VerifiedFrameId; break;
                    case MessageType.SnapshotManifest:
                        snapshotManifest = ProtocolCodec.DecodeSnapshotManifest(envelope.Payload); snapshotAssembler = new SnapshotAssembler(snapshotManifest); break;
                    case MessageType.SnapshotChunk:
                        if (snapshotAssembler is null) throw new InvalidDataException("Snapshot chunk arrived before its manifest.");
                        snapshotAssembler.Add(ProtocolCodec.DecodeSnapshotChunk(envelope.Payload)); break;
                    case MessageType.SnapshotComplete:
                        if (snapshotManifest is null || snapshotAssembler is null || ProtocolCodec.DecodeGuid(envelope.Payload) != snapshotManifest.SnapshotId) throw new InvalidDataException("Snapshot completion identity mismatch.");
                        var compressed = snapshotAssembler.Finish(); var snapshot = SnapshotCodec.Decompress(compressed, ProtocolConstants.MaxSnapshotBytes);
                        snapshotReceived(snapshotManifest, snapshot); snapshotManifest = null; snapshotAssembler = null; break;
                    case MessageType.Reject: throw new InvalidDataException(ProtocolCodec.DecodeString(envelope.Payload));
                    case MessageType.CommandRejected:
                        var rejection = ProtocolCodec.DecodeCommandResponse(envelope.Payload); log($"Command {rejection.RequestId} rejected: {rejection.Reason}"); break;
                    case MessageType.CommandAccepted: break;
                }
            }
            catch (Exception ex) { log("Client synchronization paused: " + ex.Message); }
        }
    }

    public void Tick()
    {
        if (!network.IsConnected) return;
        if (DateTime.UtcNow - lastHeartbeatUtc < TimeSpan.FromSeconds(2)) return;
        lastHeartbeatUtc = DateTime.UtcNow;
        _ = network.SendAsync(MessageType.Heartbeat, ProtocolCodec.EncodeInt64(lastHeartbeatUtc.Ticks));
    }

    public Task ClaimSeatAsync(int lobbySlotIndex) => network.SendAsync(MessageType.ClaimSeat, ProtocolCodec.EncodeInt64(lobbySlotIndex));
    public Task SetReadyAsync(bool ready) => network.SendAsync(MessageType.SetReady, ProtocolCodec.EncodeInt64(ready ? 1 : 0));
    public Task RequestSnapshotAsync()
    {
        hasSnapshotAnchor = false; snapshotRequested = true; IsCaughtUp = false;
        return network.SendAsync(MessageType.SnapshotRequest, ProtocolCodec.EncodeInt64(AppliedFrameId));
    }
    public Task RequestHistoryAsync() => network.SendAsync(MessageType.HistoryRequest, ProtocolCodec.EncodeInt64(VerifiedFrameId));

    public void AcceptSnapshotAnchor(SnapshotManifest manifest)
    {
        if (matchId.HasValue && manifest.MatchId != matchId.Value) throw new InvalidDataException("Snapshot belongs to another match.");
        matchId = manifest.MatchId; VerifiedFrameId = manifest.FrameId; AppliedFrameId = manifest.FrameId;
        verifiedHash = (byte[])manifest.FrameHash.Clone(); received.Clear(); IsCaughtUp = false; hasSnapshotAnchor = true; snapshotRequested = false;
    }

    private void AcceptFrame(AuthorityFrame frame)
    {
        if (!matchId.HasValue) matchId = frame.MatchId;
        AuthorityHashChain.VerifyNext(frame, matchId.Value, VerifiedFrameId + 1, verifiedHash);
        received.Add(frame.FrameId, frame); VerifiedFrameId = frame.FrameId; verifiedHash = (byte[])frame.Hash.Clone();
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

    public void Dispose() => network.Dispose();
}
