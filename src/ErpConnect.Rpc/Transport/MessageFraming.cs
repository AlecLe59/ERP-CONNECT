using System.Buffers;
using System.Buffers.Binary;

namespace ErpConnect.Rpc.Transport;

/// <summary>
/// Découpage des messages : longueur sur 4 octets (entier non signé, petit-boutiste), puis le JSON en UTF-8.
/// Une longueur supérieure au maximum est refusée sans lire le contenu.
/// </summary>
public static class MessageFraming
{
    public const int HeaderSize = 4;

    /// <summary>Lit un message complet. Renvoie null si le flux est fermé proprement avant un nouvel en-tête.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, int maxMessageBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderSize];
        var read = await ReadFullyAsync(stream, header, ct).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < HeaderSize)
        {
            throw new FramingException("En-tête de message tronqué.");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0)
        {
            throw new FramingException("Message vide.");
        }

        if (length > (uint)maxMessageBytes)
        {
            throw new FramingException($"Message de {length} octets refusé (maximum {maxMessageBytes}).");
        }

        var payload = new byte[length];
        var got = await ReadFullyAsync(stream, payload, ct).ConfigureAwait(false);
        if (got != payload.Length)
        {
            throw new FramingException("Longueur annoncée supérieure aux données reçues.");
        }

        return payload;
    }

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = ArrayPool<byte>.Shared.Rent(HeaderSize + payload.Length);
        try
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)payload.Length);
            payload.Span.CopyTo(buffer.AsSpan(HeaderSize));
            await stream.WriteAsync(buffer.AsMemory(0, HeaderSize + payload.Length), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<int> ReadFullyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}

/// <summary>Erreur de transport : la connexion est fermée.</summary>
public sealed class FramingException : Exception
{
    public FramingException(string message)
        : base(message)
    {
    }

    public FramingException()
    {
    }

    public FramingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
