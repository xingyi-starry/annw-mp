using System;
using System.IO;
using Google.Protobuf;
using Wire = XingyiStarry.Mp.Protocol.Wire;
namespace XingyiStarry.Mp.Protocol;

public sealed class NetworkMessage
{
    internal Wire.WireEnvelope Body { get; }
    internal NetworkMessage(Wire.WireEnvelope body)
    {
        if (body.BodyCase == Wire.WireEnvelope.BodyOneofCase.None) throw new InvalidDataException("Missing message body.");
        Body = body;
    }
    public MessageType Type => (MessageType)((int)Body.BodyCase - 100);
    public IMessage Payload => Body.BodyCase switch
    {
        Wire.WireEnvelope.BodyOneofCase.Hello => Body.Hello,
        Wire.WireEnvelope.BodyOneofCase.Welcome => Body.Welcome,
        Wire.WireEnvelope.BodyOneofCase.Reject => Body.Reject,
        Wire.WireEnvelope.BodyOneofCase.Heartbeat => Body.Heartbeat,
        Wire.WireEnvelope.BodyOneofCase.RoomState => Body.RoomState,
        Wire.WireEnvelope.BodyOneofCase.ClaimSeat => Body.ClaimSeat,
        Wire.WireEnvelope.BodyOneofCase.SetReady => Body.SetReady,
        Wire.WireEnvelope.BodyOneofCase.StartMatch => Body.StartMatch,
        Wire.WireEnvelope.BodyOneofCase.CommandRequest => Body.CommandRequest,
        Wire.WireEnvelope.BodyOneofCase.CommandAccepted => Body.CommandAccepted,
        Wire.WireEnvelope.BodyOneofCase.CommandRejected => Body.CommandRejected,
        Wire.WireEnvelope.BodyOneofCase.AuthorityFrame => Body.AuthorityFrame,
        Wire.WireEnvelope.BodyOneofCase.HistoryRequest => Body.HistoryRequest,
        Wire.WireEnvelope.BodyOneofCase.HistoryComplete => Body.HistoryComplete,
        Wire.WireEnvelope.BodyOneofCase.SnapshotRequest => Body.SnapshotRequest,
        Wire.WireEnvelope.BodyOneofCase.SnapshotManifest => Body.SnapshotManifest,
        Wire.WireEnvelope.BodyOneofCase.SnapshotChunk => Body.SnapshotChunk,
        Wire.WireEnvelope.BodyOneofCase.SnapshotComplete => Body.SnapshotComplete,
        Wire.WireEnvelope.BodyOneofCase.CatchUpComplete => Body.CatchUpComplete,
        Wire.WireEnvelope.BodyOneofCase.SessionEnded => Body.SessionEnded,
        Wire.WireEnvelope.BodyOneofCase.LobbyDraftChange => Body.LobbyDraftChange,
        Wire.WireEnvelope.BodyOneofCase.ParticipantNotice => Body.ParticipantNotice,
        Wire.WireEnvelope.BodyOneofCase.RelayRegisterRoom => Body.RelayRegisterRoom,
        Wire.WireEnvelope.BodyOneofCase.RelayUpdateRoom => Body.RelayUpdateRoom,
        Wire.WireEnvelope.BodyOneofCase.RelayListRooms => Body.RelayListRooms,
        Wire.WireEnvelope.BodyOneofCase.RelayRoomList => Body.RelayRoomList,
        Wire.WireEnvelope.BodyOneofCase.RelayJoinRoom => Body.RelayJoinRoom,
        Wire.WireEnvelope.BodyOneofCase.RelayControlResponse => Body.RelayControlResponse,
        Wire.WireEnvelope.BodyOneofCase.RelayCloseJoining => Body.RelayCloseJoining,
        Wire.WireEnvelope.BodyOneofCase.RelayLeaveRoom => Body.RelayLeaveRoom,
        Wire.WireEnvelope.BodyOneofCase.RelayCloseRoom => Body.RelayCloseRoom,
        Wire.WireEnvelope.BodyOneofCase.RelayPeerJoined => Body.RelayPeerJoined,
        Wire.WireEnvelope.BodyOneofCase.RelayPeerLeft => Body.RelayPeerLeft,
        Wire.WireEnvelope.BodyOneofCase.ReleaseSeat => Body.ReleaseSeat,
        Wire.WireEnvelope.BodyOneofCase.JoinMatchRequest => Body.JoinMatchRequest,
        Wire.WireEnvelope.BodyOneofCase.JoinMatchAccepted => Body.JoinMatchAccepted,
        Wire.WireEnvelope.BodyOneofCase.ResumeSession => Body.ResumeSession,
        Wire.WireEnvelope.BodyOneofCase.ResumeSessionAccepted => Body.ResumeSessionAccepted,
        Wire.WireEnvelope.BodyOneofCase.ResumeSessionRejected => Body.ResumeSessionRejected,
        Wire.WireEnvelope.BodyOneofCase.LeaveSession => Body.LeaveSession,
        Wire.WireEnvelope.BodyOneofCase.RelayResumeRoom => Body.RelayResumeRoom,
        Wire.WireEnvelope.BodyOneofCase.MatchStarting => Body.MatchStarting,
        Wire.WireEnvelope.BodyOneofCase.ChatSend => Body.ChatSend,
        Wire.WireEnvelope.BodyOneofCase.ChatEvent => Body.ChatEvent,
        Wire.WireEnvelope.BodyOneofCase.PingSend => Body.PingSend,
        Wire.WireEnvelope.BodyOneofCase.PingEvent => Body.PingEvent,
        Wire.WireEnvelope.BodyOneofCase.SocialSyncRequest => Body.SocialSyncRequest,
        Wire.WireEnvelope.BodyOneofCase.SocialSyncBegin => Body.SocialSyncBegin,
        Wire.WireEnvelope.BodyOneofCase.SocialSyncChunk => Body.SocialSyncChunk,
        Wire.WireEnvelope.BodyOneofCase.SocialSyncComplete => Body.SocialSyncComplete,
        Wire.WireEnvelope.BodyOneofCase.RelayRoomClosed => Body.RelayRoomClosed,
        _ => throw new InvalidDataException("Unknown message body.")
    };
}
public static class Messages
{
    public static NetworkMessage RelayRoomClosed(Wire.RelayPeerNotice value) => new NetworkMessage(new Wire.WireEnvelope { RelayRoomClosed = value });
    public static NetworkMessage Hello(Wire.Hello value) => new NetworkMessage(new Wire.WireEnvelope { Hello = value });
    public static NetworkMessage Welcome(Wire.Welcome value) => new NetworkMessage(new Wire.WireEnvelope { Welcome = value });
    public static NetworkMessage Reject(Wire.StringValue value) => new NetworkMessage(new Wire.WireEnvelope { Reject = value });
    public static NetworkMessage Heartbeat(Wire.Int64Value value) => new NetworkMessage(new Wire.WireEnvelope { Heartbeat = value });
    public static NetworkMessage RoomState(Wire.Room value) => new NetworkMessage(new Wire.WireEnvelope { RoomState = value });
    public static NetworkMessage ClaimSeat(Wire.Int64Value value) => new NetworkMessage(new Wire.WireEnvelope { ClaimSeat = value });
    public static NetworkMessage SetReady(Wire.Int64Value value) => new NetworkMessage(new Wire.WireEnvelope { SetReady = value });
    public static NetworkMessage StartMatch(Wire.GuidValue value) => new NetworkMessage(new Wire.WireEnvelope { StartMatch = value });
    public static NetworkMessage CommandRequest(Wire.CommandRequestMessage value) => new NetworkMessage(new Wire.WireEnvelope { CommandRequest = value });
    public static NetworkMessage CommandAccepted(Wire.CommandResponseMessage value) => new NetworkMessage(new Wire.WireEnvelope { CommandAccepted = value });
    public static NetworkMessage CommandRejected(Wire.CommandResponseMessage value) => new NetworkMessage(new Wire.WireEnvelope { CommandRejected = value });
    public static NetworkMessage AuthorityFrame(Wire.AuthorityFrameMessage value) => new NetworkMessage(new Wire.WireEnvelope { AuthorityFrame = value });
    public static NetworkMessage HistoryRequest(Wire.Int64Value value) => new NetworkMessage(new Wire.WireEnvelope { HistoryRequest = value });
    public static NetworkMessage HistoryComplete(Wire.Int64Value value) => new NetworkMessage(new Wire.WireEnvelope { HistoryComplete = value });
    public static NetworkMessage SnapshotRequest(Wire.Int64Value value) => new NetworkMessage(new Wire.WireEnvelope { SnapshotRequest = value });
    public static NetworkMessage SnapshotManifest(Wire.SnapshotManifestMessage value) => new NetworkMessage(new Wire.WireEnvelope { SnapshotManifest = value });
    public static NetworkMessage SnapshotChunk(Wire.SnapshotChunkMessage value) => new NetworkMessage(new Wire.WireEnvelope { SnapshotChunk = value });
    public static NetworkMessage SnapshotComplete(Wire.GuidValue value) => new NetworkMessage(new Wire.WireEnvelope { SnapshotComplete = value });
    public static NetworkMessage CatchUpComplete(Wire.GuidValue value) => new NetworkMessage(new Wire.WireEnvelope { CatchUpComplete = value });
    public static NetworkMessage SessionEnded(Wire.StringValue value) => new NetworkMessage(new Wire.WireEnvelope { SessionEnded = value });
    public static NetworkMessage LobbyDraftChange(Wire.Room value) => new NetworkMessage(new Wire.WireEnvelope { LobbyDraftChange = value });
    public static NetworkMessage ParticipantNotice(Wire.StringValue value) => new NetworkMessage(new Wire.WireEnvelope { ParticipantNotice = value });
    public static NetworkMessage RelayRegisterRoom(Wire.RelayRegisterRoomRequest value) => new NetworkMessage(new Wire.WireEnvelope { RelayRegisterRoom = value });
    public static NetworkMessage RelayUpdateRoom(Wire.RelayUpdateRoomRequest value) => new NetworkMessage(new Wire.WireEnvelope { RelayUpdateRoom = value });
    public static NetworkMessage RelayListRooms(Wire.RelayListRoomsRequest value) => new NetworkMessage(new Wire.WireEnvelope { RelayListRooms = value });
    public static NetworkMessage RelayRoomList(Wire.RelayListRoomsResponse value) => new NetworkMessage(new Wire.WireEnvelope { RelayRoomList = value });
    public static NetworkMessage RelayJoinRoom(Wire.RelayJoinRoomRequest value) => new NetworkMessage(new Wire.WireEnvelope { RelayJoinRoom = value });
    public static NetworkMessage RelayControlResponse(Wire.RelayControlResponse value) => new NetworkMessage(new Wire.WireEnvelope { RelayControlResponse = value });
    public static NetworkMessage RelayCloseJoining(Wire.RelayRoomRequest value) => new NetworkMessage(new Wire.WireEnvelope { RelayCloseJoining = value });
    public static NetworkMessage RelayLeaveRoom(Wire.RelayRoomRequest value) => new NetworkMessage(new Wire.WireEnvelope { RelayLeaveRoom = value });
    public static NetworkMessage RelayCloseRoom(Wire.RelayRoomRequest value) => new NetworkMessage(new Wire.WireEnvelope { RelayCloseRoom = value });
    public static NetworkMessage RelayPeerJoined(Wire.RelayPeerNotice value) => new NetworkMessage(new Wire.WireEnvelope { RelayPeerJoined = value });
    public static NetworkMessage RelayPeerLeft(Wire.RelayPeerNotice value) => new NetworkMessage(new Wire.WireEnvelope { RelayPeerLeft = value });
    public static NetworkMessage ReleaseSeat(Wire.GuidValue value) => new NetworkMessage(new Wire.WireEnvelope { ReleaseSeat = value });
    public static NetworkMessage JoinMatchRequest(Wire.JoinMatchRequest value) => new NetworkMessage(new Wire.WireEnvelope { JoinMatchRequest = value });
    public static NetworkMessage JoinMatchAccepted(Wire.JoinMatchAcceptedMessage value) => new NetworkMessage(new Wire.WireEnvelope { JoinMatchAccepted = value });
    public static NetworkMessage ResumeSession(Wire.ResumeSessionRequest value) => new NetworkMessage(new Wire.WireEnvelope { ResumeSession = value });
    public static NetworkMessage ResumeSessionAccepted(Wire.ResumeSessionAccepted value) => new NetworkMessage(new Wire.WireEnvelope { ResumeSessionAccepted = value });
    public static NetworkMessage ResumeSessionRejected(Wire.StringValue value) => new NetworkMessage(new Wire.WireEnvelope { ResumeSessionRejected = value });
    public static NetworkMessage LeaveSession(Wire.GuidValue value) => new NetworkMessage(new Wire.WireEnvelope { LeaveSession = value });
    public static NetworkMessage RelayResumeRoom(Wire.RelayResumeRoomRequest value) => new NetworkMessage(new Wire.WireEnvelope { RelayResumeRoom = value });
    public static NetworkMessage MatchStarting(Wire.GuidValue value) => new NetworkMessage(new Wire.WireEnvelope { MatchStarting = value });
    public static NetworkMessage ChatSend(Wire.ChatSendMessage value) => new NetworkMessage(new Wire.WireEnvelope { ChatSend = value });
    public static NetworkMessage ChatEvent(Wire.ChatEventMessage value) => new NetworkMessage(new Wire.WireEnvelope { ChatEvent = value });
    public static NetworkMessage PingSend(Wire.PingSendMessage value) => new NetworkMessage(new Wire.WireEnvelope { PingSend = value });
    public static NetworkMessage PingEvent(Wire.PingEventMessage value) => new NetworkMessage(new Wire.WireEnvelope { PingEvent = value });
    public static NetworkMessage SocialSyncRequest(Wire.SocialSyncRequestMessage value) => new NetworkMessage(new Wire.WireEnvelope { SocialSyncRequest = value });
    public static NetworkMessage SocialSyncBegin(Wire.SocialSyncBeginMessage value) => new NetworkMessage(new Wire.WireEnvelope { SocialSyncBegin = value });
    public static NetworkMessage SocialSyncChunk(Wire.SocialSyncChunkMessage value) => new NetworkMessage(new Wire.WireEnvelope { SocialSyncChunk = value });
    public static NetworkMessage SocialSyncComplete(Wire.SocialSyncCompleteMessage value) => new NetworkMessage(new Wire.WireEnvelope { SocialSyncComplete = value });
}
