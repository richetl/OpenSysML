# The .NET client API

This page lists what the `OpenMBEE.OpenSysML` assembly exposes and the rules it keeps. To choose
between the clients, see [client libraries](clients.md); for a task-oriented walkthrough, see the
[.NET client guide](../clients/dotnet.md). The client's own README,
[client/dotnet/README.md](../../client/dotnet/README.md), states every rule below in full, with
its build, test and conformance procedures.

The client is consumed from a checkout by `ProjectReference`, never from NuGet:

```xml
<ProjectReference Include="path/to/OpenSysML/client/dotnet/src/OpenMBEE.OpenSysML/OpenMBEE.OpenSysML.csproj" />
```

It targets **`net10.0`** alone, built with the SDK the repository-root `global.json` pins. Every
call is asynchronous, returns a `Task` and takes an optional `CancellationToken`; a cancelled call
throws `OperationCanceledException` carrying the caller's token. Results are immutable and safe
to share between threads. The assembly makes no `IsAotCompatible` or `IsTrimmable` claim.

## Connection

```csharp
using OpenMBEE.OpenSysML;

await using var connection = await Connection.OpenAsync(new ConnectionOptions
{
    Address = "localhost:50051",
    RequireCapabilities = [Capabilities.Query],
});
Console.WriteLine(connection.ServerInfo);          // sysml-grpc <version>, <count> capabilities
bool converts = connection.Capabilities.Has(Capabilities.Convert);
```

`Connection.OpenAsync` reaches the service `Address` names, else the one `OPENSYSML_SERVICE`
names, else starts a private one. It asks `GetServerInfo` once, keeps the answer as `ServerInfo`
and `Capabilities`, and checks `Version` and `RequireCapabilities` against it. `DisposeAsync`
refuses new calls, cancels those in flight and releases the connection's hold on a private
service; it is idempotent and never throws.

| `ConnectionOptions` | Meaning |
| --- | --- |
| `Address` | `host:port`, `[::1]:port` or an `http`/`https` URL with nothing after its port |
| `Protocol`, `Encoding` | `Grpc` (default) or `Connect`; `Protobuf` (default) or `Json`, which only `Connect` carries |
| `RequestTimeout` | Each call's deadline, 60 seconds by default |
| `Headers` | Sent with every call; protocol-controlled headers are refused and values are never logged |
| `MaxRequestSize`, `MaxResponseSize` | 512 MiB each by default, a response measured after decompression |
| `Logger` | An `ILogger`, `NullLogger` by default |
| `HttpMessageHandler` | A handler to borrow, never disposed by the connection |
| `Version`, `RequireCapabilities` | An exact service version (`StaleServiceException`) and required capabilities (`CapabilityException`) |
| `AutoStart` | Whether a connection naming no service starts a private one; true by default |
| `BinaryPath`, `ExpectedBinarySha256` | The private service's binary, and the digest it must have |
| `IsolatedService` | A private service of this connection's own rather than a shared one |
| `StartupTimeout`, `ShutdownTimeout` | 30 and 5 seconds by default |
| `DownloadVersion`, `GithubRepo` | The release to download, a tag or `latest`, and its repository |

The private-service options are refused alongside `Address` or with `AutoStart` false. Every
option is checked before anything is sent; an invalid one is an `InvalidRequestException`.
`ConnectionOptions` also names the environment variables as constants: `ServiceVariable`,
`BinaryVariable`, `VersionVariable`, `GithubRepoVariable` and `AllowUnpinnedDownloadVariable`.

| `Connection` member | Answers |
| --- | --- |
| `LoadAsync(path)` | A `Model` of a file the service reads, by its path as given |
| `LoadContentAsync(content)` | A `Model` of inline notation, in `ParseOptions.Language` |
| `ParseSourcesAsync(documents)` | One `Model` of several `SourceDocument`s, files or named inline notation |
| `ModelByHash(hash)` | A `Model` the service already holds, without a call |
| `ConvertAsync`, `MigrateAsync` | A `Conversion` between notation, Turtle and API JSON; a `Migration` of a SysML v1 model |
| `ListEnginesAsync` | Each `EngineInfo` the service can answer with |
| `CallAsync<TRequest, TResponse>(method, request)` | Any RPC with the generated `OpenMBEE.OpenSysML.Wire` messages, unchecked by capability |
| `Protocol`, `Encoding`, `Address`, `ServerInfo`, `Capabilities` | What the connection speaks, where, and what the service reported |
| `StopSharedServicesAsync()` (static) | Stops every shared private service of this copy of the client |

`OpenSysMLClient.LoadAsync` and `LoadContentAsync` open a connection of their own, which the
returned model owns and closes when it is disposed.

