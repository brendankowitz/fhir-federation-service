using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Api.Continuations;

/// <summary>
/// Bounded payload for primary unsorted paging (design spec §7.4), independent of the protected envelope.
/// </summary>
/// <remarks>
/// Big-endian v2: version byte, original page size, stream count, ordered requests, one continuation, then the
/// 32-byte registry version of exactly the participating shards. The unbound draft v1 is not redeemable.
/// A request is shard, type, sent or template count, method byte, occurrence count, then ordered key/value strings.
/// The continuation tag selects start (shard), search (exact request and anchor), or cursor (shard, type, opaque
/// target, redemption byte, optional anchor, optional unsigned 64-bit fingerprint, power, steps).
/// An anchor is shard, type and logical id. All lengths, counts, power and steps are signed 32-bit integers;
/// strings are byte-length-prefixed strict UTF-8. Option flags are one byte, 0 or 1. No trailing data is allowed.
/// Decode only authenticated snapshot bytes, using the same immutable registry as the executor. The participating
/// shards' registry version must still match, independently of the outer envelope's dependency checks.
/// </remarks>
internal sealed class UnsortedSnapshotCodec
{
    private const byte Version = 2;
    private const byte AtStart = 0;
    private const byte AtSearch = 1;
    private const byte AtCursor = 2;
    private const int MinimumRequestBytes = (4 * sizeof(int)) + sizeof(byte) + 2; // Two nonempty strings.
    private const int MinimumOccurrenceBytes = (2 * sizeof(int)) + 1; // Nonempty key, possibly empty value.
    private const int RegistryVersionBytes = 32;
    private readonly int _byteLimit;
    private readonly ShardRegistry _registry;

