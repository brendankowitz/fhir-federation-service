using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Api.Search;

/// <summary>The reverse includes for one emitted page.</summary>
/// <param name="Includes">Distinct eligible entries in resolver order, excluding resources already emitted as matches.</param>
/// <param name="Truncated">Whether a further distinct eligible entry was observed beyond the include limit, not merely whether the limit was reached.</param>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "The host composition supplies the optional resolver.")]
public sealed record RevIncludePageResult(IReadOnlyList<ShardEntry> Includes, bool Truncated);
