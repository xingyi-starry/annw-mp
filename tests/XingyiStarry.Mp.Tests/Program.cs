using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using XingyiStarry.Mp.Protocol;
using XingyiStarry.Mp.Session;
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
        Test("relay protobuf messages", RelayMessagesRoundTrip);
        Test("v16 session messages", SessionMessagesRoundTrip);
        Test("mixed move target alignment", MixedMoveFilter);
        Test("move ownership rejection", MoveOwnershipRejection);
        Test("runtime player indices ignore spawn positions", RuntimePlayerIndicesIgnoreSpawnPositions);
        Test("runtime player indices compact disabled slots", RuntimePlayerIndicesCompactDisabledSlots);
        await TestAsync("packet framing", PacketRoundTrip);
        var relayEndpoint = Environment.GetEnvironmentVariable("XINGYI_RELAY_TEST_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(relayEndpoint)) await TestAsync("C# to Go relay integration", () => RelayIntegration(relayEndpoint));
        Console.WriteLine($"PASS {passed}/{(string.IsNullOrWhiteSpace(relayEndpoint) ? 16 : 17)}"); return 0;
    }

    private static void RuntimePlayerIndicesIgnoreSpawnPositions()
    {
        var spawnPositions = new[] { 2, 0, 1 };
        var runtimeIndices = RuntimePlayerIndexResolver.Build(new[] { true, true, true });
        Equal(0, runtimeIndices[0]); Equal(1, runtimeIndices[1]); Equal(2, runtimeIndices[2]);
        True(runtimeIndices[0] != spawnPositions[0]); True(runtimeIndices[1] != spawnPositions[1]);
    }

    private static void RuntimePlayerIndicesCompactDisabledSlots()
    {
        var runtimeIndices = RuntimePlayerIndexResolver.Build(new[] { true, false, true, false, true });
        Equal(0, runtimeIndices[0]); Equal(-1, runtimeIndices[1]); Equal(1, runtimeIndices[2]);
        Equal(-1, runtimeIndices[3]); Equal(2, runtimeIndices[4]);
    }

    private static void MixedMoveFilter()
    {
        var command = new GameCommand { Kind = CommandKind.Move, UnitIds = new long[] { 1, 2, 3 },
            UnitTargetXs = new[] { 11, 22, 33 }, UnitTargetYs = new[] { 44, 55, 66 } };
        True(MoveCommandFilter.TryFilter(command, id => id == 2 ? MoveUnitDecision.Skip : MoveUnitDecision.Include));
        Equal(2, command.UnitIds.Length); Equal(1L, command.UnitIds[0]); Equal(3L, command.UnitIds[1]);
        Equal(11, command.UnitTargetXs[0]); Equal(33, command.UnitTargetXs[1]);
        Equal(44, command.UnitTargetYs[0]); Equal(66, command.UnitTargetYs[1]);
    }

    private static void MoveOwnershipRejection()
    {
        var command = new GameCommand { Kind = CommandKind.Move, UnitIds = new long[] { 1, 2, 3 },
            UnitTargetXs = new[] { 11, 22, 33 }, UnitTargetYs = new[] { 44, 55, 66 } };
        True(!MoveCommandFilter.TryFilter(command, id => id == 2 ? MoveUnitDecision.Reject : MoveUnitDecision.Skip));
        Equal(3, command.UnitIds.Length); Equal(22, command.UnitTargetXs[1]); Equal(55, command.UnitTargetYs[1]);
    }

    private static void CommandRoundTrip()
    {
        var command = new GameCommand { Kind = CommandKind.Action, UnitIds = new long[] { 9, 2 }, TargetX = -4, TargetY = 17, UnitTargetXs = new[] { 3, 4 }, UnitTargetYs = new[] { 5, 6 }, ActionCategory = 3, ActionId = "attack", TemplateId = "unit.tank", PassengerUnitId = 44, DesiredToggleState = true, DebugPlayerIndex = 3, DebugMetalDelta = 1200, DebugPowerDelta = 800, AiActionType = 2 };
        var decoded = ProtocolCodec.DecodeCommand(ProtocolCodec.EncodeCommand(command));
        Equal(command.Kind, decoded.Kind); Equal(command.UnitIds[1], decoded.UnitIds[1]); Equal(command.TargetX, decoded.TargetX); Equal(4, decoded.UnitTargetXs[1]); Equal(command.TemplateId, decoded.TemplateId);
        Equal(3, decoded.DebugPlayerIndex); Equal(1200, decoded.DebugMetalDelta); Equal(800, decoded.DebugPowerDelta); Equal(2, decoded.AiActionType);
        var surrender = ProtocolCodec.DecodeCommand(ProtocolCodec.EncodeCommand(new GameCommand { Kind = CommandKind.Surrender, TargetX = 4 }));
        Equal(CommandKind.Surrender, surrender.Kind); Equal(4, surrender.TargetX);
        Equal((ushort)20, (ushort)CommandKind.SelfDestruct);
        var selfDestruct = ProtocolCodec.DecodeCommand(ProtocolCodec.EncodeCommand(new GameCommand
            { Kind = CommandKind.SelfDestruct, UnitIds = new long[] { 17 } }));
        Equal(CommandKind.SelfDestruct, selfDestruct.Kind); Equal(17L, selfDestruct.UnitIds[0]);
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
        var room = new RoomSnapshot { RoomId = Guid.NewGuid(), MatchId = Guid.NewGuid(), MatchStarted = true, DraftRevision = 7, MapId = "my-map", MapTitle = "用户地图", FowType = 1, WinCondition = 2, QuickStart = 2, Difficulty = 20, UserMap = true, MapPreview = SnapshotCodec.Compress(new byte[] { 1, 2, 3 }) };
        var seat = new SeatInfo { SeatId = Guid.NewGuid(), LobbySlotIndex = 3, PlayerIndex = 2, DisplayName = "玩家一", OriginallyHuman = true, Connected = true, Ready = true, ClientId = Guid.NewGuid(), Controller = 0, Team = 2, Color = 4, Position = 1, PositionRandom = true, ResourceMultiplier = 1.25f, AiIntelligence = 0.7f, CommanderId = "CO_Zero", CommanderMode = 2, SkillId = "skill.zero", Defeated = true, PendingActivation = true };
        seat.PassiveIds.Add("ps.one"); seat.PassiveIds.Add("ps.two"); room.Seats.Add(seat);
        var restored = ProtocolCodec.DecodeRoom(ProtocolCodec.EncodeRoom(room));
        Equal(room.RoomId, restored.RoomId); Equal(room.MatchId, restored.MatchId); Equal("my-map", restored.MapId); Equal(7, restored.DraftRevision); Equal(20, restored.Difficulty); Equal("玩家一", restored.Seats[0].DisplayName); Equal(3, restored.Seats[0].LobbySlotIndex); Equal(2, restored.Seats[0].PlayerIndex); Equal(true, restored.Seats[0].Ready); Equal(2, restored.Seats[0].Team); Equal(4, restored.Seats[0].Color); Equal(1.25f, restored.Seats[0].ResourceMultiplier); Equal("CO_Zero", restored.Seats[0].CommanderId); Equal(2, restored.Seats[0].CommanderMode); Equal("skill.zero", restored.Seats[0].SkillId); Equal("ps.two", restored.Seats[0].PassiveIds[1]); Equal(false, restored.SavedGame); Equal(true, restored.UserMap); True(AuthorityHashChain.FixedEquals(room.MapPreview, restored.MapPreview)); Equal(true, restored.Seats[0].Defeated); Equal(true, restored.Seats[0].PendingActivation);
        Throws<InvalidDataException>(() => ProtocolCodec.EncodeRoom(new RoomSnapshot { RoomId = Guid.NewGuid(), MapPreview = new byte[ProtocolConstants.MaxMapPreviewBytes + 1] }));
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

        var roomId = Guid.NewGuid(); var clientId = Guid.NewGuid(); var targetId = Guid.NewGuid();
        var encodedEnvelope = ProtocolCodec.EncodeEnvelope(new Envelope { Type = MessageType.CommandRequest,
            Payload = new byte[] { 4, 5 }, RoomId = roomId, ClientId = clientId,
            TargetClientId = targetId, Delivery = Delivery.ToHost });
        var wireEnvelope = Wire.WireEnvelope.Parser.ParseFrom(encodedEnvelope);
        Equal((byte)0x08, encodedEnvelope[0]);
        Equal((uint)MessageType.CommandRequest, wireEnvelope.MessageType);
        Equal(2, wireEnvelope.Payload.Length);
        Equal(roomId, new Guid(wireEnvelope.RoomId.ToByteArray()));
        Equal(clientId, new Guid(wireEnvelope.ClientId.ToByteArray()));
        Equal(targetId, new Guid(wireEnvelope.TargetClientId.ToByteArray()));
        Equal(Wire.Delivery.ToHost, wireEnvelope.Delivery);
        var restoredEnvelope = ProtocolCodec.DecodeEnvelope(encodedEnvelope);
        Equal(roomId, restoredEnvelope.RoomId); Equal(clientId, restoredEnvelope.ClientId);
        Equal(targetId, restoredEnvelope.TargetClientId); Equal(Delivery.ToHost, restoredEnvelope.Delivery);
    }

    private static void AllProtobufMessagesRoundTrip()
    {
        var clientId = Guid.NewGuid(); var roomId = Guid.NewGuid(); var matchId = Guid.NewGuid();
        var hello = ProtocolCodec.DecodeHello(ProtocolCodec.EncodeHello(new HelloMessage
            { ProtocolVersion = ProtocolConstants.Version, PluginVersion = "0.8.0", GameFingerprint = "game", ContentFingerprint = "content", DisplayName = "玩家" }));
        Equal("玩家", hello.DisplayName); Equal(ProtocolConstants.Version, hello.ProtocolVersion);

        var welcome = ProtocolCodec.DecodeWelcome(ProtocolCodec.EncodeWelcome(new WelcomeMessage
            { ClientId = clientId, RoomId = roomId, MatchId = matchId, LatestFrameId = 41, Mode = WelcomeMode.JoinSelection }));
        Equal(clientId, welcome.ClientId); Equal(matchId, welcome.MatchId); Equal(41L, welcome.LatestFrameId); Equal(WelcomeMode.JoinSelection, welcome.Mode);

        var request = ProtocolCodec.DecodeCommandRequest(ProtocolCodec.EncodeCommandRequest(new CommandRequest
            { ClientId = clientId, RequestId = 12, SeatId = roomId, Round = 3, AppliedFrameId = 39,
              Command = new GameCommand { Kind = CommandKind.ToggleSleep, UnitIds = new long[] { 7 }, DesiredToggleState = true } }));
        Equal(12UL, request.RequestId); Equal(CommandKind.ToggleSleep, request.Command.Kind); Equal(true, request.Command.DesiredToggleState);
        Equal(Guid.Empty, request.ClientId);
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

    private static void RelayMessagesRoundTrip()
    {
        var roomId = Guid.NewGuid(); var clientId = Guid.NewGuid();
        var register = ProtocolCodec.DecodeRelayRegisterRoom(ProtocolCodec.EncodeRelayRegisterRoom(new RelayRegisterRoomRequest
            { RequestId = 1, RoomId = roomId, RoomName = "公共房间", HostName = "主机", Password = "secret",
              PluginVersion = "plugin", GameFingerprint = "game", ContentFingerprint = "content" }));
        Equal(roomId, register.RoomId); Equal("secret", register.Password); Equal("主机", register.HostName);

        var update = ProtocolCodec.DecodeRelayUpdateRoom(ProtocolCodec.EncodeRelayUpdateRoom(new RelayUpdateRoomRequest
            { RequestId = 2, RoomId = roomId, MapTitle = "小型密林", ConnectedPlayers = 2, HumanSeats = 3, Status = RelayRoomStatus.Playing, AvailableSeats = 1, MatchId = Guid.NewGuid() }));
        Equal(2, update.ConnectedPlayers); Equal(RelayRoomStatus.Playing, update.Status); Equal(1, update.AvailableSeats); True(update.MatchId.HasValue);

        Equal(3UL, ProtocolCodec.DecodeRelayListRoomsRequest(ProtocolCodec.EncodeRelayListRoomsRequest(3)));
        var list = new RelayListRoomsResponse { RequestId = 3 };
        list.Rooms.Add(new RelayRoomInfo { RoomId = roomId, RoomName = "公共房间", HostName = "主机", MapTitle = "小型密林",
            ConnectedPlayers = 2, HumanSeats = 3, HasPassword = true, Status = RelayRoomStatus.Waiting,
            PluginVersion = "plugin", GameFingerprint = "game", ContentFingerprint = "content", AvailableSeats = 2 });
        var restoredList = ProtocolCodec.DecodeRelayRoomList(ProtocolCodec.EncodeRelayRoomList(list));
        Equal(1, restoredList.Rooms.Count); Equal(true, restoredList.Rooms[0].HasPassword); Equal("content", restoredList.Rooms[0].ContentFingerprint);

        var join = ProtocolCodec.DecodeRelayJoinRoom(ProtocolCodec.EncodeRelayJoinRoom(new RelayJoinRoomRequest
            { RequestId = 4, RoomId = roomId, Password = "secret" }));
        Equal(roomId, join.RoomId); Equal("secret", join.Password);
        var response = ProtocolCodec.DecodeRelayControlResponse(ProtocolCodec.EncodeRelayControlResponse(new RelayControlResponse
            { RequestId = 4, Success = true, ClientId = clientId }));
        Equal(true, response.Success); Equal(clientId, response.ClientId);
        var roomRequest = ProtocolCodec.DecodeRelayRoomRequest(ProtocolCodec.EncodeRelayRoomRequest(new RelayRoomRequest { RequestId = 5, RoomId = roomId }));
        Equal(roomId, roomRequest.RoomId);
        var notice = ProtocolCodec.DecodeRelayPeerNotice(ProtocolCodec.EncodeRelayPeerNotice(new RelayPeerNotice { ClientId = clientId, Reason = "left" }));
        Equal(clientId, notice.ClientId); Equal("left", notice.Reason);
        var resume = ProtocolCodec.DecodeRelayResumeRoom(ProtocolCodec.EncodeRelayResumeRoom(new RelayResumeRoomRequest
            { RequestId = 6, RoomId = roomId, ClientId = clientId, MatchId = Guid.NewGuid() }));
        Equal(clientId, resume.ClientId);
    }

    private static void SessionMessagesRoundTrip()
    {
        Equal((ushort)34, (ushort)MessageType.ReleaseSeat); Equal((ushort)41, (ushort)MessageType.RelayResumeRoom);
        Equal((ushort)42, (ushort)MessageType.MatchStarting); Equal((ushort)16, ProtocolConstants.Version);
        var room = Guid.NewGuid(); var match = Guid.NewGuid(); var client = Guid.NewGuid(); var seat = Guid.NewGuid();
        Equal(seat, ProtocolCodec.DecodeJoinMatchRequest(ProtocolCodec.EncodeJoinMatchRequest(new JoinMatchRequest { SeatId = seat })).SeatId);
        var request = ProtocolCodec.DecodeResumeSession(ProtocolCodec.EncodeResumeSession(new ResumeSessionRequest
            { RoomId = room, MatchId = match, ClientId = client, AppliedFrameId = 9, VerifiedFrameId = 11 }));
        Equal(room, request.RoomId); Equal(9L, request.AppliedFrameId); Equal(11L, request.VerifiedFrameId);
        var accepted = ProtocolCodec.DecodeResumeSessionAccepted(ProtocolCodec.EncodeResumeSessionAccepted(new ResumeSessionAccepted
            { RoomId = room, MatchId = match, ClientId = client, LatestFrameId = 12 }));
        Equal(12L, accepted.LatestFrameId);
        var changed = ProtocolCodec.DecodeSeatControlChanged(ProtocolCodec.EncodeSeatControlChanged(new SeatControlChanged
            { SeatId = seat, PlayerIndex = 2, AiControlled = true, Reason = "timeout" }));
        Equal(2, changed.PlayerIndex); Equal(true, changed.AiControlled); Equal("timeout", changed.Reason);
    }

    private static async Task RelayIntegration(string endpoint)
    {
        var split = endpoint.LastIndexOf(':'); var hostName = endpoint.Substring(0, split); var port = int.Parse(endpoint.Substring(split + 1));
        using var host = new TcpClient(); using var browser = new TcpClient(); using var client = new TcpClient();
        await host.ConnectAsync(hostName, port); await browser.ConnectAsync(hostName, port); await client.ConnectAsync(hostName, port);
        var roomId = Guid.NewGuid();
        await PacketFraming.WriteAsync(host.GetStream(), new Envelope { Type = MessageType.RelayRegisterRoom, Delivery = Delivery.RelayControl,
            Payload = ProtocolCodec.EncodeRelayRegisterRoom(new RelayRegisterRoomRequest { RequestId = 101, RoomId = roomId,
                RoomName = "interop", HostName = "host", Password = "secret", PluginVersion = "0.8.0", GameFingerprint = "game", ContentFingerprint = "content" }) }, CancellationToken.None);
        var registered = ProtocolCodec.DecodeRelayControlResponse((await ReadType(host, MessageType.RelayControlResponse)).Payload);
        True(registered.Success); True(registered.ClientId.HasValue);

        await PacketFraming.WriteAsync(browser.GetStream(), new Envelope { Type = MessageType.RelayListRooms, Delivery = Delivery.RelayControl,
            Payload = ProtocolCodec.EncodeRelayListRoomsRequest(102) }, CancellationToken.None);
        var rooms = ProtocolCodec.DecodeRelayRoomList((await ReadType(browser, MessageType.RelayRoomList)).Payload);
        True(rooms.Rooms.Exists(value => value.RoomId == roomId && value.HasPassword));

        await PacketFraming.WriteAsync(client.GetStream(), new Envelope { Type = MessageType.RelayJoinRoom, Delivery = Delivery.RelayControl,
            Payload = ProtocolCodec.EncodeRelayJoinRoom(new RelayJoinRoomRequest { RequestId = 103, RoomId = roomId, Password = "secret" }) }, CancellationToken.None);
        var joined = ProtocolCodec.DecodeRelayControlResponse((await ReadType(client, MessageType.RelayControlResponse)).Payload);
        True(joined.Success); True(joined.ClientId.HasValue); var clientId = joined.ClientId!.Value;
        await ReadType(host, MessageType.RelayPeerJoined);

        await PacketFraming.WriteAsync(client.GetStream(), new Envelope { Type = MessageType.Hello, Delivery = Delivery.ToHost,
            RoomId = roomId, ClientId = clientId, Payload = ProtocolCodec.EncodeHello(new HelloMessage { ProtocolVersion = ProtocolConstants.Version, PluginVersion = "0.8.0",
                GameFingerprint = "game", ContentFingerprint = "content", DisplayName = "client" }) }, CancellationToken.None);
        var hello = await ReadType(host, MessageType.Hello); Equal(clientId, hello.ClientId); Equal(Delivery.ToHost, hello.Delivery);

        await PacketFraming.WriteAsync(host.GetStream(), new Envelope { Type = MessageType.Welcome, Delivery = Delivery.ToClient,
            RoomId = roomId, ClientId = registered.ClientId, TargetClientId = clientId,
            Payload = ProtocolCodec.EncodeWelcome(new WelcomeMessage { ClientId = clientId, RoomId = roomId }) }, CancellationToken.None);
        await ReadType(client, MessageType.Welcome);
        var preview = SnapshotCodec.Compress(new byte[] { 7, 8, 9 });
        await PacketFraming.WriteAsync(host.GetStream(), new Envelope { Type = MessageType.RoomState, Delivery = Delivery.Broadcast,
            RoomId = roomId, ClientId = registered.ClientId, Payload = ProtocolCodec.EncodeRoom(new RoomSnapshot
            { RoomId = roomId, MapId = "local-map", UserMap = true, MapPreview = preview }) }, CancellationToken.None);
        var forwardedRoom = ProtocolCodec.DecodeRoom((await ReadType(client, MessageType.RoomState)).Payload);
        Equal("local-map", forwardedRoom.MapId); Equal(true, forwardedRoom.UserMap);
        True(AuthorityHashChain.FixedEquals(preview, forwardedRoom.MapPreview));

        await PacketFraming.WriteAsync(client.GetStream(), new Envelope { Type = MessageType.CommandRequest, Delivery = Delivery.ToHost,
            RoomId = roomId, ClientId = clientId, Payload = ProtocolCodec.EncodeCommandRequest(new CommandRequest
            { ClientId = clientId, RequestId = 106, Command = new GameCommand { Kind = CommandKind.SelfDestruct, UnitIds = new long[] { 17 } } }) }, CancellationToken.None);
        var forwardedSelfDestruct = ProtocolCodec.DecodeCommandRequest((await ReadType(host, MessageType.CommandRequest)).Payload);
        Equal(CommandKind.SelfDestruct, forwardedSelfDestruct.Command.Kind); Equal(17L, forwardedSelfDestruct.Command.UnitIds[0]);

        var matchId = Guid.NewGuid();
        await PacketFraming.WriteAsync(host.GetStream(), new Envelope { Type = MessageType.RelayUpdateRoom, Delivery = Delivery.RelayControl,
            RoomId = roomId, ClientId = registered.ClientId,
            Payload = ProtocolCodec.EncodeRelayUpdateRoom(new RelayUpdateRoomRequest { RequestId = 104, RoomId = roomId,
                Status = RelayRoomStatus.Playing, AvailableSeats = 1, MatchId = matchId }) }, CancellationToken.None);
        True(ProtocolCodec.DecodeRelayControlResponse((await ReadType(host, MessageType.RelayControlResponse)).Payload).Success);

        client.Close();
        await ReadType(host, MessageType.RelayPeerLeft);
        using var resumed = new TcpClient(); await resumed.ConnectAsync(hostName, port);
        await PacketFraming.WriteAsync(resumed.GetStream(), new Envelope { Type = MessageType.RelayResumeRoom, Delivery = Delivery.RelayControl,
            Payload = ProtocolCodec.EncodeRelayResumeRoom(new RelayResumeRoomRequest { RequestId = 105, RoomId = roomId,
                MatchId = matchId, ClientId = clientId }) }, CancellationToken.None);
        var resumeResponse = ProtocolCodec.DecodeRelayControlResponse((await ReadType(resumed, MessageType.RelayControlResponse)).Payload);
        True(resumeResponse.Success); Equal(clientId, resumeResponse.ClientId);
        await ReadType(host, MessageType.RelayPeerJoined);
        var resumeSession = new ResumeSessionRequest { RoomId = roomId, MatchId = matchId, ClientId = clientId, AppliedFrameId = 7, VerifiedFrameId = 9 };
        await PacketFraming.WriteAsync(resumed.GetStream(), new Envelope { Type = MessageType.ResumeSession, Delivery = Delivery.ToHost,
            RoomId = roomId, ClientId = clientId, Payload = ProtocolCodec.EncodeResumeSession(resumeSession) }, CancellationToken.None);
        var forwardedResume = ProtocolCodec.DecodeResumeSession((await ReadType(host, MessageType.ResumeSession)).Payload);
        Equal(7L, forwardedResume.AppliedFrameId); Equal(9L, forwardedResume.VerifiedFrameId);
    }

    private static async Task<Envelope> ReadType(TcpClient client, MessageType type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var envelope = await PacketFraming.ReadAsync(client.GetStream(), timeout.Token) ?? throw new EndOfStreamException();
            if (envelope.Type == MessageType.Heartbeat) continue;
            if (envelope.Type != type) throw new InvalidDataException($"Expected {type}, got {envelope.Type}.");
            return envelope;
        }
    }

    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("ok  " + name); }
    private static async Task TestAsync(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("ok  " + name); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Expected true"); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
