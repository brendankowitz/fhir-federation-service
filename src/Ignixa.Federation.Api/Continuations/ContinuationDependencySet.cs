using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Ignixa.Federation.Api.Continuations;

/// <summary>
/// The dependencies a continuation was planned against (design spec §7.4, invalidation): for each shard a stream
/// reads and each search parameter definition the query resolved, the key and the revision (fingerprint) observed
/// when the search was planned. Shard and definition keys are separate namespaces.
/// </summary>
/// <remarks>
/// <para>
/// Only these entries decide whether a continuation is still current. Redemption looks up exactly these keys and
/// nothing else, so changes to unrelated shards or definitions never invalidate it, and relevance is never inferred
/// from the whole registry or catalogue.
/// </para>
/// <para>
/// Entries are held in canonical order (group, then key by UTF-8 bytes) and encode as: count (2 bytes), then per
/// entry group (1 byte), key length (2 bytes), key, revision length (2 bytes), revision; big-endian, strict UTF-8,
/// keys and revisions non-empty. Instances are immutable and thread-safe; the default
/// <see cref="object.ToString"/> is kept, so no key or revision is rendered.
/// </para>
/// </remarks>
internal sealed class ContinuationDependencySet
{
    private const int CountLength = sizeof(ushort);
    private const int EntryOverhead = 1 + sizeof(ushort) + sizeof(ushort);

    private readonly ImmutableArray<Entry> _entries;

    private ContinuationDependencySet(ImmutableArray<Entry> entries) => _entries = entries;

    /// <summary>Gets the encoded length in bytes, count prefix included.</summary>
    public long EncodedLength
    {
        get
        {
            long length = CountLength;
            foreach (Entry entry in _entries)
            {
                length += EntryOverhead + entry.KeyBytes.Length + entry.RevisionBytes.Length;
            }

            return length;
        }
    }

    /// <summary>Creates the set from the revisions observed when the search was planned.</summary>
    /// <param name="shardRevisions">Shard registry key to revision, for each shard a stream reads.</param>
    /// <param name="definitionRevisions">Definition key to fingerprint, for each definition the query resolved.</param>
    /// <returns>The set.</returns>
    /// <exception cref="ArgumentException">
    /// A key repeats within a group, a key or revision is empty, not well-formed UTF-16, or over 65535 UTF-8 bytes, or
    /// there are more than 65535 entries. Messages never contain the values.
    /// </exception>
    public static ContinuationDependencySet Create(
        IEnumerable<KeyValuePair<string, string>> shardRevisions,
        IEnumerable<KeyValuePair<string, string>> definitionRevisions)
    {
        ArgumentNullException.ThrowIfNull(shardRevisions);
        ArgumentNullException.ThrowIfNull(definitionRevisions);

        List<Entry> entries = [];
        Add(entries, ContinuationDependencyGroup.Shard, shardRevisions, nameof(shardRevisions));
        Add(entries, ContinuationDependencyGroup.Definition, definitionRevisions, nameof(definitionRevisions));

        if (entries.Count > ushort.MaxValue)
        {
            throw new ArgumentException("Too many dependencies.", nameof(shardRevisions));
        }

        entries.Sort(static (left, right) => Compare(left, right));
        for (int i = 1; i < entries.Count; i++)
        {
            if (Compare(entries[i - 1], entries[i]) == 0)
            {
                throw new ArgumentException(
                    "A dependency key is repeated within its group.",
                    entries[i].Group == ContinuationDependencyGroup.Shard ? nameof(shardRevisions) : nameof(definitionRevisions));
            }
        }

        return new ContinuationDependencySet([.. entries]);
    }

