using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Ignixa.Federation.Api.Continuations;

/// <summary>
/// The caller a continuation is bound to (design spec §7.4): a SHA-256 digest over the validated issuer, subject,
/// effective object id (or, only when there is none, client id), and the role values sorted ordinally. Only the
/// digest is carried in protected contents; the raw identifiers and roles never are.
/// </summary>
/// <remarks>
/// <para>
/// The digest input is an unambiguous encoding: a versioned domain label, then each value as strict UTF-8 with a
/// 4-byte big-endian length prefix, a tag that distinguishes an object id from a client id, and a role count. No
/// delimiter or concatenation can make two different callers collide, and ill-formed UTF-16 is refused rather than
/// collapsed to U+FFFD. Roles are compared ordinally, so case or Unicode normalisation differences are different
/// callers; reordering is not a difference, duplicates are.
/// </para>
/// <para>
/// This type takes identifiers that composition has already validated from the authenticated principal; it does
/// not read claims and grants nothing. Missing required identifiers are refused, never replaced by a synthetic
/// identity. Instances are immutable and thread-safe. The default <see cref="object.ToString"/> is kept, so no
/// identifier is rendered.
/// </para>
/// </remarks>
internal sealed class ContinuationBinding
{
    /// <summary>The digest length in bytes.</summary>
    public const int HashLength = SHA256.HashSizeInBytes;

    private const byte ObjectIdKind = 1;
    private const byte ClientIdKind = 2;

    private static readonly byte[] DomainLabel = "Ignixa.Federation.Continuations.Binding.v1"u8.ToArray();

    private readonly byte[] _hash;

    private ContinuationBinding(byte[] hash) => _hash = hash;

    /// <summary>Gets the binding digest written into protected contents.</summary>
    internal ReadOnlySpan<byte> Hash => _hash;

    /// <summary>Creates the binding for a validated caller.</summary>
    /// <param name="issuer">The validated token issuer.</param>
    /// <param name="subject">The validated subject.</param>
    /// <param name="objectId">The effective object id, or <see langword="null"/> when the caller has none.</param>
    /// <param name="clientId">The client id; bound only when <paramref name="objectId"/> is <see langword="null"/>.</param>
    /// <param name="roles">The caller's role values, in any order; duplicates and empty values are significant.</param>
    /// <returns>The binding.</returns>
    /// <exception cref="ArgumentException">
    /// An identifier is blank, neither an object id nor a client id is present, a role is <see langword="null"/>, or
    /// a value is not well-formed UTF-16. Messages never contain the values.
    /// </exception>
    public static ContinuationBinding Create(string issuer, string subject, string? objectId, string? clientId, IEnumerable<string> roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(roles);

        // An object id that is present but blank is malformed input, not a reason to fall back to the client id.
        (byte kind, byte[] id) = objectId is not null
            ? (ObjectIdKind, EncodeIdentifier(objectId, nameof(objectId)))
            : (ClientIdKind, EncodeIdentifier(clientId, nameof(clientId)));

        string[] sortedRoles = [.. roles];
        if (Array.IndexOf(sortedRoles, null) >= 0)
        {
            throw new ArgumentException("Role values must not be null.", nameof(roles));
        }

        Array.Sort(sortedRoles, StringComparer.Ordinal);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendPrefixed(hash, DomainLabel);
        AppendPrefixed(hash, StrictUtf8.GetBytes(issuer, nameof(issuer)));
        AppendPrefixed(hash, StrictUtf8.GetBytes(subject, nameof(subject)));
        hash.AppendData([kind]);
        AppendPrefixed(hash, id);
        AppendLength(hash, sortedRoles.Length);
        foreach (string role in sortedRoles)
        {
            AppendPrefixed(hash, StrictUtf8.GetBytes(role, nameof(roles)));
        }

        return new ContinuationBinding(hash.GetHashAndReset());
    }

    /// <summary>Compares a digest read from authenticated contents with this binding in fixed time.</summary>
    /// <param name="hash">The digest from the contents.</param>
    /// <returns>Whether the contents were issued to this caller.</returns>
    public bool Matches(ReadOnlySpan<byte> hash) => CryptographicOperations.FixedTimeEquals(_hash, hash);

    private static byte[] EncodeIdentifier(string? value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return StrictUtf8.GetBytes(value, paramName);
    }

    private static void AppendPrefixed(IncrementalHash hash, byte[] value)
    {
        AppendLength(hash, value.Length);
        hash.AppendData(value);
    }

    private static void AppendLength(IncrementalHash hash, int length)
    {
        Span<byte> prefix = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(prefix, length);
        hash.AppendData(prefix);
    }
}
