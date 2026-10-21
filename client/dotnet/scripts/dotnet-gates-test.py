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
IDENTITY = "OpenMBEE.OpenSysML 0.10.1 (.NETCoreApp,Version=v10.0)"


def report(classes, valid=10, covered=5, sources=()):
    rows = "".join(f'<class name="C{i}" filename="{f}" />' for i, f in enumerate(classes))
    listed = "".join(f"<source>{s}</source>" for s in sources)
    return (
        f'<coverage lines-covered="{covered}" lines-valid="{valid}"><sources>{listed}</sources>'
        f'<packages><package name="P"><classes>{rows}</classes></package></packages></coverage>'
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

    def test_coverage_resolves_filenames_against_the_sources(self):
        self.write("run/coverage.cobertura.1.xml", report(
            ["A.cs", "Inner/B.cs"], sources=["/_/client/dotnet/src/Lib/Internal/"]))
        gates.coverage(os.path.join(self.tmp.name, "run"))

    def test_coverage_resolves_windows_sources(self):
        self.write("run/coverage.cobertura.1.xml", report(["A.cs"], sources=["C:\\r\\client\\dotnet\\src\\Lib\\"]))
        gates.coverage(os.path.join(self.tmp.name, "run"))

    def test_coverage_rejects_a_relative_filename_outside_the_library_sources(self):
        self.write("run/coverage.cobertura.1.xml", report(["ATests.cs"], sources=["/r/client/dotnet/test/T/"]))
        with self.assertRaisesRegex(gates.GateError, "measures ATests.cs, outside client/dotnet/src/"):
            gates.coverage(os.path.join(self.tmp.name, "run"))

    def test_coverage_rejects_relative_generated_code(self):
        self.write("run/coverage.cobertura.1.xml", report(
            ["Internal/A.cs", "Wire/Sysml.cs"], sources=["/r/client/dotnet/src/Lib/"]))
        with self.assertRaisesRegex(gates.GateError, "measures generated Wire/Sysml.cs"):
            gates.coverage(os.path.join(self.tmp.name, "run"), ["Lib/Wire/Sysml.cs"])

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

    def executable(self, name, body):
        path = self.write(name, f"#!{sys.executable}\n" + textwrap.dedent(body))
        os.chmod(path, os.stat(path).st_mode | stat.S_IEXEC)
        return path

    def sample_host(self, service_line="sysml-grpc v1.0.0, 3 capabilities", identity=IDENTITY):
        """A stand-in dotnet whose sample prints its identity and, with a service named, a service line."""
        seen = os.path.join(self.tmp.name, "seen-service")
        return self.executable("sample-dotnet", f"""\
            import os
            print({identity!r})
            service = os.environ.get("OPENSYSML_SERVICE")
            if service:
                open({seen!r}, "w").write(service)
                if {service_line!r}:
                    print({service_line!r})
            """), seen

    def service_binary(self, address="127.0.0.1:4321", version="v1.0.0"):
        """A stand-in sysml-grpc that reports an address and exits at end of file on stdin."""
        stopped = os.path.join(self.tmp.name, "service-stopped")
        return self.executable("sysml-grpc", f"""\
            import sys
            if sys.argv[1:] == ["-version"]:
                print("sysml-grpc version {version}"); sys.exit(0)
            print({address!r}, flush=True)
            sys.stdin.read()
            open({stopped!r}, "w").write(" ".join(sys.argv[1:]))
            """), stopped

    def test_sample_without_a_service_prints_its_identity_alone(self):
        dotnet, seen = self.sample_host()
        self.assertIsNone(gates.sample(IDENTITY, dotnet, environ={"OPENSYSML_SERVICE": "elsewhere:1"}))
        self.assertFalse(os.path.exists(seen))

    def test_sample_without_a_binary_fails_when_a_service_is_required(self):
        dotnet, _ = self.sample_host()
        with self.assertRaisesRegex(gates.GateError, "OPENSYSML_REQUIRE_SERVICE is set"):
            gates.sample(IDENTITY, dotnet, environ={"OPENSYSML_REQUIRE_SERVICE": "1"})

    def test_sample_rejects_another_identity(self):
        dotnet, _ = self.sample_host(identity="OpenMBEE.OpenSysML 0.0.0 (.NETCoreApp,Version=v8.0)")
        with self.assertRaisesRegex(gates.GateError, "with no service named"):
            gates.sample(IDENTITY, dotnet, environ={})

    def test_sample_connects_to_the_service_it_starts_and_stops_it(self):
        dotnet, seen = self.sample_host()
        binary, stopped = self.service_binary()
        self.assertEqual(gates.sample(IDENTITY, dotnet, binary, environ={}), "sysml-grpc v1.0.0, 3 capabilities")
        with open(seen, encoding="utf-8") as f:
            self.assertEqual(f.read(), "127.0.0.1:4321")
        with open(stopped, encoding="utf-8") as f:
            self.assertEqual(f.read(), "-port 0 -health-port 0 -report-address -exit-with-parent")

    def test_sample_takes_the_binary_from_the_environment(self):
        dotnet, _ = self.sample_host()
        binary, _ = self.service_binary()
        self.assertIsNotNone(gates.sample(IDENTITY, dotnet, environ={"OPENSYSML_GRPC_BINARY": binary}))

    def test_sample_rejects_a_service_line_of_another_version(self):
        dotnet, _ = self.sample_host(service_line="sysml-grpc v2.0.0, 3 capabilities")
        binary, stopped = self.service_binary()
        with self.assertRaisesRegex(gates.GateError, "connected to sysml-grpc v1.0.0"):
            gates.sample(IDENTITY, dotnet, binary, environ={})
        self.assertTrue(os.path.exists(stopped))

    def test_sample_rejects_no_capabilities_or_no_service_line(self):
        for line in ("sysml-grpc v1.0.0, 0 capabilities", ""):
            dotnet, _ = self.sample_host(service_line=line)
            binary, _ = self.service_binary()
            with self.assertRaisesRegex(gates.GateError, "the sample printed"):
                gates.sample(IDENTITY, dotnet, binary, environ={})

    def test_sample_rejects_a_service_that_reports_no_address(self):
        dotnet, _ = self.sample_host()
        binary, stopped = self.service_binary(address="")
        with self.assertRaisesRegex(gates.GateError, "reported no address"):
            gates.sample(IDENTITY, dotnet, binary, environ={})
        self.assertTrue(os.path.exists(stopped))

    def test_sample_rejects_a_binary_that_is_not_a_file(self):
        dotnet, _ = self.sample_host()
        with self.assertRaisesRegex(gates.GateError, "is not a file"):
            gates.sample(IDENTITY, dotnet, os.path.join(self.tmp.name, "absent"), environ={})

    def test_main_reports_a_failed_gate(self):
        os.makedirs(os.path.join(self.tmp.name, "run"))
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            self.assertEqual(gates.main(["coverage", os.path.join(self.tmp.name, "run")]), 1)
        self.assertIn("holds no Cobertura report", stderr.getvalue())


if __name__ == "__main__":
    unittest.main()
