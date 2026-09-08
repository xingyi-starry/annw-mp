using System;
using System.Collections.Generic;

namespace XingyiStarry.Mp.Protocol;

public sealed class Envelope
{
    public MessageType Type { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();
}

public sealed class HelloMessage
{
    public ushort ProtocolVersion { get; set; } = ProtocolConstants.Version;
    public string PluginVersion { get; set; } = "";
    public string GameFingerprint { get; set; } = "";
    public string ContentFingerprint { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public sealed class WelcomeMessage
{
    public Guid ClientId { get; set; }
    public Guid RoomId { get; set; }
    public Guid? MatchId { get; set; }
    public long LatestFrameId { get; set; }
}

public sealed class SeatInfo
{
    public Guid SeatId { get; set; }
    public int LobbySlotIndex { get; set; } = -1;
    public int PlayerIndex { get; set; } = -1;
    public string DisplayName { get; set; } = "";
    public bool OriginallyHuman { get; set; }
    public bool Connected { get; set; }
    public bool Ready { get; set; }
    public bool AiControlled { get; set; }
    public Guid? ClientId { get; set; }
    public int Controller { get; set; }
    public int Team { get; set; }
    public int Color { get; set; }
    public int Position { get; set; }
    public bool PositionRandom { get; set; }
    public float ResourceMultiplier { get; set; } = 1f;
    public float AiIntelligence { get; set; } = 1f;
    public string CommanderId { get; set; } = "";
    public int CommanderMode { get; set; } = 1;
    public string SkillId { get; set; } = "";
    public List<string> PassiveIds { get; } = new List<string>();
}

public sealed class RoomSnapshot
{
    public Guid RoomId { get; set; }
    public Guid? MatchId { get; set; }
    public bool MatchStarted { get; set; }
    public int DraftRevision { get; set; }
    public string MapId { get; set; } = "";
    public string MapTitle { get; set; } = "";
    public int FowType { get; set; }
    public int WinCondition { get; set; }
    public int QuickStart { get; set; }
    public List<SeatInfo> Seats { get; } = new List<SeatInfo>();
}

public sealed class CommandRequest
{
    public Guid ClientId { get; set; }
    public ulong RequestId { get; set; }
    public Guid SeatId { get; set; }
    public int Round { get; set; }
    public long AppliedFrameId { get; set; }
    public GameCommand Command { get; set; } = new GameCommand();
}

public sealed class CommandResponse
{
    public ulong RequestId { get; set; }
    public long AuthorityFrameId { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class GameCommand
{
    public CommandKind Kind { get; set; }
    public long[] UnitIds { get; set; } = Array.Empty<long>();
    public int TargetX { get; set; }
    public int TargetY { get; set; }
    public int[] UnitTargetXs { get; set; } = Array.Empty<int>();
    public int[] UnitTargetYs { get; set; } = Array.Empty<int>();
    public int ActionCategory { get; set; }
    public string ActionId { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public long PassengerUnitId { get; set; }
    public bool DesiredToggleState { get; set; }
    public int DebugPlayerIndex { get; set; }
    public int DebugMetalDelta { get; set; }
    public int DebugPowerDelta { get; set; }
    public int AiActionType { get; set; }
}

public sealed class RandomRecord
{
    public string CallSite { get; set; } = "";
    public int Ordinal { get; set; }
    public byte ValueKind { get; set; }
    public long IntegerValue { get; set; }
    public double FloatingValue { get; set; }
}

public sealed class ResolutionPayload
{
    public Guid OperationId { get; set; }
    public int StageId { get; set; }
    public int SettlementOrdinal { get; set; }
    public List<RandomRecord> RandomRecords { get; } = new List<RandomRecord>();
}

public sealed class AuthorityFrame
{
    public Guid MatchId { get; set; }
    public long FrameId { get; set; }
    public byte[] PrevHash { get; set; } = Array.Empty<byte>();
    public AuthorityFrameType FrameType { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public byte[] Hash { get; set; } = Array.Empty<byte>();
}

public sealed class SnapshotManifest
{
    public Guid SnapshotId { get; set; }
    public Guid MatchId { get; set; }
    public long FrameId { get; set; }
    public byte[] FrameHash { get; set; } = Array.Empty<byte>();
    public int CompressedLength { get; set; }
    public int ChunkCount { get; set; }
    public byte[] ContentHash { get; set; } = Array.Empty<byte>();
}

public sealed class SnapshotChunk
{
    public Guid SnapshotId { get; set; }
    public int Index { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
}
