using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ignixa.Federation.Tests.Integration.Budgets;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Search;

/// <summary>
/// How search fails: unconfigured hosts, unsupported or unbounded requests, budgets, deadlines, cancellation and shard
/// failures. Every failure is a whole-request failure with an OperationOutcome that names no parameter value, token,
/// exception or shard URL.
/// </summary>
public sealed class PrimarySearchFailureTests
{
    [Fact]
    public async Task UnconfiguredHost_ServesHealthButNotSearch()
    {
        using var host = SearchHost.Start(setup =>
        {
            setup.Unconfigure();
            setup.Capabilities = null;
            setup.RegisterTransport = false;
        });

        SearchResult search = await host.GetAsync("/Patient?name=Orchid", token: null);
        HttpResponseMessage live = await host.Client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        HttpResponseMessage ready = await host.Client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        search.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        search.MediaType.ShouldBe("application/fhir+json");
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("http://fhir.example.test/federation")]
    [InlineData("https://fhir.example.test/federation?x=1")]
    [InlineData("https://user@fhir.example.test/federation")]
    [InlineData("/federation")]
    public void InvalidPublicBaseUrl_FailsStartupWithoutEchoingIt(string publicBase)
    {
        var exception = Should.Throw<Exception>(() =>
        {
            using var host = SearchHost.Start(setup => setup.Settings["Federation:Host:PublicBaseUrl"] = publicBase);
        });

        exception.ToString().ShouldContain("Federation:Host");
        exception.ToString().ShouldNotContain(publicBase);
    }

    [Fact]
    public void ConfiguredShardsThatDisagreeWithTheCapabilitiesRegistry_FailStartup()
    {
        Should.Throw<Exception>(() =>
        {
            using var host = SearchHost.Start(setup => setup.Settings["Federation:Shards:1:Alias"] = "zz99");
        });
    }

    [Fact]
    public void ConfiguredHostWithoutATransport_FailsStartup()
    {
        Should.Throw<Exception>(() =>
        {
            using var host = SearchHost.Start(setup => setup.RegisterTransport = false);
        });
    }

    [Fact]
    public void ConfiguredHostWithoutCapabilities_FailsStartup()
    {
        Should.Throw<Exception>(() =>
        {
            using var host = SearchHost.Start(setup => setup.Capabilities = null);
        });
    }

    [Fact]
    public async Task FailingShard_FailsTheWholeRequestNamingOnlyItsAlias()
    {
        using var host = SearchHost.Start(setup => setup.Shards.FailingShard = "west");

        SearchResult result = await host.GetAsync("/Patient?name=Orchid");

        result.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        result.MediaType.ShouldBe("application/fhir+json");
        result.Body.ShouldContain("p9xm");
        result.Body.ShouldNotContain("Orchid");
        result.Body.ShouldNotContain("fhir-west.example.org");
        result.Body.ShouldNotContain("secret-path");
        result.Body.ShouldNotContain("entry");
        result.Body.ShouldNotContain("Bundle");
    }

    [Fact]
    public async Task FailingShardNotNeededForThePage_DoesNotFailIt()
    {
        using var host = SearchHost.Start(setup => setup.Shards.FailingShard = "west");

        SearchResult result = await host.GetAsync("/Patient?name=Orchid&_count=1");

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Entries.Single().FullUrl.ShouldBe(SearchWorld.EastDupUrl);
    }

    [Fact]
    public async Task MalformedShardResponse_FailsTheWholeRequest()
    {
        using var host = SearchHost.Start(setup => setup.Shards.MalformedShard = "west");

        SearchResult result = await host.GetAsync("/Patient?name=Orchid");

        result.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        result.Body.ShouldContain("p9xm");
        result.Body.ShouldNotContain("Bundle");
    }

    [Fact]
    public async Task ShardResourceOnlyTheSearchsetWriterRejects_FailsTheWholeRequestNamingItsAlias()
    {
        using var host = SearchHost.Start(setup => setup.Shards.UnshapeableShard = "west");

        SearchResult result = await host.GetAsync("/Patient?name=Orchid");

        result.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        result.MediaType.ShouldBe("application/fhir+json");
        result.Body.ShouldContain("p9xm");
        result.Body.ShouldNotContain("k7q2");
        result.Body.ShouldNotContain("Orchid");
        result.Body.ShouldNotContain("fhir-west.example.org");
        result.Body.ShouldNotContain("Bundle");
    }

