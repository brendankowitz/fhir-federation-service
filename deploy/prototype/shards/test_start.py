"""Behavioural witness for the native prototype shard launcher.

Default run: every test is mocked or stubbed. It needs only Python and never
starts SqlLocalDB or sqlcmd, never reads or changes SQL Server or LocalDB state,
and never starts a shard host. Cases that would are skipped, visibly.

Opted-in run: setting all three variables below enables every case that touches
real SQL Server or LocalDB (the prerequisite and catalog-guard cases, the real
partial-enter case, and the native acceptance tests). Those need Windows, a local
SQL Server Developer instance at lpc:localhost, SqlLocalDB (version 15.0) and
sqlcmd on PATH, plus caller-built stock artifacts:

    FED_PROTO_DOTNET            path to a dotnet.exe that has the needed runtimes
    FED_PROTO_IGNIXA_DLL        path to Ignixa.Web.dll
    FED_PROTO_MICROSOFT_DLL     path to Microsoft.Health.Fhir.R4.Web.dll

The native tests are an independent witness: they drive start.py as a black box
and read the shards with their own HTTP client, never through the launcher's.

    python -B -m unittest discover -s deploy/prototype/shards -v
"""

import contextlib
import io
import json
import os
import queue
import re
import shlex
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import time
import types
import unittest
import uuid
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

START = HERE / "start.py"
CURL = Path(os.environ.get("SYSTEMROOT", r"C:\Windows")) / "System32" / "curl.exe"
DEVELOPER = "lpc:localhost"

DOTNET = os.environ.get("FED_PROTO_DOTNET")
IGNIXA_DLL = os.environ.get("FED_PROTO_IGNIXA_DLL")
MICROSOFT_DLL = os.environ.get("FED_PROTO_MICROSOFT_DLL")
NATIVE = all((DOTNET, IGNIXA_DLL, MICROSOFT_DLL))
NATIVE_SKIP = ("opt-in: touches real SQL Server/LocalDB; set FED_PROTO_DOTNET, "
               "FED_PROTO_IGNIXA_DLL and FED_PROTO_MICROSOFT_DLL")

EAST_BASE = r"https://localhost:[1-9]\d{0,4}/tenant/1"
WEST_BASE = r"https://localhost:[1-9]\d{0,4}"


def installed_dotnet():
    return Path(DOTNET or shutil.which("dotnet") or "dotnet.exe")


def launcher_args(*extra, ignixa=None, microsoft=None, dotnet=None):
    return [sys.executable, "-B", str(START),
            "--ignixa-dll", str(ignixa or IGNIXA_DLL),
            "--microsoft-dll", str(microsoft or MICROSOFT_DLL),
            "--dotnet", str(dotnet or DOTNET), *extra]


def sql(server, database, query):
    done = subprocess.run(
        ["sqlcmd", "-S", server, "-d", database, "-E", "-b", "-l", "15", "-t", "30", "-h", "-1", "-W",
         "-Q", "set nocount on; " + query], capture_output=True, timeout=60)
    if done.returncode:
        raise AssertionError("sqlcmd failed against " + server)
    return done.stdout.decode().strip()


OWNED_PREFIXES = ("FedProto", "FedCanary")  # names this launcher and these tests create; the machine is shared


def owned(names):
    return sorted(n for n in names if n.startswith(OWNED_PREFIXES))


def localdb_instances():
    done = subprocess.run(["SqlLocalDB", "info"], capture_output=True, text=True, timeout=30, check=True)
    return owned(done.stdout.split())


def developer_databases():
    return owned(sql(DEVELOPER, "master", "select name from sys.databases").split())


def database_guid(name):
    return sql(DEVELOPER, "master",
               f"select convert(varchar(36),database_guid) from sys.database_recovery_status where database_id=db_id(N'{name}')")


def curl(url):
    """Independent witness client: stock curl on the Windows trust store, no verification bypass."""
    done = subprocess.run(
        [str(CURL), "-q", "--silent", "--show-error", "--noproxy", "*", "--proto", "=https", "--max-time", "30",
         "-H", "Accept: application/fhir+json", "-w", "\n%{http_code}", url], capture_output=True, timeout=45)
    if done.returncode:
        return None, None
    body, status = done.stdout.rsplit(b"\n", 1)
    return int(status), json.loads(body) if body else None


def read_ok(url):
    status, resource = curl(url)
    assert status == 200, f"GET {url} -> {status}"
    return resource


def dotnet_processes():
    """Every dotnet.exe on the machine as {ProcessId, ParentProcessId, CommandLine} rows; selection happens in owned_hosts."""
    script = ("ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" | "
              "Select-Object ProcessId,ParentProcessId,CommandLine)")
    done = subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True, timeout=60)
    rows = json.loads(done.stdout)
    return [rows] if isinstance(rows, dict) else rows


def owned_hosts(rows, dll, parent_pid):
    """Pids of the direct children of ``parent_pid`` whose entry assembly (argv[1]) is exactly ``dll``.

    Neither a substring of some other command line nor a host of the same dll under another parent qualifies,
    so a test can never select (and then kill) a process that is not its own launcher's child.
    """
    wanted = os.path.normcase(os.path.abspath(dll))
    pids = []
    for row in rows:
        if row["ParentProcessId"] != parent_pid:
            continue
        try:
            argv = [token.strip('"') for token in shlex.split(row["CommandLine"] or "", posix=False)]
        except ValueError:
            continue
        if len(argv) > 1 and os.path.normcase(os.path.abspath(argv[1])) == wanted:
            pids.append(row["ProcessId"])
    return pids


@contextlib.contextmanager
def preserved_signals():
    saved = {s: signal.getsignal(s) for s in (signal.SIGINT, signal.SIGBREAK)}
    try:
        yield
    finally:
        for sig, handler in saved.items():
            signal.signal(sig, handler)


# Allow in-flight verification (4 x 75s), two host stops (2 x 2 x 15s), west probe/drop (2 x 60s),
# east list/stop/delete (30 + 90 + 90s), directory retries (4s), and process overhead.
LAUNCHER_STOP_SECONDS = 750


def stop_launcher(proc):
    """Request shutdown and return the exit code, forcing only the owned launcher after the full grace period."""
    status = proc.poll()
    if status is not None:
        return status
    signal_failure = None
    try:
        proc.send_signal(signal.CTRL_BREAK_EVENT)
    except OSError as error:
        # A failed signal does not tell us whether the launcher is already cleaning up.
        signal_failure = error
    try:
        return proc.wait(timeout=LAUNCHER_STOP_SECONDS)
    except subprocess.TimeoutExpired as timeout:
        status = proc.poll()
        if status is not None:
            return status
        try:
            proc.kill()
            proc.wait(timeout=30)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise RuntimeError(f"could not force-stop launcher {proc.pid}; shard cleanup may be incomplete") from error
        raise RuntimeError(f"launcher {proc.pid} was force-stopped; shard cleanup may be incomplete") from (signal_failure or timeout)


