using System.Diagnostics.CodeAnalysis;
using System.Net;
using Ignixa.Federation.Api.Continuations;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Api.Search;

/// <summary>Reconstructs frozen reverse-chain owner requests and checks current transport fit without consulting a shard.</summary>
internal static class ReverseChainReplay
{
    public static bool TryRebuild(ShardRegistry registry, QueryPlan plan, UnsortedContinuationSnapshot snapshot, int maxKeys,
        Func<ShardSearchRequest, bool> fits, [NotNullWhen(true)] out ShardSearchRequest[]? rebuilt)
    {
        rebuilt = null;
        PlannedOccurrence[] outbound = [.. plan.Occurrences.Where(o => o.Action != OccurrenceAction.Remove)];
        var streams = new List<ShardSearchRequest>(snapshot.Streams.Count);
        int previousOrdinal = -1;
        int remainingKeys = maxKeys;
        foreach (ShardSearchRequest saved in snapshot.Streams)
        {
            if (!registry.TryGetById(saved.Shard, out ShardDescriptor? owner) || owner.Ordinal <= previousOrdinal
                || saved.Parameters.Count != outbound.Length)
            {
                return false;
            }

            previousOrdinal = owner.Ordinal;
            var replacements = new Dictionary<int, string>();
            string? ownerIds = null;
            for (int i = 0; i < outbound.Length; i++)
            {
                if (outbound[i].Action != OccurrenceAction.Replace)
                {
                    continue;
                }

                // Recover a value only at an original ordinal classified as a rewrite. A kept _id is not a witness.
                string decoded = WebUtility.UrlDecode(saved.Parameters[i].Value);
                if (ownerIds is null)
                {
                    string[] ids = decoded.Split(',');
                    if (ids.Length > remainingKeys || ids.Any(id => !ResourceId.TryParse(id, out _))
                        || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                    {
                        return false;
                    }

                    remainingKeys -= ids.Length;
                    ownerIds = decoded;
                }
                else if (decoded != ownerIds)
                {
                    return false;
                }

                replacements.Add(outbound[i].Occurrence.Ordinal, decoded);
            }

            IReadOnlyList<ParameterOccurrence> parameters = plan.BuildBackendParameters(replacements);
            ShardSearchMethod method = OwnerMethod(owner, parameters);
            int sendCount = Math.Min(snapshot.PageSize, owner.Profile.MaxCount);
            if (!fits(new ShardSearchRequest(saved.Shard, plan.Type, parameters, sendCount, method)))
            {
                return false;
            }

            if (saved.Shard == snapshot.Continuation.Shard
                && ((snapshot.Continuation.Search is { } sent && (sent.PageSize > owner.Profile.MaxCount || !fits(sent)))
                    || (snapshot.Continuation.Cursor is { } cursor && cursor.Redemption != owner.Profile.RedemptionFor(method))))
            {
                return false;
            }

            streams.Add(new ShardSearchRequest(saved.Shard, plan.Type, parameters, snapshot.PageSize, method));
        }

        rebuilt = [.. streams];
        return true;
    }

    private static ShardSearchMethod OwnerMethod(ShardDescriptor owner, IReadOnlyList<ParameterOccurrence> parameters)
    {
        // Same contract as ReverseChainTarget: a POST self link may collapse form-decoded duplicate pairs.
        var seen = new HashSet<(string Key, string Value)>();
        return owner.Profile.SupportsPostSearch
            && parameters.All(p => seen.Add((WebUtility.UrlDecode(p.Key), WebUtility.UrlDecode(p.Value))))
            ? ShardSearchMethod.PostSearch
            : ShardSearchMethod.Get;
    }
}
