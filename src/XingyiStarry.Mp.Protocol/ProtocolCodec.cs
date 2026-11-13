using System;
using System.IO;
using System.Linq;
using Google.Protobuf;
using Wire = XingyiStarry.Mp.Protocol.Wire;

namespace XingyiStarry.Mp.Protocol;

public static class ProtocolCodec
{
    private const int MaxSeats = 64;
    private const int MaxUnits = 4096;

    public static byte[] EncodeEnvelope(Envelope value)
    {
        var wire = value.Message.Body.Clone();
        wire.Delivery = (Wire.Delivery)value.Delivery;
        wire.ClearRoomId(); wire.ClearClientId(); wire.ClearTargetClientId();
        if (value.RoomId.HasValue) wire.RoomId = GuidBytes(value.RoomId.Value);
        if (value.ClientId.HasValue) wire.ClientId = GuidBytes(value.ClientId.Value);
        if (value.TargetClientId.HasValue) wire.TargetClientId = GuidBytes(value.TargetClientId.Value);
        return wire.ToByteArray();
    }

    public static Envelope DecodeEnvelope(byte[] bytes)
    {
        var wire = Wire.WireEnvelope.Parser.ParseFrom(bytes);
        var delivery = (Delivery)wire.Delivery;
        if (!Enum.IsDefined(typeof(Delivery), delivery)) throw new InvalidDataException("Unknown delivery mode.");
        return new Envelope { Message = new NetworkMessage(wire), Delivery = delivery,
            RoomId = wire.HasRoomId ? ReadGuid(wire.RoomId) : null,
            ClientId = wire.HasClientId ? ReadGuid(wire.ClientId) : null,
            TargetClientId = wire.HasTargetClientId ? ReadGuid(wire.TargetClientId) : null };
    }

    public static Wire.Hello EncodeHello(HelloMessage value) => Serialize(new Wire.Hello
    {
        ProtocolVersion = value.ProtocolVersion, PluginVersion = value.PluginVersion,
        GameFingerprint = value.GameFingerprint, ContentFingerprint = value.ContentFingerprint,
        DisplayName = value.DisplayName, Password = value.Password
    });

    public static HelloMessage DecodeHello(IMessage bytes)
    {
        var wire = Parse(Wire.Hello.Parser, bytes);
        return new HelloMessage { ProtocolVersion = CheckedUShort(wire.ProtocolVersion), PluginVersion = wire.PluginVersion,
            GameFingerprint = wire.GameFingerprint, ContentFingerprint = wire.ContentFingerprint,
            DisplayName = wire.DisplayName, Password = wire.Password };
    }

    public static Wire.Welcome EncodeWelcome(WelcomeMessage value)
    {
        var wire = new Wire.Welcome { ClientId = GuidBytes(value.ClientId), RoomId = GuidBytes(value.RoomId), LatestFrameId = value.LatestFrameId, Mode = (Wire.WelcomeMode)value.Mode };
        if (value.MatchId.HasValue) wire.MatchId = GuidBytes(value.MatchId.Value);
        return Serialize(wire);
    }

    public static WelcomeMessage DecodeWelcome(IMessage bytes)
    {
        var wire = Parse(Wire.Welcome.Parser, bytes);
        var mode = (WelcomeMode)wire.Mode;
        if (!Enum.IsDefined(typeof(WelcomeMode), mode)) throw new InvalidDataException("Unknown welcome mode.");
        return new WelcomeMessage { ClientId = ReadGuid(wire.ClientId), RoomId = ReadGuid(wire.RoomId),
            MatchId = wire.HasMatchId ? ReadGuid(wire.MatchId) : null, LatestFrameId = wire.LatestFrameId, Mode = mode };
    }

    public static Wire.Room EncodeRoom(RoomSnapshot value)
    {
        if (value.Seats.Count > MaxSeats) throw new InvalidDataException("Invalid seat count.");
        if (value.MapPreview.Length > ProtocolConstants.MaxMapPreviewBytes) throw new InvalidDataException("Map preview is too large.");
        if (value.Participants.Count > 8) throw new InvalidDataException("Invalid participant count.");
        if (value.MaxParticipants < 1 || value.MaxParticipants > 8 || value.Participants.Count > value.MaxParticipants)
            throw new InvalidDataException("Invalid participant limit.");
        var wire = new Wire.Room { RoomId = GuidBytes(value.RoomId), MatchStarted = value.MatchStarted,
            DraftRevision = value.DraftRevision, MapId = value.MapId, MapTitle = value.MapTitle,
            FowType = value.FowType, WinCondition = value.WinCondition, QuickStart = value.QuickStart, Difficulty = value.Difficulty, SavedGame = value.SavedGame,
            UserMap = value.UserMap, MapPreview = ByteString.CopyFrom(value.MapPreview), MaxParticipants = value.MaxParticipants,
            ParticipantRevision = value.ParticipantRevision };
        if (value.MatchId.HasValue) wire.MatchId = GuidBytes(value.MatchId.Value);
        foreach (var seat in value.Seats)
        {
            if (seat.PassiveIds.Count > MaxSeats) throw new InvalidDataException("Invalid commander passive count.");
            var item = new Wire.Seat { SeatId = GuidBytes(seat.SeatId), LobbySlotIndex = seat.LobbySlotIndex,
                PlayerIndex = seat.PlayerIndex, DisplayName = seat.DisplayName, OriginallyHuman = seat.OriginallyHuman,
                Connected = seat.Connected, Ready = seat.Ready, AiControlled = seat.AiControlled,
                Controller = seat.Controller, Team = seat.Team, Color = seat.Color, Position = seat.Position,
                PositionRandom = seat.PositionRandom, ResourceMultiplier = seat.ResourceMultiplier,
                AiIntelligence = seat.AiIntelligence, CommanderId = seat.CommanderId,
                CommanderMode = seat.CommanderMode, SkillId = seat.SkillId, Defeated = seat.Defeated,
                PendingActivation = seat.PendingActivation, KeepMapCommander = seat.KeepMapCommander };
            if (seat.ClientId.HasValue) item.ClientId = GuidBytes(seat.ClientId.Value);
            item.PassiveIds.Add(seat.PassiveIds);
            wire.Seats.Add(item);
        }
        wire.Participants.Add(value.Participants.Select(ToWire));
        return Serialize(wire);
    }

