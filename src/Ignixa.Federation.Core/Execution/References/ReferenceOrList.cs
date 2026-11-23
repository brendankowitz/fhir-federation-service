using System.Diagnostics.CodeAnalysis;
using System.Text;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.References;

/// <summary>
/// Spells resource keys as one FHIR reference OR list for a queried shard: each key the shard owns in its admitted own-side
/// forms and each other key in its admitted foreign forms.
/// </summary>
/// <remarks>
/// The caller owns the evidence: it passes the forms the queried shard certified for the exact operation it performs, so one
/// rendering serves every operation that searches by reference. Rendering fails rather than guesses: a key that none of the
/// admitted forms spells, such as an own key certified only by link base aliases on an owner that has none, fails the whole
/// list, because dropping it would change the search's answer.
/// </remarks>
internal static class ReferenceOrList
{
    /// <summary>Tries to spell <paramref name="keys"/> as one OR list for <paramref name="queried"/>.</summary>
    /// <param name="keys">The keys, in the order their alternatives are emitted; at least one, each owned by a registry shard.</param>
    /// <param name="queried">The shard the list is sent to.</param>
    /// <param name="registry">The registry that owns every key and spells canonical URLs and link base aliases.</param>
    /// <param name="ownSide">The forms admitted for keys <paramref name="queried"/> owns.</param>
    /// <param name="foreign">The forms admitted for other keys; never <see cref="ReferenceSearchForms.Relative"/>.</param>
    /// <param name="orList">The FHIR-escaped alternatives joined by <c>,</c>, not yet percent-encoded.</param>
    /// <returns><see langword="false"/> when some key has no spelling in its admitted forms.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="keys"/> is empty or names a shard outside <paramref name="registry"/>, or <paramref name="foreign"/>
    /// admits a relative reference, which on <paramref name="queried"/> would name its own resource instead.
    /// </exception>
    public static bool TryRender(
        IReadOnlyCollection<ResourceKey> keys,
        ShardId queried,
        ShardRegistry registry,
        ReferenceSearchForms ownSide,
        ReferenceSearchForms foreign,
        [NotNullWhen(true)] out string? orList)
    {
        if (keys.Count == 0)
        {
            throw new ArgumentException("An OR list needs at least one key.", nameof(keys));
        }

        if (foreign.HasFlag(ReferenceSearchForms.Relative))
        {
            throw new ArgumentException("A foreign key is never spelled relative.", nameof(foreign));
        }

        orList = null;
        var values = new List<string>();
        foreach (ResourceKey key in keys)
        {
            if (!registry.TryGetById(key.Shard, out ShardDescriptor? owner))
            {
                throw new ArgumentException("A key is owned by a shard outside the registry.", nameof(keys));
            }

            int before = values.Count;
            ReferenceSearchForms forms = key.Shard == queried ? ownSide : foreign;
            string relative = $"{key.Type.Value}/{key.Id.Value}";
            if (forms.HasFlag(ReferenceSearchForms.Relative))
            {
                values.Add(Escape(relative));
            }

            if (forms.HasFlag(ReferenceSearchForms.CanonicalAbsolute))
            {
                values.Add(Escape(registry.FullUrl(key)));
            }

            if (forms.HasFlag(ReferenceSearchForms.LinkBaseAliases))
            {
                values.AddRange(owner.CanonicalLinkBaseAliases.Select(alias => Escape($"{alias}/{relative}")));
            }

            if (values.Count == before)
            {
                return false;
            }
        }

        orList = string.Join(',', values);
        return true;
    }

    /// <summary>
    /// Reads the alternative that starts at <paramref name="position"/> in a once-decoded OR list and undoes the FHIR escapes
    /// <see cref="TryRender"/> writes. Reading stops at the first malformed alternative, so a caller can refuse a list before
    /// reading the rest of it.
    /// </summary>
    /// <param name="orList">The decoded value.</param>
    /// <param name="position">
    /// Where the alternative starts, at most <c>orList.Length</c>; on success, where the next one starts, which is past the
    /// end after the last alternative.
    /// </param>
    /// <param name="alternative">The unescaped alternative.</param>
    /// <returns>
    /// <see langword="false"/> when the alternative is empty, holds an unescaped <c>|</c> or <c>$</c>, escapes any character
    /// but <c>, | $ \</c>, or ends in a lone backslash: nothing <see cref="TryRender"/> writes.
    /// </returns>
    public static bool TryReadAlternative(string orList, ref int position, [NotNullWhen(true)] out string? alternative)
    {
        alternative = null;
        var text = new StringBuilder();
        int end = position;
        for (; end < orList.Length && orList[end] != ','; end++)
        {
            char c = orList[end];
            if (c == '\\')
            {
                if (++end == orList.Length || orList[end] is not (',' or '|' or '$' or '\\'))
                {
                    return false;
                }

                c = orList[end];
            }
            else if (c is '|' or '$')
            {
                return false;
            }

            text.Append(c);
        }

        if (text.Length == 0)
        {
            return false;
        }

        position = end + 1;
        alternative = text.ToString();
        return true;
    }

    /// <summary>FHIR-escapes one OR alternative, so its own <c>, | $ \</c> are not read as search syntax.</summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal);
}
