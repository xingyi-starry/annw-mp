using System;
using System.IO;
using System.Text;

namespace XingyiStarry.Mp.Protocol;

public sealed class CanonicalWriter : IDisposable
{
    private readonly MemoryStream stream = new MemoryStream();
    private readonly BinaryWriter writer;

    public CanonicalWriter() => writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);

    public void Write(byte value) => writer.Write(value);
    public void Write(bool value) => writer.Write(value);
    public void Write(ushort value) => writer.Write(value);
    public void Write(int value) => writer.Write(value);
    public void Write(long value) => writer.Write(value);
    public void Write(ulong value) => writer.Write(value);
    public void Write(double value) => writer.Write(value);
    public void Write(float value) => writer.Write(value);
    public void Write(Guid value) => Write(value.ToByteArray());
    public void Write(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? throw new ArgumentNullException(nameof(value)));
        Write(bytes);
    }
    public void Write(byte[] value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        writer.Write(value.Length);
        writer.Write(value);
    }
    public byte[] ToArray()
    {
        writer.Flush();
        return stream.ToArray();
    }
    public void Dispose()
    {
        writer.Dispose();
        stream.Dispose();
    }
}
