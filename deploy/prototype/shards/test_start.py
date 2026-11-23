"""Behavioural witness for the native prototype shard launcher.

Offline tests run anywhere on Windows with Python. The native acceptance tests
need the caller-built stock artifacts, named by these variables, and are
skipped (visibly) without them:

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
NATIVE_SKIP = "set FED_PROTO_DOTNET, FED_PROTO_IGNIXA_DLL and FED_PROTO_MICROSOFT_DLL"

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


class RunningLauncher:
    """start.py in run mode, in its own process group so the test can send it Ctrl+Break."""

    def __init__(self):
        self._stderr = tempfile.TemporaryFile()
        self.proc = subprocess.Popen(launcher_args(), stdout=subprocess.PIPE, stderr=self._stderr, text=True,
                                     creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
        first = queue.Queue()
        threading.Thread(target=lambda: first.put(self.proc.stdout.readline()), daemon=True).start()
        try:
            self.line = first.get(timeout=600)
        except queue.Empty:
            self.line = ""
            self.proc.kill()
        self.endpoints = json.loads(self.line) if self.line else None

    def stderr(self):
        self._stderr.seek(0)
        return self._stderr.read().decode(errors="replace")

    def interrupt(self):
        self.proc.send_signal(signal.CTRL_BREAK_EVENT)
        return self.wait()

    def wait(self, timeout=180):
        try:
            return self.proc.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait(timeout=30)
            raise

    def close(self):
        if self.proc.poll() is None:
            self.proc.kill()
            self.proc.wait(timeout=30)
        self.proc.stdout.close()
        self._stderr.close()


class PrerequisiteTests(unittest.TestCase):
    """Fail-closed behaviour; touches no SQL and starts no server."""

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

    def run_main(self, *flags, west_cleanup_error=None, sleep=None):
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
                    ("private_localdb", self.resource("east", r"(localdb)\FedProtoabc")),
                    ("west_catalog", self.resource("west", "FedProtoWest_x", west_cleanup_error)),
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
                text = marker if ("create database [FedProtoWest" in query or "select isnull" in query) else ""
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


class CatalogGuardTests(unittest.TestCase):
    """The west catalog is dropped only when it is still the one this run created."""

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
                    done = subprocess.run(launcher_args("--verify"), capture_output=True, text=True, timeout=900)
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
            self.assertNotEqual(0, run.wait(timeout=120))
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
        for appears in ("the LocalDB instance", "the west catalog"):
            with self.subTest(appears):
                instances, databases = localdb_instances(), developer_databases()
                with tempfile.TemporaryFile() as stderr:
                    proc = subprocess.Popen(launcher_args(), stdout=subprocess.PIPE, stderr=stderr, text=True,
                                            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
                    try:
                        deadline = time.monotonic() + 240
                        while time.monotonic() < deadline and proc.poll() is None:
                            now = localdb_instances() if appears == "the LocalDB instance" else developer_databases()
                            if now != (instances if appears == "the LocalDB instance" else databases):
                                break
                            time.sleep(0.2)
                        self.assertIsNone(proc.poll(), "the launcher ended before the resource appeared")
                        proc.send_signal(signal.CTRL_BREAK_EVENT)
                        self.assertEqual(130, proc.wait(timeout=240))
                        self.assertEqual("", proc.stdout.read(), "no endpoint may be advertised")
                    finally:
                        if proc.poll() is None:
                            proc.kill()
                            proc.wait(timeout=30)
                        proc.stdout.close()
                    stderr.seek(0)
                    self.assertNotIn("Traceback", stderr.read().decode(errors="replace"))
                self.assertEqual(instances, localdb_instances())
                self.assertEqual(databases, developer_databases())


if __name__ == "__main__":
    unittest.main()
