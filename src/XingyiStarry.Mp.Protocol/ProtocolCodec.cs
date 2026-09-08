using System;
using System.Collections.Generic;
using System.IO;

namespace XingyiStarry.Mp.Protocol;

public static class ProtocolCodec
{
    public static byte[] EncodeWelcome(WelcomeMessage value)
    {
        using var w = new CanonicalWriter();
        w.Write(value.ClientId); w.Write(value.RoomId); w.Write(value.MatchId.HasValue);
        if (value.MatchId.HasValue) w.Write(value.MatchId.Value);
        w.Write(value.LatestFrameId); return w.ToArray();
    }

    public static WelcomeMessage DecodeWelcome(byte[] bytes)
    {
        using var r = new CanonicalReader(bytes);
        var value = new WelcomeMessage { ClientId = r.ReadGuid(), RoomId = r.ReadGuid() };
        if (r.ReadBoolean()) value.MatchId = r.ReadGuid();
        value.LatestFrameId = r.ReadInt64(); r.EnsureEnd(); return value;
    }

    public static byte[] EncodeCommandRequest(CommandRequest value)
    {
        using var w = new CanonicalWriter();
        w.Write(value.ClientId); w.Write(value.RequestId); w.Write(value.SeatId); w.Write(value.Round);
        w.Write(value.AppliedFrameId); w.Write(EncodeCommand(value.Command)); return w.ToArray();
    }

    public static CommandRequest DecodeCommandRequest(byte[] bytes)
    {
        using var r = new CanonicalReader(bytes);
        var value = new CommandRequest { ClientId = r.ReadGuid(), RequestId = r.ReadUInt64(), SeatId = r.ReadGuid(), Round = r.ReadInt32(), AppliedFrameId = r.ReadInt64(), Command = DecodeCommand(r.ReadBytes()) };
        r.EnsureEnd(); return value;
    }

    public static byte[] EncodeCommandResponse(CommandResponse value)
    {
        using var w = new CanonicalWriter(); w.Write(value.RequestId); w.Write(value.AuthorityFrameId); w.Write(value.Reason); return w.ToArray();
    }

    public static CommandResponse DecodeCommandResponse(byte[] bytes)
    {
        using var r = new CanonicalReader(bytes);
        var value = new CommandResponse { RequestId = r.ReadUInt64(), AuthorityFrameId = r.ReadInt64(), Reason = r.ReadStringValue() };
        r.EnsureEnd(); return value;
    }

    public static byte[] EncodeInt64(long value) { using var w = new CanonicalWriter(); w.Write(value); return w.ToArray(); }
    public static long DecodeInt64(byte[] bytes) { using var r = new CanonicalReader(bytes); var value = r.ReadInt64(); r.EnsureEnd(); return value; }
    public static byte[] EncodeGuid(Guid value) { using var w = new CanonicalWriter(); w.Write(value); return w.ToArray(); }
    public static Guid DecodeGuid(byte[] bytes) { using var r = new CanonicalReader(bytes); var value = r.ReadGuid(); r.EnsureEnd(); return value; }
    public static byte[] EncodeString(string value) { using var w = new CanonicalWriter(); w.Write(value); return w.ToArray(); }
    public static string DecodeString(byte[] bytes) { using var r = new CanonicalReader(bytes); var value = r.ReadStringValue(); r.EnsureEnd(); return value; }

    public static byte[] EncodeRoom(RoomSnapshot value)
    {
        using var w = new CanonicalWriter();
        w.Write(value.RoomId); w.Write(value.MatchId.HasValue); if (value.MatchId.HasValue) w.Write(value.MatchId.Value); w.Write(value.MatchStarted);
        w.Write(value.DraftRevision); w.Write(value.MapId); w.Write(value.MapTitle); w.Write(value.FowType); w.Write(value.WinCondition); w.Write(value.QuickStart);
        w.Write(value.Seats.Count);
        foreach (var seat in value.Seats)
        {
            w.Write(seat.SeatId); w.Write(seat.LobbySlotIndex); w.Write(seat.PlayerIndex); w.Write(seat.DisplayName); w.Write(seat.OriginallyHuman);
            w.Write(seat.Connected); w.Write(seat.Ready); w.Write(seat.AiControlled); w.Write(seat.ClientId.HasValue);
            if (seat.ClientId.HasValue) w.Write(seat.ClientId.Value);
            w.Write(seat.Controller); w.Write(seat.Team); w.Write(seat.Color); w.Write(seat.Position); w.Write(seat.PositionRandom);
            w.Write(seat.ResourceMultiplier); w.Write(seat.AiIntelligence); w.Write(seat.CommanderId);
            w.Write(seat.CommanderMode); w.Write(seat.SkillId); w.Write(seat.PassiveIds.Count);
            foreach (var passive in seat.PassiveIds) w.Write(passive);
        }
        return w.ToArray();
    }

