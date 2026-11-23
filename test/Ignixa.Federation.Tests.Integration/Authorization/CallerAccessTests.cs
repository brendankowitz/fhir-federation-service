using System.Security.Claims;
using Ignixa.Federation.Api.Authorization;
using Ignixa.Federation.Core.Identity;
using Microsoft.AspNetCore.Authentication;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Authorization;

/// <summary>
/// Exercises <see cref="CallerAccess.Evaluate"/> with real ASP.NET <see cref="AuthenticateResult"/> and
/// <see cref="AuthenticationTicket"/> values over synthetic principals (design spec §10.1). No token is parsed or
/// validated here: the component only ever sees the result of the configured JWT scheme.
/// </summary>
public sealed class CallerAccessTests
{
    private const string Scheme = "Bearer";

    private static readonly ResourceType Patient = ResourceType.Parse("Patient");
    private static readonly ResourceType Observation = ResourceType.Parse("Observation");
    private static readonly ResourceType Encounter = ResourceType.Parse("Encounter");

    private static readonly ResourceType[] AllTypes = [Patient, Observation, Encounter];

    public static TheoryData<string> ScopeClaimTypes => ["scp", "scope"];

    public static TheoryData<string> SmartScopeValues =>
    [
        "patient/Patient.read",
        "user/Observation.rs",
        "system/*.*",
        "launch",
        "launch/patient",
        "openid patient/Patient.read",
        "openid profile user/*.read",
        "access_as_user  system/Patient.read",
        "access_as_user\tlaunch",
        "PATIENT/Patient.read",
        "System/*.read",
        "Launch/Patient",
    ];

    public static TheoryData<string> OrdinaryScopeValues =>
    [
        "access_as_user",
        "openid profile email",
        "Reader",
        "Patient",
        "Patient.read",
        "mypatient/Patient.read",
        "patients/Patient.read",
        "relaunch",
    ];

    [Fact]
    public void NoAuthenticationResult_Is401()
    {
        CallerAccess access = CallerAccess.Evaluate(null, Scheme, Grants(Map("Reader", Patient)));

        AssertDenied(access, 401);
    }

    [Fact]
    public void NoResultAndFailedResults_Are401()
    {
        IReadOnlyDictionary<string, IReadOnlyCollection<ResourceType>> grants = Grants(Map("Reader", Patient));

        AssertDenied(CallerAccess.Evaluate(AuthenticateResult.NoResult(), Scheme, grants), 401);
        AssertDenied(CallerAccess.Evaluate(AuthenticateResult.Fail("bad token"), Scheme, grants), 401);
        AssertDenied(CallerAccess.Evaluate(AuthenticateResult.Fail(new InvalidOperationException("bad"), new AuthenticationProperties()), Scheme, grants), 401);
    }

    [Theory]
    [InlineData("Other")]
    [InlineData("bearer")]
    [InlineData("Bearer ")]
    [InlineData("")]
    public void TicketFromAnotherScheme_Is401EvenWithAMappedRole(string ticketScheme)
    {
        AuthenticateResult result = Succeeded(ticketScheme, Identity(Role("Reader")));

        CallerAccess access = CallerAccess.Evaluate(result, Scheme, Grants(Map("Reader", Patient)));

        AssertDenied(access, 401);
    }

    [Fact]
    public void UnauthenticatedIdentity_Is401EvenWithAMappedRole()
    {
        ClaimsIdentity identity = new([Role("Reader")]);
        identity.IsAuthenticated.ShouldBeFalse();

        CallerAccess access = CallerAccess.Evaluate(Succeeded(Scheme, identity), Scheme, Grants(Map("Reader", Patient)));

        AssertDenied(access, 401);
    }

