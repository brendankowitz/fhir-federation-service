using System.Text.Json;
using Ignixa.Federation.Api.Reads;
using Ignixa.Federation.Api.Search;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Tests.Integration.Authentication;
using Ignixa.Federation.Tests.Integration.Search;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Reads;

/// <summary>
/// The proxied read, its <c>read</c> capability and the search <c>alternate</c> links are offered for exactly the resource types
/// the published view searches. Eligibility never replaces authorisation: a caller who may read an unsupported type gets the
/// ordinary not-found, and the binding is not asked.
/// </summary>
public sealed class ProxiedReadEligibilityTests
{
    private const string EastObservation = "/shards/k7q2/Observation/obs-dup";
    private const string EastPatient = "/shards/k7q2/Patient/dup";

    private static SearchCapabilities PatientOnly() => SearchWorld.Capabilities(observation: false);

    private static SearchCapabilities PatientAndObservation() => SearchWorld.Capabilities();

    private static SearchHost Start(ScriptedReadBinding binding, SearchCapabilities view) =>
        ProxiedReadTests.Start(binding, setup => setup.Capabilities = view);

    [Fact]
    public async Task BroadlyAuthorizedCaller_ReadingAnUnsupportedSeededType_GetsTheStandaloneNotFound_WithoutBindingIo()
    {
        var binding = new ScriptedReadBinding();
        using SearchHost host = Start(binding, PatientOnly());
        SearchWorld.Slice("east", "Observation", "obs-dup").ShouldNotBeEmpty();

        ReadResponse unsupported = await ReadResponse.GetAsync(host, EastObservation, host.Token());
        ReadResponse missing = await ReadResponse.GetAsync(host, "/shards/k7q2/Patient/missing", host.Token());

        unsupported.Status.ShouldBe(404);
        unsupported.IssueCode.ShouldBe("not-found");
        unsupported.IsCleanFailure.ShouldBeTrue();
        unsupported.Body.ShouldBe(missing.Body);
        binding.Calls.Count.ShouldBe(1);
        binding.Calls[0].Key.Type.Value.ShouldBe("Patient");
    }

    [Fact]
    public async Task SupportedType_StillReadsTheExactBytes_AndTheStatementAndSearchOfferOnlyThatType()
    {
        var binding = new ScriptedReadBinding();
        using SearchHost host = Start(binding, PatientOnly());

        ReadResponse read = await ReadResponse.GetAsync(host, EastPatient, host.Token());
        JsonElement statement = JsonElement.Parse((await ReadResponse.GetAsync(host, "/metadata", token: null)).Body);
        SearchResult search = await host.GetAsync("/Patient?name=Orchid");

        read.Status.ShouldBe(200);
        read.Bytes.ShouldBe(SearchWorld.Slice("east", "Patient", "dup"));
        read.ContentLocation.ShouldBe(SearchWorld.EastDupUrl);
        ReadInteractions(statement).ShouldBe([("Patient", true)]);
        search.Json.GetProperty("entry").EnumerateArray()
            .ShouldAllBe(e => e.GetProperty("link").EnumerateArray().Any(l => l.GetProperty("relation").GetString() == "alternate"));
    }

