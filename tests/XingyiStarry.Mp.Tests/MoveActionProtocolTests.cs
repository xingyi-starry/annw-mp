using System;
using System.IO;
using Google.Protobuf;
using XingyiStarry.Mp.Protocol;
using Wire = XingyiStarry.Mp.Protocol.Wire;

namespace XingyiStarry.Mp.Tests;

internal static class MoveActionProtocolTests
{
    internal static void Run()
    {
        foreach (var kind in new[] { CommandKind.BuildWithMove, CommandKind.EquipmentMoveAction })
        foreach (var position in new[] { (0, 0), (-7, 19), (13, -5) })
        {
            var proposal = new CommandRequest { RequestId = 7, SeatId = Guid.NewGuid(), Round = 2,
                Command = new GameCommand { Kind = kind, UnitIds = new long[] { 9 }, TemplateId = "factory",
                    TargetX = 30, TargetY = 40, UnitTargetXs = new[] { position.Item1 }, UnitTargetYs = new[] { position.Item2 } } };
            // Both network directions used to lose the movement destination.
            var inbound = Wire.CommandRequestMessage.Parser.ParseFrom(ProtocolCodec.EncodeCommandRequest(proposal).ToByteArray());
            var host = ProtocolCodec.DecodeCommandRequest(inbound);
            var outbound = Wire.OperationBegin.Parser.ParseFrom(ProtocolCodec.EncodeOperationBegin(new OperationBeginPayload {
                OperationId = Guid.NewGuid(), SeatId = proposal.SeatId, RequestId = 7, Command = host.Command }).ToByteArray());
            var replay = ProtocolCodec.DecodeOperationBegin(outbound).Command;
            if (replay.UnitTargetXs.Length != 1 || replay.UnitTargetYs.Length != 1 || replay.UnitTargetXs[0] != position.Item1 ||
                replay.UnitTargetYs[0] != position.Item2 || replay.TargetX != 30 || replay.TargetY != 40 || replay.TemplateId != "factory")
                throw new Exception("Composite movement parameters did not survive both network directions.");
            host.Command.UnitTargetXs = Array.Empty<int>(); host.Command.UnitTargetYs = Array.Empty<int>();
            Reject(() => ProtocolCodec.EncodeCommand(host.Command));
            if (kind == CommandKind.BuildWithMove) outbound.Command.BuildWithMove.MovePosition = null;
            else outbound.Command.EquipmentMoveAction.MovePosition = null;
            Reject(() => ProtocolCodec.DecodeOperationBegin(outbound));
        }
    }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Missing movement destination was accepted."); }
}
