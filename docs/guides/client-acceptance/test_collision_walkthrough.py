"""Tests for collision_walkthrough.py.

Run from the repository root:

    python -B docs/guides/client-acceptance/test_collision_walkthrough.py

All sample output below is synthetic. Nothing here touches the network or starts a process.
"""

import contextlib
import io
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import collision_walkthrough as cw  # noqa: E402

FED = "https://fed.example/fhir/"
EAST = "https://shard-east.example/fhir/"
WEST = "https://shard-west.example/fhir/"

ALTERNATE_READ = "Patient/dup read via alternate from https://fed.example/fhir/shards/east1/Patient/dup"
DIRECT_READ = "Patient/dup read via direct from https://shard-east.example/fhir/Patient/dup"

GOOD = "\n".join([
    "page 1: matches=1 includes=0 outcomes=0",
    "match  https://shard-east.example/fhir/Patient/dup  (shard=east1)",
    "page 2: matches=1 includes=0 outcomes=0",
    "match  https://shard-west.example/fhir/Patient/dup  (shard=west1)",
    "summary: pages=2 matches=2 includes=0 outcomes=0",
    ALTERNATE_READ,
]) + "\n"


def expectation(read="alternate", east=EAST, west=WEST):
    return cw.Expectation(federation_base=FED, shard_bases=(east, west), read=read, max_pages=10)


def judge(stdout, exit_code=0, **kwargs):
    return cw.judge(stdout, exit_code, expectation(**kwargs))


