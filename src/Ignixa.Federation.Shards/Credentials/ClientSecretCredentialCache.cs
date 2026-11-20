using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Azure.Core;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Credentials;

/// <summary>
/// Keeps the one current client-secret SDK credential for each tenant, client and secret name, so the SDK's own per-scope
/// token cache survives across acquisitions while every acquisition still resolves the secret afresh.
/// </summary>
/// <remarks>
/// <para><b>Identity of a value.</b> A resolved secret is recognised by a SHA-256 fingerprint of its exact UTF-16 code
/// units, computed on the stack. The secret itself is held only by the SDK credential built from it; the fingerprint is
/// never logged or exposed.</para>
/// <para><b>Ordering.</b> Each lookup is numbered before the resolver is called. A slot remembers the highest number that
/// successfully observed its current value, including when that value was merely seen again. A lookup that returns a
/// different value with a lower number is stale: the store has already been seen to hold something newer, so it fails
/// closed instead of rolling the slot back or borrowing the newer credential.</para>
/// <para><b>Concurrency.</b> Only selection and replacement run under the slot's lock; resolver and SDK I/O never do.
/// Concurrent lookups of an unchanged value build one credential. Cancellation is checked inside the lock, so a canceled
/// lookup neither publishes nor advances the slot. A replaced credential is dropped from the slot; acquisitions that already
/// selected it finish with it.</para>
/// <para><b>Bound.</b> One slot per distinct tenant, client and secret name, which the shard registry fixes.</para>
/// </remarks>
internal sealed class ClientSecretCredentialCache(Func<string, string, string, TokenCredential> create)
{
    private readonly ConcurrentDictionary<(string TenantId, string ClientId, string SecretName), Slot> _slots = new();
    private long _lookups;

    /// <summary>Numbers a lookup; call before resolving the secret.</summary>
    internal long BeginLookup() => Interlocked.Increment(ref _lookups);

    /// <summary>Gets the credential for a freshly resolved secret, building and publishing one only for a newer value.</summary>
    /// <exception cref="ShardCredentialException">The value is stale (<see cref="ShardCredentialFailure.SecretUnavailable"/>).</exception>
    /// <exception cref="ArgumentException">The SDK rejected the tenant, client or secret; nothing was published.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; nothing was published.</exception>
    internal TokenCredential Select(ClientCredentialsAuth auth, long lookup, string secret, CancellationToken cancellationToken)
    {
        Span<byte> fingerprint = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(MemoryMarshal.AsBytes(secret.AsSpan()), fingerprint);
        Slot slot = _slots.GetOrAdd((auth.TenantId, auth.ClientId, auth.SecretName), static _ => new Slot());
        lock (slot.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (slot.Credential is not null && CryptographicOperations.FixedTimeEquals(slot.Fingerprint, fingerprint))
            {
                slot.Latest = Math.Max(slot.Latest, lookup);
                return slot.Credential;
            }

            if (lookup < slot.Latest)
            {
                throw new ShardCredentialException(ShardCredentialFailure.SecretUnavailable);
            }

            TokenCredential credential = create(auth.TenantId, auth.ClientId, secret);
            slot.Fingerprint = fingerprint.ToArray();
            slot.Credential = credential;
            slot.Latest = lookup;
            return credential;
        }
    }

    // Every field is read and written only under Gate.
    private sealed class Slot
    {
        public readonly Lock Gate = new();
        public byte[] Fingerprint = [];
        public TokenCredential? Credential;
        public long Latest;
    }
}
