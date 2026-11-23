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

import json
import os
import queue
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import unittest
import uuid
from pathlib import Path

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


def developer_pids(dll):
    """Process ids of dotnet.exe hosts running this exact dll (the launcher's children)."""
    script = ("Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" | "
              "Where-Object { $_.CommandLine -like ('*' + $env:FED_WITNESS_DLL + '*') } | "
              "ForEach-Object { $_.ProcessId }")
    done = subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True,
                          timeout=60, env={**os.environ, "FED_WITNESS_DLL": str(dll)})
    return [int(p) for p in done.stdout.split()]


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
            victims = developer_pids(MICROSOFT_DLL)
            self.assertEqual(1, len(victims), "expected exactly the launcher's Microsoft child")
            os.kill(victims[0], signal.SIGTERM)
            self.assertNotEqual(0, run.wait(timeout=120))
            self.assertIn("exited", run.stderr())
        finally:
            run.close()
        self.assertEqual((None, None), curl(run.endpoints["east"] + "/metadata"), "the surviving shard must be stopped")
        self.assertEqual([], developer_pids(IGNIXA_DLL))
        self.assertEqual(instances, localdb_instances())
        self.assertEqual(databases, developer_databases())


if __name__ == "__main__":
    unittest.main()
