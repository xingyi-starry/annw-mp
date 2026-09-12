namespace XingyiStarry.Mp.Protocol;

public static class ProtocolConstants
{
    public const ushort Version = 11;
    public const int DefaultPort = 24555;
    public const int MaxPacketBytes = 16 * 1024 * 1024;
    public const int MaxSnapshotBytes = 256 * 1024 * 1024;
    public const int SnapshotChunkBytes = 256 * 1024;
    public const string HashDomain = "XingyiStarry.Mp/authority/v1";
}

public enum MessageType : ushort
{
    Hello = 1, Welcome, Reject, Heartbeat,
    RoomState, ClaimSeat, SetReady, StartMatch,
    CommandRequest, CommandAccepted, CommandRejected,
    AuthorityFrame, HistoryRequest, HistoryComplete,
    SnapshotRequest, SnapshotManifest, SnapshotChunk, SnapshotComplete,
    CatchUpComplete, SessionEnded, LobbyDraftChange, ParticipantNotice,
    RelayRegisterRoom, RelayUpdateRoom, RelayListRooms, RelayRoomList,
    RelayJoinRoom, RelayControlResponse, RelayCloseJoining, RelayLeaveRoom,
    RelayCloseRoom, RelayPeerJoined, RelayPeerLeft
}

public enum Delivery : byte
{
    RelayControl = 0,
    ToHost = 1,
    ToClient = 2,
    Broadcast = 3
}

public enum RelayRoomStatus : byte
{
    Waiting = 0,
    Closed = 1,
    Playing = 2
}

public enum AuthorityFrameType : byte
{
    OperationBegin = 1,
    Resolution,
    OperationEnd,
    TurnPhase,
    SeatChanged,
    MatchEnded,
    OperationFailed
}

public enum CommandKind : ushort
{
    Move = 1,
    Action,
    EquipmentAction,
    EquipmentMoveAction,
    BuildWithMove,
    Skill,
    UndoMove,
    EndTurn,
    AutoGuideStart,
    AutoGuideCancel,
    DebugAddResources,
    DebugFillSkill,
    AiUnitAction,
    AiSkill,
    TurnAdvance,
    Surrender,
    ToggleStandby,
    ToggleSleep,
    Stay
}

public enum TurnPhase : byte
{
    RoundStarted = 1,
    SeatTurnStarting,
    SeatTurnReady,
    SeatTurnEnding,
    SeatTurnEnded
}
