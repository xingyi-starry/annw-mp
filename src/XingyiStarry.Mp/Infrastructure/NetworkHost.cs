using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Infrastructure;

internal sealed class NetworkHost : IHostTransport
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private readonly ConcurrentQueue<InboundEnvelope> inbox = new ConcurrentQueue<InboundEnvelope>();
    private readonly ConcurrentQueue<Exception> errors = new ConcurrentQueue<Exception>();
    private readonly List<PeerConnection> peers = new List<PeerConnection>();
    private readonly object peerLock = new object();

    public NetworkHost(int port) => listener = new TcpListener(IPAddress.Any, port);

    public void Start()
    {
        listener.Start();
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                var peer = new PeerConnection(tcp, inbox, errors);
                lock (peerLock) peers.Add(peer);
                peer.Start();
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { errors.Enqueue(ex); }
    }

    public bool TryDequeue(out InboundEnvelope? envelope) => inbox.TryDequeue(out envelope);
    public bool TryDequeueError(out Exception? exception) => errors.TryDequeue(out exception);
    public Task SendAsync(IRemotePeer peer, MessageType type, byte[] payload) => peer.SendAsync(new Envelope { Type = type, Payload = payload });

    public async Task BroadcastAsync(MessageType type, byte[] payload)
    {
        PeerConnection[] copy;
        lock (peerLock) copy = peers.ToArray();
        foreach (var peer in copy) if (peer.IsConnected) await SendAsync(peer, type, payload).ConfigureAwait(false);
    }

    public void Dispose()
    {
        stop.Cancel(); listener.Stop();
        lock (peerLock) { foreach (var peer in peers) peer.Dispose(); peers.Clear(); }
        stop.Dispose();
    }
}
