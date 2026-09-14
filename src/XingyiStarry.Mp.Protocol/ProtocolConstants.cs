namespace XingyiStarry.Mp.Protocol;

public static class ProtocolConstants
{
    public const ushort Version = 12;
    public const int DefaultPort = 24555;
    public const int MaxPacketBytes = 16 * 1024 * 1024;
    public const int MaxSnapshotBytes = 256 * 1024 * 1024;
    public const int SnapshotChunkBytes = 256 * 1024;
    public const string HashDomain = "XingyiStarry.Mp/authority/v1";
}

public enum MessageType : ushort
{
    Hello = 1,
    Welcome = 2,
    Reject = 3,
    Heartbeat = 4,
    RoomState = 5,
    ClaimSeat = 6,
    SetReady = 7,
    StartMatch = 8,
    CommandRequest = 9,
    CommandAccepted = 10,
    CommandRejected = 11,
    AuthorityFrame = 12,
    HistoryRequest = 13,
    HistoryComplete = 14,
    SnapshotRequest = 15,
    SnapshotManifest = 16,
    SnapshotChunk = 17,
    SnapshotComplete = 18,
    CatchUpComplete = 19,
    SessionEnded = 20,
    LobbyDraftChange = 21,
    ParticipantNotice = 22,
    RelayRegisterRoom = 23,
    RelayUpdateRoom = 24,
    RelayListRooms = 25,
    RelayRoomList = 26,
    RelayJoinRoom = 27,
    RelayControlResponse = 28,
    RelayCloseJoining = 29,
    RelayLeaveRoom = 30,
    RelayCloseRoom = 31,
    RelayPeerJoined = 32,
    RelayPeerLeft = 33,
    ReleaseSeat = 34,
    JoinMatchRequest = 35,
    JoinMatchAccepted = 36,
    ResumeSession = 37,
    ResumeSessionAccepted = 38,
    ResumeSessionRejected = 39,
    LeaveSession = 40,
    RelayResumeRoom = 41
}

public enum WelcomeMode : byte
{
    Lobby = 0,
    JoinSelection = 1,
    ActiveMatch = 2
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
    Resolution = 2,
    OperationEnd = 3,
    TurnPhase = 4,
    SeatChanged = 5,
    MatchEnded = 6,
    OperationFailed = 7
}

public enum CommandKind : ushort
{
    Move = 1,
    Action = 2,
    EquipmentAction = 3,
    EquipmentMoveAction = 4,
    BuildWithMove = 5,
    Skill = 6,
    UndoMove = 7,
    EndTurn = 8,
    AutoGuideStart = 9,
    AutoGuideCancel = 10,
    DebugAddResources = 11,
    DebugFillSkill = 12,
    AiUnitAction = 13,
    AiSkill = 14,
    TurnAdvance = 15,
    Surrender = 16,
    ToggleStandby = 17,
    ToggleSleep = 18,
    Stay = 19
}

public enum TurnPhase : byte
{
    RoundStarted = 1,
    SeatTurnStarting = 2,
    SeatTurnReady = 3,
    SeatTurnEnding = 4,
    SeatTurnEnded = 5
}
