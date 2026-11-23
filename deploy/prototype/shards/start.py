"""Start two stock FHIR R4 shards natively and load the committed collision corpus.

east  stock Ignixa on a private LocalDB instance and catalog
west  stock Microsoft FHIR Server on a fresh, GUID-guarded catalog on the local
      SQL Server Developer instance, reached only over shared memory (lpc:)

One JSON line on stdout, ``{"east": ..., "west": ...}``, once both shards are
live and verified. Diagnostics go to stderr. Ctrl+C stops everything and
removes every resource this run created. See README.md for constraints.
"""

from __future__ import annotations

import argparse
import contextlib
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import uuid
from dataclasses import dataclass
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
CORPUS_DIR = REPO_ROOT / "test" / "fixtures" / "federation-oracle" / "seeds" / "collision"
EAST_PLACEHOLDER = "https://fhir-east.example.org/"

DEVELOPER_SERVER = "lpc:localhost"
STARTUP_BUDGET_SECONDS = 300
CHILD_STOP_SECONDS = 15

# Only these Windows variables reach children: no host overrides, credentials or proxies.
INHERITED_ENVIRONMENT = frozenset({
    "PATH", "SYSTEMROOT", "WINDIR", "SYSTEMDRIVE", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "PROGRAMDATA",
    "PROGRAMFILES", "PROGRAMFILES(X86)", "TEMP", "TMP", "COMSPEC", "COMPUTERNAME"})

IGNIXA_DLL_NAME = "Ignixa.Web.dll"
MICROSOFT_DLL_NAME = "Microsoft.Health.Fhir.R4.Web.dll"


class PrerequisiteError(Exception):
    """A missing or invalid prerequisite, found before any resource was created."""


class LaunchError(Exception):
    """The shards could not be started, seeded or verified. The message is safe to print."""


class CleanupError(Exception):
    """A resource this run created could not be removed, or was no longer ours to remove."""


def log(message: str) -> None:
    print(message, file=sys.stderr, flush=True)


def child_environment() -> dict[str, str]:
    return {k: v for k, v in os.environ.items() if k.upper() in INHERITED_ENVIRONMENT}


_cleanup_running = False


def run_tool(argv: list[str], *, timeout: int, stdin: bytes | None = None) -> subprocess.CompletedProcess[bytes]:
    """Run a helper tool. During cleanup (see ``interrupts_deferred``) the tool gets no console of its own to share.

    A console Ctrl+C / Ctrl+Break reaches every process attached to the launcher's console, so a cleanup tool left on
    it would be killed by the very interrupt cleanup is shielding itself from. CREATE_NO_WINDOW starts it on a hidden
    console instead. Startup tools keep the shared console and stay interruptible.
    """
    flags = subprocess.CREATE_NO_WINDOW if _cleanup_running else 0
    try:
        return subprocess.run(argv, input=stdin, capture_output=True, timeout=timeout, env=child_environment(),
                              creationflags=flags)
    except subprocess.TimeoutExpired:
        raise LaunchError(f"{Path(argv[0]).name} did not finish within {timeout} seconds") from None
    except OSError:
        raise LaunchError(f"{Path(argv[0]).name} could not be run") from None


# --- prerequisites ---------------------------------------------------------------------------------


@dataclass(frozen=True)
class Prerequisites:
    dotnet: Path
    ignixa_dll: Path
    microsoft_dll: Path
    curl: Path


def installed_runtimes(dotnet: Path) -> set[tuple[str, tuple[int, ...]]]:
    done = run_tool([str(dotnet), "--list-runtimes"], timeout=30)
    runtimes = set()
    for line in done.stdout.decode(errors="replace").splitlines():
        name, _, rest = line.partition(" ")
        version = rest.split(" ", 1)[0]
        if re.fullmatch(r"\d+(\.\d+){2}", version):
            runtimes.add((name, tuple(int(p) for p in version.split("."))))
    return runtimes