    public static RoomSnapshot DecodeRoom(IMessage bytes)
    {
        var wire = Parse(Wire.Room.Parser, bytes);
        if (wire.Seats.Count > MaxSeats) throw new InvalidDataException("Invalid seat count.");
        if (wire.Participants.Count > 8) throw new InvalidDataException("Invalid participant count.");
        if (wire.MapPreview.Length > ProtocolConstants.MaxMapPreviewBytes) throw new InvalidDataException("Map preview is too large.");
        if (wire.MaxParticipants < 1 || wire.MaxParticipants > 8 || wire.Participants.Count > wire.MaxParticipants)
            throw new InvalidDataException("Invalid participant limit.");
        var value = new RoomSnapshot { RoomId = ReadGuid(wire.RoomId), MatchId = wire.HasMatchId ? ReadGuid(wire.MatchId) : null,
            MatchStarted = wire.MatchStarted, DraftRevision = wire.DraftRevision, MapId = wire.MapId,
            MapTitle = wire.MapTitle, FowType = wire.FowType, WinCondition = wire.WinCondition, QuickStart = wire.QuickStart, Difficulty = wire.Difficulty,
            SavedGame = wire.SavedGame, UserMap = wire.UserMap, MapPreview = wire.MapPreview.ToByteArray(),
            MaxParticipants = wire.MaxParticipants, ParticipantRevision = wire.ParticipantRevision };
        foreach (var seat in wire.Seats)
        {
            if (seat.PassiveIds.Count > MaxSeats) throw new InvalidDataException("Invalid commander passive count.");
            var item = new SeatInfo { SeatId = ReadGuid(seat.SeatId), LobbySlotIndex = seat.LobbySlotIndex,
                PlayerIndex = seat.PlayerIndex, DisplayName = seat.DisplayName, OriginallyHuman = seat.OriginallyHuman,
                Connected = seat.Connected, Ready = seat.Ready, AiControlled = seat.AiControlled,
                ClientId = seat.HasClientId ? ReadGuid(seat.ClientId) : null, Controller = seat.Controller,
                Team = seat.Team, Color = seat.Color, Position = seat.Position, PositionRandom = seat.PositionRandom,
                ResourceMultiplier = seat.ResourceMultiplier, AiIntelligence = seat.AiIntelligence,
                CommanderId = seat.CommanderId, CommanderMode = seat.CommanderMode, SkillId = seat.SkillId,
                Defeated = seat.Defeated, PendingActivation = seat.PendingActivation, KeepMapCommander = seat.KeepMapCommander };
            item.PassiveIds.AddRange(seat.PassiveIds);
            value.Seats.Add(item);
        }
        value.Participants.AddRange(wire.Participants.Select(FromWire));
        return value;
    }

    public static Wire.GameCommandMessage EncodeCommand(GameCommand value) => Serialize(ToWire(value));
    public static GameCommand DecodeCommand(IMessage bytes) => FromWire(Parse(Wire.GameCommandMessage.Parser, bytes));

    public static Wire.CommandRequestMessage EncodeCommandRequest(CommandRequest value) => Serialize(new Wire.CommandRequestMessage
    {
        RequestId = value.RequestId, SeatId = GuidBytes(value.SeatId),
        Round = value.Round, AppliedFrameId = value.AppliedFrameId, Command = ToWire(value.Command)
    });

    public static CommandRequest DecodeCommandRequest(IMessage bytes)
    {
        var wire = Parse(Wire.CommandRequestMessage.Parser, bytes);
        if (wire.Command is null) throw new InvalidDataException("Command request has no command.");
        return new CommandRequest { RequestId = wire.RequestId,
            SeatId = ReadGuid(wire.SeatId), Round = wire.Round, AppliedFrameId = wire.AppliedFrameId,
            Command = FromWire(wire.Command) };
    }

    public static Wire.RelayRegisterRoomRequest EncodeRelayRegisterRoom(RelayRegisterRoomRequest value) => Serialize(new Wire.RelayRegisterRoomRequest
    {
        RequestId = value.RequestId, RoomId = GuidBytes(value.RoomId), RoomName = value.RoomName,
        HostName = value.HostName, Password = value.Password, PluginVersion = value.PluginVersion,
        GameFingerprint = value.GameFingerprint, ContentFingerprint = value.ContentFingerprint,
        MaxParticipants = value.MaxParticipants
    });

    public static RelayRegisterRoomRequest DecodeRelayRegisterRoom(IMessage bytes)
    {
        var wire = Parse(Wire.RelayRegisterRoomRequest.Parser, bytes);
        return new RelayRegisterRoomRequest { RequestId = wire.RequestId, RoomId = ReadGuid(wire.RoomId),
            RoomName = wire.RoomName, HostName = wire.HostName, Password = wire.Password,
            PluginVersion = wire.PluginVersion, GameFingerprint = wire.GameFingerprint,
            ContentFingerprint = wire.ContentFingerprint, MaxParticipants = wire.MaxParticipants };
    }

    public static Wire.RelayUpdateRoomRequest EncodeRelayUpdateRoom(RelayUpdateRoomRequest value)
    {
        var wire = new Wire.RelayUpdateRoomRequest { RequestId = value.RequestId, RoomId = GuidBytes(value.RoomId), MapTitle = value.MapTitle,
            ConnectedPlayers = value.ConnectedPlayers, HumanSeats = value.HumanSeats, Status = (Wire.RelayRoomStatus)value.Status,
            AvailableSeats = value.AvailableSeats, MaxParticipants = value.MaxParticipants };
        if (value.MatchId.HasValue) wire.MatchId = GuidBytes(value.MatchId.Value);
        return Serialize(wire);
    }

    public static RelayUpdateRoomRequest DecodeRelayUpdateRoom(IMessage bytes)
    {
        var wire = Parse(Wire.RelayUpdateRoomRequest.Parser, bytes);
        var status = CheckedRelayRoomStatus(wire.Status);
        return new RelayUpdateRoomRequest { RequestId = wire.RequestId, RoomId = ReadGuid(wire.RoomId),
            MapTitle = wire.MapTitle, ConnectedPlayers = wire.ConnectedPlayers, HumanSeats = wire.HumanSeats, Status = status,
            AvailableSeats = wire.AvailableSeats, MatchId = wire.HasMatchId ? ReadGuid(wire.MatchId) : null,
            MaxParticipants = wire.MaxParticipants };
    }

    public static Wire.RelayListRoomsRequest EncodeRelayListRoomsRequest(ulong requestId) => Serialize(new Wire.RelayListRoomsRequest { RequestId = requestId });
    public static ulong DecodeRelayListRoomsRequest(IMessage bytes) => Parse(Wire.RelayListRoomsRequest.Parser, bytes).RequestId;

    public static Wire.RelayListRoomsResponse EncodeRelayRoomList(RelayListRoomsResponse value)
    {
        if (value.Rooms.Count > 4096) throw new InvalidDataException("Invalid relay room count.");
        var wire = new Wire.RelayListRoomsResponse { RequestId = value.RequestId };
        wire.Rooms.Add(value.Rooms.Select(ToWire));
        return Serialize(wire);
    }

    public static RelayListRoomsResponse DecodeRelayRoomList(IMessage bytes)
    {
        var wire = Parse(Wire.RelayListRoomsResponse.Parser, bytes);
        if (wire.Rooms.Count > 4096) throw new InvalidDataException("Invalid relay room count.");
        var value = new RelayListRoomsResponse { RequestId = wire.RequestId };
        value.Rooms.AddRange(wire.Rooms.Select(FromWire));
        return value;
    }

