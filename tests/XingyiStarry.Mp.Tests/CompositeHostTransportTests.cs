using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Tests
{
    using XingyiStarry.Mp.Infrastructure;

    internal static class CompositeHostTransportTests
    {
        public static async Task Run()
        {
            var direct = new FakeHostTransport();
            var relay = new FakeHostTransport();
            var directPeer = new FakePeer();
            var relayPeer = new FakePeer();
            direct.Enqueue(directPeer);
            relay.Enqueue(relayPeer);

            using var composite = new CompositeHostTransport(direct, relay);
            composite.Start();
            Require(direct.StartCount == 1 && relay.StartCount == 1, "all transports should start");
            Require(composite.TryDequeue(out var first) && first?.Peer == directPeer, "direct inbox should be visible");
            Require(composite.TryDequeue(out var second) && second?.Peer == relayPeer, "relay inbox should be visible");

            await composite.SendAsync(relayPeer, Messages.Welcome(new XingyiStarry.Mp.Protocol.Wire.Welcome()));
            Require(relayPeer.SendCount == 1 && directPeer.SendCount == 0, "single-peer send should stay on its peer");

            await composite.BroadcastAsync(Messages.RoomState(new XingyiStarry.Mp.Protocol.Wire.Room()));
            Require(direct.BroadcastCount == 1 && relay.BroadcastCount == 1, "broadcast should reach all transports");

            await composite.UpdateRoomAsync(new RelayUpdateRoomRequest { RoomId = Guid.NewGuid(), MaxParticipants = 4 });
            Require(direct.UpdateCount == 1 && relay.UpdateCount == 1, "directory updates should reach all publishers");

            relay.FailBroadcast = true;
            await composite.BroadcastAsync(Messages.SessionEnded(new XingyiStarry.Mp.Protocol.Wire.StringValue()));
            Require(direct.BroadcastCount == 2, "one failed transport must not block another transport");
            Require(composite.TryDequeueError(out var error) && error?.Message.Contains(nameof(FakeHostTransport), StringComparison.Ordinal) == true,
                "isolated transport failure should be observable");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class FakeHostTransport : IHostTransport, IRoomDirectoryPublisher
        {
            private readonly ConcurrentQueue<InboundEnvelope> inbox = new ConcurrentQueue<InboundEnvelope>();
            public int StartCount { get; private set; }
            public int BroadcastCount { get; private set; }
            public int UpdateCount { get; private set; }
            public bool FailBroadcast { get; set; }

            public void Enqueue(IRemotePeer peer) => inbox.Enqueue(new InboundEnvelope(peer, new Envelope { Message = Messages.Hello(new XingyiStarry.Mp.Protocol.Wire.Hello()) }));
            public void Start() => StartCount++;
            public bool TryDequeue(out InboundEnvelope? envelope) => inbox.TryDequeue(out envelope);
            public bool TryDequeueError(out Exception? exception) { exception = null; return false; }
            public Task SendAsync(IRemotePeer peer, NetworkMessage message) => peer.SendAsync(new Envelope { Message = message });
            public Task BroadcastAsync(NetworkMessage message)
            {
                BroadcastCount++;
                return FailBroadcast ? Task.FromException(new InvalidOperationException("synthetic failure")) : Task.CompletedTask;
            }
            public Task UpdateRoomAsync(RelayUpdateRoomRequest update) { UpdateCount++; return Task.CompletedTask; }
            public Task CloseJoiningAsync(ulong requestId) => Task.CompletedTask;
            public void Dispose() { }
        }

        private sealed class FakePeer : IRemotePeer
        {
            public Guid ConnectionId { get; } = Guid.NewGuid();
            public bool IsConnected => true;
            public DateTime LastReceivedUtc => DateTime.UtcNow;
            public int SendCount { get; private set; }
            public Task SendAsync(Envelope envelope) { SendCount++; return Task.CompletedTask; }
            public void Close() { }
        }
    }
}
