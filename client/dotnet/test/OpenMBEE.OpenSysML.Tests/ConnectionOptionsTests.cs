using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using OpenMBEE.OpenSysML.Internal;
using Xunit;

namespace OpenMBEE.OpenSysML.Tests;

[Trait(Category.Name, Category.Unit)]
public sealed class ConnectionOptionsTests
{
    public static TheoryData<string, Func<System.Net.Http.HttpMessageHandler, ConnectionOptions>> InvalidOptions => new()
    {
        { "RequestTimeout", handler => FakeConnection.Options(handler, requestTimeout: TimeSpan.Zero) },
        { "RequestTimeout", handler => FakeConnection.Options(handler, requestTimeout: TimeSpan.FromSeconds(-1)) },
        { "RequestTimeout", handler => FakeConnection.Options(handler, requestTimeout: ConnectionOptions.MaxRequestTimeout + TimeSpan.FromMilliseconds(1)) },
        { "RequestTimeout", handler => FakeConnection.Options(handler, requestTimeout: System.Threading.Timeout.InfiniteTimeSpan) },
        { "MaxRequestSize", handler => FakeConnection.Options(handler, maxRequestSize: 0) },
        { "MaxResponseSize", handler => FakeConnection.Options(handler, maxResponseSize: -1) },
        { "Version", handler => FakeConnection.Options(handler, version: " ") },
        { "RequireCapabilities", handler => FakeConnection.Options(handler, requireCapabilities: ["query", ""]) },
        { "Protocol", handler => new ConnectionOptions { Address = FakeGrpcHandler.Address, HttpMessageHandler = handler, Protocol = (Protocol)7 } },
        { "Address", handler => new ConnectionOptions { Address = "ftp://host:1", HttpMessageHandler = handler } },
        { "Header", handler => FakeConnection.Options(handler, headers: new Dictionary<string, string> { ["Content-Type"] = "text/plain" }) },
    };

    [Fact]
    public void DefaultsAreGrpcAutoStartAndTheDocumentedLimits()
    {
        var options = new ConnectionOptions();

        Assert.Null(options.Address);
        Assert.Equal(Protocol.Grpc, options.Protocol);
        Assert.True(options.AutoStart);
        Assert.Equal(TimeSpan.FromSeconds(60), options.RequestTimeout);
        Assert.Equal(512 * 1024 * 1024, options.MaxRequestSize);
        Assert.Equal(512 * 1024 * 1024, options.MaxResponseSize);
        Assert.Empty(options.Headers);
        Assert.Empty(options.RequireCapabilities);
        Assert.Null(options.Version);
        Assert.Same(NullLogger.Instance, options.Logger);
        Assert.Null(options.HttpMessageHandler);
        Assert.Equal("OPENSYSML_SERVICE", ConnectionOptions.ServiceVariable);
        Assert.Equal(options.RequestTimeout, ConnectionOptions.Default.RequestTimeout);
        Assert.Equal(options.MaxResponseSize, ConnectionOptions.Default.MaxResponseSize);
        Assert.True(ConnectionOptions.Default.AutoStart);
    }

    [Fact]
    public void CollectionsAreCopiedWhenSet()
    {
        var headers = new Dictionary<string, string> { ["authorization"] = "Bearer a" };
        var required = new List<string> { "query", "convert", "query" };
        var options = new ConnectionOptions { Headers = headers, RequireCapabilities = required };
        headers["authorization"] = "Bearer b";
        headers["x-other"] = "c";
        required.Add("migrate");

        Assert.Equal("Bearer a", Assert.Single(options.Headers).Value);
        Assert.Equal(["convert", "query"], options.RequireCapabilities);
        Assert.IsNotType<Dictionary<string, string>>(options.Headers);
    }

