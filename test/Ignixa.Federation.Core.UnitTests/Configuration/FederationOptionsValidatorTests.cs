using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Shards;
using Microsoft.Extensions.Options;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Shards.TestShards;

namespace Ignixa.Federation.Core.UnitTests.Configuration;

public sealed class FederationOptionsValidatorTests
{
    private readonly FederationOptionsValidator _validator = new(Profiles);

    [Fact]
    public void Defaults_WithAValidShard_AreValid()
    {
        _validator.Validate(null, Valid()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void ProxyReadsOff_AndPassthroughPaths_AreLegal()
    {
        FederationOptions options = Valid();
        options.ProxyReads = false;
        options.ChainResolution.Passthrough.Add("Observation.subject:Patient");
        options.ChainResolution.Passthrough.Add("Encounter.service-provider:Organization");

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void ShardErrors_AreTheRegistrysErrors()
    {
        FederationOptions options = Valid();
        options.Shards.Clear();

        Failures(options).ShouldBe(["Federation:Shards: at least one shard is required."]);
    }

    [Theory]
    [InlineData("DefaultPageSize")]
    [InlineData("MaxPageSize")]
    [InlineData("MaxChainKeys")]
    [InlineData("MaxChainSourceRows")]
    [InlineData("MaxIncludesPerPage")]
    [InlineData("BackendCallsPerShard")]
    [InlineData("MaxResponseBytes")]
    [InlineData("MaxParallelBackendCalls")]
    [InlineData("ShardCountWarningThreshold")]
    public void EveryLimit_MustBePositive(string limit)
    {
        foreach (int value in new[] { 0, -1 })
        {
            FederationOptions options = Valid();
            SetLimit(options.Limits, limit, value);

            Failures(options).ShouldContain(f => f.StartsWith($"Federation:Limits:{limit}: ", StringComparison.Ordinal), $"{limit} = {value}");
        }
    }

    [Theory]
    [InlineData(20, 100, true)]
    [InlineData(100, 100, true)]
    [InlineData(10, 50, true)]
    [InlineData(51, 50, false)]
    public void DefaultPageSize_MustNotExceedMaxPageSize(int defaultPageSize, int maxPageSize, bool valid)
    {
        FederationOptions options = Valid();
        options.Limits.DefaultPageSize = defaultPageSize;
        options.Limits.MaxPageSize = maxPageSize;

        Check(options, valid, "Federation:Limits:DefaultPageSize");
    }

    [Fact]
    public void MaxPageSize_MustNotExceedTheSpecMaximum()
    {
        FederationOptions options = Valid();
        options.Limits.MaxPageSize = 101;

        Failures(options).ShouldHaveSingleItem().ShouldStartWith("Federation:Limits:MaxPageSize: ");
    }

    [Theory]
    [InlineData(int.MaxValue, 1, true)]
    [InlineData(int.MaxValue / 2, 2, true)]
    [InlineData((int.MaxValue / 2) + 1, 2, false)]
    public void BackendCallsAcrossAllShards_MustFitInAnInt(int backendCallsPerShard, int shardCount, bool valid)
    {
        FederationOptions options = Valid();
        options.Shards.Clear();
        for (int i = 0; i < shardCount; i++)
        {
            options.Shards.Add(Shard($"shard-{i}", $"s{i}", $"https://shard{i}.example/fhir"));
        }

        options.Limits.BackendCallsPerShard = backendCallsPerShard;

        Check(options, valid, "Federation:Limits:BackendCallsPerShard");
    }

    [Theory]
    [InlineData("Request")]
    [InlineData("BackendCall")]
    [InlineData("ChainSubquery")]
    public void EveryTimeout_MustBePositive(string timeout)
    {
        foreach (TimeSpan value in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1) })
        {
            FederationOptions options = Valid();
            SetTimeout(options.Timeouts, timeout, value);

            Failures(options).ShouldContain(f => f.StartsWith($"Federation:Timeouts:{timeout}: ", StringComparison.Ordinal), $"{timeout} = {value}");
        }
    }

    [Theory]
    [InlineData("BackendCall", 30, true)]
    [InlineData("BackendCall", 31, false)]
    [InlineData("ChainSubquery", 30, true)]
    [InlineData("ChainSubquery", 31, false)]
    public void InnerDeadlines_MustFitWithinTheRequest(string timeout, int seconds, bool valid)
    {
        FederationOptions options = Valid();
        SetTimeout(options.Timeouts, timeout, TimeSpan.FromSeconds(seconds));

        Check(options, valid, $"Federation:Timeouts:{timeout}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxInlineTokenBytes_MustBePositive(int value)
    {
        FederationOptions options = Valid();
        options.Tokens.MaxInlineTokenBytes = value;

        Failures(options).ShouldHaveSingleItem().ShouldStartWith("Federation:Tokens:MaxInlineTokenBytes: ");
    }

    [Theory]
    [InlineData(0, 60, "Federation:Tokens:SlidingLifetime")]
    [InlineData(-1, 60, "Federation:Tokens:SlidingLifetime")]
    [InlineData(61, 60, "Federation:Tokens:SlidingLifetime")]
    [InlineData(60, 60, null)]
    public void SlidingLifetime_MustBePositive_AndWithinTheAbsoluteLifetime(int slidingMinutes, int absoluteMinutes, string? failingPath)
    {
        FederationOptions options = Valid();
        options.Tokens.SlidingLifetime = TimeSpan.FromMinutes(slidingMinutes);
        options.Tokens.AbsoluteLifetime = TimeSpan.FromMinutes(absoluteMinutes);

        Check(options, failingPath is null, failingPath);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AbsoluteLifetime_MustBePositive(int minutes)
    {
        FederationOptions options = Valid();
        options.Tokens.SlidingLifetime = TimeSpan.FromMinutes(-5);
        options.Tokens.AbsoluteLifetime = TimeSpan.FromMinutes(minutes);

        Failures(options).ShouldContain(f => f.StartsWith("Federation:Tokens:AbsoluteLifetime: ", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultChainResolution_MustBeDistributed()
    {
        FederationOptions options = Valid();
        options.ChainResolution.Default = ChainResolutionMode.Passthrough;

        Failures(options).ShouldHaveSingleItem().ShouldStartWith("Federation:ChainResolution:Default: ");
    }

    [Theory]
    [InlineData("observation.subject:Patient")]
    [InlineData("xObservation.subject:Patient")]
    [InlineData(" Observation.subject:Patient")]
    [InlineData("Observation.subject")]
    [InlineData("Observation.subject:patient")]
    [InlineData("Observation..subject:Patient")]
    [InlineData("Observation.1subject:Patient")]
    [InlineData("Observation.subject:Patient\n")]
    [InlineData("Observation.subject:Patient.name")]
    [InlineData("")]
    [InlineData(null)]
    public void PassthroughPath_MustBeTypeDotParameterColonTarget(string? path)
    {
        FederationOptions options = Valid();
        options.ChainResolution.Passthrough.Add("Observation.subject:Patient");
        options.ChainResolution.Passthrough.Add(path!);

        Failures(options).ShouldHaveSingleItem().ShouldStartWith("Federation:ChainResolution:Passthrough:1: ");
    }

    [Fact]
    public void PassthroughPath_MayNotRepeat()
    {
        FederationOptions options = Valid();
        options.ChainResolution.Passthrough.Add("Observation.subject:Patient");
        options.ChainResolution.Passthrough.Add("Observation.subject:Patient");

        Failures(options).ShouldHaveSingleItem().ShouldStartWith("Federation:ChainResolution:Passthrough:1: ");
    }

    [Fact]
    public void AllFailures_AreReportedAtOnce()
    {
        FederationOptions options = Valid();
        options.Shards[0].Alias = "BAD";
        options.Limits.MaxChainKeys = 0;
        options.Timeouts.BackendCall = TimeSpan.FromMinutes(1);
        options.Tokens.MaxInlineTokenBytes = 0;
        options.ChainResolution.Default = ChainResolutionMode.Passthrough;

        Failures(options).Count.ShouldBe(5);
    }

    [Fact]
    public void InvalidArguments_AreProgrammingErrors()
    {
        Should.Throw<ArgumentNullException>(() => new FederationOptionsValidator(null!)).ParamName.ShouldBe("profiles");
        Should.Throw<ArgumentNullException>(() => _validator.Validate(null, null!)).ParamName.ShouldBe("options");
    }

    [Fact]
    public void Profiles_AreCopied()
    {
        BackendProfile[] profiles = [MicrosoftFhirServer];
        var validator = new FederationOptionsValidator(profiles);

        profiles[0] = IgnixaProfile;

        validator.Validate(null, Valid()).Succeeded.ShouldBeTrue();
    }

    private static FederationOptions Valid()
    {
        var options = new FederationOptions();
        options.Shards.Add(East());
        return options;
    }

    private List<string> Failures(FederationOptions options)
    {
        ValidateOptionsResult result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        return [.. result.Failures];
    }

    private void Check(FederationOptions options, bool valid, string? failingPath)
    {
        if (valid)
        {
            _validator.Validate(null, options).Succeeded.ShouldBeTrue();
        }
        else
        {
            Failures(options).ShouldHaveSingleItem().ShouldStartWith(failingPath + ": ");
        }
    }

    private static void SetLimit(LimitOptions limits, string limit, int value)
    {
        switch (limit)
        {
            case "DefaultPageSize": limits.DefaultPageSize = value; break;
            case "MaxPageSize": limits.MaxPageSize = value; break;
            case "MaxChainKeys": limits.MaxChainKeys = value; break;
            case "MaxChainSourceRows": limits.MaxChainSourceRows = value; break;
            case "MaxIncludesPerPage": limits.MaxIncludesPerPage = value; break;
            case "BackendCallsPerShard": limits.BackendCallsPerShard = value; break;
            case "MaxResponseBytes": limits.MaxResponseBytes = value; break;
            case "MaxParallelBackendCalls": limits.MaxParallelBackendCalls = value; break;
            case "ShardCountWarningThreshold": limits.ShardCountWarningThreshold = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(limit), limit, "Unknown limit.");
        }
    }

    private static void SetTimeout(TimeoutOptions timeouts, string timeout, TimeSpan value)
    {
        switch (timeout)
        {
            case "Request": timeouts.Request = value; break;
            case "BackendCall": timeouts.BackendCall = value; break;
            case "ChainSubquery": timeouts.ChainSubquery = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Unknown timeout.");
        }
    }
}