    public static RoomSnapshot DecodeRoom(byte[] bytes)
    {
        using var r = new CanonicalReader(bytes);
        var value = new RoomSnapshot { RoomId = r.ReadGuid() };
        if (r.ReadBoolean()) value.MatchId = r.ReadGuid();
        value.MatchStarted = r.ReadBoolean();
        value.DraftRevision = r.ReadInt32(); value.MapId = r.ReadStringValue(); value.MapTitle = r.ReadStringValue();
        value.FowType = r.ReadInt32(); value.WinCondition = r.ReadInt32(); value.QuickStart = r.ReadInt32();
        var count = r.ReadInt32();
        if (count < 0 || count > 64) throw new InvalidDataException("Invalid seat count.");
        for (var i = 0; i < count; i++)
        {
            var seat = new SeatInfo { SeatId = r.ReadGuid(), LobbySlotIndex = r.ReadInt32(), PlayerIndex = r.ReadInt32(), DisplayName = r.ReadStringValue(), OriginallyHuman = r.ReadBoolean(), Connected = r.ReadBoolean(), Ready = r.ReadBoolean(), AiControlled = r.ReadBoolean() };
            if (r.ReadBoolean()) seat.ClientId = r.ReadGuid();
            seat.Controller = r.ReadInt32(); seat.Team = r.ReadInt32(); seat.Color = r.ReadInt32(); seat.Position = r.ReadInt32(); seat.PositionRandom = r.ReadBoolean();
            seat.ResourceMultiplier = r.ReadSingle(); seat.AiIntelligence = r.ReadSingle(); seat.CommanderId = r.ReadStringValue();
            seat.CommanderMode = r.ReadInt32(); seat.SkillId = r.ReadStringValue();
            var passiveCount = r.ReadInt32();
            if (passiveCount < 0 || passiveCount > 64) throw new InvalidDataException("Invalid commander passive count.");
            for (var passiveIndex = 0; passiveIndex < passiveCount; passiveIndex++) seat.PassiveIds.Add(r.ReadStringValue());
            value.Seats.Add(seat);
        }
        r.EnsureEnd(); return value;
    }

    public static byte[] EncodeEnvelope(Envelope envelope)
    {
        using var w = new CanonicalWriter();
        w.Write(ProtocolConstants.Version);
        w.Write((ushort)envelope.Type);
        w.Write(envelope.Payload);
        return w.ToArray();
    }

    public static Envelope DecodeEnvelope(byte[] bytes)
    {
        using var r = new CanonicalReader(bytes);
        var version = r.ReadUInt16();
        if (version != ProtocolConstants.Version) throw new InvalidDataException("Unsupported envelope version.");
        var result = new Envelope { Type = (MessageType)r.ReadUInt16(), Payload = r.ReadBytes() };
        r.EnsureEnd();
        if (!Enum.IsDefined(typeof(MessageType), result.Type)) throw new InvalidDataException("Unknown message type.");
        return result;
    }

    public static byte[] EncodeHello(HelloMessage value)
    {
        using var w = new CanonicalWriter();
        w.Write(value.ProtocolVersion); w.Write(value.PluginVersion); w.Write(value.GameFingerprint);
        w.Write(value.ContentFingerprint); w.Write(value.DisplayName);
        return w.ToArray();
    }

    public static HelloMessage DecodeHello(byte[] bytes)
    {
        using var r = new CanonicalReader(bytes);
        var value = new HelloMessage { ProtocolVersion = r.ReadUInt16(), PluginVersion = r.ReadStringValue(), GameFingerprint = r.ReadStringValue(), ContentFingerprint = r.ReadStringValue(), DisplayName = r.ReadStringValue() };
        r.EnsureEnd(); return value;
    }

