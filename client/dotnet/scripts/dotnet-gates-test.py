#!/usr/bin/env python3
"""Tests for dotnet-gates.py, with stand-in dotnet hosts and reports."""

import contextlib
import importlib.util
import io
import json
import os
import stat
import sys
import tempfile
import textwrap
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("dotnet_gates", os.path.join(HERE, "dotnet-gates.py"))
gates = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gates)

RUNTIMES_10 = (
    "Microsoft.AspNetCore.App 10.0.13 [/x/shared/Microsoft.AspNetCore.App]\n"
    "Microsoft.NETCore.App 10.0.13 [/x/shared/Microsoft.NETCore.App]\n"
)
RUNTIMES_9 = "Microsoft.NETCore.App 9.0.10 [/x/shared/Microsoft.NETCore.App]\n"


def report(classes, valid=10, covered=5):
    rows = "".join(f'<class name="C{i}" filename="{f}" />' for i, f in enumerate(classes))
    return (
        f'<coverage lines-covered="{covered}" lines-valid="{valid}"><packages><package name="P">'
        f"<classes>{rows}</classes></package></packages></coverage>"
    )


class GatesTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.global_json = os.path.join(self.tmp.name, "global.json")
        with open(self.global_json, "w", encoding="utf-8") as f:
            json.dump({"sdk": {"version": "10.0.402"}}, f)

    def host(self, version, runtimes, version_exit=0):
        """A stand-in dotnet printing a version and a runtime list."""
        path = os.path.join(self.tmp.name, "dotnet")
        with open(path, "w", encoding="utf-8") as f:
            f.write(textwrap.dedent(f"""\
                #!{sys.executable}
                import sys
                if sys.argv[1] == "--version":
                    print({version!r}); sys.exit({version_exit})
                sys.stdout.write({runtimes!r})
                """))
        os.chmod(path, os.stat(path).st_mode | stat.S_IEXEC)
        return path

    def write(self, name, text):
        path = os.path.join(self.tmp.name, name)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
        return path

    def test_preflight_accepts_the_pinned_sdk_with_a_net10_runtime(self):
        sdk, runtimes = gates.preflight(self.host("10.0.402", RUNTIMES_10), self.global_json)
        self.assertEqual(sdk, "10.0.402")
        self.assertEqual(runtimes, ["10.0.13"])

    def test_preflight_rejects_another_sdk(self):
        with self.assertRaisesRegex(gates.GateError, "SDK '10.0.403', but global.json pins '10.0.402'"):
            gates.preflight(self.host("10.0.403", RUNTIMES_10), self.global_json)

    def test_preflight_rejects_a_host_that_cannot_select_the_pinned_sdk(self):
        with self.assertRaisesRegex(gates.GateError, "exited 155"):
            gates.preflight(self.host("", RUNTIMES_10, version_exit=155), self.global_json)

    def test_preflight_rejects_an_absent_net10_runtime(self):
        with self.assertRaisesRegex(gates.GateError, "no .NET 10 runtime"):
            gates.preflight(self.host("10.0.402", RUNTIMES_9), self.global_json)

    def test_preflight_rejects_a_missing_host(self):
        with self.assertRaisesRegex(gates.GateError, "cannot run"):
            gates.preflight(os.path.join(self.tmp.name, "absent"), self.global_json)

    def test_preflight_rejects_a_global_json_without_a_version(self):
        with open(self.global_json, "w", encoding="utf-8") as f:
            json.dump({"test": {}}, f)
        with self.assertRaisesRegex(gates.GateError, "pins no sdk.version"):
            gates.preflight(self.host("10.0.402", RUNTIMES_10), self.global_json)

    def test_the_real_global_json_pins_an_sdk(self):
        self.assertRegex(gates.pinned_sdk(), r"^10\.0\.\d+$")

    def test_coverage_accepts_handwritten_library_code(self):
        self.write("run/coverage.cobertura.1.xml", report(["/_/client/dotnet/src/Lib/A.cs"]))
        [(_, covered, valid)] = gates.coverage(os.path.join(self.tmp.name, "run"), ["Lib/Wire/Sysml.cs"])
        self.assertEqual((covered, valid), (5, 10))

    def test_coverage_accepts_windows_paths(self):
        self.write("run/coverage.cobertura.1.xml", report([r"C:\r\client\dotnet\src\Lib\A.cs"]))
        gates.coverage(os.path.join(self.tmp.name, "run"))

    def test_coverage_rejects_a_run_without_a_report(self):
        os.makedirs(os.path.join(self.tmp.name, "run"))
        with self.assertRaisesRegex(gates.GateError, "holds no Cobertura report"):
            gates.coverage(os.path.join(self.tmp.name, "run"))

    def test_coverage_rejects_an_empty_report(self):
        self.write("run/coverage.cobertura.1.xml", report([], valid=0, covered=0))
        with self.assertRaisesRegex(gates.GateError, "measures no code"):
            gates.coverage(os.path.join(self.tmp.name, "run"))

    def test_coverage_rejects_a_report_with_no_valid_lines(self):
        self.write("run/coverage.cobertura.1.xml", report(["/r/client/dotnet/src/A.cs"], valid=0))
        with self.assertRaisesRegex(gates.GateError, "measures no code"):
            gates.coverage(os.path.join(self.tmp.name, "run"))

    def test_coverage_rejects_generated_code(self):
        self.write("run/coverage.cobertura.1.xml", report([
            "/r/client/dotnet/src/Lib/A.cs", "/r/client/dotnet/src/Lib/Wire/Sysml.cs",
        ]))
        with self.assertRaisesRegex(gates.GateError, "measures generated .*Wire/Sysml.cs"):
            gates.coverage(os.path.join(self.tmp.name, "run"), ["Lib/Wire/Sysml.cs"])

    def test_coverage_rejects_code_outside_the_library_sources(self):
        self.write("run/coverage.cobertura.1.xml", report(["/r/client/dotnet/test/T/ATests.cs"]))
        with self.assertRaisesRegex(gates.GateError, "outside client/dotnet/src/"):
            gates.coverage(os.path.join(self.tmp.name, "run"))

    def test_coverage_rejects_a_report_that_is_not_cobertura(self):
        self.write("run/coverage.cobertura.1.xml", "<testsuites />")
        with self.assertRaisesRegex(gates.GateError, "not a Cobertura report"):
            gates.coverage(os.path.join(self.tmp.name, "run"))

    def test_coverage_rejects_a_report_that_is_not_xml(self):
        self.write("run/coverage.cobertura.1.xml", "<coverage")
        with self.assertRaisesRegex(gates.GateError, "is not XML"):
            gates.coverage(os.path.join(self.tmp.name, "run"))

    def test_coverage_checks_every_report(self):
        self.write("run/a/coverage.cobertura.1.xml", report(["/r/client/dotnet/src/A.cs"]))
        self.write("run/b/coverage.cobertura.2.xml", report([], valid=0))
        with self.assertRaisesRegex(gates.GateError, "b.coverage.cobertura.2.xml measures no code"):
            gates.coverage(os.path.join(self.tmp.name, "run"))

    def test_main_reports_a_failed_gate(self):
        os.makedirs(os.path.join(self.tmp.name, "run"))
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            self.assertEqual(gates.main(["coverage", os.path.join(self.tmp.name, "run")]), 1)
        self.assertIn("holds no Cobertura report", stderr.getvalue())


if __name__ == "__main__":
    unittest.main()
