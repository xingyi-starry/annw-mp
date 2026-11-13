using Google.Protobuf;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Infrastructure;

internal static class RelayEndpoint
{
    public static (string Host, int Port) Parse(string value)
    {
        var text = (value ?? "").Trim();
        var split = text.LastIndexOf(':');
        if (split <= 0 || split == text.Length - 1 || !int.TryParse(text.Substring(split + 1), out var port) || port < 1 || port > 65535)
            throw new InvalidDataException("${notice_relay_address_must_use_host_port_format}");
        return (text.Substring(0, split), port);
    }
    // Only these join rejections are displayed to the player.
    internal static string JoinNoticeTemplate(string reason) => reason switch
    {
        "invalid password" => "${notice_incorrect_password}",
        "password attempts are rate limited" => "${notice_relay_password_rate_limit}",
        "room not found" => "${notice_relay_room_missing}",
        "room is not accepting players" => "${notice_relay_room_not_accepting}",
        _ => reason
    };

}

internal sealed class RelayHostTransport : IHostTransport, IRoomDirectoryPublisher
{
    private readonly Guid roomId;
    private readonly Guid hostClientId;
    private readonly RelaySocket socket;
    private readonly ConcurrentQueue<InboundEnvelope> inbox = new ConcurrentQueue<InboundEnvelope>();
    private readonly Dictionary<Guid, RelayRemotePeer> peers = new Dictionary<Guid, RelayRemotePeer>();
    private readonly object peerLock = new object();
    private long controlSequence = long.MinValue;
    private bool failed;