def require_artifact(path: Path, name: str, runtimes: set[tuple[str, tuple[int, ...]]]) -> Path:
    path = path.resolve()
    if path.name != name or not path.is_file():
        raise PrerequisiteError(f"{name} not found at the given path")
    config = path.with_name(path.stem + ".runtimeconfig.json")
    if not config.is_file() or not path.with_name(path.stem + ".deps.json").is_file():
        raise PrerequisiteError(f"{name} is not a built host (missing runtimeconfig or deps beside it)")
    try:
        frameworks = [(f["name"], tuple(int(p) for p in f["version"].split(".")))
                      for f in json.loads(config.read_text(encoding="utf-8"))["runtimeOptions"]["frameworks"]]
    except (ValueError, KeyError, TypeError):
        raise PrerequisiteError(f"{name} has an unreadable runtimeconfig") from None
    for framework, needed in frameworks:
        if not any(n == framework and v[0] == needed[0] and v >= needed for n, v in runtimes):
            version = ".".join(map(str, needed))
            raise PrerequisiteError(f"{name} needs {framework} {version} or a later patch of that major version")
    return path


def require_developer_edition() -> None:
    """Read-only proof, before anything is created, that the local Developer instance answers over shared memory."""
    try:
        edition = sql(DEVELOPER_SERVER, "master", "select convert(nvarchar(96), serverproperty('Edition'))")
    except LaunchError:
        raise PrerequisiteError(f"cannot reach the local SQL Server instance at {DEVELOPER_SERVER} "
                                "with Windows authentication") from None
    if "Developer" not in edition:
        raise PrerequisiteError("the local SQL Server instance is not a Developer edition")


def check_prerequisites(args: argparse.Namespace) -> Prerequisites:
    if os.name != "nt":
        raise PrerequisiteError("this launcher is Windows only (integrated authentication, LocalDB)")
    dotnet = Path(args.dotnet)
    if dotnet.name.lower() != "dotnet.exe" or not dotnet.is_file():
        raise PrerequisiteError("--dotnet must be the path of an existing dotnet.exe")
    for tool in ("sqlcmd", "SqlLocalDB"):
        if not shutil.which(tool):
            raise PrerequisiteError(f"{tool} is not on PATH")
    curl = Path(os.environ.get("SYSTEMROOT", r"C:\Windows")) / "System32" / "curl.exe"
    if not curl.is_file():
        raise PrerequisiteError("System32 curl.exe is missing")
    for side in ("east", "west"):
        if not (CORPUS_DIR / f"{side}.json").is_file():
            raise PrerequisiteError(f"committed corpus file {side}.json is missing")
    runtimes = installed_runtimes(dotnet)
    prerequisites = Prerequisites(
        dotnet=dotnet,
        ignixa_dll=require_artifact(Path(args.ignixa_dll), IGNIXA_DLL_NAME, runtimes),
        microsoft_dll=require_artifact(Path(args.microsoft_dll), MICROSOFT_DLL_NAME, runtimes),
        curl=curl)
    require_developer_edition()
    return prerequisites


# --- corpus ----------------------------------------------------------------------------------------


def collision_corpus(east_base: str) -> tuple[list[dict], list[dict]]:
    """The committed east and west resources, Patients first, with the one synthetic reference rewritten.

    The only change is the west ``obs-dup-xref`` subject: its reserved placeholder base becomes the real east base.
    Anything else that still carries the placeholder fails the load.
    """
    sides = []
    for side in ("east", "west"):
        resources = json.loads((CORPUS_DIR / f"{side}.json").read_text(encoding="utf-8"))
        for resource in resources:
            if side == "west" and resource["id"] == "obs-dup-xref":
                reference = resource["subject"]["reference"]
                if not reference.startswith(EAST_PLACEHOLDER):
                    raise LaunchError("the committed corpus no longer has the expected cross-shard reference")
                resource["subject"]["reference"] = east_base + "/" + reference[len(EAST_PLACEHOLDER):]
            if "example.org/" in json.dumps(resource).replace("https://example.org/fhir/CodeSystem/", ""):
                raise LaunchError("the committed corpus has an unexpected placeholder reference")
        sides.append(sorted(resources, key=lambda r: r["resourceType"] != "Patient"))
    return sides[0], sides[1]


