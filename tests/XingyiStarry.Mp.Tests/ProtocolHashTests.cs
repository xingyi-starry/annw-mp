using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using XingyiStarry.Mp.Protocol;
using Wire = XingyiStarry.Mp.Protocol.Wire;

namespace XingyiStarry.Mp.Tests;

internal static class ProtocolHashTests
{
    internal sealed class Vector
    {
        public string Name { get; set; } = "";
        public string Frame { get; set; } = "";
        public string Hash { get; set; } = "";
    }
    internal static void Run()
    {
        var vectors = new List<Vector>();
        foreach (var field in Wire.AuthorityFrameMessage.Descriptor.Oneofs.First().Fields)
            Add(vectors, "frame/" + field.Name, (AuthorityFrameType)(field.FieldNumber - 100), Minimal(field.MessageType));
        foreach (var field in Wire.GameCommandMessage.Descriptor.Oneofs.First().Fields.Where(f => f.GetOptions()?.Deprecated != true))
        {
            var command = new Wire.GameCommandMessage(); field.Accessor.SetValue(command, Minimal(field.MessageType));
            Add(vectors, "command/" + field.Name, AuthorityFrameType.OperationBegin, new Wire.OperationBegin { OperationId = GuidBytes(1), SeatId = GuidBytes(2), Command = command, RequestId = ulong.MaxValue, Round = -1 });
        }
        foreach (var field in Wire.SettlementEvent.Descriptor.Oneofs.First().Fields)
        {
            var node = new Wire.SettlementEvent { SourceUnitId = long.MaxValue, TargetUnitId = 123, PlayerIndex = 4 };
            field.Accessor.SetValue(node, Minimal(field.MessageType));
            node.RandomResult = new Wire.RandomResult { DamageRoll = new Wire.RandomRecordMessage { CallSite = "UnitData.Hurt", Floating = -0.0 }, DamageVariation = new Wire.RandomRecordMessage { CallSite = "UnitData.Hurt", Ordinal = 0, Floating = 0.125 } };
            node.Children.Add(new Wire.SettlementEvent { TargetUnitId = 9, Heal = new Wire.HealEvent { Amount = 1.5f } });
            Add(vectors, "settlement/" + field.Name, AuthorityFrameType.Resolution, new Wire.Resolution { OperationId = GuidBytes(3), Event = node });
        }
        // Optional zero preserves IEEE -0 through protobuf; absent and present zero differ.
        var zeroNode = new Wire.SettlementEvent { Damage = new Wire.DamageEvent() };
        Add(vectors, "presence/absent", AuthorityFrameType.Resolution, new Wire.Resolution { OperationId = GuidBytes(3), Event = zeroNode.Clone() });
        zeroNode.Damage.Damage = 0f;
        Add(vectors, "presence/zero", AuthorityFrameType.Resolution, new Wire.Resolution { OperationId = GuidBytes(3), Event = zeroNode.Clone() });
        zeroNode.Damage.Damage = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        Add(vectors, "ieee/negative-zero", AuthorityFrameType.Resolution, new Wire.Resolution { OperationId = GuidBytes(3), Event = zeroNode.Clone() });
        var nested = new Wire.GameCommandMessage { ResolvedUnitActions = new Wire.ResolvedUnitActionsCommand() };
        foreach (var field in Wire.ResolvedUnitAction.Descriptor.Oneofs.First(x => !x.IsSynthetic).Fields)
        {
            var step = new Wire.ResolvedUnitAction { UnitId = 42 }; field.Accessor.SetValue(step, Minimal(field.MessageType)); nested.ResolvedUnitActions.ResolvedActions.Add(step);
        }
        Add(vectors, "repeated/resolved", AuthorityFrameType.OperationBegin, new Wire.OperationBegin { OperationId = GuidBytes(1), SeatId = GuidBytes(2), Command = nested });
        var orderedHash = vectors.Last().Hash;
        var reversed = nested.ResolvedUnitActions.ResolvedActions.Reverse().Select(x => x.Clone()).ToArray();
        nested.ResolvedUnitActions.ResolvedActions.Clear(); nested.ResolvedUnitActions.ResolvedActions.Add(reversed);
        Add(vectors, "repeated/reversed", AuthorityFrameType.OperationBegin, new Wire.OperationBegin { OperationId = GuidBytes(1), SeatId = GuidBytes(2), Command = nested });
        if (vectors.Last().Hash == orderedHash) throw new Exception("Repeated message order omitted from hash.");
        var random = new Wire.RandomResult { DeathWreckScale = new Wire.RandomRecordMessage { CallSite = "UnitData.Die", Floating = 1.1 } };
        random.CommanderPositions.Add(new Wire.UnitRandomResult { UnitId = 42, Values = new Wire.RandomRecordList { Records = { new Wire.RandomRecordMessage { CallSite = "Player.AutoSetCmdPos", Integer = 0 } } } });
        random.ExperienceShares.Add(new Wire.UnitRandomResult { UnitId = 42, Values = new Wire.RandomRecordList { Records = { new Wire.RandomRecordMessage { CallSite = "UnitEffect_ShareExp.PreAddExp/Shuffle", Integer = 3 }, new Wire.RandomRecordMessage { CallSite = "UnitEffect_ShareExp.PreAddExp/Shuffle", Ordinal = 1, Integer = 0 } } } });
        random.Wrecks.Add(new Wire.TileRandomResult { X = -1, Y = 2, Values = new Wire.RandomRecordList { Records = { new Wire.RandomRecordMessage { CallSite = "GameTileData.CreateWreck", Floating = .9 } } } });
        zeroNode.RandomResult = random;
        Add(vectors, "random/wrappers", AuthorityFrameType.Resolution, new Wire.Resolution { OperationId = GuidBytes(3), Event = zeroNode });
        if (vectors.TakeLast(5).Select(v => v.Hash).Distinct().Count() != 5) throw new Exception("Presence or nested values omitted from hash.");
        var targets = new Wire.AttackCellEvent { ActionName = "attack", Data = 0f };
        targets.Targets.Add(new Wire.AttackTargetResult { Position = new Wire.ActionMovePosition { X = -7, Y = 2 }, Damage = 0f, Unit = new Wire.AttackUnitRecipient { UnitId = 42 } });
        targets.Targets.Add(new Wire.AttackTargetResult { Position = new Wire.ActionMovePosition(), Damage = 12.5f, Shield = new Wire.AttackShieldRecipient { CasterUnitId = 71 } });
        void TargetVector(string name) => Add(vectors, "targets/" + name, AuthorityFrameType.Resolution,
            new Wire.Resolution { OperationId = GuidBytes(3), Event = new Wire.SettlementEvent { AttackCell = targets.Clone() } });
        TargetVector("unit-and-shield");
        targets.Targets[0].Unit.UnitId++;
        TargetVector("recipient");
        targets.Targets[0].Damage = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        TargetVector("negative-zero");
        var firstTarget = targets.Targets[0]; targets.Targets.RemoveAt(0); targets.Targets.Add(firstTarget);
        TargetVector("order");
        if (vectors.TakeLast(4).Select(v => v.Hash).Distinct().Count() != 4) throw new Exception("Native attack targets omitted from hash.");
        var path = Environment.GetEnvironmentVariable("XINGYI_GENERATE_HASH_VECTORS");
        if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, JsonSerializer.Serialize(vectors, new JsonSerializerOptions { WriteIndented = true }));
        else
        {
            var expected = JsonSerializer.Deserialize<List<Vector>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "hash-v22.json")))!;
            if (expected.Count != vectors.Count) throw new Exception("Hash vector count changed; review field encoding.");
            for (var i = 0; i < vectors.Count; i++) if (expected[i].Name != vectors[i].Name || expected[i].Hash != vectors[i].Hash) throw new Exception("Hash vector changed: " + vectors[i].Name);
        }
    }
    private static ByteString GuidBytes(int value) { var bytes = new byte[16]; bytes[0] = (byte)value; return ByteString.CopyFrom(bytes); }
    private static void Add(List<Vector> vectors, string name, AuthorityFrameType type, IMessage payload)
    {
        var frame = new AuthorityFrame { MatchId = new Guid(GuidBytes(7).ToByteArray()), FrameId = long.MaxValue, PrevHash = Enumerable.Repeat((byte)0xA5, 32).ToArray(), Message = (AuthorityMessage)typeof(AuthorityMessages).GetMethod(type.ToString())!.Invoke(null, new object[] { payload })! };
        frame.Hash = AuthorityHashChain.ComputeHash(frame);
        var wire = ProtocolCodec.EncodeAuthorityFrame(frame);
        var restored = ProtocolCodec.DecodeAuthorityFrame(Wire.AuthorityFrameMessage.Parser.ParseFrom(wire.ToByteArray()));
        if (!frame.Hash.SequenceEqual(AuthorityHashChain.ComputeHash(restored))) throw new Exception("Hash changed on wire: " + name);
        vectors.Add(new Vector { Name = name, Frame = Convert.ToBase64String(wire.ToByteArray()), Hash = Convert.ToHexString(frame.Hash).ToLowerInvariant() });
        // Each active field must affect the explicit hash, including defaults and presence.
        CheckFields(frame, payload);
    }
    private static void CheckFields(AuthorityFrame frame, IMessage message, int depth = 0)
    {
        if (depth > 5) return;
        foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
        {
            if (field.GetOptions()?.Deprecated == true) continue;
            if (field.ContainingOneof is not null && !field.ContainingOneof.IsSynthetic && field.ContainingOneof.Accessor.GetCaseFieldDescriptor(message) != field) continue;
            var present = !field.HasPresence || field.Accessor.HasValue(message);
            var old = field.Accessor.GetValue(message);
            if (field.FieldType == FieldType.Message && !field.IsRepeated && old is IMessage child) { CheckFields(frame, child, depth + 1); continue; }
            if (field.FieldType == FieldType.Message) continue;
            var before = AuthorityHashChain.ComputeHash(frame);
            if (field.IsRepeated)
            {
                var list = (IList)old; list.Add(Value(field));
                if (before.SequenceEqual(AuthorityHashChain.ComputeHash(frame))) throw new Exception("Repeated field omitted from hash: " + field.FullName);
                list.RemoveAt(list.Count - 1); continue;
            }
            var changed = Value(field);
            if (Equals(changed, old)) changed = field.FieldType switch { FieldType.String => (object)"other", FieldType.Bytes => ByteString.CopyFrom(new byte[] { 3, 2 }), FieldType.Bool => false, FieldType.Float => 2.25f, FieldType.Double => 2.25, FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => 2L, FieldType.UInt64 or FieldType.Fixed64 => 2UL, FieldType.UInt32 or FieldType.Fixed32 => 2U, _ => 2 };
            field.Accessor.SetValue(message, changed);
            if (before.SequenceEqual(AuthorityHashChain.ComputeHash(frame))) throw new Exception("Field omitted from hash: " + field.FullName);
            if (present) field.Accessor.SetValue(message, old); else field.Accessor.Clear(message);
        }
    }
    private static IMessage Minimal(MessageDescriptor descriptor, int depth = 0)
    {
        var message = descriptor.Parser.ParseFrom(Array.Empty<byte>());
        foreach (var oneof in descriptor.Oneofs.Where(x => !x.IsSynthetic))
        {
            var field = oneof.Fields.First(); field.Accessor.SetValue(message, field.FieldType == FieldType.Message ? Minimal(field.MessageType, depth + 1) : Value(field));
        }
        foreach (var field in descriptor.Fields.InFieldNumberOrder())
        {
            if (field.FieldType == FieldType.Message && field.MessageType.Name == "ActionMovePosition")
            { field.Accessor.SetValue(message, Minimal(field.MessageType, depth + 1)); continue; }
            if (field.GetOptions()?.Deprecated == true || field.IsRepeated || field.ContainingOneof is not null || field.FieldType == FieldType.Message) continue;
            field.Accessor.SetValue(message, Value(field));
        }
        return message;
    }
    private static object Value(FieldDescriptor field) => field.FieldType switch
    {
        FieldType.Bool => true,
        FieldType.String => "字段/field",
        FieldType.Bytes => ByteString.CopyFrom(new byte[] { 0, 1, 255 }),
        FieldType.Float => 0.125f,
        FieldType.Double => 0.125,
        FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => -7L,
        FieldType.UInt64 or FieldType.Fixed64 => 7UL,
        FieldType.UInt32 or FieldType.Fixed32 => 7U,
        _ => 7
    };
}
