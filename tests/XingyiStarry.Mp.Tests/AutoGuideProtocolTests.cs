using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Google.Protobuf;
using XingyiStarry.Mp.Protocol;
using XingyiStarry.Mp.Session;
using MpContext = XingyiStarry.Mp.Session.ExecutionContext;
using Wire = XingyiStarry.Mp.Protocol.Wire;

namespace XingyiStarry.Mp.Tests;

internal static class AutoGuideProtocolTests
{
    internal static GameCommand MovementBatch()
    {
        var command = new GameCommand { Kind = CommandKind.ResolvedUnitActions, UnitIds = new long[] { 8, 3 } };
        command.ResolvedActions.Add(new ResolvedUnitAction { UnitId = 8, Type = ResolvedActionType.Move, MoveX = -7, MoveY = 11 });
        command.ResolvedActions.Add(new ResolvedUnitAction { UnitId = 3, Type = ResolvedActionType.Move, MoveX = 2, MoveY = -5, SkipAnimations = true });
        return command;
    }

    internal static void RoundTrip()
    {
        var batch = ProtocolCodec.DecodeCommand(ProtocolCodec.EncodeCommand(MovementBatch()));
        Require(batch.Kind == CommandKind.ResolvedUnitActions && batch.UnitIds[0] == 8 && batch.UnitIds[1] == 3);
        Require(batch.ResolvedActions.Count == 2 && batch.ResolvedActions[0].MoveX == -7 && batch.ResolvedActions[1].SkipAnimations);
        foreach (var type in new[] { ResolvedActionType.Action, ResolvedActionType.Create, ResolvedActionType.Unload })
        {
            var command = new GameCommand { Kind = CommandKind.ResolvedUnitActions, UnitIds = new long[] { 55 } };
            command.ResolvedActions.Add(new ResolvedUnitAction { UnitId = 55, Type = type, MoveX = -1, MoveY = 2,
                TargetX = 13, TargetY = -14, ActionCategory = 6, TemplateId = "resolved.factory", PassengerUnitId = 71 });
            var operation = new OperationBeginPayload { OperationId = Guid.NewGuid(), SeatId = Guid.NewGuid(), RequestId = 99, Command = command };
            var result = ProtocolCodec.DecodeOperationBegin(ProtocolCodec.EncodeOperationBegin(operation)).Command.ResolvedActions[0];
            Require(result.UnitId == 55 && result.Type == type && result.TargetY == -14 && result.ActionCategory == 6 &&
                result.TemplateId == (type==ResolvedActionType.Create?"resolved.factory":"") && result.PassengerUnitId == (type==ResolvedActionType.Unload?71:0));
        }
    }