# --- HTTP ------------------------------------------------------------------------------------------


def json_object(payload: bytes) -> dict | None:
    """The body as a JSON object; None when it is empty or anything else, such as a plain-text startup error."""
    try:
        parsed = json.loads(payload) if payload.strip() else None
    except ValueError:  # includes undecodable bytes
        return None
    return parsed if isinstance(parsed, dict) else None


class Endpoint:
    """A shard's loopback HTTPS base; every request is bounded and verified against the system trust store."""

    def __init__(self, curl: Path, base: str) -> None:
        if not re.fullmatch(r"https://localhost:[1-9]\d{0,4}(/tenant/1)?", base):
            raise ValueError("expected a loopback shard base")
        self.curl, self.base = curl, base

    def request(self, path: str, body: dict | None = None, *, url: str | None = None) -> tuple[int, dict | None]:
        target = url or self.base + path
        if not target.startswith(self.base + "/"):
            raise LaunchError("refusing to contact an endpoint outside this shard")
        argv = [str(self.curl), "-q", "--silent", "--show-error", "--noproxy", "*", "--proto", "=https",
                "--max-time", "60", "--max-filesize", str(16 << 20), "-H", "Accept: application/fhir+json",
                "-w", "\n%{http_code}"]
        if body is not None:
            argv += ["-X", "PUT", "-H", "Content-Type: application/fhir+json", "--data-binary", "@-"]
        done = run_tool(argv + [target], timeout=75, stdin=json.dumps(body).encode() if body is not None else None)
        if done.returncode:
            raise LaunchError(f"HTTPS request failed (curl exit {done.returncode})")
        payload, _, status = done.stdout.rpartition(b"\n")
        try:
            code = int(status)
        except ValueError:
            raise LaunchError("the HTTPS response had no readable status") from None
        return code, json_object(payload)

    def get_resource(self, resource_type: str, resource_id: str, *, url: str | None = None) -> dict:
        status, resource = self.request(f"/{resource_type}/{resource_id}", url=url)
        if status != 200 or not resource or (resource.get("resourceType"), resource.get("id")) != (resource_type, resource_id):
            raise LaunchError(f"GET {resource_type}/{resource_id} did not return that resource (HTTP {status})")
        return resource

    def metadata(self) -> dict | None:
        status, statement = self.request("/metadata")
        if status == 200 and statement and statement.get("resourceType") == "CapabilityStatement":
            return statement
        return None

    def put(self, resource: dict) -> None:
        path = f"/{resource['resourceType']}/{resource['id']}"
        status, outcome = self.request(path, resource)
        if status not in (200, 201):
            codes = [i.get("code") for i in (outcome or {}).get("issue", []) if isinstance(i, dict)]
            raise LaunchError(f"PUT {path} was refused (HTTP {status}, issue codes {codes})")


# --- SQL -------------------------------------------------------------------------------------------


def sql(server: str, database: str, query: str) -> str:
    if not re.fullmatch(rf"{re.escape(DEVELOPER_SERVER)}|\(localdb\)\\FedProto[0-9a-f]{{12}}", server):
        raise ValueError("unexpected SQL server")
    done = run_tool(["sqlcmd", "-S", server, "-d", database, "-E", "-b", "-l", "15", "-t", "30", "-h", "-1", "-W",
                     "-Q", "set nocount on; " + query], timeout=60)
    if done.returncode:
        raise LaunchError("SQL Server did not complete the requested operation")
    return done.stdout.decode(errors="strict").strip()


