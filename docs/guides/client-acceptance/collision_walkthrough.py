"""Read-only acceptance check for the two-shard "collision" walkthrough.

Runs the real sample client (samples/Ignixa.Federation.SampleClient) against an operator's
disposable, seeded fixture federation and judges the sample's stdout. The driver makes no HTTP
calls itself and never touches credentials: the environment is passed through unchanged, so
FEDERATION_ACCESS_TOKEN / SHARD_ACCESS_TOKEN set in the operator's shell reach the sample, which
confines them. Nothing is written to the federation or the shards.

Scenario (oracle case collision.tagged-union): Patient?name=Orchid&_count=1 against a federation
whose two shards both hold Patient/dup must return two match entries, one per shard, with distinct
fullUrls <east base>Patient/dup and <west base>Patient/dup.

The federation does not serve search yet (the merged API answers 404), so today a live run ends in
FAIL. That is the honest result.

Run: python -B docs/guides/client-acceptance/collision_walkthrough.py --help
Exit codes: 0 PASS, 1 FAIL, 2 usage error.
"""

import argparse
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlsplit

SAMPLE_PROJECT = Path(__file__).resolve().parent.parents[2] / "samples" / "Ignixa.Federation.SampleClient"
BUILD_TIMEOUT_SECONDS = 600
RUN_TIMEOUT_SECONDS = 300
RESOURCE = "Patient/dup"

SAMPLE_EXIT_CODES = {
    2: "usage error",
    3: "continuation expired (410): restart the search",
    4: "federation or HTTP error (for example 404: the federation does not serve search)",
    5: "link outside the allowed scope, or the first match has no alternate link",
}


@dataclass(frozen=True)
class Expectation:
    federation_base: str
    shard_bases: tuple[str, str]
    read: str
    max_pages: int
    count: int = 1


@dataclass(frozen=True)
class Verdict:
    failures: tuple[str, ...]

    @property
    def passed(self) -> bool:
        return not self.failures


@dataclass(frozen=True)
class Arguments:
    expectation: Expectation
    dotnet: str


# URLs

def _with_slash(base: str) -> str:
    return base if base.endswith("/") else base + "/"


def canonical(url: str) -> str:
    """Lowercase scheme and host, drop port 443, keep the path exactly. For comparison only."""
    parts = urlsplit(url)
    try:
        port = parts.port
    except ValueError:
        return url
    host = parts.hostname or ""
    if ":" in host:
        host = f"[{host}]"
    netloc = host if port in (None, 443) else f"{host}:{port}"
    return f"{parts.scheme.lower()}://{netloc}{parts.path}"


# Command line

def sample_arguments(expectation: Expectation) -> list[str]:
    """Arguments for the sample client (everything after `dotnet run ... --`)."""
    args = [
        "--base", expectation.federation_base,
        "--type", "Patient",
        "--param", "name=Orchid",
        "--param", f"_count={expectation.count}",
        "--max-pages", str(expectation.max_pages),
        "--read", expectation.read,
    ]
    if expectation.read == "direct":
        for shard in expectation.shard_bases:
            args += ["--direct-shard", shard]
    return args


def _https_base(value: str) -> str:
    problem = argparse.ArgumentTypeError(
        "must be an absolute https URL with no userinfo, query or fragment"
    )
    try:
        parts = urlsplit(value)
        parts.port  # noqa: B018 - raises ValueError on a malformed port
    except ValueError:
        raise problem from None
    if parts.scheme.lower() != "https" or not parts.hostname:
        raise problem
    if "@" in parts.netloc or "?" in value or "#" in value:
        raise problem
    return _with_slash(value)


def _max_pages(value: str) -> int:
    if not (value.isascii() and value.isdigit()) or int(value) < 3:
        raise argparse.ArgumentTypeError("must be a whole number of at least 3")
    return int(value)


