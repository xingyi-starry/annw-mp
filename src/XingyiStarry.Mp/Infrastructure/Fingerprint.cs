using System;
using System.IO;
using System.Security.Cryptography;

namespace XingyiStarry.Mp.Infrastructure;

internal static class Fingerprint
{
    public static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return ToHex(sha.ComputeHash(stream));
    }

    public static string Combined(params string[] paths)
    {
        using var sha = SHA256.Create();
        foreach (var path in paths)
        {
            var fileHash = Convert.FromBase64String(Convert.ToBase64String(HexToBytes(FileSha256(path))));
            sha.TransformBlock(fileHash, 0, fileHash.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return ToHex(sha.Hash ?? throw new InvalidOperationException("Hash did not finalize."));
    }

    private static byte[] HexToBytes(string value)
    {
        var result = new byte[value.Length / 2];
        for (var i = 0; i < result.Length; i++) result[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
        return result;
    }
    private static string ToHex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
}
