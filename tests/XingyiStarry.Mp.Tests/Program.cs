using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XingyiStarry.Mp.Protocol;
using Wire = XingyiStarry.Mp.Protocol.Wire;

namespace XingyiStarry.Mp.Tests;

internal static class Program
{
    private static int passed;

    public static async Task<int> Main()
    {
        Test("command round trip", CommandRoundTrip);
        Test("authority chain", AuthorityChain);
        Test("tamper detection", TamperDetection);
        Test("snapshot codec", SnapshotRoundTrip);
        Test("room round trip", RoomRoundTrip);
        Test("snapshot assembly", SnapshotAssembly);
        Test("operation failure codec", OperationFailureRoundTrip);
        Test("protobuf wire format", ProtobufWireFormat);
        Test("all protobuf messages", AllProtobufMessagesRoundTrip);
        await TestAsync("packet framing", PacketRoundTrip);
        Console.WriteLine($"PASS {passed}/10"); return 0;
    }

    private static void CommandRoundTrip()
    {
        var command = new GameCommand { Kind = CommandKind.Action, UnitIds = new long[] { 9, 2 }, TargetX = -4, TargetY = 17, UnitTargetXs = new[] { 3, 4 }, UnitTargetYs = new[] { 5, 6 }, ActionCategory = 3, ActionId = "attack", TemplateId = "unit.tank", PassengerUnitId = 44, DesiredToggleState = true, DebugPlayerIndex = 3, DebugMetalDelta = 1200, DebugPowerDelta = 800, AiActionType = 2 };
        var decoded = ProtocolCodec.DecodeCommand(ProtocolCodec.EncodeCommand(command));
        Equal(command.Kind, decoded.Kind); Equal(command.UnitIds[1], decoded.UnitIds[1]); Equal(command.TargetX, decoded.TargetX); Equal(4, decoded.UnitTargetXs[1]); Equal(command.TemplateId, decoded.TemplateId);
        Equal(3, decoded.DebugPlayerIndex); Equal(1200, decoded.DebugMetalDelta); Equal(800, decoded.DebugPowerDelta); Equal(2, decoded.AiActionType);
        var surrender = ProtocolCodec.DecodeCommand(ProtocolCodec.EncodeCommand(new GameCommand { Kind = CommandKind.Surrender, TargetX = 4 }));
        Equal(CommandKind.Surrender, surrender.Kind); Equal(4, surrender.TargetX);
    }

    private static void AuthorityChain()
    {
        var match = Guid.NewGuid(); var chain = new AuthorityHashChain(match);
        var one = chain.Append(AuthorityFrameType.OperationBegin, new byte[] { 1 });
        var two = chain.Append(AuthorityFrameType.OperationEnd, new byte[] { 2 });
        AuthorityHashChain.VerifyNext(one, match, 1, new byte[32]); AuthorityHashChain.VerifyNext(two, match, 2, one.Hash);
    }

    private static void TamperDetection()
    {
        var match = Guid.NewGuid(); var frame = new AuthorityHashChain(match).Append(AuthorityFrameType.Resolution, new byte[] { 4 });
        frame.Payload[0] = 5;
        Throws<InvalidDataException>(() => AuthorityHashChain.VerifyNext(frame, match, 1, new byte[32]));
    }

    private static void SnapshotRoundTrip()
    {
        var source = new byte[900000]; new Random(42).NextBytes(source);
        var compressed = SnapshotCodec.Compress(source); var restored = SnapshotCodec.Decompress(compressed, source.Length);
        True(AuthorityHashChain.FixedEquals(SnapshotCodec.Hash(source), SnapshotCodec.Hash(restored)));
    }

    private static async Task PacketRoundTrip()
    {
        using var stream = new MemoryStream();
        await PacketFraming.WriteAsync(stream, new Envelope { Type = MessageType.Heartbeat, Payload = new byte[] { 1, 2, 3 } }, CancellationToken.None);
        stream.Position = 0; var decoded = await PacketFraming.ReadAsync(stream, CancellationToken.None);
        Equal(MessageType.Heartbeat, decoded?.Type); Equal(3, decoded?.Payload.Length);
    }

