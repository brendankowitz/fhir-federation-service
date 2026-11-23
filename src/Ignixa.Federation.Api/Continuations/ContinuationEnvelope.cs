using System.Buffers.Binary;

namespace Ignixa.Federation.Api.Continuations;

/// <summary>
/// The format v1 plaintext of an inline continuation, before Data Protection. A bounded binary layout, big-endian,
/// with no trailing bytes:
/// <list type="table">
/// <item><term>0</term><description>Format version, 1 byte, always 1.</description></item>
/// <item><term>1</term><description>Chain issue instant: UTC ticks of the original (first) issue, 8 bytes.</description></item>
/// <item><term>9</term><description>Issue instant: UTC ticks of this token's issue, 8 bytes.</description></item>
/// <item><term>17</term><description>The original chain's absolute deadline and this token's sliding deadline, 8 bytes each.</description></item>
/// <item><term>33</term><description>Caller binding digest, 32 bytes (see <see cref="ContinuationBinding"/>).</description></item>
/// <item><term>65</term><description>Dependencies (see <see cref="ContinuationDependencySet"/>), starting with a 2-byte count.</description></item>
/// <item><term>…</term><description>Snapshot length, 4 bytes, then the snapshot bytes.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Reading is only ever done on authenticated contents, but the layout is still checked strictly: an unknown
/// version, a timestamp outside <c>0 ≤ chain ≤ issued ≤ DateTimeOffset.MaxValue</c>, a non-canonical dependency
/// set, truncation, or trailing bytes all fail. Spans returned by <see cref="TryRead"/> alias the contents.
/// </remarks>
internal readonly ref struct ContinuationEnvelope
{
    /// <summary>The only supported format version.</summary>
    public const byte Version = 1;

    private const int ChainIssuedOffset = 1;
    private const int IssuedOffset = ChainIssuedOffset + sizeof(long);
    private const int ChainDeadlineOffset = IssuedOffset + sizeof(long);
    private const int SlidingDeadlineOffset = ChainDeadlineOffset + sizeof(long);
    private const int BindingOffset = SlidingDeadlineOffset + sizeof(long);
    private const int DependenciesOffset = BindingOffset + ContinuationBinding.HashLength;
    private const int HeaderAndSnapshotLength = DependenciesOffset + sizeof(uint);

    private ContinuationEnvelope(
        long chainIssuedTicks,
        long issuedTicks,
        long chainDeadlineTicks,
        long slidingDeadlineTicks,
        ReadOnlySpan<byte> bindingHash,
        ContinuationDependencySet dependencies,
        ReadOnlySpan<byte> snapshot)
    {
        ChainIssuedTicks = chainIssuedTicks;
        IssuedTicks = issuedTicks;
        ChainDeadlineTicks = chainDeadlineTicks;
        DeadlineTicks = Math.Min(chainDeadlineTicks, slidingDeadlineTicks);
        BindingHash = bindingHash;
        Dependencies = dependencies;
        Snapshot = snapshot;
    }

    /// <summary>Gets the UTC ticks of the chain's original issue; the absolute lifetime runs from here.</summary>
    public long ChainIssuedTicks { get; }

    /// <summary>Gets the UTC ticks of this token's issue; the sliding lifetime runs from here.</summary>
    public long IssuedTicks { get; }

    /// <summary>Gets the deadline fixed at the original issue.</summary>
    public long ChainDeadlineTicks { get; }

    /// <summary>Gets the earlier of the sliding and absolute deadlines.</summary>
    public long DeadlineTicks { get; }

    /// <summary>Gets the caller binding digest.</summary>
    public ReadOnlySpan<byte> BindingHash { get; }

    /// <summary>Gets the authenticated dependencies.</summary>
    public ContinuationDependencySet Dependencies { get; }

    /// <summary>Gets the snapshot bytes.</summary>
    public ReadOnlySpan<byte> Snapshot { get; }

    /// <summary>Computes the plaintext length for the given dependencies and snapshot length, without allocating.</summary>
    /// <param name="dependencies">The dependencies.</param>
    /// <param name="snapshotLength">The snapshot length in bytes.</param>
    /// <returns>The plaintext length in bytes.</returns>
    public static long Measure(ContinuationDependencySet dependencies, int snapshotLength) =>
        HeaderAndSnapshotLength + dependencies.EncodedLength + snapshotLength;

    /// <summary>Writes the plaintext.</summary>
    /// <param name="destination">A span of exactly <see cref="Measure"/> bytes.</param>
    /// <param name="chainIssuedTicks">UTC ticks of the chain's original issue.</param>
    /// <param name="issuedTicks">UTC ticks of this issue; not earlier than <paramref name="chainIssuedTicks"/>.</param>
    /// <param name="chainDeadlineTicks">The absolute deadline set at the original issue.</param>
    /// <param name="slidingDeadlineTicks">The sliding deadline set at this issue.</param>
    /// <param name="bindingHash">The caller binding digest.</param>
    /// <param name="dependencies">The dependencies.</param>
    /// <param name="snapshot">The snapshot bytes.</param>
    public static void Write(
        Span<byte> destination,
        long chainIssuedTicks,
        long issuedTicks,
        long chainDeadlineTicks,
        long slidingDeadlineTicks,
        ReadOnlySpan<byte> bindingHash,
        ContinuationDependencySet dependencies,
        ReadOnlySpan<byte> snapshot)
    {
        destination[0] = Version;
        BinaryPrimitives.WriteInt64BigEndian(destination[ChainIssuedOffset..], chainIssuedTicks);
        BinaryPrimitives.WriteInt64BigEndian(destination[IssuedOffset..], issuedTicks);
        BinaryPrimitives.WriteInt64BigEndian(destination[ChainDeadlineOffset..], chainDeadlineTicks);
        BinaryPrimitives.WriteInt64BigEndian(destination[SlidingDeadlineOffset..], slidingDeadlineTicks);
        bindingHash.CopyTo(destination[BindingOffset..]);
        int offset = DependenciesOffset + dependencies.WriteTo(destination[DependenciesOffset..]);
        BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], (uint)snapshot.Length);
        snapshot.CopyTo(destination[(offset + sizeof(uint))..]);
    }

    /// <summary>Reads authenticated plaintext, requiring a well-formed format v1 envelope.</summary>
    /// <param name="contents">The unprotected contents.</param>
    /// <param name="envelope">The envelope; its spans alias <paramref name="contents"/>.</param>
    /// <returns>Whether the contents are a well-formed format v1 envelope.</returns>
    public static bool TryRead(ReadOnlySpan<byte> contents, out ContinuationEnvelope envelope)
    {
        envelope = default;
        if (contents.Length < DependenciesOffset || contents[0] != Version)
        {
            return false;
        }

        long chainIssuedTicks = BinaryPrimitives.ReadInt64BigEndian(contents[ChainIssuedOffset..]);
        long issuedTicks = BinaryPrimitives.ReadInt64BigEndian(contents[IssuedOffset..]);
        long chainDeadlineTicks = BinaryPrimitives.ReadInt64BigEndian(contents[ChainDeadlineOffset..]);
        long slidingDeadlineTicks = BinaryPrimitives.ReadInt64BigEndian(contents[SlidingDeadlineOffset..]);
        if (chainIssuedTicks < 0 || chainIssuedTicks > issuedTicks || issuedTicks > DateTimeOffset.MaxValue.UtcTicks
            || chainDeadlineTicks <= chainIssuedTicks || slidingDeadlineTicks <= issuedTicks)
        {
            return false;
        }

        if (!ContinuationDependencySet.TryRead(contents[DependenciesOffset..], out ContinuationDependencySet? dependencies, out int consumed))
        {
            return false;
        }

        ReadOnlySpan<byte> rest = contents[(DependenciesOffset + consumed)..];
        if (!TryReadSnapshot(rest, out ReadOnlySpan<byte> snapshot))
        {
            return false;
        }

        envelope = new ContinuationEnvelope(
            chainIssuedTicks,
            issuedTicks,
            chainDeadlineTicks,
            slidingDeadlineTicks,
            contents.Slice(BindingOffset, ContinuationBinding.HashLength),
            dependencies,
            snapshot);
        return true;
    }

    private static bool TryReadSnapshot(ReadOnlySpan<byte> rest, out ReadOnlySpan<byte> snapshot)
    {
        snapshot = default;
        if (rest.Length < sizeof(uint))
        {
            return false;
        }

        // Exact length: anything after the snapshot is trailing data and fails.
        uint length = BinaryPrimitives.ReadUInt32BigEndian(rest);
        if (length != (uint)(rest.Length - sizeof(uint)))
        {
            return false;
        }

        snapshot = rest[sizeof(uint)..];
        return true;
    }
}
