#!/usr/bin/env python3
"""The .NET client's environment and coverage gates, which MSBuild cannot express.

    python3 client/dotnet/scripts/dotnet-gates.py preflight [--dotnet DOTNET]
    python3 client/dotnet/scripts/dotnet-gates.py coverage RESULTS_DIR [--generated PATH ...]
    python3 client/dotnet/scripts/dotnet-gates.py sample --expect LINE [--dotnet DOTNET] [--binary PATH]

`preflight` fails unless `dotnet`, invoked from the repository root, is exactly the
SDK root global.json pins and a .NET 10 runtime is installed beside it: global.json's
roll-forward would otherwise let a different SDK build, and the tests must execute
on net10, not merely compile for it.

`coverage` fails unless RESULTS_DIR holds at least one coverlet Cobertura report,
each measuring some handwritten library code under client/dotnet/src and none of
the generated files named by --generated, which the run must exclude by path.
There is no percentage threshold.

`sample` runs the built Vehicle sample three times: with neither a service nor a binary named,
when it must print exactly the --expect line; with $OPENSYSML_SERVICE naming a sysml-grpc the
gate starts on a port the kernel chooses; and with $OPENSYSML_GRPC_BINARY alone naming the
binary, which the sample starts as its private service. Each of the last two must also print
the version that binary reports and its capability count, then the vehicle it parsed, looked up
and evaluated. The binary is --binary or $OPENSYSML_GRPC_BINARY; without one the last two runs
are skipped, unless $OPENSYSML_REQUIRE_SERVICE is set, which makes it a failure.
"""

import argparse
import glob
import json
import os
import re
import subprocess
import sys
import threading
import xml.etree.ElementTree as ET

REPO_ROOT = os.path.dirname(
    os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
)
GLOBAL_JSON = os.path.join(REPO_ROOT, "global.json")
RUNTIME_MAJOR = 10
HANDWRITTEN_ROOT = "client/dotnet/src/"
SAMPLE_PROJECT = "client/dotnet/samples/Vehicle"
SERVICE_VARIABLE = "OPENSYSML_SERVICE"
SAMPLE_MODEL_LINE = "Demo::Vehicle (partDef): mass = 1500.0"
BINARY_VARIABLE = "OPENSYSML_GRPC_BINARY"
REQUIRE_VARIABLE = "OPENSYSML_REQUIRE_SERVICE"
STARTUP_TIMEOUT = 30
RUN_TIMEOUT = 120
SHUTDOWN_GRACE = 10


class GateError(Exception):
    """A gate's condition does not hold."""


def pinned_sdk(global_json=GLOBAL_JSON):
    """The SDK version root global.json pins.

    Args:
        global_json (str): Path to global.json

    Returns:
        str: The pinned version, e.g. '10.0.402'

    Raises:
        GateError: If the file pins no SDK version
    """
    with open(global_json, encoding="utf-8") as f:
        version = json.load(f).get("sdk", {}).get("version")
    if not isinstance(version, str):
        raise GateError(f"{global_json} pins no sdk.version")
    return version


def _dotnet(dotnet, *args):
    try:
        result = subprocess.run(
            [dotnet, *args], cwd=REPO_ROOT, capture_output=True, text=True, check=False
        )
    except OSError as e:
        raise GateError(f"cannot run {dotnet}: {e}") from e
    if result.returncode != 0:
        raise GateError(
            f"`{dotnet} {' '.join(args)}` exited {result.returncode} from the repository root; "
            f"is the SDK root global.json pins installed?\n{(result.stdout + result.stderr).strip()}"
        )
    return result.stdout


def preflight(dotnet="dotnet", global_json=GLOBAL_JSON):
    """Check the SDK and runtime the repository root selects.

    Args:
        dotnet (str): The dotnet host to run
        global_json (str): Path to the root global.json

    Returns:
        tuple[str, list[str]]: The SDK version and the .NET 10 runtime versions

    Raises:
        GateError: If the SDK is not the pinned one or no .NET 10 runtime is installed
    """
    pinned = pinned_sdk(global_json)
    sdk = _dotnet(dotnet, "--version").strip()
    if sdk != pinned:
        raise GateError(
            f"dotnet from the repository root is SDK {sdk!r}, but global.json pins {pinned!r}; "
            "install that SDK."
        )
    runtimes = re.findall(
        rf"^Microsoft\.NETCore\.App ({RUNTIME_MAJOR}\.\d+\.\d+)\s",
        _dotnet(dotnet, "--list-runtimes"),
        re.MULTILINE,
    )
    if not runtimes:
        raise GateError(
            f"no .NET {RUNTIME_MAJOR} runtime (Microsoft.NETCore.App {RUNTIME_MAJOR}.x) is installed "
            f"beside SDK {sdk}; the tests must execute on net{RUNTIME_MAJOR}.0."
        )
    return sdk, runtimes