    [Fact]
    public async Task EachCapabilityStatementReadInteraction_IsServedAndNoOtherTypeIs()
    {
        var binding = new ScriptedReadBinding();
        using SearchHost host = Start(binding, PatientAndObservation());

        JsonElement statement = JsonElement.Parse((await ReadResponse.GetAsync(host, "/metadata", token: null)).Body);

        ReadInteractions(statement).ShouldBe([("Observation", true), ("Patient", true)], ignoreOrder: true);
        (await ReadResponse.GetAsync(host, EastObservation, host.Token())).Status.ShouldBe(200);
        (await ReadResponse.GetAsync(host, EastPatient, host.Token())).Status.ShouldBe(200);
        binding.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ReplacingAndWithdrawingTheView_ChangesWhichTypesAreReadableAndAdvertised()
    {
        var binding = new ScriptedReadBinding();
        using SearchHost host = Start(binding, PatientOnly());
        IFederationCapabilityPublisher publisher = host.Services.GetRequiredService<IFederationCapabilityPublisher>();
        (await ReadResponse.GetAsync(host, EastObservation, host.Token())).Status.ShouldBe(404);
        binding.Calls.ShouldBeEmpty();

        publisher.Publish(PatientAndObservation());
        ReadResponse admitted = await ReadResponse.GetAsync(host, EastObservation, host.Token());
        admitted.Status.ShouldBe(200);
        admitted.Bytes.ShouldBe(SearchWorld.Slice("east", "Observation", "obs-dup"));
        admitted.ContentLocation.ShouldBe("https://fhir-east.example.org/Observation/obs-dup");
        ReadInteractions(JsonElement.Parse((await ReadResponse.GetAsync(host, "/metadata", token: null)).Body))
            .ShouldBe([("Observation", true), ("Patient", true)], ignoreOrder: true);
        binding.Calls.Count.ShouldBe(1);

        publisher.Publish(PatientOnly());
        (await ReadResponse.GetAsync(host, EastObservation, host.Token())).Status.ShouldBe(404);
        ReadInteractions(JsonElement.Parse((await ReadResponse.GetAsync(host, "/metadata", token: null)).Body)).ShouldBe([("Patient", true)]);
        binding.Calls.Count.ShouldBe(1);

        publisher.Publish(null);
        (await ReadResponse.GetAsync(host, EastObservation, host.Token())).Status.ShouldBe(503);
        (await ReadResponse.GetAsync(host, EastPatient, host.Token())).Status.ShouldBe(503);
        binding.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AuthenticationAndAuthorisation_OutrankEligibility_ForAnUnsupportedType()
    {
        var binding = new ScriptedReadBinding();
        using SearchHost host = Start(binding, PatientOnly());
        string untrusted = host.Token(new SyntheticTokenRequest { Roles = [SearchWorld.ReaderRole], UntrustedSigner = true });
        string scoped = host.Token(new SyntheticTokenRequest { Roles = [SearchWorld.ReaderRole], Scp = "patient/*.read" });
        string noRoles = host.Token(new SyntheticTokenRequest());
        string observationOnly = host.Token(new SyntheticTokenRequest { Roles = [SearchWorld.ObservationRole] });

        (await ReadResponse.GetAsync(host, EastObservation, token: null)).Status.ShouldBe(401);
        (await ReadResponse.GetAsync(host, EastObservation, untrusted)).Status.ShouldBe(401);
        (await ReadResponse.GetAsync(host, EastObservation, scoped)).Status.ShouldBe(403);
        (await ReadResponse.GetAsync(host, EastObservation, noRoles)).Status.ShouldBe(403);
        // Granted Observation but not Encounter: the ungranted unsupported type is forbidden, the granted one is not found.
        (await ReadResponse.GetAsync(host, "/shards/k7q2/Encounter/e1", observationOnly)).Status.ShouldBe(403);
        (await ReadResponse.GetAsync(host, EastObservation, observationOnly)).Status.ShouldBe(404);
        binding.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnsupportedType_IsNotFoundBeforeTheQueryAliasAndIdAreExamined_LikeADisabledProxy()
    {
        var binding = new ScriptedReadBinding();
        using SearchHost host = Start(binding, PatientOnly());

        string[] paths =
        [
            EastObservation + "?_elements=id",
            "/shards/zzzz/Observation/obs-dup",
            "/shards/k7q2/Observation/" + new string('a', 65),
        ];
        foreach (string path in paths)
        {
            ReadResponse response = await ReadResponse.GetAsync(host, path, host.Token());
            response.Status.ShouldBe(404, path);
            response.IssueCode.ShouldBe("not-found");
        }

        (await ReadResponse.GetAsync(host, EastPatient + "?_elements=id", host.Token())).Status.ShouldBe(400);
        binding.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task DisabledProxy_StaysDisabledForEverySupportedType()
    {
        using SearchHost host = ProxiedReadTests.Start(binding: null, setup => setup.Capabilities = PatientAndObservation());

        JsonElement statement = JsonElement.Parse((await ReadResponse.GetAsync(host, "/metadata", token: null)).Body);

        ReadInteractions(statement).ShouldBe([("Observation", false), ("Patient", false)], ignoreOrder: true);
        (await ReadResponse.GetAsync(host, EastObservation, host.Token())).Status.ShouldBe(404);
        (await host.GetAsync("/Patient?name=Orchid")).Body.ShouldNotContain("alternate");
    }

    private static (string Type, bool Read)[] ReadInteractions(JsonElement statement) =>
        statement.GetProperty("rest")[0].TryGetProperty("resource", out JsonElement resources)
            ? [.. resources.EnumerateArray().Select(r => (
                r.GetProperty("type").GetString()!,
                r.GetProperty("interaction").EnumerateArray().Any(i => i.GetProperty("code").GetString() == "read")))]
            : [];
}
