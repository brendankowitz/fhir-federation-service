using System.Text.Json;
using Ignixa.Federation.Api.Reads;
using Ignixa.Federation.Api.Search;
using Ignixa.Federation.Core.Search;
using Ignixa.Specification.ValueSets.Normative;

namespace Ignixa.Federation.Api.Metadata;

internal static class MetadataEndpoints
{
    public static void MapFederatedMetadata(this WebApplication app)
    {
        if (app.Services.GetService<SearchHostSettings>() is null)
        {
            app.MapGet("/metadata", () => SearchOutcome.Unavailable("Federation capabilities are not configured on this host."));
            return;
        }

        PrimarySearchState state = app.Services.GetRequiredService<PrimarySearchState>();
        app.MapGet("/metadata", (HttpRequest request) =>
        {
            // Capture the view once; with none, refuse before looking at the request.
            if (state.Current is not { } ready)
            {
                return SearchOutcome.Unavailable(PrimarySearchState.NotReady);
            }

            if (request.Query.TryGetValue("_format", out var format))
            {
                if (format.Count != 1)
                {
                    return SearchOutcome.Invalid("Supply at most one _format value.");
                }

                // Query decoding turns an unescaped '+' into a space, as on the search routes.
                if (format[0] is not ("json" or "application/json" or "application/fhir+json" or "application/fhir json"))
                {
                    return SearchOutcome.NotSupported(StatusCodes.Status415UnsupportedMediaType, "Only JSON is supported.");
                }
            }

            return Results.Bytes(ready.Statement, "application/fhir+json; charset=utf-8");
        });
    }

    /// <summary>Writes the CapabilityStatement for a view.</summary>
    /// <param name="publicBase">The public base URL the statement names.</param>
    /// <param name="publishedAt">The statement's date.</param>
    /// <param name="resources">The resources the view supports.</param>
    /// <param name="proxiedReads">The shared eligibility; a resource is given the <c>read</c> interaction exactly when it serves the type.</param>
    /// <param name="executesReverseIncludes">Whether the prepared search executes the view's admitted reverse includes.</param>
    /// <returns>The serialized statement.</returns>
    internal static byte[] WriteStatement(
        string publicBase,
        DateTimeOffset publishedAt,
        IReadOnlyList<ResourceSearchSupport> resources,
        ProxiedReads proxiedReads,
        bool executesReverseIncludes)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("resourceType", "CapabilityStatement");
            json.WriteString("status", "active");
            json.WriteString("date", publishedAt);
            json.WriteString("kind", "instance");
            json.WriteStartObject("implementation");
            json.WriteString("description", "Ignixa Federation");
            json.WriteString("url", publicBase);
            json.WriteEndObject();
            json.WriteString("fhirVersion", "4.0.1");
            json.WriteStartArray("format");
            json.WriteStringValue("json");
            json.WriteEndArray();
            json.WriteStartArray("rest");
            json.WriteStartObject();
            json.WriteString("mode", "server");
            if (resources.Count != 0)
            {
                json.WriteStartArray("resource");
                foreach (ResourceSearchSupport resource in resources)
                {
                    WriteResource(json, resource, proxiedReads, executesReverseIncludes);
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteResource(
        Utf8JsonWriter json, ResourceSearchSupport resource, ProxiedReads proxiedReads, bool executesReverseIncludes)
    {
        json.WriteStartObject();
        json.WriteString("type", resource.Type.Value);
        // The planner can admit features that the current host does not execute.
        json.WriteStartArray("interaction");
        if (proxiedReads.Serves(resource.Type))
        {
            json.WriteStartObject();
            json.WriteString("code", "read");
            json.WriteEndObject();
        }

        json.WriteStartObject();
        json.WriteString("code", "search-type");
        json.WriteEndObject();
        json.WriteEndArray();

        if (executesReverseIncludes && resource.SearchRevIncludes.Count != 0)
        {
            json.WriteStartArray("searchRevInclude");
            foreach (string value in resource.SearchRevIncludes)
            {
                json.WriteStringValue(value);
            }

            json.WriteEndArray();
        }

        if (resource.Parameters.Count != 0)
        {
            json.WriteStartArray("searchParam");
            foreach (SearchParameterSupport parameter in resource.Parameters)
            {
                json.WriteStartObject();
                json.WriteString("name", parameter.Code);
                json.WriteString("definition", parameter.Definition);
                json.WriteString("type", ParameterTypeCode(parameter.Type));
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
    }

    private static string ParameterTypeCode(SearchParamType type) => type switch
    {
        SearchParamType.Number => "number",
        SearchParamType.Date => "date",
        SearchParamType.String => "string",
        SearchParamType.Token => "token",
        SearchParamType.Reference => "reference",
        SearchParamType.Composite => "composite",
        SearchParamType.Quantity => "quantity",
        SearchParamType.Uri => "uri",
        SearchParamType.Special => "special",
        _ => throw new InvalidOperationException("The published search parameter has no R4 type code."),
    };
}