    /// <summary>Reads a set from authenticated contents, requiring the canonical encoding.</summary>
    /// <param name="source">The contents, starting at the count prefix.</param>
    /// <param name="set">The set, or <see langword="null"/> when the encoding is not canonical.</param>
    /// <param name="consumed">The number of bytes read.</param>
    /// <returns>Whether a canonical set was read; entries out of order or repeated, an unknown group, an empty or ill-formed value, or truncation all fail.</returns>
    public static bool TryRead(ReadOnlySpan<byte> source, [NotNullWhen(true)] out ContinuationDependencySet? set, out int consumed)
    {
        set = null;
        consumed = 0;
        if (source.Length < CountLength)
        {
            return false;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(source);
        if (count > (source.Length - CountLength) / (EntryOverhead + 2))
        {
            return false;
        }

        int offset = CountLength;
        var entries = ImmutableArray.CreateBuilder<Entry>(count);
        for (int i = 0; i < count; i++)
        {
            if (source.Length - offset < 1)
            {
                return false;
            }

            var group = (ContinuationDependencyGroup)source[offset++];
            if (group is not (ContinuationDependencyGroup.Shard or ContinuationDependencyGroup.Definition)
                || !TryReadValue(source, ref offset, out byte[]? keyBytes, out string? key)
                || !TryReadValue(source, ref offset, out byte[]? revisionBytes, out string? revision))
            {
                return false;
            }

            var entry = new Entry(group, key, keyBytes, revision, revisionBytes);
            if (entries.Count > 0 && Compare(entries[^1], entry) >= 0)
            {
                return false;
            }

            entries.Add(entry);
        }

        set = new ContinuationDependencySet(entries.MoveToImmutable());
        consumed = offset;
        return true;
    }

    /// <summary>Writes the canonical encoding.</summary>
    /// <param name="destination">A span of at least <see cref="EncodedLength"/> bytes.</param>
    /// <returns>The number of bytes written.</returns>
    public int WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)_entries.Length);
        int offset = CountLength;
        foreach (Entry entry in _entries)
        {
            destination[offset++] = (byte)entry.Group;
            offset += WriteValue(destination[offset..], entry.KeyBytes);
            offset += WriteValue(destination[offset..], entry.RevisionBytes);
        }

        return offset;
    }

    /// <summary>
    /// Determines whether every dependency still has the revision it was planned against, consulting exactly these
    /// keys. A key the lookup no longer knows (<see langword="null"/>) counts as changed. Revisions compare ordinally.
    /// </summary>
    /// <param name="currentRevision">Trusted lookup of the current revision for a group and key. Exceptions it throws propagate.</param>
    /// <returns>Whether nothing relevant changed or disappeared.</returns>
    public bool AreCurrent(Func<ContinuationDependencyGroup, string, string?> currentRevision)
    {
        foreach (Entry entry in _entries)
        {
            if (!string.Equals(currentRevision(entry.Group, entry.Key), entry.Revision, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void Add(List<Entry> entries, ContinuationDependencyGroup group, IEnumerable<KeyValuePair<string, string>> revisions, string paramName)
    {
        foreach ((string key, string revision) in revisions)
        {
            entries.Add(new Entry(group, key, Encode(key, paramName), revision, Encode(revision, paramName)));
        }
    }

    private static byte[] Encode(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, paramName);
        byte[] bytes = StrictUtf8.GetBytes(value, paramName);
        return bytes.Length <= ushort.MaxValue ? bytes : throw new ArgumentException("A dependency value is too long.", paramName);
    }

    private static int Compare(Entry left, Entry right)
    {
        int byGroup = left.Group.CompareTo(right.Group);
        return byGroup != 0 ? byGroup : left.KeyBytes.AsSpan().SequenceCompareTo(right.KeyBytes);
    }

    private static int WriteValue(Span<byte> destination, byte[] value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)value.Length);
        value.CopyTo(destination[sizeof(ushort)..]);
        return sizeof(ushort) + value.Length;
    }

    private static bool TryReadValue(
        ReadOnlySpan<byte> source,
        ref int offset,
        [NotNullWhen(true)] out byte[]? bytes,
        [NotNullWhen(true)] out string? value)
    {
        bytes = null;
        value = null;
        if (source.Length - offset < sizeof(ushort))
        {
            return false;
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(source[offset..]);
        offset += sizeof(ushort);
        if (length == 0 || source.Length - offset < length)
        {
            return false;
        }

        ReadOnlySpan<byte> encoded = source.Slice(offset, length);
        if (!StrictUtf8.TryGetString(encoded, out value))
        {
            return false;
        }

        bytes = encoded.ToArray();
        offset += length;
        return true;
    }

    // Keeps the encoded bytes alongside the text so a successor re-encodes exactly what was authenticated.
    private sealed class Entry(ContinuationDependencyGroup group, string key, byte[] keyBytes, string revision, byte[] revisionBytes)
    {
        public ContinuationDependencyGroup Group { get; } = group;

        public string Key { get; } = key;

        public byte[] KeyBytes { get; } = keyBytes;

        public string Revision { get; } = revision;

        public byte[] RevisionBytes { get; } = revisionBytes;
    }
}
