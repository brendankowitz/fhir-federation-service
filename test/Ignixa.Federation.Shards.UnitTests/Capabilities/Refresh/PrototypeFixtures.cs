namespace Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;

/// <summary>The reduced pinned metadata controls, embedded from <c>Capabilities/Fixtures/Prototype</c>.</summary>
internal static class PrototypeFixtures
{
    /// <summary>The pinned Ignixa advertisement.</summary>
    public static string East => Load("east-metadata.json");

    /// <summary>The pinned Microsoft FHIR Server advertisement.</summary>
    public static string West => Load("west-metadata.json");

    private static string Load(string name)
    {
        using Stream stream = typeof(PrototypeFixtures).Assembly.GetManifestResourceStream("Prototype." + name)
            ?? throw new InvalidOperationException($"The fixture {name} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
