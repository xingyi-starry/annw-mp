using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace XingyiStarry.Mp.Protocol;

public static class SnapshotCodec
{
    public static byte[] Compress(byte[] snapshot)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(snapshot, 0, snapshot.Length);
        return output.ToArray();
    }

    public static byte[] Decompress(byte[] compressed, int maximumBytes)
    {
        using var input = new MemoryStream(compressed, false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = gzip.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) throw new InvalidDataException("Expanded snapshot is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public static byte[] Hash(byte[] bytes) { using var sha = SHA256.Create(); return sha.ComputeHash(bytes); }
}