    public static Wire.RelayJoinRoomRequest EncodeRelayJoinRoom(RelayJoinRoomRequest value) => Serialize(new Wire.RelayJoinRoomRequest
        { RequestId = value.RequestId, RoomId = GuidBytes(value.RoomId), Password = value.Password });

    public static RelayJoinRoomRequest DecodeRelayJoinRoom(IMessage bytes)
    {
        var wire = Parse(Wire.RelayJoinRoomRequest.Parser, bytes);
        return new RelayJoinRoomRequest { RequestId = wire.RequestId, RoomId = ReadGuid(wire.RoomId), Password = wire.Password };
    }

    public static Wire.RelayResumeRoomRequest EncodeRelayResumeRoom(RelayResumeRoomRequest value) => Serialize(new Wire.RelayResumeRoomRequest
        { RequestId = value.RequestId, RoomId = GuidBytes(value.RoomId), ClientId = GuidBytes(value.ClientId), MatchId = GuidBytes(value.MatchId) });

    public static RelayResumeRoomRequest DecodeRelayResumeRoom(IMessage bytes)
    {
        var wire = Parse(Wire.RelayResumeRoomRequest.Parser, bytes);
        return new RelayResumeRoomRequest { RequestId = wire.RequestId, RoomId = ReadGuid(wire.RoomId),
            ClientId = ReadGuid(wire.ClientId), MatchId = ReadGuid(wire.MatchId) };
    }

    public static Wire.RelayControlResponse EncodeRelayControlResponse(RelayControlResponse value)
    {
        var wire = new Wire.RelayControlResponse { RequestId = value.RequestId, Success = value.Success, Reason = value.Reason };
        if (value.ClientId.HasValue) wire.ClientId = GuidBytes(value.ClientId.Value);
        return Serialize(wire);
    }

    public static RelayControlResponse DecodeRelayControlResponse(IMessage bytes)
    {
        var wire = Parse(Wire.RelayControlResponse.Parser, bytes);
        return new RelayControlResponse { RequestId = wire.RequestId, Success = wire.Success, Reason = wire.Reason,
            ClientId = wire.HasClientId ? ReadGuid(wire.ClientId) : null };
    }

    public static Wire.RelayRoomRequest EncodeRelayRoomRequest(RelayRoomRequest value) => Serialize(new Wire.RelayRoomRequest
        { RequestId = value.RequestId, RoomId = GuidBytes(value.RoomId) });

    public static RelayRoomRequest DecodeRelayRoomRequest(IMessage bytes)
    {
        var wire = Parse(Wire.RelayRoomRequest.Parser, bytes);
        return new RelayRoomRequest { RequestId = wire.RequestId, RoomId = ReadGuid(wire.RoomId) };
    }

    public static Wire.RelayPeerNotice EncodeRelayPeerNotice(RelayPeerNotice value) => Serialize(new Wire.RelayPeerNotice
        { ClientId = GuidBytes(value.ClientId), Reason = value.Reason });

    public static RelayPeerNotice DecodeRelayPeerNotice(IMessage bytes)
    {
        var wire = Parse(Wire.RelayPeerNotice.Parser, bytes);
        return new RelayPeerNotice { ClientId = ReadGuid(wire.ClientId), Reason = wire.Reason };
    }

    public static Wire.JoinMatchRequest EncodeJoinMatchRequest(JoinMatchRequest value)
    {
        var wire = new Wire.JoinMatchRequest();
        if (value.SeatId.HasValue) wire.SeatId = GuidBytes(value.SeatId.Value);
        return Serialize(wire);
    }
    public static JoinMatchRequest DecodeJoinMatchRequest(IMessage bytes)
    {
        var wire = Parse(Wire.JoinMatchRequest.Parser, bytes);
        return new JoinMatchRequest { SeatId = wire.HasSeatId ? ReadGuid(wire.SeatId) : null };
    }

    public static Wire.JoinMatchAcceptedMessage EncodeJoinMatchAccepted(JoinMatchAccepted value)
    {
        var wire = new Wire.JoinMatchAcceptedMessage { Spectator = value.Spectator };
        if (value.SeatId.HasValue) wire.SeatId = GuidBytes(value.SeatId.Value);
        return Serialize(wire);
    }
    public static JoinMatchAccepted DecodeJoinMatchAccepted(IMessage bytes)
    {
        var wire = Parse(Wire.JoinMatchAcceptedMessage.Parser, bytes);
        return new JoinMatchAccepted { SeatId = wire.HasSeatId ? ReadGuid(wire.SeatId) : null, Spectator = wire.Spectator };
    }

    public static Wire.ChatSendMessage EncodeChatSend(ChatSend value) => Serialize(new Wire.ChatSendMessage
        { Channel = (Wire.ChatChannel)value.Channel, Text = value.Text });
    public static ChatSend DecodeChatSend(IMessage bytes)
    {
        var wire = Parse(Wire.ChatSendMessage.Parser, bytes);
        return new ChatSend { Channel = CheckedChatChannel(wire.Channel), Text = wire.Text };
    }
    public static Wire.ChatEventMessage EncodeChatEvent(ChatEvent value) => Serialize(ToWire(value));
    public static ChatEvent DecodeChatEvent(IMessage bytes) => FromWire(Parse(Wire.ChatEventMessage.Parser, bytes));
    public static Wire.PingSendMessage EncodePingSend(PingSend value) => Serialize(new Wire.PingSendMessage { TileX = value.TileX, TileY = value.TileY });
    public static PingSend DecodePingSend(IMessage bytes)
    {
        var wire = Parse(Wire.PingSendMessage.Parser, bytes); return new PingSend { TileX = wire.TileX, TileY = wire.TileY };
    }
    public static Wire.PingEventMessage EncodePingEvent(PingEvent value) => Serialize(ToWire(value));
    public static PingEvent DecodePingEvent(IMessage bytes) => FromWire(Parse(Wire.PingEventMessage.Parser, bytes));
    public static Wire.SocialSyncRequestMessage EncodeSocialSyncRequest(ulong requestId) => Serialize(new Wire.SocialSyncRequestMessage { RequestId = requestId });
    public static ulong DecodeSocialSyncRequest(IMessage bytes) => Parse(Wire.SocialSyncRequestMessage.Parser, bytes).RequestId;
    public static Wire.SocialSyncBeginMessage EncodeSocialSyncBegin(SocialSyncBegin value)
    {
        if (value.Participants.Count > 8) throw new InvalidDataException("Social sync state is too large.");
        var wire = new Wire.SocialSyncBeginMessage { RequestId = value.RequestId, Watermark = value.Watermark, ChunkCount = value.ChunkCount };
        wire.Participants.Add(value.Participants.Select(ToWire)); return Serialize(wire);
    }
    public static SocialSyncBegin DecodeSocialSyncBegin(IMessage bytes)
    {
        var wire = Parse(Wire.SocialSyncBeginMessage.Parser, bytes);
        if (wire.Participants.Count > 8) throw new InvalidDataException("Social sync state is too large.");
        var value = new SocialSyncBegin { RequestId = wire.RequestId, Watermark = wire.Watermark, ChunkCount = wire.ChunkCount };
        value.Participants.AddRange(wire.Participants.Select(FromWire)); return value;
    }
    public static Wire.SocialSyncChunkMessage EncodeSocialSyncChunk(SocialSyncChunk value)
    {
        if (value.Messages.Count > 64) throw new InvalidDataException("Social sync chunk is too large.");
        var wire = new Wire.SocialSyncChunkMessage { RequestId = value.RequestId, ChunkIndex = value.ChunkIndex };
        wire.Messages.Add(value.Messages.Select(ToWire)); return Serialize(wire);
    }
    public static SocialSyncChunk DecodeSocialSyncChunk(IMessage bytes)
    {
        var wire = Parse(Wire.SocialSyncChunkMessage.Parser, bytes);
        if (wire.Messages.Count > 64) throw new InvalidDataException("Social sync chunk is too large.");
        var value = new SocialSyncChunk { RequestId = wire.RequestId, ChunkIndex = wire.ChunkIndex };
        value.Messages.AddRange(wire.Messages.Select(FromWire)); return value;
    }
    public static Wire.SocialSyncCompleteMessage EncodeSocialSyncComplete(SocialSyncComplete value) => Serialize(new Wire.SocialSyncCompleteMessage
        { RequestId = value.RequestId, Watermark = value.Watermark });
    public static SocialSyncComplete DecodeSocialSyncComplete(IMessage bytes)
    {
        var wire = Parse(Wire.SocialSyncCompleteMessage.Parser, bytes);
        return new SocialSyncComplete { RequestId = wire.RequestId, Watermark = wire.Watermark };
    }

