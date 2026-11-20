using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution;

/// <summary>
/// Constant-size cycle detection for the cursors of one unsorted stream, carried from page to page in
/// <see cref="UnsortedContinuation"/> so that a cyclic <c>next</c> chain is a shard failure even when the cycle only
/// shows across continuations (design spec §7.8). It follows Brent's algorithm: it saves the fingerprint of the cursor
/// seen at each power of two, and a cursor whose fingerprint equals the saved one closes a cycle.
/// </summary>
/// <remarks>
/// <para>
/// A fingerprint is the first 64 bits of a SHA-256 over the cursor's shard, type, target and redemption. The state holds no
/// cursor text and no resource data, and <see cref="ToString"/> leaves the fingerprint out. A cycle of length λ entered
/// after μ distinct cursors is detected within about 2·max(μ, λ) + λ followed cursors.
/// </para>
/// <para>
/// The public constructor exists so that a continuation token can rebuild the state; it accepts exactly the states the
/// algorithm can reach.
/// </para>
/// </remarks>
public sealed record CursorCycleState
{
    /// <summary>The largest <see cref="Power"/>. It stops doubling there, and detection still covers cycles up to this length.</summary>
    private const int MaxPower = 1 << 30;

    /// <summary>Initializes a new instance of the <see cref="CursorCycleState"/> class.</summary>
    /// <param name="savedFingerprint">
    /// The fingerprint saved at the last power of two, or <see langword="null"/> when no cursor was followed yet.
    /// </param>
    /// <param name="power">The length of the current window: a power of two, 1 exactly when no cursor was followed yet.</param>
    /// <param name="steps">The cursors followed in the current window, from 0 to <paramref name="power"/> - 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="power"/> is not a power of two from 1 to 2^30, or <paramref name="steps"/> is outside its window.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="savedFingerprint"/> is absent while <paramref name="power"/> is above 1, or present while it is 1.
    /// </exception>
    public CursorCycleState(ulong? savedFingerprint, int power, int steps)
    {
        if (power is < 1 or > MaxPower || !int.IsPow2(power))
        {
            throw new ArgumentOutOfRangeException(nameof(power), power, "The power must be a power of two from 1 to 2^30.");
        }

        if (steps < 0 || steps >= power)
        {
            throw new ArgumentOutOfRangeException(nameof(steps), steps, "The steps must be at least 0 and less than the power.");
        }

        if (savedFingerprint.HasValue != (power > 1))
        {
            throw new ArgumentException("A fingerprint is saved exactly when a cursor was followed.", nameof(savedFingerprint));
        }

        SavedFingerprint = savedFingerprint;
        Power = power;
        Steps = steps;
    }

    /// <summary>Gets the state of a stream that has followed no cursor: a fresh stream, or one resumed at its first request.</summary>
    public static CursorCycleState Initial { get; } = new(null, 1, 0);

    /// <summary>Gets the fingerprint saved at the last power of two, or <see langword="null"/> when no cursor was followed.</summary>
    public ulong? SavedFingerprint { get; }

    /// <summary>Gets the length of the current window; always a power of two.</summary>
    public int Power { get; }

    /// <summary>Gets the cursors followed in the current window; less than <see cref="Power"/>.</summary>
    public int Steps { get; }

    /// <summary>Describes the state without its fingerprint.</summary>
    /// <returns>The power and the steps.</returns>
    public override string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"CursorCycleState {{ Power = {Power}, Steps = {Steps} }}");

    /// <summary>
    /// Records that the stream followed <paramref name="successor"/>. Call it once per committed forward transition, never
    /// for a replay of the current input.
    /// </summary>
    /// <param name="successor">The cursor the stream follows next.</param>
    /// <param name="followed">The state after following it, when no cycle closed.</param>
    /// <returns><see langword="false"/> when <paramref name="successor"/> closes a cycle.</returns>
    internal bool TryFollow(ShardCursor successor, [NotNullWhen(true)] out CursorCycleState? followed)
    {
        ulong fingerprint = Fingerprint(successor);
        if (SavedFingerprint == fingerprint)
        {
            followed = null;
            return false;
        }

        int steps = Steps + 1;
        followed = steps < Power
            ? new CursorCycleState(SavedFingerprint, Power, steps)
            : new CursorCycleState(fingerprint, Power == MaxPower ? Power : Power * 2, 0);
        return true;
    }

    /// <summary>Hashes each field with its length, so that no two distinct cursors share an input.</summary>
    private static ulong Fingerprint(ShardCursor cursor)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, cursor.Shard.Value);
        Append(hash, cursor.Type.Value);
        Append(hash, cursor.Target);
        Span<byte> redemption = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(redemption, (int)cursor.Redemption);
        hash.AppendData(redemption);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        hash.GetHashAndReset(digest);
        return BinaryPrimitives.ReadUInt64BigEndian(digest);
    }

    private static void Append(IncrementalHash hash, string field)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(field);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
