namespace Ignixa.Federation.Core.Identity;

/// <summary>FHIR extension URLs that the federation defines (design spec §11).</summary>
public static class FederationExtensions
{
    /// <summary>
    /// The URL of the <c>source-shard</c> entry extension, whose <c>valueString</c> is the shard alias (design spec §11).
    /// A stable identifier; it need not resolve.
    /// </summary>
    public const string SourceShardUrl = "http://ignixa.io/federation/StructureDefinition/source-shard";
}
