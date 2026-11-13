using System;
using System.Linq;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using System.IO;
using XingyiStarry.Mp.Protocol;
using XingyiStarry.Mp.Session;

namespace XingyiStarry.Mp.Tests;

internal static class AttackProtocolTests
{
    internal static void Events()
    {
        foreach (var field in Protocol.Wire.SettlementEvent.Descriptor.Oneofs.First().Fields)
        {
            var node = new Protocol.Wire.SettlementEvent { SourceUnitId = 234, TargetUnitId = 159 };
            field.Accessor.SetValue(node, field.MessageType.Parser.ParseFrom(Array.Empty<byte>()));
            var value = new ResolutionPayload { OperationId = Guid.NewGuid(), Event = node };
            var wire = ProtocolCodec.EncodeResolution(value);
            var copy = ProtocolCodec.DecodeResolution(Protocol.Wire.Resolution.Parser.ParseFrom(wire.ToByteArray()));
            if (copy.OperationId != value.OperationId || !node.Equals(copy.Event)) throw new Exception("Settlement changed on wire.");
            RejectDeprecated(wire);
        }
        var hello = new HelloMessage { ProtocolVersion = 20, PluginVersion = "same", GameFingerprint = "game", ContentFingerprint = "content", DisplayName = "name" };
        if (HandshakeValidator.TryValidate(hello, "same", "game", "content", "", out _, out _)) throw new Exception("Old settlement semantics accepted.");
        MustReject(() => ProtocolCodec.DecodeResolution(new Protocol.Wire.Resolution()));
    }

    internal static void StreamedRandom()
    {
        var damage = new Protocol.Wire.RandomResult(); var death = new Protocol.Wire.RandomResult();
        using (var host = new RandomResultContext(damage, false, 159))
        {
            host.Float("UnitData.Hurt", () => 0f); host.Float("UnitData.Hurt", () => 0.25f);
            using (var child = new RandomResultContext(death, false, 159)) child.Float("UnitData.Die", () => 1.1f);
            if (RandomResultContext.Current != host) throw new Exception("Nested context leaked.");
        }
        if (RandomResultContext.Current is not null) throw new Exception("Random context leaked.");
        using (var client = new RandomResultContext(damage, true, 159))
        {
            if (client.Float("UnitData.Hurt", () => throw new Exception("Local RNG")) != 0f) throw new Exception("Zero result lost.");
            MustReject(client.Complete);
            client.Float("UnitData.Hurt", () => throw new Exception("Local RNG")); client.Complete();
        }
        using (var client = new RandomResultContext(death, true, 159))
        { if (client.Float("UnitData.Die", () => 0) != 1.1f) throw new Exception(); client.Complete(); }
        using (var client = new RandomResultContext(new Protocol.Wire.RandomResult(), true, 159)) MustReject(() => client.Float("UnitData.Hurt", () => 0));
        var malformed = new Protocol.Wire.RandomResult();
        malformed.CommanderPositions.Add(new Protocol.Wire.UnitRandomResult { UnitId = 999, Values = new Protocol.Wire.RandomRecordList() });
        using (var client = new RandomResultContext(malformed, true, 159)) MustReject(client.Complete);
        malformed = new Protocol.Wire.RandomResult();
        malformed.Wrecks.Add(new Protocol.Wire.TileRandomResult { X = 1, Y = 2, Values = new Protocol.Wire.RandomRecordList() });
        using (var client = new RandomResultContext(malformed, true, 159)) MustReject(client.Complete);
    }

    internal static void AllEnvelopeCases()
    {
        foreach (var field in Protocol.Wire.WireEnvelope.Descriptor.Oneofs.First().Fields)
        {
            var wire = new Protocol.Wire.WireEnvelope();
            field.Accessor.SetValue(wire, field.MessageType.Parser.ParseFrom(Array.Empty<byte>()));
            var envelope = ProtocolCodec.DecodeEnvelope(wire.ToByteArray());
            var restored = Protocol.Wire.WireEnvelope.Parser.ParseFrom(ProtocolCodec.EncodeEnvelope(envelope));
            if (!wire.Equals(restored)) throw new Exception("Envelope branch changed: " + field.Name);
            RejectDeprecated(restored);
        }
        MustReject(() => ProtocolCodec.DecodeEnvelope(new Protocol.Wire.WireEnvelope().ToByteArray()));
    }

    private static void RejectDeprecated(IMessage message)
    {
        var input = new CodedInputStream(message.ToByteArray()); uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var field = message.Descriptor.FindFieldByNumber((int)(tag >> 3));
            if (field?.GetOptions()?.Deprecated == true) throw new Exception("Deprecated field written: " + field.FullName);
            input.SkipLastField();
        }
    }

    private static void MustReject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Malformed settlement accepted.");
    }
}