## Model

A `Model` is the service's parse of its sources, named by `Hash`: its `Roots`, `Documents`,
`ParseDiagnostics` and their `Errors`. `HasErrors` says whether it has errors, and
`RequireNoErrors` raises `ModelErrorsException` when it does. A model loaded through a connection
borrows it. Each method below is one RPC:

| Area | Methods | Result |
| --- | --- | --- |
| Symbols | `SymbolAsync`, `FindSymbolAsync`, `FindAsync`, `LookupAsync` | `Symbol`, or null from the `Find` forms |
| Diagnostics | `GetDiagnosticsAsync` | `ValueList<Diagnostic>`, under a lint selection |
| Expressions | `EvalAsync`, `EvaluateAsync` | `Value`; `Evaluation` keeps its diagnostics |
| Objects | `InstantiateAsync` | `Instantiation` |
| Behavior | `ExecuteActionAsync`, `ExecuteStateAsync`, `ExploreActionAsync`, `ExploreStateAsync`, `CheckActionAsync`, `CheckStateAsync` | `ActionRun`, `StateRun`, `Exploration`, `CheckResult` |
| Verification | `VerifyConstraintAsync`, `VerifyRequirementAsync`, `VerifySatisfactionAsync`, `SatisfiedAsync`, `ValidateInstanceAsync` | `Verification`, `Satisfaction`, `bool`, `Validation` |
| Calculation and analysis | `CalcAsync`, `RunAnalysisAsync`, `ExploreAnalysisAsync`, `RunSweepAsync` | `Calculation`, `Analysis`, `Exploration`, `Sweep` |
| Repeated runs | `RunActionRunsAsync`, `RunAnalysisRunsAsync`, `SelfCheckAsync`, `RecordRunAsync` | `Runs`, `SelfCheck`, `Recording` |
| Conversion | `ConvertAsync`, `ToNotationAsync`, `ToTurtleAsync`, `ToApiJsonAsync`, `SaveAsync` | `Conversion` |
| Queries and documents | `QueryAsync`, `QueryOslcAsync`, `RunDocumentQueryAsync`, `RenderDocumentAsync`, `RenderViewAsync` | `QueryResult`, `DocumentQueryResult`, `RenderedDocument`, `RenderedView` |
| Editing | `Edit()`, `ApplyEditsAsync` | an `Editor`; `EditResult` |
| Graphs and data | `ExportGraphsAsync`, `ImportDataAsync`, `CompareResultsAsync` | `Graphs`, `ImportResult`, `Comparison` |

Each takes its own options record (`EvalOptions`, `RunOptions`, `CheckOptions`, `VerifyOptions`,
`AnalysisOptions`, `SweepOptions`, `ConvertOptions`, `EditOptions`, …), so a call accepts only
what its RPC can use; a combination its RPC cannot answer, such as an exploring schedule passed to
`ExecuteActionAsync`, is refused before anything is sent. A false verdict, a failed sweep row and
a failed run among repeated runs are answers, never exceptions.

## Writing files

Nothing is written to disk until `Model.SaveAsync`, `Conversion.SaveAsync`,
`Migration.WriteAsync`, `EditResult.SaveAsync` or `EditedDocument.SaveAsync` is called. Each
writes a file whole through a temporary file beside it, never replaces an existing file unless
`SaveOptions.Overwrite` asks, and refuses an unsafe destination (a symbolic link, the v1 source
of a migration, a migration asset path that escapes its directory or is not a portable file name)
with `UnwritableException` before writing anything.

## Values

`Value` is an immutable sealed hierarchy with one nested class per arm of the schema's `Value`:
`IntegerValue`, `BigIntegerValue`, `RationalValue`, `RealValue`, `ComplexValue`, `BooleanValue`,
`TextValue`, `QuantityValue`, `VectorValue`, `VectorQuantityValue`, `TensorQuantityValue`,
`ArrayValue`, `SequenceValue`, `SetValue`, `EnumLiteralValue`, `InstanceRefValue`,
`MeasurementRefValue`, `FunctionValue`, `MetaobjectValue`, `NullValue`, `UnsetValue`,
`UndeterminedValue` and `InfinityValue`, named by `Kind`. The four numeric arms derive from
`Value.NumberValue`. A `switch` over the arms needs a default case, since a release adds an arm
whenever the service does.

Numbers never pass through a `double`: an Integer beyond `long` is a `BigIntegerValue`, and a
Rational is exact in lowest terms. Equality is by content, never the model's numeric comparison,
so `IntegerValue(1)` is not `RealValue(1.0)`. `ToString` writes a value as a reader would, in any
culture: `1800.0`, `1/3`, `5.4 [km/h]`, `{1, 2}`, `*`. Collections are `ValueList<T>` and
`ValueDictionary<TValue>`, compared by content.

