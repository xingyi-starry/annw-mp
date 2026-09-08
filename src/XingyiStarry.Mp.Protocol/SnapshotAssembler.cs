using System;
using System.IO;

namespace XingyiStarry.Mp.Protocol;

public sealed class SnapshotAssembler
{
    private readonly SnapshotManifest manifest;
    private readonly byte[][] chunks;
    private int received;

    public SnapshotAssembler(SnapshotManifest manifest)
    {
        this.manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        if (manifest.CompressedLength < 0 || manifest.CompressedLength > ProtocolConstants.MaxSnapshotBytes) throw new InvalidDataException("Snapshot length is outside protocol bounds.");
        var expectedCount = manifest.CompressedLength == 0 ? 0 : (manifest.CompressedLength + ProtocolConstants.SnapshotChunkBytes - 1) / ProtocolConstants.SnapshotChunkBytes;
        if (manifest.ChunkCount != expectedCount) throw new InvalidDataException("Snapshot chunk count is not canonical.");
        chunks = new byte[manifest.ChunkCount][];
    }

    public bool IsComplete => received == chunks.Length;

    public void Add(SnapshotChunk chunk)
    {
        if (chunk.SnapshotId != manifest.SnapshotId || chunk.Index < 0 || chunk.Index >= chunks.Length) throw new InvalidDataException("Snapshot chunk identity or index is invalid.");
        var expected = chunk.Index == chunks.Length - 1 ? manifest.CompressedLength - chunk.Index * ProtocolConstants.SnapshotChunkBytes : ProtocolConstants.SnapshotChunkBytes;
        if (chunk.Data.Length != expected) throw new InvalidDataException("Snapshot chunk length is invalid.");
        if (chunks[chunk.Index] is not null)
        {
            if (!AuthorityHashChain.FixedEquals(chunks[chunk.Index], chunk.Data)) throw new InvalidDataException("Conflicting duplicate snapshot chunk.");
            return;
        }
        chunks[chunk.Index] = (byte[])chunk.Data.Clone(); received++;
    }

    public byte[] Finish()
    {
        if (!IsComplete) throw new InvalidOperationException("Snapshot is incomplete.");
        var result = new byte[manifest.CompressedLength]; var offset = 0;
        foreach (var chunk in chunks) { Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length); offset += chunk.Length; }
        if (!AuthorityHashChain.FixedEquals(SnapshotCodec.Hash(result), manifest.ContentHash)) throw new InvalidDataException("Snapshot content hash mismatch.");
        return result;
    }
}