    internal static void AuthorityBoundary()
    {
        Require(CommandRules.MayRequest(CommandKind.AutoGuideStart));
        var request = ProtocolCodec.DecodeCommand(ProtocolCodec.EncodeCommand(new GameCommand { Kind = CommandKind.AutoGuideStart, UnitIds = new long[] { 3 } }));
        Require(request.Kind == CommandKind.AutoGuideStart);
        Require(CommandRules.MayBroadcast(CommandKind.ResolvedUnitActions) && !CommandRules.MayRequest(CommandKind.ResolvedUnitActions));
        foreach (var kind in new[] { CommandKind.AiUnitAction, CommandKind.AiSkill, CommandKind.TurnAdvance, CommandKind.DebugAddResources })
            Require(!CommandRules.MayRequest(kind));
        foreach (var kind in new[] { CommandKind.AutoGuideStart, (CommandKind)10 })
        {
            Require(!CommandRules.MayBroadcast(kind));
            Throws<InvalidDataException>(() => ProtocolCodec.EncodeOperationBegin(new OperationBeginPayload { Command = new GameCommand { Kind = kind } }));
            var forged = new Wire.OperationBegin { OperationId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                SeatId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()), Command = kind == CommandKind.AutoGuideStart ? new Wire.GameCommandMessage { AutoGuideStart=new Wire.AutoGuideStartCommand() } : new Wire.GameCommandMessage() };
            Throws<InvalidDataException>(() => ProtocolCodec.DecodeOperationBegin(forged));
        }
        Throws<InvalidDataException>(() => ProtocolCodec.DecodeCommand(new Wire.GameCommandMessage()));
        Require(!CommandRules.MayRequest((CommandKind)10));
        Require(!HandshakeValidator.TryValidate(new HelloMessage { ProtocolVersion = 18, PluginVersion = "p", GameFingerprint = "g",
            ContentFingerprint = "c", DisplayName = "old" }, "p", "g", "c", "", out _, out _));
    }

    internal static void MalformedBatch()
    {
        var batch = MovementBatch(); batch.ResolvedActions[1].UnitId = 8;
        Throws<InvalidDataException>(() => ProtocolCodec.EncodeCommand(batch));
        batch = MovementBatch(); batch.ResolvedActions[1].Type = ResolvedActionType.Action;
        Throws<InvalidDataException>(() => ProtocolCodec.EncodeCommand(batch));
        batch = MovementBatch(); batch.UnitIds = new long[] { 8 };
        Throws<InvalidDataException>(() => ProtocolCodec.EncodeCommand(batch));
        batch = MovementBatch(); batch.ResolvedActions[0].Type = (ResolvedActionType)99;
        Throws<InvalidDataException>(() => ProtocolCodec.EncodeCommand(batch));
        batch = MovementBatch(); batch.Kind = CommandKind.AutoGuideStart;
        Throws<InvalidDataException>(() => ProtocolCodec.EncodeCommand(batch));
        var forged = new Wire.GameCommandMessage { ResolvedUnitActions = new Wire.ResolvedUnitActionsCommand() };
        for (var index = 0; index <= CommandRules.MaxResolvedActions; index++)
        {
            forged.ResolvedUnitActions.UnitIds.Add(index); forged.ResolvedUnitActions.ResolvedActions.Add(new Wire.ResolvedUnitAction { UnitId = index });
        }
        Throws<InvalidDataException>(() => ProtocolCodec.DecodeCommand(forged));
    }

    internal static void CoroutineScopes()
    {
        var observations = new List<ExecutionOrigin>();
        IEnumerator Child() { observations.Add(MpContext.Origin); yield return 0f; observations.Add(MpContext.Origin); }
        IEnumerator Root() { yield return Child(); observations.Add(MpContext.Origin); yield return 0.25f; }
        using var stack = new CoroutineStack(Root());
        using (MpContext.Enter(ExecutionOrigin.ClientReplay)) { Require(stack.MoveNext()); Require((float)stack.Current! == 0f); }
        Require(MpContext.Origin == ExecutionOrigin.LocalInput);
        using (MpContext.Enter(ExecutionOrigin.ClientReplay)) { Require(stack.MoveNext()); Require((float)stack.Current! == 0.25f); }
        Require(observations.Count == 3 && observations.TrueForAll(origin => origin == ExecutionOrigin.ClientReplay));
        Require(!stack.MoveNext());
    }

    internal static void CoroutineFailures()
    {
        var disposed = false;
        IEnumerator Child() { yield return 0f; throw new InvalidDataException("native nested failure"); }
        IEnumerator Root() { try { yield return Child(); } finally { disposed = true; } }
        using (var stack = new CoroutineStack(Root()))
        {
            Require(stack.MoveNext());
            Throws<InvalidDataException>(() => { using (MpContext.Enter(ExecutionOrigin.HostAuthority)) stack.MoveNext(); });
            Require(MpContext.Origin == ExecutionOrigin.LocalInput);
        }
        Require(disposed);
    }

    internal static void QueueReset()
    {
        var queue = new OperationQueue();
        var request = new CommandRequest { ClientId = Guid.NewGuid(), RequestId = 8 };
        Require(queue.Enqueue(request) && queue.TryBegin(out _));
        queue.Reset(); Require(!queue.IsExecuting && queue.Enqueue(request) && queue.TryBegin(out _));
        queue.Complete(); Require(!queue.TryBegin(out _));
    }

    internal static void ArrivalOrder()
    {
        foreach (var completionOrder in new[] { new long[] { 3, 8, 12 }, new long[] { 12, 3, 8 }, new long[] { 8, 12, 3 } })
        {
            var order = new OrderedActionBatch(new long[] { 8, 3, 12 });
            order.Start(8); Require(!order.MayFinish(8));
            order.Start(3); order.Start(12);
            var ready = new HashSet<long>(); var committed = new List<long>();
            foreach (var unit in completionOrder)
            {
                ready.Add(unit);
                bool progressed;
                do
                {
                    progressed = false;
                    foreach (var next in new long[] { 8, 3, 12 })
                        if (ready.Contains(next) && !committed.Contains(next) && order.MayFinish(next))
                        { order.Complete(next); committed.Add(next); progressed = true; }
                } while (progressed);
            }
            Require(string.Join(",", committed) == "8,3,12");
        }
        // A no-op finishes without a movement iterator; it still unblocks its successors.
        var withNoOp = new OrderedActionBatch(new long[] { 1, 2 });
        withNoOp.Start(1); withNoOp.Complete(1); withNoOp.Start(2); Require(withNoOp.MayFinish(2));
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Auto-guide regression assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
