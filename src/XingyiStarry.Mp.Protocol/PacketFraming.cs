using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace XingyiStarry.Mp.Protocol;

public static class PacketFraming
{
    public static async Task WriteAsync(Stream stream, Envelope envelope, CancellationToken cancellationToken)
    {
        var payload = ProtocolCodec.EncodeEnvelope(envelope);
        if (payload.Length > ProtocolConstants.MaxPacketBytes) throw new InvalidDataException("Packet is too large.");
        var prefix = new[] { (byte)payload.Length, (byte)(payload.Length >> 8), (byte)(payload.Length >> 16), (byte)(payload.Length >> 24) };
        await stream.WriteAsync(prefix, 0, prefix.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<Envelope?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        if (!await ReadExactAsync(stream, prefix, true, cancellationToken).ConfigureAwait(false)) return null;
        var length = prefix[0] | prefix[1] << 8 | prefix[2] << 16 | prefix[3] << 24;
        if (length <= 0 || length > ProtocolConstants.MaxPacketBytes) throw new InvalidDataException("Invalid packet length.");
        var payload = new byte[length];
        await ReadExactAsync(stream, payload, false, cancellationToken).ConfigureAwait(false);
        return ProtocolCodec.DecodeEnvelope(payload);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, bool eofAllowed, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (eofAllowed && offset == 0) return false;
                throw new EndOfStreamException("Connection closed inside a packet.");
            }
            offset += read;
        }
        return true;
    }
}
