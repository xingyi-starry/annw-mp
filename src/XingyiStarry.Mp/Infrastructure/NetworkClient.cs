using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Tasks;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Infrastructure;

internal sealed class NetworkClient : IDisposable
{
    private readonly ConcurrentQueue<InboundEnvelope> inbox = new ConcurrentQueue<InboundEnvelope>();
    private readonly ConcurrentQueue<Exception> errors = new ConcurrentQueue<Exception>();
    private PeerConnection? peer;
    public bool IsConnected => peer?.IsConnected == true;

    public async Task ConnectAsync(string host, int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port).ConfigureAwait(false);
        peer = new PeerConnection(tcp, inbox, errors); peer.Start();
    }

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

    public void Dispose() { peer?.Dispose(); peer = null; }
}
