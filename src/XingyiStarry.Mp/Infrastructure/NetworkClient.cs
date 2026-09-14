using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Tasks;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Infrastructure;

internal sealed class NetworkClient : IClientTransport
{
    private readonly ConcurrentQueue<InboundEnvelope> inbox = new ConcurrentQueue<InboundEnvelope>();
    private readonly ConcurrentQueue<Exception> errors = new ConcurrentQueue<Exception>();
    private PeerConnection? peer;
    private int connectionGeneration;
    public bool IsConnected => peer?.IsConnected == true;

    public async Task ConnectAsync(string host, int port)
    {
        var generation = ++connectionGeneration;
        var previous = peer; peer = null; previous?.Dispose();
        while (inbox.TryDequeue(out _)) { }
        while (errors.TryDequeue(out _)) { }
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port).ConfigureAwait(false);
        if (generation != connectionGeneration) { tcp.Close(); throw new OperationCanceledException("A newer connection attempt replaced this one."); }
        peer = new PeerConnection(tcp, inbox, errors); peer.Start();
    }

    public Task ReconnectAsync(string host, int port, Guid roomId, Guid clientId, Guid matchId, ulong requestId) => ConnectAsync(host, port);

    public bool TryDequeue(out Envelope? envelope)
    {
        if (inbox.TryDequeue(out var received)) { envelope = received.Envelope; return true; }
        envelope = null; return false;
    }

    public bool TryDequeueError(out Exception? exception) => errors.TryDequeue(out exception);
    public Task SendAsync(MessageType type, byte[] payload)
    {
        if (peer is null) throw new InvalidOperationException("Client is not connected.");
        return peer.SendAsync(new Envelope { Type = type, Payload = payload });
    }

    public void Dispose() { connectionGeneration++; var previous = peer; peer = null; previous?.Dispose(); }
}
