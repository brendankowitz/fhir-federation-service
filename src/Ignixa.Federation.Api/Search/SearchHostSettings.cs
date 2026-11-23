using Ignixa.Federation.Core.Identity;

namespace Ignixa.Federation.Api.Search;

/// <summary>
/// The trusted <c>Federation:Host</c> configuration of the public search surface: the public base every link is built
/// from, the one caller token issuer and audience, and the app-role grants.
/// </summary>
/// <remarks>
/// Links are built only from <see cref="PublicBase"/>, never from the request's <c>Host</c> or forwarding headers.
/// Failures name the configuration key and never repeat its value.
/// </remarks>
internal sealed class SearchHostSettings
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Federation:Host";

    private const int MaxPublicBaseLength = 2048;

    private SearchHostSettings(
        string publicBase,
        Uri authority,
        string issuer,
        string audience,
        IReadOnlyDictionary<string, IReadOnlyCollection<ResourceType>> roleGrants)
    {
        PublicBase = publicBase;
        Authority = authority;
        Issuer = issuer;
        Audience = audience;
        RoleGrants = roleGrants;
    }

    /// <summary>Gets the absolute HTTPS public base, without a trailing slash, query, fragment or user information.</summary>
    public string PublicBase { get; }

    /// <summary>Gets the trusted HTTPS discovery authority for caller tokens.</summary>
    public Uri Authority { get; }

    /// <summary>Gets the one trusted caller token issuer.</summary>
    public string Issuer { get; }

    /// <summary>Gets the one trusted caller token audience.</summary>
    public string Audience { get; }

    /// <summary>Gets the app role value to the resource types it may read.</summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<ResourceType>> RoleGrants { get; }

    /// <summary>Reads the section.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The settings, or <see langword="null"/> when the section is absent and search is not configured.</returns>
    /// <exception cref="InvalidOperationException">The section is present but a value is missing or invalid.</exception>
    public static SearchHostSettings? Read(IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection(SectionName);
        if (!section.Exists())
        {
            return null;
        }

        string publicBase = ReadPublicBase(section["PublicBaseUrl"]) ?? throw Invalid("PublicBaseUrl");
        Uri authority = Uri.TryCreate(section["Authority"], UriKind.Absolute, out Uri? parsed)
            && parsed.Scheme == Uri.UriSchemeHttps
            && parsed.UserInfo.Length == 0
            && parsed.Query.Length == 0
            && parsed.Fragment.Length == 0
                ? parsed
                : throw Invalid("Authority");
        string issuer = NonBlank(section["Issuer"]) ?? throw Invalid("Issuer");
        string audience = NonBlank(section["Audience"]) ?? throw Invalid("Audience");

        var grants = new Dictionary<string, IReadOnlyCollection<ResourceType>>(StringComparer.Ordinal);
        foreach (IConfigurationSection role in section.GetSection("Roles").GetChildren())
        {
            var types = new List<ResourceType>();
            foreach (IConfigurationSection type in role.GetChildren())
            {
                types.Add(ResourceType.TryParse(type.Value, out ResourceType? parsedType) ? parsedType : throw Invalid("Roles"));
            }

            if (types.Count == 0)
            {
                throw Invalid("Roles");
            }

            grants.Add(role.Key, types.AsReadOnly());
        }

        return new SearchHostSettings(publicBase, authority, issuer, audience, grants.AsReadOnly());
    }

    private static string? ReadPublicBase(string? value)
    {
        // The configured spelling is what links carry, so it is checked as text as well as parsed.
        if (value is null
            || value.Length > MaxPublicBaseLength
            || value.AsSpan().IndexOfAny('?', '#') >= 0
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0
            || !value.StartsWith("https://", StringComparison.Ordinal))
        {
            return null;
        }

        return value.TrimEnd('/');
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static InvalidOperationException Invalid(string key) =>
        new($"{SectionName}:{key} is missing or invalid.");
}
