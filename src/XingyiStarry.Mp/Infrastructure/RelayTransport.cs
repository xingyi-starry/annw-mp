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
            throw new InvalidDataException("中继服务器地址必须使用 host:port 格式。");
        return (text.Substring(0, split), port);
    }
}

internal sealed class RelayHostTransport : IHostTransport
{
    private readonly Guid roomId;
    private readonly Guid hostClientId;
    private readonly RelaySocket socket;
    private readonly ConcurrentQueue<InboundEnvelope> inbox = new ConcurrentQueue<InboundEnvelope>();
    private readonly Dictionary<Guid, RelayRemotePeer> peers = new Dictionary<Guid, RelayRemotePeer>();
    private readonly object peerLock = new object();
    private long controlSequence = long.MinValue;

    private RelayHostTransport(Guid roomId, Guid hostClientId, RelaySocket socket)
    { this.roomId = roomId; this.hostClientId = hostClientId; this.socket = socket; }
    public Guid HostClientId => hostClientId;

    public static async Task<RelayHostTransport> ConnectAsync(string endpoint, RelayRegisterRoomRequest registration)
    {
        var parsed = RelayEndpoint.Parse(endpoint); var socket = new RelaySocket();
        await socket.ConnectAsync(parsed.Host, parsed.Port).ConfigureAwait(false);
        var response = await socket.RequestAsync(new Envelope { Type = MessageType.RelayRegisterRoom, Delivery = Delivery.RelayControl,
            Payload = ProtocolCodec.EncodeRelayRegisterRoom(registration) },
            MessageType.RelayControlResponse, registration.RequestId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var result = ProtocolCodec.DecodeRelayControlResponse(response.Payload);
        if (!result.Success || !result.ClientId.HasValue) { socket.Dispose(); throw new InvalidDataException(result.Reason.Length == 0 ? "中继服务器拒绝创建房间。" : result.Reason); }
        return new RelayHostTransport(registration.RoomId, result.ClientId.Value, socket);
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
                    peer.Touch();
                }
                envelope = new InboundEnvelope(peer, wire); return true;
            }
            if (wire.Delivery == Delivery.RelayControl && wire.Type == MessageType.RelayPeerLeft)
            {
                var notice = ProtocolCodec.DecodeRelayPeerNotice(wire.Payload);
                lock (peerLock) if (peers.TryGetValue(notice.ClientId, out var peer)) peer.MarkClosed();
            }
        }
        envelope = null; return false;
    }

    public bool TryDequeueError(out Exception? exception) => socket.TryDequeueError(out exception);
    public Task SendAsync(IRemotePeer peer, MessageType type, byte[] payload)
    {
        if (peer is not RelayRemotePeer relay || relay.Owner != this) throw new InvalidOperationException("Peer does not belong to this relay host.");
        return socket.SendAsync(new Envelope { Type = type, Payload = payload, RoomId = roomId, ClientId = hostClientId,
            TargetClientId = relay.ClientId, Delivery = Delivery.ToClient });
    }
    public Task BroadcastAsync(MessageType type, byte[] payload) => socket.SendAsync(new Envelope
        { Type = type, Payload = payload, RoomId = roomId, ClientId = hostClientId, Delivery = Delivery.Broadcast });
    public async Task UpdateRoomAsync(RelayUpdateRoomRequest update)
    {
        update.RequestId = (ulong)Interlocked.Increment(ref controlSequence);
        var envelope = await socket.RequestAsync(new Envelope { Type = MessageType.RelayUpdateRoom,
            Payload = ProtocolCodec.EncodeRelayUpdateRoom(update), RoomId = roomId, ClientId = hostClientId,
            Delivery = Delivery.RelayControl }, MessageType.RelayControlResponse, update.RequestId,
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var response = ProtocolCodec.DecodeRelayControlResponse(envelope.Payload);
        if (!response.Success) throw new InvalidDataException(response.Reason.Length == 0 ? "中继服务器拒绝房间更新。" : response.Reason);
    }
    public async Task CloseJoiningAsync(ulong requestId)
    {
        var response = await socket.RequestAsync(new Envelope { Type = MessageType.RelayCloseJoining, RoomId = roomId, ClientId = hostClientId,
            Delivery = Delivery.RelayControl, Payload = ProtocolCodec.EncodeRelayRoomRequest(new RelayRoomRequest { RequestId = requestId, RoomId = roomId }) },
            MessageType.RelayControlResponse, requestId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var result = ProtocolCodec.DecodeRelayControlResponse(response.Payload);
        if (!result.Success) throw new InvalidDataException(result.Reason.Length == 0 ? "中继服务器未能关闭房间加入。" : result.Reason);
    }
    public void Dispose()
    {
        try { _ = socket.SendAsync(new Envelope { Type = MessageType.RelayCloseRoom, RoomId = roomId, ClientId = hostClientId,
            Delivery = Delivery.RelayControl, Payload = ProtocolCodec.EncodeRelayRoomRequest(new RelayRoomRequest { RoomId = roomId }) }); } catch { }
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
        public Task SendAsync(Envelope envelope) => Owner.SendAsync(this, envelope.Type, envelope.Payload);
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
    public bool IsConnected => socket?.IsConnected == true;
    public RelayClientTransport(Guid roomId, string password, ulong joinRequestId)
    { this.roomId = roomId; this.password = password ?? ""; this.joinRequestId = joinRequestId; }

    public async Task ConnectAsync(string host, int port)
    {
        socket = new RelaySocket(); await socket.ConnectAsync(host, port).ConfigureAwait(false);
        var envelope = await socket.RequestAsync(new Envelope { Type = MessageType.RelayJoinRoom, Delivery = Delivery.RelayControl,
            Payload = ProtocolCodec.EncodeRelayJoinRoom(new RelayJoinRoomRequest { RequestId = joinRequestId, RoomId = roomId, Password = password }) },
            MessageType.RelayControlResponse, joinRequestId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var response = ProtocolCodec.DecodeRelayControlResponse(envelope.Payload);
        if (!response.Success || !response.ClientId.HasValue) throw new InvalidDataException(response.Reason.Length == 0 ? "中继服务器拒绝加入房间。" : response.Reason);
        clientId = response.ClientId.Value;
    }
    public bool TryDequeue(out Envelope? envelope)
    {
        if (socket is null) { envelope = null; return false; }
        while (socket.TryDequeue(out var value) && value is not null)
        {
            if (value.Delivery == Delivery.RelayControl && value.Type == MessageType.RelayCloseRoom)
            {
                envelope = new Envelope { Type = MessageType.SessionEnded, Payload = ProtocolCodec.EncodeString("公共房间已关闭。") }; return true;
            }
            if ((value.Delivery == Delivery.ToClient || value.Delivery == Delivery.Broadcast) && value.RoomId == roomId)
            { envelope = value; return true; }
        }
        envelope = null; return false;
    }
    public bool TryDequeueError(out Exception? exception)
    { if (socket is null) { exception = null; return false; } return socket.TryDequeueError(out exception); }
    public Task SendAsync(MessageType type, byte[] payload)
    {
        if (socket is null) throw new InvalidOperationException("Client is not connected.");
        return socket.SendAsync(new Envelope { Type = type, Payload = payload, RoomId = roomId, ClientId = clientId, Delivery = Delivery.ToHost });
    }
    public void Dispose()
    {
        try { if (socket is not null) _ = socket.SendAsync(new Envelope { Type = MessageType.RelayLeaveRoom, RoomId = roomId,
            ClientId = clientId, Delivery = Delivery.RelayControl, Payload = ProtocolCodec.EncodeRelayRoomRequest(new RelayRoomRequest { RoomId = roomId }) }); } catch { }
        socket?.Dispose(); socket = null;
    }
}

internal static class RelayDirectoryClient
{
    public static async Task<IReadOnlyList<RelayRoomInfo>> ListRoomsAsync(string endpoint, ulong requestId)
    {
        var parsed = RelayEndpoint.Parse(endpoint); using var socket = new RelaySocket();
        await socket.ConnectAsync(parsed.Host, parsed.Port).ConfigureAwait(false);
        var envelope = await socket.RequestAsync(new Envelope { Type = MessageType.RelayListRooms, Delivery = Delivery.RelayControl,
            Payload = ProtocolCodec.EncodeRelayListRoomsRequest(requestId) }, MessageType.RelayRoomList, requestId,
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
        client = new TcpClient { NoDelay = true }; await client.ConnectAsync(host, port).ConfigureAwait(false);
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
            if (completed != completion.Task) throw new TimeoutException("等待中继服务器响应超时。");
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
                if (envelope is null) throw new EndOfStreamException("中继服务器已关闭连接。");
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
                await SendAsync(new Envelope { Type = MessageType.Heartbeat, Delivery = Delivery.RelayControl }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { errors.Enqueue(ex); }
    }
    public void Dispose() { stop.Cancel(); client?.Close(); sendLock.Dispose(); stop.Dispose(); }
}