def run_launcher_verify(timeout=900):
    """Run the resource-owning verify mode without pipe backpressure or timeout-induced automatic kills."""
    args = launcher_args("--verify")
    with tempfile.TemporaryFile(mode="w+t") as stdout, tempfile.TemporaryFile(mode="w+t") as stderr:
        proc = subprocess.Popen(args, stdout=stdout, stderr=stderr, creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
        try:
            try:
                status = proc.wait(timeout=timeout)
            finally:
                stop_launcher(proc)
        except subprocess.TimeoutExpired as error:
            stdout.seek(0)
            stderr.seek(0)
            error.output, error.stderr = stdout.read(), stderr.read()
            raise
        stdout.seek(0)
        stderr.seek(0)
        return subprocess.CompletedProcess(args, status, stdout.read(), stderr.read())


class RunningLauncher:
    """start.py in run mode, in its own process group so the test can send it Ctrl+Break."""

    def __init__(self):
        self.proc = None
        self._stderr = tempfile.TemporaryFile()
        try:
            self.proc = subprocess.Popen(launcher_args(), stdout=subprocess.PIPE, stderr=self._stderr, text=True,
                                         creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
            first = queue.Queue()
            threading.Thread(target=lambda: first.put(self.proc.stdout.readline()), daemon=True).start()
            try:
                self.line = first.get(timeout=600)
            except queue.Empty:
                self.line = ""
            self.endpoints = json.loads(self.line) if self.line else None
            if not self.line:
                stop_launcher(self.proc)  # the caller reads stderr for the reason, then close()s
        except BaseException:
            self.close()
            raise

    def stderr(self):
        self._stderr.seek(0)
        return self._stderr.read().decode(errors="replace")

    def interrupt(self):
        return stop_launcher(self.proc)

    def wait(self, timeout=LAUNCHER_STOP_SECONDS):
        try:
            return self.proc.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            stop_launcher(self.proc)
            raise

    def close(self):
        try:
            if self.proc is not None:
                stop_launcher(self.proc)
        finally:
            if self.proc is not None and self.proc.stdout is not None:
                self.proc.stdout.close()
            self._stderr.close()


# Test-side harness that runs the real launcher in its own process and parks it at a named point, so a test
# can interrupt it only once the resources under test are registered with the launcher's cleanup. The hooks
# live here, never in start.py: each point wraps a callable that launch() invokes right after the resources
# before it were entered (see FlowTests.test_the_barrier_points_follow_the_registrations_they_claim).
#   east-registered: west_catalog() is evaluated after the east LocalDB was entered onto the cleanup stack.
#   west-registered: microsoft_command() is evaluated after the west catalog was entered and before any host starts.
# The parking loop is only a hold; the test synchronises on the BARRIER line this prints, never on a delay.
BARRIER_HARNESS = '''\
import contextlib
import sys
import time

here, point, *launcher = sys.argv[1:]
sys.path.insert(0, here)
import start

east = []
real_private_localdb = start.private_localdb


@contextlib.contextmanager
def recording_private_localdb(workdir):
    with real_private_localdb(workdir) as server:
        east.append(server.rsplit("\\\\", 1)[-1])
        yield server


def hold(*resources):
    print("BARRIER", point, *east, *resources, file=sys.stderr, flush=True)
    deadline = time.monotonic() + 300
    while time.monotonic() < deadline:
        time.sleep(0.05)
        start.checkpoint()  # a recorded Ctrl+Break is acted on here, through the real cleanup stack
    raise SystemExit(99)


start.private_localdb = recording_private_localdb
if point == "east-registered":
    start.west_catalog = lambda: hold()
elif point == "west-registered":
    start.microsoft_command = lambda prerequisites, catalog, folder: hold(catalog)
else:
    raise SystemExit("unknown barrier point " + point)
sys.exit(start.main(launcher))
'''


@unittest.skipUnless(NATIVE, NATIVE_SKIP)
class PrerequisiteTests(unittest.TestCase):
    """Fail-closed behaviour of the real launcher process. Opt-in: it reads the real LocalDB inventory
    before and after each rejected launch to prove nothing was created."""

    def rejected(self, **artifacts):
        before = localdb_instances()
        done = subprocess.run(launcher_args(**artifacts), capture_output=True, text=True, timeout=120)
        self.assertEqual(before, localdb_instances(), "a rejected launch must not create a LocalDB instance")
        self.assertEqual(2, done.returncode, done.stderr)
        self.assertEqual("", done.stdout, "no endpoint may be advertised on failure")
        self.assertNotIn("Traceback", done.stderr)
        self.assertTrue(done.stderr.startswith("prerequisite: "), done.stderr)
        return done

    def test_a_missing_dotnet_is_rejected(self):
        with tempfile.TemporaryDirectory() as root:
            done = self.rejected(ignixa=Path(root) / "Ignixa.Web.dll",
                                 microsoft=Path(root) / "Microsoft.Health.Fhir.R4.Web.dll", dotnet=Path(root) / "dotnet.exe")
        self.assertIn("dotnet.exe", done.stderr)

    def test_missing_artifacts_are_rejected(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            done = self.rejected(ignixa=root / "absent" / "Ignixa.Web.dll",
                                 microsoft=root / "Microsoft.Health.Fhir.R4.Web.dll", dotnet=installed_dotnet())
        self.assertIn("Ignixa.Web.dll not found", done.stderr)

    def test_a_wrong_name_or_a_dll_with_no_runtime_config_is_rejected(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            wrong = root / "Other.dll"
            bare = root / "Ignixa.Web.dll"  # right name, but no runtimeconfig/deps beside it
            for path in (wrong, bare):
                path.write_bytes(b"not an assembly")
            microsoft = root / "ms" / "Microsoft.Health.Fhir.R4.Web.dll"
            microsoft.parent.mkdir()
            microsoft.write_bytes(b"not an assembly")
            for ignixa, reason in ((wrong, "Ignixa.Web.dll not found"), (bare, "is not a built host")):
                with self.subTest(ignixa=ignixa.name):
                    done = self.rejected(ignixa=ignixa, microsoft=microsoft, dotnet=installed_dotnet())
                    self.assertIn(reason, done.stderr)

    def test_a_malformed_framework_version_is_rejected_without_a_traceback(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            ignixa = root / "Ignixa.Web.dll"
            ignixa.write_bytes(b"not an assembly")
            (root / "Ignixa.Web.deps.json").write_text("{}")
            (root / "Ignixa.Web.runtimeconfig.json").write_text(json.dumps(
                {"runtimeOptions": {"frameworks": [{"name": "Microsoft.NETCore.App", "version": "ten"}]}}))
            done = self.rejected(ignixa=ignixa, microsoft=root / "Microsoft.Health.Fhir.R4.Web.dll",
                                 dotnet=installed_dotnet())
        self.assertIn("unreadable runtimeconfig", done.stderr)


class DeveloperPrerequisiteTests(unittest.TestCase):
    """The Developer instance is a prerequisite: it is checked, read-only, before any resource is created."""

    def test_a_non_developer_edition_is_a_prerequisite_failure(self):
        import start
        with mock.patch.object(start, "sql", return_value="Express Edition (64-bit)"):
            with self.assertRaisesRegex(start.PrerequisiteError, "Developer"):
                start.require_developer_edition()

    def test_an_unreachable_instance_is_a_prerequisite_failure(self):
        import start
        with mock.patch.object(start, "sql", side_effect=start.LaunchError("SQL Server did not complete the requested operation")):
            with self.assertRaisesRegex(start.PrerequisiteError, "lpc:localhost"):
                start.require_developer_edition()

    @unittest.skipUnless(NATIVE, NATIVE_SKIP)
    def test_the_edition_is_checked_before_any_resource_is_created(self):
        import start
        err = io.StringIO()
        with preserved_signals(), mock.patch.object(start, "sql", return_value="Standard Edition (64-bit)"), \
                mock.patch.object(start, "private_localdb", side_effect=AssertionError("a resource was created")), \
                contextlib.redirect_stderr(err):
            status = start.main(["--dotnet", DOTNET, "--ignixa-dll", IGNIXA_DLL, "--microsoft-dll", MICROSOFT_DLL])
        self.assertEqual(2, status)
        self.assertTrue(err.getvalue().startswith("prerequisite: "), err.getvalue())
        self.assertNotIn("Traceback", err.getvalue())


class ProtocolErrorTests(unittest.TestCase):
    """A shard that answers with something other than JSON must never crash readiness or skip cleanup."""

    NOT_JSON = {
        "plain-text startup failure": b"Application startup exception\n503",
        "html error page": b"<html><body>Bad Gateway</body></html>\n502",
        "json array, not an object": b"[1, 2]\n200",
        "invalid utf-8": b"\xff\xfe\x00\n200",
        "empty body": b"\n503",
    }
    UNREADABLE_STATUS = {"nothing at all": b"", "no status line": b"Application startup exception", "word status": b"{}\nOK"}

    @staticmethod
    def answering(stdout):
        return mock.patch("start.run_tool", side_effect=lambda argv, **kw: subprocess.CompletedProcess(argv, 0, stdout, b""))

    def endpoint(self):
        import start
        return start.Endpoint(Path("curl.exe"), "https://localhost:12345")

    def test_a_body_that_is_not_a_json_object_is_reported_as_no_resource(self):
        for label, stdout in self.NOT_JSON.items():
            with self.subTest(label), self.answering(stdout):
                status, body = self.endpoint().request("/metadata")
                self.assertIsNone(body)
                self.assertIsNone(self.endpoint().metadata())
                self.assertEqual(int(stdout.rsplit(b"\n", 1)[1]), status)

    def test_a_response_with_no_readable_status_is_a_launch_error_not_a_crash(self):
        import start
        for label, stdout in self.UNREADABLE_STATUS.items():
            with self.subTest(label), self.answering(stdout):
                with self.assertRaises(start.LaunchError):
                    self.endpoint().request("/metadata")

    def test_a_get_that_answers_with_text_names_the_http_status(self):
        import start
        with self.answering(b"Application startup exception\n503"):
            with self.assertRaisesRegex(start.LaunchError, "HTTP 503"):
                self.endpoint().get_resource("Patient", "dup")

    def test_a_put_refused_with_text_is_a_launch_error(self):
        import start
        with self.answering(b"upstream unavailable\n503"):
            with self.assertRaisesRegex(start.LaunchError, "HTTP 503"):
                self.endpoint().put({"resourceType": "Patient", "id": "dup"})

    def child(self, log_text, returncode=None):
        import start
        folder = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, folder, True)
        (folder / "host.log").write_text(log_text, encoding="utf-8")
        process = types.SimpleNamespace(poll=lambda: returncode, returncode=returncode)
        return start.Child("east (Ignixa)", process, folder / "host.log")

    def test_readiness_stays_bounded_while_the_shard_answers_with_non_json(self):
        import start
        child = self.child("Now listening on: https://127.0.0.1:12345\n")
        for label, stdout in {**self.NOT_JSON, **self.UNREADABLE_STATUS}.items():
            with self.subTest(label), self.answering(stdout):
                started = time.monotonic()
                with self.assertRaisesRegex(start.LaunchError, "was not ready within"):
                    start.await_ready(child, Path("curl.exe"), "", time.monotonic() + 0.3)
                self.assertLess(time.monotonic() - started, 10)

    def test_a_port_that_cannot_be_a_loopback_base_is_a_launch_error(self):
        import start
        child = self.child("Now listening on: https://127.0.0.1:99999999999\n")
        with self.assertRaises(start.LaunchError):
            start.await_ready(child, Path("curl.exe"), "", time.monotonic() + 0.3)

    def test_a_child_that_dies_with_a_plain_text_failure_ends_readiness_with_its_exit_code(self):
        import start
        child = self.child("Unhandled exception. System.InvalidOperationException: boom\n", returncode=134)
        with self.assertRaisesRegex(start.LaunchError, r"exited with code 134 before it was ready.*InvalidOperationException"):
            start.await_ready(child, Path("curl.exe"), "", time.monotonic() + 30)

    def test_a_tool_that_cannot_be_run_is_a_launch_error(self):
        import start
        with self.assertRaises(start.LaunchError):
            start.run_tool([str(Path(tempfile.gettempdir()) / "no-such-tool-fed.exe")], timeout=5)


class HostSelectionTests(unittest.TestCase):
    """The acceptance test may signal only an actual child of its own launcher running exactly its own dll."""

    DLL = r"C:\build output\Microsoft.Health.Fhir.R4.Web.dll"

    def row(self, pid, parent, command_line):
        return {"ProcessId": pid, "ParentProcessId": parent, "CommandLine": command_line}

    def test_only_a_direct_child_running_exactly_that_dll_is_selected(self):
        rows = [
            self.row(10, 1, f'"C:\\dotnet\\dotnet.exe" "{self.DLL}" --urls https://127.0.0.1:0'),      # the launcher's child
            self.row(11, 2, f'"C:\\dotnet\\dotnet.exe" "{self.DLL}" --urls https://127.0.0.1:0'),      # same dll, another run
            self.row(12, 1, f'"C:\\dotnet\\dotnet.exe" "{self.DLL}.bak" --urls https://127.0.0.1:0'),  # near-miss path
            self.row(13, 1, f'"C:\\dotnet\\dotnet.exe" "C:\\other.dll" --note "{self.DLL}"'),          # dll only mentioned
            self.row(14, 1, f'"C:\\dotnet\\dotnet.exe" "{self.DLL.upper()}"'),                         # case-insensitive path
            self.row(15, 1, None),                                                                      # no readable command line
            self.row(16, 1, '"C:\\dotnet\\dotnet.exe" "unterminated'),
        ]
        self.assertEqual([10, 14], owned_hosts(rows, self.DLL, parent_pid=1))
        self.assertEqual([11], owned_hosts(rows, self.DLL, parent_pid=2))
        self.assertEqual([], owned_hosts(rows, self.DLL, parent_pid=99))


class ReadmeTests(unittest.TestCase):
    def test_windows_powershell_examples_call_curl_exe_not_the_invoke_webrequest_alias(self):
        lines = (HERE / "README.md").read_text(encoding="utf-8").splitlines()
        self.assertEqual([], [line for line in lines if re.match(r"\s*curl\s", line)])
        self.assertEqual(4, len([line for line in lines if line.startswith("curl.exe --noproxy ")]))

    def test_the_default_and_opted_in_test_runs_are_documented(self):
        text = (HERE / "README.md").read_text(encoding="utf-8")
        for phrase in ("no SQL or data access", "touches real SQL", "FED_PROTO_DOTNET", "lpc:localhost", "SqlLocalDB", "sqlcmd"):
            self.assertIn(phrase, text)


class SqlOptInGateTests(unittest.TestCase):
    """Every case that touches real SQL Server or LocalDB is skipped unless all three artifact variables are set."""

    def test_the_sql_touching_cases_are_gated_by_the_opt_in(self):
        gated = (PrerequisiteTests, CatalogGuardTests, NativeAcceptanceTests,
                 PrivateLocalDbTests.test_an_interrupt_just_after_the_instance_exists_still_removes_it)
        for case in gated:
            with self.subTest(case=getattr(case, "__qualname__", case)):
                self.assertEqual(not NATIVE, bool(getattr(case, "__unittest_skip__", False)))


class FlowTests(unittest.TestCase):
    """The launcher's own control flow, with its resources replaced by recorders."""

    def setUp(self):
        import start
        self.start = start
        self.events = []
        self.signals_during_cleanup = []

    def resource(self, name, value, cleanup_error=None):
        @contextlib.contextmanager
        def manager(*_):
            self.events.append("create " + name)
            yield value
            self.signals_during_cleanup.append((signal.getsignal(signal.SIGINT), signal.getsignal(signal.SIGBREAK)))
            self.events.append("remove " + name)
            if cleanup_error:
                raise self.start.CleanupError(cleanup_error)
        return manager

    def run_main(self, *flags, west_cleanup_error=None, sleep=None, east=None, west=None):
        start, events = self.start, self.events

        class Stdout(io.StringIO):
            def write(self, text):
                if text.strip():
                    events.append("stdout " + text.strip())
                return super().write(text)

        out, err = Stdout(), io.StringIO()
        real_remove = start.remove_tree
        prerequisites = start.Prerequisites(Path("dotnet.exe"), Path("i.dll"), Path("m.dll"), Path("curl.exe"))
        child = lambda label, *_: types.SimpleNamespace(
            label=label, process=types.SimpleNamespace(poll=lambda: None, returncode=None),
            stop=lambda: events.append("stop " + label))
        ready = lambda child, curl, suffix, deadline: types.SimpleNamespace(
            base="https://localhost:1/tenant/1" if suffix else "https://localhost:2")
        with preserved_signals(), contextlib.ExitStack() as patches:
            for target, replacement in (
                    ("check_prerequisites", mock.Mock(return_value=prerequisites)),
                    ("private_localdb", east or self.resource("east", r"(localdb)\FedProtoabc")),
                    ("west_catalog", west or self.resource("west", "FedProtoWest_x", west_cleanup_error)),
                    ("ignixa_command", mock.Mock(return_value=([], {}))),
                    ("microsoft_command", mock.Mock(return_value=([], {}))),
                    ("start_child", child), ("await_ready", ready),
                    ("collision_corpus", mock.Mock(return_value=([], []))), ("verify", mock.Mock()),
                    ("remove_tree", lambda path: (real_remove(path), events.append("remove workdir")))):
                patches.enter_context(mock.patch.object(start, target, replacement))
            if sleep:
                patches.enter_context(mock.patch.object(start.time, "sleep", sleep))
            patches.enter_context(contextlib.redirect_stdout(out))
            patches.enter_context(contextlib.redirect_stderr(err))
            status = start.main([*flags, "--dotnet", "d", "--ignixa-dll", "i", "--microsoft-dll", "m"])
        return status, out.getvalue(), err.getvalue()

    def test_the_barrier_points_follow_the_registrations_they_claim(self):
        """The real launch() order the native barrier harness relies on, with every resource replaced by a recorder."""
        start, events = self.start, self.events
        prerequisites = start.Prerequisites(Path("dotnet.exe"), Path("i.dll"), Path("m.dll"), Path("curl.exe"))

        @contextlib.contextmanager
        def held(name):
            events.append("create " + name)
            try:
                yield name
            finally:
                events.append("remove " + name)

        def hold(point):
            def barrier(*_):
                events.append("barrier " + point)
                raise KeyboardInterrupt
            return barrier

        for point, expected in (
                ("east-registered", ["create east", "barrier east-registered", "remove east"]),
                ("west-registered", ["create east", "create west", "barrier west-registered", "remove west", "remove east"])):
            with self.subTest(point):
                events.clear()
                west = hold(point) if point == "east-registered" else lambda: held("west")
                microsoft = hold(point) if point == "west-registered" else mock.Mock(return_value=([], {}))
                with contextlib.ExitStack() as patches:
                    for target, replacement in (
                            ("private_localdb", lambda workdir: held("east")), ("west_catalog", west),
                            ("ignixa_command", mock.Mock(return_value=([], {}))), ("microsoft_command", microsoft),
                            ("start_child", lambda *_: events.append("host started"))):
                        patches.enter_context(mock.patch.object(start, target, replacement))
                    with self.assertRaises(KeyboardInterrupt), contextlib.ExitStack() as stack:
                        start.launch(stack, prerequisites, False, [])
                self.assertEqual(expected, events, "no host may start before the west barrier, and cleanup is LIFO")

    def test_verified_is_printed_only_after_every_resource_has_been_cleaned_up(self):
        status, out, _ = self.run_main("--verify")
        self.assertEqual(0, status)
        self.assertEqual('{"verified": true}\n', out)
        self.assertEqual(['stdout {"verified": true}'], self.events[-1:])
        for cleanup in ("stop east (Ignixa)", "stop west (Microsoft)", "remove west", "remove east", "remove workdir"):
            self.assertIn(cleanup, self.events[:-1])

    def test_cleanup_runs_with_further_interrupts_ignored(self):
        self.run_main("--verify")
        self.assertEqual([(signal.SIG_IGN, signal.SIG_IGN)] * 2, self.signals_during_cleanup)

    def test_a_cleanup_failure_means_no_verified_line_and_a_nonzero_status(self):
        status, out, err = self.run_main("--verify", west_cleanup_error="west catalog FedProtoWest_x could not be removed")
        self.assertEqual(3, status)
        self.assertEqual("", out)
        self.assertIn("FedProtoWest_x could not be removed", err)
        self.assertNotIn("verified", err)

    def test_run_mode_still_prints_the_two_real_bases_once_and_cleans_up_on_interrupt(self):
        def interrupt(_seconds):
            raise KeyboardInterrupt
        status, out, err = self.run_main(sleep=interrupt)
        self.assertEqual(0, status)
        self.assertEqual({"east": "https://localhost:1/tenant/1", "west": "https://localhost:2"}, json.loads(out))
        self.assertEqual(1, len(out.splitlines()))
        self.assertLess(self.events.index(next(e for e in self.events if e.startswith("stdout "))), self.events.index("remove east"))
        self.assertIn("remove workdir", self.events)

    def test_an_unreadable_localdb_listing_at_teardown_means_no_verified_line(self):
        start, calls, real_private_localdb = self.start, [], self.start.private_localdb

        def run(argv, **kwargs):
            verb = argv[1] if argv[0] == "SqlLocalDB" else "sqlcmd"
            calls.append(verb)
            listing = (calls.count("info"), verb) == (2, "info")  # the teardown listing fails
            return subprocess.CompletedProcess(argv, 1 if listing else 0, b"", b"")
        with mock.patch.object(start.subprocess, "run", run):
            status, out, err = self.run_main("--verify", east=lambda workdir: real_private_localdb(workdir))
        self.assertEqual(["info", "create", "sqlcmd", "info"], calls, "nothing is stopped or deleted on an unknown inventory")
        self.assertEqual(3, status)
        self.assertEqual("", out)
        self.assertRegex(err, r"FedProto[0-9a-f]{12}")
        self.assertNotIn("verified", err)
        self.assertIn("remove west", self.events, "the other cleanup still runs")
        self.assertIn("remove workdir", self.events)

    def test_an_interrupt_in_run_mode_stops_and_cleans_up_through_the_real_handlers(self):
        for sig in (signal.SIGINT, signal.SIGBREAK):
            with self.subTest(signal=sig.name):
                self.events.clear()
                status, out, err = self.run_main(sleep=lambda seconds: signal.raise_signal(sig))
                self.assertEqual(0, status, err)
                self.assertEqual(1, len(out.splitlines()), "the bases are printed once")
                self.assertEqual(["remove west", "remove east", "remove workdir"],
                                 [e for e in self.events if e.startswith("remove ")])

    def test_a_catalog_whose_identity_cannot_be_read_fails_the_run_and_is_named(self):
        start, server = self.start, FakeDeveloperInstance()
        real_west_catalog = start.west_catalog  # run_main replaces the module attribute with this very test double

        @contextlib.contextmanager
        def west():
            with real_west_catalog() as name:
                yield name
                server.unreadable.add(name)  # by the time cleanup reads it, ownership can no longer be proven
        with mock.patch.object(start, "run_tool", server):
            status, out, err = self.run_main("--verify", west=west)
        self.assertEqual(3, status)
        self.assertEqual("", out)
        self.assertRegex(err, r"FedProtoWest_[0-9a-f]{32}")
        self.assertIn("left in place", err)
        self.assertNotIn("verified", err)
        self.assertEqual([], server.drops)
        self.assertIn("remove workdir", self.events, "the other cleanup still runs")

    def test_interrupts_are_ignored_only_while_cleanup_runs(self):
        import start
        with preserved_signals():
            before = [signal.getsignal(s) for s in (signal.SIGINT, signal.SIGBREAK)]
            with start.interrupts_deferred():
                self.assertEqual([signal.SIG_IGN] * 2, [signal.getsignal(s) for s in (signal.SIGINT, signal.SIGBREAK)])
            self.assertEqual(before, [signal.getsignal(s) for s in (signal.SIGINT, signal.SIGBREAK)])


class CleanupShieldingTests(unittest.TestCase):
    """Cleanup tools must not share the launcher's console, because a console Ctrl+Break reaches all of its processes."""

    NO_WINDOW = subprocess.CREATE_NO_WINDOW

    def setUp(self):
        import start
        self.start = start

    def fake_tools(self, calls, *, interrupt_on=None, localdb=()):
        """subprocess.run stand-in recording (tool, verb, creationflags, SIGINT handler) and answering plausibly."""
        marker = "11111111-1111-1111-1111-111111111111"
        created = list(localdb)

        def run(argv, **kwargs):
            verb = argv[1] if argv[0] == "SqlLocalDB" else "sqlcmd"
            calls.append((argv[0], verb, kwargs.get("creationflags", 0), signal.getsignal(signal.SIGINT)))
            if verb == "create":
                created.append(argv[2])
            if interrupt_on == verb:
                raise KeyboardInterrupt
            if verb == "info":
                return subprocess.CompletedProcess(argv, 0, " ".join(created).encode(), b"")
            if argv[0] == "sqlcmd":
                query = argv[argv.index("-Q") + 1]
                text = f"1 1 {marker}" if ("create database [FedProtoWest" in query or "from sys.databases" in query) else ""
                return subprocess.CompletedProcess(argv, 0, text.encode(), b"")
            return subprocess.CompletedProcess(argv, 0, b"", b"")
        return run

    def test_partly_created_localdb_is_discarded_by_tools_that_are_shielded_from_the_console(self):
        calls = []
        with preserved_signals(), tempfile.TemporaryDirectory() as workdir, \
                mock.patch.object(self.start.subprocess, "run", self.fake_tools(calls, interrupt_on="sqlcmd")):
            with self.assertRaises(KeyboardInterrupt):
                with self.start.private_localdb(Path(workdir)):
                    self.fail("the interrupt must stop the launch before the body runs")
        by_verb = {verb: (flags, handler) for _, verb, flags, handler in calls}
        self.assertEqual(["info", "create", "sqlcmd", "info", "stop", "delete"], [verb for _, verb, _, _ in calls])
        for verb in ("create", "sqlcmd"):
            self.assertEqual(0, by_verb[verb][0], f"{verb} is a startup tool and must stay on the shared console")
        for _, verb, flags, handler in calls[3:]:
            self.assertEqual(self.NO_WINDOW, flags, f"cleanup tool {verb} must not share the launcher's console")
            self.assertEqual(signal.SIG_IGN, handler, f"cleanup tool {verb} must run with interrupts ignored")

    def test_catalog_cleanup_sql_is_shielded_while_its_creation_sql_is_not(self):
        calls = []
        with preserved_signals(), mock.patch.object(self.start.subprocess, "run", self.fake_tools(calls)):
            catalog = self.start.west_catalog()
            catalog.__enter__()
            created = len(calls)
            with self.start.interrupts_deferred():
                catalog.__exit__(None, None, None)
        self.assertEqual([0], [flags for _, _, flags, _ in calls[:created]])
        self.assertGreaterEqual(len(calls) - created, 2, "the guard read and the drop both run at cleanup")
        self.assertEqual({self.NO_WINDOW}, {flags for _, _, flags, _ in calls[created:]})

    def test_the_shield_covers_only_the_scope_and_nests(self):
        calls = []
        with preserved_signals(), mock.patch.object(self.start.subprocess, "run", self.fake_tools(calls)):
            self.start.run_tool(["SqlLocalDB", "info"], timeout=5)
            with self.start.interrupts_deferred():
                with self.start.interrupts_deferred():
                    self.start.run_tool(["SqlLocalDB", "info"], timeout=5)
                self.start.run_tool(["SqlLocalDB", "info"], timeout=5)
            self.start.run_tool(["SqlLocalDB", "info"], timeout=5)
        self.assertEqual([0, self.NO_WINDOW, self.NO_WINDOW, 0], [flags for _, _, flags, _ in calls])

    def test_setup_tools_in_the_launcher_do_not_share_its_interruptible_console(self):
        calls = []
        with preserved_signals(), self.start.interrupt_policy(), \
                mock.patch.object(self.start.subprocess, "run", self.fake_tools(calls)):
            self.start.run_tool(["SqlLocalDB", "info"], timeout=5)
        self.assertEqual([self.NO_WINDOW], [flags for _, _, flags, _ in calls])

    def test_a_failing_cleanup_tool_is_still_reported_not_swallowed(self):
        def stop_fails(argv, **kwargs):
            return subprocess.CompletedProcess(argv, 1 if argv[1:2] == ["stop"] else 0, b"FedProtoabc" if argv[1:2] == ["info"] else b"", b"")
        with preserved_signals(), mock.patch.object(self.start.subprocess, "run", stop_fails):
            with self.assertRaises(self.start.CleanupError) as raised, self.start.interrupts_deferred():
                self.start.discard_localdb("FedProtoabc")
        self.assertIn("FedProtoabc", str(raised.exception))

    HELPER = r'''
import json, signal, subprocess, sys, threading
sys.path.insert(0, sys.argv[1])
import start
marker, scoped, result = sys.argv[2], sys.argv[3] == "scoped", sys.argv[4]
for s in (signal.SIGINT, signal.SIGBREAK):
    signal.signal(s, signal.SIG_IGN)
# GenerateConsoleCtrlEvent(CTRL_BREAK, 0) reaches every process attached to THIS helper's private console only.
threading.Timer(1.0, lambda: __import__("ctypes").windll.kernel32.GenerateConsoleCtrlEvent(1, 0)).start()
tool = [sys.executable, "-c", "import sys, time; time.sleep(3); open(sys.argv[1], 'w').write('survived')", marker]
outcome = "completed"
try:
    if scoped:
        with start.interrupts_deferred():
            start.run_checked(tool, "cleanup tool failed", cleanup=True)
    else:
        start.run_checked(tool, "startup tool failed")
except Exception as error:
    outcome = "failed: " + str(error)
open(result, "w").write(outcome)
'''

    def broadcast_break_in_private_console(self, scoped):
        """Run the helper in its own hidden console, broadcast Ctrl+Break there, and report (tool survived, outcome)."""
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder)
            marker, result, helper = folder / "marker", folder / "result", folder / "helper.py"
            helper.write_text(self.HELPER, encoding="utf-8")
            hidden = subprocess.STARTUPINFO(dwFlags=subprocess.STARTF_USESHOWWINDOW, wShowWindow=0)
            process = subprocess.Popen(
                [sys.executable, "-B", str(helper), str(HERE), str(marker), "scoped" if scoped else "plain", str(result)],
                creationflags=subprocess.CREATE_NEW_CONSOLE, startupinfo=hidden,
                stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=os.environ.copy())
            try:
                process.wait(timeout=60)
            finally:
                if process.poll() is None:
                    process.kill()
            return marker.exists(), result.read_text() if result.exists() else "no result"

    def test_a_break_on_a_private_console_kills_an_unshielded_tool(self):
        survived, outcome = self.broadcast_break_in_private_console(scoped=False)
        self.assertFalse(survived, "control: the broadcast must reach a tool that shares the console")
        self.assertTrue(outcome.startswith("failed"), outcome)

    def test_a_break_on_a_private_console_does_not_reach_a_cleanup_tool(self):
        survived, outcome = self.broadcast_break_in_private_console(scoped=True)
        self.assertTrue(survived, "the cleanup tool was killed by the console event: " + outcome)
        self.assertEqual("completed", outcome)


class LocalDbInventoryTests(unittest.TestCase):
    """A failed or unreadable ``SqlLocalDB info`` listing is never read as "no instances".

    Every process response here is a stub; no SqlLocalDB, sqlcmd or other real tool is started.
    """

    def setUp(self):
        import start
        self.start = start
        self.calls = []

    def stub_tools(self, *, listings):
        """subprocess.run stand-in. ``listings`` answers successive ``info`` calls as (returncode, stdout) pairs;
        the instance the launcher creates is appended to a listing given as a callable of the created names."""
        created, pending = [], list(listings)

        def run(argv, **kwargs):
            self.assertIn(argv[0], ("SqlLocalDB", "sqlcmd"))
            verb = argv[1] if argv[0] == "SqlLocalDB" else "sqlcmd"
            self.calls.append(verb)
            if verb == "create":
                created.append(argv[2])
            if verb == "info":
                code, out = pending.pop(0) if pending else (0, b"")
                return subprocess.CompletedProcess(argv, code, out(created) if callable(out) else out, b"")
            return subprocess.CompletedProcess(argv, 0, b"", b"")
        return mock.patch.object(self.start.subprocess, "run", run)

    FAILED = (1, b"")
    UNREADABLE = (0, b"\xff\xfe\x00 not text")
    LISTS_CREATED = (0, lambda created: " ".join(created).encode())

    def enter_and_leave(self):
        with tempfile.TemporaryDirectory() as workdir, self.start.private_localdb(Path(workdir)):
            pass

    def test_a_failed_listing_before_creation_creates_nothing_and_fails_visibly(self):
        for label, bad in (("non-zero exit", self.FAILED), ("undecodable output", self.UNREADABLE)):
            with self.subTest(label), self.stub_tools(listings=[bad]):
                self.calls.clear()
                with self.assertRaises(self.start.LaunchError):
                    self.enter_and_leave()
                self.assertEqual(["info"], self.calls, "no instance may be created on an unknown inventory")

    def test_a_failed_listing_at_teardown_names_the_instance_and_guesses_nothing(self):
        for label, bad in (("non-zero exit", self.FAILED), ("undecodable output", self.UNREADABLE)):
            with self.subTest(label), self.stub_tools(listings=[(0, b""), bad]):
                self.calls.clear()
                with self.assertRaises(self.start.CleanupError) as raised:
                    self.enter_and_leave()
                self.assertRegex(str(raised.exception), r"FedProto[0-9a-f]{12}")
                self.assertEqual(["info", "create", "sqlcmd", "info"], self.calls, "no stop or delete on an unknown inventory")

    def test_a_genuinely_empty_listing_still_means_nothing_to_discard(self):
        with self.stub_tools(listings=[(0, b"")]):
            self.start.discard_localdb("FedProtoabcdef123456")
        self.assertEqual(["info"], self.calls)

    def test_successful_listings_still_create_and_then_stop_and_delete_the_instance(self):
        with self.stub_tools(listings=[(0, b""), self.LISTS_CREATED]):
            self.enter_and_leave()
        self.assertEqual(["info", "create", "sqlcmd", "info", "stop", "delete"], self.calls)


class FakeDeveloperInstance:
    """Stands in for ``sqlcmd`` against the local Developer instance. No SQL Server is involved.

    It answers only the statements ``west_catalog`` issues: create, the identity probe and the guarded drop. A catalog
    is a name mapped to its ``database_guid``. A name in ``unreadable`` still exists but exposes no guid, as for a
    database that is not online. The probe answers ``"<sees all> <exists> <guid>"``, where ``sees_all`` is whether the
    caller may see every database; ``probe_stdout`` and ``probe_returncode`` let a test spoil what it answers.
    """

    def __init__(self):
        self.databases, self.unreadable, self.drops = {}, set(), []
        self.sees_all, self.probe_stdout, self.probe_returncode, self._serial = True, None, 0, 0

    def __call__(self, argv, **kwargs):
        query = argv[argv.index("-Q") + 1]
        done = lambda code=0, out="": subprocess.CompletedProcess(argv, code, out.encode(), b"")
        dropped = re.search(r"drop database \[([^\]]+)\]", query)
        if dropped:
            guard = re.search(r"database_guid='([^']+)'", query)
            if self.databases.get(dropped[1]) != guard[1]:
                return done(1)  # the guard's throw
            del self.databases[dropped[1]]
            self.drops.append(dropped[1])
            return done()
        created = re.search(r"create database \[([^\]]+)\]", query)
        if created:
            self._serial += 1
            self.databases[created[1]] = f"00000000-0000-0000-0000-{self._serial:012d}"
        name = re.search(r"db_id\(N'([^']+)'\)", query)[1]
        if self.probe_returncode:
            return done(self.probe_returncode)
        if self.probe_stdout is not None:
            return subprocess.CompletedProcess(argv, 0, self.probe_stdout, b"")
        guid = "" if name not in self.databases or name in self.unreadable else self.databases[name]
        return done(0, f"{int(self.sees_all)} {int(name in self.databases)} {guid}".strip())


class CatalogOwnershipTests(unittest.TestCase):
    """The west catalog is dropped only when it is proven to be the one this run created, against a SQL double.

    A catalog that is verifiably gone is not an error. One whose identity cannot be read is neither dropped nor
    reported as removed: ownership is unproven, so cleanup is incomplete and says so.
    """

    def setUp(self):
        import start
        self.start = start
        self.server = FakeDeveloperInstance()
        self.err = io.StringIO()
        patches = contextlib.ExitStack()
        patches.enter_context(mock.patch.object(start, "run_tool", self.server))
        patches.enter_context(contextlib.redirect_stderr(self.err))
        self.addCleanup(patches.close)

    def run_catalog(self, change=lambda name: None):
        with self.start.west_catalog() as name:
            change(name)
        return name

    def test_the_catalog_this_run_created_is_dropped_while_it_is_still_ours(self):
        name = self.run_catalog()
        self.assertEqual([name], self.server.drops)
        self.assertEqual({}, self.server.databases)

    def test_a_catalog_verified_absent_is_reported_and_is_not_an_error(self):
        name = self.run_catalog(lambda name: self.server.databases.pop(name))
        self.assertEqual([], self.server.drops)
        self.assertIn(f"west catalog {name} was already removed", self.err.getvalue())

    def test_an_empty_listing_is_not_absence_when_the_caller_may_not_see_every_database(self):
        """sys.databases hides other principals' offline databases from a caller without the visibility permissions."""
        def hidden(name):
            self.server.databases.pop(name)
            self.server.sees_all = False
        with self.assertRaises(self.start.CleanupError) as raised:
            self.run_catalog(hidden)
        self.assertRegex(str(raised.exception), r"FedProtoWest_[0-9a-f]{32}")
        self.assertIn("manually", str(raised.exception))
        self.assertNotIn("already removed", self.err.getvalue())
        self.assertEqual([], self.server.drops)

    def test_a_replaced_catalog_is_refused_and_left_in_place(self):
        replacement = "99999999-9999-9999-9999-999999999999"
        with self.assertRaisesRegex(self.start.CleanupError, r"FedProtoWest_[0-9a-f]{32} was replaced") as raised:
            self.run_catalog(lambda name: self.server.databases.update({name: replacement}))
        self.assertEqual([], self.server.drops)
        self.assertEqual(replacement, next(iter(self.server.databases.values())), str(raised.exception))

    def test_a_catalog_whose_identity_cannot_be_read_is_left_in_place_and_reported(self):
        with self.assertRaises(self.start.CleanupError) as raised:
            self.run_catalog(lambda name: self.server.unreadable.add(name))
        message = str(raised.exception)
        self.assertRegex(message, r"FedProtoWest_[0-9a-f]{32}")
        self.assertIn("left in place", message)
        self.assertIn("manually", message)
        self.assertNotIn("replaced", message, "an unreadable identity is not a replacement")
        self.assertNotIn("already removed", self.err.getvalue(), "it was not verified absent")
        self.assertEqual([], self.server.drops)
        self.assertEqual(1, len(self.server.databases), "an unproven catalog must never be dropped")

    def test_an_identity_answer_that_cannot_be_trusted_is_never_taken_as_absence_or_ownership(self):
        answers = {"nothing at all": b"", "sqlcmd error text": b"Msg 229, Level 14, State 5",
                   "a truncated guid": b"1 1 1234",
                   "a braced guid": b"1 1 {00000000-0000-0000-0000-000000000001}",
                   "no visibility column": b"0", "a bad visibility flag": b"2 0", "trailing text": b"1 0 x",
                   "undecodable bytes": b"\xff\xfe\x00"}
        for label, stdout in answers.items():
            with self.subTest(label):
                self.server.drops.clear(), self.server.databases.clear()
                self.server.probe_stdout = None
                self.err.truncate(0), self.err.seek(0)

                def spoil(name):
                    self.server.probe_stdout = stdout
                with self.assertRaises(self.start.CleanupError) as raised:
                    self.run_catalog(spoil)
                self.assertRegex(str(raised.exception), r"FedProtoWest_[0-9a-f]{32}")
                self.assertIn("manually", str(raised.exception))
                self.assertNotIn("already removed", self.err.getvalue())
                self.assertEqual([], self.server.drops)

    def test_an_identity_probe_that_cannot_run_names_the_catalog(self):
        def probe_fails(name):
            self.server.probe_returncode = 1
        with self.assertRaisesRegex(self.start.CleanupError, r"FedProtoWest_[0-9a-f]{32} could not be removed"):
            self.run_catalog(probe_fails)
        self.assertEqual([], self.server.drops)

    def test_a_new_catalog_whose_identity_is_not_proven_stops_the_launch_and_names_it(self):
        for label, answer in (("unreadable", b"1"), ("reported absent", b"1 0"), ("visibility unknown", b"0 0")):
            with self.subTest(label):
                self.server.probe_stdout = answer
                with self.assertRaisesRegex(self.start.LaunchError, r"FedProtoWest_[0-9a-f]{32}"):
                    self.run_catalog()
                self.assertEqual([], self.server.drops)


class StartupInterruptTests(unittest.TestCase):
    """A Ctrl+C or Ctrl+Break pressed while something is created must still reach that thing's cleanup.

    Each signal is delivered with ``signal.raise_signal``, which runs the installed handler in the main thread
    before it returns, at the point where a creation call is about to hand its result back: the instant the
    resource exists but is not yet registered. No tool, SQL Server or shard host starts in these cases.
    A real console event reaches the same handler; the opt-in native tests exercise that path.
    """

    SIGNALS = (signal.SIGINT, signal.SIGBREAK)

    class Host:
        """A started-process double that records how it is stopped."""

        def __init__(self, label, events):
            self.label, self.events, self.running = label, events, True

        def poll(self):
            return None if self.running else 0

        def terminate(self):
            self.events.append("terminate " + self.label)
            self.running = False

        def wait(self, timeout=None):
            return 0

        def kill(self):
            self.events.append("kill " + self.label)

    class AfterEnter:
        """Delivers the interrupt just as the wrapped resource finishes entering, before the caller sees it."""

        def __init__(self, manager, deliver):
            self.manager, self.deliver = manager, deliver

        def __enter__(self):
            value = self.manager.__enter__()
            self.deliver()
            return value

        def __exit__(self, *exception):
            return self.manager.__exit__(*exception)

    def setUp(self):
        import start
        self.start = start
        self.events = []
        self.root = Path(tempfile.mkdtemp(prefix="fed-test-"))
        self.addCleanup(shutil.rmtree, self.root, True)

    def run_serve(self, at, *signals, during_shutdown=()):
        """Run ``start.serve``, pressing ``signals`` as ``at`` ("workdir", "east", "west", "host 1" or "host 2") is created.

        ``during_shutdown`` is pressed at the first instant of cleanup, before the shield that ignores signals is up.
        The run must end in KeyboardInterrupt, with the folder gone and the signal handlers restored.
        """
        start, events, hosts = self.start, self.events, []
        interrupt = lambda: [signal.raise_signal(s) for s in signals]
        real_mkdtemp, real_remove, real_deferred = tempfile.mkdtemp, start.remove_tree, start.interrupts_deferred

        @contextlib.contextmanager
        def deferred():
            [signal.raise_signal(s) for s in during_shutdown]
            with real_deferred():
                yield

        def mkdtemp(prefix):
            path = real_mkdtemp(prefix=prefix, dir=self.root)
            if at == "workdir":
                interrupt()
            return path

        def resource(name):
            @contextlib.contextmanager
            def manager():
                events.append("create " + name)
                try:
                    yield name
                finally:
                    events.append("remove " + name)
            return self.AfterEnter(manager(), interrupt) if at == name else manager()

        def popen(argv, **kwargs):
            hosts.append(self.Host(argv[0], events))
            events.append("start " + argv[0])
            if at == f"host {len(hosts)}":
                interrupt()
            return hosts[-1]

        prerequisites = start.Prerequisites(Path("dotnet.exe"), Path("i.dll"), Path("m.dll"), Path("curl.exe"))
        caught, problems = None, []
        with preserved_signals(), contextlib.ExitStack() as patches:
            for sig in self.SIGNALS:
                signal.signal(sig, signal.default_int_handler)  # what the interpreter and main() leave in place
            before = [signal.getsignal(sig) for sig in self.SIGNALS]
            for target, replacement in (
                    ("private_localdb", lambda workdir: resource("east")), ("west_catalog", lambda: resource("west")),
                    ("ignixa_command", mock.Mock(return_value=(["east-host"], {}))),
                    ("microsoft_command", mock.Mock(return_value=(["west-host"], {}))),
                    ("remove_tree", lambda path: (real_remove(path), events.append("remove workdir"))),
                    ("interrupts_deferred", deferred)):
                patches.enter_context(mock.patch.object(start, target, replacement))
            patches.enter_context(mock.patch.object(start.tempfile, "mkdtemp", mkdtemp))
            patches.enter_context(mock.patch.object(start.subprocess, "Popen", popen))
            try:
                start.serve(prerequisites, False, problems)
            except KeyboardInterrupt as error:
                caught = error  # keeps the traceback frames alive until the checks below have run
            self.assertIsNotNone(caught, "the interrupt must still end the run")
            self.assertEqual(before, [signal.getsignal(sig) for sig in self.SIGNALS], "the run must restore the handlers")
        self.assertEqual([], problems)
        self.assertEqual([], list(self.root.iterdir()), "the working directory must be gone")

    def test_an_interrupt_as_anything_is_created_still_removes_everything_in_reverse_order(self):
        """One run per creation point, pressing Ctrl+C, Ctrl+Break and Ctrl+C again as the thing comes into being."""
        host1, host2 = ["start east-host", "terminate east-host"], ["start east-host", "start west-host", "terminate west-host", "terminate east-host"]
        both = ["create east", "create west"]
        for at, expected in (
                ("workdir", ["remove workdir"]),
                ("east", ["create east", "remove east", "remove workdir"]),
                ("west", [*both, "remove west", "remove east", "remove workdir"]),
                ("host 1", [*both, *host1, "remove west", "remove east", "remove workdir"]),
                ("host 2", [*both, *host2, "remove west", "remove east", "remove workdir"])):
            with self.subTest(at=at):
                self.events.clear()
                self.run_serve(at, signal.SIGINT, signal.SIGBREAK, signal.SIGINT)
                self.assertEqual(expected, self.events)

    def test_presses_as_cleanup_begins_cannot_skip_it(self):
        """The window between the failing launch and the cleanup shield, and presses inside the cleanup itself."""
        self.run_serve("host 1", signal.SIGINT, during_shutdown=(signal.SIGINT, signal.SIGBREAK))
        self.assertEqual(["create east", "create west", "start east-host", "terminate east-host",
                          "remove west", "remove east", "remove workdir"], self.events)

    def test_the_policy_records_presses_without_raising_and_restores_the_handlers(self):
        with preserved_signals():
            for sig in self.SIGNALS:
                signal.signal(sig, signal.default_int_handler)
            with self.start.interrupt_policy():
                self.start.checkpoint()
                for sig in (*self.SIGNALS, *self.SIGNALS):
                    signal.raise_signal(sig)  # no exception from the handler, wherever it lands
                with self.assertRaises(KeyboardInterrupt):
                    self.start.checkpoint()
            self.assertEqual([signal.default_int_handler] * 2, [signal.getsignal(sig) for sig in self.SIGNALS])
            with self.start.interrupt_policy():
                self.start.checkpoint()  # a new run does not inherit the previous run's request


class LauncherTeardownTests(unittest.TestCase):
    def test_slow_cleanup_finishes_without_force_even_if_the_stop_signal_fails(self):
        for signal_failure in (False, True):
            with self.subTest(signal_failure=signal_failure):
                proc = mock.Mock(pid=1)
                proc.send_signal.side_effect = OSError("signalling failed") if signal_failure else None
                elapsed = 0
                stopped = False
                cleanup_ends_at = 690  # up to 300s in-flight verification plus both hosts and both databases
                exit_code = 130
                proc.poll.side_effect = lambda: exit_code if stopped or elapsed >= cleanup_ends_at else None

                def kill():
                    nonlocal stopped
                    stopped = True

                def wait(*, timeout):
                    nonlocal elapsed
                    if stopped:
                        return 1
                    elapsed = min(elapsed + timeout, cleanup_ends_at)
                    if elapsed < cleanup_ends_at:
                        raise subprocess.TimeoutExpired("launcher", timeout)
                    return exit_code

                proc.kill.side_effect = kill
                proc.wait.side_effect = wait
                try:
                    result = stop_launcher(proc)
                except RuntimeError as error:
                    self.fail(f"force-stopped a launcher still within its cleanup budget: {error}")
                self.assertEqual(exit_code, result)
                self.assertEqual(cleanup_ends_at, elapsed)
                proc.kill.assert_not_called()

    def test_close_gives_the_owned_launcher_a_bounded_grace_period_before_force(self):
        timeout = subprocess.TimeoutExpired("launcher", LAUNCHER_STOP_SECONDS)
        for graceful_result in (0, timeout):
            with self.subTest(graceful_result=graceful_result):
                proc = mock.Mock(pid=1)
                proc.stdout = io.StringIO('{"east": "https://localhost:1", "west": "https://localhost:2"}\n')
                proc.poll.return_value = None
                proc.wait.side_effect = [graceful_result, 0]
                with mock.patch.object(subprocess, "Popen", return_value=proc):
                    run = RunningLauncher()
                expected = [mock.call.poll(), mock.call.send_signal(signal.CTRL_BREAK_EVENT),
                            mock.call.wait(timeout=LAUNCHER_STOP_SECONDS)]
                if graceful_result is timeout:
                    with self.assertRaisesRegex(RuntimeError, "cleanup may be incomplete"):
                        run.close()
                    expected += [mock.call.poll(), mock.call.kill(), mock.call.wait(timeout=30)]
                else:
                    run.close()
                self.assertEqual(expected, proc.mock_calls)
                self.assertTrue(proc.stdout.closed and run._stderr.closed)

    def test_signal_error_can_force_only_after_the_launcher_exhausts_its_grace_period(self):
        proc = mock.Mock(pid=1)
        proc.poll.return_value = None
        proc.send_signal.side_effect = OSError("signalling failed")
        proc.wait.side_effect = [subprocess.TimeoutExpired("launcher", LAUNCHER_STOP_SECONDS), 1]
        with self.assertRaisesRegex(RuntimeError, "cleanup may be incomplete"):
            stop_launcher(proc)
        self.assertEqual(
            [mock.call.poll(), mock.call.send_signal(signal.CTRL_BREAK_EVENT), mock.call.wait(timeout=LAUNCHER_STOP_SECONDS),
             mock.call.poll(), mock.call.kill(), mock.call.wait(timeout=30)],
            proc.mock_calls)

    def test_verify_timeout_waits_for_cleanup_before_raising_the_original_timeout(self):
        proc = mock.Mock(pid=1)
        proc.poll.return_value = None
        timed_out = subprocess.TimeoutExpired("launcher", 1)
        proc.wait.side_effect = [timed_out, 130]

        def launch_verify(args, *, stdout, stderr, creationflags):
            stdout.write("partial output\n")
            proc.send_signal.side_effect = lambda _: stderr.write("finished cleanup\n")
            return proc

        with mock.patch.object(subprocess, "Popen", side_effect=launch_verify) as launch:
            with self.assertRaises(subprocess.TimeoutExpired) as raised:
                run_launcher_verify(timeout=1)
        self.assertIs(timed_out, raised.exception)
        self.assertEqual("partial output\n", timed_out.stdout)
        self.assertEqual("finished cleanup\n", timed_out.stderr)
        self.assertIn("--verify", launch.call_args.args[0])
        self.assertTrue(launch.call_args.kwargs["creationflags"] & subprocess.CREATE_NEW_PROCESS_GROUP)
        proc.send_signal.assert_called_once_with(signal.CTRL_BREAK_EVENT)
        self.assertEqual([mock.call(timeout=1), mock.call(timeout=LAUNCHER_STOP_SECONDS)], proc.wait.call_args_list)
        proc.kill.assert_not_called()

    def test_a_launcher_that_cannot_be_started_leaves_no_open_handle(self):
        handle = io.BytesIO()
        with mock.patch.object(tempfile, "TemporaryFile", return_value=handle), \
                mock.patch.object(subprocess, "Popen", side_effect=FileNotFoundError("python")):
            with self.assertRaises(FileNotFoundError):
                RunningLauncher()
        self.assertTrue(handle.closed)


class SeedingTests(unittest.TestCase):
    """The pure parts of seeding: what is loaded, in what order, and the single rewrite."""

    def test_only_the_synthetic_canonical_reference_changes_and_patients_load_first(self):
        import start
        corpus = Path(start.__file__).resolve().parents[3] / "test" / "fixtures" / "federation-oracle" / "seeds" / "collision"
        east_base = "https://localhost:1234/tenant/1"
        planned = dict(zip(("east", "west"), start.collision_corpus(east_base)))
        for side in ("east", "west"):
            expected = json.loads((corpus / f"{side}.json").read_text(encoding="utf-8"))
            for resource in expected:
                if resource["id"] == "obs-dup-xref":  # the one known synthetic canonical reference
                    resource["subject"]["reference"] = east_base + "/Patient/dup"
            kinds = [r["resourceType"] for r in planned[side]]
            self.assertEqual(sorted(kinds, key=lambda kind: kind != "Patient"), kinds, "Patients must precede Observations")
            key = lambda r: (r["resourceType"], r["id"])
            self.assertEqual(sorted(expected, key=key), sorted(planned[side], key=key))

    def test_children_do_not_inherit_host_overrides_credentials_or_proxies(self):
        import start
        polluted = {"HTTPS_PROXY": "http://proxy.invalid", "ASPNETCORE_URLS": "http://0.0.0.0:1",
                    "SqlServer__ConnectionString": "Server=elsewhere", "AZURE_CLIENT_SECRET": "x",
                    "DOTNET_ENVIRONMENT": "Development", "CURL_CA_BUNDLE": "x", "FhirServer__Security__Enabled": "true"}
        saved = {k: os.environ.get(k) for k in polluted}
        os.environ.update(polluted)
        try:
            inherited = start.child_environment()
        finally:
            for key, value in saved.items():
                os.environ.pop(key, None)
                if value is not None:
                    os.environ[key] = value
        for key in polluted:
            self.assertNotIn(key.upper(), {k.upper() for k in inherited})
        self.assertIn("SYSTEMROOT", {k.upper() for k in inherited})


@unittest.skipUnless(NATIVE, NATIVE_SKIP)
class CatalogGuardTests(unittest.TestCase):
    """The west catalog is dropped only when it is still the one this run created.
    Opt-in: these create and drop real catalogs on the local Developer instance."""

    def drop(self, name):
        sql(DEVELOPER, "master", f"if db_id(N'{name}') is not null begin alter database [{name}] set single_user "
                                 f"with rollback immediate; drop database [{name}]; end")

    def test_a_replaced_catalog_is_refused_and_left_in_place(self):
        import start
        before = developer_databases()
        name = None
        try:
            with self.assertRaises(start.CleanupError):
                with start.west_catalog() as created:
                    name = created
                    self.drop(name)
                    sql(DEVELOPER, "master", f"create database [{name}]")  # same name, different identity
            self.assertIn(name, developer_databases(), "the replacement must not be dropped")
        finally:
            if name:
                self.drop(name)
        self.assertEqual(before, developer_databases())

    def test_a_catalog_removed_early_is_not_an_error(self):
        import start
        before = developer_databases()
        with start.west_catalog() as name:
            self.drop(name)
        self.assertEqual(before, developer_databases())

    def test_a_cleanup_that_fails_names_the_catalog_it_could_not_remove(self):
        import start
        real, name = start.sql, None

        def drop_fails(server, database, query):
            if "drop database" in query:
                raise start.LaunchError("SQL Server did not complete the requested operation")
            return real(server, database, query)
        try:
            with mock.patch.object(start, "sql", drop_fails):
                with self.assertRaises(start.CleanupError) as raised:
                    with start.west_catalog() as created:
                        name = created
            self.assertIn(name, str(raised.exception))
            self.assertIn(name, developer_databases(), "a catalog that could not be dropped stays, and was named")
        finally:
            if name:
                self.drop(name)

    def test_an_interrupt_while_the_catalog_is_created_names_the_candidate_and_propagates(self):
        import start
        real, err = start.sql, io.StringIO()

        def interrupted_after_create(server, database, query):
            result = real(server, database, query)
            if "create database" in query:
                raise KeyboardInterrupt
            return result
        before, name = developer_databases(), None
        try:
            with mock.patch.object(start, "sql", interrupted_after_create), contextlib.redirect_stderr(err):
                with self.assertRaises(KeyboardInterrupt):
                    with start.west_catalog():
                        self.fail("the interrupt must stop the launch before the body runs")
            named = re.search(r"FedProtoWest_[0-9a-f]{32}", err.getvalue())
            self.assertIsNotNone(named, "the possibly created catalog must be named: " + err.getvalue())
            name = named[0]
            self.assertIn(name, developer_databases(), "the launcher has no GUID to prove ownership, so it leaves it and says so")
        finally:
            if name:
                self.drop(name)
        self.assertEqual(before, developer_databases())


class PrivateLocalDbTests(unittest.TestCase):
    """An interrupt or failure while the private LocalDB instance is being created must not leak it silently."""

    @unittest.skipUnless(NATIVE, NATIVE_SKIP)  # the only case here that creates a real LocalDB instance
    def test_an_interrupt_just_after_the_instance_exists_still_removes_it(self):
        import start
        before, real = localdb_instances(), start.run_tool

        def interrupted_after_create(argv, **kwargs):
            done = real(argv, **kwargs)
            if argv[:2] == ["SqlLocalDB", "create"]:
                raise KeyboardInterrupt
            return done
        with tempfile.TemporaryDirectory() as workdir, mock.patch.object(start, "run_tool", interrupted_after_create):
            with self.assertRaises(KeyboardInterrupt):
                with start.private_localdb(Path(workdir)):
                    self.fail("the interrupt must stop the launch before the body runs")
        self.assertEqual(before, localdb_instances())

    def test_an_interrupt_before_any_instance_exists_cleans_up_quietly(self):
        import start
        calls = []

        def interrupted_create(argv, **kwargs):
            calls.append(argv[:2])
            if argv[:2] == ["SqlLocalDB", "create"]:
                raise KeyboardInterrupt
            return subprocess.CompletedProcess(argv, 0, b"", b"")
        with tempfile.TemporaryDirectory() as workdir, mock.patch.object(start, "run_tool", interrupted_create):
            with self.assertRaises(KeyboardInterrupt):
                with start.private_localdb(Path(workdir)):
                    pass
        self.assertNotIn(["SqlLocalDB", "stop"], calls)
        self.assertNotIn(["SqlLocalDB", "delete"], calls)

    def test_a_removal_failure_names_the_instance(self):
        import start
        name = []

        def stop_fails(argv, **kwargs):
            if argv[:2] == ["SqlLocalDB", "create"]:
                name.append(argv[2])
            if argv[:2] == ["SqlLocalDB", "stop"]:
                return subprocess.CompletedProcess(argv, 1, b"", b"")
            return subprocess.CompletedProcess(argv, 0, (name[0] if name else "").encode(), b"")
        with tempfile.TemporaryDirectory() as workdir, mock.patch.object(start, "run_tool", stop_fails):
            with self.assertRaises(start.CleanupError) as raised:
                with start.private_localdb(Path(workdir)):
                    pass
        self.assertIn(name[0], str(raised.exception))


@unittest.skipUnless(NATIVE, NATIVE_SKIP)
class NativeAcceptanceTests(unittest.TestCase):
    """Real stock Ignixa (east, private LocalDB) and Microsoft (west, Developer) shards."""

    def given(self, base, expected):
        patient = read_ok(base + "/Patient/dup")
        self.assertEqual(("Patient", "dup", [expected]),
                         (patient["resourceType"], patient["id"], patient["name"][0]["given"]))

    def test_run_mode_serves_the_collision_corpus_and_cleans_up_on_interrupt(self):
        instances, databases = localdb_instances(), developer_databases()
        run = RunningLauncher()
        try:
            self.assertIsNotNone(run.endpoints, "no endpoint line; stderr: " + run.stderr()[-500:])
            self.assertEqual({"east", "west"}, set(run.endpoints))
            east, west = run.endpoints["east"], run.endpoints["west"]
            self.assertRegex(east, f"^{EAST_BASE}$")
            self.assertRegex(west, f"^{WEST_BASE}$")

            statements = {side: read_ok(base + "/metadata") for side, base in (("east", east), ("west", west))}
            for statement in statements.values():
                self.assertEqual("CapabilityStatement", statement["resourceType"])
                self.assertTrue(statement["fhirVersion"].startswith("4.0"), statement["fhirVersion"])
            self.assertNotEqual(statements["east"].get("software"), statements["west"].get("software"))

            self.given(east, "Eli")
            self.given(west, "Gus")

            self.assertEqual(200, curl(east + "/Patient/east-only")[0])
            self.assertEqual(200, curl(west + "/Patient/west-only")[0])
            self.assertEqual(404, curl(east + "/Patient/west-only")[0])
            self.assertEqual(404, curl(west + "/Patient/east-only")[0])

            xref = read_ok(west + "/Observation/obs-dup-xref")
            self.assertEqual(("Observation", "obs-dup-xref"), (xref["resourceType"], xref["id"]))
            self.assertEqual(east + "/Patient/dup", xref["subject"]["reference"])
            target = read_ok(xref["subject"]["reference"])
            self.assertEqual(("Patient", "dup", ["Eli"]),
                             (target["resourceType"], target["id"], target["name"][0]["given"]))
            self.assertEqual("Patient/dup", read_ok(west + "/Observation/obs-dup")["subject"]["reference"])

            self.assertEqual(0, run.interrupt(), run.stderr()[-500:])
            self.assertEqual("", run.proc.stdout.read(), "nothing more may be written to stdout")
        finally:
            run.close()
        self.assertEqual((None, None), curl(east + "/metadata"), "east must be stopped")
        self.assertEqual((None, None), curl(west + "/metadata"), "west must be stopped")
        self.assertEqual(instances, localdb_instances())
        self.assertEqual(databases, developer_databases())

    def test_two_sequential_verify_runs_clean_only_their_own_resources(self):
        canary_instance = "FedCanary" + uuid.uuid4().hex[:12]
        canary_catalog = "FedCanary_" + uuid.uuid4().hex
        subprocess.run(["SqlLocalDB", "create", canary_instance, "15.0"], check=True, capture_output=True, timeout=60)
        try:
            sql(DEVELOPER, "master", f"create database [{canary_catalog}]")
            guid = database_guid(canary_catalog)
            instances, databases = localdb_instances(), developer_databases()
            self.assertIn(canary_instance, instances)
            self.assertIn(canary_catalog, databases)
            for attempt in (1, 2):
                with self.subTest(attempt=attempt):
                    done = run_launcher_verify()
                    self.assertEqual(0, done.returncode, done.stderr[-800:])
                    lines = done.stdout.splitlines()
                    self.assertEqual(1, len(lines), done.stdout)
                    result = json.loads(lines[0])
                    self.assertIs(True, result["verified"])
                    self.assertNotIn("east", result, "stopped shards must not be advertised")
                    self.assertEqual(instances, localdb_instances())
                    self.assertEqual(databases, developer_databases())
                    self.assertEqual(guid, database_guid(canary_catalog))
        finally:
            subprocess.run(["SqlLocalDB", "delete", canary_instance], capture_output=True, timeout=60)
            if database_guid(canary_catalog) == locals().get("guid", "?"):
                sql(DEVELOPER, "master", f"alter database [{canary_catalog}] set single_user with rollback immediate; "
                                         f"drop database [{canary_catalog}]")

    def test_a_child_dying_after_ready_stops_everything_and_cleans_up(self):
        instances, databases = localdb_instances(), developer_databases()
        run = RunningLauncher()
        try:
            self.assertIsNotNone(run.endpoints, "no endpoint line; stderr: " + run.stderr()[-500:])
            rows = dotnet_processes()
            victims = owned_hosts(rows, MICROSOFT_DLL, run.proc.pid)
            self.assertEqual(1, len(victims), "expected exactly this launcher's Microsoft child")
            self.assertEqual(1, len(owned_hosts(rows, IGNIXA_DLL, run.proc.pid)), "expected exactly this launcher's Ignixa child")
            os.kill(victims[0], signal.SIGTERM)  # a pid proven to be this test's own launcher's child
            self.assertNotEqual(0, run.wait())
            self.assertIn("exited", run.stderr())
        finally:
            run.close()
        self.assertEqual((None, None), curl(run.endpoints["east"] + "/metadata"), "the surviving shard must be stopped")
        rows = dotnet_processes()
        self.assertEqual([], owned_hosts(rows, MICROSOFT_DLL, run.proc.pid))
        self.assertEqual([], owned_hosts(rows, IGNIXA_DLL, run.proc.pid))
        self.assertEqual(instances, localdb_instances())
        self.assertEqual(databases, developer_databases())

    def test_an_interrupt_during_startup_cleans_up_whatever_exists_by_then(self):
        """Ctrl+Break is sent only once a barrier proves the resource under test is registered with the launcher's
        cleanup, never because a resource is merely visible: a catalog can exist before its guard is registered."""
        for point in ("east-registered", "west-registered"):
            with self.subTest(point):
                instances, databases = localdb_instances(), developer_databases()
                with tempfile.TemporaryDirectory() as folder:
                    script = Path(folder) / "barrier_harness.py"
                    script.write_text(BARRIER_HARNESS, encoding="utf-8")
                    proc = subprocess.Popen([sys.executable, "-B", str(script), str(HERE), point, *launcher_args()[3:]],
                                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                                            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
                    lines, barrier = [], queue.Queue()

                    def read_stderr():
                        for line in proc.stderr:
                            lines.append(line)
                            if line.startswith("BARRIER "):
                                barrier.put(line.split())
                        barrier.put(None)  # EOF: the launcher ended, with or without reaching the barrier
                    reader = threading.Thread(target=read_stderr, daemon=True)
                    try:
                        reader.start()
                        reached = barrier.get(timeout=240)
                        self.assertIsNotNone(reached, "the launcher ended before the barrier: " + "".join(lines)[-500:])
                        self.assertEqual(point, reached[1])
                        instance = reached[2]
                        self.assertRegex(instance, r"FedProto[0-9a-f]{12}")
                        self.assertNotIn(instance, instances)
                        self.assertIn(instance, localdb_instances(), "the east instance must exist at the barrier")
                        if point == "east-registered":
                            self.assertEqual(databases, developer_databases(), "no west catalog yet at the east barrier")
                        else:
                            catalog = reached[3]
                            self.assertRegex(catalog, r"FedProtoWest_[0-9a-f]{32}")
                            self.assertIn(catalog, developer_databases(), "the west catalog must exist at the barrier")
                            hosts = dotnet_processes()
                            self.assertEqual([], owned_hosts(hosts, IGNIXA_DLL, proc.pid) + owned_hosts(hosts, MICROSOFT_DLL, proc.pid),
                                             "no host may have started at the west barrier")
                        self.assertEqual(130, stop_launcher(proc))
                        self.assertEqual("", proc.stdout.read(), "no endpoint may be advertised")
                    finally:
                        try:
                            stop_launcher(proc)
                        finally:
                            if reader.ident is not None:
                                reader.join(timeout=30)
                            proc.stdout.close()
                            proc.stderr.close()
                    self.assertNotIn("Traceback", "".join(lines))
                self.assertEqual(instances, localdb_instances())
                self.assertEqual(databases, developer_databases())


if __name__ == "__main__":
    unittest.main()