    [Fact]
    public void PrincipalWithoutIdentities_Is401()
    {
        AuthenticateResult result = AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(), Scheme));

        AssertDenied(CallerAccess.Evaluate(result, Scheme, Grants(Map("Reader", Patient))), 401);
    }

    [Fact]
    public void MoreThanOneIdentity_Is401AndRolesAreNotAggregated()
    {
        IReadOnlyDictionary<string, IReadOnlyCollection<ResourceType>> grants = Grants(Map("Reader", Patient), Map("Other", Observation));

        ClaimsPrincipal twoAuthenticated = new([Identity(Role("Reader")), Identity(Role("Other"))]);
        ClaimsPrincipal authenticatedPlusAnonymous = new([Identity(Role("Reader")), new ClaimsIdentity([Role("Other")])]);

        AssertDenied(CallerAccess.Evaluate(Succeeded(Scheme, twoAuthenticated), Scheme, grants), 401);
        AssertDenied(CallerAccess.Evaluate(Succeeded(Scheme, authenticatedPlusAnonymous), Scheme, grants), 401);
    }

    [Fact]
    public void ValidCallerWithoutRoleClaims_Is403()
    {
        CallerAccess access = CallerAccess.Evaluate(Succeeded(Scheme, Identity()), Scheme, Grants(Map("Reader", Patient)));

        AssertDenied(access, 403);
    }

    [Fact]
    public void RoleWithNoMappingOrAnEmptyMapping_Is403()
    {
        AuthenticateResult result = Succeeded(Scheme, Identity(Role("Reader")));

        AssertDenied(CallerAccess.Evaluate(result, Scheme, Grants(Map("Writer", Patient))), 403);
        AssertDenied(CallerAccess.Evaluate(result, Scheme, Grants(Map("Reader"))), 403);
        AssertDenied(CallerAccess.Evaluate(result, Scheme, Grants()), 403);
    }

    [Fact]
    public void SingleRole_AllowsExactlyItsMappedTypes()
    {
        CallerAccess access = CallerAccess.Evaluate(
            Succeeded(Scheme, Identity(Role("Reader"))),
            Scheme,
            Grants(Map("Reader", Patient, Observation), Map("Admin", Encounter)));

        access.FailureStatusCode.ShouldBeNull();
        access.CanRead(Patient).ShouldBeTrue();
        access.CanRead(Observation).ShouldBeTrue();
        access.CanRead(Encounter).ShouldBeFalse();
    }

    [Fact]
    public void MultipleRoles_GrantTheUnionOfTheirTypes()
    {
        CallerAccess access = CallerAccess.Evaluate(
            Succeeded(Scheme, Identity(Role("A"), Role("B"), Role("Unmapped"))),
            Scheme,
            Grants(Map("A", Patient), Map("B", Observation), Map("C", Encounter)));

        access.FailureStatusCode.ShouldBeNull();
        access.CanRead(Patient).ShouldBeTrue();
        access.CanRead(Observation).ShouldBeTrue();
        access.CanRead(Encounter).ShouldBeFalse();
    }

    [Fact]
    public void TypeCheckIsExactBecauseResourceTypesAreCaseSensitive()
    {
        CallerAccess access = CallerAccess.Evaluate(
            Succeeded(Scheme, Identity(Role("Reader"))),
            Scheme,
            Grants(Map("Reader", Patient)));

        access.CanRead(ResourceType.Parse("Patient")).ShouldBeTrue();
        access.CanRead(ResourceType.Parse("PatientX")).ShouldBeFalse();
        access.CanRead(ResourceType.Parse("Patien")).ShouldBeFalse();
    }

    [Fact]
    public void RoleNamesAreMatchedOrdinally_EvenWhenTheConfiguredDictionaryIsCaseInsensitive()
    {
        IReadOnlyDictionary<string, IReadOnlyCollection<ResourceType>> grants = new Dictionary<string, IReadOnlyCollection<ResourceType>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Reader"] = [Patient],
        };

        AssertDenied(CallerAccess.Evaluate(Succeeded(Scheme, Identity(Role("reader"))), Scheme, grants), 403);
        AssertDenied(CallerAccess.Evaluate(Succeeded(Scheme, Identity(Role("READER"))), Scheme, grants), 403);
        CallerAccess.Evaluate(Succeeded(Scheme, Identity(Role("Reader"))), Scheme, grants).CanRead(Patient).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Reader Writer")]
    [InlineData(" Reader")]
    [InlineData("Reader ")]
    [InlineData("Reader,Writer")]
    [InlineData("Reader\tWriter")]
    [InlineData("*")]
    [InlineData("Reader.*")]
    [InlineData("Federation.Reader")]
    [InlineData("")]
    public void RoleClaimValueIsIndivisibleAndNeverAWildcardOrPrefix(string roleClaimValue)
    {
        CallerAccess access = CallerAccess.Evaluate(
            Succeeded(Scheme, Identity(Role(roleClaimValue))),
            Scheme,
            Grants(Map("Reader", Patient), Map("Writer", Observation), Map("Federation.Read", Encounter)));

        AssertDenied(access, 403);
    }

    [Fact]
    public void ConfiguredWildcardLookingRoleKeysAreLiteralNames()
    {
        CallerAccess access = CallerAccess.Evaluate(
            Succeeded(Scheme, Identity(Role("Federation.*"))),
            Scheme,
            Grants(Map("Federation.*", Patient), Map("*", Observation)));

        access.CanRead(Patient).ShouldBeTrue();
        access.CanRead(Observation).ShouldBeFalse();
    }

    [Theory]
    [InlineData("role")]
    [InlineData("Roles")]
    [InlineData("ROLES")]
    [InlineData(ClaimTypes.Role)]
    [InlineData("groups")]
    [InlineData("wids")]
    [InlineData("http://schemas.microsoft.com/ws/2008/06/identity/claims/roles")]
    public void OnlyTheExactRolesClaimTypeGrantsAnything(string claimType)
    {
        CallerAccess access = CallerAccess.Evaluate(
            Succeeded(Scheme, Identity(new Claim(claimType, "Reader"))),
            Scheme,
            Grants(Map("Reader", Patient)));

        AssertDenied(access, 403);
    }

    [Theory]
    [MemberData(nameof(ScopeClaimTypes))]
    public void ScopesNeverGrantRoles_EvenWhenAScopeEqualsAMappedRoleOrTypeName(string scopeClaimType)
    {
        CallerAccess access = CallerAccess.Evaluate(
            Succeeded(Scheme, Identity(new Claim(scopeClaimType, "Reader"), new Claim(scopeClaimType, "Patient Patient.read access_as_user"))),
            Scheme,
            Grants(Map("Reader", Patient), Map("Patient", Observation), Map("access_as_user", Encounter)));

        AssertDenied(access, 403);
    }

    [Theory]
    [MemberData(nameof(OrdinaryScopeValues))]
    public void OrdinaryScopes_NeitherGrantNorRefuseAnExistingRoleGrant(string scopeValue)
    {
        foreach (string scopeClaimType in new[] { "scp", "scope" })
        {
            CallerAccess access = CallerAccess.Evaluate(
                Succeeded(Scheme, Identity(Role("Reader"), new Claim(scopeClaimType, scopeValue))),
                Scheme,
                Grants(Map("Reader", Patient)));

            access.FailureStatusCode.ShouldBeNull();
            access.CanRead(Patient).ShouldBeTrue();
            access.CanRead(Observation).ShouldBeFalse();
        }
    }

    [Theory]
    [MemberData(nameof(SmartScopeValues))]
    public void AnySmartScope_RefusesTheWholeCallerWith403_EvenWithAMappedRole(string scopeValue)
    {
        foreach (string scopeClaimType in new[] { "scp", "scope" })
        {
            CallerAccess access = CallerAccess.Evaluate(
                Succeeded(Scheme, Identity(Role("Reader"), new Claim(scopeClaimType, scopeValue))),
                Scheme,
                Grants(Map("Reader", Patient)));

            AssertDenied(access, 403);
        }
    }

    [Theory]
    [MemberData(nameof(ScopeClaimTypes))]
    public void SmartScopeInARepeatedClaim_RefusesTheCaller(string scopeClaimType)
    {
        CallerAccess access = CallerAccess.Evaluate(
            Succeeded(
                Scheme,
                Identity(
                    Role("Reader"),
                    new Claim(scopeClaimType, "access_as_user"),
                    new Claim(scopeClaimType, "openid profile"),
                    new Claim(scopeClaimType, "email system/Patient.read"))),
            Scheme,
            Grants(Map("Reader", Patient)));

        AssertDenied(access, 403);
    }

    [Fact]
    public void SmartScopeInOneScopeClaimTypeRefusesTheCallerWhateverTheOtherHolds()
    {
        CallerAccess access = CallerAccess.Evaluate(
            Succeeded(Scheme, Identity(Role("Reader"), new Claim("scp", "access_as_user"), new Claim("scope", "launch/patient"))),
            Scheme,
            Grants(Map("Reader", Patient)));

        AssertDenied(access, 403);
    }

    [Fact]
    public void ResultIsASnapshot_OfTheConfiguredGrants()
    {
        List<ResourceType> readerTypes = [Patient];
        Dictionary<string, IReadOnlyCollection<ResourceType>> grants = new() { ["Reader"] = readerTypes };

        CallerAccess access = CallerAccess.Evaluate(Succeeded(Scheme, Identity(Role("Reader"))), Scheme, grants);

        readerTypes.Add(Observation);
        grants["Reader"] = [Encounter];
        grants.Clear();

        access.CanRead(Patient).ShouldBeTrue();
        access.CanRead(Observation).ShouldBeFalse();
        access.CanRead(Encounter).ShouldBeFalse();
    }

    private static void AssertDenied(CallerAccess access, int expectedStatusCode)
    {
        access.FailureStatusCode.ShouldBe(expectedStatusCode);
        foreach (ResourceType type in AllTypes)
        {
            access.CanRead(type).ShouldBeFalse($"a denied caller must not read {type}");
        }
    }

    private static (string Role, ResourceType[] Types) Map(string role, params ResourceType[] types) => (role, types);

    private static Dictionary<string, IReadOnlyCollection<ResourceType>> Grants(params (string Role, ResourceType[] Types)[] entries)
        => entries.ToDictionary(e => e.Role, e => (IReadOnlyCollection<ResourceType>)e.Types, StringComparer.Ordinal);

    private static Claim Role(string value) => new("roles", value);

    private static ClaimsIdentity Identity(params Claim[] claims) => new(claims, "TestAuthentication");

    private static AuthenticateResult Succeeded(string ticketScheme, ClaimsIdentity identity)
        => Succeeded(ticketScheme, new ClaimsPrincipal(identity));

    private static AuthenticateResult Succeeded(string ticketScheme, ClaimsPrincipal principal)
        => AuthenticateResult.Success(new AuthenticationTicket(principal, ticketScheme));
}