def _normalized(path):
    return path.replace("\\", "/")


def check_report(path, generated=()):
    """Check one Cobertura report.

    Args:
        path (str): The report
        generated (iterable[str]): Path suffixes of generated files the run excludes

    Returns:
        tuple[int, int]: Lines covered and lines valid

    Raises:
        GateError: If the report measures nothing, measures a generated file, or
            measures a file outside the handwritten library sources
    """
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as e:
        raise GateError(f"{path} is not XML: {e}") from e
    if root.tag != "coverage":
        raise GateError(f"{path} is not a Cobertura report")
    classes = root.findall("./packages/package/classes/class")
    valid = int(root.get("lines-valid", "0"))
    if not classes or valid <= 0:
        raise GateError(f"{path} measures no code")
    sources = [_normalized(source.text or "") for source in root.findall("./sources/source")]
    for cls in classes:
        filename = _normalized(cls.get("filename", ""))
        candidates = _resolved(filename, sources)
        if any(c.endswith("/" + _normalized(g).lstrip("/")) for c in candidates for g in generated):
            raise GateError(f"{path} measures generated {filename}; exclude it by path")
        if not any(f"/{HANDWRITTEN_ROOT}" in f"/{c}" for c in candidates):
            raise GateError(f"{path} measures {filename}, outside {HANDWRITTEN_ROOT}")
    return int(root.get("lines-covered", "0")), valid


def _resolved(filename, sources):
    """The paths a class's filename may name: itself when absolute or when the report lists
    no sources, otherwise relative to each source, as Cobertura reads it."""
    if not sources or filename.startswith("/") or re.match(r"^[A-Za-z]:/", filename):
        return [filename]
    return [source.rstrip("/") + "/" + filename for source in sources if source]


def coverage(results_dir, generated=()):
    """Check every Cobertura report a coverage run wrote.

    Args:
        results_dir (str): The run's results directory
        generated (iterable[str]): Path suffixes of generated files the run excludes

    Returns:
        list[tuple[str, int, int]]: Each report with its lines covered and valid

    Raises:
        GateError: If there is no report or any report fails check_report
    """
    reports = sorted(glob.glob(os.path.join(results_dir, "**", "*.cobertura*.xml"), recursive=True))
    if not reports:
        raise GateError(f"{results_dir} holds no Cobertura report")
    return [(report, *check_report(report, generated)) for report in reports]


def _required(environ):
    value = (environ.get(REQUIRE_VARIABLE) or "").strip().lower()
    return value not in ("", "0", "false", "no")