    public static byte[] EncodeCommand(GameCommand value)
    {
        using var w = new CanonicalWriter();
        w.Write((ushort)value.Kind); w.Write(value.UnitIds.Length);
        foreach (var id in value.UnitIds) w.Write(id);
        w.Write(value.TargetX); w.Write(value.TargetY);
        if (value.UnitTargetXs.Length != value.UnitTargetYs.Length) throw new InvalidDataException("Move target arrays have different lengths.");
        w.Write(value.UnitTargetXs.Length);
        for (var i = 0; i < value.UnitTargetXs.Length; i++) { w.Write(value.UnitTargetXs[i]); w.Write(value.UnitTargetYs[i]); }
        w.Write(value.ActionCategory); w.Write(value.ActionId);
        w.Write(value.TemplateId); w.Write(value.PassengerUnitId); w.Write(value.DesiredToggleState);
        w.Write(value.DebugPlayerIndex); w.Write(value.DebugMetalDelta); w.Write(value.DebugPowerDelta);
        return w.ToArray();
    }

    public static GameCommand DecodeCommand(byte[] bytes)
    {
        using var r = new CanonicalReader(bytes);
        var value = new GameCommand { Kind = (CommandKind)r.ReadUInt16() };
        var count = r.ReadInt32();
        if (count < 0 || count > 4096) throw new InvalidDataException("Invalid unit count.");
        value.UnitIds = new long[count];
        for (var i = 0; i < count; i++) value.UnitIds[i] = r.ReadInt64();
        value.TargetX = r.ReadInt32(); value.TargetY = r.ReadInt32();
        var targetCount = r.ReadInt32();
        if (targetCount < 0 || targetCount > 4096) throw new InvalidDataException("Invalid move target count.");
        value.UnitTargetXs = new int[targetCount]; value.UnitTargetYs = new int[targetCount];
        for (var i = 0; i < targetCount; i++) { value.UnitTargetXs[i] = r.ReadInt32(); value.UnitTargetYs[i] = r.ReadInt32(); }
        value.ActionCategory = r.ReadInt32();
        value.ActionId = r.ReadStringValue(); value.TemplateId = r.ReadStringValue(); value.PassengerUnitId = r.ReadInt64();
        value.DesiredToggleState = r.ReadBoolean();
        value.DebugPlayerIndex = r.ReadInt32(); value.DebugMetalDelta = r.ReadInt32(); value.DebugPowerDelta = r.ReadInt32();
        r.EnsureEnd(); return value;
    }

    public static byte[] EncodeResolution(ResolutionPayload value)
    {
        using var w = new CanonicalWriter();
        w.Write(value.OperationId); w.Write(value.StageId); w.Write(value.SettlementOrdinal); w.Write(value.RandomRecords.Count);
        foreach (var item in value.RandomRecords)
        {
            w.Write(item.CallSite); w.Write(item.Ordinal); w.Write(item.ValueKind); w.Write(item.IntegerValue); w.Write(item.FloatingValue);
        }
        return w.ToArray();
    }

    public static byte[] EncodeOperationBegin(OperationBeginPayload value)
    {
        using var w = new CanonicalWriter();
        w.Write(value.OperationId); w.Write(value.SeatId); w.Write(value.RequestId); w.Write(value.Round); w.Write(EncodeCommand(value.Command));
        return w.ToArray();
    }

    public static OperationBeginPayload DecodeOperationBegin(byte[] bytes)
    {
        using var r = new CanonicalReader(bytes);
        var value = new OperationBeginPayload { OperationId = r.ReadGuid(), SeatId = r.ReadGuid(), RequestId = r.ReadUInt64(), Round = r.ReadInt32(), Command = DecodeCommand(r.ReadBytes()) };
        r.EnsureEnd(); return value;
    }

    public static byte[] EncodeOperationEnd(OperationEndPayload value)
    {
        using var w = new CanonicalWriter(); w.Write(value.OperationId); return w.ToArray();
    }

    public static OperationEndPayload DecodeOperationEnd(byte[] bytes)
    {
        using var r = new CanonicalReader(bytes);
        var value = new OperationEndPayload { OperationId = r.ReadGuid() };
        r.EnsureEnd(); return value;
    }

