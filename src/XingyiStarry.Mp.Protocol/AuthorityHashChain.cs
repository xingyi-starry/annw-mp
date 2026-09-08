using System;
using System.IO;
using System.Security.Cryptography;

namespace XingyiStarry.Mp.Protocol;

public sealed class AuthorityHashChain
{
    public const int HashLength = 32;
    private readonly Guid matchId;
    private long nextFrameId = 1;
    private byte[] previousHash = new byte[HashLength];

    public AuthorityHashChain(Guid matchId) => this.matchId = matchId;

    public AuthorityFrame Append(AuthorityFrameType type, byte[] payload)
    {
        var frame = new AuthorityFrame { MatchId = matchId, FrameId = nextFrameId, PrevHash = (byte[])previousHash.Clone(), FrameType = type, Payload = payload };
        frame.Hash = ComputeHash(frame);
        previousHash = (byte[])frame.Hash.Clone(); nextFrameId++;
        return frame;
    }

    public static byte[] ComputeHash(AuthorityFrame frame)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(ProtocolCodec.EncodeAuthorityFrameForHash(frame));
    }

    public static void VerifyNext(AuthorityFrame frame, Guid matchId, long expectedFrameId, byte[] expectedPreviousHash)
    {
        if (frame.MatchId != matchId || frame.FrameId != expectedFrameId) throw new InvalidDataException("Authority sequence mismatch.");
        if (!FixedEquals(frame.PrevHash, expectedPreviousHash)) throw new InvalidDataException("Authority previous hash mismatch.");
        if (!FixedEquals(frame.Hash, ComputeHash(frame))) throw new InvalidDataException("Authority frame hash mismatch.");
    }

    public static bool FixedEquals(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        var difference = 0;
        for (var i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
        return difference == 0;
    }
}