def _run_sample(dotnet, project, environ):
    try:
        result = subprocess.run(
            [dotnet, "run", "--project", project, "-c", "Release", "--no-build"],
            cwd=REPO_ROOT, env=environ, capture_output=True, text=True, timeout=RUN_TIMEOUT, check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as e:
        raise GateError(f"cannot run the sample {project}: {e}") from e
    if result.returncode != 0:
        raise GateError(
            f"the sample {project} exited {result.returncode}:\n{(result.stdout + result.stderr).strip()}"
        )
    return result.stdout.splitlines()


def _binary_version(binary):
    try:
        result = subprocess.run([binary, "-version"], capture_output=True, text=True, timeout=RUN_TIMEOUT, check=False)
    except (OSError, subprocess.TimeoutExpired) as e:
        raise GateError(f"cannot run {binary} -version: {e}") from e
    first = result.stdout.splitlines()[0] if result.stdout else ""
    match = re.fullmatch(r"sysml-grpc version (\S+)", first.strip())
    if result.returncode != 0 or not match:
        raise GateError(f"{binary} -version exited {result.returncode} and printed {first!r}")
    return match.group(1)


def _read_line(stream, timeout):
    line = []
    reader = threading.Thread(target=lambda: line.append(stream.readline()), daemon=True)
    reader.start()
    reader.join(timeout)
    return line[0] if line else None


def _check_served(lines, expect, version, how):
    pattern = rf"sysml-grpc {re.escape(version)}, [1-9][0-9]* capabilities"
    if len(lines) != 3 or lines[0] != expect or not re.fullmatch(pattern, lines[1]) or lines[2] != SAMPLE_MODEL_LINE:
        raise GateError(
            f"{how}, the sample printed {lines!r}, not {expect!r}, "
            f"a line matching {pattern!r} and {SAMPLE_MODEL_LINE!r}"
        )


def sample(expect, dotnet="dotnet", binary=None, project=SAMPLE_PROJECT, environ=None):
    """Run the sample without a service, against one the gate starts, and against one it starts privately.

    Args:
        expect (str): The line the sample prints first, its client identity
        dotnet (str): The dotnet host to run
        binary (str): The sysml-grpc binary; $OPENSYSML_GRPC_BINARY when None
        project (str): The sample project, relative to the repository root
        environ (dict): The environment to run in; this process's when None

    Returns:
        str | None: The service line the sample printed, or None when no binary was configured

    Raises:
        GateError: If the sample's output is not what it must be, or a required service is absent
    """
    environ = dict(os.environ if environ is None else environ)
    environ.pop(SERVICE_VARIABLE, None)
    binary = binary or environ.get(BINARY_VARIABLE)
    environ.pop(BINARY_VARIABLE, None)
    lines = _run_sample(dotnet, project, environ)
    if lines != [expect]:
        raise GateError(f"with no service named, the sample printed {lines!r}, not {[expect]!r}")

    if not binary:
        if _required(environ):
            raise GateError(f"${REQUIRE_VARIABLE} is set, but ${BINARY_VARIABLE} names no sysml-grpc for the sample")
        return None
    if not os.path.isfile(binary):
        raise GateError(f"{binary}, the sysml-grpc the sample is to connect to, is not a file")
    version = _binary_version(binary)

    try:
        service = subprocess.Popen(
            [binary, "-port", "0", "-health-port", "0", "-report-address", "-exit-with-parent"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True,
        )
    except OSError as e:
        raise GateError(f"cannot start {binary}: {e}") from e
    try:
        address = _read_line(service.stdout, STARTUP_TIMEOUT)
        if not address or not address.strip():
            raise GateError(f"{binary} reported no address within {STARTUP_TIMEOUT}s")
        named = dict(environ)
        named[SERVICE_VARIABLE] = address.strip()
        lines = _run_sample(dotnet, project, named)
    finally:
        service.stdin.close()
        try:
            service.wait(SHUTDOWN_GRACE)
        except subprocess.TimeoutExpired:
            service.kill()
            service.wait()
        service.stdout.close()

    _check_served(lines, expect, version, f"connected to sysml-grpc {version}")

    private = dict(environ)
    private[BINARY_VARIABLE] = binary
    lines = _run_sample(dotnet, project, private)
    _check_served(lines, expect, version, f"starting sysml-grpc {version} privately")
    return lines[1]


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    pre = commands.add_parser("preflight", help="check the SDK and runtime the repository root selects")
    pre.add_argument("--dotnet", default="dotnet", help="the dotnet host (default: dotnet on PATH)")
    cov = commands.add_parser("coverage", help="check a coverage run's Cobertura reports")
    cov.add_argument("results_dir")
    cov.add_argument("--generated", nargs="*", default=[], help="generated file path suffixes the run excludes")
    smp = commands.add_parser("sample", help="run the Vehicle sample without a service and against one")
    smp.add_argument("--expect", required=True, help="the identity line the sample prints first")
    smp.add_argument("--dotnet", default="dotnet", help="the dotnet host (default: dotnet on PATH)")
    smp.add_argument("--binary", help=f"the sysml-grpc binary (default: ${BINARY_VARIABLE})")
    args = parser.parse_args(argv)

    try:
        if args.command == "preflight":
            sdk, runtimes = preflight(args.dotnet)
            print(f"✓ .NET SDK {sdk}, runtime {', '.join(runtimes)}")
        elif args.command == "sample":
            served = sample(args.expect, args.dotnet, args.binary)
            print(f"✓ {args.expect}")
            print(f"✓ {served}" if served else f"- no sysml-grpc configured (${BINARY_VARIABLE}); skipped the service run")
        else:
            for report, covered, valid in coverage(args.results_dir, args.generated):
                print(f"✓ {report}: {covered} of {valid} lines covered")
    except GateError as e:
        print(f"error: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
