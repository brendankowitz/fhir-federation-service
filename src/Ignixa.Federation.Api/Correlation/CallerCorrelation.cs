using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Ignixa.Federation.Api.Correlation;

internal static class CallerCorrelation
{
    private const int KeyLength = 32;
    private const int MaxIdentityBytes = 2048;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static ReadOnlySpan<byte> Prefix => "ignixa.federation.caller-correlation\0\u0001"u8;

    /// <summary>
    /// Derives an opaque v1 identifier from the exact validated issuer and already-selected typed subject.
    /// The caller owns the dedicated key and must not modify it during derivation.
    /// </summary>
    /// <remarks>Invalid inputs produce the first fixed refusal in key, kind, issuer, subject order and no identifier.</remarks>
    internal static bool TryDerive(
        ReadOnlySpan<byte> key, CallerSubjectKind kind, string? issuer, string? subject,
        [NotNullWhen(true)] out string? value, [NotNullWhen(false)] out string? reason)
    {
        value = null;
        if (key.Length != KeyLength || key.IndexOfAnyExcept((byte)0) < 0)
        {
            reason = "invalid correlation key";
            return false;
        }

        if (kind is not (CallerSubjectKind.Sub or CallerSubjectKind.Oid))
        {
            reason = "invalid subject kind";
            return false;
        }

        int issuerLength = GetIdentityByteCount(issuer);
        if (issuerLength == 0)
        {
            reason = "invalid issuer";
            return false;
        }

        int subjectLength = GetIdentityByteCount(subject);
        if (subjectLength == 0)
        {
            reason = "invalid subject";
            return false;
        }

        Span<byte> frame = stackalloc byte[Prefix.Length + sizeof(uint) + issuerLength + 1 + sizeof(uint) + subjectLength];
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        try
        {
            Prefix.CopyTo(frame);
            WriteField(issuer.AsSpan(), issuerLength, frame[Prefix.Length..]);
            int kindOffset = Prefix.Length + sizeof(uint) + issuerLength;
            frame[kindOffset] = (byte)kind;
            WriteField(subject.AsSpan(), subjectLength, frame[(kindOffset + 1)..]);

            HMACSHA256.HashData(key, frame, mac);
            value = "v1." + Base64Url.EncodeToString(mac);
            reason = null;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(frame);
            CryptographicOperations.ZeroMemory(mac);
        }
    }

    private static int GetIdentityByteCount(string? input)
    {
        if (input is null || input.Length is 0 or > MaxIdentityBytes)
        {
            return 0;
        }

        bool allSpaces = true;
        foreach (char c in input)
        {
            if (c < ' ' || c == '\x7f')
            {
                return 0;
            }

            allSpaces &= c == ' ';
        }

        if (allSpaces)
        {
            return 0;
        }

        try
        {
            int count = StrictUtf8.GetByteCount(input);
            return count <= MaxIdentityBytes ? count : 0;
        }
        catch (EncoderFallbackException)
        {
            return 0;
        }
    }

    private static void WriteField(ReadOnlySpan<char> input, int byteCount, Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)byteCount);
        StrictUtf8.GetBytes(input, destination[sizeof(uint)..]);
    }
}