    public static Wire.ResumeSessionRequest EncodeResumeSession(ResumeSessionRequest value) => Serialize(new Wire.ResumeSessionRequest
        { RoomId = GuidBytes(value.RoomId), MatchId = GuidBytes(value.MatchId), ClientId = GuidBytes(value.ClientId),
          AppliedFrameId = value.AppliedFrameId, VerifiedFrameId = value.VerifiedFrameId });

    public static ResumeSessionRequest DecodeResumeSession(IMessage bytes)
    {
        var wire = Parse(Wire.ResumeSessionRequest.Parser, bytes);
        return new ResumeSessionRequest { RoomId = ReadGuid(wire.RoomId), MatchId = ReadGuid(wire.MatchId), ClientId = ReadGuid(wire.ClientId),
            AppliedFrameId = wire.AppliedFrameId, VerifiedFrameId = wire.VerifiedFrameId };
    }

    public static Wire.ResumeSessionAccepted EncodeResumeSessionAccepted(ResumeSessionAccepted value) => Serialize(new Wire.ResumeSessionAccepted
        { RoomId = GuidBytes(value.RoomId), MatchId = GuidBytes(value.MatchId), ClientId = GuidBytes(value.ClientId), LatestFrameId = value.LatestFrameId });

    public static ResumeSessionAccepted DecodeResumeSessionAccepted(IMessage bytes)
    {
        var wire = Parse(Wire.ResumeSessionAccepted.Parser, bytes);
        return new ResumeSessionAccepted { RoomId = ReadGuid(wire.RoomId), MatchId = ReadGuid(wire.MatchId), ClientId = ReadGuid(wire.ClientId), LatestFrameId = wire.LatestFrameId };
    }

    public static Wire.SeatControlChanged EncodeSeatControlChanged(SeatControlChanged value) => Serialize(new Wire.SeatControlChanged
        { SeatId = GuidBytes(value.SeatId), PlayerIndex = value.PlayerIndex, AiControlled = value.AiControlled, Reason = value.Reason });

    public static SeatControlChanged DecodeSeatControlChanged(IMessage bytes)
    {
        var wire = Parse(Wire.SeatControlChanged.Parser, bytes);
        return new SeatControlChanged { SeatId = ReadGuid(wire.SeatId), PlayerIndex = wire.PlayerIndex,
            AiControlled = wire.AiControlled, Reason = wire.Reason };
    }

    public static ulong DecodeRelayControlRequestId(IMessage bytes, MessageType type)
    {
        switch (type)
        {
            case MessageType.RelayControlResponse: return Parse(Wire.RelayControlResponse.Parser, bytes).RequestId;
            case MessageType.RelayRoomList: return Parse(Wire.RelayListRoomsResponse.Parser, bytes).RequestId;
            default: throw new InvalidDataException("Message does not carry a relay request id.");
        }
    }

    public static Wire.CommandResponseMessage EncodeCommandResponse(CommandResponse value) => Serialize(new Wire.CommandResponseMessage
        { RequestId = value.RequestId, AuthorityFrameId = value.AuthorityFrameId, Reason = value.Reason });

    public static CommandResponse DecodeCommandResponse(IMessage bytes)
    {
        var wire = Parse(Wire.CommandResponseMessage.Parser, bytes);
        return new CommandResponse { RequestId = wire.RequestId, AuthorityFrameId = wire.AuthorityFrameId, Reason = wire.Reason };
    }

    public static Wire.OperationBegin EncodeOperationBegin(OperationBeginPayload value)
    {
        if (!CommandRules.MayBroadcast(value.Command.Kind)) throw new InvalidDataException("Request-only or retired command in authority log.");
        return Serialize(new Wire.OperationBegin { OperationId = GuidBytes(value.OperationId), SeatId = GuidBytes(value.SeatId),
            RequestId = value.RequestId, Round = value.Round, Command = ToWire(value.Command) });
    }

    public static OperationBeginPayload DecodeOperationBegin(IMessage bytes)
    {
        var wire = Parse(Wire.OperationBegin.Parser, bytes);
        if (wire.Command is null) throw new InvalidDataException("Operation has no command.");
        if (!CommandRules.MayBroadcast((CommandKind)((int)wire.Command.BodyCase - 100))) throw new InvalidDataException("Request-only or retired command in authority log.");
        return new OperationBeginPayload { OperationId = ReadGuid(wire.OperationId), SeatId = ReadGuid(wire.SeatId),
            RequestId = wire.RequestId, Round = wire.Round, Command = FromWire(wire.Command) };
    }

    public static Wire.OperationEnd EncodeOperationEnd(OperationEndPayload value) => Serialize(new Wire.OperationEnd { OperationId = GuidBytes(value.OperationId) });
    public static OperationEndPayload DecodeOperationEnd(IMessage bytes) => new OperationEndPayload { OperationId = ReadGuid(Parse(Wire.OperationEnd.Parser, bytes).OperationId) };

