using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using XingyiStarry.Mp.Infrastructure;
using XingyiStarry.Mp.Protocol;
using XingyiStarry.Mp.Session;

namespace XingyiStarry.Mp.Tests;

internal static class LifecycleTests
{
    public static void ConsecutiveMatches()
    {
        var host = Guid.NewGuid(); var guest = Guid.NewGuid(); var spectator = Guid.NewGuid();
        var room = new RoomState(); room.AddHost(host, "host");
        Require(room.TryAddParticipant(guest, "guest", out _));
        Require(room.TryAddParticipant(spectator, "spectator", out _));
        var seat = new SeatInfo { SeatId = Guid.NewGuid(), LobbySlotIndex = 0, PlayerIndex = 0, OriginallyHuman = true };
        var aiSeat = new SeatInfo { SeatId = Guid.NewGuid(), LobbySlotIndex = 1, PlayerIndex = 1, Controller = 3 };
        room.SyncDraft("map", "Map", 0, 0, 0, 30, false, new byte[] { 1 }, "draft", new[] { seat, aiSeat });
        Require(room.TryClaimSeat(host, "host", 0, out _, out _));
        foreach (var id in new[] { host, guest, spectator }) room.SetReady(id, true);
        room.Start(); Require(!room.CanStart);
        room.SetDefeated(0, true);
        var newcomer = Guid.NewGuid(); Require(room.TryAddParticipant(newcomer, "new", out _));
        Require(room.TryClaimSeat(newcomer, "new", 1, out _, out _));
        room.ReturnToLobby();
        Require(room.RoomId != Guid.Empty && !room.MatchStarted && !room.CanStart);
        Require(room.Seats[0].ClientId == host && room.Seats[0].SeatId == seat.SeatId && !room.Seats[0].Defeated);
        Require(room.Participants.Count == 4 && room.Participants.All(p => p.Admission == ParticipantAdmission.Lobby && !p.Ready));
        Require(aiSeat.ClientId == newcomer && !aiSeat.PendingActivation && !aiSeat.AiControlled && aiSeat.OriginallyHuman && aiSeat.Controller == 0);
        room.SetReady(host, true); room.SetReady(guest, true); Require(!room.CanStart);
        room.SetReady(spectator, true); Require(!room.CanStart);
        room.SetReady(newcomer, true); Require(room.CanStart);
        room.Start(); Require(room.FindParticipant(spectator)!.Admission == ParticipantAdmission.Spectator);
        room.ReturnToLobby(); Require(!room.CanStart);
    }

    public static void SavedRoomReset()
    {
        var host = Guid.NewGuid(); var room = new RoomState(); room.AddHost(host, "host");
        var alive = new SeatInfo { SeatId = Guid.NewGuid(), LobbySlotIndex = 0, PlayerIndex = 0 };
        var defeated = new SeatInfo { SeatId = Guid.NewGuid(), LobbySlotIndex = 1, PlayerIndex = 1, Defeated = true };
        room.ConfigureSavedGame("save", "Save", 0, 0, 0, 30, new[] { alive, defeated });
        Require(room.TryClaimSeat(host, "host", 0, out _, out _)); room.SetReady(host, true); room.Start();
        room.SetDefeated(0, true); room.SetDefeated(1, false); room.ReturnToLobby();
        Require(!alive.Defeated && defeated.Defeated && room.SavedGame && alive.ClientId == host);
        room.SetReady(host, true); Require(room.CanStart);
    }

