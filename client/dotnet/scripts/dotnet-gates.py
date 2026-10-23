#!/usr/bin/env python3
"""The .NET client's environment and coverage gates, which MSBuild cannot express.

    python3 client/dotnet/scripts/dotnet-gates.py preflight [--dotnet DOTNET]
    python3 client/dotnet/scripts/dotnet-gates.py coverage RESULTS_DIR [--generated PATH ...]
    python3 client/dotnet/scripts/dotnet-gates.py sample --expect LINE [--dotnet DOTNET] [--binary PATH]
    python3 client/dotnet/scripts/dotnet-gates.py conformance-check --report R --log L --junit J \
        --reference-report RR --reference-log RL --expected-skips E

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

`conformance-check` holds a run of the .NET conformance runner to the reference runner's run of
the same suite against the same service. Both reports must have the reference report's members in
its order and totals that add up, and the .NET JUnit report the same totals, one suite per
configuration and protocol and one case per scenario with its matching outcome. Then, scenario by
scenario, both runs must agree on the outcome, the status, the capabilities missing and whether the without-capability
expectation applied, and their -v logs on the normalized response. Timing and what answered are
not compared. A .NET skip the expected skips list is excluded, as reviewed; nothing else is. The
service's capabilities are compared as a set, since the client holds them as one.
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


TOTALS = ("total", "passed", "failed", "skipped", "errored", "without_capability")
OUTCOME_TOTALS = {"pass": "passed", "fail": "failed", "skip": "skipped", "error": "errored"}
# Each object of a report, its members in the reference runner's order; True marks a member it may omit.
REPORT_MEMBERS = [("service", False), ("mcp_server", True)] + [(t, False) for t in TOTALS] + [("configurations", False)]
CONFIGURATION_MEMBERS = (
    [("name", False), ("unavailable_capabilities", True)] + [(t, False) for t in TOTALS] + [("protocols", False)]
)
PROTOCOL_MEMBERS = (
    [("protocol", False), ("service", False), ("capabilities", False)] + [(t, False) for t in TOTALS] + [("results", False)]
)
RESULT_MEMBERS = [
    ("id", False),
    ("outcome", False),
    ("rpc", False),
    ("reason", True),
    ("failures", True),
    ("status", False),
    ("without_capability", True),
    ("missing_capabilities", True),
    ("duration_ms", False),
]
LOG_MARK = re.compile(r"^(PASS|FAIL|SKIP|ERR) +(\S+) +(\S+)$")
LOG_SUMMARY = re.compile(r"^\[(\S+)\] \d+ scenarios: ")
LOG_INDENT = " " * 7


def _object(value, where, members):
    """Check an object has only the members named, in their order, and every one not optional."""
    if not isinstance(value, dict):
        raise GateError(f"{where} is not an object")
    names = [name for name, _ in members]
    unknown = [key for key in value if key not in names]
    if unknown:
        raise GateError(f"{where} has unknown member {unknown[0]!r}")
    missing = [name for name, optional in members if not optional and name not in value]
    if missing:
        raise GateError(f"{where} has no {missing[0]!r}")
    order = [name for name in names if name in value]
    if list(value) != order:
        raise GateError(f"{where} orders its members {list(value)}, want {order}")
    return value


def _totals(value, where, counted):
    for name in TOTALS:
        if value[name] != counted[name]:
            raise GateError(f"{where}: {name} is {value[name]}, but its parts add up to {counted[name]}")


def check_conformance_report(path):
    """Check a conformance report has the reference runner's shape and that its totals add up.

    Args:
        path (str): The report

    Returns:
        dict: The report

    Raises:
        GateError: If it is not JSON, has another shape, or a total disagrees with its parts
    """
    try:
        with open(path, encoding="utf-8") as f:
            report = json.load(f)
    except (OSError, ValueError) as e:
        raise GateError(f"{path} is not a readable JSON report: {e}") from e
    _object(report, path, REPORT_MEMBERS)
    report_counted = dict.fromkeys(TOTALS, 0)
    for c, configuration in enumerate(report["configurations"]):
        where = f"{path}: configuration {c}"
        _object(configuration, where, CONFIGURATION_MEMBERS)
        configuration_counted = dict.fromkeys(TOTALS, 0)
        for p, protocol in enumerate(configuration["protocols"]):
            at = f"{where} protocol {p}"
            _object(protocol, at, PROTOCOL_MEMBERS)
            counted = dict.fromkeys(TOTALS, 0)
            for r, result in enumerate(protocol["results"]):
                _object(result, f"{at} result {r}", RESULT_MEMBERS)
                if result["outcome"] not in OUTCOME_TOTALS:
                    raise GateError(f"{at} result {r} ends {result['outcome']!r}")
                counted["total"] += 1
                counted[OUTCOME_TOTALS[result["outcome"]]] += 1
                counted["without_capability"] += 1 if result.get("without_capability") else 0
            _totals(protocol, at, counted)
            for name in TOTALS:
                configuration_counted[name] += counted[name]
        _totals(configuration, where, configuration_counted)
        for name in TOTALS:
            report_counted[name] += configuration_counted[name]
    _totals(report, path, report_counted)
    return report


def check_conformance_junit(path, report):
    """Check a JUnit report states a JSON report's results: its totals, a suite per configuration
    and protocol, and a case per scenario, in order, with its matching outcome.

    Raises:
        GateError: If it is not JUnit XML or disagrees with the report
    """
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as e:
        raise GateError(f"{path} is not readable XML: {e}") from e
    if root.tag != "testsuites":
        raise GateError(f"{path} is not a JUnit report")

    def counts(element, totals, where):
        for attribute, total in (("tests", "total"), ("failures", "failed"), ("errors", "errored"), ("skipped", "skipped")):
            if element.get(attribute) != str(totals[total]):
                raise GateError(f"{where}: {attribute}={element.get(attribute)!r}, but the report counts {totals[total]}")

    counts(root, report, path)
    protocols = [(c, p) for c in report["configurations"] for p in c["protocols"]]
    suites = root.findall("testsuite")
    if len(suites) != len(protocols):
        raise GateError(f"{path} has {len(suites)} suites, but the report runs {len(protocols)} protocols")
    for suite, (configuration, protocol) in zip(suites, protocols):
        name = f"{configuration['name']}/{protocol['protocol']}"
        if suite.get("name") != name:
            raise GateError(f"{path}: suite {suite.get('name')!r}, want {name!r}")
        counts(suite, protocol, f"{path} suite {name}")
        cases = suite.findall("testcase")
        ids = [result["id"] for result in protocol["results"]]
        if [case.get("name") for case in cases] != ids:
            raise GateError(f"{path} suite {name}: its cases are not the report's scenarios, in order")
        dispositions = {"pass": [], "fail": ["failure"], "skip": ["skipped"], "error": ["error"]}
        for case, result in zip(cases, protocol["results"]):
            actual = [child.tag for child in case if child.tag in ("failure", "skipped", "error")]
            want = dispositions[result["outcome"]]
            if actual != want:
                raise GateError(f"{path} suite {name} case {result['id']}: outcome elements {actual}, want {want}")


def read_conformance_log(path):
    """Read a runner's -v output: per configuration and protocol, each scenario's mark, status and
    normalized response, the response None where none was printed.

    Returns:
        dict: (configuration, protocol) -> {id: (mark, status, response)}

    Raises:
        GateError: If a response is not JSON or a scenario line comes before any configuration
    """
    try:
        with open(path, encoding="utf-8") as f:
            lines = f.read().split("\n")
    except OSError as e:
        raise GateError(f"{path} cannot be read: {e}") from e
    runs = {}
    configuration = None
    current = {}
    response = None
    collecting = None
    for number, line in enumerate(lines, 1):
        if collecting is not None:
            collecting.append(line)
            if line == LOG_INDENT + "}":
                try:
                    response = json.loads("\n".join(entry[len(LOG_INDENT):] for entry in collecting))
                except ValueError as e:
                    raise GateError(f"{path}:{number}: a response that is not JSON: {e}") from e
                collecting = None
            continue
        if line.startswith("Configuration "):
            configuration = line[len("Configuration "):]
            current = {}
        elif line == LOG_INDENT + "{":
            collecting = [line]
        elif (mark := LOG_MARK.match(line)) is not None:
            if configuration is None:
                raise GateError(f"{path}:{number}: a scenario before any configuration")
            current[mark.group(2)] = (mark.group(1), mark.group(3), response)
            response = None
        elif (summary := LOG_SUMMARY.match(line)) is not None:
            runs[(configuration, summary.group(1))] = current
            current = {}
    return runs


def _sorted_capabilities(rpc, response):
    if rpc == "GetServerInfo" and isinstance(response, dict) and isinstance(response.get("capabilities"), list):
        response = dict(response, capabilities=sorted(response["capabilities"]))
    return response


def _difference(got, want, path=""):
    """The first path at which two normalized responses differ, or None."""
    if isinstance(got, dict) and isinstance(want, dict):
        for key in sorted(set(got) | set(want)):
            if key not in got or key not in want:
                return f"{path}{key}: only the {'.NET' if key in got else 'reference'} run has it"
            found = _difference(got[key], want[key], f"{path}{key}.")
            if found:
                return found
        return None
    if isinstance(got, list) and isinstance(want, list):
        if len(got) != len(want):
            return f"{path.rstrip('.')}: {len(got)} entries, the reference {len(want)}"
        for i, (g, w) in enumerate(zip(got, want)):
            found = _difference(g, w, f"{path}{i}.")
            if found:
                return found
        return None
    if got != want or type(got) is not type(want):
        return f"{path.rstrip('.')}: {got!r}, the reference {want!r}"
    return None


def conformance_check(report, log, junit, reference_report, reference_log, expected_skips):
    """Compare a .NET conformance run with the reference runner's, scenario by scenario.

    Args:
        report (str): The .NET runner's -report
        log (str): Its -v output
        junit (str): Its -junit
        reference_report (str): The reference runner's -report
        reference_log (str): Its -v output
        expected_skips (str): The .NET client's expected-skips.json

    Returns:
        tuple[int, int]: Results that agree, and reviewed skips excluded

    Raises:
        GateError: Naming every disagreement, if there is any
    """
    ours = check_conformance_report(report)
    check_conformance_junit(junit, ours)
    theirs = check_conformance_report(reference_report)
    our_log = read_conformance_log(log)
    their_log = read_conformance_log(reference_log)
    try:
        with open(expected_skips, encoding="utf-8") as f:
            skips = json.load(f)
    except (OSError, ValueError) as e:
        raise GateError(f"{expected_skips} is not a readable list: {e}") from e
    uncovered = set(skips.get("uncovered_rpcs", []))
    unexpressible = skips.get("unexpressible", {})

    problems = []
    agreed = excluded = 0
    our_configurations = [(c["name"], c.get("unavailable_capabilities", [])) for c in ours["configurations"]]
    their_configurations = [(c["name"], c.get("unavailable_capabilities", [])) for c in theirs["configurations"]]
    if our_configurations != their_configurations:
        raise GateError(f"the runs test configurations {our_configurations} and {their_configurations}")
    for our_configuration, their_configuration in zip(ours["configurations"], theirs["configurations"]):
        name = our_configuration["name"]
        our_protocols = [p["protocol"] for p in our_configuration["protocols"]]
        their_protocols = [p["protocol"] for p in their_configuration["protocols"]]
        if our_protocols != their_protocols:
            problems.append(f"{name}: the runs test protocols {our_protocols} and {their_protocols}")
            continue
        for our_protocol, their_protocol in zip(our_configuration["protocols"], their_configuration["protocols"]):
            where = f"{name} {our_protocol['protocol']}"
            if set(our_protocol["capabilities"]) != set(their_protocol["capabilities"]):
                problems.append(f"{where}: the services advertised different capabilities")
            our_results = {r["id"]: r for r in our_protocol["results"]}
            their_results = {r["id"]: r for r in their_protocol["results"]}
            if list(our_results) != list(their_results):
                problems.append(f"{where}: the runs judged different scenarios")
                continue
            our_responses = our_log.get((name, our_protocol["protocol"]))
            their_responses = their_log.get((name, their_protocol["protocol"]))
            if our_responses is None or their_responses is None:
                problems.append(f"{where}: a log has no run of it")
                continue
            for scenario, our in our_results.items():
                their = their_results[scenario]
                if our["outcome"] == "skip" and (
                    our["rpc"] in uncovered or unexpressible.get(scenario) == our.get("reason")
                ):
                    excluded += 1
                    continue
                if scenario not in our_responses or scenario not in their_responses:
                    problems.append(f"{where}: {scenario}: a log has no line for it")
                    continue
                differences = [
                    f"{field} {our.get(field, default)!r}, the reference {their.get(field, default)!r}"
                    for field, default in (
                        ("outcome", None),
                        ("status", None),
                        ("without_capability", False),
                        ("missing_capabilities", []),
                    )
                    if our.get(field, default) != their.get(field, default)
                ]
                difference = _difference(
                    _sorted_capabilities(our["rpc"], our_responses[scenario][2]),
                    _sorted_capabilities(their["rpc"], their_responses[scenario][2]),
                )
                if difference:
                    differences.append(f"response {difference}")
                if differences:
                    problems.append(f"{where}: {scenario}: " + "; ".join(differences))
                else:
                    agreed += 1
    if problems:
        raise GateError("the .NET run disagrees with the reference runner:\n  " + "\n  ".join(problems))
    return agreed, excluded


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
    chk = commands.add_parser("conformance-check", help="compare a .NET conformance run with the reference runner's")
    chk.add_argument("--report", required=True, help="the .NET runner's -report")
    chk.add_argument("--log", required=True, help="the .NET runner's -v output")
    chk.add_argument("--junit", required=True, help="the .NET runner's -junit")
    chk.add_argument("--reference-report", required=True, help="the reference runner's -report")
    chk.add_argument("--reference-log", required=True, help="the reference runner's -v output")
    chk.add_argument("--expected-skips", required=True, help="the .NET client's expected-skips.json")
    args = parser.parse_args(argv)

    try:
        if args.command == "preflight":
            sdk, runtimes = preflight(args.dotnet)
            print(f"OK .NET SDK {sdk}, runtime {', '.join(runtimes)}")
        elif args.command == "sample":
            served = sample(args.expect, args.dotnet, args.binary)
            print(f"OK {args.expect}")
            print(f"OK {served}" if served else f"- no sysml-grpc configured (${BINARY_VARIABLE}); skipped the service run")
        elif args.command == "conformance-check":
            agreed, excluded = conformance_check(
                args.report, args.log, args.junit, args.reference_report, args.reference_log, args.expected_skips
            )
            print(f"OK {agreed} results agree with the reference runner; {excluded} reviewed skips excluded")
        else:
            for report, covered, valid in coverage(args.results_dir, args.generated):
                print(f"OK {report}: {covered} of {valid} lines covered")
    except GateError as e:
        print(f"error: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
