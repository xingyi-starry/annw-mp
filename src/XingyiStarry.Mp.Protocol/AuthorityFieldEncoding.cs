// Explicit v21 field encoding. Generated from the canonical schema; no protobuf serialization/reflection at runtime.
using System;
using System.IO;
using System.Text;
using Google.Protobuf;
using Wire = XingyiStarry.Mp.Protocol.Wire;
namespace XingyiStarry.Mp.Protocol;
internal static class AuthorityFieldEncoding
{
    internal static byte[] Encode(AuthorityFrame frame)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        Text(writer, ProtocolConstants.HashDomain);
        Bytes(writer, ByteString.CopyFrom(frame.MatchId.ToByteArray()));
        writer.Write(frame.FrameId); Bytes(writer, ByteString.CopyFrom(frame.PrevHash));
        writer.Write((uint)(100 + (int)frame.FrameType));
        switch (frame.FrameType)
        {
            case AuthorityFrameType.OperationBegin: Write(writer, (Wire.OperationBegin)frame.Payload); break;
            case AuthorityFrameType.Resolution: Write(writer, (Wire.Resolution)frame.Payload); break;
            case AuthorityFrameType.OperationEnd: Write(writer, (Wire.OperationEnd)frame.Payload); break;
            case AuthorityFrameType.TurnPhase: Write(writer, (Wire.AuthorityTurnPhase)frame.Payload); break;
            case AuthorityFrameType.SeatChanged: Write(writer, (Wire.SeatControlChanged)frame.Payload); break;
            case AuthorityFrameType.MatchEnded: Write(writer, (Wire.AuthorityMatchEnded)frame.Payload); break;
            case AuthorityFrameType.OperationFailed: Write(writer, (Wire.OperationFailed)frame.Payload); break;
            default: throw new InvalidDataException("Unknown authority frame body.");
        }
        return stream.ToArray();
    }
    private static void Text(BinaryWriter writer, string value) { var data = Encoding.UTF8.GetBytes(value); writer.Write((uint)data.Length); writer.Write(data); }
    private static void Bytes(BinaryWriter writer, ByteString value) { writer.Write((uint)value.Length); writer.Write(value.ToByteArray()); }
    private static void Write(BinaryWriter writer, Wire.GameCommandMessage value)
    {
        writer.Write((uint)value.BodyCase);
        switch (value.BodyCase)
        {
            case Wire.GameCommandMessage.BodyOneofCase.Move: Write(writer, value.Move); break;
            case Wire.GameCommandMessage.BodyOneofCase.Action: Write(writer, value.Action); break;
            case Wire.GameCommandMessage.BodyOneofCase.EquipmentAction: Write(writer, value.EquipmentAction); break;
            case Wire.GameCommandMessage.BodyOneofCase.EquipmentMoveAction: Write(writer, value.EquipmentMoveAction); break;
            case Wire.GameCommandMessage.BodyOneofCase.BuildWithMove: Write(writer, value.BuildWithMove); break;
            case Wire.GameCommandMessage.BodyOneofCase.Skill: Write(writer, value.Skill); break;
            case Wire.GameCommandMessage.BodyOneofCase.UndoMove: Write(writer, value.UndoMove); break;
            case Wire.GameCommandMessage.BodyOneofCase.EndTurn: Write(writer, value.EndTurn); break;
            case Wire.GameCommandMessage.BodyOneofCase.AutoGuideStart: Write(writer, value.AutoGuideStart); break;
            case Wire.GameCommandMessage.BodyOneofCase.DebugAddResources: Write(writer, value.DebugAddResources); break;
            case Wire.GameCommandMessage.BodyOneofCase.DebugFillSkill: Write(writer, value.DebugFillSkill); break;
            case Wire.GameCommandMessage.BodyOneofCase.AiUnitAction: Write(writer, value.AiUnitAction); break;
            case Wire.GameCommandMessage.BodyOneofCase.AiSkill: Write(writer, value.AiSkill); break;
            case Wire.GameCommandMessage.BodyOneofCase.TurnAdvance: Write(writer, value.TurnAdvance); break;
            case Wire.GameCommandMessage.BodyOneofCase.Surrender: Write(writer, value.Surrender); break;
            case Wire.GameCommandMessage.BodyOneofCase.ToggleStandby: Write(writer, value.ToggleStandby); break;
            case Wire.GameCommandMessage.BodyOneofCase.ToggleSleep: Write(writer, value.ToggleSleep); break;
            case Wire.GameCommandMessage.BodyOneofCase.Stay: Write(writer, value.Stay); break;
            case Wire.GameCommandMessage.BodyOneofCase.SelfDestruct: Write(writer, value.SelfDestruct); break;
            case Wire.GameCommandMessage.BodyOneofCase.ResolvedUnitActions: Write(writer, value.ResolvedUnitActions); break;
            default: throw new InvalidDataException("Missing oneof value.");
        }
    }
    private static void Write(BinaryWriter writer, Wire.ResolvedUnitAction value)
    {
        writer.Write(value.UnitId);
        writer.Write(value.SkipAnimations);
        writer.Write((uint)value.BodyCase);
        switch (value.BodyCase)
        {
            case Wire.ResolvedUnitAction.BodyOneofCase.Move: Write(writer, value.Move); break;
            case Wire.ResolvedUnitAction.BodyOneofCase.Action: Write(writer, value.Action); break;
            case Wire.ResolvedUnitAction.BodyOneofCase.Create: Write(writer, value.Create); break;
            case Wire.ResolvedUnitAction.BodyOneofCase.Unload: Write(writer, value.Unload); break;
            default: throw new InvalidDataException("Missing oneof value.");
        }
    }
    private static void Write(BinaryWriter writer, Wire.SeatControlChanged value)
    {
        Bytes(writer, value.SeatId);
        writer.Write(value.PlayerIndex);
        writer.Write(value.AiControlled);
        Text(writer, value.Reason);
    }
    private static void Write(BinaryWriter writer, Wire.RandomRecordMessage value)
    {
        Text(writer, value.CallSite);
        writer.Write(value.Ordinal);
        writer.Write((uint)value.ValueCase);
        switch (value.ValueCase)
        {
            case Wire.RandomRecordMessage.ValueOneofCase.Integer: writer.Write(value.Integer); break;
            case Wire.RandomRecordMessage.ValueOneofCase.Floating: writer.Write(value.Floating); break;
            default: throw new InvalidDataException("Missing oneof value.");
        }
    }
    private static void Write(BinaryWriter writer, Wire.Resolution value)
    {
        Bytes(writer, value.OperationId);
        writer.Write(value.Event is not null);
        if (value.Event is not null) { Write(writer, value.Event); }
    }
    private static void Write(BinaryWriter writer, Wire.OperationBegin value)
    {
        Bytes(writer, value.OperationId);
        Bytes(writer, value.SeatId);
        writer.Write(value.RequestId);
        writer.Write(value.Round);
        writer.Write(value.Command is not null);
        if (value.Command is not null) { Write(writer, value.Command); }
    }
    private static void Write(BinaryWriter writer, Wire.OperationEnd value)
    {
        Bytes(writer, value.OperationId);
    }
    private static void Write(BinaryWriter writer, Wire.OperationFailed value)
    {
        Bytes(writer, value.OperationId);
        Text(writer, value.Reason);
    }
    private static void Write(BinaryWriter writer, Wire.AuthorityFrameMessage value)
    {
        Bytes(writer, value.MatchId);
        writer.Write(value.FrameId);
        Bytes(writer, value.PreviousHash);
        Bytes(writer, value.Hash);
        writer.Write((uint)value.BodyCase);
        switch (value.BodyCase)
        {
            case Wire.AuthorityFrameMessage.BodyOneofCase.OperationBegin: Write(writer, value.OperationBegin); break;
            case Wire.AuthorityFrameMessage.BodyOneofCase.Resolution: Write(writer, value.Resolution); break;
            case Wire.AuthorityFrameMessage.BodyOneofCase.OperationEnd: Write(writer, value.OperationEnd); break;
            case Wire.AuthorityFrameMessage.BodyOneofCase.TurnPhase: Write(writer, value.TurnPhase); break;
            case Wire.AuthorityFrameMessage.BodyOneofCase.SeatChanged: Write(writer, value.SeatChanged); break;
            case Wire.AuthorityFrameMessage.BodyOneofCase.MatchEnded: Write(writer, value.MatchEnded); break;
            case Wire.AuthorityFrameMessage.BodyOneofCase.OperationFailed: Write(writer, value.OperationFailed); break;
            default: throw new InvalidDataException("Missing oneof value.");
        }
    }
    private static void Write(BinaryWriter writer, Wire.MoveCommand value)
    {
        writer.Write((uint)value.UnitIds.Count);
        foreach (var item in value.UnitIds) { writer.Write(item); }
        writer.Write(value.TargetX);
        writer.Write(value.TargetY);
        writer.Write((uint)value.UnitTargetXs.Count);
        foreach (var item in value.UnitTargetXs) { writer.Write(item); }
        writer.Write((uint)value.UnitTargetYs.Count);
        foreach (var item in value.UnitTargetYs) { writer.Write(item); }
        writer.Write(value.DesiredToggleState);
    }
    private static void Write(BinaryWriter writer, Wire.ActionCommand value)
    {
        writer.Write((uint)value.UnitIds.Count);
        foreach (var item in value.UnitIds) { writer.Write(item); }
        writer.Write(value.TargetX);
        writer.Write(value.TargetY);
        writer.Write((uint)value.UnitTargetXs.Count);
        foreach (var item in value.UnitTargetXs) { writer.Write(item); }
        writer.Write((uint)value.UnitTargetYs.Count);
        foreach (var item in value.UnitTargetYs) { writer.Write(item); }
        writer.Write(value.ActionCategory);
        Text(writer, value.TemplateId);
        writer.Write(value.PassengerUnitId);
        writer.Write(value.DesiredToggleState);
    }
    private static void Write(BinaryWriter writer, Wire.EquipmentActionCommand value)
    {
        writer.Write((uint)value.UnitIds.Count);
        foreach (var item in value.UnitIds) { writer.Write(item); }
        writer.Write(value.TargetX);
        writer.Write(value.TargetY);
        writer.Write(value.ActionCategory);
        Text(writer, value.ActionId);
        Text(writer, value.TemplateId);
    }
    private static void Write(BinaryWriter writer, Wire.EquipmentMoveActionCommand value)
    {
        writer.Write((uint)value.UnitIds.Count);
        foreach (var item in value.UnitIds) { writer.Write(item); }
        writer.Write(value.TargetX);
        writer.Write(value.TargetY);
        writer.Write(value.ActionCategory);
        Text(writer, value.ActionId);
        Text(writer, value.TemplateId);
    }
    private static void Write(BinaryWriter writer, Wire.BuildWithMoveCommand value)
    {
        writer.Write((uint)value.UnitIds.Count);
        foreach (var item in value.UnitIds) { writer.Write(item); }
        writer.Write(value.TargetX);
        writer.Write(value.TargetY);
        writer.Write(value.ActionCategory);
        Text(writer, value.TemplateId);
    }
    private static void Write(BinaryWriter writer, Wire.SkillCommand value)
    {
        writer.Write(value.TargetX);
        writer.Write(value.TargetY);
    }
    private static void Write(BinaryWriter writer, Wire.UndoMoveCommand value)
    {
    }
    private static void Write(BinaryWriter writer, Wire.EndTurnCommand value)
    {
    }
    private static void Write(BinaryWriter writer, Wire.AutoGuideStartCommand value)
    {
        writer.Write((uint)value.UnitIds.Count);
        foreach (var item in value.UnitIds) { writer.Write(item); }
    }
    private static void Write(BinaryWriter writer, Wire.DebugAddResourcesCommand value)
    {
        writer.Write(value.DebugPlayerIndex);
        writer.Write(value.DebugMetalDelta);
        writer.Write(value.DebugPowerDelta);
    }
    private static void Write(BinaryWriter writer, Wire.DebugFillSkillCommand value)
    {
        writer.Write(value.DebugPlayerIndex);
    }
    private static void Write(BinaryWriter writer, Wire.AiUnitActionCommand value)
    {
        writer.Write((uint)value.UnitIds.Count);
        foreach (var item in value.UnitIds) { writer.Write(item); }
        writer.Write(value.TargetX);
        writer.Write(value.TargetY);
        writer.Write((uint)value.UnitTargetXs.Count);
        foreach (var item in value.UnitTargetXs) { writer.Write(item); }
        writer.Write((uint)value.UnitTargetYs.Count);
        foreach (var item in value.UnitTargetYs) { writer.Write(item); }
        writer.Write(value.ActionCategory);
        Text(writer, value.TemplateId);
        writer.Write(value.PassengerUnitId);
        writer.Write(value.DesiredToggleState);
        writer.Write(value.AiActionType);
    }
    private static void Write(BinaryWriter writer, Wire.AiSkillCommand value)
    {
        writer.Write(value.TargetX);
        writer.Write(value.TargetY);
        writer.Write(value.DesiredToggleState);
    }
    private static void Write(BinaryWriter writer, Wire.TurnAdvanceCommand value)
    {
    }
    private static void Write(BinaryWriter writer, Wire.SurrenderCommand value)
    {
        writer.Write(value.TargetX);
    }
    private static void Write(BinaryWriter writer, Wire.ToggleStandbyCommand value)
    {
        writer.Write((uint)value.UnitIds.Count);
        foreach (var item in value.UnitIds) { writer.Write(item); }
        writer.Write(value.DesiredToggleState);
    }
    private static void Write(BinaryWriter writer, Wire.ToggleSleepCommand value)
    {
        writer.Write((uint)value.UnitIds.Count);
        foreach (var item in value.UnitIds) { writer.Write(item); }
        writer.Write(value.DesiredToggleState);
    }
    private static void Write(BinaryWriter writer, Wire.StayCommand value)
    {
        writer.Write((uint)value.UnitIds.Count);
        foreach (var item in value.UnitIds) { writer.Write(item); }
    }
    private static void Write(BinaryWriter writer, Wire.SelfDestructCommand value)
    {
        writer.Write((uint)value.UnitIds.Count);
        foreach (var item in value.UnitIds) { writer.Write(item); }
    }
    private static void Write(BinaryWriter writer, Wire.ResolvedUnitActionsCommand value)
    {
        writer.Write((uint)value.UnitIds.Count);
        foreach (var item in value.UnitIds) { writer.Write(item); }
        writer.Write((uint)value.ResolvedActions.Count);
        foreach (var item in value.ResolvedActions) { Write(writer, item); }
    }
    private static void Write(BinaryWriter writer, Wire.RandomRecordList value)
    {
        writer.Write((uint)value.Records.Count);
        foreach (var item in value.Records) { Write(writer, item); }
    }
    private static void Write(BinaryWriter writer, Wire.UnitRandomResult value)
    {
        writer.Write(value.UnitId);
        writer.Write(value.Values is not null);
        if (value.Values is not null) { Write(writer, value.Values); }
    }
    private static void Write(BinaryWriter writer, Wire.TileRandomResult value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Values is not null);
        if (value.Values is not null) { Write(writer, value.Values); }
    }
    private static void Write(BinaryWriter writer, Wire.RandomResult value)
    {
        writer.Write(value.DamageRoll is not null);
        if (value.DamageRoll is not null) { Write(writer, value.DamageRoll); }
        writer.Write(value.DamageVariation is not null);
        if (value.DamageVariation is not null) { Write(writer, value.DamageVariation); }
        writer.Write(value.DeathWreckScale is not null);
        if (value.DeathWreckScale is not null) { Write(writer, value.DeathWreckScale); }
        writer.Write((uint)value.Wrecks.Count);
        foreach (var item in value.Wrecks) { Write(writer, item); }
        writer.Write((uint)value.CommanderPositions.Count);
        foreach (var item in value.CommanderPositions) { Write(writer, item); }
        writer.Write((uint)value.ExperienceShares.Count);
        foreach (var item in value.ExperienceShares) { Write(writer, item); }
    }
    private static void Write(BinaryWriter writer, Wire.SettlementEvent value)
    {
        writer.Write(value.SourceUnitId);
        writer.Write(value.TargetUnitId);
        writer.Write(value.RandomResult is not null);
        if (value.RandomResult is not null) { Write(writer, value.RandomResult); }
        writer.Write((uint)value.Children.Count);
        foreach (var item in value.Children) { Write(writer, item); }
        writer.Write(value.PlayerIndex);
        writer.Write((uint)value.BodyCase);
        switch (value.BodyCase)
        {
            case Wire.SettlementEvent.BodyOneofCase.AttackStarted: Write(writer, value.AttackStarted); break;
            case Wire.SettlementEvent.BodyOneofCase.AttackCell: Write(writer, value.AttackCell); break;
            case Wire.SettlementEvent.BodyOneofCase.AttackFinished: Write(writer, value.AttackFinished); break;
            case Wire.SettlementEvent.BodyOneofCase.Damage: Write(writer, value.Damage); break;
            case Wire.SettlementEvent.BodyOneofCase.DeathStarted: Write(writer, value.DeathStarted); break;
            case Wire.SettlementEvent.BodyOneofCase.DeathCommitted: Write(writer, value.DeathCommitted); break;
            case Wire.SettlementEvent.BodyOneofCase.PlayerDefeated: Write(writer, value.PlayerDefeated); break;
            case Wire.SettlementEvent.BodyOneofCase.CleanupCompleted: Write(writer, value.CleanupCompleted); break;
            case Wire.SettlementEvent.BodyOneofCase.Effect: Write(writer, value.Effect); break;
            case Wire.SettlementEvent.BodyOneofCase.Spawn: Write(writer, value.Spawn); break;
            case Wire.SettlementEvent.BodyOneofCase.Heal: Write(writer, value.Heal); break;
            case Wire.SettlementEvent.BodyOneofCase.Resource: Write(writer, value.Resource); break;
            case Wire.SettlementEvent.BodyOneofCase.CommanderPosition: Write(writer, value.CommanderPosition); break;
            case Wire.SettlementEvent.BodyOneofCase.ForcedAttack: Write(writer, value.ForcedAttack); break;
            case Wire.SettlementEvent.BodyOneofCase.Pulse: Write(writer, value.Pulse); break;
            case Wire.SettlementEvent.BodyOneofCase.Wreck: Write(writer, value.Wreck); break;
            case Wire.SettlementEvent.BodyOneofCase.Shield: Write(writer, value.Shield); break;
            case Wire.SettlementEvent.BodyOneofCase.EffectMutation: Write(writer, value.EffectMutation); break;
            case Wire.SettlementEvent.BodyOneofCase.Experience: Write(writer, value.Experience); break;
            case Wire.SettlementEvent.BodyOneofCase.SkillFinished: Write(writer, value.SkillFinished); break;
            default: throw new InvalidDataException("Missing oneof value.");
        }
    }
    private static void Write(BinaryWriter writer, Wire.AttackStartedEvent value)
    {
        Text(writer, value.ActionName);
        writer.Write(value.TargetX);
        writer.Write(value.TargetY);
        writer.Write(value.GlobalPhase);
    }
    private static void Write(BinaryWriter writer, Wire.AttackCellEvent value)
    {
        Text(writer, value.ActionName);
        writer.Write(value.TargetX);
        writer.Write(value.TargetY);
        writer.Write(value.HasOriginX);
        if (value.HasOriginX) { writer.Write(value.OriginX); }
        writer.Write(value.HasOriginY);
        if (value.HasOriginY) { writer.Write(value.OriginY); }
        writer.Write(value.HasData);
        if (value.HasData) { writer.Write(value.Data); }
        Text(writer, value.ActionType);
        writer.Write(value.SkillIndex);
    }
    private static void Write(BinaryWriter writer, Wire.AttackFinishedEvent value)
    {
        Text(writer, value.ActionName);
        writer.Write(value.CheckAfterAction);
    }
    private static void Write(BinaryWriter writer, Wire.DamageEvent value)
    {
        writer.Write(value.HasDamage);
        if (value.HasDamage) { writer.Write(value.Damage); }
        Text(writer, value.ActionName);
        writer.Write(value.HasCenterX);
        if (value.HasCenterX) { writer.Write(value.CenterX); }
        writer.Write(value.HasCenterY);
        if (value.HasCenterY) { writer.Write(value.CenterY); }
        writer.Write(value.UsePredamage);
        writer.Write(value.DoRandom);
        writer.Write(value.NoKill);
    }
    private static void Write(BinaryWriter writer, Wire.DeathStartedEvent value)
    {
        writer.Write(value.Reason);
        writer.Write(value.HasDamage);
        if (value.HasDamage) { writer.Write(value.Damage); }
        Text(writer, value.ActionName);
        writer.Write(value.GenerateWreck);
        writer.Write(value.HasCenterX);
        if (value.HasCenterX) { writer.Write(value.CenterX); }
        writer.Write(value.HasCenterY);
        if (value.HasCenterY) { writer.Write(value.CenterY); }
    }
    private static void Write(BinaryWriter writer, Wire.DeathCommittedEvent value)
    {
        writer.Write(value.Reason);
        writer.Write(value.KillerPlayer);
        writer.Write(value.DescendOnly);
        writer.Write(value.CallbacksOnly);
    }
    private static void Write(BinaryWriter writer, Wire.PlayerDefeatedEvent value)
    {
        writer.Write(value.PlayerIndex);
    }
    private static void Write(BinaryWriter writer, Wire.CleanupCompletedEvent value)
    {
        writer.Write(value.PlayerIndex);
    }
    private static void Write(BinaryWriter writer, Wire.EffectEvent value)
    {
        Text(writer, value.EffectName);
        writer.Write(value.EffectIndex);
        Text(writer, value.Callback);
        writer.Write(value.OtherUnitId);
        writer.Write(value.HasDamage);
        if (value.HasDamage) { writer.Write(value.Damage); }
        writer.Write(value.Killed);
        writer.Write(value.Reason);
        writer.Write(value.HasValue);
        if (value.HasValue) { writer.Write(value.Value); }
        writer.Write(value.Preview);
        Text(writer, value.EffectType);
        writer.Write(value.X);
        writer.Write(value.Y);
    }
    private static void Write(BinaryWriter writer, Wire.SpawnEvent value)
    {
        Text(writer, value.TemplateId);
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.PlayerIndex);
        writer.Write(value.Building);
        writer.Write(value.Spawned);
        writer.Write(value.CreateReason);
        writer.Write(value.TriggerPassives);
        writer.Write(value.HasPosition);
        writer.Write(value.Moved);
        writer.Write(value.Actioned);
    }
    private static void Write(BinaryWriter writer, Wire.HealEvent value)
    {
        writer.Write(value.HasAmount);
        if (value.HasAmount) { writer.Write(value.Amount); }
    }
    private static void Write(BinaryWriter writer, Wire.ResourceEvent value)
    {
        writer.Write(value.PlayerIndex);
        writer.Write(value.Reason);
        writer.Write(value.Metal);
        writer.Write(value.Power);
    }
    private static void Write(BinaryWriter writer, Wire.CommanderPositionEvent value)
    {
        writer.Write(value.PlayerIndex);
    }
    private static void Write(BinaryWriter writer, Wire.ForcedAttackEvent value)
    {
        writer.Write(value.ActionCategory);
        writer.Write(value.TargetX);
        writer.Write(value.TargetY);
    }
    private static void Write(BinaryWriter writer, Wire.PulseEvent value)
    {
        writer.Write(value.EffectIndex);
    }
    private static void Write(BinaryWriter writer, Wire.WreckEvent value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Amount);
        writer.Write(value.UseRandom);
    }
    private static void Write(BinaryWriter writer, Wire.ShieldEvent value)
    {
        writer.Write(value.Damage);
        writer.Write(value.Preview);
    }
    private static void Write(BinaryWriter writer, Wire.EffectMutationEvent value)
    {
        Text(writer, value.EffectName);
        writer.Write(value.Layer);
        writer.Write(value.CasterUnitId);
        writer.Write(value.CasterPlayer);
        writer.Write(value.Remove);
        writer.Write(value.EffectIndex);
    }
    private static void Write(BinaryWriter writer, Wire.ExperienceEvent value)
    {
        writer.Write(value.HasAmount);
        if (value.HasAmount) { writer.Write(value.Amount); }
        writer.Write(value.UseFilter);
        writer.Write(value.BasePercent);
    }
    private static void Write(BinaryWriter writer, Wire.SkillFinishedEvent value)
    {
        writer.Write(value.PlayerIndex);
    }
    private static void Write(BinaryWriter writer, Wire.ResolvedMove value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
    }
    private static void Write(BinaryWriter writer, Wire.ResolvedAction value)
    {
        writer.Write(value.MoveX);
        writer.Write(value.MoveY);
        writer.Write(value.TargetX);
        writer.Write(value.TargetY);
        writer.Write(value.ActionCategory);
    }
    private static void Write(BinaryWriter writer, Wire.ResolvedCreate value)
    {
        writer.Write(value.MoveX);
        writer.Write(value.MoveY);
        writer.Write(value.TargetX);
        writer.Write(value.TargetY);
        writer.Write(value.ActionCategory);
        Text(writer, value.TemplateId);
    }
    private static void Write(BinaryWriter writer, Wire.ResolvedUnload value)
    {
        writer.Write(value.MoveX);
        writer.Write(value.MoveY);
        writer.Write(value.TargetX);
        writer.Write(value.TargetY);
        writer.Write(value.ActionCategory);
        writer.Write(value.PassengerUnitId);
    }
    private static void Write(BinaryWriter writer, Wire.AuthorityTurnPhase value)
    {
        writer.Write(value.Phase);
    }
    private static void Write(BinaryWriter writer, Wire.AuthorityMatchEnded value)
    {
        writer.Write(value.LocalVictory);
    }
}