    public static Wire.OperationFailed EncodeOperationFailed(OperationFailedPayload value) => Serialize(new Wire.OperationFailed { OperationId = GuidBytes(value.OperationId), Reason = value.Reason });
    public static OperationFailedPayload DecodeOperationFailed(IMessage bytes)
    {
        var wire = Parse(Wire.OperationFailed.Parser, bytes);
        return new OperationFailedPayload { OperationId = ReadGuid(wire.OperationId), Reason = wire.Reason };
    }

    public static Wire.Resolution EncodeResolution(ResolutionPayload value) => new Wire.Resolution {
        OperationId = GuidBytes(value.OperationId), Event = value.Event ?? throw new InvalidDataException("Missing settlement event.") };
    public static ResolutionPayload DecodeResolution(IMessage message)
    {
        var wire = Parse(Wire.Resolution.Parser, message);
        if (wire.Event is null || wire.Event.BodyCase == Wire.SettlementEvent.BodyOneofCase.None)
            throw new InvalidDataException("Missing settlement event.");
        return new ResolutionPayload { OperationId = ReadGuid(wire.OperationId), Event = wire.Event };
    }

    public static Wire.AuthorityFrameMessage EncodeAuthorityFrame(AuthorityFrame value)
    {
        var wire = value.Message.Body.Clone();
        wire.MatchId = GuidBytes(value.MatchId); wire.FrameId = value.FrameId;
        wire.PreviousHash = ByteString.CopyFrom(value.PrevHash); wire.Hash = ByteString.CopyFrom(value.Hash);
        return wire;
    }
    public static AuthorityFrame DecodeAuthorityFrame(IMessage message)
    {
        var wire = Parse(Wire.AuthorityFrameMessage.Parser, message);
        return new AuthorityFrame { MatchId = ReadGuid(wire.MatchId), FrameId = wire.FrameId,
            PrevHash = CheckedHash(wire.PreviousHash), Message = new AuthorityMessage(wire), Hash = CheckedHash(wire.Hash) };
    }
    public static byte[] EncodeAuthorityFrameForHash(AuthorityFrame value) => AuthorityFieldEncoding.Encode(value);

    public static Wire.SnapshotManifestMessage EncodeSnapshotManifest(SnapshotManifest value) => Serialize(new Wire.SnapshotManifestMessage
    {
        SnapshotId = GuidBytes(value.SnapshotId), MatchId = GuidBytes(value.MatchId), FrameId = value.FrameId,
        FrameHash = ByteString.CopyFrom(value.FrameHash), CompressedLength = value.CompressedLength,
        ChunkCount = value.ChunkCount, ContentHash = ByteString.CopyFrom(value.ContentHash)
    });

    public static SnapshotManifest DecodeSnapshotManifest(IMessage bytes)
    {
        var wire = Parse(Wire.SnapshotManifestMessage.Parser, bytes);
        var value = new SnapshotManifest { SnapshotId = ReadGuid(wire.SnapshotId), MatchId = ReadGuid(wire.MatchId),
            FrameId = wire.FrameId, FrameHash = CheckedHash(wire.FrameHash), CompressedLength = wire.CompressedLength,
            ChunkCount = wire.ChunkCount, ContentHash = CheckedHash(wire.ContentHash) };
        if (value.CompressedLength < 0 || value.CompressedLength > ProtocolConstants.MaxSnapshotBytes || value.ChunkCount < 0 || value.ChunkCount > 65536)
            throw new InvalidDataException("Invalid snapshot manifest bounds.");
        return value;
    }

    public static Wire.SnapshotChunkMessage EncodeSnapshotChunk(SnapshotChunk value) => Serialize(new Wire.SnapshotChunkMessage
        { SnapshotId = GuidBytes(value.SnapshotId), Index = value.Index, Data = ByteString.CopyFrom(value.Data) });

    public static SnapshotChunk DecodeSnapshotChunk(IMessage bytes)
    {
        var wire = Parse(Wire.SnapshotChunkMessage.Parser, bytes);
        if (wire.Data.Length > ProtocolConstants.SnapshotChunkBytes) throw new InvalidDataException("Snapshot chunk is too large.");
        return new SnapshotChunk { SnapshotId = ReadGuid(wire.SnapshotId), Index = wire.Index, Data = wire.Data.ToByteArray() };
    }

    public static Wire.AuthorityTurnPhase EncodeTurnPhase(TurnPhase phase) => new Wire.AuthorityTurnPhase { Phase = (int)phase };
    public static Wire.AuthorityMatchEnded EncodeMatchEnded(bool localVictory) => new Wire.AuthorityMatchEnded { LocalVictory = localVictory };
    public static bool DecodeMatchEnded(IMessage value) => Parse(Wire.AuthorityMatchEnded.Parser, value).LocalVictory;

    public static Wire.Int64Value EncodeInt64(long value) => Serialize(new Wire.Int64Value { Value = value });
    public static long DecodeInt64(IMessage bytes) => Parse(Wire.Int64Value.Parser, bytes).Value;
    public static Wire.GuidValue EncodeGuid(Guid value) => Serialize(new Wire.GuidValue { Value = GuidBytes(value) });
    public static Guid DecodeGuid(IMessage bytes) => ReadGuid(Parse(Wire.GuidValue.Parser, bytes).Value);
    public static Wire.StringValue EncodeString(string value) => Serialize(new Wire.StringValue { Value = value });
    public static string DecodeString(IMessage bytes) => Parse(Wire.StringValue.Parser, bytes).Value;

    private static Wire.RelayRoomInfo ToWire(RelayRoomInfo value) => new Wire.RelayRoomInfo
    {
        RoomId = GuidBytes(value.RoomId), RoomName = value.RoomName, HostName = value.HostName,
        MapTitle = value.MapTitle, ConnectedPlayers = value.ConnectedPlayers, HumanSeats = value.HumanSeats,
        HasPassword = value.HasPassword, Status = (Wire.RelayRoomStatus)value.Status,
        PluginVersion = value.PluginVersion, GameFingerprint = value.GameFingerprint,
        ContentFingerprint = value.ContentFingerprint, AvailableSeats = value.AvailableSeats,
        MaxParticipants = value.MaxParticipants
    };

    private static RelayRoomInfo FromWire(Wire.RelayRoomInfo wire) => new RelayRoomInfo
    {
        RoomId = ReadGuid(wire.RoomId), RoomName = wire.RoomName, HostName = wire.HostName,
        MapTitle = wire.MapTitle, ConnectedPlayers = wire.ConnectedPlayers, HumanSeats = wire.HumanSeats,
        HasPassword = wire.HasPassword, Status = CheckedRelayRoomStatus(wire.Status),
        PluginVersion = wire.PluginVersion, GameFingerprint = wire.GameFingerprint,
        ContentFingerprint = wire.ContentFingerprint, AvailableSeats = wire.AvailableSeats,
        MaxParticipants = wire.MaxParticipants
    };