def parse_arguments(argv: list[str] | None = None) -> Arguments:
    parser = argparse.ArgumentParser(
        prog="collision_walkthrough.py",
        description="Read-only acceptance check for the two-shard collision walkthrough. "
        "Tokens are never read by this script; set FEDERATION_ACCESS_TOKEN / SHARD_ACCESS_TOKEN "
        "in your shell if the sample needs them.",
    )
    parser.add_argument("--base", required=True, type=_https_base, help="federation base URL (https)")
    parser.add_argument(
        "--shard-base", action="append", default=[], type=_https_base,
        help="shard base URL (https); give exactly two",
    )
    parser.add_argument("--read", choices=("alternate", "direct"), default="alternate")
    parser.add_argument("--max-pages", type=_max_pages, default=10, help="default 10, at least 3")
    parser.add_argument("--dotnet", default="dotnet", help="dotnet executable (default: dotnet)")
    parsed, extras = parser.parse_known_args(argv)
    if extras:
        parser.error("unrecognised argument(s); see --help")

    if len(parsed.shard_base) != 2:
        parser.error("--shard-base must be given exactly twice")
    if len({canonical(base) for base in parsed.shard_base}) != 2:
        parser.error("the two --shard-base values must be different")

    expectation = Expectation(parsed.base, tuple(parsed.shard_base), parsed.read, parsed.max_pages)
    return Arguments(expectation, parsed.dotnet)


# The verdict

_PAGE = re.compile(r"page (\d+): matches=(\d+) includes=(\d+) outcomes=(\d+)")
_ENTRY = re.compile(r"(match|include)  (\S+?)(?:  \(shard=([^)\s]+)\))?")
_OUTCOME = re.compile(r"outcome .*")
_STOPPED = re.compile(r"stopped after (\d+) page\(s\): more pages are available \(not fetched\)\.")
_SUMMARY = re.compile(r"summary: pages=(\d+) matches=(\d+) includes=(\d+) outcomes=(\d+)")
_READ = re.compile(r"(\w+/\S+) read via (alternate|direct) from (\S+)")
_NO_READ = "read: there is no match to read."


@dataclass
class _Page:
    number: int
    declared: tuple[int, int, int]
    matches: list[tuple[str, str | None]]
    includes: int = 0
    outcomes: int = 0


@dataclass
class _Output:
    pages: list[_Page]
    summaries: list[tuple[int, int, int, int]]
    reads: list[tuple[str, str, str]]
    stopped: bool = False
    no_read: bool = False


def _parse(stdout: str, failures: list[str]) -> _Output:
    out = _Output(pages=[], summaries=[], reads=[])
    for number, line in enumerate(stdout.splitlines(), start=1):
        after_summary = bool(out.summaries)
        if m := _PAGE.fullmatch(line):
            if after_summary:
                failures.append(f"line {number}: a page line appears after the summary")
            n, matches, includes, outcomes = map(int, m.groups())
            out.pages.append(_Page(n, (matches, includes, outcomes), []))
        elif m := _ENTRY.fullmatch(line):
            if after_summary or not out.pages:
                failures.append(f"line {number}: a {m.group(1)} line is outside any page")
            elif m.group(1) == "match":
                out.pages[-1].matches.append((m.group(2), m.group(3)))
            else:
                out.pages[-1].includes += 1
        elif _OUTCOME.fullmatch(line):
            if after_summary or not out.pages:
                failures.append(f"line {number}: an outcome line is outside any page")
            else:
                out.pages[-1].outcomes += 1
        elif _STOPPED.fullmatch(line):
            out.stopped = True
        elif m := _SUMMARY.fullmatch(line):
            out.summaries.append(tuple(map(int, m.groups())))
        elif m := _READ.fullmatch(line):
            if not after_summary:
                failures.append(f"line {number}: the read line appears before the summary")
            out.reads.append(m.groups())
        elif line == _NO_READ:
            out.no_read = True
        else:
            failures.append(f"line {number}: not in the sample's output grammar")
    return out


