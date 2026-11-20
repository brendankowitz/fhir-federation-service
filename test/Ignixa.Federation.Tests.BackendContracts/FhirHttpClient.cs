using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// A minimal FHIR R4 JSON client for the one local backend. It never follows redirects, never uses a
/// proxy, and reports failures as <see cref="BackendEvidenceException"/> with status codes only, so
/// no response body, endpoint, or credential reaches a test report.
/// </summary>
internal sealed class FhirHttpClient : IDisposable
{
    private const long MaxResponseBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;

    public FhirHttpClient(Uri baseAddress)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = RequestTimeout,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
    }

    public async Task<string?> GetSoftwareIdentityAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync("metadata", cancellationToken);
            return response.StatusCode == HttpStatusCode.OK
                ? ReadSoftwareIdentity(await response.Content.ReadAsStringAsync(cancellationToken))
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static string ReadSoftwareIdentity(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (Text(root, "resourceType") != "CapabilityStatement" ||
                !root.TryGetProperty("software", out JsonElement software))
            {
                throw new BackendEvidenceException(BackendFailureKind.Startup, "metadata did not return CapabilityStatement.software.");
            }

            if (Text(software, "name") is not { Length: > 0 and <= 80 } name ||
                !name.Any(char.IsAsciiLetter) ||
                !name.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '.'))
            {
                throw new BackendEvidenceException(BackendFailureKind.Startup, "metadata did not provide a reportable software.name.");
            }

            if (Text(software, "version") is not { Length: > 0 and <= 64 } version ||
                !char.IsAsciiDigit(version[0]) ||
                !version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-'))
            {
                throw new BackendEvidenceException(BackendFailureKind.Startup, "metadata did not provide a reportable software.version.");
            }

            return $"{name} {version}";
        }
        catch (JsonException)
        {
            throw new BackendEvidenceException(BackendFailureKind.Startup, "metadata did not return valid JSON.");
        }

        static string? Text(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>Writes a resource with a client-assigned id (update-as-create).</summary>
    public async Task PutAsync(SeedResource resource, CancellationToken cancellationToken)
    {
        using var content = new StringContent(resource.Json, System.Text.Encoding.UTF8, "application/fhir+json");
        HttpStatusCode status;
        try
        {
            using HttpResponseMessage response = await _http.PutAsync($"{resource.ResourceType}/{resource.Id}", content, cancellationToken);
            status = response.StatusCode;
        }
        catch (HttpRequestException e)
        {
            throw new BackendEvidenceException(BackendFailureKind.Startup, $"seed: writing a {resource.ResourceType} failed before a response ({e.GetType().Name}).");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BackendEvidenceException(BackendFailureKind.Startup, $"seed: writing a {resource.ResourceType} timed out.");
        }

        if (status is not (HttpStatusCode.OK or HttpStatusCode.Created))
        {
            throw new BackendEvidenceException(BackendFailureKind.Startup, $"seed: writing a {resource.ResourceType} returned HTTP {(int)status}.");
        }
    }

    /// <summary>
    /// Runs <c>GET Observation?{query}&amp;_count=100&amp;_total=accurate</c> with strict handling, so the
    /// backend must reject a search it cannot honor instead of silently ignoring a parameter.
    /// </summary>
    /// <param name="query">Already URL-encoded search parameters, without a leading <c>?</c>.</param>
    public async Task<SearchsetEvidence> SearchObservationsAsync(string query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"Observation?{query}&_count=100&_total=accurate");
        request.Headers.TryAddWithoutValidation("Prefer", "handling=strict");

        HttpStatusCode status;
        string body;
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new BackendEvidenceException(BackendFailureKind.IncompleteSearch, $"the search got no response ({e.GetType().Name}).");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BackendEvidenceException(BackendFailureKind.IncompleteSearch, $"the search timed out after {RequestTimeout.TotalSeconds:0} s.");
        }

        return status switch
        {
            HttpStatusCode.OK => SearchsetEvidence.Parse(body),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotImplemented =>
                throw new BackendEvidenceException(
                    BackendFailureKind.UnsupportedCapability,
                    $"the backend rejected the search with HTTP {(int)status}. This is a capability answer, not a match result."),
            _ => throw new BackendEvidenceException(
                BackendFailureKind.IncompleteSearch,
                $"the search returned unexpected HTTP {(int)status}, which is not evidence either way."),
        };
    }

    public void Dispose() => _http.Dispose();
}