    private static Wire.Participant ToWire(ParticipantInfo value)
    {
        var wire = new Wire.Participant { ClientId = GuidBytes(value.ClientId), DisplayName = value.DisplayName, Ready = value.Ready,
            Connected = value.Connected, Reconnecting = value.Reconnecting, IsHost = value.IsHost,
            Admission = (Wire.ParticipantAdmission)value.Admission };
        if (value.SeatId.HasValue) wire.SeatId = GuidBytes(value.SeatId.Value);
        return wire;
    }

    private static ParticipantInfo FromWire(Wire.Participant wire)
    {
        var admission = (ParticipantAdmission)wire.Admission;
        if (!Enum.IsDefined(typeof(ParticipantAdmission), admission)) throw new InvalidDataException("Unknown participant admission.");
        return new ParticipantInfo { ClientId = ReadGuid(wire.ClientId), DisplayName = wire.DisplayName, Ready = wire.Ready,
            Connected = wire.Connected, Reconnecting = wire.Reconnecting, IsHost = wire.IsHost, Admission = admission,
            SeatId = wire.HasSeatId ? ReadGuid(wire.SeatId) : null };
    }

    private static Wire.ChatEventMessage ToWire(ChatEvent value)
    {
        var wire = new Wire.ChatEventMessage { SocialSeq = value.SocialSeq, ServerTicks = value.ServerTicks,
            Channel = (Wire.ChatChannel)value.Channel, Kind = (Wire.ChatKind)value.Kind,
            SystemKind = (Wire.SystemEventKind)value.SystemKind, SenderName = value.SenderName,
            SenderTeam = value.SenderTeam, SenderColor = value.SenderColor,
            SenderIsSpectator = value.SenderIsSpectator, Text = value.Text };
        if (value.SenderClientId.HasValue) wire.SenderClientId = GuidBytes(value.SenderClientId.Value);
        return wire;
    }

    private static ChatEvent FromWire(Wire.ChatEventMessage wire)
    {
        var kind = (ChatKind)wire.Kind;
        var systemKind = (SystemEventKind)wire.SystemKind;
        if (!Enum.IsDefined(typeof(ChatKind), kind)) throw new InvalidDataException("Unknown chat kind.");
        if (!Enum.IsDefined(typeof(SystemEventKind), systemKind)) throw new InvalidDataException("Unknown system event kind.");
        return new ChatEvent
        {
            SocialSeq = wire.SocialSeq, ServerTicks = wire.ServerTicks, Channel = CheckedChatChannel(wire.Channel),
            Kind = kind, SystemKind = systemKind,
            SenderClientId = wire.HasSenderClientId ? ReadGuid(wire.SenderClientId) : null,
            SenderName = wire.SenderName, SenderTeam = wire.SenderTeam, SenderColor = wire.SenderColor,
            SenderIsSpectator = wire.SenderIsSpectator, Text = wire.Text
        };
    }

    private static Wire.PingEventMessage ToWire(PingEvent value) => new Wire.PingEventMessage
    {
        SenderClientId = GuidBytes(value.SenderClientId), SenderName = value.SenderName,
        SenderTeam = value.SenderTeam, SenderColor = value.SenderColor, TileX = value.TileX, TileY = value.TileY,
        RemainingTtlMillis = value.RemainingTtlMillis
    };
    private static PingEvent FromWire(Wire.PingEventMessage wire) => new PingEvent
    {
        SenderClientId = ReadGuid(wire.SenderClientId), SenderName = wire.SenderName,
        SenderTeam = wire.SenderTeam, SenderColor = wire.SenderColor, TileX = wire.TileX, TileY = wire.TileY,
        RemainingTtlMillis = wire.RemainingTtlMillis
    };

