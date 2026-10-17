using System;
using System.Collections.Generic;
using System.Linq;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Session;

internal sealed class HostSocialState
{
    private readonly List<StoredChat> chats = new List<StoredChat>();
    private ulong nextSeq;

    public ulong Watermark => nextSeq;

    public ChatEvent CreatePlayerChat(RoomState room, Guid clientId, ChatSend request, out IReadOnlyCollection<Guid> audience)
    {
        var participant = room.FindParticipant(clientId) ?? throw new InvalidOperationException("Participant does not exist.");
        var text = Sanitize(request.Text);
        if (text.Length == 0 || text.Length > 512) throw new InvalidOperationException("Chat messages must contain 1–512 characters.");
        var seat = room.Seats.FirstOrDefault(value => value.ClientId == clientId && value.Connected);
        if (request.Channel == ChatChannel.Team && (!room.MatchStarted || participant.Admission != ParticipantAdmission.Player || seat is null))
            throw new InvalidOperationException("Only public chat is available right now.");
        audience = request.Channel == ChatChannel.Team
            ? room.Participants.Where(value => value.Connected && value.Admission == ParticipantAdmission.Player &&
                room.Seats.Any(seatValue => seatValue.ClientId == value.ClientId && seatValue.Team == seat!.Team)).Select(value => value.ClientId).ToArray()
            : room.Participants.Where(value => value.Connected).Select(value => value.ClientId).ToArray();
        var message = new ChatEvent
        {
            SocialSeq = ++nextSeq, ServerTicks = DateTime.UtcNow.Ticks, Channel = request.Channel, Kind = ChatKind.Player,
            SenderClientId = clientId, SenderName = participant.DisplayName, SenderTeam = seat?.Team ?? -1,
            SenderColor = seat?.Color ?? -1, SenderIsSpectator = participant.Admission == ParticipantAdmission.Spectator, Text = text
        };
        chats.Add(new StoredChat(message, request.Channel == ChatChannel.Team ? new HashSet<Guid>(audience) : null));
        return message;
    }

    public ChatEvent CreateSystem(RoomState room, SystemEventKind kind, string text, out IReadOnlyCollection<Guid> audience)
    {
        audience = room.Participants.Where(value => value.Connected).Select(value => value.ClientId).ToArray();
        var message = new ChatEvent { SocialSeq = ++nextSeq, ServerTicks = DateTime.UtcNow.Ticks,
            Channel = ChatChannel.Public, Kind = ChatKind.System, SystemKind = kind, Text = Sanitize(text) };
        chats.Add(new StoredChat(message, null)); return message;
    }

    public PingEvent CreatePing(RoomState room, Guid clientId, PingSend request, out IReadOnlyCollection<Guid> audience)
    {
        var participant = room.FindParticipant(clientId) ?? throw new InvalidOperationException("Participant does not exist.");
        var seat = room.Seats.FirstOrDefault(value => value.ClientId == clientId && value.Connected);
        if (!room.MatchStarted || participant.Admission != ParticipantAdmission.Player || seat is null)
            throw new InvalidOperationException("Only active players can send pings.");
        audience = room.Participants.Where(value => value.Connected && value.Admission == ParticipantAdmission.Player &&
            room.Seats.Any(candidate => candidate.ClientId == value.ClientId && candidate.Team == seat.Team)).Select(value => value.ClientId).ToArray();
        var message = new PingEvent { SenderClientId = clientId, SenderName = participant.DisplayName,
            SenderTeam = seat.Team, SenderColor = seat.Color, TileX = request.TileX, TileY = request.TileY, RemainingTtlMillis = 3000 };
        return message;
    }

    public IReadOnlyList<ChatEvent> ChatsFor(Guid clientId) => chats
        .Where(value => value.Audience is null || value.Audience.Contains(clientId)).Select(value => value.Message).ToArray();

    private static string Sanitize(string value) => new string((value ?? "").Trim().Where(character => character == '\t' || character >= ' ').ToArray());

    private sealed class StoredChat
    {
        public ChatEvent Message { get; }
        public HashSet<Guid>? Audience { get; }
        public StoredChat(ChatEvent message, HashSet<Guid>? audience) { Message = message; Audience = audience; }
    }
}
