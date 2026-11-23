# Native prototype shards

One foreground command starts two **stock, unmodified** FHIR R4 servers natively on Windows, loads the
committed collision corpus (`test/fixtures/federation-oracle/seeds/collision/{east,west}.json`) and prints
their real base URLs:

| Shard | Server                          | Database                                              |
|-------|---------------------------------|-------------------------------------------------------|
| east  | stock Ignixa R4 (`Ignixa.Web`)  | private LocalDB instance, catalog `Prototype_east`    |
| west  | stock Microsoft FHIR Server R4  | fresh GUID-named catalog on the local SQL Developer instance |

This is a prototype aid for exercising the federation service against two real engines. It is **not** a
deployment, and it makes **no statement about any container image**.

## Prerequisites

- Windows, Python 3 (tested with 3.12), `sqlcmd` and `SqlLocalDB` on `PATH`, the Windows `curl.exe` in `System32`.
- A local SQL Server **Developer** edition instance reachable over shared memory (`lpc:localhost`) with
  your Windows login allowed to create and drop databases.
- A `dotnet.exe` with the runtimes both hosts need (.NET 10 and .NET 11 runtimes).
- A development HTTPS certificate trusted for your user (`dotnet dev-certs https --trust`).
- Built stock artifacts (you build them; this folder does not):
  - Ignixa: `brendankowitz/ignixa-fhir` at `main` commit `2f07ac88d655b3e8e87d41c39f6c2b4039b5a8d7`, SDK
    10.0.401 (roll-forward to a later 10.0 feature band), `dotnet build src/Application/Ignixa.Web -c Release`
    → `…/bin/Release/net10.0/Ignixa.Web.dll`.
  - Microsoft: `microsoft/fhir-server` at `release/5.2.84`, commit
    `2a67597a015b957066822b0d54f2ccc46971c14e`, SDK 11.0.100 (its `global.json`),
    `dotnet build src/Microsoft.Health.Fhir.R4.Web -c Release`
    → `…/bin/Release/net11.0/Microsoft.Health.Fhir.R4.Web.dll`.
  - Neither build command was replayed or captured verbatim for this task; they are the standard commands for
    those commits and are unverified here.

Before it creates anything the launcher checks: Windows, `dotnet.exe`, `sqlcmd`, `SqlLocalDB`, `curl.exe`,
the committed corpus files, that both `.dll`s are built hosts whose runtimes are installed, and (read-only)
that `lpc:localhost` answers and is a Developer edition. A failure there exits `2` with a `prerequisite: …`
message on stderr, having started and created nothing.

Not checked in advance: the Python version, your permission to create and drop databases, and certificate
trust. A refused `create database` fails the launch (exit `1`) and anything already created is removed. An
untrusted development certificate makes every HTTPS probe fail, so the launch fails only after the
300-second startup budget.

## Run

```powershell
python -B deploy\prototype\shards\start.py `
  --dotnet <path-to-dotnet.exe> `
  --ignixa-dll <path-to-Ignixa.Web.dll> `
  --microsoft-dll <path-to-Microsoft.Health.Fhir.R4.Web.dll>
```

Startup takes roughly half a minute. When both shards are up, seeded and verified, exactly one JSON line is
printed to stdout (progress and diagnostics go to stderr):

```json
{"east": "https://localhost:<port>/tenant/1", "west": "https://localhost:<port>"}
```

The process then stays in the foreground. **Ctrl+C or Ctrl+Break** stops both servers and removes every
resource the launcher created. Pressing it during startup does the same for whatever exists by then, and
exits `130`. While cleanup runs, further Ctrl+C/Ctrl+Break presses are ignored so they cannot abandon it.

`--verify` runs the identical launch, seed and live reads, then stops and cleans up. It prints
`{"verified": true}` only after cleanup has finished without a problem; if any resource could not be removed
it prints nothing on stdout and exits `3`.

## If the launcher is killed

Cleanup cannot run if the process is ended forcibly (`taskkill /F`, closing the console window, a crash,
power loss). Then nothing is cleaned up: the two server processes may keep running, and these remain:

- a LocalDB instance `FedProto<12 hex>`: `SqlLocalDB stop <name> -k`, then `SqlLocalDB delete <name>`;
- a catalog `FedProtoWest_<guid>` on `lpc:localhost`: drop it once you know it is yours;
- a `%TEMP%\fed-shards-*` folder.

When a cleanup step fails or an interrupt lands while a resource is being created, the launcher names the
LocalDB instance or catalog it could not remove on stderr, and exits non-zero, so you can finish by hand. A
catalog whose creation was interrupted is named but never dropped, because its identity could not be proven.

## Direct reads

Use the printed bases. The servers present the local ASP.NET development certificate for `localhost`; keep
certificate verification on (`curl.exe` uses the system trust store once `dotnet dev-certs https --trust` has
been run for your user). In Windows PowerShell `curl` is an alias for `Invoke-WebRequest`; call `curl.exe`:

```powershell
curl.exe --noproxy "*" <east>/Patient/dup                 # east: Eli
curl.exe --noproxy "*" <west>/Patient/dup                 # west: Gus, same id, different resource
curl.exe --noproxy "*" <west>/Observation/obs-dup-xref    # subject -> <east>/Patient/dup
curl.exe --noproxy "*" <east>/metadata                    # R4 CapabilityStatement
```

The west `obs-dup-xref` subject is the only value rewritten from the committed corpus: its reserved
placeholder base `https://fhir-east.example.org/` becomes the real east base. Nothing else is altered, and
any other placeholder host fails the launch.

## Intentional constraints

- Loopback HTTPS only; every launcher request disables proxies, allows only `https`, is time-bounded, and
  **never disables TLS verification** (no `-k`, no `TrustServerCertificate`).
- SQL traffic stays on local named pipe / shared memory. Because those transports never leave the machine,
  the connection strings use `Encrypt=false` (LocalDB) and `Encrypt=Optional` (Developer instance). That is a
  transport statement, not a certificate-validation bypass.
- The Developer instance is touched only through `lpc:localhost` and only for one new catalog named
  `FedProtoWest_<guid>`. Its `database_guid` is recorded at creation, and cleanup drops the catalog only if
  the GUID still matches. A missing catalog is reported and ignored; a replaced one is refused and left in
  place. No other database is read or written.
- The east database lives on a private `FedProto<hex>` LocalDB instance created and deleted by the launcher.
- Children receive an allow-listed environment (no inherited host overrides, credentials or proxies) and a
  fresh temporary working folder; their authority is an unroutable `.invalid` placeholder.
- Ignixa runs with Reindex disabled, as the stock build requires on this setup.
- No Docker, no network beyond loopback, nothing remote, no changes to any service configuration.

Exit codes: `0` normal stop or `--verify` success (after cleanup), `1` launch failure or a child dying after
ready, `2` prerequisite not met, `3` run completed but a cleanup step needs attention (`--verify` prints no
success line), `130` interrupted during startup.

## Tests

`test_start.py` holds unit-level checks (always run) and a native acceptance class that is skipped unless all
three variables are set. The acceptance tests use an independent `curl` witness to read the shards, then
check teardown against canary LocalDB instances and Developer catalogs.

```powershell
$env:FED_PROTO_DOTNET = "<path-to-dotnet.exe>"
$env:FED_PROTO_IGNIXA_DLL = "<path-to-Ignixa.Web.dll>"
$env:FED_PROTO_MICROSOFT_DLL = "<path-to-Microsoft.Health.Fhir.R4.Web.dll>"
python -B -m unittest discover -s deploy/prototype/shards -v
```

The full native suite takes about three minutes.