## Exceptions

Every failure derives from `OpenSysMLException`, except cancellation and use after disposal:

```text
OpenSysMLException
├── InvalidRequestException        refused before anything was sent
├── CapabilityException            the service lacks a capability; Capability, Remedy
├── ServiceStartException          no service could be started or reached
│   ├── BinaryNotFoundException       SearchedLocations
│   ├── ChecksumMismatchException     BinaryPath, ExpectedSha256, ActualSha256
│   ├── UnpinnedReleaseException      Repository, Version, Asset
│   ├── UnsupportedPlatformException  Platform
│   └── StaleServiceException
├── ServiceStopException           Failures
├── ServiceException               a status the service sent; StatusCode, ServiceMessage, Method
│   ├── ModelNotFoundException
│   └── ModelFileNotFoundException
├── ModelException                 the service answered a failure about the model; Diagnostics
│   ├── ModelErrorsException, SymbolNotFoundException, ExecutionException, CheckException
│   ├── QueryException, ConversionException, MigrationException, EditException
│   └── VerificationException
│       └── AnalysisException, RunsException, RecordException, CompareException
├── TransportException             no well-formed answer was read; StatusCode, Method
├── UnsupportedValueException      a value of a kind this client does not know
└── UnwritableException            a destination that cannot be written safely
```

A `ServiceException` is only ever a status the service itself sent. A status the transport made
up, a malformed answer, an over-limit response and a passed deadline are `TransportException`s.
A failure that kept a partial result carries it: a check's or an analysis's `Partial`, a run's
`Trace`, a refused edit's `Referrers`.

## Capability negotiation

`Capabilities` holds a constant for each capability name the service can advertise. Decide on
those names, never on the version. Before sending, every typed call checks the capabilities its
arguments need, in the order the service checks them, and raises `CapabilityException` naming the
first one missing, so a gated option is never sent to a service that would ignore it. Response
fields a service fills only under a capability read as empty or null, never invented, from a
service without it. `CallAsync` is the escape hatch below this and checks nothing.

## The service binary

A private service is started from `BinaryPath`, else `OPENSYSML_GRPC_BINARY`, else the release
asked for in the shared cache `~/.opensysml/bin/sysml-grpc` (`sysml-grpc.exe` on Windows),
which it downloads, verifies and installs under the cache's cross-client lock when the cache does
not hold it. When the release the client was built with cannot be had, the first `sysml-grpc` on
`PATH` is started instead. The release is `DownloadVersion`, else `OPENSYSML_GRPC_VERSION`, else
`v` and the client's version; its repository is `GithubRepo`, else `OPENSYSML_GITHUB_REPO`, else
`Open-MBEE/OpenSysML`. Releases are published for Linux and macOS on x64 and arm64 and for
Windows on x64; another platform that asks for one fails with `UnsupportedPlatformException`.

A download is trusted only against the SHA-256 pins embedded in the assembly, a copy of
`client/release-digests.json`; the digest the release serves must agree with the pin. The client
does not verify the Sigstore signature on a release's checksum manifest, so a release it pins no
digest for fails with `UnpinnedReleaseException` before anything is fetched, unless
`OPENSYSML_ALLOW_UNPINNED_DOWNLOAD` grants same-origin trust. That trusts the digest served beside
the binary, which detects corruption but not a compromised release. Releases come over HTTPS
alone, from `github.com` and `api.github.com`; a stalled or oversized response is abandoned and
never retried.

## What the client does not do

- It is not on NuGet, and has no packaged `dotnet` tool, symbol package or SourceLink.
- It targets `net10.0` only, and makes no Native AOT, trimming, browser or Blazor claim.
- It has no synchronous API, no notebook display hooks and no DataFrame integration.
- It has no generated typed model classes and no standalone metamodel reader.
- It never retries a call, falls back to another protocol, or bypasses TLS validation.
- It starts no service implicitly from the compiler or MSBuild.

## Conformance

The client's conformance runner runs the shared [conformance suite](../../conformance/) through
the public API, over gRPC, Connect with protobuf and Connect with JSON, and again against a
service withholding `strict_conformance` and `oslc_query`. The only skips are the reviewed
requests the public types cannot express:

```bash
make build-grpc
make conformance-dotnet          # bin/conformance-report-dotnet.json and .xml
make conformance-dotnet-check    # the same run beside the reference runner's, scenario by scenario
```

The scenario counts, the reviewed skips and the runner's flags are in
[client/dotnet/README.md](../../client/dotnet/README.md#conformance).
