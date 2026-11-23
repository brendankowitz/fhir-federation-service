namespace Ignixa.Federation.Api.Search;

/// <summary>
/// Whether the host opts in to the proof-backed prototype catalog: only an explicit
/// <c>Federation:Backend:Mode=PrototypeCatalog</c> does. Nothing is ever inferred from the registered services.
/// </summary>
/// <remarks>
/// Only the presence of the mode key makes a selection explicit, including a null, blank or structured value.
/// Failures name the configuration path and never repeat the configured value.
/// </remarks>
internal static class CatalogBackendSelection
{
    /// <summary>The configuration path of the mode.</summary>
    public const string ModePath = BackendPath + ":" + ModeKey;

    /// <summary>The only mode value.</summary>
    public const string PrototypeCatalog = "PrototypeCatalog";

    private const string BackendPath = "Federation:Backend";
    private const string ModeKey = "Mode";

    /// <summary>Reads the selection.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns><see langword="true"/> for an explicit <see cref="PrototypeCatalog"/>; <see langword="false"/> when the mode key is absent.</returns>
    /// <exception cref="InvalidOperationException">The mode is explicit but not recognized.</exception>
    public static bool IsSelected(IConfiguration configuration)
    {
        // GetChildren lists a key a provider holds even with a null value, which Exists() would call absent.
        IConfigurationSection? mode = configuration.GetSection(BackendPath).GetChildren()
            .FirstOrDefault(child => string.Equals(child.Key, ModeKey, StringComparison.OrdinalIgnoreCase));
        if (mode is null)
        {
            return false;
        }

        return string.Equals(mode.Value, PrototypeCatalog, StringComparison.Ordinal)
            ? true
            : throw new InvalidOperationException($"{ModePath} is invalid.");
    }
}
