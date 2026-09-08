using System;

namespace XingyiStarry.Mp.Protocol;

public sealed class OperationBeginPayload
{
    public Guid OperationId { get; set; }
    public Guid SeatId { get; set; }
    public ulong RequestId { get; set; }
    public int Round { get; set; }
    public GameCommand Command { get; set; } = new GameCommand();
}

public sealed class OperationEndPayload
{
    public Guid OperationId { get; set; }
    public byte[] StateHash { get; set; } = Array.Empty<byte>();
}