    private static void RoomRoundTrip()
    {
        var room = new RoomSnapshot { RoomId = Guid.NewGuid(), MatchId = Guid.NewGuid(), MatchStarted = true, DraftRevision = 7, MapId = "CP_1", MapTitle = "初次接触", FowType = 1, WinCondition = 2, QuickStart = 2 };
        var seat = new SeatInfo { SeatId = Guid.NewGuid(), LobbySlotIndex = 3, PlayerIndex = 2, DisplayName = "玩家一", OriginallyHuman = true, Connected = true, Ready = true, ClientId = Guid.NewGuid(), Controller = 0, Team = 2, Color = 4, Position = 1, PositionRandom = true, ResourceMultiplier = 1.25f, AiIntelligence = 0.7f, CommanderId = "CO_Zero", CommanderMode = 2, SkillId = "skill.zero" };
        seat.PassiveIds.Add("ps.one"); seat.PassiveIds.Add("ps.two"); room.Seats.Add(seat);
        var restored = ProtocolCodec.DecodeRoom(ProtocolCodec.EncodeRoom(room));
        Equal(room.RoomId, restored.RoomId); Equal(room.MatchId, restored.MatchId); Equal("CP_1", restored.MapId); Equal(7, restored.DraftRevision); Equal("玩家一", restored.Seats[0].DisplayName); Equal(3, restored.Seats[0].LobbySlotIndex); Equal(2, restored.Seats[0].PlayerIndex); Equal(true, restored.Seats[0].Ready); Equal(2, restored.Seats[0].Team); Equal(4, restored.Seats[0].Color); Equal(1.25f, restored.Seats[0].ResourceMultiplier); Equal("CO_Zero", restored.Seats[0].CommanderId); Equal(2, restored.Seats[0].CommanderMode); Equal("skill.zero", restored.Seats[0].SkillId); Equal("ps.two", restored.Seats[0].PassiveIds[1]);
    }

    private static void SnapshotAssembly()
    {
        var bytes = new byte[ProtocolConstants.SnapshotChunkBytes + 17]; new Random(7).NextBytes(bytes);
        var id = Guid.NewGuid();
        var manifest = new SnapshotManifest { SnapshotId = id, CompressedLength = bytes.Length, ChunkCount = 2, ContentHash = SnapshotCodec.Hash(bytes) };
        var assembler = new SnapshotAssembler(manifest);
        var tail = new byte[17]; Buffer.BlockCopy(bytes, ProtocolConstants.SnapshotChunkBytes, tail, 0, tail.Length);
        var head = new byte[ProtocolConstants.SnapshotChunkBytes]; Buffer.BlockCopy(bytes, 0, head, 0, head.Length);
        assembler.Add(new SnapshotChunk { SnapshotId = id, Index = 1, Data = tail }); assembler.Add(new SnapshotChunk { SnapshotId = id, Index = 0, Data = head });
        True(AuthorityHashChain.FixedEquals(bytes, assembler.Finish()));
    }

    private static void OperationFailureRoundTrip()
    {
        var source = new OperationFailedPayload { OperationId = Guid.NewGuid(), Reason = "failed safely" };
        var restored = ProtocolCodec.DecodeOperationFailed(ProtocolCodec.EncodeOperationFailed(source));
        Equal(source.OperationId, restored.OperationId); Equal(source.Reason, restored.Reason);
    }

    private static void ProtobufWireFormat()
    {
        var command = new GameCommand { Kind = CommandKind.Move, UnitIds = new long[] { 17 }, TargetX = -3, TargetY = 8 };
        var wireCommand = Wire.GameCommandMessage.Parser.ParseFrom(ProtocolCodec.EncodeCommand(command));
        Equal((uint)CommandKind.Move, wireCommand.Kind); Equal(17L, wireCommand.UnitIds[0]); Equal(-3, wireCommand.TargetX);

        var encodedEnvelope = ProtocolCodec.EncodeEnvelope(new Envelope { Type = MessageType.CommandRequest, Payload = new byte[] { 4, 5 } });
        var wireEnvelope = Wire.WireEnvelope.Parser.ParseFrom(encodedEnvelope);
        Equal((uint)ProtocolConstants.Version, wireEnvelope.ProtocolVersion);
        Equal((uint)MessageType.CommandRequest, wireEnvelope.MessageType);
        Equal(2, wireEnvelope.Payload.Length);
    }

