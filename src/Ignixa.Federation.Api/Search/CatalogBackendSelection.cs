namespace Ignixa.Federation.Api.Search;

/// <summary>
/// Whether the host opts in to the proof-backed prototype catalog: only an explicit
/// <c>Federation:Backend:Mode=PrototypeCatalog</c> does. Nothing is ever inferred from the registered services.
/// </summary>
/// <remarks>
/// Absence of <c>Federation:Backend</c> means no selection. Once the key exists in any provider, even as a null, blank or
/// object value, it is an explicit selection and must say exactly <see cref="PrototypeCatalog"/>. Failures name the
/// configuration path and never repeat the configured value.
/// </remarks>
internal static class CatalogBackendSelection
{
    /// <summary>The configuration path of the mode.</summary>
    public const string ModePath = "Federation:Backend:Mode";

    /// <summary>The only mode value.</summary>
    public const string PrototypeCatalog = "PrototypeCatalog";

    private const string FederationSection = "Federation";
    private const string BackendKey = "Backend";
    private const string ModeKey = "Mode";

    /// <summary>Reads the selection.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns><see langword="true"/> for an explicit <see cref="PrototypeCatalog"/>; <see langword="false"/> when the backend is not configured at all.</returns>
    /// <exception cref="InvalidOperationException">The backend is configured but its mode is missing, blank, structured or unknown.</exception>
    public static bool IsSelected(IConfiguration configuration)
    {
        // GetChildren lists a key a provider holds even with a null value, which Exists() would call absent.
        IConfigurationSection? backend = configuration.GetSection(FederationSection).GetChildren()
            .FirstOrDefault(child => string.Equals(child.Key, BackendKey, StringComparison.OrdinalIgnoreCase));
        if (backend is null)
        {
            return false;
        }

        IConfigurationSection? mode = backend.GetChildren()
            .FirstOrDefault(child => string.Equals(child.Key, ModeKey, StringComparison.OrdinalIgnoreCase));
        return string.Equals(mode?.Value, PrototypeCatalog, StringComparison.Ordinal)
            ? true
            : throw new InvalidOperationException($"{ModePath} is missing or invalid.");
    }
}
