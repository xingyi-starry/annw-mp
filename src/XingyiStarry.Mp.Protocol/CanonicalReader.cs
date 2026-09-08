using System;
using System.IO;
using System.Text;

namespace XingyiStarry.Mp.Protocol;

public sealed class CanonicalReader : IDisposable
{
    private readonly MemoryStream stream;
    private readonly BinaryReader reader;

    public CanonicalReader(byte[] bytes)
    {
        stream = new MemoryStream(bytes ?? throw new ArgumentNullException(nameof(bytes)), false);
        reader = new BinaryReader(stream, new UTF8Encoding(false, true), true);
    }

    public byte ReadByte() => reader.ReadByte();
    public bool ReadBoolean() => reader.ReadBoolean();
    public ushort ReadUInt16() => reader.ReadUInt16();
    public int ReadInt32() => reader.ReadInt32();
    public long ReadInt64() => reader.ReadInt64();
    public ulong ReadUInt64() => reader.ReadUInt64();
    public double ReadDouble() => reader.ReadDouble();
    public float ReadSingle() => reader.ReadSingle();
    public Guid ReadGuid() => new Guid(ReadBytes(16));
    public string ReadStringValue() => Encoding.UTF8.GetString(ReadBytes(ProtocolConstants.MaxPacketBytes));
    public byte[] ReadBytes(int maximum = ProtocolConstants.MaxPacketBytes)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > maximum || length > stream.Length - stream.Position)
            throw new InvalidDataException("Invalid length-prefixed field.");
        return reader.ReadBytes(length);
    }
    public void EnsureEnd()
    {
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing bytes in canonical payload.");
    }
    public void Dispose()
    {
        reader.Dispose();
        stream.Dispose();
    }
}
