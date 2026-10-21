# OpenSysML .NET client

`OpenMBEE.OpenSysML` is the C# client for the `sysml-grpc` service, built from a checkout
of this repository and consumed by `ProjectReference`. It is not published to NuGet.

> **Status:** the client connects to a `sysml-grpc` that is already running, over gRPC or the
> Connect protocol with protobuf or JSON bodies, reports what that service is and can do, and
> calls any of its methods through a raw, schema-checked escape hatch. It has the immutable
> values those operations exchange, but does not yet start a service of its own, load models
> or offer the typed service operations. The other clients
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
service, what that service reports:

```bash
bin/sysml-grpc -port 50051 &
OPENSYSML_SERVICE=localhost:50051 dotnet run --project client/dotnet/samples/Vehicle -c Release
# OpenMBEE.OpenSysML 0.10.1 (.NETCoreApp,Version=v10.0)
# sysml-grpc v0.10.1, 72 capabilities
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
the `OPENSYSML_SERVICE` environment variable names. An address is `host:port`, which means
`http://host:port`, a bracketed IPv6 `[::1]:port`, or an `http` or `https` URL with nothing
after its port; user information, a path, a query or a fragment is refused. A service named
this way is never started, stopped or downloaded by the client. With no service named,
`OpenAsync` fails with `ServiceStartException`: starting a private service is not available
yet, whatever `AutoStart` says, and `AutoStart` stays `true` by default so that this does not
change when it is.

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
| No service is named | `ServiceStartException` |
| The service is not the `Version` asked for | `StaleServiceException` |
| The service lacks a required capability | `CapabilityException`, with `Capability` and `Remedy` |
| The service answered with a status other than OK, `Unavailable` included | `ServiceException`, with `StatusCode`, `ServiceMessage` and `Method` |
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

`StatusCode` numbers its values as gRPC does and the Connect protocol names them. Where the
gRPC library's status disagrees with gRPC's own definitions, the client reports gRPC's: an
answer that does not parse is `Internal`, and a connection that could not be established (name
lookup, refusal, TLS) is `Unavailable`. Connect failures are classified the same way: an
answer that does not parse, is cut short or is not HTTP is `Internal`, and a connection that
could not be made or was lost before an answer is `Unavailable`.

`DisposeAsync` refuses new calls, cancels the calls in flight, waits for them and closes the
channel. It is idempotent, safe to call concurrently, and never throws: a failure to close
is logged as a warning.

## Values

`Value` is an immutable value the service evaluates, or one sent to it: one nested sealed
class per arm of the schema's `Value`, `IntegerValue` to `InfinityValue`, which `Kind`, a
`ValueKind`, names. No type outside the library can derive from it. A switch over the arms
still needs a default case, since a release adds an arm whenever the service does. The typed
operations that answer or take values build on these types; no public method returns or
takes one yet.

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
its grpc-go-only `grpc` transport, reached over gRPC, where Connect must be refused. Unit tests
of behavior every protocol shares run over all three wires too. They skip without a binary;
set `OPENSYSML_REQUIRE_SERVICE=1` to turn that skip into a failure, as CI does. `make
dotnet-test` also runs the Vehicle sample against such a service, under the same rule:

```bash
OPENSYSML_GRPC_BINARY="$PWD/bin/sysml-grpc" OPENSYSML_REQUIRE_SERVICE=1 make dotnet-test
```

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
