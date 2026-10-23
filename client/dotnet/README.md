# OpenSysML .NET client

`OpenMBEE.OpenSysML` is the C# client for the `sysml-grpc` service, built from a checkout
of this repository and consumed by `ProjectReference`. It is not published to NuGet.

> **Status:** the client connects to a `sysml-grpc` that is already running, or starts a
> private one from a binary installed locally, over gRPC or the Connect protocol with protobuf
> or JSON bodies; reports what that service is and can do; and calls any of its methods
> through a raw, schema-checked escape hatch. It loads models from a file, from inline
> notation or from several documents, and reads them: symbols and name lookup, diagnostics,
> expression evaluation and instantiation, with the immutable values those operations
> exchange. It [runs and checks behavior](#running-and-checking-behavior): actions and state
> machines once or over every order of their choice points, and a search of every schedule
> for a violated property. It [verifies and analyzes](#verifying-calculating-and-analyzing)
> models: constraints, requirements, satisfactions and whole objects, calculations, analysis
> cases, parameter sweeps and the engines that answer them. Its
> [conformance runner](#conformance) runs the shared suite through that API. It does not
> download a release of the service, and offers none of the other typed operations
> (sampled runs, conversion, queries, documents, editing). The other clients
> ([Python](../python/), [Java](../java/), [Rust](../rust/), [Node](../node/)) are the ones
> to use for those today.

## Requirements

- The .NET SDK that the repository-root [`global.json`](../../global.json) pins (10.0.402),
  with its .NET 10 runtime. Run every `dotnet` command from the repository root so that
  file selects the SDK; `make dotnet-preflight` checks both.
- Python 3, for the preflight and coverage checks the Make targets run.
- For the integration tests, a `sysml-grpc` binary: `make build-grpc` writes `bin/sysml-grpc`.

The library, tests and sample target `net10.0` only. Native AOT and trimming are not
supported: the assembly makes no `IsAotCompatible` or `IsTrimmable` claim.

## Using it from a checkout

Reference the project from your own:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/OpenSysML/client/dotnet/src/OpenMBEE.OpenSysML/OpenMBEE.OpenSysML.csproj" />
</ItemGroup>
```

[`samples/Vehicle`](samples/Vehicle/) is such a consumer. It prints the referenced
assembly's version and target framework and, when `OPENSYSML_SERVICE` names a running
service or `OPENSYSML_GRPC_BINARY` names a binary to start privately, what that service
reports, then parses a vehicle model, looks the vehicle up and evaluates its mass:

```bash
make build-grpc
OPENSYSML_GRPC_BINARY="$PWD/bin/sysml-grpc" dotnet run --project client/dotnet/samples/Vehicle -c Release
# OpenMBEE.OpenSysML 0.10.1 (.NETCoreApp,Version=v10.0)
# sysml-grpc v0.10.1, 72 capabilities
# Demo::Vehicle (partDef): mass = 1500.0
```

## Connecting to a service

```csharp
using OpenMBEE.OpenSysML;

await using var connection = await Connection.OpenAsync(new ConnectionOptions
{
    Address = "localhost:50051",
    RequireCapabilities = [Capabilities.Query],
});
Console.WriteLine(connection.ServerInfo);              // sysml-grpc v0.10.1, 72 capabilities
bool converts = connection.Capabilities.Has(Capabilities.Convert);
```

`Connection.OpenAsync` reaches the service `ConnectionOptions.Address` names, or else the one
the `OPENSYSML_SERVICE` environment variable names, or else starts a private one (see
[Private services](#private-services)). An address is `host:port`, which means
`http://host:port`, a bracketed IPv6 `[::1]:port`, or an `http` or `https` URL with nothing
after its port; user information, a path, a query or a fragment is refused. A service named
this way is never started, stopped or downloaded by the client.

Opening asks the service `GetServerInfo` once. `ServerInfo` keeps the answer: the service's
version, informational only, and its `Capabilities`, which `Capabilities` has a constant for
each of. Decide what to do on capability names, never on the version. A service too old to
have `GetServerInfo` answers it `Unimplemented`; `ServerInfo.Answered` is then false and no
capability is claimed. `ConnectionOptions.Version` requires an exact version, failing with
`StaleServiceException`, and `RequireCapabilities` requires capabilities, failing with
`CapabilityException`; both are checked after that one call, and a failed open closes what
it opened.

`ConnectionOptions` also holds the request timeout (60 seconds by default), headers sent with
every call, the largest request and response messages (512 MiB each by default), a logger
(`NullLogger` by default) and an HTTP handler to borrow. Every option is checked when the
connection opens, which refuses an invalid one with `InvalidRequestException` before it sends
anything. Headers carry authorization and custom metadata only: a header the protocol
controls (content type and encoding, timeouts, host, any `grpc-` or `connect-` header) is
refused, and header values are never logged. A handler given as `HttpMessageHandler` is
borrowed, never disposed, so it can serve several connections; otherwise the connection
makes its own and disposes it.

The client never retries a call, hedges it or falls back to another protocol: a call that
failed in transport may still have run.

### Private services

```csharp
await using var connection = await Connection.OpenAsync();   // no service named: start one
```

With no service named and `ConnectionOptions.AutoStart` true, the default, `OpenAsync` starts
`sysml-grpc` as a child of this process: `sysml-grpc -port 0 -health-port 0 -report-address
-exit-with-parent`. The kernel chooses its port, which the child reports as the first line of
its output; the connection dials that loopback address, which no other process is told of. The
child exits at end of file on its input, and only this process holds the other end of that
pipe, so it exits with this process however this process ends, a kill included; no exit
handler is involved. Its output is read for as long as it runs, so a chatty child never
blocks, and its last 20 lines, each cut at 1,024 characters, are kept for error messages. With
`AutoStart` false, a connection that names no service fails with `ServiceStartException`.

The binary is, in order, the one `ConnectionOptions.BinaryPath` names, the one the
`OPENSYSML_GRPC_BINARY` environment variable names, `~/.opensysml/bin/sysml-grpc`
(`sysml-grpc.exe` on Windows, under `%USERPROFILE%`), where every client installs its
download, and the first `sysml-grpc` on `PATH`. A binary named by the option or the variable
that is not an executable file fails the open with `BinaryNotFoundException` rather than
another being used, and finding none lists every place looked at in `SearchedLocations`. This
release does not download a release of the service. The binary is hashed before it is
started, with symbolic links followed before any subsequent `..` component, including in
link targets; a missing directory or regular file cannot be traversed by `..`.
`ConnectionOptions.ExpectedBinarySha256` requires a digest, and a binary without it
fails with `ChecksumMismatchException` and is never executed. A platform that cannot start a
process, such as a browser, fails with `UnsupportedPlatformException`.

The connections of one copy of the client share a private service while they agree on what
it is: the same binary, by its path with every link followed and by the digest of its
contents, and the same `StartupTimeout`, `ShutdownTimeout` and `Version`. Protocol, encoding,
headers, logger, request timeout and HTTP handler belong to each connection and never split
a service. Concurrent opens of one service wait for one start; one that is cancelled gives up
only its own wait, and the start is cancelled when none waits for it any more. A start that
fails stops its child and gives every waiting open the same failure. A connection holds the
service from the moment it starts opening; an open that fails, its handshake included, gives
its hold up, and the last connection to close stops the service: its input is closed, it has
`ShutdownTimeout` (5 seconds by default) to exit, and is then killed with every process it
started. `IsolatedService` gives a connection a service of its own, stopped when it closes.
A copy of the client is its assembly load context: plugins sharing one loaded copy share its
services, and a copy loaded into another context starts its own. Unloading a collectible
context closes the input of every service its copy started, a start still in progress
included, and each exits; the copy registers no process-exit handler, starts no foreground
thread and leaves nothing in the default context referring to it, so once its services are
gone it can be collected. That cleanup is best effort; that a child ends when its input does
is the guarantee.

A private service that exits on its own is not restarted: the connections to it fail from
then on, calls in flight included, with `TransportException` and `Unavailable`, saying how it
ended, and a connection opened afterwards starts another. `Connection.StopSharedServicesAsync`
stops every shared private service of this copy of the client, as a host unloading it does;
their connections fail the same way and are never moved to another service. It tries every
service, including stops already underway, before reporting the ones it could not stop in a
`ServiceStopException` with a read-only `Failures` snapshot, and leaves isolated services to
their connections. Unloading also stops a child that finishes starting after its startup was
cancelled. A shared service logs its life (start, unexpected
exit, a kill after the grace period, a failure to stop) to the logger of the connection that
started it; each line of its output goes to that logger at `Trace`.

`StartupTimeout` (30 seconds by default) bounds the wait for the address. A child that
reports something other than a loopback `host:port`, exits or ends its output first, or
reports nothing in time is stopped, and the open fails with `ServiceStartException` quoting
how it ended and what it last wrote. `BinaryPath`, `ExpectedBinarySha256` and
`IsolatedService` only describe a private service, so they are refused alongside `Address` or
with `AutoStart` false; when `OPENSYSML_SERVICE` names a service instead, they are unused and a
warning says so.

These guarantees hold where the tests that prove them run: Linux, macOS and Windows, the
platforms CI runs the whole suite on (see [Building and testing](#building-and-testing)). On
any other platform the client starts a private service the same way, but nothing proves that
it ends with the process or that an unloaded copy is collected.

### Protocols

`ConnectionOptions.Protocol` and `ConnectionOptions.Encoding` choose how a connection speaks to
the service, and `Connection.Protocol` and `Connection.Encoding` report it:

| `Protocol` | `Encoding` | On the wire |
| --- | --- | --- |
| `Grpc` (default) | `Protobuf` (default) | gRPC over HTTP/2 (cleartext h2c for `http`) |
| `Connect` | `Protobuf` | One HTTP/1.1 `POST /sysml.SysMLService/<Method>` per call, `application/proto` |
| `Connect` | `Json` | The same, `application/json`, in the protobuf JSON mapping |

`Grpc` with `Json` is refused when the connection opens, before anything is sent: gRPC carries
protobuf alone. A connection speaks the one protocol it was given; if the service does not
answer it, the call fails rather than trying another. The service's default `connect`
transport answers all three; its `grpc` transport speaks gRPC alone and resets a Connect
request's connection, which the client reports as a `TransportException` with `Unavailable`.
Connect suits a proxy that does not carry HTTP/2, and JSON one that must read the traffic;
protobuf is smaller than JSON and faster to decode.

The gRPC channel advertises only the identity encoding. A message size limit in the
underlying library bounds a compressed frame, not what it inflates to, so accepting
compressed responses would let one exceed `MaxResponseSize`.

Over Connect, the client asks for gzip and reads it itself, so that `MaxResponseSize` bounds
the body after inflation, and the compressed body by the most a deflate encoder can make of
that many bytes; no buffer of the limit's size is made up front. A gzip body must be one gzip
member, as HTTP servers write one, and its CRC-32 and length are checked: the .NET inflater
alone returns a body cut short as if it were whole. A Connect call sends its deadline as
`Connect-Timeout-Ms` and also enforces it itself, because the service does not cut a call
that is already running short. The connection's own HTTP handler follows no redirect and keeps
no cookies; a borrowed `HttpMessageHandler` is used as it is configured.

With `Json`, the size limits measure the JSON text. A response field this client does not
know is ignored, so that a newer service can still be read, and so is an enum value given by
a name the client has no number for: it reads as the enum's zero value. Protobuf keeps such a
value's number, and JSON keeps one the service sends as a number; the service sends names.

### Raw calls

`Connection.CallAsync<TRequest, TResponse>(method, request)` calls any method of the service
with the generated `OpenMBEE.OpenSysML.Wire` messages, under the connection's headers, timeout,
size limits, error mapping and disposal checks. The method is named bare (`GetSymbol`) or
qualified (`sysml.SysMLService/GetSymbol`); a name the schema does not have, or messages it
does not pair with the method, fails with `InvalidRequestException` and sends nothing. It is
the escape hatch below the typed API: it checks no capability and interprets no result, so
a failure the service reports inside an answer comes back as an ordinary response.

### Failures

Everything the client reports derives from `OpenSysMLException`, except cancellation and
use after disposal, which are the standard exceptions:

| What happened | Exception |
| --- | --- |
| An option, address, header or raw method/message pairing is invalid | `InvalidRequestException`, before anything is sent |
| No service is named and `AutoStart` is false, or the private service did not start | `ServiceStartException` |
| No service is named and no binary was found to start | `BinaryNotFoundException`, a `ServiceStartException` with `SearchedLocations` |
| The binary does not have `ExpectedBinarySha256` | `ChecksumMismatchException`, a `ServiceStartException` with `BinaryPath`, `ExpectedSha256` and `ActualSha256` |
| This platform cannot start a process | `UnsupportedPlatformException`, a `ServiceStartException` with `Platform` |
| The private service exited on its own, or was stopped by `StopSharedServicesAsync` | `TransportException` with `Unavailable`, saying which |
| `StopSharedServicesAsync` could not stop some services cleanly | `ServiceStopException`, with one of `Failures` for each |
| The service is not the `Version` asked for | `StaleServiceException` |
| The service lacks a required capability | `CapabilityException`, with `Capability` and `Remedy` |
| The service answered with a status other than OK, `Unavailable` included | `ServiceException`, with `StatusCode`, `ServiceMessage` and `Method` |
| The service no longer holds the model a call named | `ModelNotFoundException`, a `ServiceException` with `NotFound` |
| The service could not read a file a parse named | `ModelFileNotFoundException`, a `ServiceException` with `NotFound` |
| The service answered, reporting a failure about the model: an expression that did not evaluate, a symbol that is not there, an object that could not be built | `ModelException`, with `Method` and the answer's `Diagnostics`; `SymbolNotFoundException`, with `Name` and `Suggestions`, for a symbol; `ModelErrorsException` from `RequireNoErrors` |
| The service answered that a run failed: no initial node, no such behavior, a performer that could not be made, a run still waiting when its advance ran out | `ExecutionException`, a `ModelException` with the run's `FinalTime` and, for a traced state run, its `Trace` up to the failure and `TraceDropped` |
| The service answered a check it refused before any search: the behavior, a property or the performer does not resolve | `CheckException`, a `ModelException` with the `Partial` result the answer carried, including its verdict when supplied |
| The service answered that it could not verify, validate, calculate or sweep at all: an unknown symbol, a symbol of another kind, a subject or argument that does not read, a range no values follow from | `VerificationException`, a `ModelException` with the failure's `Reason` (`FailureReason.WrongKind` for a symbol of another kind) and the `Instances`, `Verifications` and engine `Standing` the answer carried |
| The service answered that an analysis case could not run to its end | `AnalysisException`, a `VerificationException` with the `Partial` run: the outputs computed, the verdicts and evaluations made and the objects named before the failure |
| No well-formed answer was read: the endpoint was unreachable or did not speak the protocol, a Connect refusal was not a Connect error, the answer was malformed or over a size limit, or the deadline passed | `TransportException`, with `StatusCode`, `Method` and the cause as `InnerException` |
| The service answered a value of a kind this client does not know | `UnsupportedValueException`, with `Method`, `Path` and the value's `ValueBytes` |
| The caller's token was cancelled | `OperationCanceledException` carrying that token |
| The connection was disposed before or during the call | `ObjectDisposedException` |

A status is the service's only when the service sent it. Over gRPC, that is when the response
carried it and the gRPC library raised it unchanged; a status the library made up for a broken
connection or a malformed answer is a `TransportException`. Over Connect, it is when the body
of a refusal is a Connect error, `{"code": …, "message": …}` with one of the protocol's codes,
which is JSON whatever the request's encoding; the code, not the HTTP status, is the status.
Any other refusal, such as a proxy's HTML page or the plain 404 an HTTP router answers for a
method it does not have, is a `TransportException` whose status is inferred from the HTTP
status as the Connect protocol specifies, quoting up to 200 characters of what was answered
with control characters blanked. So over Connect, a service too old to have `GetServerInfo`
fails to open rather than answering `Unimplemented`.

A deadline is always a `TransportException` with `DeadlineExceeded`, whether the client or the
service saw it pass. When both the caller's token and the deadline fired, the caller's
cancellation is reported, and an answer already received is returned whatever happens to the
token afterwards. An endpoint that speaks only HTTP/1.1 fails a gRPC connection with
`Internal`, and the message says so and names `Protocol.Connect`.
This diagnosis covers both HTTP/2 protocol errors and failed version negotiation,
which runtimes can report differently.

`StatusCode` numbers its values as gRPC does and the Connect protocol names them. Where the
gRPC library's status disagrees with gRPC's own definitions, the client reports gRPC's: an
answer that does not parse is `Internal`, and a connection that could not be established (name
lookup, refusal, TLS) is `Unavailable`. Connect failures are classified the same way: an
answer that does not parse, is cut short or is not HTTP is `Internal`, and a connection that
could not be made or was lost before an answer is `Unavailable`.

`DisposeAsync` refuses new calls, cancels the calls in flight, waits for them and closes the
channel, then gives up its hold on a private service, stopping it if it was the last. It is
idempotent, safe to call concurrently, and never throws: a failure to close or to stop is
logged as a warning.

## Models

```csharp
await using var connection = await Connection.OpenAsync(new ConnectionOptions { Address = "localhost:50051" });
var model = (await connection.LoadAsync("vehicle.sysml")).RequireNoErrors();
var vehicle = await model.LookupAsync("Vehicle");                       // Demo::Vehicle (partDef)
var mass = await model.EvalAsync("mass", new EvalOptions { ContextSymbolId = vehicle.Id });
var car = await model.InstantiateAsync("Demo::Vehicle");
var engine = car.Resolve((Value.InstanceRefValue)car.Root.FeatureValues["engine"].Value!);
```

`Connection.LoadAsync` parses a file the service reads, in its own file system and with the
path as given; `LoadContentAsync` parses notation given inline, in `ParseOptions.Language`
(SysML by default); `ParseSourcesAsync` parses several `SourceDocument`s, files or named
inline notation, as one model, so a name one declares resolves in another. Each returns a
`Model`, which the service caches under its `Hash`. `Connection.ModelByHash` names one parsed
earlier without sending anything. A model is immutable and safe to share between threads: it
keeps its `Roots`, one per document, the `ParseDiagnostics` and their `Errors`, and the
`Documents` they name, and nothing a later call changes. A source with errors still parses
into a model; `HasErrors` says so, and `RequireNoErrors` turns it into a
`ModelErrorsException`.

| Method | Answers |
| --- | --- |
| `SymbolAsync` / `FindSymbolAsync` | The `Symbol` of a qualified name as the service resolves it, through imports and aliases; not found raises `SymbolNotFoundException`, its message the service's own, or answers null |
| `FindAsync` / `LookupAsync` | The symbol of a short name or of its own id, the outermost first; not found answers null or raises `SymbolNotFoundException` naming at most three near names |
| `GetDiagnosticsAsync` | The diagnostics again, under a `DiagnosticsOptions` lint selection |
| `EvalAsync` / `EvaluateAsync` | The `Value` of an expression, in `EvalOptions.ContextSymbolId`'s scope or against an object of `SubjectSymbolId`; `EvaluateAsync` keeps the diagnostics with it |
| `InstantiateAsync` | An `Instantiation`: the object built, every object reachable from it, and `Resolve` for the references among their `FeatureValues` |

A `Symbol` carries everything the service reports of an element: its kind, metadata, child
ids, attributes with their values, `TypeFacts`, `Multiplicity` and every `Specialization`.
Each fact is read whatever the service advertises, and one the service did not report is
empty or null, never invented. `FindAsync` and `LookupAsync` ask the service which elements
carry a name when it advertises `query`, in one or two calls whatever the model's size, and
otherwise walk the model from its roots, one call per symbol; a model named by hash alone has
no roots to walk and needs `query`. Their near names are those Python's `difflib` would
suggest, each named once, comparing Unicode code points and applying its automatic
popular-character heuristic to names of at least 200 code points.

Options a service would silently ignore without its capability are refused first, with
`CapabilityException` and before anything is sent, in the order the service checks them:
`strict_conformance` for `StrictConformance`, `inline_language` for a language named for
inline notation, `parse_sources`, `parse_sources_affected` for `SourcesOptions.BaseModelHash`,
and `evaluate_subject` for a subject. A file's notation comes from its extension, so loading
one with `ParseOptions.Language` set is refused with `InvalidRequestException`.

A model loaded through a connection borrows it: disposing the model does nothing, and the
connection's owner closes it. `OpenSysMLClient.LoadAsync` and `LoadContentAsync` open a
connection of their own, which the model they return owns (`OwnsConnection`) and closes,
once, when it is disposed. Every concurrent disposal waits for that same close; a parse that
fails closes it before the failure is reported.

```csharp
await using var model = await OpenSysMLClient.LoadContentAsync(
    "package P { part def A; }", new ConnectionOptions { Address = "localhost:50051" });
```

## Running and checking behavior

```csharp
var model = (await connection.LoadAsync("behavior.sysml")).RequireNoErrors();
var run = await model.ExecuteActionAsync("Test::addFive", new Dictionary<string, Value> { ["result"] = new Value.IntegerValue(10) });
var declared = await model.ExecuteActionAsync("Test::race", options: new RunOptions { Schedule = "declared" });
var every = await model.ExploreActionAsync("Test::race");             // every outcome, and whether that is all of them
var traced = await model.ExecuteStateAsync("Test::Machine", options: new RunOptions { Trace = true });
var tank = (await connection.LoadAsync("tank.sysml")).RequireNoErrors();
var check = await tank.CheckActionAsync("Plant::Tank::fill", options: new CheckOptions
{
    PerformerSymbolId = "Plant::tank", Properties = ["Plant::Tank::low"], Diverge = ["this.level"],
});
```

| Method | Answers |
| --- | --- |
| `ExecuteActionAsync` | An `ActionRun`: the action's `Outputs`, the `PerformerAttributes` of the object it ran on (`this.level`), its `Diagnostics` (choice points, unevaluable guards), the `FinalTime` of its clock, and the outputs each `OrderDependent` choice touched |
| `ExecuteStateAsync` | A `StateRun`: the `StatesVisited` in the order the run entered them, the `FinalContext`, and, when `RunOptions.Trace` asks for it, the run's `Trace` of `DocumentEvent`s and how many records the service dropped |
| `ExploreActionAsync` / `ExploreStateAsync` | An `Exploration`: each distinct `Outcome` with its linearizations, witness choices and model-draw probability, whether the exploration was `Complete`, and the budgets that cut it short |
| `CheckActionAsync` / `CheckStateAsync` | A `CheckResult`: the verdict (`holds`, `fails`, `undecided`), the report's lines, the search's statistics, each violation, divergence and final outcome with a `CheckWitness` that replays it, the symbolic engine's claim, and the `Standing` of the engine that answered |

`RunOptions` carries what the `sysml` command's run flags do: the `Schedule` (`declared`,
`reverse`, `seed:<n>`, or `explore[:runs=<n>,depth=<d>]` for an exploration), the
`PerformerSymbolId` of the object performing the behavior, the state-run `Trace`, and the
run's `Advance`, `Seed`, `Draws` and `ClockStep`. A run and an exploration answer differently,
so each call takes only its own kind of schedule: an exploring schedule passed to
`ExecuteActionAsync`, a single-run schedule passed to an explore call, or a trace asked of an
action or an exploration is an `InvalidRequestException` before anything is sent. An explore
call naming no schedule explores under the service's default budget. `CheckOptions` carries
the `-check-*` flags: the properties held at every stable state, the features allowed to
diverge, the symbolic engine's free inputs and assumptions, whether each witness carries its
file, the bounds, the engine and the performer. A failing property is a `CheckResult`, never
an exception. A value the service would not accept, a malformed schedule or budget, an unknown
draw policy or engine, and a negative bound are the service's to refuse, with
`InvalidArgument`.

Before anything is sent, a call is held to the capabilities its options need, in the order
the service checks them: `schedule` for a schedule and `schedule_explore` for an exploring
one, `state_trace` for a trace, `performer` for a performer, and the value capabilities of
each input; a check needs `check`, then `performer`, `engines` for an engine and
`schedule_explore` for the `explore` engine. `final_time` and `order_dependence` only say
whether the service fills those fields: they are read whatever it advertises, as 0 or empty
from a service without them.

A trace record, and the row of a document query, is read into the `DocumentValue` family:
an element (with no `Id` when the service names an anonymous one), an object, a string, a
number kept as the `Value.NumberValue` it was sent as, a boolean, `*`, a quantity, or a
verdict, state or event row. An arm this client does not know is an
`UnsupportedValueException`, never an anonymous element, and an answer of the wrong shape (an
exploration answering one run, a run answering an exploration) is a malformed answer, a
`TransportException` with `Internal`.

A check answer containing neither a verdict nor an error is also malformed and raises
`TransportException` with `Internal`. An in-band error remains a `CheckException`, preserving
the partial result even when the service omitted its verdict.

## Verifying, calculating and analyzing

```csharp
var model = (await connection.LoadAsync("verification.sysml")).RequireNoErrors();
var light = await model.VerifyConstraintAsync("Demo::Vehicle::massLight");          // Verdict.Violated, with its Condition
var onSedan = await model.VerifyRequirementAsync("Demo::Vehicle::tiny", new VerifyOptions { SubjectSymbolId = "Demo::sedan" });
var proved = await model.VerifyConstraintAsync("Demo::sq", new VerifyOptions { Question = VerifyOptions.Holds });
var sedan = await model.ValidateInstanceAsync("Demo::sedan");                       // every assertion, and a Summary
var sum = await model.CalcAsync("Demo::add", [new Value.IntegerValue(2), new Value.IntegerValue(3)]);
var analysis = (await connection.LoadAsync("analysis.sysml")).RequireNoErrors();
var sweep = (await connection.LoadAsync("sweep.sysml")).RequireNoErrors();
var cost = await analysis.RunAnalysisAsync("An::CostAnalysis", new AnalysisOptions { SubjectSymbolId = "An::barge" });
var table = await sweep.RunSweepAsync("Sw::Ratio", [new SweepRange("b", new Value.IntegerValue(-1), new Value.IntegerValue(1))],
    new SweepOptions { Arguments = [new Value.RealValue(4)] });                       // the b = 0 row carries its error
var engines = await connection.ListEnginesAsync();
```

| Method | Answers |
| --- | --- |
| `VerifyConstraintAsync` / `VerifyRequirementAsync` | A `Verification`: the `Verdict`, the objects it is about with its `Subject`, and, for a requirement, the `Verifications` its verification cases answered |
| `VerifySatisfactionAsync` / `SatisfiedAsync` | A `Satisfaction`: one verdict per satisfaction assertion the model, or one element of it, states, each against an object of its own subject; `SatisfiedAsync` answers whether every one is decided and holds |
| `ValidateInstanceAsync` | A `Validation`: one verdict per assertion about an object of a part and the objects it holds, each with its `InstancePath`, the `Summary` about the object as a whole, and whether nesting was left `Bounded` |
| `CalcAsync` | A `Calculation`: the `Result` an invocation returned, or each output feature a calc usage named with no arguments computed, in declaration order |
| `RunAnalysisAsync` | An `Analysis`: the case's `Outputs` in declaration order, the verdicts of its objective and assertions, a verification case's `Verifications`, the `Evaluations` a trade study made with the `Selected` one, the objects they name, and with `AnalysisOptions.ToolDryRun` the `ToolDryRun` preview of the external tool the run reached |
| `ExploreAnalysisAsync` | An `Exploration` of the case's distinct outcomes, as for a behavior, with the `Standing` of the engine that answered |
| `RunSweepAsync` | A `Sweep`: one `SweepRow` per run, each with the inputs it bound, its outputs, verdicts, evaluations and wall time, and the error of a run that failed; the swept `Parameters`, and whether the rows were `Sampled` with the `Seed` |
| `Connection.ListEnginesAsync` | Each `EngineInfo`, in name order: what it answers, its authority, its bounds, whether it is ready and served, and where a manifest engine comes from; `ListEnginesOptions.Probe` starts each external engine once to check its handshake, which a listing alone never does |

A verdict of false is an answer, never an exception: `Verdict.Decided` is true and `Violated`
names it. A verdict that could not be evaluated, an unbound feature, a solver that decided
nothing, or a symbol of another kind, is answered too, with its `Error` and `FailureReason`,
and `Decided` false. Every verdict carries the `Standing` of the engine that answered, the
`Question` it answers (`evaluate`, `holds`, `satisfiable`) with its `Status`, and the
`Witness` assignment that replays a violated or satisfiable answer. Only a request the
service could not answer at all raises `VerificationException`, whose `Reason` tells a wrong
request (`FailureReason.WrongKind`) from one that could not be evaluated. A sweep's failed run
is that row's `Error`, beside what it computed first, and never fails the table; an analysis
case that failed raises `AnalysisException` keeping its `Partial` run.

`VerifyOptions` carries the `Question`, the `SubjectSymbolId` the verdict is about, the
`Arguments` and `NamedArguments` bound to the element's `in` parameters, and the `Engine`; a
satisfaction is asked about an object of each assertion's own subject, so it takes neither a
subject nor arguments. `CalcAsync` binds its arguments positionally, which is all the service
reads. `AnalysisOptions` carries the subject, arguments, schedule and engine, and
`SweepOptions` the subject, the arguments every row binds, `Samples` and `Seed`. A run and an
exploration of a case answer differently, so `RunAnalysisAsync` refuses an exploring schedule
and the `explore` engine, and `ExploreAnalysisAsync` a schedule that runs once and a tool dry
run, with `InvalidRequestException` before anything is sent; an exploration naming neither
explores under the service's default budget. A question, schedule or engine the service does
not know, and arguments with a solver question, are the service's to refuse, with
`InvalidArgument`; a negative sample count and a range no values follow from it refuses in its
answer.

Before anything is sent, a call is held to the capabilities its options need, in the order
the service checks them: `verification`, then `verification_questions` for `holds` or
`satisfiable`, `engines` for an engine and `schedule_explore` for the `explore` engine,
`verification_arguments` for any argument, and the value capabilities of each argument and
range endpoint; a case run checks `schedule` and `schedule_explore` for its schedule first.
`ListEnginesAsync` needs `engines`. `verification_verdicts`, `case_evaluations` and the
`engines` response fields only say whether the service fills them: they are read whatever it
advertises, empty from a service without them. A failure reason this client does not name
is kept as its number.

## Running repeatedly, self-checking and recording

```csharp
var mc = (await connection.LoadAsync("monte_carlo.sysml")).RequireNoErrors();
var runs = await mc.RunActionRunsAsync("MC::Settle", 100, options: new ActionRunsOptions { Seed = 7, Observe = ["t"] });
var t = runs.Distribution("t");                                                     // Min, Max, Mean, Percentiles, Histogram
var sample = await mc.RunAnalysisRunsAsync("MC::Mc", 100, new AnalysisRunsOptions { SubjectSymbolId = "MC::probe", Seed = 7 });
var check = await mc.SelfCheckAsync();                                              // check.Holds, check.Findings
var recording = await mc.RecordRunAsync("MC::Cost", new RecordOptions { SubjectSymbolId = "MC::p" });
var recorded = recording.Model;                                                     // a new model holding Records::Cost_run1
```

| Method | Answers |
| --- | --- |
| `RunActionRunsAsync` | `Runs`: one `SweepRow` per run, in run order, its `Inputs` the run's number as `run` and its `Outputs` what the run observed, a failed run's `Error` beside what it observed first; the `Observables` tabled and each one's `RunDistribution` over the completed runs, numeric (`Min`, `Max`, `Mean`, `Deviation`, `Percentiles`, `Histogram`) or counted by distinct `Values`; the `Lines` `sysml -runs` prints, the `Seed` echoed, and the engine's `Standing` |
| `RunAnalysisRunsAsync` | The same for a `Simulation::MonteCarlo` case, whose runs report its `observed` value, with the case's conclusion over the sample in `Outputs` and `Verdicts`, which settle every row's, or why it has none in `Unconcluded` |
| `SelfCheckAsync` | A `SelfCheck`: the `Status` (`holds`, `fails`, `unresolved`) of the `SysMLValidation` constraints, and those of any rule packages named, applied to every element, every `SelfCheckFinding`, the counts, and the elements each constraint was applied to |
| `RecordRunAsync` | A `Recording`: the `Run`, `Sweep` or `Sample` made, the record `Package`, `Definition` and `Records`, the `Source` generated, the `Documents` they were merged into (with `Content` for a model of one document the records joined), the runs `Skipped` and why, and the recorded `Model` |

The same `Seed` makes the same runs on every platform; random draws without one, unless
`Draws` fixes them as `min`, `max` or `average`, are the service's to refuse with
`InvalidArgument`, as are a run count that is not positive, an unknown draw policy, a feature
observed twice, and, for a recording, both `Ranges` and `Runs`. An action runs with its inputs,
a performer and the features to observe, a Monte Carlo case with its subject and arguments, so
each has its own method and options. A run that fails is that row's error, never an exception;
runs the service could not table, an unknown action or case, a case that is not a Monte Carlo
one, or a feature no completed run produced, raise `RunsException`, a `VerificationException`
keeping the `Partial` table. A violated self-check constraint is a finding; a rule package the
model does not declare raises `ModelException`.

A recording is a new model the service holds, named by `Recording.Model`, which borrows the
connection; the model it was asked of keeps its hash and contents, and nothing is written to
disk. With neither `RecordOptions.Ranges` nor `Runs` the case runs once, with `Ranges` once per
row of the sweep, and with `Runs` and a `Seed` as a Monte Carlo sample; `RecordInto` names the
package the records go into. The request always accepts the documents of a model of several,
since every document the records went into is returned. A recording not made raises
`RecordException` with its `FailureReason`, the run, sweep or sample made, whether that run
itself reported the failure (`RunFailed`), and, when the records were generated but would not
analyze cleanly, their `GeneratedSource`. Before anything is sent the calls need `runs`,
`self_check` or `record_run`; then `performer` for a performer and the value capabilities of
each input, `engines` and `schedule_explore` for an engine, and the value capabilities of each
argument and range endpoint, in the order the service checks them.

## Values

`Value` is an immutable value the service evaluates, or one sent to it: one nested sealed
class per arm of the schema's `Value`, `IntegerValue` to `InfinityValue`, which `Kind`, a
`ValueKind`, names. No type outside the library can derive from it. A switch over the arms
still needs a default case, since a release adds an arm whenever the service does. A
model's evaluations, attributes and feature values are these values, and so are the inputs,
arguments and sweep ranges a call sends.

```csharp
var speed = new Value.QuantityValue(new Quantity(
    new Value.RealValue(5.4), "km/h",
    new UnitTerm(1000, 3600, [new UnitFactor("SI::m", 1), new UnitFactor("SI::s", -1)])));
var third = new Value.RationalValue(new Rational(2, 6));      // 1/3
var members = new Value.SetValue([new Value.IntegerValue(3), new Value.IntegerValue(1)]);
Console.WriteLine($"{speed} {third} {members}");              // 5.4 [km/h] 1/3 {3, 1}
```

Numbers keep the schema's kinds apart and never pass through a `double`: `IntegerValue`
within `long`, `BigIntegerValue` beyond it (`Value.FromInteger` chooses), `RationalValue`
holding an exact `Rational` in lowest terms over a positive denominator, and `RealValue`,
which keeps NaN, the infinities and a negative zero exactly. These four are
`Value.NumberValue`, which a `Quantity`'s magnitude and a `VectorValue`'s components are;
`ToDouble` gives the nearest `double`, ties to even. A `Quantity` keeps its unit as written
and the `UnitTerm` it reduces to. `InfinityValue` is the model's unbounded `*`, not a Real.

Equality is by content and never the model's numeric comparison: `IntegerValue(1)` is not
`RealValue(1.0)`. Reals compare as `double.Equals` does, so NaN equals NaN and `0.0` equals
`-0.0`, while each is kept and sent as given. A `SetValue` equals one holding the same members
in any order, and hashes alike; it keeps the order it was given, which for an answer is the
service's canonical order, for reading and display only. Its membership is the same equality,
so `1` and `1.0` are two members here, although the service equates them and refuses a set
holding both. An `EnumLiteralValue` is its `LiteralId` and a `MetaobjectValue` its
`ElementId`, whatever else describes them; a `FunctionValue` is its `CalcId` and `SelfId`.
Collections are `ValueList<T>`, compared element by element in order, and
`ValueDictionary<TValue>`, compared entry by entry whatever order it was built in and
enumerated in ordinal order of its names; both copy what they are built from, and `default`
is empty. `ToString` writes a value as a reader would, in any culture: `4.0`, `1/3`,
`"text"`, `5.4 [km/h]`, `(1, 2)`, `{1, 2}`, `<undetermined>`, `*`.

Reading an answer checks what the schema makes canonical and never repairs it: an Integer
beyond `long` in canonical decimal digits, a Rational in lowest terms that no `double` holds
(the service sends one a `double` holds as a Real), dimensions the elements fill, a vector
of numbers. A malformed value is a `TransportException` with `Internal` naming where in the
answer it is, as `result.sequence[1].set[0]`, and a value of a kind the client does not know
is an `UnsupportedValueException`, never a null. Every arm is read whatever the service
advertises. Over Connect JSON a value of an unknown kind arrives with no kind at all, since
the parser drops fields it does not know.

Sending refuses what no service accepts as an argument, before anything is sent, with
`InvalidRequestException`: `UnsetValue` and `UndeterminedValue`, which are answers only; a
null standing for a value the service could not send; a function closing over an instance,
which lives only within the response that held it; and a quantity in a named unit without its
reduction. A kind the service gates needs its capability wherever the service looks for it:
in a sequence, set or array element or a vector component at any depth, and for an Integer
beyond `long` or a Rational also in a quantity's magnitude and an enumeration literal's
scalar. A missing one is a `CapabilityException` naming the first the service would refuse.
To a service without `rational_values`, a Rational a `double` holds exactly is sent as that
`double`, which is the same number; any other still needs the capability.

## Building and testing

```bash
make dotnet-test       # preflight, locked restore, build, format check, tests, sample
make dotnet-coverage   # the tests under coverlet: Cobertura and JUnit in bin/dotnet/coverage
make conformance-dotnet conformance-dotnet-check   # the conformance suite; see Conformance
```

`make dotnet-test` runs these commands, which also work on their own:

```bash
dotnet restore client/dotnet/OpenSysML.slnx --locked-mode
dotnet format client/dotnet/OpenSysML.slnx --verify-no-changes --no-restore
dotnet build client/dotnet/OpenSysML.slnx -c Release --no-restore -warnaserror
dotnet test --solution client/dotnet/OpenSysML.slnx -c Release --no-build --filter 'Category=Unit'
```

`dotnet test` runs on Microsoft.Testing.Platform, which `global.json` selects; the VSTest
form `dotnet test <solution>` is not supported. Every test declares a `Category` trait,
`Unit` or `Integration`, and a test enforces that, so a filter never silently drops one.

Integration tests run against the binary `OPENSYSML_GRPC_BINARY` names, which they start
themselves on ports the kernel chooses and reach by address: once serving its default
`connect` transport, reached over gRPC, Connect with protobuf and Connect with JSON, and once
its grpc-go-only `grpc` transport, reached over gRPC, where Connect must be refused. Others
start it as a private service, shared, isolated, killed and stopped. Unit tests of behavior
every protocol shares run over all three wires too. They skip without a binary; set
`OPENSYSML_REQUIRE_SERVICE=1` to turn that skip into a failure, as CI does. `make
dotnet-test` also runs the Vehicle sample against such a service, and with the binary alone,
which it starts privately, under the same rule:

```bash
OPENSYSML_GRPC_BINARY="$PWD/bin/sysml-grpc" OPENSYSML_REQUIRE_SERVICE=1 make dotnet-test
```

Unit tests drive every way a private service starts and stops against
[`test/FakeService`](test/FakeService/), a stand-in the solution builds and the tests copy,
under the binary's name, into a directory of their own with a script beside it: it reports,
misreports or withholds an address, exits, lingers or floods its output as the script says.
It serves nothing; the in-process fake HTTP handler answers the connection.

Two probes, which the solution also builds, prove what only a process of its own can show,
and the tests run them against the real binary. [`test/OpenMBEE.OpenSysML.OrphanProbe`](test/OpenMBEE.OpenSysML.OrphanProbe/)
opens a private service, starts unrelated processes beside it, eight of them at the very
moment the service starts in the repeated race rounds, and is killed alone: the service must
end and the other processes run on. Its leak control hands a duplicate of the service's input
to one of them, and the test must then see the service survive, and end once that process
goes, so the check is known to catch an orphan. [`test/OpenMBEE.OpenSysML.LoadContextProbe`](test/OpenMBEE.OpenSysML.LoadContextProbe/)
loads three copies of the client, with their package dependencies, into collectible load
contexts and drives each by reflection, never naming a client type in the default context:
each copy must start a service of its own, which closing or unloading that copy alone stops,
including one still starting; every copy must be collected; and the probe must then exit by
returning from `Main`. Every process id a probe reports is killed when its test ends, whatever
the outcome. Race tests release opens, closes, stops of every service, cancelled waits and a
service's death from one barrier, round after round, and require every service to be stopped
exactly once and nothing to remain registered.
Each race round has a thirty-second deadline and passes its cancellation token to the
operations. Terminal assertions wait for background service-death cleanup. Connection attempts
and resolved child process ids belong to a disposable test scope: partial acquisition or a
failed assertion still releases successful connections, and a ten-second cleanup deadline
falls back to killing and awaiting only the scope's owned processes while reporting the
timeout as a failure.

CI runs the whole suite on Linux against the service its build job makes, then both
conformance targets, and, in the full tier, natively on macOS and Windows against a
`sysml-grpc` built on that runner, with the same minimum test count.
The Python gates use ASCII success markers so preflight, sample and coverage reports
also work with legacy Windows console encodings.

## Protocol code

`src/OpenMBEE.OpenSysML/Wire/Sysml.cs` and `SysmlGrpc.cs` are the message classes and the
gRPC client stub buf generates from [`api/proto/sysml.proto`](../../api/proto/sysml.proto)
into the `OpenMBEE.OpenSysML.Wire` namespace. They are committed, so a checkout builds with
neither buf nor network access. Do not edit them; regenerate them after a schema change:

```bash
make proto-csharp      # also part of make proto; CI fails when the committed files differ
```

[`api/proto/buf.gen.csharp.yaml`](../../api/proto/buf.gen.csharp.yaml) pins the generators
to the runtime packages `Directory.Packages.props` pins: `protocolbuffers/csharp` v36.2 for
Google.Protobuf 3.36.2 and `grpc/csharp` v1.84.0, client stub only, for Grpc.Net.Client
2.84.0. The generated types are public and mutable protocol code, not the client's API:
they are left out of the public API baseline and of coverage by their exact paths, in
[`.editorconfig`](.editorconfig) and the Makefile's `DOTNET_GENERATED`, and a test fails
when either names anything else. Handwritten code refers to them as `Wire.X` rather than
importing the namespace, whose `Array` would otherwise shadow `System.Array`.

## Parity inventory

[`test/testdata/parity.json`](test/testdata/parity.json) records what the client must cover,
and the tests hold it to the schema and the [conformance scenarios](../../conformance/):

- `rpcs`: one row per RPC, in schema order, with its request and response messages, the
  public methods that will call it, its status (`deferred` until a public method calls it),
  every scenario of the RPC, and the capabilities the service requires of a request, each
  with the request field and the condition that make it required, in the order the service
  checks them. `valueFields` lists the request fields whose `Value`s are subject to the
  value gates.
- `values` and `edits`: every arm of `Value` and of `EditOperation`, with the capability a
  request holding the arm needs and, for a `Value`, the one under which the service sends it,
  and once implemented the test that proves the arm. Every `Value` arm is implemented, and
  `sendGateOrder` is the order the service checks the value capabilities in, which a test
  reads from the service's source.
- `population`: response fields the service fills only under a capability.
- `fields`: every field of every message an RPC reaches, `deferred`, or once mapped `typed`,
  `raw-only` or `unsupported` with its reason and the test that proves it.

The checks compare sets, never counts: an RPC, field, arm or scenario the schema or the
suite adds, renames or removes fails the tests until the inventory accounts for it, and so
does a scenario that requires a capability no gate or governed field of its RPC names.
The scenarios are read with the conformance runner's own reader, described next.

## Conformance runner core

[`src/OpenMBEE.OpenSysML.Conformance`](src/OpenMBEE.OpenSysML.Conformance/) is the part of a
.NET runner for the [conformance suite](../../conformance/) that does not depend on how a
call is made: it reads the scenarios, builds each request, judges each answer and writes the
reports, by the rules of the suite's README and as the reference runner,
[`tools/cmd/conformance`](../../tools/cmd/conformance/), applies them.

- **Reading.** Every `*.json` of `conformance/scenarios`, in the order of the file names'
  bytes (`1-…`, `10-…`, `2-…`), then of declaration. Reading is strict: a member the format
  does not define or states twice, a value of the wrong kind, a scenario without an id or an
  RPC, a model naming no fixture or both forms, and an id declared twice are refused.
- **Requests.** `${model_hash}`, `${fixture:…}` and `${fixture_base64:…}` are resolved where
  a whole string is one of them, a fixture outside `conformance/fixtures` is refused, and the
  protobuf-JSON is decoded into the RPC's message by the generated schema. A 64-bit integer
  keeps every digit, including decimal and exponent spellings: they are normalized exactly,
  range-checked and passed to Google.Protobuf as text rather than through a `double`.
  An invalid fixture path is a scenario error, not an abort of the remaining suite.
- **Judging.** Capability gating and without-capability expectations; each model parsed once
  per run; a deadline per parse and call, a call outliving it answering `DEADLINE_EXCEEDED`;
  the runner bounds its wait even if an adapter ignores cancellation, observes late failures
  and never caches a late parse hash. Caller cancellation is thrown, not reported as an outcome.
  Statuses compare by canonical name; responses are normalized by descriptor (version, model hash and
  absolute paths replaced, runtime instance ids labelled `@1`, `@2`, … in order of first
  appearance, enums by name, integral fields never compared as Reals) and compared: only
  what is named, every named list at its exact length, an unset field equal to its default,
  finite Reals within a relative 1e-9, integers by their exact digits for every whole-number
  spelling. NaN and infinities match only their explicit text spellings, never a finite number;
  overflowing or nonzero underflowing expectations are rejected. Mismatch messages spell
  values as the reference does.
- **Reports.** The JSON `-report` and JUnit `-junit` shapes the reference writes, member for
  member, with outcomes `pass`, `fail`, `skip` and `error`. JUnit replaces XML-invalid
  characters with U+FFFD in text and attributes; JSON retains the original strings.

How a scenario reaches the service is an adapter's: an `IScenarioClient` answers through the
client's public API and renders the result as the RPC's response message, or reports that the
API cannot express the request, which is a skip with that reason. The next section describes
the one that runs the suite.

[`test/OpenMBEE.OpenSysML.Conformance.Tests`](test/OpenMBEE.OpenSysML.Conformance.Tests/)
holds every comparison rule to a case that must hold and one that must not, ports the
reference runner's suite checks (every RPC covered, both kinds of failure pinned, every
fixture read), and pins a whole run of a small suite against a scripted adapter as goldens
of its JSON report, JUnit report and log. After an intended change to any of them, rewrite
the goldens and review the difference:

```bash
OPENSYSML_UPDATE_GOLDENS=1 dotnet test --project client/dotnet/test/OpenMBEE.OpenSysML.Conformance.Tests -c Release
```

## Conformance

The same project is the runner: it runs the suite **through the public API**, so a report says
what the client gave its caller, not only what the service put on the wire.

```bash
make build-grpc
make conformance-dotnet        # every protocol, then strict_conformance and oslc_query withheld;
                               # bin/conformance-report-dotnet.json and .xml
make conformance-dotnet-check  # the same run beside the reference runner's, compared scenario by scenario
dotnet run --project client/dotnet/src/OpenMBEE.OpenSysML.Conformance -c Release -- \
  -binary bin/sysml-grpc -run '^evaluate/' -protocols connect-json -v
```

| Flag | Meaning |
| --- | --- |
| `-binary <path>` | The `sysml-grpc` to start, once per configuration, on ports the kernel chooses |
| `-service <host:port>` | A service already running, instead; it cannot withhold capabilities or choose a transport |
| `-dir <path>`, `-expected-skips <path>` | The suite and the reviewed skips; by default the checkout's `conformance/` and [`test/testdata/expected-skips.json`](test/testdata/expected-skips.json) |
| `-run <regexp>` | Only the scenarios whose id matches |
| `-protocols <list>` | Of `grpc`, `connect`, `connect-json`; all three by default |
| `-transport <name>` | What a started service serves: `connect` (default) or `grpc`, which only `-protocols grpc` can reach |
| `-withhold-capabilities <list>` | Also run a configuration whose service withholds these, through its test-only switch |
| `-report <path>`, `-junit <path>` | The reference runner's JSON (`-` for stdout) and JUnit reports |
| `-allow-skips` | Accept every skip, not only the reviewed ones |
| `-mutate <name>` | Corrupt every answer, to show the comparison is not vacuous |
| `-v` | Print each normalized response, as the reference runner does |

A flag the runner does not know, one given twice, a value missing, and a contradictory line
are usage errors (exit 2). A following option is never consumed as a value. Value-taking flags
also accept `-flag=value`; use this form for a literal value starting with `-`, such as
`-run=-v`, while `-report -` still selects stdout. A run that passes exits 0, and any other
exits 1, including a JSON or JUnit destination that cannot be written: the diagnostic names
the report format, destination and filesystem error.

Each RPC the API covers has an adapter, grouped by area beside the methods it calls
(`ConnectionArea`, `ModelArea`, `ExecutionArea`, `VerificationArea`, `SamplingArea`). An adapter reads the
decoded request as the arguments of the typed method, so the request the client sends is the
one the scenario states, and renders what the method returned, with
[`Rendering`](src/OpenMBEE.OpenSysML.Conformance/Rendering.cs) reading only public types, back
into the response. The values a request sends are read into public `Value`s by public
constructors, so the typed call writes them itself. ExecuteAction and ExecuteState call the
explore method when the schedule explores, and RunAnalysis when its schedule or engine does,
and the run method otherwise; RunRuns calls the action or the Monte Carlo case method by which
the request names. A failure the service reported in band is rendered as the
response's `error`, failure reason and diagnostics, with what the exception kept of the answer:
a run's clock and trace, a check's or an analysis's partial result, a verification's objects,
verification verdicts and engine standing, the table runs made, and the run, sweep or sample a
recording made, with its own failure where it reported one; a refusal keeps the service's status and message; a
capability the client refuses for before sending is `UNIMPLEMENTED`, which the service would
answer; a transport failure is its status. Nothing is sent raw: a test reads the runner's IL
and fails on any call to `Connection.CallAsync` or the generated stub. A request setting a field
the adapter cannot send, or one the API has no way to express, is skipped for a stated reason;
so is one the API itself refuses with `InvalidRequestException`, for that exception's message.

Skips are accounted exactly. `expected-skips.json` lists the RPCs no adapter covers yet,
which must be the inventory's `deferred` rows, and the scenarios whose request the API cannot
express, each with the reason its adapter gives. The run fails for any other skip, a listed
skip given another reason, and a listed skip that ran, whether or not `-allow-skips` is
passed; in a configuration withholding capabilities, a skip is accounted only by a withheld
capability, the service must advertise exactly the default set without them, and each must
have its without-capability expectation executed, or, when every such scenario is a listed
skip, the run says so. Per protocol, of 194 scenarios:

| Configuration | ran | passed | skipped as uncovered | skipped as unexpressible |
| --- | --: | --: | --: | --: |
| default | 121 | 121 | 69 | 4 |
| `strict_conformance` and `oslc_query` withheld | 120 | 120 | 69 | 4 |

The unexpressible requests send what the public types refuse to hold or send:
`parse/naming_no_source_is_invalid` names no source, which the API always does, and three
EvaluateCalc scenarios send a measurement reference without its reduction, a vector with a
string component, and a function closing over an object of another call. Withheld, `strict_conformance`'s fallback runs and is refused before
sending; `oslc_query`'s is a `Query` scenario, an uncovered RPC, and is reported as a listed skip.

`-mutate` shows the comparison catches what each corruption breaks, on every protocol, and a
test asserts it fails the run:

| `-mutate` | What it does to every answer | Scenarios that fail, per protocol |
| --- | --- | --: |
| `perturb-reals` | Moves each real by a millionth | 26 |
| `truncate-lists` | Drops the last entry of each list | 39 |
| `rewrite-strings` | Replaces each non-empty string but no map key | 62 |

`make conformance-dotnet-check` also runs the reference runner against the same binary, both
with `-v`, and `dotnet-gates.py conformance-check` holds the two runs together: both reports
have the reference's members in its order and totals that add up, the JUnit report states the
same results, including each testcase's pass, failure, error or skip disposition, and every
scenario the .NET run did not skip as reviewed has the reference's
outcome, status, missing capabilities and normalized response. Only timing, including the
wall time each row of a sweep or of runs reports and the time a recording states in the
records it generates, with the recorded model's hash that follows from it, which compares only
as present, and what answered are not compared, and the service's capabilities compare as a
set, which is how the client holds them.

## Build policy

[`Directory.Build.props`](Directory.Build.props) holds the policy every project shares:
nullable reference types, deterministic builds, warnings as errors, the recommended .NET
analyzers, and generated XML documentation, with a public member that has no XML comment
failing the build. The library tracks its public API in `PublicAPI.Shipped.txt` and
`PublicAPI.Unshipped.txt`; a public symbol missing from them fails the build.

Package versions are pinned once, in [`Directory.Packages.props`](Directory.Packages.props),
and each project's `packages.lock.json` records the resolved graph: a locked restore
fails on any change to either. [`nuget.config`](nuget.config) restores from nuget.org
alone.

## Version and pinned release digests

The client's version is the `Version` in `Directory.Build.props`, the SemVer spelling of
the core version it is released with. `python3 client/python/scripts/check_version.py
--tag v<version> --dotnet` checks it, and the Python suite fails when it disagrees with
the Python client's version.

`src/OpenMBEE.OpenSysML/release-digests.json` is a copy of
[`client/release-digests.json`](../release-digests.json), embedded in the assembly. Do not
edit it: `python3 scripts/sync-release-digests.py` rewrites it, and its `--check` and the
tests fail when it drifts.