def _check_pages(out: _Output, exp: Expectation, failures: list[str]) -> None:
    if not out.pages:
        failures.append("the sample printed no page lines")
    if [p.number for p in out.pages] != list(range(1, len(out.pages) + 1)):
        failures.append("page numbers do not run 1..P in order")
    for page in out.pages:
        actual = (len(page.matches), page.includes, page.outcomes)
        if actual != page.declared:
            failures.append(
                f"page {page.number}: header declares matches/includes/outcomes={page.declared} "
                f"but the lines that follow number {actual}"
            )
        if len(page.matches) > exp.count:
            failures.append(f"page {page.number}: {len(page.matches)} matches, at most {exp.count} allowed (_count)")
        _, declared_includes, declared_outcomes = page.declared
        if declared_includes > 0:
            failures.append(f"page {page.number}: includes={declared_includes}, expected 0 (no _include was requested)")
        if declared_outcomes > 0:
            failures.append(
                f"page {page.number}: outcomes={declared_outcomes}, expected 0 (any outcome means the run is not clean)"
            )
    if out.stopped:
        failures.append(
            f"the sample stopped after --max-pages={exp.max_pages} with more pages available; "
            "the search must end by itself"
        )


def _check_summary(out: _Output, exp: Expectation, failures: list[str]) -> None:
    if len(out.summaries) != 1:
        failures.append(f"expected exactly one summary line, found {len(out.summaries)}")
        return
    pages, matches, includes, outcomes = out.summaries[0]
    if pages != len(out.pages):
        failures.append(f"summary pages={pages} but the sample printed {len(out.pages)} page lines")
    if matches != len(exp.shard_bases):
        failures.append(f"summary matches={matches}, expected {len(exp.shard_bases)}")
    if includes != 0:
        failures.append(f"summary includes={includes}, expected 0 (no _include was requested)")
    if outcomes != 0:
        failures.append(f"summary outcomes={outcomes}, expected 0 (any outcome means the run is not clean)")


def _check_matches(out: _Output, exp: Expectation, failures: list[str]) -> list[tuple[str, str | None]]:
    matches = [match for page in out.pages for match in page.matches]
    urls = [canonical(url) for url, _ in matches]
    expected = {canonical(_with_slash(base) + RESOURCE) for base in exp.shard_bases}

    if len(matches) != len(expected):
        failures.append(f"expected exactly {len(expected)} match lines, found {len(matches)}")
    for url in sorted({u for u in urls if urls.count(u) > 1}):
        failures.append(f"fullUrl repeated across match lines: {url}")
    for url in sorted(expected - set(urls)):
        failures.append(f"missing match fullUrl: {url}")
    for url in sorted(set(urls) - expected):
        failures.append(f"unexpected match fullUrl: {url}")

    aliases = [alias for _, alias in matches]
    if any(alias is None for alias in aliases):
        failures.append("a match line has no (shard=<alias>) source-shard extension")
    elif len(set(aliases)) != len(aliases):
        failures.append("match lines carry the same shard alias; they must come from different shards")
    return matches


def _check_read(
    out: _Output, exp: Expectation, matches: list[tuple[str, str | None]], failures: list[str]
) -> None:
    if out.no_read:
        failures.append("the sample reported there is no match to read")
    if len(out.reads) != 1:
        if not out.no_read:
            failures.append(f"expected exactly one read line, found {len(out.reads)}")
        return
    resource, mode, url = out.reads[0]
    if resource != RESOURCE:
        failures.append(f"read line is for {resource}, expected {RESOURCE}")
    if mode != exp.read:
        failures.append(f"read via {mode}, expected read via {exp.read}")
    if not matches:
        return
    first_url, first_alias = matches[0]
    if exp.read == "direct":
        expected = canonical(first_url)
    elif first_alias is not None:
        expected = canonical(f"{_with_slash(exp.federation_base)}shards/{first_alias}/{RESOURCE}")
    else:
        return
    if canonical(url) != expected:
        failures.append(f"read from {canonical(url)}, expected {expected}")


