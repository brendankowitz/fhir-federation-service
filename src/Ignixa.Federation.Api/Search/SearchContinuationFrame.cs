using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Api.Continuations;

namespace Ignixa.Federation.Api.Search;

/// <summary>
/// The snapshot a primary search continuation carries: how the search arrived, its stored raw query, and the
/// executor's unsorted snapshot. Redemption replans the stored query against the current view and requires it to
/// admit exactly the saved shard requests, so a drifted planner or view is refused before any shard call.
/// </summary>
/// <remarks>
/// Big-endian v1: version byte, origin byte (0 <c>GET</c>, 1 <c>POST</c>), signed 32-bit byte length, the stored
/// raw query in strict UTF-8 (without <c>?</c>), then the <see cref="UnsortedSnapshotCodec"/> bytes. Read only from
/// authenticated bytes; nothing may trail the codec bytes, which the codec itself bounds.
/// </remarks>
internal sealed class SearchContinuationFrame
{
    private const byte Version = 1;
    private const int HeaderBytes = 2 + sizeof(int);

    public SearchContinuationFrame(bool post, string rawQuery, byte[] snapshot)
    {
        Post = post;
        RawQuery = rawQuery;
        Snapshot = snapshot;
    }

    /// <summary>Gets a value indicating whether the search arrived as <c>POST _search</c>, which fixes each shard's method.</summary>
    public bool Post { get; }

    /// <summary>Gets the original raw query (URL query, then form body), without <c>?</c>; also the page's <c>self</c> query.</summary>
    public string RawQuery { get; }

    /// <summary>Gets the encoded <see cref="UnsortedContinuationSnapshot"/>.</summary>
    public byte[] Snapshot { get; }

    /// <summary>Reads a frame.</summary>
    /// <param name="bytes">Authenticated snapshot bytes.</param>
    /// <param name="frame">The frame, when the bytes are a well-formed current frame.</param>
    /// <returns>
    /// <see cref="UnsortedSnapshotOutcome.Success"/>, <see cref="UnsortedSnapshotOutcome.UnsupportedVersion"/> for another
    /// frame version, or <see cref="UnsortedSnapshotOutcome.Invalid"/>.
    /// </returns>
    public static UnsortedSnapshotOutcome TryRead(ReadOnlySpan<byte> bytes, [NotNullWhen(true)] out SearchContinuationFrame? frame)
    {
        frame = null;
        if (bytes.IsEmpty)
        {
            return UnsortedSnapshotOutcome.Invalid;
        }

        if (bytes[0] != Version)
        {
            return UnsortedSnapshotOutcome.UnsupportedVersion;
        }

        if (bytes.Length < HeaderBytes || bytes[1] > 1)
        {
            return UnsortedSnapshotOutcome.Invalid;
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(bytes[2..HeaderBytes]);
        ReadOnlySpan<byte> rest = bytes[HeaderBytes..];
        if (length < 0 || length >= rest.Length || !StrictUtf8.TryGetString(rest[..length], out string? raw))
        {
            return UnsortedSnapshotOutcome.Invalid;
        }

        frame = new SearchContinuationFrame(bytes[1] == 1, raw, rest[length..].ToArray());
        return UnsortedSnapshotOutcome.Success;
    }

    /// <summary>Writes the frame.</summary>
    /// <returns>The frame bytes.</returns>
    public byte[] ToArray()
    {
        byte[] raw = StrictUtf8.GetBytes(RawQuery, nameof(RawQuery));
        byte[] bytes = new byte[HeaderBytes + raw.Length + Snapshot.Length];
        bytes[0] = Version;
        bytes[1] = Post ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(2), raw.Length);
        raw.CopyTo(bytes.AsSpan(HeaderBytes));
        Snapshot.CopyTo(bytes.AsSpan(HeaderBytes + raw.Length));
        return bytes;
    }
}
