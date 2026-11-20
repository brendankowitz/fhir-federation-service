using System.Security.Cryptography;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// One real Microsoft FHIR Server R4 on SQL Server, started in a private Docker network for the
/// lifetime of this object. Only the FHIR port is published, and only on the loopback interface
/// with a Docker-assigned port. SQL Server is reachable only from the FHIR container.
/// </summary>
internal sealed class MicrosoftFhirSqlBackend : IAsyncDisposable
{
    // Source: microsoft/fhir-server release/5.2.84, commit 2a67597a015b957066822b0d54f2ccc46971c14e.
    private const string FhirImage =
        "mcr.microsoft.com/healthcareapis/r4-fhir-server:5.2.84@sha256:6f2798229c7f9f987909475c7ed99b1ddd87d30a08c55520f475f8a58c5c75f1";

    private const string SqlImage =
        "mcr.microsoft.com/mssql/server:2022-CU28-ubuntu-22.04@sha256:4525e3efac9f3e241cb003bb2ff2a82818b502f37e12049d788b1a9fa7e13b7f";

    private const ushort FhirContainerPort = 8080;
    private const string SqlAlias = "sql";
    private const string SqlReadyMessage = "SQL Server is now ready for client connections";

    private static readonly TimeSpan StartupBudget = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan FhirReadyBudget = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SeedSearchableBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RemovalBudget = TimeSpan.FromSeconds(30);
    private static readonly Uri DockerEndpoint = new(OperatingSystem.IsWindows()
        ? "npipe://./pipe/dockerDesktopLinuxEngine"
        : "unix:///var/run/docker.sock");

    private string? _sqlName;
    private string? _fhirName;
    private INetwork? _network;

    private MicrosoftFhirSqlBackend()
    {
    }

    /// <summary>The base the test client uses against the backend, with a trailing slash.</summary>
    public string LocalBaseUrl { get; private set; } = string.Empty;

    public FhirHttpClient Client { get; private set; } = null!;
    public string SoftwareIdentity { get; private set; } = string.Empty;

    /// <summary>
    /// Starts SQL Server and the FHIR server, seeds <see cref="SeedData"/>, and returns only after the
    /// seeded Observations are searchable. Anything started before a failure is removed again.
    /// </summary>
    /// <exception cref="BackendEvidenceException">Docker is unavailable, startup or seeding failed or timed out.</exception>
    public static async Task<MicrosoftFhirSqlBackend> StartAsync(CancellationToken cancellationToken)
    {
        var backend = new MicrosoftFhirSqlBackend();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(StartupBudget);
        try
        {
            await backend.StartCoreAsync(budget.Token);
            return backend;
        }
        catch (Exception e)
        {
            // Docker exceptions can include configuration. Never forward the original exception.
            BackendEvidenceException failure = e switch
            {
                BackendEvidenceException evidence => evidence,
                OperationCanceledException => new(
                    BackendFailureKind.Startup, "startup exceeded its deadline or was cancelled."),
                _ => new(BackendFailureKind.Startup, $"initializing the backend failed ({e.GetType().Name})."),
            };
            string? sqlState = await DescribeContainerAsync("SQL", backend._sqlName);
            string? fhirState = await DescribeContainerAsync("FHIR", backend._fhirName);
            string? cleanupFailure = await backend.CleanUpAsync();
            string[] details = [.. new[] { sqlState, fhirState, cleanupFailure }.OfType<string>()];
            if (details.Length > 0)
            {
                throw new BackendEvidenceException(failure.Kind, $"{failure.Detail} {string.Join(" ", details)}");
            }

            throw failure;
        }
    }

    private static async Task<string?> DescribeContainerAsync(string label, string? name)
    {
        if (name is null)
        {
            return null;
        }

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using IDockerClient docker = new DockerClientBuilder().WithEndpoint(DockerEndpoint).Build();
            ContainerInspectResponse report = await docker.Containers.InspectContainerAsync(name, budget.Token);
            return report.State switch
            {
                { Running: true } => $"{label} container is running.",
                { } state => $"{label} container is not running (Docker exit code {state.ExitCode}).",
                null => $"{label} container did not report its state.",
            };
        }
        catch (Exception e)
        {
            // Report the unavailable diagnostic without emitting daemon messages or container configuration.
            return $"{label} container state unavailable ({e.GetType().Name}).";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (await CleanUpAsync() is { } failure)
        {
            throw new BackendEvidenceException(BackendFailureKind.Cleanup, failure);
        }
    }

