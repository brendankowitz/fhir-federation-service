using System.Collections.Frozen;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;

namespace Ignixa.Federation.Api.Search;

/// <summary>One admitted view and the evidence that admitted it, as produced by an <see cref="ICapabilityAdmissionSource"/>.</summary>
/// <param name="capabilities">The complete view to serve.</param>
/// <param name="readableResourceTypes">
/// The types with proven owner-qualified reads, possibly none. Copied at construction; the read serves only types that are
/// also searchable in <paramref name="capabilities"/>.
/// </param>
/// <param name="isCurrent">
/// Whether the evidence still holds. Evaluated once per request capture, so it must be pure, thread-safe, synchronous,
/// non-throwing and free of I/O. Once false for an admission it should stay false; recovery is a fresh admission.
/// </param>
/// <param name="tryCommitIfCurrent">
/// Under the source's evidence gate, invokes the given action exactly once and returns <see langword="true"/> if this
/// admission is still current; otherwise returns <see langword="false"/> without invoking it. Evidence cannot be
/// replaced while the action runs. The action is a short, synchronous, non-throwing state swap that does no I/O and does
/// not re-enter the source.
/// </param>
/// <remarks>Immutable and thread-safe.</remarks>
internal sealed class CapabilityAdmission(
    SearchCapabilities capabilities,
    IEnumerable<ResourceType> readableResourceTypes,
    Func<bool> isCurrent,
    Func<Action, bool> tryCommitIfCurrent)
{
    /// <summary>Gets the complete view to serve.</summary>
    public SearchCapabilities Capabilities { get; } = capabilities;

    /// <summary>Gets the types with proven owner-qualified reads, frozen at construction.</summary>
    public IReadOnlySet<ResourceType> ReadableResourceTypes { get; } = readableResourceTypes.ToFrozenSet();

    /// <summary>Gets the evidence predicate; see the constructor for its contract.</summary>
    public Func<bool> IsCurrent { get; } = isCurrent;

    /// <summary>Gets the atomic commit; see the constructor for its contract.</summary>
    public Func<Action, bool> TryCommitIfCurrent { get; } = tryCommitIfCurrent;
}
