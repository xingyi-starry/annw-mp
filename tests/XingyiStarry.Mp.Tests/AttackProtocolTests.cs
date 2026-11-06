using System;
using System.IO;
using XingyiStarry.Mp.Protocol;
using XingyiStarry.Mp.Session;

namespace XingyiStarry.Mp.Tests;

internal static class AttackProtocolTests
{
    internal static void Events()
    {
        var begin = new OperationBeginPayload { OperationId = Guid.NewGuid(), SeatId = Guid.NewGuid(), LiveSettlements = true,
            Command = new GameCommand { Kind = CommandKind.EquipmentAction, UnitIds = new long[] { 234 } } };
        if (!ProtocolCodec.DecodeOperationBegin(ProtocolCodec.EncodeOperationBegin(begin)).LiveSettlements) throw new Exception("Live settlement flag lost.");
        foreach (var kind in Enum.GetValues<AttackSettlementKind>())
        {
            var value = new ResolutionPayload { OperationId = begin.OperationId, StageId = 1, SettlementOrdinal = 7,
                AttackSettlement = new AttackSettlement { Kind = kind, BatchId = 3, UnitId = 234, ActionName = "HiveMissile",
                    TargetX = -9, TargetY = 2, OriginX = -6, OriginY = -3, Data = 0.125f } };
            var copy = ProtocolCodec.DecodeResolution(ProtocolCodec.EncodeResolution(value));
            var hit = copy.AttackSettlement!;
            if (copy.OperationId != begin.OperationId || copy.SettlementOrdinal != 7 || hit.Kind != kind || hit.BatchId != 3 ||
                hit.UnitId != 234 || hit.ActionName != "HiveMissile" || hit.TargetX != -9 || hit.OriginY != -3 || hit.Data != 0.125f)
                throw new Exception("Attack event changed on the wire.");
            value.StageId = 0; MustReject(() => ProtocolCodec.EncodeResolution(value));
            value.StageId = 1; value.SettlementOrdinal = 0; MustReject(() => ProtocolCodec.EncodeResolution(value));
            value.SettlementOrdinal = 7; value.AttackSettlement.Kind = (AttackSettlementKind)99; MustReject(() => ProtocolCodec.EncodeResolution(value));
            value.AttackSettlement.Kind = kind; value.AttackSettlement.Data = float.NaN; MustReject(() => ProtocolCodec.EncodeResolution(value));
        }
        var hello = new HelloMessage { ProtocolVersion = 19, PluginVersion = "same", GameFingerprint = "game", ContentFingerprint = "content", DisplayName = "name" };
        if (HandshakeValidator.TryValidate(hello, "same", "game", "content", "", out _, out _)) throw new Exception("Old settlement semantics accepted.");
    }

    internal static void StreamedRandom()
    {
        var host = new RandomTape(); host.BeginRecording();
        host.Float("hurt", () => 0.25f);
        var first = host.DrainRecording();
        host.Integer("wreck", () => 42);
        var second = host.DrainRecording();
        host.Float("after", () => 0.75f);
        var final = host.EndRecording();
        if (first.Count != 1 || second[0].Ordinal != 1 || final[0].Ordinal != 2) throw new Exception("Stage boundaries reset random ordinals.");
        var client = new RandomTape(); client.BeginReplay(first);
        if (client.Float("hurt", () => throw new Exception("Local RNG called")) != 0.25f) throw new Exception();
        client.AppendReplay(second);
        if (client.Integer("wreck", () => throw new Exception("Local RNG called")) != 42) throw new Exception();
        client.AppendReplay(final);
        MustReject(client.EndReplay);
        if (client.Float("after", () => throw new Exception("Local RNG called")) != 0.75f) throw new Exception();
        client.EndReplay();
        client.BeginReplay(second); MustReject(() => client.Integer("wreck", () => 0));
        client.BeginReplay(first); MustReject(() => client.Float("different target callback", () => 0));
    }

    private static void MustReject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Malformed settlement accepted.");
    }
}
