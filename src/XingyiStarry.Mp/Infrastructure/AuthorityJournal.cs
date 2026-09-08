using System;
using System.Collections.Generic;
using System.IO;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Infrastructure;

internal sealed class AuthorityJournal : IDisposable
{
    private readonly List<AuthorityFrame> frames = new List<AuthorityFrame>();
    private readonly FileStream stream;
    private readonly AuthorityHashChain chain;

    public AuthorityJournal(Guid matchId, string path)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("Journal path must have a directory.", nameof(path));
        Directory.CreateDirectory(directory);
        stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        chain = new AuthorityHashChain(matchId);
    }

    public IReadOnlyList<AuthorityFrame> Frames => frames;

    public AuthorityFrame Append(AuthorityFrameType type, byte[] payload)
    {
        var frame = chain.Append(type, payload);
        var bytes = ProtocolCodec.EncodeAuthorityFrame(frame);
        var prefix = new[] { (byte)bytes.Length, (byte)(bytes.Length >> 8), (byte)(bytes.Length >> 16), (byte)(bytes.Length >> 24) };
        stream.Write(prefix, 0, prefix.Length); stream.Write(bytes, 0, bytes.Length); stream.Flush();
        frames.Add(frame); return frame;
    }

    public IEnumerable<AuthorityFrame> After(long frameId)
    {
        foreach (var frame in frames) if (frame.FrameId > frameId) yield return frame;
    }

    public void Dispose() => stream.Dispose();
}
