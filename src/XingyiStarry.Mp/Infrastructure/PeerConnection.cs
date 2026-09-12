using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Infrastructure;

internal sealed class PeerConnection : IRemotePeer, IDisposable
{
    private readonly TcpClient client;
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
    private readonly ConcurrentQueue<InboundEnvelope> inbox;
    private readonly ConcurrentQueue<Exception> errors;

    public Guid ConnectionId { get; } = Guid.NewGuid();
    public bool IsConnected => client.Connected && !stop.IsCancellationRequested;
    public DateTime LastReceivedUtc { get; private set; } = DateTime.UtcNow;

    public PeerConnection(TcpClient client, ConcurrentQueue<InboundEnvelope> inbox, ConcurrentQueue<Exception> errors)
    {
        this.client = client; this.inbox = inbox; this.errors = errors;
        client.NoDelay = true;
    }

    public void Start() => _ = ReceiveLoopAsync();

    public async Task SendAsync(Envelope envelope)
    {
        await sendLock.WaitAsync(stop.Token).ConfigureAwait(false);
        try { await PacketFraming.WriteAsync(client.GetStream(), envelope, stop.Token).ConfigureAwait(false); }
        finally { sendLock.Release(); }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var envelope = await PacketFraming.ReadAsync(client.GetStream(), stop.Token).ConfigureAwait(false);
                if (envelope is null)
                {
                    if (!stop.IsCancellationRequested) errors.Enqueue(new EndOfStreamException("远端已关闭连接。"));
                    break;
                }
                LastReceivedUtc = DateTime.UtcNow; inbox.Enqueue(new InboundEnvelope(this, envelope));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { errors.Enqueue(ex); }
        finally { stop.Cancel(); }
    }

    public void Dispose()
    {
        stop.Cancel(); client.Close(); sendLock.Dispose(); stop.Dispose();
    }
    public void Close() => Dispose();
}

internal sealed class InboundEnvelope
{
    public IRemotePeer Peer { get; }
    public Envelope Envelope { get; }
    public InboundEnvelope(IRemotePeer peer, Envelope envelope) { Peer = peer; Envelope = envelope; }
}