class JudgeTests(unittest.TestCase):
    def assertFailsWith(self, verdict, *fragments):
        self.assertFalse(verdict.passed, "expected FAIL, got PASS")
        for fragment in fragments:
            self.assertTrue(
                any(fragment in failure for failure in verdict.failures),
                f"no failure mentions {fragment!r}: {verdict.failures}",
            )

    def assertPasses(self, verdict):
        self.assertTrue(verdict.passed, verdict.failures)
        self.assertEqual(verdict.failures, ())

    # PASS cases

    def test_alternate_read_passes(self):
        self.assertPasses(judge(GOOD))

    def test_direct_read_passes(self):
        self.assertPasses(judge(GOOD.replace(ALTERNATE_READ, DIRECT_READ), read="direct"))

    def test_trailing_empty_page_passes(self):
        text = GOOD.replace(
            "summary: pages=2",
            "page 3: matches=0 includes=0 outcomes=0\nsummary: pages=3",
        )
        self.assertPasses(judge(text))

    def test_expected_bases_are_canonicalised(self):
        self.assertPasses(judge(GOOD, east="HTTPS://Shard-East.example:443/fhir", west="https://shard-west.example/fhir"))

    # FAIL cases

    def test_exit_4_with_empty_stdout_explains_the_404(self):
        self.assertFailsWith(judge("", exit_code=4), "exit code 4: federation or HTTP error", "404: the federation does not serve search")

    def test_other_documented_exit_codes_are_explained(self):
        self.assertFailsWith(judge("", exit_code=3), "exit code 3: continuation expired", "410")
        self.assertFailsWith(judge("", exit_code=5), "exit code 5: link outside the allowed scope", "no alternate link")

    def test_missing_shard_is_named(self):
        text = GOOD.replace(
            "match  https://shard-west.example/fhir/Patient/dup  (shard=west1)\n", ""
        ).replace("page 2: matches=1", "page 2: matches=0").replace("matches=2 includes", "matches=1 includes")
        self.assertFailsWith(judge(text), "https://shard-west.example/fhir/Patient/dup")

    def test_same_full_url_on_both_pages_fails(self):
        text = GOOD.replace("shard-west.example", "shard-east.example")
        self.assertFailsWith(judge(text), "repeated", "https://shard-west.example/fhir/Patient/dup")

    def test_unexpected_url_is_named(self):
        text = GOOD.replace("shard-west.example/fhir/Patient/dup", "shard-other.example/fhir/Patient/dup")
        self.assertFailsWith(judge(text), "https://shard-other.example/fhir/Patient/dup")

    def test_two_matches_on_one_page_break_the_count_bound(self):
        text = "\n".join([
            "page 1: matches=2 includes=0 outcomes=0",
            "match  https://shard-east.example/fhir/Patient/dup  (shard=east1)",
            "match  https://shard-west.example/fhir/Patient/dup  (shard=west1)",
            "summary: pages=1 matches=2 includes=0 outcomes=0",
            ALTERNATE_READ,
        ]) + "\n"
        self.assertFailsWith(judge(text), "page 1: 2 matches, at most 1 allowed")

    def test_stopped_line_fails(self):
        text = GOOD.replace(
            "summary:", "stopped after 2 page(s): more pages are available (not fetched).\nsummary:"
        )
        self.assertFailsWith(judge(text), "the sample stopped after --max-pages=10")

    def test_page_header_count_must_match_its_lines(self):
        self.assertFailsWith(judge(GOOD.replace("page 1: matches=1", "page 1: matches=0")), "page 1: header declares")

    def test_page_numbers_must_run_in_order(self):
        self.assertFailsWith(judge(GOOD.replace("page 2:", "page 3:")), "page numbers do not run 1..P in order")

    def test_summary_includes_must_be_zero(self):
        self.assertFailsWith(judge(GOOD.replace("includes=0 outcomes=0\nPatient", "includes=1 outcomes=0\nPatient")), "summary includes=1, expected 0")

    def test_summary_outcomes_must_be_zero(self):
        self.assertFailsWith(judge(GOOD.replace("outcomes=0\nPatient", "outcomes=1\nPatient")), "summary outcomes=1, expected 0")

    def test_summary_pages_must_equal_page_lines(self):
        self.assertFailsWith(judge(GOOD.replace("summary: pages=2", "summary: pages=3")), "summary pages=3 but the sample printed 2 page lines")

    def test_unrecognised_line_fails(self):
        self.assertFailsWith(judge(GOOD + "debug: hello\n"), "line 7: not in the sample's output grammar")

    def test_identical_aliases_fail(self):
        self.assertFailsWith(judge(GOOD.replace("west1", "east1")), "carry the same shard alias")

    def test_missing_alias_fails(self):
        self.assertFailsWith(judge(GOOD.replace("  (shard=west1)", "")), "no (shard=<alias>) source-shard extension")

    def test_read_mode_must_be_the_requested_one(self):
        self.assertFailsWith(
            judge(GOOD.replace("via alternate", "via direct")), "read via direct, expected read via alternate"
        )

    def test_alternate_read_must_use_the_first_matchs_alias(self):
        text = GOOD.replace("shards/east1/", "shards/west1/")
        self.assertFailsWith(judge(text), "https://fed.example/fhir/shards/east1/Patient/dup")

    def test_direct_read_must_use_the_first_matchs_url(self):
        text = GOOD.replace(ALTERNATE_READ, DIRECT_READ.replace("shard-east", "shard-west"))
        self.assertFailsWith(judge(text, read="direct"), "https://shard-east.example/fhir/Patient/dup")

    def test_missing_read_line_fails(self):
        self.assertFailsWith(judge(GOOD.replace(ALTERNATE_READ + "\n", "")), "expected exactly one read line, found 0")

    def test_no_match_to_read_fails(self):
        text = GOOD.replace(ALTERNATE_READ, "read: there is no match to read.")
        self.assertFailsWith(judge(text), "no match to read")

    def test_missing_summary_fails(self):
        text = GOOD.replace("summary: pages=2 matches=2 includes=0 outcomes=0\n", "")
        self.assertFailsWith(judge(text), "expected exactly one summary line, found 0")

    def test_page_includes_and_outcomes_fail_even_when_the_summary_says_zero(self):
        text = GOOD.replace(
            "page 1: matches=1 includes=0 outcomes=0\n"
            "match  https://shard-east.example/fhir/Patient/dup  (shard=east1)\n",
            "page 1: matches=1 includes=1 outcomes=1\n"
            "match  https://shard-east.example/fhir/Patient/dup  (shard=east1)\n"
            "include  https://shard-east.example/fhir/Organization/o1  (shard=east1)\n"
            "outcome something went wrong\n",
        )
        self.assertFailsWith(
            judge(text),
            "page 1: includes=1, expected 0",
            "page 1: outcomes=1, expected 0",
        )

    def test_page_line_after_the_summary_fails(self):
        text = GOOD.replace(ALTERNATE_READ, "page 3: matches=0 includes=0 outcomes=0\n" + ALTERNATE_READ)
        self.assertFailsWith(judge(text), "line 6: a page line appears after the summary")

    def test_read_line_before_the_summary_fails(self):
        text = GOOD.replace(ALTERNATE_READ + "\n", "").replace("summary:", ALTERNATE_READ + "\nsummary:")
        self.assertFailsWith(judge(text), "line 5: the read line appears before the summary")

    def test_entry_line_before_any_page_fails(self):
        text = "match  https://shard-east.example/fhir/Patient/dup  (shard=east1)\n" + GOOD
        self.assertFailsWith(judge(text), "line 1: a match line is outside any page")

    def test_search_ok_but_read_failed_reports_both_reasons(self):
        text = GOOD.replace(ALTERNATE_READ + "\n", "")
        self.assertFailsWith(
            judge(text, exit_code=4),
            "exit code 4: federation or HTTP error",
            "expected exactly one read line, found 0",
        )

    def test_all_failures_are_collected(self):
        text = GOOD.replace("west1", "east1").replace("via alternate", "via direct")
        verdict = judge(text)
        self.assertFailsWith(verdict, "carry the same shard alias", "read via direct, expected read via alternate")
        self.assertGreaterEqual(len(verdict.failures), 2)