    [Fact]
    public async Task OverlongQuery_IsRejectedBeforeParsing()
    {
        using var host = SearchHost.Start();

        SearchResult result = await host.GetAsync("/Patient?name=" + new string('a', 9000));

        result.StatusCode.ShouldBe(HttpStatusCode.RequestUriTooLong);
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task OversizedBody_IsRejected_WithOrWithoutADeclaredLength()
    {
        using var host = SearchHost.Start();
        string body = "name=" + new string('a', 40_000);

        SearchResult declared = await host.PostFormAsync("/Patient/_search", body);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Patient/_search") { Content = new UndeclaredLengthContent(Encoding.UTF8.GetBytes(body)) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-www-form-urlencoded");
        SearchResult undeclared = await host.SendAsync(request, host.Token());

        declared.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        undeclared.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData("application/x-www-form-urlencoded; charset=iso-8859-1")]
    [InlineData("text/plain")]
    public async Task PostWithAnotherContentType_IsUnsupportedMediaType(string mediaType)
    {
        using var host = SearchHost.Start();

        SearchResult result = await host.PostFormAsync("/Patient/_search", "name=Orchid", mediaType: mediaType);

        result.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task PostWithoutAContentType_IsUnsupportedMediaType()
    {
        using var host = SearchHost.Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Patient/_search") { Content = new ByteArrayContent("name=Orchid"u8.ToArray()) };

        SearchResult result = await host.SendAsync(request, host.Token());

        result.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task PostWithInvalidUtf8_IsInvalid()
    {
        using var host = SearchHost.Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Patient/_search") { Content = new ByteArrayContent([(byte)'n', (byte)'=', 0xC3, 0x28]) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-www-form-urlencoded");

        SearchResult result = await host.SendAsync(request, host.Token());

        result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("_total=accurate")]
    [InlineData("_summary=count")]
    [InlineData("_count=0")]
    public async Task AdmittedButUnimplementedStages_AreNotImplemented_WithoutAnyShardCall(string query)
    {
        // Every shard certifies an exact count for this shape, so the planner admits the count and only the host refuses it.
        using var host = SearchHost.Start(setup => setup.Capabilities = SearchWorld.Capabilities(exactCountShapes: ["Patient?name"]));

        SearchResult result = await host.GetAsync($"/Patient?name=Orchid&{query}");

        result.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        result.IssueCode.ShouldBe("not-supported");
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReferenceMissingPredicate_PagesAndFollows_SendingOnlyThatPredicateToEveryShard()
    {
        // The discovery projection never lists a reference parameter, but the planner admits `organization:missing` and the
        // view resolves its definition, so the search is pinned, paged and followed. Only the presence test reaches the
        // shards: no reference value is admitted, rewritten or resolved to a shard identity.
        using var host = SearchHost.Start(setup => setup.Capabilities = SearchWorld.Capabilities(organizationMissing: true));

        SearchResult first = await host.GetAsync("/Patient?organization:missing=true&_count=1");
        SearchResult second = await host.FollowAsync(first.Link("next")!);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.Entries.ShouldHaveSingleItem();
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Entries.ShouldHaveSingleItem();
        second.Entries.Single().FullUrl.ShouldNotBe(first.Entries.Single().FullUrl);
        host.Shards.Calls.Where(c => c.Method is not null).ShouldNotBeEmpty();
        host.Shards.Calls.Where(c => c.Method is not null)
            .ShouldAllBe(c => c.Parameters == "organization:missing=true" && c.PageSize == 1);
    }

    [Fact]
    public async Task ReferenceMissingCapability_DoesNotAdmitAReferenceIdentityFilter()
    {
        using var host = SearchHost.Start(setup => setup.Capabilities = SearchWorld.Capabilities(organizationMissing: true));

        SearchResult result = await host.GetAsync("/Patient?organization=Organization/dup");

        result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        result.IssueCode.ShouldBe("not-supported");
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("_sort=_lastUpdated", "not-supported")]
    [InlineData("_include=Patient:organization", "not-supported")]
    [InlineData("unknown-parameter=x", "not-supported")]
    [InlineData("name:sounds-like=x", "not-supported")]
    [InlineData("_count=abc", "invalid")]
    [InlineData("name", "invalid")]
    [InlineData("name=%E0%A4", "invalid")]
    public async Task UnsupportedOrInvalidQueries_AreRejected_WithoutEchoingThem(string query, string code)
    {
        using var host = SearchHost.Start();

        SearchResult result = await host.GetAsync($"/Patient?{query}");

        result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        result.IssueCode.ShouldBe(code);
        result.Body.ShouldNotContain("abc");
        result.Body.ShouldNotContain("sounds-like");
        result.Body.ShouldNotContain("unknown-parameter");
        result.Body.ShouldNotContain("organization");
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnsupportedFormat_IsUnsupportedMediaType()
    {
        using var host = SearchHost.Start();

        SearchResult result = await host.GetAsync("/Patient?_format=xml");

        result.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task RetriesBeyondTheBackendCallBudget_AreTooCostly()
    {
        using var host = SearchHost.Start(setup =>
        {
            setup.Settings["Federation:Limits:BackendCallsPerShard"] = "1";
            setup.Shards.SendsPerCall = 2;
        });

        SearchResult result = await host.GetAsync("/Patient?name=Orchid");

        result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        result.IssueCode.ShouldBe("too-costly");
        result.Body.ShouldNotContain("Bundle");
    }

    [Fact]
    public async Task ResponsesBeyondTheByteBudget_AreTooCostly()
    {
        using var host = SearchHost.Start(setup => setup.Settings["Federation:Limits:MaxResponseBytes"] = "100");

        SearchResult result = await host.GetAsync("/Patient?name=Orchid");

        result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        result.IssueCode.ShouldBe("too-costly");
    }

    [Fact]
    public async Task RequestDeadline_IsAGatewayTimeout()
    {
        var time = new ManualTimeProvider();
        using var host = SearchHost.Start(setup =>
        {
            setup.Time = time;
            setup.Shards.DuringCall = () => time.Advance(TimeSpan.FromMinutes(1));
        });

        SearchResult result = await host.GetAsync("/Patient?name=Orchid");

        result.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        result.Body.ShouldNotContain("Bundle");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExpiredEndpointBudgetStopsAfterAuthentication_BeforeChallengeOrQueryHandling(bool authenticated)
    {
        var time = new DeadlineArmingTimeProvider(TimeSpan.FromSeconds(17), expireWhenArmed: true);
        using var host = SearchHost.Start(setup =>
        {
            setup.Time = time;
            setup.Settings["Federation:Timeouts:Request"] = "00:00:17";
        });

        SearchResult result = await host.GetAsync("/Patient?unsupported=Orchid", authenticated ? host.Token() : null);

        result.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        result.IssueCode.ShouldBe("timeout");
        result.Challenges.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExpiredBudgetTakesPrecedenceOverColdSigningReadiness()
    {
        var time = new DeadlineArmingTimeProvider(TimeSpan.FromSeconds(17), expireWhenArmed: true);
        using var host = SearchHost.Start(setup =>
        {
            setup.Time = time;
            setup.Settings["Federation:Timeouts:Request"] = "00:00:17";
            setup.WaitForReadiness = false;
            setup.IdentityProvider.BeforeResponse = (_, _, cancellation) => Task.Delay(Timeout.Infinite, cancellation);
        });

        SearchResult result = await host.GetAsync("/Patient?name=Orchid");

        result.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        result.IssueCode.ShouldBe("timeout");
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task RequestDeadline_BoundsASlowAuthenticatedBody_WithoutAnyShardCall()
    {
        // The request deadline starts at entry: a caller that sends half a form and stalls is answered 504 when the deadline
        // passes, not held until it finishes, and no shard is called.
        var time = new DeadlineArmingTimeProvider(TimeSpan.FromSeconds(17));
        using var host = SearchHost.Start(setup =>
        {
            setup.Time = time;
            setup.Settings["Federation:Timeouts:Request"] = "00:00:17";
        });
        using var release = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        async Task<SearchResult> SendStalledFormAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/Patient/_search") { Content = new StallingContent("name=Orch"u8.ToArray(), release.Token) };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-www-form-urlencoded");
            return await host.SendAsync(request, host.Token());
        }

        try
        {
            Task<SearchResult> search = SendStalledFormAsync();
            await time.Armed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromMinutes(1));
            SearchResult result = await search.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            result.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
            result.IssueCode.ShouldBe("timeout");
            result.Body.ShouldNotContain("Orch");
            host.Shards.Calls.ShouldBeEmpty();
        }
        finally
        {
            await release.CancelAsync();
        }
    }

    [Fact]
    public async Task CallerCancellation_ReachesTheTransport()
    {
        using var host = SearchHost.Start(setup => setup.Shards.Hang = true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<SearchResult> search = host.GetCancellableAsync("/Patient?name=Orchid", host.Token(), cancellation.Token);
        await host.Shards.Hanging.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(search);
        await host.Shards.ObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NextLinkLongerThanAnInlineToken_IsUnavailableRatherThanTruncated()
    {
        using var host = SearchHost.Start();

        SearchResult result = await host.GetAsync("/Patient?_count=1&name=" + new string('a', 3000));

        result.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        result.Body.ShouldNotContain("aaaa");
        result.Body.ShouldNotContain("Bundle");
    }

    [Fact]
    public async Task NextLinkWithinTheInlineTokenButBeyondTheQueryLimit_IsUnavailable()
    {
        using var host = SearchHost.Start(setup =>
        {
            setup.Settings["Federation:Tokens:MaxInlineTokenBytes"] = "32768";
            setup.MaxRequestLineSize = 65536;
        });

        SearchResult control = await host.GetAsync("/Patient?name=Orchid&_count=1");
        SearchResult result = await host.GetAsync("/Patient?_count=1&name=" + new string('a', 6200));

        control.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.FollowAsync(control.Link("next")!)).StatusCode.ShouldBe(HttpStatusCode.OK);
        result.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        result.Body.ShouldNotContain("aaaa");
        result.Body.ShouldNotContain("Bundle");
    }

    [Fact]
    public async Task NextLinkRequestLine_MustFitTheServersRequestLineLimit_IncludingTheBasePath()
    {
        string query = "/Patient?name=Orchid&_count=1";
        int lineBytes;
        using (var measure = SearchHost.Start())
        {
            var next = new Uri((await measure.GetAsync(query)).Link("next")!);
            lineBytes = System.Text.Encoding.UTF8.GetByteCount($"GET {next.PathAndQuery} HTTP/1.1\r\n");
        }

        using var fits = SearchHost.Start(setup => setup.MaxRequestLineSize = lineBytes);
        using var longerBase = SearchHost.Start(setup =>
        {
            setup.MaxRequestLineSize = lineBytes;
            setup.Settings["Federation:Host:PublicBaseUrl"] = SearchWorld.PublicBase + "x";
        });

        SearchResult fitting = await fits.GetAsync(query);
        SearchResult overflowing = await longerBase.GetAsync(query);

        fitting.StatusCode.ShouldBe(HttpStatusCode.OK);
        fitting.Link("next").ShouldNotBeNull();
        overflowing.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        overflowing.Body.ShouldNotContain("Bundle");
    }

    /// <summary>Sends a prefix of a body, then nothing until released; never declares a length.</summary>
    private sealed class StallingContent(byte[] prefix, CancellationToken release) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await stream.WriteAsync(prefix, release);
            await stream.FlushAsync(release);
            await Task.Delay(Timeout.Infinite, release).ContinueWith(_ => { }, TaskScheduler.Default);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>
    /// A <see cref="ManualTimeProvider"/> that reports when a timer is armed for exactly the request deadline, which is
    /// the request budget starting; only then can advancing the clock past the deadline be observed by that request.
    /// </summary>
    private sealed class DeadlineArmingTimeProvider(TimeSpan deadline, bool expireWhenArmed = false) : TimeProvider
    {
        private readonly ManualTimeProvider _inner = new();

        public TaskCompletionSource Armed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override long TimestampFrequency => _inner.TimestampFrequency;

        public override long GetTimestamp() => _inner.GetTimestamp();

        public void Advance(TimeSpan by) => _inner.Advance(by);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ArmingTimer(this, _inner.CreateTimer(callback, state, dueTime, period));
            Arm(dueTime);
            return timer;
        }

        private void Arm(TimeSpan dueTime)
        {
            if (dueTime == deadline)
            {
                if (expireWhenArmed)
                {
                    Advance(deadline);
                }

                Armed.TrySetResult();
            }
        }

        private sealed class ArmingTimer(DeadlineArmingTimeProvider owner, ITimer inner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                // Arm after the inner timer is scheduled, so an Advance the signal releases always finds it due.
                bool changed = inner.Change(dueTime, period);
                owner.Arm(dueTime);
                return changed;
            }

            public void Dispose() => inner.Dispose();

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed class UndeclaredLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
