using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Unicode;

namespace Ignixa.Federation.Api.Continuations;

/// <summary>
/// UTF-8 that refuses ill-formed UTF-16 (lone surrogates) instead of substituting U+FFFD, so distinct inputs can
/// never encode to the same bytes in a binding hash or an authenticated envelope.
/// </summary>
internal static class StrictUtf8
{
    private static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Encodes <paramref name="value"/>, refusing a lone surrogate.</summary>
    /// <param name="value">The text to encode.</param>
    /// <param name="paramName">The caller's parameter name, reported on refusal.</param>
    /// <returns>The UTF-8 bytes.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not well-formed UTF-16. The value is not echoed.</exception>
    public static byte[] GetBytes(string value, string paramName)
    {
        try
        {
            return Encoding.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            // Deliberately no inner exception: the fallback exception carries the offending characters.
            throw new ArgumentException("The value is not well-formed UTF-16.", paramName);
        }
    }

    /// <summary>Decodes authenticated bytes that are required to be well-formed UTF-8.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <param name="value">The decoded text, or <see langword="null"/> when the bytes are not well-formed UTF-8.</param>
    /// <returns>Whether the bytes are well-formed UTF-8.</returns>
    public static bool TryGetString(ReadOnlySpan<byte> bytes, [NotNullWhen(true)] out string? value)
    {
        if (!Utf8.IsValid(bytes))
        {
            value = null;
            return false;
        }

        value = Encoding.GetString(bytes);
        return true;
    }
}