@contextlib.contextmanager
def private_localdb(workdir: Path):
    """A uniquely named LocalDB instance holding the east catalog; both are discarded on exit.

    The instance is discarded even when an interrupt or failure struck while it was being created.
    """
    name = "FedProto" + uuid.uuid4().hex[:12]
    if name in localdb_names():
        raise LaunchError("LocalDB instance name collision")
    server = "(localdb)\\" + name
    try:
        run_checked(["SqlLocalDB", "create", name, "15.0", "-s"], f"could not create the private LocalDB instance {name}")
        mdf = str(workdir / "east").replace("'", "''")
        sql(server, "master", f"create database [Prototype_east] on primary (name='Prototype_east', filename=N'{mdf}.mdf') "
                              f"log on (name='Prototype_east_log', filename=N'{mdf}_log.ldf')")
        yield server
    finally:
        # This also runs while the first interrupt is still propagating out of a partly finished creation.
        with interrupts_deferred():
            discard_localdb(name)


def localdb_names() -> set[str]:
    return set(run_tool(["SqlLocalDB", "info"], timeout=30).stdout.decode().split())


def discard_localdb(name: str) -> None:
    """Stop and delete the instance if it exists; a failure or interrupt during creation may have left none."""
    try:
        if name not in localdb_names():
            return
    except LaunchError as error:
        raise CleanupError(f"private LocalDB instance {name} may remain: {error}") from None
    try:
        run_checked(["SqlLocalDB", "stop", name, "-k"], f"could not stop the private LocalDB instance {name}", cleanup=True)
    finally:
        run_checked(["SqlLocalDB", "delete", name], f"could not delete the private LocalDB instance {name}", cleanup=True)


def run_checked(argv: list[str], failure: str, *, cleanup: bool = False) -> None:
    error = CleanupError if cleanup else LaunchError
    try:
        done = run_tool(argv, timeout=90)
    except LaunchError as timed_out:
        raise error(f"{failure}: {timed_out}") from None
    if done.returncode:
        raise error(failure)


@contextlib.contextmanager
def west_catalog():
    """Create a fresh catalog on the Developer instance; drop it only if it is still the catalog created here.

    Raises CleanupError, leaving any replacement untouched, when the name now belongs to a different database,
    and when the catalog cannot be removed; the message always names the catalog.
    """
    name = "FedProtoWest_" + uuid.uuid4().hex
    guid_of = ("select isnull((select convert(varchar(36), database_guid) from sys.database_recovery_status "
               f"where database_id=db_id(N'{name}')), '')")
    try:
        marker = sql(DEVELOPER_SERVER, "master",
                     f"if db_id(N'{name}') is not null throw 50001, 'Name already in use', 1; create database [{name}]; {guid_of}")
    except BaseException:
        # An interrupt or timeout can land after the server created it; without its GUID it is not safe to drop.
        log(f"west catalog {name} may exist because its creation did not finish; if it does, remove it manually")
        raise
    if not re.fullmatch(r"[0-9A-Fa-f-]{36}", marker):
        raise LaunchError(f"could not identify the new catalog; it may need manual removal: {name}")
    try:
        yield name
    finally:
        try:
            current = sql(DEVELOPER_SERVER, "master", guid_of)
            if current == "":
                log(f"west catalog {name} was already removed")
            elif current != marker:
                raise CleanupError(f"west catalog {name} was replaced by another database; left in place")
            else:
                sql(DEVELOPER_SERVER, "master",
                    f"if not exists (select 1 from sys.database_recovery_status where database_id=db_id(N'{name}') "
                    f"and database_guid='{marker}') throw 50001, 'Catalog no longer matches', 1; "
                    f"alter database [{name}] set single_user with rollback immediate; drop database [{name}];")
        except LaunchError as error:
            raise CleanupError(f"west catalog {name} could not be removed ({error}); remove it manually") from None


# --- children --------------------------------------------------------------------------------------