    private RelayHostTransport(Guid roomId, Guid hostClientId, RelaySocket socket)
    { this.roomId = roomId; this.hostClientId = hostClientId; this.socket = socket; }
    public static async Task<RelayHostTransport> ConnectAsync(string endpoint, RelayRegisterRoomRequest registration)
    {
        var parsed = RelayEndpoint.Parse(endpoint); var socket = new RelaySocket();
        try
        {
            await socket.ConnectAsync(parsed.Host, parsed.Port).ConfigureAwait(false);
            var response = await socket.RequestAsync(new Envelope { Message = Messages.RelayRegisterRoom(ProtocolCodec.EncodeRelayRegisterRoom(registration)), Delivery = Delivery.RelayControl },
                MessageType.RelayControlResponse, registration.RequestId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var result = ProtocolCodec.DecodeRelayControlResponse(response.Payload);
            if (!result.Success || !result.ClientId.HasValue)
                throw new InvalidDataException(result.Reason.Length == 0 ? "The relay rejected room creation." : result.Reason);
            return new RelayHostTransport(registration.RoomId, result.ClientId.Value, socket);
        }
        catch { socket.Dispose(); throw; }
    }

    public void Start() { }
    public bool TryDequeue(out InboundEnvelope? envelope)
    {
        if (inbox.TryDequeue(out envelope)) return true;
        while (socket.TryDequeue(out var wire) && wire is not null)
        {
            if (wire.Delivery == Delivery.ToHost && wire.RoomId == roomId && wire.ClientId is Guid clientId)
            {
                RelayRemotePeer peer;
                lock (peerLock)
                {
                    if (!peers.TryGetValue(clientId, out peer!)) { peer = new RelayRemotePeer(this, clientId); peers.Add(clientId, peer); }
                    if (failed) peer.MarkClosed(); else peer.Touch();
                }
                envelope = new InboundEnvelope(peer, wire); return true;
            }
            if (wire.Delivery == Delivery.RelayControl && wire.Type == MessageType.RelayPeerLeft)
            {
                var notice = ProtocolCodec.DecodeRelayPeerNotice(wire.Payload);
                RelayRemotePeer? disconnected = null;
                lock (peerLock) if (peers.TryGetValue(notice.ClientId, out disconnected)) disconnected.MarkClosed();
                if (notice.Reason == "left" && disconnected is not null)
                {
                    envelope = new InboundEnvelope(disconnected, new Envelope { Message = Messages.LeaveSession(new XingyiStarry.Mp.Protocol.Wire.GuidValue()) });
                    return true;
                }
                // Let HostSession observe the logical disconnect and enter its grace state
                // before a fast ResumeSession from the replacement socket is dispatched.
                envelope = null; return false;
            }
        }
        envelope = null; return false;
    }

    public bool TryDequeueError(out Exception? exception)
    {
        if (!socket.TryDequeueError(out exception)) return false;
        MarkFailed();
        return true;
    }
    public async Task SendAsync(IRemotePeer peer, NetworkMessage message)
    {
        if (peer is not RelayRemotePeer relay || relay.Owner != this) throw new InvalidOperationException("Peer does not belong to this relay host.");
        try
        {
            await socket.SendAsync(new Envelope { Message = message, RoomId = roomId, ClientId = hostClientId,
                TargetClientId = relay.ClientId, Delivery = Delivery.ToClient }).ConfigureAwait(false);
        }
        catch { MarkFailed(); throw; }
    }
    public async Task BroadcastAsync(NetworkMessage message)
    {
        try
        {
            await socket.SendAsync(new Envelope
                { Message = message, RoomId = roomId, ClientId = hostClientId, Delivery = Delivery.Broadcast }).ConfigureAwait(false);
        }
        catch { MarkFailed(); throw; }
    }

    private void MarkFailed()
    {
        lock (peerLock)
        {
            failed = true;
            foreach (var peer in peers.Values) peer.MarkClosed();
        }
    }
    public async Task UpdateRoomAsync(RelayUpdateRoomRequest update)
    {
        update.RequestId = (ulong)Interlocked.Increment(ref controlSequence);
        var envelope = await socket.RequestAsync(new Envelope { Message = Messages.RelayUpdateRoom(ProtocolCodec.EncodeRelayUpdateRoom(update)), RoomId = roomId, ClientId = hostClientId, Delivery = Delivery.RelayControl }, MessageType.RelayControlResponse, update.RequestId,
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var response = ProtocolCodec.DecodeRelayControlResponse(envelope.Payload);
        if (!response.Success) throw new InvalidDataException(response.Reason.Length == 0 ? "The relay rejected the room update." : response.Reason);
    }
    public async Task CloseJoiningAsync(ulong requestId)
    {
        var response = await socket.RequestAsync(new Envelope { Message = Messages.RelayCloseJoining(ProtocolCodec.EncodeRelayRoomRequest(new RelayRoomRequest { RequestId = requestId, RoomId = roomId })), RoomId = roomId, ClientId = hostClientId, Delivery = Delivery.RelayControl },
            MessageType.RelayControlResponse, requestId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var result = ProtocolCodec.DecodeRelayControlResponse(response.Payload);
        if (!result.Success) throw new InvalidDataException(result.Reason.Length == 0 ? "The relay could not close room admissions." : result.Reason);
    }
    public void Dispose()
    {
        try { _ = socket.SendAsync(new Envelope { Message = Messages.RelayCloseRoom(ProtocolCodec.EncodeRelayRoomRequest(new RelayRoomRequest { RoomId = roomId })), RoomId = roomId, ClientId = hostClientId, Delivery = Delivery.RelayControl }); } catch { }
        socket.Dispose(); lock (peerLock) foreach (var peer in peers.Values) peer.MarkClosed();
    }

    private sealed class RelayRemotePeer : IRemotePeer
    {
        public RelayHostTransport Owner { get; }
        public Guid ClientId { get; }
        public Guid ConnectionId => ClientId;
        public bool IsConnected { get; private set; } = true;
        public DateTime LastReceivedUtc { get; private set; } = DateTime.UtcNow;
        public RelayRemotePeer(RelayHostTransport owner, Guid clientId) { Owner = owner; ClientId = clientId; }
        public void Touch() { LastReceivedUtc = DateTime.UtcNow; IsConnected = true; }
        public void MarkClosed() => IsConnected = false;
        public Task SendAsync(Envelope envelope) => Owner.SendAsync(this, envelope.Message);
        public void Close() => MarkClosed();
    }
}

internal sealed class RelayClientTransport : IClientTransport
{
    private readonly Guid roomId;
    private readonly string password;
    private readonly ulong joinRequestId;
    private RelaySocket? socket;
    private Guid clientId;
    private int connectionGeneration;
    public bool IsConnected => socket?.IsConnected == true;
    public RelayClientTransport(Guid roomId, string password, ulong joinRequestId)
    { this.roomId = roomId; this.password = password ?? ""; this.joinRequestId = joinRequestId; }

    public async Task ConnectAsync(string host, int port)
    {
        var generation = ++connectionGeneration; var nextSocket = new RelaySocket();
        try
        {
            await nextSocket.ConnectAsync(host, port).ConfigureAwait(false);
            var envelope = await nextSocket.RequestAsync(new Envelope { Message = Messages.RelayJoinRoom(ProtocolCodec.EncodeRelayJoinRoom(new RelayJoinRoomRequest { RequestId = joinRequestId, RoomId = roomId, Password = password })), Delivery = Delivery.RelayControl },
                MessageType.RelayControlResponse, joinRequestId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var response = ProtocolCodec.DecodeRelayControlResponse(envelope.Payload);
            if (!response.Success || !response.ClientId.HasValue) throw new InvalidDataException(response.Reason.Length == 0 ? "${notice_the_relay_rejected_joining_the_room}" : RelayEndpoint.JoinNoticeTemplate(response.Reason));
            if (generation != connectionGeneration) throw new OperationCanceledException("A newer connection attempt replaced this one.");
            socket?.Dispose(); socket = nextSocket; nextSocket = null!;
            clientId = response.ClientId.Value;
        }
        finally { nextSocket?.Dispose(); }
    }
    public async Task ReconnectAsync(string host, int port, Guid expectedRoomId, Guid expectedClientId, Guid matchId, ulong requestId)
    {
        var generation = ++connectionGeneration; var previous = socket; socket = null; previous?.Dispose();
        var nextSocket = new RelaySocket();
        try
        {
            await nextSocket.ConnectAsync(host, port).ConfigureAwait(false);
            var envelope = await nextSocket.RequestAsync(new Envelope { Message = Messages.RelayResumeRoom(ProtocolCodec.EncodeRelayResumeRoom(new RelayResumeRoomRequest { RequestId = requestId, RoomId = expectedRoomId,
                    ClientId = expectedClientId, MatchId = matchId })), Delivery = Delivery.RelayControl }, MessageType.RelayControlResponse, requestId, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            var response = ProtocolCodec.DecodeRelayControlResponse(envelope.Payload);
            if (!response.Success || response.ClientId != expectedClientId) throw new InvalidDataException(response.Reason.Length == 0 ? "The relay rejected reconnection." : response.Reason);
            if (generation != connectionGeneration) throw new OperationCanceledException("A newer reconnect attempt replaced this one.");
            socket = nextSocket; nextSocket = null!;
            clientId = expectedClientId;
        }
        finally { nextSocket?.Dispose(); }
    }
    public bool TryDequeue(out Envelope? envelope)
    {
        if (socket is null) { envelope = null; return false; }
        while (socket.TryDequeue(out var value) && value is not null)
        {
            if (value.Delivery == Delivery.RelayControl && value.Type == MessageType.RelayRoomClosed)
            {
                envelope = new Envelope { Message = Messages.SessionEnded(ProtocolCodec.EncodeString("${notice_the_public_room_has_closed}")) }; return true;
            }
            if ((value.Delivery == Delivery.ToClient || value.Delivery == Delivery.Broadcast) && value.RoomId == roomId)
            { envelope = value; return true; }
        }
        envelope = null; return false;
    }
    public bool TryDequeueError(out Exception? exception)
    { if (socket is null) { exception = null; return false; } return socket.TryDequeueError(out exception); }
    public Task SendAsync(NetworkMessage message)
    {
        if (socket is null) throw new InvalidOperationException("Client is not connected.");
        return socket.SendAsync(new Envelope { Message = message, RoomId = roomId, ClientId = clientId, Delivery = Delivery.ToHost });
    }
    public void Dispose()
    {
        connectionGeneration++;
        try { if (socket is not null) _ = socket.SendAsync(new Envelope { Message = Messages.RelayLeaveRoom(ProtocolCodec.EncodeRelayRoomRequest(new RelayRoomRequest { RoomId = roomId })), RoomId = roomId, ClientId = clientId, Delivery = Delivery.RelayControl }); } catch { }
        var previous = socket; socket = null; previous?.Dispose();
    }
}

internal static class RelayDirectoryClient
{
    public static async Task<IReadOnlyList<RelayRoomInfo>> ListRoomsAsync(string endpoint, ulong requestId)
    {
        var parsed = RelayEndpoint.Parse(endpoint); using var socket = new RelaySocket();
        await socket.ConnectAsync(parsed.Host, parsed.Port).ConfigureAwait(false);
        var envelope = await socket.RequestAsync(new Envelope { Message = Messages.RelayListRooms(ProtocolCodec.EncodeRelayListRoomsRequest(requestId)), Delivery = Delivery.RelayControl }, MessageType.RelayRoomList, requestId,
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return ProtocolCodec.DecodeRelayRoomList(envelope.Payload).Rooms;
    }
}

internal sealed class RelaySocket : IDisposable
{
    private readonly ConcurrentQueue<Envelope> inbox = new ConcurrentQueue<Envelope>();
    private readonly ConcurrentQueue<Exception> errors = new ConcurrentQueue<Exception>();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Envelope>> pending = new ConcurrentDictionary<string, TaskCompletionSource<Envelope>>();
    private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private TcpClient? client;
    public bool IsConnected => client?.Connected == true && !stop.IsCancellationRequested;
    public async Task ConnectAsync(string host, int port)
    {
        client = new TcpClient { NoDelay = true };
        var connecting = client.ConnectAsync(host, port);
        if (await Task.WhenAny(connecting, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false) != connecting)
        {
            client.Close();
            try { await connecting.ConfigureAwait(false); } catch { }
            throw new TimeoutException("${notice_connection_to_the_relay_timed_out}");
        }
        await connecting.ConfigureAwait(false);
        _ = ReceiveLoopAsync(); _ = HeartbeatLoopAsync();
    }
    public async Task SendAsync(Envelope envelope)
    {
        if (client is null) throw new InvalidOperationException("Relay socket is not connected.");
        await sendLock.WaitAsync(stop.Token).ConfigureAwait(false);
        try { await PacketFraming.WriteAsync(client.GetStream(), envelope, stop.Token).ConfigureAwait(false); }
        finally { sendLock.Release(); }
    }
    public bool TryDequeue(out Envelope? envelope) => inbox.TryDequeue(out envelope);
    public bool TryDequeueError(out Exception? error) => errors.TryDequeue(out error);
    public async Task<Envelope> RequestAsync(Envelope request, MessageType type, ulong requestId, TimeSpan timeout)
    {
        var key = type + ":" + requestId; var completion = new TaskCompletionSource<Envelope>();
        if (!pending.TryAdd(key, completion)) throw new InvalidOperationException("Duplicate relay request id.");
        try
        {
            await SendAsync(request).ConfigureAwait(false);
            var delay = Task.Delay(timeout); var completed = await Task.WhenAny(completion.Task, delay).ConfigureAwait(false);
            if (completed != completion.Task) throw new TimeoutException("${notice_timed_out_waiting_for_the_relay_response}");
            return await completion.Task.ConfigureAwait(false);
        }
        finally { pending.TryRemove(key, out _); }
    }
    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var envelope = await PacketFraming.ReadAsync(client!.GetStream(), stop.Token).ConfigureAwait(false);
                if (envelope is null) throw new EndOfStreamException("${notice_the_relay_closed_the_connection}");
                if (envelope.Type == MessageType.RelayControlResponse || envelope.Type == MessageType.RelayRoomList)
                {
                    var requestId = ProtocolCodec.DecodeRelayControlRequestId(envelope.Payload, envelope.Type);
                    if (pending.TryRemove(envelope.Type + ":" + requestId, out var completion)) { completion.TrySetResult(envelope); continue; }
                }
                inbox.Enqueue(envelope);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            errors.Enqueue(ex);
            foreach (var completion in pending.Values) completion.TrySetException(ex);
        }
    }
    private async Task HeartbeatLoopAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stop.Token).ConfigureAwait(false);
                await SendAsync(new Envelope { Message = Messages.Heartbeat(new XingyiStarry.Mp.Protocol.Wire.Int64Value()), Delivery = Delivery.RelayControl }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { errors.Enqueue(ex); }
    }
    public void Dispose() { stop.Cancel(); client?.Close(); sendLock.Dispose(); stop.Dispose(); }
}