    private async Task<string?> CleanUpAsync()
    {
        Client?.Dispose();

        string? fhir = await TryRemoveAsync("FHIR container", _fhirName is { } api
            ? ct => RemoveContainerAsync(api, ct)
            : null);
        string? sql = await TryRemoveAsync("SQL container", _sqlName is { } database
            ? ct => RemoveContainerAsync(database, ct)
            : null);
        string? network = await TryRemoveAsync("Docker network", _network is { } bridge ? bridge.DeleteAsync : null);
        _fhirName = null;
        _sqlName = null;
        _network = null;
        string[] failures = [.. new[] { fhir, sql, network }.OfType<string>()];
        return failures.Length == 0 ? null : $"Cleanup failed: {string.Join(", ", failures)}.";
    }

    private static async Task RemoveContainerAsync(string name, CancellationToken cancellationToken)
    {
        // With Ryuk disabled, Testcontainers 4.17 DisposeAsync stops containers but does not delete them.
        using IDockerClient docker = new DockerClientBuilder().WithEndpoint(DockerEndpoint).Build();
        try
        {
            await docker.Containers.RemoveContainerAsync(
                name, new ContainerRemoveParameters { Force = true, RemoveVolumes = true }, cancellationToken);
        }
        catch (DockerContainerNotFoundException)
        {
            // Docker never created the container, or it is already gone.
        }
    }

    private static async Task<string?> TryRemoveAsync(string name, Func<CancellationToken, Task>? remove)
    {
        if (remove is null)
        {
            return null;
        }

        using var budget = new CancellationTokenSource(RemovalBudget);
        try
        {
            await remove(budget.Token);
            return null;
        }
        catch (Exception e)
        {
            return $"{name} ({e.GetType().Name})";
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        // Avoid giving a helper container the host Docker socket. Delete the owned resources ourselves.
        string runId = Guid.NewGuid().ToString("N");
        string password = NewSqlPassword();

        INetwork network = await RunPhaseAsync(
            BackendFailureKind.Prerequisite,
            "creating the private Docker network (is a Docker engine reachable?)",
            async () =>
            {
                using IDockerClient docker = new DockerClientBuilder().WithEndpoint(DockerEndpoint).Build();
                await docker.System.PingAsync(cancellationToken);
                TestcontainersSettings.ResourceReaperEnabled = false;

                INetwork created = new NetworkBuilder()
                    .WithName($"fhir-backend-contract-{runId}")
                    .WithDockerEndpoint(DockerEndpoint)
                    .WithLogger(NullLogger.Instance)
                    .Build();
                _network = created;
                await created.CreateAsync(cancellationToken);
                return created;
            });

        await RunPhaseAsync(
            BackendFailureKind.Startup,
            "starting SQL Server",
            async () =>
            {
                string name = $"fhir-backend-contract-sql-{runId}";
                IContainer sql = new ContainerBuilder(SqlImage)
                    .WithName(name)
                    .WithDockerEndpoint(DockerEndpoint)
                    .WithNetwork(network)
                    .WithNetworkAliases(SqlAlias)
                    .WithEnvironment("ACCEPT_EULA", "Y")
                    .WithEnvironment("MSSQL_SA_PASSWORD", password)
                    .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(SqlReadyMessage))
                    .WithLogger(NullLogger.Instance)
                    .Build();
                _sqlName = name;
                await sql.StartAsync(cancellationToken);
                return sql;
            });

        string connectionString =
            $"Server={SqlAlias},1433;Initial Catalog=FHIR;Persist Security Info=False;User ID=sa;Password={password};" +
            "MultipleActiveResultSets=False;Connection Timeout=30;TrustServerCertificate=true";

        IContainer fhir = await RunPhaseAsync(
            BackendFailureKind.Startup,
            "starting the FHIR server",
            async () =>
            {
                string name = $"fhir-backend-contract-fhir-{runId}";
                IContainer container = new ContainerBuilder(FhirImage)
                    .WithName(name)
                    .WithDockerEndpoint(DockerEndpoint)
                    .WithNetwork(network)
                    .WithExposedPort(FhirContainerPort)
                    .WithCreateParameterModifier(parameters =>
                    {
                        // Loopback only, Docker picks the host port.
                        parameters.HostConfig ??= new HostConfig();
                        parameters.HostConfig.PortBindings = new Dictionary<string, IList<PortBinding>>
                        {
                            [$"{FhirContainerPort}/tcp"] = [new PortBinding { HostIP = "127.0.0.1", HostPort = string.Empty }],
                        };
                    })
                    .WithEnvironment("DataStore", "SqlServer")
                    .WithEnvironment("SqlServer__ConnectionString", connectionString)
                    .WithEnvironment("SqlServer__AllowDatabaseCreation", "true")
                    .WithEnvironment("SqlServer__Initialize", "true")
                    .WithEnvironment("SqlServer__SchemaOptions__AutomaticUpdatesEnabled", "true")
                    .WithEnvironment("FhirServer__Security__Enabled", "false")
                    // SecurityProvider requires a non-null Authority even with security off. The
                    // value is never contacted because discovery only runs when security is on.
                    .WithEnvironment("FhirServer__Security__Authentication__Authority", "https://security-disabled.invalid")
                    .WithEnvironment("ASPNETCORE_URLS", $"http://+:{FhirContainerPort}")
                    .WithLogger(NullLogger.Instance)
                    .Build();
                _fhirName = name;
                await container.StartAsync(cancellationToken);
                return container;
            });

        LocalBaseUrl = $"http://127.0.0.1:{fhir.GetMappedPublicPort(FhirContainerPort)}/";
        Client = new FhirHttpClient(new Uri(LocalBaseUrl));

        SoftwareIdentity = await WaitForMetadataAsync(fhir, cancellationToken);
        await SeedAsync(cancellationToken);
        await WaitForSeedSearchableAsync(cancellationToken);
    }

