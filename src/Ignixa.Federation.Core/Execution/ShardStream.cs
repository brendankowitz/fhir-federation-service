using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution;

/// <summary>One shard's stream in a federated search: the caller's request template and the shard it targets.</summary>
internal sealed class ShardStream
{
    private readonly ShardSearchRequest _template;
    private readonly ShardDescriptor _descriptor;

    public ShardStream(ShardSearchRequest template, ShardDescriptor descriptor)
    {
        _template = template;
        _descriptor = descriptor;
        Redemption = descriptor.Profile.RedemptionFor(template.Method);
    }

    public ShardId Shard => _template.Shard;

    public int Ordinal => _descriptor.Ordinal;

    /// <summary>Gets how every cursor of this stream must be redeemed.</summary>
    public NextLinkRedemption Redemption { get; }

    /// <summary>Builds the stream's first request, asking for what the page still needs within the backend's maximum.</summary>
    public ShardSearchRequest FirstPage(int remaining) =>
        new(_template.Shard, _template.Type, _template.Parameters, Math.Min(remaining, _descriptor.Profile.MaxCount), _template.Method);

    /// <summary>Gets whether <paramref name="resume"/> can have been issued for this stream.</summary>
    public bool Accepts(UnsortedContinuation resume)
    {
        if (resume.Search is { } search)
        {
            return search.Shard == Shard && search.Type == _template.Type && search.Method == _template.Method;
        }

        return resume.Cursor is not { } cursor || Owns(cursor);
    }

    /// <summary>
    /// Validates a page the federation needs, before any of it is emitted or searched for an anchor: every key has this
    /// stream's shard and type and appears once, and a successor cursor belongs to this stream and was not redeemed in
    /// this execution.
    /// </summary>
    /// <exception cref="ShardFailureException">The page breaks the shard contract.</exception>
    public void Validate(ShardPage page, IReadOnlySet<ShardCursor> redeemed)
    {
        var seen = new HashSet<ResourceKey>();
        foreach (ShardEntry entry in page.Entries)
        {
            if (entry.Key.Shard != Shard || entry.Key.Type != _template.Type || !seen.Add(entry.Key))
            {
                throw new ShardFailureException(Shard, ShardFailureReason.ContractViolation, null);
            }
        }

        if (page.Next is { } next && (!Owns(next) || redeemed.Contains(next)))
        {
            throw new ShardFailureException(Shard, ShardFailureReason.ContractViolation, null);
        }
    }

    private bool Owns(ShardCursor cursor) =>
        cursor.Shard == Shard && cursor.Type == _template.Type && cursor.Redemption == Redemption;
}
