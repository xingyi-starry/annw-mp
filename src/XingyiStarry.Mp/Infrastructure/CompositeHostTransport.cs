using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Infrastructure;

internal sealed class CompositeHostTransport : IHostTransport, IRoomDirectoryPublisher
{
    private readonly IHostTransport[] transports;
    private readonly ConcurrentQueue<Exception> errors = new ConcurrentQueue<Exception>();
    private int nextInbox;
    private int nextError;

    public CompositeHostTransport(params IHostTransport[] transports)
    {
        if (transports is null || transports.Length == 0) throw new ArgumentException("At least one host transport is required.", nameof(transports));
        if (transports.Any(value => value is null)) throw new ArgumentException("Host transports cannot contain null.", nameof(transports));
        this.transports = transports.ToArray();
    }

    public void Start()
    {
        foreach (var transport in transports) transport.Start();
    }

    public bool TryDequeue(out InboundEnvelope? envelope)
    {
        for (var offset = 0; offset < transports.Length; offset++)
        {
            var index = (nextInbox + offset) % transports.Length;
            if (!transports[index].TryDequeue(out envelope)) continue;
            nextInbox = (index + 1) % transports.Length;
            return true;
        }
        envelope = null;
        return false;
    }

    public bool TryDequeueError(out Exception? exception)
    {
        if (errors.TryDequeue(out exception)) return true;
        for (var offset = 0; offset < transports.Length; offset++)
        {
            var index = (nextError + offset) % transports.Length;
            if (!transports[index].TryDequeueError(out var inner)) continue;
            nextError = (index + 1) % transports.Length;
            exception = new InvalidOperationException(transports[index].GetType().Name + ": " + inner?.Message, inner);
            return true;
        }
        exception = null;
        return false;
    }

    public Task SendAsync(IRemotePeer peer, MessageType type, byte[] payload) =>
        peer.SendAsync(new Envelope { Type = type, Payload = payload });

    public Task BroadcastAsync(MessageType type, byte[] payload) => RunAllAsync(
        transports.Select(transport => (transport, (Func<Task>)(() => transport.BroadcastAsync(type, payload)))));

    public Task UpdateRoomAsync(RelayUpdateRoomRequest update) => RunAllAsync(
        transports.OfType<IRoomDirectoryPublisher>()
            .Select(publisher => ((IHostTransport)publisher, (Func<Task>)(() => publisher.UpdateRoomAsync(update)))));

    public Task CloseJoiningAsync(ulong requestId) => RunAllAsync(
        transports.OfType<IRoomDirectoryPublisher>()
            .Select(publisher => ((IHostTransport)publisher, (Func<Task>)(() => publisher.CloseJoiningAsync(requestId)))));

    private Task RunAllAsync(IEnumerable<(IHostTransport Transport, Func<Task> Operation)> operations) =>
        Task.WhenAll(operations.Select(value => CaptureAsync(value.Transport, value.Operation)));

    private async Task CaptureAsync(IHostTransport transport, Func<Task> operation)
    {
        try { await operation().ConfigureAwait(false); }
        catch (Exception ex) { errors.Enqueue(new InvalidOperationException(transport.GetType().Name + ": " + ex.Message, ex)); }
    }

    public void Dispose()
    {
        foreach (var transport in transports)
        {
            try { transport.Dispose(); }
            catch (Exception ex) { errors.Enqueue(new InvalidOperationException(transport.GetType().Name + ": " + ex.Message, ex)); }
        }
    }
}