    public static byte[] EncodeOperationFailed(OperationFailedPayload value)
    {
        using var w = new CanonicalWriter(); w.Write(value.OperationId); w.Write(value.Reason); return w.ToArray();
    }

    public static OperationFailedPayload DecodeOperationFailed(byte[] bytes)
    {
        using var r = new CanonicalReader(bytes);
        var value = new OperationFailedPayload { OperationId = r.ReadGuid(), Reason = r.ReadStringValue() };
        r.EnsureEnd(); return value;
    }

    public static ResolutionPayload DecodeResolution(byte[] bytes)
    {
        using var r = new CanonicalReader(bytes);
        var value = new ResolutionPayload { OperationId = r.ReadGuid(), StageId = r.ReadInt32(), SettlementOrdinal = r.ReadInt32() };
        var count = r.ReadInt32();
        if (count < 0 || count > 65536) throw new InvalidDataException("Invalid random record count.");
        for (var i = 0; i < count; i++) value.RandomRecords.Add(new RandomRecord { CallSite = r.ReadStringValue(), Ordinal = r.ReadInt32(), ValueKind = r.ReadByte(), IntegerValue = r.ReadInt64(), FloatingValue = r.ReadDouble() });
        r.EnsureEnd(); return value;
    }

    public static byte[] EncodeAuthorityFrameForHash(AuthorityFrame value)
    {
        using var w = new CanonicalWriter();
        w.Write(ProtocolConstants.HashDomain); w.Write(value.MatchId); w.Write(value.FrameId); w.Write(value.PrevHash);
        w.Write((byte)value.FrameType); w.Write(value.Payload);
        return w.ToArray();
    }

    public static byte[] EncodeAuthorityFrame(AuthorityFrame value)
    {
        using var w = new CanonicalWriter();
        w.Write(value.MatchId); w.Write(value.FrameId); w.Write(value.PrevHash); w.Write((byte)value.FrameType); w.Write(value.Payload); w.Write(value.Hash);
        return w.ToArray();
    }

    public static AuthorityFrame DecodeAuthorityFrame(byte[] bytes)
    {
        using var r = new CanonicalReader(bytes);
        var value = new AuthorityFrame { MatchId = r.ReadGuid(), FrameId = r.ReadInt64(), PrevHash = r.ReadBytes(64), FrameType = (AuthorityFrameType)r.ReadByte(), Payload = r.ReadBytes(), Hash = r.ReadBytes(64) };
        r.EnsureEnd(); return value;
    }

    public static byte[] EncodeSnapshotManifest(SnapshotManifest value)
    {
        using var w = new CanonicalWriter();
        w.Write(value.SnapshotId); w.Write(value.MatchId); w.Write(value.FrameId); w.Write(value.FrameHash);
        w.Write(value.CompressedLength); w.Write(value.ChunkCount); w.Write(value.ContentHash); return w.ToArray();
    }

    public static SnapshotManifest DecodeSnapshotManifest(byte[] bytes)
    {
        using var r = new CanonicalReader(bytes);
        var value = new SnapshotManifest { SnapshotId = r.ReadGuid(), MatchId = r.ReadGuid(), FrameId = r.ReadInt64(), FrameHash = r.ReadBytes(64), CompressedLength = r.ReadInt32(), ChunkCount = r.ReadInt32(), ContentHash = r.ReadBytes(64) };
        r.EnsureEnd();
        if (value.CompressedLength < 0 || value.CompressedLength > ProtocolConstants.MaxSnapshotBytes || value.ChunkCount < 0 || value.ChunkCount > 65536) throw new InvalidDataException("Invalid snapshot manifest bounds.");
        return value;
    }

    public static byte[] EncodeSnapshotChunk(SnapshotChunk value)
    {
        using var w = new CanonicalWriter(); w.Write(value.SnapshotId); w.Write(value.Index); w.Write(value.Data); return w.ToArray();
    }

    public static SnapshotChunk DecodeSnapshotChunk(byte[] bytes)
    {
        using var r = new CanonicalReader(bytes);
        var value = new SnapshotChunk { SnapshotId = r.ReadGuid(), Index = r.ReadInt32(), Data = r.ReadBytes(ProtocolConstants.SnapshotChunkBytes) };
        r.EnsureEnd(); return value;
    }
}