    [Fact]
    public void NullCollectionsAndMembersAreRefusedWhenSet()
    {
        Assert.Throws<ArgumentNullException>(() => new ConnectionOptions { Headers = null! });
        Assert.Throws<ArgumentException>(() => new ConnectionOptions { Headers = new Dictionary<string, string> { ["a"] = null! } });
        Assert.Throws<ArgumentNullException>(() => new ConnectionOptions { RequireCapabilities = null! });
        Assert.Throws<ArgumentException>(() => new ConnectionOptions { RequireCapabilities = ["query", null!] });
        Assert.Throws<ArgumentNullException>(() => new ConnectionOptions { Logger = null! });
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public async Task AnInvalidOptionFailsTheOpenBeforeAnythingIsSent(string option, Func<System.Net.Http.HttpMessageHandler, ConnectionOptions> options)
    {
        using var handler = new FakeGrpcHandler();

        var error = await Assert.ThrowsAsync<InvalidRequestException>(() => FakeConnection.OpenAsync(handler, options(handler)));

        Assert.Contains(option, error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, handler.Disposals);
    }

    [Fact]
    public async Task NullOptionsAreTheDefaults()
    {
        var error = await Assert.ThrowsAsync<ServiceStartException>(() => Connection.OpenAsync(null, _ => null, TestContext.Current.CancellationToken));

        Assert.Contains("cannot start a private one yet", error.Message, StringComparison.Ordinal);
    }
}

[Trait(Category.Name, Category.Unit)]
public sealed class RequestHeadersTests
{
    [Theory]
    [InlineData("content-type")]
    [InlineData("Content-Type")]
    [InlineData("CONTENT-ENCODING")]
    [InlineData("accept-encoding")]
    [InlineData("Host")]
    [InlineData("te")]
    [InlineData("user-agent")]
    [InlineData("transfer-encoding")]
    [InlineData("grpc-timeout")]
    [InlineData("Grpc-Status")]
    [InlineData("grpc-message")]
    [InlineData("grpc-accept-encoding")]
    [InlineData("connect-timeout-ms")]
    [InlineData("Connect-Protocol-Version")]
    public void AHeaderTheProtocolControlsIsRefused(string name)
    {
        var error = Assert.Throws<InvalidRequestException>(() => RequestHeaders.Validate(new Dictionary<string, string> { [name] = "x" }));

        Assert.Contains("controlled by the protocol", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(":authority")]
    [InlineData("x header")]
    [InlineData("x/header")]
    [InlineData("é")]
    public void AnInvalidNameIsRefused(string name)
    {
        Assert.Throws<InvalidRequestException>(() => RequestHeaders.Validate(new Dictionary<string, string> { [name] = "x" }));
    }

    [Theory]
    [InlineData("a\r\nInjected: yes")]
    [InlineData("a\nb")]
    [InlineData("caf\u00e9")]
    [InlineData("tab\there")]
    [InlineData("\u007f")]
    public void AValueThatIsNotPrintableAsciiIsRefused(string value)
    {
        var error = Assert.Throws<InvalidRequestException>(() => RequestHeaders.Validate(new Dictionary<string, string> { ["x-custom"] = value }));

        Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BinaryMetadataAndCaseInsensitiveRepeatsAreRefused()
    {
        Assert.Throws<InvalidRequestException>(() => RequestHeaders.Validate(new Dictionary<string, string> { ["x-trace-bin"] = "x" }));
        Assert.Throws<InvalidRequestException>(() => RequestHeaders.Validate(new Dictionary<string, string> { ["X-Custom"] = "a", ["x-custom"] = "b" }));
    }

    [Fact]
    public void AuthorizationAndCustomMetadataAreAccepted()
    {
        RequestHeaders.Validate(new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer abc.def-ghi",
            ["x-request-id"] = "42",
            ["tenant_id"] = "a b ~",
            ["x.empty"] = string.Empty,
        });
    }
}

[Trait(Category.Name, Category.Unit)]
public sealed class ServiceAddressTests
{
    [Theory]
    [InlineData("localhost:50051", "http://localhost:50051/")]
    [InlineData(" 127.0.0.1:1 ", "http://127.0.0.1:1/")]
    [InlineData("[::1]:50051", "http://[::1]:50051/")]
    [InlineData("http://example.com:8080", "http://example.com:8080/")]
    [InlineData("http://example.com:8080/", "http://example.com:8080/")]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("HTTPS://Example.com:443", "https://example.com/")]
    [InlineData("http://[::1]:9", "http://[::1]:9/")]
    public void AnAddressIsHostPortOrARootUrl(string text, string expected)
    {
        Assert.Equal(new Uri(expected), ServiceAddress.Parse(text, "ConnectionOptions.Address"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]
    [InlineData("localhost:")]
    [InlineData("localhost:port")]
    [InlineData("localhost:99999")]
    [InlineData("::1:50051")]
    [InlineData("[::1]")]
    [InlineData("user@host:1")]
    [InlineData("host:1/path")]
    [InlineData("ftp://host:1")]
    [InlineData("grpc://host:1")]
    [InlineData("http://user:secret@host:1")]
    [InlineData("http://host:1/prefix")]
    [InlineData("http://host:1/?q=1")]
    [InlineData("http://host:1/?")]
    [InlineData("http://host:1#fragment")]
    [InlineData("http://")]
    [InlineData("http:///path")]
    public void AnythingElseIsRefusedNamingWhereItCameFrom(string text)
    {
        var error = Assert.Throws<InvalidRequestException>(() => ServiceAddress.Parse(text, "$OPENSYSML_SERVICE"));

        Assert.StartsWith("$OPENSYSML_SERVICE is", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOptionWinsOverTheEnvironmentWhichWinsOverStarting()
    {
        var named = new ConnectionOptions { Address = "option:1" };
        Assert.Equal(new Uri("http://option:1/"), Connection.ResolveAddress(named, _ => "environment:2"));
        Assert.Equal(new Uri("http://environment:2/"), Connection.ResolveAddress(new ConnectionOptions(), Environment(" environment:2 ")));
        Assert.Throws<InvalidRequestException>(() => Connection.ResolveAddress(new ConnectionOptions { Address = string.Empty }, _ => "environment:2"));

        var invalid = Assert.Throws<InvalidRequestException>(() => Connection.ResolveAddress(new ConnectionOptions(), Environment("nope")));
        Assert.Contains("$OPENSYSML_SERVICE", invalid.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoServiceNamedIsAStartFailureThatSaysWhy(string? environment)
    {
        var starting = Assert.Throws<ServiceStartException>(() => Connection.ResolveAddress(new ConnectionOptions(), Environment(environment)));
        Assert.Contains("cannot start a private one yet", starting.Message, StringComparison.Ordinal);
        Assert.Contains("ConnectionOptions.Address or $OPENSYSML_SERVICE", starting.Message, StringComparison.Ordinal);

        var off = Assert.Throws<ServiceStartException>(() => Connection.ResolveAddress(new ConnectionOptions { AutoStart = false }, Environment(environment)));
        Assert.Contains("AutoStart is false", off.Message, StringComparison.Ordinal);
    }

    private static Func<string, string?> Environment(string? service) =>
        name => name == ConnectionOptions.ServiceVariable ? service : null;
}