    private async Task<string> WaitForMetadataAsync(IContainer fhir, CancellationToken cancellationToken)
    {
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        phase.CancelAfter(FhirReadyBudget);
        try
        {
            while (true)
            {
                if (fhir.State != TestcontainersStates.Running)
                {
                    throw new BackendEvidenceException(
                        BackendFailureKind.Startup,
                        $"the FHIR container stopped before it was ready (state {fhir.State}). Container output is not shown because it can contain the connection string.");
                }

                if (await Client.GetSoftwareIdentityAsync(phase.Token) is { } identity)
                {
                    return identity;
                }

                await Task.Delay(PollInterval, phase.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BackendEvidenceException(
                BackendFailureKind.Startup,
                $"the FHIR server did not answer metadata within {FhirReadyBudget.TotalMinutes:0} minutes.");
        }
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (SeedResource resource in SeedData.Resources(LocalBaseUrl))
        {
            await Client.PutAsync(resource, cancellationToken);
        }
    }

    /// <summary>
    /// Metadata alone says nothing about the search index. This polls an <c>_id</c> search until every
    /// seeded Observation is returned, so a later missing id is a contract result and not lag.
    /// </summary>
    private async Task WaitForSeedSearchableAsync(CancellationToken cancellationToken)
    {
        string query = $"_id={string.Join(',', SeedData.AllObservationIds)}";
        string[] expected = [.. SeedData.AllObservationIds.Order(StringComparer.Ordinal)];

        using var phase = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        phase.CancelAfter(SeedSearchableBudget);
        try
        {
            while (true)
            {
                try
                {
                    SearchsetEvidence found = await Client.SearchObservationsAsync(query, phase.Token);
                    if (found.MatchIds.SequenceEqual(expected))
                    {
                        return;
                    }
                }
                catch (BackendEvidenceException e) when (e.Kind == BackendFailureKind.IncompleteSearch)
                {
                    // Not ready yet; the budget below turns persistent failure into a reported one.
                }

                await Task.Delay(PollInterval, phase.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BackendEvidenceException(
                BackendFailureKind.IncompleteSearch,
                $"the {expected.Length} seeded Observations were not all searchable within {SeedSearchableBudget.TotalSeconds:0} seconds, so no contract can be judged.");
        }
    }

    private static async Task<T> RunPhaseAsync<T>(BackendFailureKind kind, string phase, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new BackendEvidenceException(kind, $"{phase} failed ({e.GetType().Name}). Docker details are not shown because they can contain credentials.");
        }
    }

    private static string NewSqlPassword()
    {
        const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string Lower = "abcdefghijkmnopqrstuvwxyz";
        const string Digits = "23456789";
        const string All = Upper + Lower + Digits;

        // Letters and digits only, so the value is safe inside a connection string. One of each
        // class is guaranteed because SQL Server enforces a complexity policy.
        char[] chars = [.. RandomNumberGenerator.GetItems<char>(All, 28)];
        char[] required =
        [
            RandomNumberGenerator.GetItems<char>(Upper, 1)[0],
            RandomNumberGenerator.GetItems<char>(Lower, 1)[0],
            RandomNumberGenerator.GetItems<char>(Digits, 1)[0],
        ];
        char[] password = [.. chars, .. required];
        RandomNumberGenerator.Shuffle(password.AsSpan());
        return new string(password);
    }
}