def judge(stdout: str, exit_code: int, expectation: Expectation) -> Verdict:
    """Judge the sample's stdout and exit code. Pure: no I/O."""
    failures: list[str] = []
    if exit_code != 0:
        reason = SAMPLE_EXIT_CODES.get(exit_code, "unexpected exit code")
        failures.append(f"sample exited with exit code {exit_code}: {reason}")
        if not stdout.strip():
            return Verdict(tuple(failures))

    out = _parse(stdout, failures)
    _check_pages(out, expectation, failures)
    _check_summary(out, expectation, failures)
    matches = _check_matches(out, expectation, failures)
    _check_read(out, expectation, matches, failures)
    return Verdict(tuple(failures))


# Running the sample
#
# The sample is run as `dotnet <built assembly> <args>`: one process, no `dotnet run` parent. A
# parent that spawns the sample would keep the capture pipes open after a timeout kills it, and on
# Windows subprocess.run would then wait forever.

def build_command(dotnet: str, project: str) -> list[str]:
    """Build the sample and print only the path of its assembly.

    -getProperty alone only evaluates the project and compiles nothing, so the Build target is
    requested explicitly.
    """
    return [dotnet, "build", project, "-c", "Release", "--nologo", "-t:Build", "-getProperty:TargetPath"]


def target_path(build_stdout: str) -> str | None:
    lines = [line.strip() for line in build_stdout.splitlines() if line.strip()]
    return lines[-1] if lines else None


def run_command(dotnet: str, assembly: str, expectation: Expectation) -> list[str]:
    return [dotnet, assembly, *sample_arguments(expectation)]


def _run(command: list[str], timeout: int) -> subprocess.CompletedProcess:
    return subprocess.run(
        command, capture_output=True, text=True, encoding="utf-8", errors="replace",
        timeout=timeout, check=False,
    )


def _build(dotnet: str) -> tuple[str | None, Verdict | None]:
    """Return (assembly path, None) on success, or (None, FAIL verdict)."""
    try:
        build = _run(build_command(dotnet, str(SAMPLE_PROJECT)), BUILD_TIMEOUT_SECONDS)
    except (OSError, subprocess.TimeoutExpired) as error:
        return None, Verdict((f"could not build the sample: {type(error).__name__}",))
    if build.returncode != 0:
        print("\n".join((build.stdout + build.stderr).splitlines()[-20:]))
        return None, Verdict((f"sample build failed (exit {build.returncode})",))
    assembly = target_path(build.stdout)
    if assembly is None or not Path(assembly).is_file():
        return None, Verdict(("sample build did not produce an assembly at the reported TargetPath",))
    return assembly, None


def _execute(args: Arguments) -> Verdict:
    assembly, failed = _build(args.dotnet)
    if failed:
        return failed
    try:
        result = _run(run_command(args.dotnet, assembly, args.expectation), RUN_TIMEOUT_SECONDS)
    except (OSError, subprocess.TimeoutExpired) as error:
        return Verdict((f"could not run the sample: {type(error).__name__}",))

    for line in result.stdout.splitlines():
        print(line)
    for line in result.stderr.splitlines():
        print(f"sample stderr: {line}")
    return judge(result.stdout, result.returncode, args.expectation)


def main(argv: list[str] | None = None) -> int:
    # Redirected output must not crash on a character the console encoding lacks.
    sys.stdout.reconfigure(errors="replace")
    sys.stderr.reconfigure(errors="replace")
    verdict = _execute(parse_arguments(argv))
    print("verdict: PASS" if verdict.passed else "verdict: FAIL")
    for failure in verdict.failures:
        print(f"- {failure}")
    return 0 if verdict.passed else 1


if __name__ == "__main__":
    sys.exit(main())