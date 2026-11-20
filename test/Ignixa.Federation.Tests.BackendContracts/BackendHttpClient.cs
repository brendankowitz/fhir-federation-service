using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// A minimal FHIR R4 JSON client for the one local backend. It never follows redirects, never uses a
/// proxy, and reports failures as <see cref="BackendEvidenceException"/> with status codes only, so
/// no response body, endpoint, or credential reaches a test report.
/// </summary>
internal sealed class BackendHttpClient : IDisposable
{
    private const long MaxResponseBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private const string SeededPatientId = "count-female-1";
    private const string PlainIdSpelling = "_id=count-female-1";
    private const string EncodedIdSpelling = "%5Fid=%63ount-female-1";

    private readonly HttpClient _http;

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "HttpClient owns and disposes its handler.")]
    public BackendHttpClient(Uri baseAddress)
    {
        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            CheckCertificateRevocationList = true,
        })
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
            using HttpResponseMessage response = await _http.GetAsync(new Uri("metadata", UriKind.Relative), cancellationToken);
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
            using HttpResponseMessage response = await _http.PutAsync(new Uri($"{resource.ResourceType}/{resource.Id}", UriKind.Relative), content, cancellationToken);
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
    public Task<SearchsetEvidence> SearchObservationsAsync(string query, CancellationToken cancellationToken) =>
        SearchAsync("Observation", query, cancellationToken);

    /// <summary>
    /// Runs <c>GET {resourceType}?{query}&amp;_count=100&amp;_total=accurate</c> with strict handling and
    /// accounts for every match of that type on the single complete page.
    /// </summary>
    /// <param name="resourceType">A reserved resource type name.</param>
    /// <param name="query">Already URL-encoded search parameters, without a leading <c>?</c>.</param>
    public async Task<SearchsetEvidence> SearchAsync(string resourceType, string query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{resourceType}?{query}&_count=100&_total=accurate");
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
            HttpStatusCode.OK => SearchsetEvidence.Parse(body, resourceType),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotImplemented =>
                throw new BackendEvidenceException(
                    BackendFailureKind.UnsupportedCapability,
                    $"the backend rejected the search with HTTP {(int)status}. This is a capability answer, not a match result."),
            _ => throw new BackendEvidenceException(
                BackendFailureKind.IncompleteSearch,
                $"the search returned unexpected HTTP {(int)status}, which is not evidence either way."),
        };
    }

    /// <summary>
    /// Builds the ordinary count request for Patients: <c>_count=0</c> and an optional <c>gender</c>
    /// filter, as a query string for GET or a form body for <c>POST Patient/_search</c>. It carries no
    /// <c>_total</c>, so the count is the server's answer to <c>_count=0</c> alone, and it asks for
    /// strict handling so an unsupported parameter is rejected and not ignored.
    /// </summary>
    /// <param name="method">GET or POST.</param>
    /// <param name="gender">A reserved gender code, or <see langword="null"/> for no filter.</param>
    internal static HttpRequestMessage BuildCountRequest(HttpMethod method, string? gender)
    {
        List<KeyValuePair<string, string>> parameters = [new("_count", "0")];
        if (gender is not null)
        {
            parameters.Add(new("gender", gender));
        }

        HttpRequestMessage request;
        if (method == HttpMethod.Get)
        {
            string query = string.Join('&', parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
            request = new HttpRequestMessage(HttpMethod.Get, $"Patient?{query}");
        }
        else if (method == HttpMethod.Post)
        {
            request = new HttpRequestMessage(HttpMethod.Post, "Patient/_search") { Content = new FormUrlEncodedContent(parameters) };
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(method), "Only GET and POST have a count request shape.");
        }

        request.Headers.TryAddWithoutValidation("Prefer", "handling=strict");
        return request;
    }

    /// <summary>
    /// Sends <see cref="BuildCountRequest"/> and accounts for the response as a count. Failures name only
    /// the method, the HTTP status and fixed text, never a response body or a server link.
    /// </summary>
    public async Task<CountEvidence> CountPatientsAsync(HttpMethod method, string? gender, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = BuildCountRequest(method, gender);
        return await SendForEvidenceAsync(request, $"the {method.Method} count", "a count", CountEvidence.Parse, cancellationToken);
    }

    /// <summary>
    /// Builds the repeated decoded <c>_id</c> request for the seeded Patient: <c>_id=count-female-1</c> and a second
    /// occurrence spelled <c>%5Fid=%63ount-female-1</c>, which decodes to the same name and the same value. The
    /// literal escapes are sent as written: in the GET target, and in the body of <c>POST Patient/_search</c>, which
    /// is not re-encoded. <see cref="Uri"/> would otherwise unescape these unreserved characters and turn the
    /// spelling pair into two plain occurrences, so the GET target is built with canonicalization disabled. That
    /// is why the request is absolute. The primary shape asks for a single page (<c>_count=100</c>) with an
    /// accurate total; the count shape is the ordinary <c>_count=0</c> with no <c>_total</c>. Both ask for strict
    /// handling.
    /// </summary>
    /// <param name="method">GET or POST.</param>
    /// <param name="shape">The primary page or the ordinary count.</param>
    /// <param name="baseAddress">The backend base address, ending in a slash.</param>
    internal static HttpRequestMessage BuildRepeatedIdRequest(HttpMethod method, RepeatedIdShape shape, Uri baseAddress)
    {
        string controls = shape == RepeatedIdShape.Primary ? "_count=100&_total=accurate" : "_count=0";
        return BuildLiteralPatientRequest(method, $"{PlainIdSpelling}&{EncodedIdSpelling}&{controls}", baseAddress);
    }

    /// <summary>
    /// Builds a Patient.given request whose selection text is sent exactly as written, followed by
    /// <c>_count=100&amp;_total=accurate</c>: in the GET target, and in the body of <c>POST Patient/_search</c>.
    /// Escapes such as <c>%2C</c> and <c>%3A</c> reach the wire literally in both. Strict handling is requested.
    /// </summary>
    /// <param name="method">GET or POST.</param>
    /// <param name="given">The case whose <see cref="GivenCase.WireQuery"/> is sent.</param>
    /// <param name="baseAddress">The backend base address, ending in a slash.</param>
    internal static HttpRequestMessage BuildGivenRequest(HttpMethod method, GivenCase given, Uri baseAddress) =>
        BuildLiteralPatientRequest(method, $"{given.WireQuery}&_count=100&_total=accurate", baseAddress);

    private static HttpRequestMessage BuildLiteralPatientRequest(HttpMethod method, string query, Uri baseAddress)
    {
        HttpRequestMessage request;
        if (method == HttpMethod.Get)
        {
            var target = new Uri($"{baseAddress.AbsoluteUri}Patient?{query}", new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
            request = new HttpRequestMessage(HttpMethod.Get, target);
        }
        else if (method == HttpMethod.Post)
        {
            request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, "Patient/_search"))
            {
                Content = new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes(query))
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded") },
                },
            };
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(method), "Only GET and POST have a literal Patient request shape.");
        }

        request.Headers.TryAddWithoutValidation("Prefer", "handling=strict");
        return request;
    }

    /// <summary>
    /// Sends <see cref="BuildGivenRequest"/> and accounts for the response. Failures name only the method, the
    /// case label, the HTTP status and fixed text, never a response body, a server link or the request.
    /// </summary>
    public async Task<GivenEvidence> SearchGivenAsync(HttpMethod method, GivenCase given, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = BuildGivenRequest(method, given, _http.BaseAddress!);
        return await SendForEvidenceAsync(
            request,
            $"the {method.Method} {given.Label} Patient.given search",
            "a result",
            GivenEvidence.Parse,
            cancellationToken);
    }

    /// <summary>
    /// Sends <see cref="BuildRepeatedIdRequest"/> and accounts for the response. Failures name only the method,
    /// the shape, the HTTP status and fixed text, never a response body, a server link or the request.
    /// </summary>
    public async Task<RepeatedIdEvidence> SearchRepeatedIdAsync(HttpMethod method, RepeatedIdShape shape, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = BuildRepeatedIdRequest(method, shape, _http.BaseAddress!);
        return await SendForEvidenceAsync(
            request,
            $"the {method.Method} {(shape == RepeatedIdShape.Primary ? "primary" : "count")} repeated-_id search",
            "a result",
            body => RepeatedIdEvidence.Parse(body, shape, SeededPatientId),
            cancellationToken);
    }

    private async Task<T> SendForEvidenceAsync<T>(
        HttpRequestMessage request,
        string operation,
        string answer,
        Func<string, T> parse,
        CancellationToken cancellationToken)
    {
        HttpStatusCode status;
        string body;
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new BackendEvidenceException(BackendFailureKind.IncompleteSearch, $"{operation} got no response.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BackendEvidenceException(BackendFailureKind.IncompleteSearch, $"{operation} timed out after {RequestTimeout.TotalSeconds:0} s.");
        }

        switch (status)
        {
            case HttpStatusCode.OK:
                try
                {
                    return parse(body);
                }
                catch (BackendEvidenceException e)
                {
                    throw new BackendEvidenceException(e.Kind, $"{operation} response is not evidence: {e.Detail}.");
                }

            case HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotImplemented:
                throw new BackendEvidenceException(
                    BackendFailureKind.UnsupportedCapability,
                    $"the backend rejected {operation} with HTTP {(int)status}. This is a capability answer, not {answer}.");

            default:
                throw new BackendEvidenceException(
                    BackendFailureKind.IncompleteSearch,
                    $"{operation} returned unexpected HTTP {(int)status}, which is not evidence either way.");
        }
    }

    public void Dispose() => _http.Dispose();
}