    private static void AllProtobufMessagesRoundTrip()
    {
        var clientId = Guid.NewGuid(); var roomId = Guid.NewGuid(); var matchId = Guid.NewGuid();
        var hello = ProtocolCodec.DecodeHello(ProtocolCodec.EncodeHello(new HelloMessage
            { ProtocolVersion = ProtocolConstants.Version, PluginVersion = "0.4.0", GameFingerprint = "game", ContentFingerprint = "content", DisplayName = "玩家" }));
        Equal("玩家", hello.DisplayName); Equal(ProtocolConstants.Version, hello.ProtocolVersion);

        var welcome = ProtocolCodec.DecodeWelcome(ProtocolCodec.EncodeWelcome(new WelcomeMessage
            { ClientId = clientId, RoomId = roomId, MatchId = matchId, LatestFrameId = 41 }));
        Equal(clientId, welcome.ClientId); Equal(matchId, welcome.MatchId); Equal(41L, welcome.LatestFrameId);

        var request = ProtocolCodec.DecodeCommandRequest(ProtocolCodec.EncodeCommandRequest(new CommandRequest
            { ClientId = clientId, RequestId = 12, SeatId = roomId, Round = 3, AppliedFrameId = 39,
              Command = new GameCommand { Kind = CommandKind.ToggleSleep, UnitIds = new long[] { 7 }, DesiredToggleState = true } }));
        Equal(12UL, request.RequestId); Equal(CommandKind.ToggleSleep, request.Command.Kind); Equal(true, request.Command.DesiredToggleState);
        var response = ProtocolCodec.DecodeCommandResponse(ProtocolCodec.EncodeCommandResponse(new CommandResponse
            { RequestId = 12, AuthorityFrameId = 42, Reason = "ok" }));
        Equal(42L, response.AuthorityFrameId); Equal("ok", response.Reason);

        var operationId = Guid.NewGuid();
        var begin = ProtocolCodec.DecodeOperationBegin(ProtocolCodec.EncodeOperationBegin(new OperationBeginPayload
            { OperationId = operationId, SeatId = roomId, RequestId = 12, Round = 3, Command = request.Command }));
        Equal(operationId, begin.OperationId); Equal(CommandKind.ToggleSleep, begin.Command.Kind);
        Equal(operationId, ProtocolCodec.DecodeOperationEnd(ProtocolCodec.EncodeOperationEnd(new OperationEndPayload { OperationId = operationId })).OperationId);

        var resolutionSource = new ResolutionPayload { OperationId = operationId, StageId = 2, SettlementOrdinal = 4 };
        resolutionSource.RandomRecords.Add(new RandomRecord { CallSite = "hurt", Ordinal = 1, ValueKind = 2, IntegerValue = -5, FloatingValue = 0.25 });
        var resolution = ProtocolCodec.DecodeResolution(ProtocolCodec.EncodeResolution(resolutionSource));
        Equal(2, resolution.StageId); Equal("hurt", resolution.RandomRecords[0].CallSite); Equal(-5L, resolution.RandomRecords[0].IntegerValue);

        var chain = new AuthorityHashChain(matchId); var frameSource = chain.Append(AuthorityFrameType.Resolution, ProtocolCodec.EncodeResolution(resolutionSource));
        var frame = ProtocolCodec.DecodeAuthorityFrame(ProtocolCodec.EncodeAuthorityFrame(frameSource));
        Equal(frameSource.FrameId, frame.FrameId); True(AuthorityHashChain.FixedEquals(frameSource.Hash, frame.Hash));

        var snapshotId = Guid.NewGuid(); var hash = SnapshotCodec.Hash(new byte[] { 1, 2, 3 });
        var manifest = ProtocolCodec.DecodeSnapshotManifest(ProtocolCodec.EncodeSnapshotManifest(new SnapshotManifest
            { SnapshotId = snapshotId, MatchId = matchId, FrameId = 42, FrameHash = hash, CompressedLength = 3, ChunkCount = 1, ContentHash = hash }));
        Equal(snapshotId, manifest.SnapshotId); Equal(42L, manifest.FrameId);
        var chunk = ProtocolCodec.DecodeSnapshotChunk(ProtocolCodec.EncodeSnapshotChunk(new SnapshotChunk { SnapshotId = snapshotId, Index = 0, Data = new byte[] { 1, 2, 3 } }));
        Equal(3, chunk.Data.Length);
        Equal(-27L, ProtocolCodec.DecodeInt64(ProtocolCodec.EncodeInt64(-27)));
        Equal(clientId, ProtocolCodec.DecodeGuid(ProtocolCodec.EncodeGuid(clientId)));
        Equal("通知", ProtocolCodec.DecodeString(ProtocolCodec.EncodeString("通知")));
    }

    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("ok  " + name); }
    private static async Task TestAsync(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("ok  " + name); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Expected true"); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
