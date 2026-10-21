# OpenSysML .NET client

`OpenMBEE.OpenSysML` is the C# client for the `sysml-grpc` service, built from a checkout
of this repository and consumed by `ProjectReference`. It is not published to NuGet.

> **Status:** this directory holds the client's build, test and coverage gates and
> nothing callable yet: the assembly has no public API. Connecting to a service,
> loading models and the other service operations are not implemented. The other clients
> ([Python](../python/), [Java](../java/), [Rust](../rust/), [Node](../node/)) are the
> ones to use today.

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

[`samples/Vehicle`](samples/Vehicle/) is such a consumer. For now it reads the referenced
assembly's version and target framework from its metadata:

```bash
dotnet run --project client/dotnet/samples/Vehicle -c Release
# OpenMBEE.OpenSysML 0.10.1 (.NETCoreApp,Version=v10.0)
```

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

Integration tests run against the binary `OPENSYSML_GRPC_BINARY` names and skip without
one. Set `OPENSYSML_REQUIRE_SERVICE=1` to turn that skip into a failure, as CI does:

```bash
OPENSYSML_GRPC_BINARY="$PWD/bin/sysml-grpc" OPENSYSML_REQUIRE_SERVICE=1 make dotnet-test
```

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
