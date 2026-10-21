using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OpenMBEE.OpenSysML.Tests;

/// <summary>
/// A connection to a real sysml-grpc named by its address, over gRPC, against both of the service's
/// transports: connect, which serves gRPC beside Connect on one port, and grpc, which is grpc-go alone.
/// </summary>
[Trait(Category.Name, Category.Integration)]
public sealed class ServiceConnectionTests(ServiceFixture services) : IClassFixture<ServiceFixture>
{
    public static TheoryData<string> Transports => ["connect", "grpc"];

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task OpensAnExternalServiceAndReportsWhatItIs(string transport)
    {
        var service = await services.ServiceAsync(transport);

        await using var connection = await Connection.OpenAsync(new ConnectionOptions { Address = service.Address }, TestContext.Current.CancellationToken);

        Assert.Equal(new Uri($"http://{service.Address}/"), connection.Address);
        Assert.True(connection.ServerInfo.Answered);
        Assert.Equal(services.Version, connection.ServerInfo.Version);
        Assert.NotEmpty(connection.Capabilities.Names);
        Assert.All(ServerInfoScenarioCapabilities(), capability => Assert.True(connection.Capabilities.Has(capability), capability));
        Assert.Empty(connection.Capabilities.Names.Except(ServiceCapabilities.Names));
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task TheServiceCanBeNamedByTheEnvironment(string transport)
    {
        var service = await services.ServiceAsync(transport);

        await using var connection = await Connection.OpenAsync(
            null,
            name => name == ConnectionOptions.ServiceVariable ? service.Address : null,
            TestContext.Current.CancellationToken);

        Assert.Equal(services.Version, connection.ServerInfo.Version);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task ARawCallReachesTheServiceAndItsRefusalIsAServiceException(string transport)
    {
        var service = await services.ServiceAsync(transport);
        await using var connection = await Connection.OpenAsync(new ConnectionOptions { Address = service.Address }, TestContext.Current.CancellationToken);

        var info = await connection.CallAsync<Wire.ServerInfoRequest, Wire.ServerInfoResponse>(
            "sysml.SysMLService/GetServerInfo", new Wire.ServerInfoRequest(), TestContext.Current.CancellationToken);
        var refused = await Assert.ThrowsAsync<ServiceException>(() => connection.CallAsync<Wire.GetSymbolRequest, Wire.SymbolResponse>(
            "GetSymbol", new Wire.GetSymbolRequest { ModelHash = "no-such-model", SymbolId = "A" }, TestContext.Current.CancellationToken));

        Assert.Equal(connection.ServerInfo.Version, info.Version);
        Assert.Equal(connection.Capabilities, new Capabilities(info.Capabilities));
        Assert.Equal(StatusCode.NotFound, refused.StatusCode);
        Assert.Equal("sysml.SysMLService/GetSymbol", refused.Method);
        Assert.NotEmpty(refused.ServiceMessage);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task ConcurrentCallsShareOneConnection(string transport)
    {
        var service = await services.ServiceAsync(transport);
        await using var connection = await Connection.OpenAsync(new ConnectionOptions { Address = service.Address }, TestContext.Current.CancellationToken);

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => connection.CallAsync<Wire.ServerInfoRequest, Wire.ServerInfoResponse>(
            "GetServerInfo", new Wire.ServerInfoRequest(), TestContext.Current.CancellationToken)));

        Assert.All(answers, answer => Assert.Equal(connection.ServerInfo.Version, answer.Version));
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task RequiredCapabilitiesAndVersionAreCheckedAgainstTheServicesAnswer(string transport)
    {
        var service = await services.ServiceAsync(transport);
        var token = TestContext.Current.CancellationToken;

        await using (await Connection.OpenAsync(
            new ConnectionOptions { Address = service.Address, Version = services.Version, RequireCapabilities = [Capabilities.Query, Capabilities.TypeFacts] },
            token))
        {
        }

        var missing = await Assert.ThrowsAsync<CapabilityException>(() =>
            Connection.OpenAsync(new ConnectionOptions { Address = service.Address, RequireCapabilities = ["no_such_capability"] }, token));
        var stale = await Assert.ThrowsAsync<StaleServiceException>(() =>
            Connection.OpenAsync(new ConnectionOptions { Address = service.Address, Version = "v0.0.0-not-this-one" }, token));

        Assert.Equal("no_such_capability", missing.Capability);
        Assert.Equal(services.Version, stale.ServiceVersion);
        Assert.False(service.HasExited);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task AResponseOverTheLimitIsRefusedHoweverTheServiceCouldCompressIt(string transport)
    {
        var service = await services.ServiceAsync(transport);
        var token = TestContext.Current.CancellationToken;
        int size;
        await using (var measured = await Connection.OpenAsync(new ConnectionOptions { Address = service.Address }, token))
        {
            var answer = new Wire.ServerInfoResponse { Version = measured.ServerInfo.Version };
            answer.Capabilities.AddRange(measured.Capabilities.Names);
            size = answer.CalculateSize();
        }

        await using (await Connection.OpenAsync(new ConnectionOptions { Address = service.Address, MaxResponseSize = size }, token))
        {
        }

        // Compressed, the answer would fit a byte below its size; the limit must bound what it inflates to.
        var error = await Assert.ThrowsAsync<TransportException>(() =>
            Connection.OpenAsync(new ConnectionOptions { Address = service.Address, MaxResponseSize = size - 1 }, token));

        Assert.Equal(StatusCode.ResourceExhausted, error.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task ClosingConnectionsNeverStopsAnExternalService(string transport)
    {
        var binary = ServiceGate.FromProcess.RequireBinary();
        var token = TestContext.Current.CancellationToken;
        await using var service = await ServiceProcess.StartAsync(binary, transport, token);
        var options = new ConnectionOptions { Address = service.Address };

        var first = await Connection.OpenAsync(options, token);
        var second = await Connection.OpenAsync(options, token);
        await first.DisposeAsync();
        await second.DisposeAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(200), token);

        Assert.False(service.HasExited, service.ToString());
        await using var third = await Connection.OpenAsync(options, token);
        Assert.True(third.ServerInfo.Answered);
    }

    private static IEnumerable<string> ServerInfoScenarioCapabilities()
    {
        using var file = JsonDocument.Parse(System.IO.File.ReadAllBytes(Repository.PathOf("conformance", "scenarios", "01-server-info.json")));
        var scenario = file.RootElement.GetProperty("scenarios").EnumerateArray()
            .Single(scenario => scenario.GetProperty("id").GetString() == "server_info/reports_the_capabilities_clients_negotiate_on");
        return [.. scenario.GetProperty("expect").GetProperty("contains_all").GetProperty("capabilities").EnumerateArray().Select(name => name.GetString()!)];
    }
}

/// <summary>One sysml-grpc per transport, started when a test first needs it and stopped after the class.</summary>
public sealed class ServiceFixture : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<ServiceProcess>>> services = new(StringComparer.Ordinal);
    private readonly Lazy<string> version = new(ReadVersion);

    /// <summary>The version the configured binary reports.</summary>
    internal string Version => version.Value;

    /// <summary>The service serving a transport; skips or fails the test without a binary.</summary>
    internal Task<ServiceProcess> ServiceAsync(string transport)
    {
        var binary = ServiceGate.FromProcess.RequireBinary();
        return services.GetOrAdd(
            transport,
            name => new Lazy<Task<ServiceProcess>>(() => ServiceProcess.StartAsync(binary, name, CancellationToken.None))).Value;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var service in services.Values.Where(service => service.IsValueCreated))
        {
            if (service.Value.IsCompletedSuccessfully)
            {
                await (await service.Value).DisposeAsync();
            }
        }
    }

    private static string ReadVersion()
    {
        var binary = ServiceGate.FromProcess.RequireBinary();
        var start = new ProcessStartInfo(binary, "-version") { RedirectStandardOutput = true, UseShellExecute = false };
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{binary} did not start.");
        var first = process.StandardOutput.ReadLine() ?? string.Empty;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        const string Prefix = "sysml-grpc version ";
        Assert.StartsWith(Prefix, first, StringComparison.Ordinal);
        return first[Prefix.Length..].Trim();
    }
}
