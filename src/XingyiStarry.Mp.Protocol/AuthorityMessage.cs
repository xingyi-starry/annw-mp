using System.IO;
using Google.Protobuf;
using Wire = XingyiStarry.Mp.Protocol.Wire;
namespace XingyiStarry.Mp.Protocol;

// Only named factories can construct outgoing authority bodies.
public sealed class AuthorityMessage
{
    internal Wire.AuthorityFrameMessage Body { get; }
    internal AuthorityMessage(Wire.AuthorityFrameMessage body)
    {
        if (body.BodyCase == Wire.AuthorityFrameMessage.BodyOneofCase.None) throw new InvalidDataException("Missing authority frame body.");
        Body = body;
    }
    public AuthorityFrameType Type => (AuthorityFrameType)((int)Body.BodyCase - 100);
    public IMessage Payload => Body.BodyCase switch
    {
        Wire.AuthorityFrameMessage.BodyOneofCase.OperationBegin => Body.OperationBegin,
        Wire.AuthorityFrameMessage.BodyOneofCase.Resolution => Body.Resolution,
        Wire.AuthorityFrameMessage.BodyOneofCase.OperationEnd => Body.OperationEnd,
        Wire.AuthorityFrameMessage.BodyOneofCase.TurnPhase => Body.TurnPhase,
        Wire.AuthorityFrameMessage.BodyOneofCase.SeatChanged => Body.SeatChanged,
        Wire.AuthorityFrameMessage.BodyOneofCase.MatchEnded => Body.MatchEnded,
        Wire.AuthorityFrameMessage.BodyOneofCase.OperationFailed => Body.OperationFailed,
        _ => throw new InvalidDataException("Missing authority frame body.")
    };
}
public static class AuthorityMessages
{
    public static AuthorityMessage OperationBegin(Wire.OperationBegin value) => new AuthorityMessage(new Wire.AuthorityFrameMessage { OperationBegin = value });
    public static AuthorityMessage Resolution(Wire.Resolution value) => new AuthorityMessage(new Wire.AuthorityFrameMessage { Resolution = value });
    public static AuthorityMessage OperationEnd(Wire.OperationEnd value) => new AuthorityMessage(new Wire.AuthorityFrameMessage { OperationEnd = value });
    public static AuthorityMessage TurnPhase(Wire.AuthorityTurnPhase value) => new AuthorityMessage(new Wire.AuthorityFrameMessage { TurnPhase = value });
    public static AuthorityMessage SeatChanged(Wire.SeatControlChanged value) => new AuthorityMessage(new Wire.AuthorityFrameMessage { SeatChanged = value });
    public static AuthorityMessage MatchEnded(Wire.AuthorityMatchEnded value) => new AuthorityMessage(new Wire.AuthorityFrameMessage { MatchEnded = value });
    public static AuthorityMessage OperationFailed(Wire.OperationFailed value) => new AuthorityMessage(new Wire.AuthorityFrameMessage { OperationFailed = value });
}
