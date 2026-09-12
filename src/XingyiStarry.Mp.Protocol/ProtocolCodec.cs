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
    private const int MaxRandomRecords = 65536;

    public static byte[] EncodeEnvelope(Envelope value)
    {
        var wire = new Wire.WireEnvelope
        {
            ProtocolVersion = ProtocolConstants.Version,
            MessageType = (uint)value.Type,
            Payload = ByteString.CopyFrom(value.Payload),
            Delivery = (Wire.Delivery)value.Delivery
        };
        if (value.RoomId.HasValue) wire.RoomId = GuidBytes(value.RoomId.Value);
        if (value.ClientId.HasValue) wire.ClientId = GuidBytes(value.ClientId.Value);
        if (value.TargetClientId.HasValue) wire.TargetClientId = GuidBytes(value.TargetClientId.Value);
        return Serialize(wire);
    }

    public static Envelope DecodeEnvelope(byte[] bytes)
    {
        var wire = Parse(Wire.WireEnvelope.Parser, bytes);
        if (wire.ProtocolVersion != ProtocolConstants.Version) throw new InvalidDataException("Unsupported envelope version.");
        var type = (MessageType)wire.MessageType;
        if (!Enum.IsDefined(typeof(MessageType), type)) throw new InvalidDataException("Unknown message type.");
        var delivery = (Delivery)wire.Delivery;
        if (!Enum.IsDefined(typeof(Delivery), delivery)) throw new InvalidDataException("Unknown delivery mode.");
        return new Envelope
        {
            Type = type, Payload = wire.Payload.ToByteArray(), Delivery = delivery,
            RoomId = wire.HasRoomId ? ReadGuid(wire.RoomId) : null,
            ClientId = wire.HasClientId ? ReadGuid(wire.ClientId) : null,
            TargetClientId = wire.HasTargetClientId ? ReadGuid(wire.TargetClientId) : null
        };
    }

    public static byte[] EncodeHello(HelloMessage value) => Serialize(new Wire.Hello
    {
        ProtocolVersion = value.ProtocolVersion, PluginVersion = value.PluginVersion,
        GameFingerprint = value.GameFingerprint, ContentFingerprint = value.ContentFingerprint,
        DisplayName = value.DisplayName
    });

    public static HelloMessage DecodeHello(byte[] bytes)
    {
        var wire = Parse(Wire.Hello.Parser, bytes);
        return new HelloMessage { ProtocolVersion = CheckedUShort(wire.ProtocolVersion), PluginVersion = wire.PluginVersion,
            GameFingerprint = wire.GameFingerprint, ContentFingerprint = wire.ContentFingerprint, DisplayName = wire.DisplayName };
    }

    public static byte[] EncodeWelcome(WelcomeMessage value)
    {
        var wire = new Wire.Welcome { ClientId = GuidBytes(value.ClientId), RoomId = GuidBytes(value.RoomId), LatestFrameId = value.LatestFrameId };
        if (value.MatchId.HasValue) wire.MatchId = GuidBytes(value.MatchId.Value);
        return Serialize(wire);
    }

    public static WelcomeMessage DecodeWelcome(byte[] bytes)
    {
        var wire = Parse(Wire.Welcome.Parser, bytes);
        return new WelcomeMessage { ClientId = ReadGuid(wire.ClientId), RoomId = ReadGuid(wire.RoomId),
            MatchId = wire.HasMatchId ? ReadGuid(wire.MatchId) : null, LatestFrameId = wire.LatestFrameId };
    }

    public static byte[] EncodeRoom(RoomSnapshot value)
    {
        if (value.Seats.Count > MaxSeats) throw new InvalidDataException("Invalid seat count.");
        var wire = new Wire.Room { RoomId = GuidBytes(value.RoomId), MatchStarted = value.MatchStarted,
            DraftRevision = value.DraftRevision, MapId = value.MapId, MapTitle = value.MapTitle,
            FowType = value.FowType, WinCondition = value.WinCondition, QuickStart = value.QuickStart };
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
                CommanderMode = seat.CommanderMode, SkillId = seat.SkillId };
            if (seat.ClientId.HasValue) item.ClientId = GuidBytes(seat.ClientId.Value);
            item.PassiveIds.Add(seat.PassiveIds);
            wire.Seats.Add(item);
        }
        return Serialize(wire);
    }

    public static RoomSnapshot DecodeRoom(byte[] bytes)
    {
        var wire = Parse(Wire.Room.Parser, bytes);
        if (wire.Seats.Count > MaxSeats) throw new InvalidDataException("Invalid seat count.");
        var value = new RoomSnapshot { RoomId = ReadGuid(wire.RoomId), MatchId = wire.HasMatchId ? ReadGuid(wire.MatchId) : null,
            MatchStarted = wire.MatchStarted, DraftRevision = wire.DraftRevision, MapId = wire.MapId,
            MapTitle = wire.MapTitle, FowType = wire.FowType, WinCondition = wire.WinCondition, QuickStart = wire.QuickStart };
        foreach (var seat in wire.Seats)
        {
            if (seat.PassiveIds.Count > MaxSeats) throw new InvalidDataException("Invalid commander passive count.");
            var item = new SeatInfo { SeatId = ReadGuid(seat.SeatId), LobbySlotIndex = seat.LobbySlotIndex,
                PlayerIndex = seat.PlayerIndex, DisplayName = seat.DisplayName, OriginallyHuman = seat.OriginallyHuman,
                Connected = seat.Connected, Ready = seat.Ready, AiControlled = seat.AiControlled,
                ClientId = seat.HasClientId ? ReadGuid(seat.ClientId) : null, Controller = seat.Controller,
                Team = seat.Team, Color = seat.Color, Position = seat.Position, PositionRandom = seat.PositionRandom,
                ResourceMultiplier = seat.ResourceMultiplier, AiIntelligence = seat.AiIntelligence,
                CommanderId = seat.CommanderId, CommanderMode = seat.CommanderMode, SkillId = seat.SkillId };
            item.PassiveIds.AddRange(seat.PassiveIds);
            value.Seats.Add(item);
        }
        return value;
    }

    public static byte[] EncodeCommand(GameCommand value) => Serialize(ToWire(value));
    public static GameCommand DecodeCommand(byte[] bytes) => FromWire(Parse(Wire.GameCommandMessage.Parser, bytes));

    public static byte[] EncodeCommandRequest(CommandRequest value) => Serialize(new Wire.CommandRequestMessage
    {
        RequestId = value.RequestId, SeatId = GuidBytes(value.SeatId),
        Round = value.Round, AppliedFrameId = value.AppliedFrameId, Command = ToWire(value.Command)
    });

    public static CommandRequest DecodeCommandRequest(byte[] bytes)
    {
        var wire = Parse(Wire.CommandRequestMessage.Parser, bytes);
        if (wire.Command is null) throw new InvalidDataException("Command request has no command.");
        return new CommandRequest { RequestId = wire.RequestId,
            SeatId = ReadGuid(wire.SeatId), Round = wire.Round, AppliedFrameId = wire.AppliedFrameId,
            Command = FromWire(wire.Command) };
    }

    public static byte[] EncodeRelayRegisterRoom(RelayRegisterRoomRequest value) => Serialize(new Wire.RelayRegisterRoomRequest
    {
        RequestId = value.RequestId, RoomId = GuidBytes(value.RoomId), RoomName = value.RoomName,
        HostName = value.HostName, Password = value.Password, PluginVersion = value.PluginVersion,
        GameFingerprint = value.GameFingerprint, ContentFingerprint = value.ContentFingerprint
    });

    public static RelayRegisterRoomRequest DecodeRelayRegisterRoom(byte[] bytes)
    {
        var wire = Parse(Wire.RelayRegisterRoomRequest.Parser, bytes);
        return new RelayRegisterRoomRequest { RequestId = wire.RequestId, RoomId = ReadGuid(wire.RoomId),
            RoomName = wire.RoomName, HostName = wire.HostName, Password = wire.Password,
            PluginVersion = wire.PluginVersion, GameFingerprint = wire.GameFingerprint,
            ContentFingerprint = wire.ContentFingerprint };
    }

    public static byte[] EncodeRelayUpdateRoom(RelayUpdateRoomRequest value) => Serialize(new Wire.RelayUpdateRoomRequest
    {
        RequestId = value.RequestId, RoomId = GuidBytes(value.RoomId), MapTitle = value.MapTitle,
        ConnectedPlayers = value.ConnectedPlayers, HumanSeats = value.HumanSeats, Status = (Wire.RelayRoomStatus)value.Status
    });

    public static RelayUpdateRoomRequest DecodeRelayUpdateRoom(byte[] bytes)
    {
        var wire = Parse(Wire.RelayUpdateRoomRequest.Parser, bytes);
        var status = CheckedRelayRoomStatus(wire.Status);
        return new RelayUpdateRoomRequest { RequestId = wire.RequestId, RoomId = ReadGuid(wire.RoomId),
            MapTitle = wire.MapTitle, ConnectedPlayers = wire.ConnectedPlayers, HumanSeats = wire.HumanSeats, Status = status };
    }

    public static byte[] EncodeRelayListRoomsRequest(ulong requestId) => Serialize(new Wire.RelayListRoomsRequest { RequestId = requestId });
    public static ulong DecodeRelayListRoomsRequest(byte[] bytes) => Parse(Wire.RelayListRoomsRequest.Parser, bytes).RequestId;

    public static byte[] EncodeRelayRoomList(RelayListRoomsResponse value)
    {
        if (value.Rooms.Count > 4096) throw new InvalidDataException("Invalid relay room count.");
        var wire = new Wire.RelayListRoomsResponse { RequestId = value.RequestId };
        wire.Rooms.Add(value.Rooms.Select(ToWire));
        return Serialize(wire);
    }

    public static RelayListRoomsResponse DecodeRelayRoomList(byte[] bytes)
    {
        var wire = Parse(Wire.RelayListRoomsResponse.Parser, bytes);
        if (wire.Rooms.Count > 4096) throw new InvalidDataException("Invalid relay room count.");
        var value = new RelayListRoomsResponse { RequestId = wire.RequestId };
        value.Rooms.AddRange(wire.Rooms.Select(FromWire));
        return value;
    }

    public static byte[] EncodeRelayJoinRoom(RelayJoinRoomRequest value) => Serialize(new Wire.RelayJoinRoomRequest
        { RequestId = value.RequestId, RoomId = GuidBytes(value.RoomId), Password = value.Password });

    public static RelayJoinRoomRequest DecodeRelayJoinRoom(byte[] bytes)
    {
        var wire = Parse(Wire.RelayJoinRoomRequest.Parser, bytes);
        return new RelayJoinRoomRequest { RequestId = wire.RequestId, RoomId = ReadGuid(wire.RoomId), Password = wire.Password };
    }

    public static byte[] EncodeRelayControlResponse(RelayControlResponse value)
    {
        var wire = new Wire.RelayControlResponse { RequestId = value.RequestId, Success = value.Success, Reason = value.Reason };
        if (value.ClientId.HasValue) wire.ClientId = GuidBytes(value.ClientId.Value);
        return Serialize(wire);
    }

    public static RelayControlResponse DecodeRelayControlResponse(byte[] bytes)
    {
        var wire = Parse(Wire.RelayControlResponse.Parser, bytes);
        return new RelayControlResponse { RequestId = wire.RequestId, Success = wire.Success, Reason = wire.Reason,
            ClientId = wire.HasClientId ? ReadGuid(wire.ClientId) : null };
    }

    public static byte[] EncodeRelayRoomRequest(RelayRoomRequest value) => Serialize(new Wire.RelayRoomRequest
        { RequestId = value.RequestId, RoomId = GuidBytes(value.RoomId) });

    public static RelayRoomRequest DecodeRelayRoomRequest(byte[] bytes)
    {
        var wire = Parse(Wire.RelayRoomRequest.Parser, bytes);
        return new RelayRoomRequest { RequestId = wire.RequestId, RoomId = ReadGuid(wire.RoomId) };
    }

    public static byte[] EncodeRelayPeerNotice(RelayPeerNotice value) => Serialize(new Wire.RelayPeerNotice
        { ClientId = GuidBytes(value.ClientId), Reason = value.Reason });

    public static RelayPeerNotice DecodeRelayPeerNotice(byte[] bytes)
    {
        var wire = Parse(Wire.RelayPeerNotice.Parser, bytes);
        return new RelayPeerNotice { ClientId = ReadGuid(wire.ClientId), Reason = wire.Reason };
    }

    public static ulong DecodeRelayControlRequestId(byte[] bytes, MessageType type)
    {
        switch (type)
        {
            case MessageType.RelayControlResponse: return Parse(Wire.RelayControlResponse.Parser, bytes).RequestId;
            case MessageType.RelayRoomList: return Parse(Wire.RelayListRoomsResponse.Parser, bytes).RequestId;
            default: throw new InvalidDataException("Message does not carry a relay request id.");
        }
    }

    public static byte[] EncodeCommandResponse(CommandResponse value) => Serialize(new Wire.CommandResponseMessage
        { RequestId = value.RequestId, AuthorityFrameId = value.AuthorityFrameId, Reason = value.Reason });

    public static CommandResponse DecodeCommandResponse(byte[] bytes)
    {
        var wire = Parse(Wire.CommandResponseMessage.Parser, bytes);
        return new CommandResponse { RequestId = wire.RequestId, AuthorityFrameId = wire.AuthorityFrameId, Reason = wire.Reason };
    }

    public static byte[] EncodeOperationBegin(OperationBeginPayload value) => Serialize(new Wire.OperationBegin
        { OperationId = GuidBytes(value.OperationId), SeatId = GuidBytes(value.SeatId), RequestId = value.RequestId,
          Round = value.Round, Command = ToWire(value.Command) });

    public static OperationBeginPayload DecodeOperationBegin(byte[] bytes)
    {
        var wire = Parse(Wire.OperationBegin.Parser, bytes);
        if (wire.Command is null) throw new InvalidDataException("Operation has no command.");
        return new OperationBeginPayload { OperationId = ReadGuid(wire.OperationId), SeatId = ReadGuid(wire.SeatId),
            RequestId = wire.RequestId, Round = wire.Round, Command = FromWire(wire.Command) };
    }

    public static byte[] EncodeOperationEnd(OperationEndPayload value) => Serialize(new Wire.OperationEnd { OperationId = GuidBytes(value.OperationId) });
    public static OperationEndPayload DecodeOperationEnd(byte[] bytes) => new OperationEndPayload { OperationId = ReadGuid(Parse(Wire.OperationEnd.Parser, bytes).OperationId) };

    public static byte[] EncodeOperationFailed(OperationFailedPayload value) => Serialize(new Wire.OperationFailed { OperationId = GuidBytes(value.OperationId), Reason = value.Reason });
    public static OperationFailedPayload DecodeOperationFailed(byte[] bytes)
    {
        var wire = Parse(Wire.OperationFailed.Parser, bytes);
        return new OperationFailedPayload { OperationId = ReadGuid(wire.OperationId), Reason = wire.Reason };
    }

    public static byte[] EncodeResolution(ResolutionPayload value)
    {
        if (value.RandomRecords.Count > MaxRandomRecords) throw new InvalidDataException("Invalid random record count.");
        var wire = new Wire.Resolution { OperationId = GuidBytes(value.OperationId), StageId = value.StageId, SettlementOrdinal = value.SettlementOrdinal };
        wire.RandomRecords.Add(value.RandomRecords.Select(x => new Wire.RandomRecordMessage { CallSite = x.CallSite,
            Ordinal = x.Ordinal, ValueKind = x.ValueKind, IntegerValue = x.IntegerValue, FloatingValue = x.FloatingValue }));
        return Serialize(wire);
    }

    public static ResolutionPayload DecodeResolution(byte[] bytes)
    {
        var wire = Parse(Wire.Resolution.Parser, bytes);
        if (wire.RandomRecords.Count > MaxRandomRecords) throw new InvalidDataException("Invalid random record count.");
        var value = new ResolutionPayload { OperationId = ReadGuid(wire.OperationId), StageId = wire.StageId, SettlementOrdinal = wire.SettlementOrdinal };
        value.RandomRecords.AddRange(wire.RandomRecords.Select(x => new RandomRecord { CallSite = x.CallSite,
            Ordinal = x.Ordinal, ValueKind = CheckedByte(x.ValueKind), IntegerValue = x.IntegerValue, FloatingValue = x.FloatingValue }));
        return value;
    }

    public static byte[] EncodeAuthorityFrame(AuthorityFrame value) => Serialize(new Wire.AuthorityFrameMessage
    {
        MatchId = GuidBytes(value.MatchId), FrameId = value.FrameId, PreviousHash = ByteString.CopyFrom(value.PrevHash),
        FrameType = (uint)value.FrameType, Payload = ByteString.CopyFrom(value.Payload), Hash = ByteString.CopyFrom(value.Hash)
    });

    public static AuthorityFrame DecodeAuthorityFrame(byte[] bytes)
    {
        var wire = Parse(Wire.AuthorityFrameMessage.Parser, bytes);
        var type = (AuthorityFrameType)wire.FrameType;
        if (!Enum.IsDefined(typeof(AuthorityFrameType), type)) throw new InvalidDataException("Unknown authority frame type.");
        return new AuthorityFrame { MatchId = ReadGuid(wire.MatchId), FrameId = wire.FrameId,
            PrevHash = CheckedHash(wire.PreviousHash), FrameType = type, Payload = wire.Payload.ToByteArray(), Hash = CheckedHash(wire.Hash) };
    }

    public static byte[] EncodeAuthorityFrameForHash(AuthorityFrame value) => Serialize(new Wire.AuthorityHashInput
    {
        Domain = ProtocolConstants.HashDomain, MatchId = GuidBytes(value.MatchId), FrameId = value.FrameId,
        PreviousHash = ByteString.CopyFrom(value.PrevHash), FrameType = (uint)value.FrameType,
        Payload = ByteString.CopyFrom(value.Payload)
    });

    public static byte[] EncodeSnapshotManifest(SnapshotManifest value) => Serialize(new Wire.SnapshotManifestMessage
    {
        SnapshotId = GuidBytes(value.SnapshotId), MatchId = GuidBytes(value.MatchId), FrameId = value.FrameId,
        FrameHash = ByteString.CopyFrom(value.FrameHash), CompressedLength = value.CompressedLength,
        ChunkCount = value.ChunkCount, ContentHash = ByteString.CopyFrom(value.ContentHash)
    });

    public static SnapshotManifest DecodeSnapshotManifest(byte[] bytes)
    {
        var wire = Parse(Wire.SnapshotManifestMessage.Parser, bytes);
        var value = new SnapshotManifest { SnapshotId = ReadGuid(wire.SnapshotId), MatchId = ReadGuid(wire.MatchId),
            FrameId = wire.FrameId, FrameHash = CheckedHash(wire.FrameHash), CompressedLength = wire.CompressedLength,
            ChunkCount = wire.ChunkCount, ContentHash = CheckedHash(wire.ContentHash) };
        if (value.CompressedLength < 0 || value.CompressedLength > ProtocolConstants.MaxSnapshotBytes || value.ChunkCount < 0 || value.ChunkCount > 65536)
            throw new InvalidDataException("Invalid snapshot manifest bounds.");
        return value;
    }

    public static byte[] EncodeSnapshotChunk(SnapshotChunk value) => Serialize(new Wire.SnapshotChunkMessage
        { SnapshotId = GuidBytes(value.SnapshotId), Index = value.Index, Data = ByteString.CopyFrom(value.Data) });

    public static SnapshotChunk DecodeSnapshotChunk(byte[] bytes)
    {
        var wire = Parse(Wire.SnapshotChunkMessage.Parser, bytes);
        if (wire.Data.Length > ProtocolConstants.SnapshotChunkBytes) throw new InvalidDataException("Snapshot chunk is too large.");
        return new SnapshotChunk { SnapshotId = ReadGuid(wire.SnapshotId), Index = wire.Index, Data = wire.Data.ToByteArray() };
    }

    public static byte[] EncodeInt64(long value) => Serialize(new Wire.Int64Value { Value = value });
    public static long DecodeInt64(byte[] bytes) => Parse(Wire.Int64Value.Parser, bytes).Value;
    public static byte[] EncodeGuid(Guid value) => Serialize(new Wire.GuidValue { Value = GuidBytes(value) });
    public static Guid DecodeGuid(byte[] bytes) => ReadGuid(Parse(Wire.GuidValue.Parser, bytes).Value);
    public static byte[] EncodeString(string value) => Serialize(new Wire.StringValue { Value = value });
    public static string DecodeString(byte[] bytes) => Parse(Wire.StringValue.Parser, bytes).Value;

    private static Wire.RelayRoomInfo ToWire(RelayRoomInfo value) => new Wire.RelayRoomInfo
    {
        RoomId = GuidBytes(value.RoomId), RoomName = value.RoomName, HostName = value.HostName,
        MapTitle = value.MapTitle, ConnectedPlayers = value.ConnectedPlayers, HumanSeats = value.HumanSeats,
        HasPassword = value.HasPassword, Status = (Wire.RelayRoomStatus)value.Status,
        PluginVersion = value.PluginVersion, GameFingerprint = value.GameFingerprint,
        ContentFingerprint = value.ContentFingerprint
    };

    private static RelayRoomInfo FromWire(Wire.RelayRoomInfo wire) => new RelayRoomInfo
    {
        RoomId = ReadGuid(wire.RoomId), RoomName = wire.RoomName, HostName = wire.HostName,
        MapTitle = wire.MapTitle, ConnectedPlayers = wire.ConnectedPlayers, HumanSeats = wire.HumanSeats,
        HasPassword = wire.HasPassword, Status = CheckedRelayRoomStatus(wire.Status),
        PluginVersion = wire.PluginVersion, GameFingerprint = wire.GameFingerprint,
        ContentFingerprint = wire.ContentFingerprint
    };

    private static Wire.GameCommandMessage ToWire(GameCommand value)
    {
        if (value.UnitIds.Length > MaxUnits || value.UnitTargetXs.Length > MaxUnits || value.UnitTargetXs.Length != value.UnitTargetYs.Length)
            throw new InvalidDataException("Invalid command unit or target count.");
        var wire = new Wire.GameCommandMessage { Kind = (uint)value.Kind, TargetX = value.TargetX, TargetY = value.TargetY,
            ActionCategory = value.ActionCategory, ActionId = value.ActionId, TemplateId = value.TemplateId,
            PassengerUnitId = value.PassengerUnitId, DesiredToggleState = value.DesiredToggleState,
            DebugPlayerIndex = value.DebugPlayerIndex, DebugMetalDelta = value.DebugMetalDelta,
            DebugPowerDelta = value.DebugPowerDelta, AiActionType = value.AiActionType };
        wire.UnitIds.Add(value.UnitIds); wire.UnitTargetXs.Add(value.UnitTargetXs); wire.UnitTargetYs.Add(value.UnitTargetYs);
        return wire;
    }

    private static GameCommand FromWire(Wire.GameCommandMessage wire)
    {
        if (wire.UnitIds.Count > MaxUnits || wire.UnitTargetXs.Count > MaxUnits || wire.UnitTargetXs.Count != wire.UnitTargetYs.Count)
            throw new InvalidDataException("Invalid command unit or target count.");
        var kind = (CommandKind)wire.Kind;
        if (!Enum.IsDefined(typeof(CommandKind), kind)) throw new InvalidDataException("Unknown command kind.");
        return new GameCommand { Kind = kind, UnitIds = wire.UnitIds.ToArray(), TargetX = wire.TargetX, TargetY = wire.TargetY,
            UnitTargetXs = wire.UnitTargetXs.ToArray(), UnitTargetYs = wire.UnitTargetYs.ToArray(),
            ActionCategory = wire.ActionCategory, ActionId = wire.ActionId, TemplateId = wire.TemplateId,
            PassengerUnitId = wire.PassengerUnitId, DesiredToggleState = wire.DesiredToggleState,
            DebugPlayerIndex = wire.DebugPlayerIndex, DebugMetalDelta = wire.DebugMetalDelta,
            DebugPowerDelta = wire.DebugPowerDelta, AiActionType = wire.AiActionType };
    }

    private static byte[] Serialize(IMessage value) => value.ToByteArray();
    private static T Parse<T>(MessageParser<T> parser, byte[] bytes) where T : IMessage<T>
    {
        try { return parser.ParseFrom(bytes); }
        catch (InvalidProtocolBufferException ex) { throw new InvalidDataException("Invalid protobuf payload.", ex); }
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
}
