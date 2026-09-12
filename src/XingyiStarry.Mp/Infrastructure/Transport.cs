using System;
using System.Threading.Tasks;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Infrastructure;

internal interface IRemotePeer
{
    Guid ConnectionId { get; }
    bool IsConnected { get; }
    DateTime LastReceivedUtc { get; }
    Task SendAsync(Envelope envelope);
    void Close();
}

internal interface IHostTransport : IDisposable
{
    void Start();
    bool TryDequeue(out InboundEnvelope? envelope);
    bool TryDequeueError(out Exception? exception);
    Task SendAsync(IRemotePeer peer, MessageType type, byte[] payload);
    Task BroadcastAsync(MessageType type, byte[] payload);
}

internal interface IClientTransport : IDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(string host, int port);
    bool TryDequeue(out Envelope? envelope);
    bool TryDequeueError(out Exception? exception);
    Task SendAsync(MessageType type, byte[] payload);
}