    private static Wire.GameCommandMessage ToWire(GameCommand value)
    {
        var error = CommandRules.ValidateResolvedActions(value);
        if (error is not null) throw new InvalidDataException(error);
        if (value.UnitIds.Length > MaxUnits || value.UnitTargetXs.Length > MaxUnits || value.UnitTargetXs.Length != value.UnitTargetYs.Length) throw new InvalidDataException("Invalid command size.");
        var wire = new Wire.GameCommandMessage();
        switch (value.Kind)
        {
            case CommandKind.Move:
            {
                var args = new Wire.MoveCommand();
                args.UnitIds.Add(value.UnitIds);
                args.TargetX = value.TargetX;
                args.TargetY = value.TargetY;
                args.UnitTargetXs.Add(value.UnitTargetXs);
                args.UnitTargetYs.Add(value.UnitTargetYs);
                args.DesiredToggleState = value.DesiredToggleState;
                wire.Move = args; break;
            }
            case CommandKind.Action:
            {
                var args = new Wire.ActionCommand();
                args.UnitIds.Add(value.UnitIds);
                args.TargetX = value.TargetX;
                args.TargetY = value.TargetY;
                args.UnitTargetXs.Add(value.UnitTargetXs);
                args.UnitTargetYs.Add(value.UnitTargetYs);
                args.ActionCategory = value.ActionCategory;
                args.TemplateId = value.TemplateId;
                args.PassengerUnitId = value.PassengerUnitId;
                args.DesiredToggleState = value.DesiredToggleState;
                wire.Action = args; break;
            }
            case CommandKind.EquipmentAction:
            {
                var args = new Wire.EquipmentActionCommand();
                args.UnitIds.Add(value.UnitIds);
                args.TargetX = value.TargetX;
                args.TargetY = value.TargetY;
                args.ActionCategory = value.ActionCategory;
                args.ActionId = value.ActionId;
                args.TemplateId = value.TemplateId;
                wire.EquipmentAction = args; break;
            }
            case CommandKind.EquipmentMoveAction:
            {
                var args = new Wire.EquipmentMoveActionCommand();
                args.UnitIds.Add(value.UnitIds);
                args.TargetX = value.TargetX;
                args.TargetY = value.TargetY;
                args.ActionCategory = value.ActionCategory;
                args.ActionId = value.ActionId;
                args.TemplateId = value.TemplateId;
                wire.EquipmentMoveAction = args; break;
            }
            case CommandKind.BuildWithMove:
            {
                var args = new Wire.BuildWithMoveCommand();
                args.UnitIds.Add(value.UnitIds);
                args.TargetX = value.TargetX;
                args.TargetY = value.TargetY;
                args.ActionCategory = value.ActionCategory;
                args.TemplateId = value.TemplateId;
                wire.BuildWithMove = args; break;
            }
            case CommandKind.Skill:
            {
                var args = new Wire.SkillCommand();
                args.TargetX = value.TargetX;
                args.TargetY = value.TargetY;
                wire.Skill = args; break;
            }
            case CommandKind.UndoMove:
            {
                var args = new Wire.UndoMoveCommand();
                wire.UndoMove = args; break;
            }
            case CommandKind.EndTurn:
            {
                var args = new Wire.EndTurnCommand();
                wire.EndTurn = args; break;
            }
            case CommandKind.AutoGuideStart:
            {
                var args = new Wire.AutoGuideStartCommand();
                args.UnitIds.Add(value.UnitIds);
                wire.AutoGuideStart = args; break;
            }
            case CommandKind.DebugAddResources:
            {
                var args = new Wire.DebugAddResourcesCommand();
                args.DebugPlayerIndex = value.DebugPlayerIndex;
                args.DebugMetalDelta = value.DebugMetalDelta;
                args.DebugPowerDelta = value.DebugPowerDelta;
                wire.DebugAddResources = args; break;
            }
            case CommandKind.DebugFillSkill:
            {
                var args = new Wire.DebugFillSkillCommand();
                args.DebugPlayerIndex = value.DebugPlayerIndex;
                wire.DebugFillSkill = args; break;
            }
            case CommandKind.AiUnitAction:
            {
                var args = new Wire.AiUnitActionCommand();
                args.UnitIds.Add(value.UnitIds);
                args.TargetX = value.TargetX;
                args.TargetY = value.TargetY;
                args.UnitTargetXs.Add(value.UnitTargetXs);
                args.UnitTargetYs.Add(value.UnitTargetYs);
                args.ActionCategory = value.ActionCategory;
                args.TemplateId = value.TemplateId;
                args.PassengerUnitId = value.PassengerUnitId;
                args.DesiredToggleState = value.DesiredToggleState;
                args.AiActionType = value.AiActionType;
                wire.AiUnitAction = args; break;
            }
            case CommandKind.AiSkill:
            {
                var args = new Wire.AiSkillCommand();
                args.TargetX = value.TargetX;
                args.TargetY = value.TargetY;
                args.DesiredToggleState = value.DesiredToggleState;
                wire.AiSkill = args; break;
            }
            case CommandKind.TurnAdvance:
            {
                var args = new Wire.TurnAdvanceCommand();
                wire.TurnAdvance = args; break;
            }
            case CommandKind.Surrender:
            {
                var args = new Wire.SurrenderCommand();
                args.TargetX = value.TargetX;
                wire.Surrender = args; break;
            }
            case CommandKind.ToggleStandby:
            {
                var args = new Wire.ToggleStandbyCommand();
                args.UnitIds.Add(value.UnitIds);
                args.DesiredToggleState = value.DesiredToggleState;
                wire.ToggleStandby = args; break;
            }
            case CommandKind.ToggleSleep:
            {
                var args = new Wire.ToggleSleepCommand();
                args.UnitIds.Add(value.UnitIds);
                args.DesiredToggleState = value.DesiredToggleState;
                wire.ToggleSleep = args; break;
            }
            case CommandKind.Stay:
            {
                var args = new Wire.StayCommand();
                args.UnitIds.Add(value.UnitIds);
                wire.Stay = args; break;
            }
            case CommandKind.SelfDestruct:
            {
                var args = new Wire.SelfDestructCommand();
                args.UnitIds.Add(value.UnitIds);
                wire.SelfDestruct = args; break;
            }
            case CommandKind.ResolvedUnitActions:
            {
                var args = new Wire.ResolvedUnitActionsCommand();
                args.UnitIds.Add(value.UnitIds);
                args.ResolvedActions.Add(value.ResolvedActions.Select(ToWire));
                wire.ResolvedUnitActions = args; break;
            }
            default: throw new InvalidDataException("Unknown command kind.");
        }
        return wire;
    }
    private static GameCommand FromWire(Wire.GameCommandMessage wire)
    {
        var value = new GameCommand { Kind = (CommandKind)((int)wire.BodyCase - 100) };
        switch (wire.BodyCase)
        {
            case Wire.GameCommandMessage.BodyOneofCase.Move:
            {
                var args = wire.Move;
                value.UnitIds = args.UnitIds.ToArray();
                value.TargetX = args.TargetX;
                value.TargetY = args.TargetY;
                value.UnitTargetXs = args.UnitTargetXs.ToArray();
                value.UnitTargetYs = args.UnitTargetYs.ToArray();
                value.DesiredToggleState = args.DesiredToggleState;
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.Action:
            {
                var args = wire.Action;
                value.UnitIds = args.UnitIds.ToArray();
                value.TargetX = args.TargetX;
                value.TargetY = args.TargetY;
                value.UnitTargetXs = args.UnitTargetXs.ToArray();
                value.UnitTargetYs = args.UnitTargetYs.ToArray();
                value.ActionCategory = args.ActionCategory;
                value.TemplateId = args.TemplateId;
                value.PassengerUnitId = args.PassengerUnitId;
                value.DesiredToggleState = args.DesiredToggleState;
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.EquipmentAction:
            {
                var args = wire.EquipmentAction;
                value.UnitIds = args.UnitIds.ToArray();
                value.TargetX = args.TargetX;
                value.TargetY = args.TargetY;
                value.ActionCategory = args.ActionCategory;
                value.ActionId = args.ActionId;
                value.TemplateId = args.TemplateId;
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.EquipmentMoveAction:
            {
                var args = wire.EquipmentMoveAction;
                value.UnitIds = args.UnitIds.ToArray();
                value.TargetX = args.TargetX;
                value.TargetY = args.TargetY;
                value.ActionCategory = args.ActionCategory;
                value.ActionId = args.ActionId;
                value.TemplateId = args.TemplateId;
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.BuildWithMove:
            {
                var args = wire.BuildWithMove;
                value.UnitIds = args.UnitIds.ToArray();
                value.TargetX = args.TargetX;
                value.TargetY = args.TargetY;
                value.ActionCategory = args.ActionCategory;
                value.TemplateId = args.TemplateId;
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.Skill:
            {
                var args = wire.Skill;
                value.TargetX = args.TargetX;
                value.TargetY = args.TargetY;
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.UndoMove:
            {
                var args = wire.UndoMove;
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.EndTurn:
            {
                var args = wire.EndTurn;
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.AutoGuideStart:
            {
                var args = wire.AutoGuideStart;
                value.UnitIds = args.UnitIds.ToArray();
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.DebugAddResources:
            {
                var args = wire.DebugAddResources;
                value.DebugPlayerIndex = args.DebugPlayerIndex;
                value.DebugMetalDelta = args.DebugMetalDelta;
                value.DebugPowerDelta = args.DebugPowerDelta;
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.DebugFillSkill:
            {
                var args = wire.DebugFillSkill;
                value.DebugPlayerIndex = args.DebugPlayerIndex;
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.AiUnitAction:
            {
                var args = wire.AiUnitAction;
                value.UnitIds = args.UnitIds.ToArray();
                value.TargetX = args.TargetX;
                value.TargetY = args.TargetY;
                value.UnitTargetXs = args.UnitTargetXs.ToArray();
                value.UnitTargetYs = args.UnitTargetYs.ToArray();
                value.ActionCategory = args.ActionCategory;
                value.TemplateId = args.TemplateId;
                value.PassengerUnitId = args.PassengerUnitId;
                value.DesiredToggleState = args.DesiredToggleState;
                value.AiActionType = args.AiActionType;
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.AiSkill:
            {
                var args = wire.AiSkill;
                value.TargetX = args.TargetX;
                value.TargetY = args.TargetY;
                value.DesiredToggleState = args.DesiredToggleState;
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.TurnAdvance:
            {
                var args = wire.TurnAdvance;
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.Surrender:
            {
                var args = wire.Surrender;
                value.TargetX = args.TargetX;
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.ToggleStandby:
            {
                var args = wire.ToggleStandby;
                value.UnitIds = args.UnitIds.ToArray();
                value.DesiredToggleState = args.DesiredToggleState;
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.ToggleSleep:
            {
                var args = wire.ToggleSleep;
                value.UnitIds = args.UnitIds.ToArray();
                value.DesiredToggleState = args.DesiredToggleState;
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.Stay:
            {
                var args = wire.Stay;
                value.UnitIds = args.UnitIds.ToArray();
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.SelfDestruct:
            {
                var args = wire.SelfDestruct;
                value.UnitIds = args.UnitIds.ToArray();
                break;
            }
            case Wire.GameCommandMessage.BodyOneofCase.ResolvedUnitActions:
            {
                var args = wire.ResolvedUnitActions;
                value.UnitIds = args.UnitIds.ToArray();
                value.ResolvedActions.AddRange(args.ResolvedActions.Select(FromWire));
                break;
            }
            default: throw new InvalidDataException("Missing command body.");
        }
        var error = CommandRules.ValidateResolvedActions(value);
        if (error is not null) throw new InvalidDataException(error);
        if (value.UnitIds.Length > MaxUnits || value.UnitTargetXs.Length > MaxUnits || value.UnitTargetXs.Length != value.UnitTargetYs.Length) throw new InvalidDataException("Invalid command size.");
        return value;
    }
    private static Wire.ResolvedUnitAction ToWire(ResolvedUnitAction action)
    {
        var wire = new Wire.ResolvedUnitAction { UnitId = action.UnitId, SkipAnimations = action.SkipAnimations };
        switch (action.Type)
        {
            case ResolvedActionType.Move: wire.Move = new Wire.ResolvedMove { X=action.MoveX,Y=action.MoveY }; break;
            case ResolvedActionType.Action: wire.Action = new Wire.ResolvedAction { MoveX=action.MoveX,MoveY=action.MoveY,TargetX=action.TargetX,TargetY=action.TargetY,ActionCategory=action.ActionCategory }; break;
            case ResolvedActionType.Create: wire.Create = new Wire.ResolvedCreate { MoveX=action.MoveX,MoveY=action.MoveY,TargetX=action.TargetX,TargetY=action.TargetY,ActionCategory=action.ActionCategory,TemplateId=action.TemplateId }; break;
            case ResolvedActionType.Unload: wire.Unload = new Wire.ResolvedUnload { MoveX=action.MoveX,MoveY=action.MoveY,TargetX=action.TargetX,TargetY=action.TargetY,ActionCategory=action.ActionCategory,PassengerUnitId=action.PassengerUnitId }; break;
            default: throw new InvalidDataException("Unknown resolved action type.");
        }
        return wire;
    }
    private static ResolvedUnitAction FromWire(Wire.ResolvedUnitAction action)
    {
        var value=new ResolvedUnitAction { UnitId=action.UnitId,SkipAnimations=action.SkipAnimations };
        switch (action.BodyCase)
        {
            case Wire.ResolvedUnitAction.BodyOneofCase.Move: value.Type=ResolvedActionType.Move;value.MoveX=action.Move.X;value.MoveY=action.Move.Y;break;
            case Wire.ResolvedUnitAction.BodyOneofCase.Action: value.Type=ResolvedActionType.Action;value.MoveX=action.Action.MoveX;value.MoveY=action.Action.MoveY;value.TargetX=action.Action.TargetX;value.TargetY=action.Action.TargetY;value.ActionCategory=action.Action.ActionCategory;break;
            case Wire.ResolvedUnitAction.BodyOneofCase.Create: value.Type=ResolvedActionType.Create;value.MoveX=action.Create.MoveX;value.MoveY=action.Create.MoveY;value.TargetX=action.Create.TargetX;value.TargetY=action.Create.TargetY;value.ActionCategory=action.Create.ActionCategory;value.TemplateId=action.Create.TemplateId;break;
            case Wire.ResolvedUnitAction.BodyOneofCase.Unload: value.Type=ResolvedActionType.Unload;value.MoveX=action.Unload.MoveX;value.MoveY=action.Unload.MoveY;value.TargetX=action.Unload.TargetX;value.TargetY=action.Unload.TargetY;value.ActionCategory=action.Unload.ActionCategory;value.PassengerUnitId=action.Unload.PassengerUnitId;break;
            default: throw new InvalidDataException("Missing resolved action body.");
        }
        return value;
    }
    private static T Serialize<T>(T value) where T : IMessage => value;
    private static T Parse<T>(MessageParser<T> parser, IMessage value) where T : IMessage<T>
    {
        if (value is T typed) return typed;
        throw new InvalidDataException("Unexpected protobuf message: " + value.Descriptor.FullName);
    }

    private static ByteString GuidBytes(Guid value) => ByteString.CopyFrom(value.ToByteArray());
    private static Guid ReadGuid(ByteString value)
    {
        if (value.Length != 16) throw new InvalidDataException("Invalid GUID length.");
        return new Guid(value.ToByteArray());
    }

    private static byte[] CheckedHash(ByteString value)
    {
        if (value.Length != AuthorityHashChain.HashLength) throw new InvalidDataException("Invalid SHA-256 hash length.");
        return value.ToByteArray();
    }

    private static ushort CheckedUShort(uint value)
    {
        if (value > ushort.MaxValue) throw new InvalidDataException("Protocol version is outside UInt16 range.");
        return (ushort)value;
    }

    private static byte CheckedByte(uint value)
    {
        if (value > byte.MaxValue) throw new InvalidDataException("Random value kind is outside byte range.");
        return (byte)value;
    }

    private static RelayRoomStatus CheckedRelayRoomStatus(Wire.RelayRoomStatus value)
    {
        var status = (RelayRoomStatus)value;
        if (!Enum.IsDefined(typeof(RelayRoomStatus), status)) throw new InvalidDataException("Unknown relay room status.");
        return status;
    }

    private static ChatChannel CheckedChatChannel(Wire.ChatChannel value)
    {
        var channel = (ChatChannel)value;
        if (!Enum.IsDefined(typeof(ChatChannel), channel)) throw new InvalidDataException("Unknown chat channel.");
        return channel;
    }
}