class SampleArgumentsTests(unittest.TestCase):
    def test_alternate_has_no_direct_shard(self):
        args = cw.sample_arguments(expectation("alternate"))
        self.assertNotIn("--direct-shard", args)
        self.assertEqual(args[args.index("--read") + 1], "alternate")

    def test_direct_passes_both_shard_bases(self):
        args = cw.sample_arguments(expectation("direct"))
        values = [args[i + 1] for i, a in enumerate(args) if a == "--direct-shard"]
        self.assertEqual(values, [EAST, WEST])

    def test_always_searches_patient_by_name_with_count_one(self):
        for mode in ("alternate", "direct"):
            args = cw.sample_arguments(expectation(mode))
            self.assertEqual(args[args.index("--type") + 1], "Patient")
            params = [args[i + 1] for i, a in enumerate(args) if a == "--param"]
            self.assertEqual(params, ["name=Orchid", "_count=1"])
            self.assertEqual(args[args.index("--base") + 1], FED)
            self.assertEqual(args[args.index("--max-pages") + 1], "10")


class CommandTests(unittest.TestCase):
    def test_build_command_builds_and_asks_msbuild_for_the_assembly_path(self):
        command = cw.build_command("dotnet", "C:/proj")
        self.assertEqual(
            command,
            ["dotnet", "build", "C:/proj", "-c", "Release", "--nologo", "-t:Build", "-getProperty:TargetPath"],
        )
        # -getProperty alone only evaluates the project; the explicit target makes it compile.
        self.assertIn("-t:Build", command)

    def test_run_command_runs_the_assembly_directly_in_one_process(self):
        command = cw.run_command("dotnet", "C:/out/Sample.dll", expectation())
        self.assertEqual(command[:2], ["dotnet", "C:/out/Sample.dll"])
        self.assertEqual(command[2:], cw.sample_arguments(expectation()))
        self.assertNotIn("run", command)

    def test_target_path_is_the_last_non_empty_line(self):
        self.assertEqual(cw.target_path("\nC:\\out\\Sample.dll\r\n\r\n"), "C:\\out\\Sample.dll")

    def test_target_path_is_none_for_empty_output(self):
        self.assertIsNone(cw.target_path(" \n"))


class ParseArgumentsTests(unittest.TestCase):
    def parse(self, *argv):
        return cw.parse_arguments(list(argv))

    def usage_error(self, *argv):
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr), self.assertRaises(SystemExit) as raised:
            self.parse(*argv)
        self.assertEqual(raised.exception.code, 2)
        return stderr.getvalue()

    def valid(self, base=FED, east=EAST, west=WEST, *extra):
        return ["--base", base, "--shard-base", east, "--shard-base", west, *extra]

    def test_valid_arguments_add_trailing_slashes(self):
        parsed = self.parse(*self.valid("https://fed.example/fhir", "https://a.example/x", "https://b.example/y"))
        self.assertEqual(parsed.expectation.federation_base, "https://fed.example/fhir/")
        self.assertEqual(parsed.expectation.shard_bases, ("https://a.example/x/", "https://b.example/y/"))
        self.assertEqual(parsed.expectation.read, "alternate")
        self.assertEqual(parsed.expectation.max_pages, 10)
        self.assertEqual(parsed.dotnet, "dotnet")

    def test_http_base_is_rejected(self):
        self.usage_error(*self.valid(base="http://fed.example/fhir/"))

    def test_base_with_query_is_rejected_without_echoing_it(self):
        message = self.usage_error(*self.valid(base="https://fed.example/fhir/?secret=abc"))
        self.assertIn("--base", message)
        self.assertNotIn("secret", message)

    def test_base_with_userinfo_or_fragment_is_rejected(self):
        self.usage_error(*self.valid(base="https://user:pw@fed.example/fhir/"))
        self.usage_error(*self.valid(base="https://fed.example/fhir/#frag"))

    def test_unknown_arguments_are_rejected_without_echoing_them(self):
        message = self.usage_error(*self.valid(FED, EAST, WEST, "--token", "s3cr3t-value"))
        self.assertIn("unrecognised argument(s)", message)
        self.assertNotIn("s3cr3t-value", message)
        self.assertNotIn("--token", message)

    def test_one_shard_base_is_rejected(self):
        self.usage_error("--base", FED, "--shard-base", EAST)

    def test_identical_shard_bases_after_canonicalisation_are_rejected(self):
        self.usage_error(*self.valid(east="https://Shard-East.example:443/fhir", west=EAST))

    def test_max_pages_below_three_is_rejected(self):
        self.usage_error(*self.valid(FED, EAST, WEST, "--max-pages", "2"))

    def test_read_mode_is_validated(self):
        self.usage_error(*self.valid(FED, EAST, WEST, "--read", "none"))
        self.assertEqual(self.parse(*self.valid(FED, EAST, WEST, "--read", "direct")).expectation.read, "direct")


if __name__ == "__main__":
    unittest.main()