    internal UnsortedSnapshotCodec(int byteLimit, ShardRegistry registry)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteLimit);
        ArgumentNullException.ThrowIfNull(registry);
        _byteLimit = byteLimit;
        _registry = registry;
    }

    internal UnsortedSnapshotOutcome Encode(UnsortedContinuationSnapshot snapshot, out byte[]? bytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bytes = null;
        try
        {
            var writer = new PayloadWriter(_byteLimit);
            writer.WriteByte(Version);
            writer.WriteInt32(snapshot.PageSize);
            writer.WriteInt32(snapshot.Streams.Count);
            foreach (ShardSearchRequest stream in snapshot.Streams)
            {
                WriteRequest(writer, stream);
            }

            WriteContinuation(writer, snapshot.Continuation);
            if (ParticipatingVersion(snapshot.Streams) is not { } revision)
            {
                return UnsortedSnapshotOutcome.Invalid;
            }

            writer.WriteBytes(revision);
            bytes = writer.ToArray();
            return UnsortedSnapshotOutcome.Success;
        }
        catch (PayloadTooLargeException)
        {
            return UnsortedSnapshotOutcome.TooLarge;
        }
        catch (EncoderFallbackException)
        {
            // Fallback exceptions carry input characters; expose only the classification.
            return UnsortedSnapshotOutcome.Invalid;
        }
    }

    internal UnsortedSnapshotOutcome Decode(ReadOnlySpan<byte> bytes, out UnsortedContinuationSnapshot? snapshot)
    {
        snapshot = null;
        if (bytes.Length > _byteLimit)
        {
            return UnsortedSnapshotOutcome.TooLarge;
        }

        if (bytes.IsEmpty)
        {
            return UnsortedSnapshotOutcome.Invalid;
        }

        if (bytes[0] != Version)
        {
            return UnsortedSnapshotOutcome.UnsupportedVersion;
        }

        if (bytes.Length <= RegistryVersionBytes)
        {
            return UnsortedSnapshotOutcome.Invalid;
        }

        try
        {
            var reader = new PayloadReader(bytes[1..^RegistryVersionBytes]);
            int pageSize = reader.ReadInt32();
            int streamCount = reader.ReadCount(MinimumRequestBytes);
            if (streamCount == 0)
            {
                return UnsortedSnapshotOutcome.Invalid;
            }

            var streams = new List<ShardSearchRequest>();
            for (int i = 0; i < streamCount; i++)
            {
                streams.Add(ReadRequest(ref reader));
            }

            UnsortedContinuation continuation = ReadContinuation(ref reader);
            if (!reader.AtEnd)
            {
                return UnsortedSnapshotOutcome.Invalid;
            }

            var restored = new UnsortedContinuationSnapshot(streams, pageSize, continuation);
            if (ParticipatingVersion(restored.Streams) is not { } revision
                || !bytes[^RegistryVersionBytes..].SequenceEqual(revision))
            {
                return UnsortedSnapshotOutcome.Gone;
            }

            snapshot = restored;
            return UnsortedSnapshotOutcome.Success;
        }
        catch (FormatException)
        {
            return UnsortedSnapshotOutcome.Invalid;
        }
        catch (ArgumentException)
        {
            // Core constructors own domain validation. Never forward their exception or parameter values.
            return UnsortedSnapshotOutcome.Invalid;
        }
    }

    private byte[]? ParticipatingVersion(IReadOnlyList<ShardSearchRequest> streams)
    {
        foreach (ShardSearchRequest stream in streams)
        {
            if (!_registry.TryGetById(stream.Shard, out _))
            {
                return null;
            }
        }

        return Convert.FromHexString(_registry.ComputeVersion(streams.Select(stream => stream.Shard)));
    }

    private static void WriteRequest(PayloadWriter writer, ShardSearchRequest request)
    {
        writer.WriteText(request.Shard.Value);
        writer.WriteText(request.Type.Value);
        writer.WriteInt32(request.PageSize);
        writer.WriteByte(request.Method == ShardSearchMethod.Get ? (byte)0 : (byte)1);
        writer.WriteInt32(request.Parameters.Count);
        foreach (ParameterOccurrence occurrence in request.Parameters)
        {
            writer.WriteText(occurrence.Key);
            writer.WriteText(occurrence.Value);
        }
    }

    private static ShardSearchRequest ReadRequest(ref PayloadReader reader)
    {
        var shard = ShardId.Parse(reader.ReadText());
        var type = ResourceType.Parse(reader.ReadText());
        int count = reader.ReadInt32();
        ShardSearchMethod method = reader.ReadFlag() ? ShardSearchMethod.PostSearch : ShardSearchMethod.Get;
        int occurrenceCount = reader.ReadCount(MinimumOccurrenceBytes);
        var occurrences = new List<ParameterOccurrence>();
        for (int i = 0; i < occurrenceCount; i++)
        {
            occurrences.Add(new ParameterOccurrence(reader.ReadText(), reader.ReadText()));
        }

        return new ShardSearchRequest(shard, type, occurrences, count, method);
    }

    private static void WriteContinuation(PayloadWriter writer, UnsortedContinuation continuation)
    {
        if (continuation.Search is { } replay)
        {
            writer.WriteByte(AtSearch);
            WriteRequest(writer, replay);
            WriteAnchor(writer, continuation.LastEmitted!);
        }
        else if (continuation.Cursor is { } cursor)
        {
            writer.WriteByte(AtCursor);
            writer.WriteText(cursor.Shard.Value);
            writer.WriteText(cursor.Type.Value);
            writer.WriteText(cursor.Target);
            writer.WriteByte(cursor.Redemption == NextLinkRedemption.FollowGet ? (byte)0 : (byte)1);
            writer.WriteFlag(continuation.LastEmitted is not null);
            if (continuation.LastEmitted is { } anchor)
            {
                WriteAnchor(writer, anchor);
            }

            CursorCycleState cycle = continuation.CycleState;
            writer.WriteFlag(cycle.SavedFingerprint.HasValue);
            if (cycle.SavedFingerprint is { } fingerprint)
            {
                writer.WriteUInt64(fingerprint);
            }

            writer.WriteInt32(cycle.Power);
            writer.WriteInt32(cycle.Steps);
        }
        else
        {
            writer.WriteByte(AtStart);
            writer.WriteText(continuation.Shard.Value);
        }
    }

    private static UnsortedContinuation ReadContinuation(ref PayloadReader reader) => reader.ReadByte() switch
    {
        AtStart => UnsortedContinuation.AtStart(ShardId.Parse(reader.ReadText())),
        AtSearch => UnsortedContinuation.AtSearch(ReadRequest(ref reader), ReadAnchor(ref reader)),
        AtCursor => ReadCursor(ref reader),
        _ => throw new FormatException("Unknown continuation shape."),
    };

    private static UnsortedContinuation ReadCursor(ref PayloadReader reader)
    {
        var shard = ShardId.Parse(reader.ReadText());
        var type = ResourceType.Parse(reader.ReadText());
        string target = reader.ReadText();
        NextLinkRedemption redemption = reader.ReadFlag() ? NextLinkRedemption.RepostToSearch : NextLinkRedemption.FollowGet;
        ResourceKey? anchor = reader.ReadFlag() ? ReadAnchor(ref reader) : null;
        ulong? fingerprint = reader.ReadFlag() ? reader.ReadUInt64() : null;
        var cycle = new CursorCycleState(fingerprint, reader.ReadInt32(), reader.ReadInt32());
        return UnsortedContinuation.AtCursor(new ShardCursor(shard, type, target, redemption), anchor, cycle);
    }

    private static void WriteAnchor(PayloadWriter writer, ResourceKey anchor)
    {
        writer.WriteText(anchor.Shard.Value);
        writer.WriteText(anchor.Type.Value);
        writer.WriteText(anchor.Id.Value);
    }

    private static ResourceKey ReadAnchor(ref PayloadReader reader) =>
        new(ShardId.Parse(reader.ReadText()), ResourceType.Parse(reader.ReadText()), ResourceId.Parse(reader.ReadText()));

    private ref struct PayloadReader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _remaining = bytes;

        public readonly bool AtEnd => _remaining.IsEmpty;

        public byte ReadByte() => Take(sizeof(byte))[0];

        public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Take(sizeof(int)));

        public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64BigEndian(Take(sizeof(ulong)));

        public bool ReadFlag() => ReadByte() switch
        {
            0 => false,
            1 => true,
            _ => throw new FormatException("Invalid snapshot flag."),
        };

        public int ReadCount(int minimumItemBytes)
        {
            int count = ReadInt32();
            return count >= 0 && count <= _remaining.Length / minimumItemBytes
                ? count
                : throw new FormatException("Invalid snapshot count.");
        }

        public string ReadText() => StrictUtf8.TryGetString(Take(ReadInt32()), out string? text)
            ? text
            : throw new FormatException("Invalid snapshot text.");

        private ReadOnlySpan<byte> Take(int length)
        {
            if ((uint)length > (uint)_remaining.Length)
            {
                throw new FormatException("Invalid snapshot length.");
            }

            ReadOnlySpan<byte> field = _remaining[..length];
            _remaining = _remaining[length..];
            return field;
        }
    }

    private sealed class PayloadWriter(int byteLimit)
    {
        private static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        private byte[] _buffer = [];
        private int _written;

        public byte[] ToArray() => _buffer.AsSpan(0, _written).ToArray();

        public void WriteByte(byte value) => Reserve(sizeof(byte))[0] = value;

        public void WriteBytes(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Reserve(bytes.Length));

        public void WriteFlag(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteInt32(int value) => BinaryPrimitives.WriteInt32BigEndian(Reserve(sizeof(int)), value);

        public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64BigEndian(Reserve(sizeof(ulong)), value);

        public void WriteText(string value)
        {
            // Even surrogate pairs need at least one UTF-8 byte per UTF-16 code unit. Reject by this lower
            // bound before scanning, then reserve the exact byte count before encoding into the buffer.
            if (value.Length > byteLimit - _written - sizeof(int))
            {
                throw new PayloadTooLargeException();
            }

            int length;
            try
            {
                length = Encoding.GetByteCount(value);
            }
            catch (ArgumentException exception) when (exception is not EncoderFallbackException)
            {
                // A UTF-8 byte count that overflows Int32 cannot fit any configured payload budget.
                throw new PayloadTooLargeException();
            }

            WriteInt32(length);
            Encoding.GetBytes(value.AsSpan(), Reserve(length));
        }

        private Span<byte> Reserve(int length)
        {
            if (length > byteLimit - _written)
            {
                throw new PayloadTooLargeException();
            }

            int used = _written + length;
            if (used > _buffer.Length)
            {
                int grown = _buffer.Length + Math.Min(_buffer.Length, byteLimit - _buffer.Length);
                Array.Resize(ref _buffer, Math.Max(used, Math.Max(grown, Math.Min(byteLimit, 256))));
            }

            Span<byte> field = _buffer.AsSpan(_written, length);
            _written = used;
            return field;
        }
    }

    [SuppressMessage("Design", "CA1032:Implement standard exception constructors",
        Justification = "Private control-flow signal classified by Encode; it carries no input data.")]
    private sealed class PayloadTooLargeException : Exception;
}