    public static void ClientReturnsIndependently()
    {
        var transport = new FakeClientTransport(); using var client = new ClientSession(transport);
        var id = Guid.NewGuid(); var roomId = Guid.NewGuid(); var firstMatch = Guid.NewGuid();
        transport.Add(Messages.Welcome(ProtocolCodec.EncodeWelcome(new WelcomeMessage { ClientId = id, RoomId = roomId, Mode = WelcomeMode.Lobby })));
        transport.Add(Messages.MatchStarting(new XingyiStarry.Mp.Protocol.Wire.GuidValue()));
        transport.Add(Messages.RoomState(ProtocolCodec.EncodeRoom(new RoomSnapshot { RoomId = roomId, MatchId = firstMatch, MatchStarted = true })));
        Pump(client); Require(client.JoinedMatch && client.MatchStarting);
        client.AcceptSnapshotAnchor(new SnapshotManifest { MatchId = firstMatch, FrameHash = new byte[32] });
        // The host is already in the lobby while this client is still replaying the old game.
        transport.Add(Messages.RoomState(ProtocolCodec.EncodeRoom(new RoomSnapshot { RoomId = roomId })));
        var end = new AuthorityHashChain(firstMatch).Append(AuthorityMessages.MatchEnded(ProtocolCodec.EncodeMatchEnded(true)));
        transport.Add(Messages.AuthorityFrame(ProtocolCodec.EncodeAuthorityFrame(end)));
        var frames = new List<AuthorityFrame>(); Pump(client, frames.Add);
        Require(frames.Count == 1 && client.MatchId == firstMatch && client.JoinedMatch);
        client.MarkApplied(end.FrameId); client.MarkMatchEnded();
        Require(!client.TerminalSessionEnded && !client.CanFastReconnect);
        // Closing the local summary is independent of the host and sends no return message.
        var sentBeforeReturn = transport.Sent.Count;
        client.ReturnToLobby();
        Require(transport.Sent.Count == sentBeforeReturn && client.ClientId == id && client.MatchId == null && !client.JoinedMatch);
        Require(!client.MatchStarting && client.AppliedFrameId == 0 && client.VerifiedFrameId == 0);
        transport.Add(Messages.AuthorityFrame(ProtocolCodec.EncodeAuthorityFrame(end)));
        transport.Add(Messages.SnapshotManifest(ProtocolCodec.EncodeSnapshotManifest(new SnapshotManifest
            { SnapshotId = Guid.NewGuid(), MatchId = firstMatch, FrameHash = new byte[32], ContentHash = new byte[32] })));
        Pump(client); Require(client.MatchId == null && !client.SnapshotRequested);
        var secondMatch = Guid.NewGuid();
        transport.Add(Messages.MatchStarting(new XingyiStarry.Mp.Protocol.Wire.GuidValue()));
        transport.Add(Messages.RoomState(ProtocolCodec.EncodeRoom(new RoomSnapshot { RoomId = roomId, MatchId = secondMatch, MatchStarted = true })));
        Pump(client);
        Require(client.MatchId == secondMatch && client.JoinedMatch && !client.MatchEnded && client.SnapshotRequested);
        client.AcceptSnapshotAnchor(new SnapshotManifest { MatchId = secondMatch, FrameHash = new byte[32] });
        var start = new AuthorityHashChain(secondMatch).Append(AuthorityMessages.TurnPhase(ProtocolCodec.EncodeTurnPhase((TurnPhase)3)));
        transport.Add(Messages.AuthorityFrame(ProtocolCodec.EncodeAuthorityFrame(start)));
        Pump(client, frame => client.MarkApplied(frame.FrameId)); Require(client.AppliedFrameId == 1);
        transport.Add(Messages.SessionEnded(ProtocolCodec.EncodeString("closed"))); Pump(client);
        Require(client.TerminalSessionEnded && client.ConnectionLost);
    }

    public static void AcceptedSpectatorWaitsForSnapshot()
    {
        var transport = new FakeClientTransport(); using var client = new ClientSession(transport);
        var id = Guid.NewGuid(); var roomId = Guid.NewGuid(); var matchId = Guid.NewGuid();
        var room = ProtocolCodec.EncodeRoom(new RoomSnapshot { RoomId = roomId, MatchId = matchId, MatchStarted = true });
        transport.Add(Messages.Welcome(ProtocolCodec.EncodeWelcome(new WelcomeMessage
            { ClientId = id, RoomId = roomId, MatchId = matchId, Mode = WelcomeMode.JoinSelection })));
        transport.Add(Messages.RoomState(room)); Pump(client); Require(!client.JoinedMatch);
        transport.Add(Messages.JoinMatchAccepted(ProtocolCodec.EncodeJoinMatchAccepted(new JoinMatchAccepted { Spectator = true })));
        transport.Add(Messages.RoomState(room));
        Pump(client); Require(client.JoinedMatch && client.IsSpectator && client.SnapshotRequested);
    }

    private static void Pump(ClientSession client, Action<AuthorityFrame>? frame = null) => client.Pump(
        (level, message) => { if (level == SessionLogLevel.Warning) throw new InvalidOperationException(message); },
        (_, _) => { }, frame ?? (_ => { }), (_, _) => throw new InvalidOperationException("Unexpected snapshot load."), _ => { });
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Lifecycle assertion failed."); }

    private sealed class FakeClientTransport : IClientTransport
    {
        private readonly Queue<Envelope> incoming = new Queue<Envelope>();
        public List<Envelope> Sent { get; } = new List<Envelope>();
        public bool IsConnected => true;
        public void Add(NetworkMessage message) => incoming.Enqueue(new Envelope { Message = message });
        public Task ConnectAsync(string host, int port) => Task.CompletedTask;
        public Task ReconnectAsync(string host, int port, Guid roomId, Guid clientId, Guid matchId, ulong requestId) => Task.CompletedTask;
        public bool TryDequeue(out Envelope? envelope) => incoming.TryDequeue(out envelope);
        public bool TryDequeueError(out Exception? exception) { exception = null; return false; }
        public Task SendAsync(NetworkMessage message) { Sent.Add(new Envelope { Message = message }); return Task.CompletedTask; }
        public void Dispose() { }
    }
}
