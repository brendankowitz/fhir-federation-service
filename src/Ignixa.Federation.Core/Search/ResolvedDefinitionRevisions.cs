using System.Security.Cryptography;
using System.Text;
using Ignixa.Federation.Core.Identity;

namespace Ignixa.Federation.Core.Search;

/// <summary>Exposes continuation revisions for the packaged definitions resolved by a search capability view.</summary>
public static class ResolvedDefinitionRevisions
{
    /// <summary>
    /// Returns an ordinal revision for an admitted definition, independently of whether it is advertised in
    /// <see cref="SearchCapabilities.Resources"/>. This lookup does not permit any particular use of the definition.
    /// </summary>
    /// <param name="capabilities">The immutable view used by the planner.</param>
    /// <param name="type">The searched resource type.</param>
    /// <param name="code">The resolved search parameter code, without a modifier.</param>
    /// <returns>The revision, or <see langword="null"/> when the view has no common definition for the code.</returns>
    public static string? GetResolvedDefinitionRevision(this SearchCapabilities capabilities, ResourceType type, string code)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(code);
        if (capabilities.FindParameter(type, code) is not { } definition)
        {
            return null;
        }

        // The packaged identity fields cannot contain NUL; the expression is last and is hashed verbatim.
        string value = string.Join('\0', "Ignixa.Federation.Search.Definition.v1", type.Value, definition.Code,
            definition.Type.ToString(), definition.Url.OriginalString, definition.Expression);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
