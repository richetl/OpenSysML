#!/usr/bin/env python3
"""The .NET client's environment and coverage gates, which MSBuild cannot express.

    python3 client/dotnet/scripts/dotnet-gates.py preflight [--dotnet DOTNET]
    python3 client/dotnet/scripts/dotnet-gates.py coverage RESULTS_DIR [--generated PATH ...]

`preflight` fails unless `dotnet`, invoked from the repository root, is exactly the
SDK root global.json pins and a .NET 10 runtime is installed beside it: global.json's
roll-forward would otherwise let a different SDK build, and the tests must execute
on net10, not merely compile for it.

`coverage` fails unless RESULTS_DIR holds at least one coverlet Cobertura report,
each measuring some handwritten library code under client/dotnet/src and none of
the generated files named by --generated, which the run must exclude by path.
There is no percentage threshold.
"""

import argparse
import glob
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

REPO_ROOT = os.path.dirname(
    os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
)
GLOBAL_JSON = os.path.join(REPO_ROOT, "global.json")
RUNTIME_MAJOR = 10
HANDWRITTEN_ROOT = "client/dotnet/src/"


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


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    pre = commands.add_parser("preflight", help="check the SDK and runtime the repository root selects")
    pre.add_argument("--dotnet", default="dotnet", help="the dotnet host (default: dotnet on PATH)")
    cov = commands.add_parser("coverage", help="check a coverage run's Cobertura reports")
    cov.add_argument("results_dir")
    cov.add_argument("--generated", nargs="*", default=[], help="generated file path suffixes the run excludes")
    args = parser.parse_args(argv)

    try:
        if args.command == "preflight":
            sdk, runtimes = preflight(args.dotnet)
            print(f"✓ .NET SDK {sdk}, runtime {', '.join(runtimes)}")
        else:
            for report, covered, valid in coverage(args.results_dir, args.generated):
                print(f"✓ {report}: {covered} of {valid} lines covered")
    except GateError as e:
        print(f"error: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