@dataclass
class Child:
    label: str
    process: subprocess.Popen
    log_path: Path

    def exception_types(self) -> list[str]:
        text = self.log_path.read_text(encoding="utf-8", errors="replace")
        return sorted(set(re.findall(r"\b(?:[A-Za-z]+\.)+([A-Za-z]+Exception)\b", text)))

    def stop(self) -> None:
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=CHILD_STOP_SECONDS)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=CHILD_STOP_SECONDS)


def start_child(label: str, argv: list[str], env: dict[str, str], folder: Path) -> Child:
    log_path = folder / "host.log"
    try:
        with log_path.open("wb") as stream:
            # A new process group keeps Ctrl+C aimed at the launcher; children are stopped through their handles.
            process = subprocess.Popen(argv, cwd=folder, env=env, stdout=stream, stderr=subprocess.STDOUT,
                                       creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
    except OSError:
        raise LaunchError(f"{label} could not be started") from None
    return Child(label, process, log_path)


def await_ready(child: Child, curl: Path, path_suffix: str, deadline: float) -> Endpoint:
    """Wait for the child's loopback port, then for a real CapabilityStatement."""
    listening = re.compile(r"Now listening on: https://127\.0\.0\.1:(\d+)")
    while time.monotonic() < deadline:
        if child.process.poll() is not None:
            raise LaunchError(f"{child.label} exited with code {child.process.returncode} before it was ready "
                              f"(exception types: {child.exception_types()})")
        match = listening.search(child.log_path.read_text(encoding="utf-8", errors="replace"))
        if match:
            try:
                endpoint = Endpoint(curl, f"https://localhost:{match[1]}{path_suffix}")
            except ValueError:
                raise LaunchError(f"{child.label} reported a listening port that is not a loopback port") from None
            try:
                statement = endpoint.metadata()
            except LaunchError:
                statement = None  # the host may still be binding or migrating, or not answering HTTP yet
            if statement:
                if not str(statement.get("fhirVersion", "")).startswith("4.0"):
                    raise LaunchError(f"{child.label} is not an R4 server")
                return endpoint
        time.sleep(1)
    raise LaunchError(f"{child.label} was not ready within {STARTUP_BUDGET_SECONDS} seconds")


def ignixa_command(prereq: Prerequisites, east_server: str, folder: Path) -> tuple[list[str], dict[str, str]]:
    tenants = [{"TenantId": i, "IsSystemPartition": i == 0, "DisplayName": f"Prototype-{i}", "IsActive": True,
                "FhirVersion": "4.0", "Storage": {"Type": "FileSystem", "BaseDirectory": f"tenant-{i}"},
                "Packages": {"EnableAutoLoad": False, "PreloadPackages": []}} for i in (0, 1)]
    tenants[0]["Storage"] = {"Type": "SqlServer", "InheritConnectionStringFromTenant": 1}
    tenants[1]["Storage"] = {"Type": "SqlServer", "ConnectionString":
                             f"Server={east_server};Database=Prototype_east;Integrated Security=true;"
                             "Encrypt=false;Connect Timeout=10"}
    settings = {
        "AllowedHosts": "localhost;127.0.0.1", "Tenants": {"Mode": "Isolated", "Configurations": tenants},
        "FhirRepository": {"BaseDirectory": str(folder / "data")},
        "BlobStorage": {"Provider": "Local", "RootDirectory": str(folder / "blobs")},
        "DurableTask": {"Provider": "FileSystem"}, "TransactionWatcher": {"Enabled": False},
        "Authorization": {"Enabled": False}, "Sidecar": {"Enabled": False}, "Import": {"Enabled": False},
        "OpenIddict": {"Enabled": False}, "Reindex": {"Enabled": False},
        "SqlServer": {"AutomaticSchemaDeploymentEnabled": True},
        "Experimental": {"Enabled": False, "Features": {"Mcp": {"Enabled": False}, "Terminology": {"EnableAutoImport": False}}},
    }
    (folder / "appsettings.json").write_text(json.dumps(settings), encoding="utf-8")
    env = child_environment() | {"DOTNET_ROOT": str(prereq.dotnet.parent), "DOTNET_ENVIRONMENT": "Testing"}
    return [str(prereq.dotnet), str(prereq.ignixa_dll), "--urls", "https://127.0.0.1:0", "--contentRoot", str(folder)], env


def microsoft_command(prereq: Prerequisites, catalog: str, folder: Path) -> tuple[list[str], dict[str, str]]:
    env = child_environment() | {
        "DOTNET_ROOT": str(prereq.dotnet.parent), "ASPNETCORE_ENVIRONMENT": "Production",
        "ASPNETCORE_URLS": "https://127.0.0.1:0", "DataStore": "SqlServer",
        "SqlServer__ConnectionString": f"Server={DEVELOPER_SERVER};Database={catalog};Integrated Security=true;"
                                       "Encrypt=Optional;Connect Timeout=8;Application Name=FederationPrototypeShard",
        "SqlServer__AllowDatabaseCreation": "false", "SqlServer__Initialize": "true",
        "SqlServer__SchemaOptions__AutomaticUpdatesEnabled": "true",
        "FhirServer__Security__Enabled": "false",
        # Required to be non-null even with security disabled; never contacted.
        "FhirServer__Security__Authentication__Authority": "https://prototype-security-disabled.invalid",
        "Logging__LogLevel__Microsoft.Hosting.Lifetime": "Information"}
    return [str(prereq.dotnet), str(prereq.microsoft_dll), "--urls", env["ASPNETCORE_URLS"]], env


# --- the run ---------------------------------------------------------------------------------------


def verify(east: Endpoint, west: Endpoint) -> None:
    """Read the loaded corpus back: literal Eli/Gus, and the cross-shard reference and its target."""
    given = lambda patient: patient["name"][0]["given"]
    if given(east.get_resource("Patient", "dup")) != ["Eli"] or given(west.get_resource("Patient", "dup")) != ["Gus"]:
        raise LaunchError("Patient/dup does not hold Eli on east and Gus on west")
    xref = west.get_resource("Observation", "obs-dup-xref")["subject"]["reference"]
    if xref != east.base + "/Patient/dup":
        raise LaunchError("west obs-dup-xref does not reference the east Patient/dup")
    if given(east.get_resource("Patient", "dup", url=xref)) != ["Eli"]:
        raise LaunchError("the cross-shard reference does not resolve to the east Patient/dup")


def guarded(problems: list[str], label: str, action, *args) -> None:
    try:
        action(*args)
    except Exception as error:  # cleanup must continue; the failure is reported, never swallowed
        problems.append(f"cleanup: {label}: {error}")


def remove_tree(path: Path) -> None:
    for attempt in range(5):
        try:
            shutil.rmtree(path)
            return
        except FileNotFoundError:
            return
        except OSError:
            if attempt == 4:
                raise
            time.sleep(1)


def serve(prereq: Prerequisites, verify_only: bool, problems: list[str]) -> int:
    """Launch, seed and read; in run mode print the bases and wait. Everything created is removed before returning.

    Returns 0 after a verified ``--verify`` pass or a Ctrl+C stop. Cleanup shields itself from further interrupts so a
    second Ctrl+C cannot abandon it half way; failures are appended to ``problems``.
    """
    stack = contextlib.ExitStack()
    try:
        return launch(stack, prereq, verify_only, problems)
    finally:
        with interrupts_deferred():
            stack.close()


@contextlib.contextmanager
def interrupts_deferred():
    """Shield cleanup from Ctrl+C and Ctrl+Break: ignore them here and run cleanup tools off the shared console.

    Both halves are needed; ignoring the signals only protects this Python process, not the tools it spawns.
    Re-entrant: an inner scope restores whatever the outer one set.
    """
    global _cleanup_running
    signals = (signal.SIGINT, signal.SIGBREAK)
    saved = [signal.signal(s, signal.SIG_IGN) for s in signals]
    was_running, _cleanup_running = _cleanup_running, True
    try:
        yield
    finally:
        _cleanup_running = was_running
        for s, handler in zip(signals, saved):
            signal.signal(s, handler)


def launch(stack: contextlib.ExitStack, prereq: Prerequisites, verify_only: bool, problems: list[str]) -> int:
    workdir = Path(tempfile.mkdtemp(prefix="fed-shards-"))
    stack.callback(guarded, problems, "working directory", remove_tree, workdir)
    east_folder, west_folder = workdir / "east", workdir / "west"
    east_folder.mkdir(), west_folder.mkdir()

    east_server = stack.enter_context(guarded_context(problems, "east LocalDB", private_localdb(workdir)))
    catalog = stack.enter_context(guarded_context(problems, "west catalog", west_catalog()))

    children = []
    for label, folder, (argv, env) in (
            ("east (Ignixa)", east_folder, ignixa_command(prereq, east_server, east_folder)),
            ("west (Microsoft)", west_folder, microsoft_command(prereq, catalog, west_folder))):
        child = start_child(label, argv, env, folder)
        stack.callback(guarded, problems, f"stop {label}", child.stop)
        children.append(child)

    deadline = time.monotonic() + STARTUP_BUDGET_SECONDS
    east = await_ready(children[0], prereq.curl, "/tenant/1", deadline)
    west = await_ready(children[1], prereq.curl, "", deadline)
    log("both shards are serving R4 metadata; loading the collision corpus")

    east_resources, west_resources = collision_corpus(east.base)
    for endpoint, resources in ((east, east_resources), (west, west_resources)):
        for resource in resources:
            endpoint.put(resource)
    verify(east, west)

    if verify_only:
        return 0  # main reports success once cleanup has finished and succeeded
    print(json.dumps({"east": east.base, "west": west.base}), flush=True)
    log("serving; press Ctrl+C to stop and clean up")
    try:
        while True:
            for child in children:
                if child.process.poll() is not None:
                    raise LaunchError(f"{child.label} exited with code {child.process.returncode} after it was ready")
            time.sleep(1)
    except KeyboardInterrupt:
        log("interrupted; stopping")
        return 0


@contextlib.contextmanager
def guarded_context(problems: list[str], label: str, manager):
    """Enter a resource manager whose exit failure is recorded rather than masking the run's own outcome."""
    value = manager.__enter__()
    try:
        yield value
    finally:
        try:
            manager.__exit__(None, None, None)
        except Exception as error:
            problems.append(f"cleanup: {label}: {error}")


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--ignixa-dll", required=True, help="path to the built stock Ignixa.Web.dll")
    parser.add_argument("--microsoft-dll", required=True, help="path to the built stock Microsoft.Health.Fhir.R4.Web.dll")
    parser.add_argument("--dotnet", required=True, help="path to dotnet.exe with the runtimes both hosts need")
    parser.add_argument("--verify", action="store_true",
                        help="launch, seed and read exactly as normal, then stop and clean up; print {\"verified\": true}")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    signal.signal(signal.SIGBREAK, signal.default_int_handler)
    problems: list[str] = []
    try:
        prerequisites = check_prerequisites(args)
        status = serve(prerequisites, args.verify, problems)
    except PrerequisiteError as error:
        print(f"prerequisite: {error}", file=sys.stderr, flush=True)
        return 2
    except KeyboardInterrupt:
        status = 130
    except (LaunchError, CleanupError) as error:
        log(f"error: {error}")
        status = 1
    for problem in problems:
        log(problem)
    if args.verify and status == 0 and not problems:
        log("verified: Eli on east, Gus on west, cross-shard reference resolves; cleanup complete")
        print(json.dumps({"verified": True}), flush=True)
    return status if not problems else (status or 3)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
