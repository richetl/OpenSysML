using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using OpenMBEE.OpenSysML.Internal;
using Xunit;

namespace OpenMBEE.OpenSysML.Tests;

[Trait(Category.Name, Category.Unit)]
public sealed class GrpcTransportTests
{
    private const string GetSymbol = "sysml.SysMLService/GetSymbol";
    private static readonly Uri Address = new("http://fake.invalid:50051/");

    public static TheoryData<StatusCode> RefusalStatuses => [.. Enum.GetValues<StatusCode>().Where(code => code != StatusCode.Ok)];

    public static TheoryData<string, Func<HttpResponseMessage>, StatusCode> MalformedResponses => new()
    {
        { "truncated frame", () => FakeGrpcHandler.Raw([0, 0, 0, 0, 10, 1, 2], status: 0), StatusCode.Internal },
        { "truncated prefix", () => FakeGrpcHandler.Raw([0, 0, 0], status: 0), StatusCode.Internal },
        { "invalid protobuf", () => FakeGrpcHandler.Framed([0xFF, 0xFF, 0xFF], status: 0), StatusCode.Internal },
        { "no message", () => FakeGrpcHandler.Raw([], status: 0), StatusCode.Internal },
        { "two messages", () => FakeGrpcHandler.Raw([.. FakeGrpcHandler.Frame([]), .. FakeGrpcHandler.Frame([])], status: 0), StatusCode.Internal },
        { "no grpc-status", () => FakeGrpcHandler.Framed([], status: null), StatusCode.Cancelled },
        { "non-numeric grpc-status", () => FakeGrpcHandler.TrailersOnly("five", "x"), StatusCode.Internal },
        { "compressed without an encoding", () => FakeGrpcHandler.Framed([1], status: 0, compressed: true), StatusCode.Internal },
        { "html content", () => FakeGrpcHandler.Raw("<html>bad gateway</html>"u8.ToArray(), status: null, contentType: "text/html", code: HttpStatusCode.BadGateway), StatusCode.Unavailable },
        { "wrong content type", () => FakeGrpcHandler.Raw(FakeGrpcHandler.Frame([]), status: 0, contentType: "application/json"), StatusCode.Cancelled },
        { "http 404", () => FakeGrpcHandler.Raw([], status: null, contentType: "text/plain", code: HttpStatusCode.NotFound), StatusCode.Unimplemented },
        { "http 503", () => FakeGrpcHandler.Raw([], status: null, contentType: "text/plain", code: HttpStatusCode.ServiceUnavailable), StatusCode.Unavailable },
    };

    [Theory]
    [MemberData(nameof(RefusalStatuses))]
    public async Task EveryStatusTheServiceSendsIsAServiceExceptionButADeadline(StatusCode status)
    {
        using var handler = new FakeGrpcHandler();
        await using var connection = await FakeConnection.OpenAsync(handler);
        handler.Respond = (_, _) => Task.FromResult(FakeGrpcHandler.TrailersOnly((int)status, "said the service"));

        var error = await Assert.ThrowsAnyAsync<OpenSysMLException>(() => connection.GetSymbolAsync());

        if (status == StatusCode.DeadlineExceeded)
        {
            var transport = Assert.IsType<TransportException>(error);
            Assert.Equal(StatusCode.DeadlineExceeded, transport.StatusCode);
            Assert.Contains("reports that the call outlived its deadline", transport.Message, StringComparison.Ordinal);
        }
        else
        {
            var service = Assert.IsType<ServiceException>(error);
            Assert.Equal(status, service.StatusCode);
            Assert.Equal("said the service", service.ServiceMessage);
            Assert.Equal(GetSymbol, service.Method);
        }
    }

    [Fact]
    public async Task AStatusInTheTrailersAfterAMessageIsTheServices()
    {
        using var handler = new FakeGrpcHandler();
        await using var connection = await FakeConnection.OpenAsync(handler);
        handler.Respond = (_, _) => Task.FromResult(FakeGrpcHandler.Framed([], status: 5));

        var error = await Assert.ThrowsAsync<ServiceException>(() => connection.GetSymbolAsync());

        Assert.Equal(StatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task AStatusThisClientDoesNotKnowIsUnknown()
    {
        using var handler = new FakeGrpcHandler();
        await using var connection = await FakeConnection.OpenAsync(handler);
        handler.Respond = (_, _) => Task.FromResult(FakeGrpcHandler.TrailersOnly(99, "from the future"));

        var error = await Assert.ThrowsAsync<ServiceException>(() => connection.GetSymbolAsync());

        Assert.Equal(StatusCode.Unknown, error.StatusCode);
        Assert.Equal("from the future", error.ServiceMessage);
    }

    [Fact]
    public async Task UnavailableFromTheServiceIsNotUnavailableFromTheNetwork()
    {
        using var handler = new FakeGrpcHandler();
        await using var connection = await FakeConnection.OpenAsync(handler);

        handler.Respond = (_, _) => Task.FromResult(FakeGrpcHandler.TrailersOnly(14, "draining"));
        var service = await Assert.ThrowsAsync<ServiceException>(() => connection.GetSymbolAsync());

        handler.Respond = (_, _) => throw new HttpRequestException(
            HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused));
        var network = await Assert.ThrowsAsync<TransportException>(() => connection.GetSymbolAsync());

        Assert.Equal(StatusCode.Unavailable, service.StatusCode);
        Assert.Equal(StatusCode.Unavailable, network.StatusCode);
        Assert.Equal(SocketError.ConnectionRefused, Assert.IsType<SocketException>(Assert.IsType<HttpRequestException>(network.InnerException).InnerException).SocketErrorCode);
        Assert.Contains("Whether the service received the call is unknown", network.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MalformedResponses))]
    public async Task AMalformedResponseIsNeverASuccessOrTheServices(string shape, Func<HttpResponseMessage> response, StatusCode expected)
    {
        using var handler = new FakeGrpcHandler();
        await using var connection = await FakeConnection.OpenAsync(handler);
        handler.Respond = (_, _) => Task.FromResult(response());

        var error = await Assert.ThrowsAsync<TransportException>(() => connection.GetSymbolAsync());

        Assert.True(expected == error.StatusCode, $"{shape}: {error.StatusCode}, {error.Message}");
        Assert.Equal(GetSymbol, error.Method);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public async Task AResponseAtTheLimitIsReadAndOneByteMoreIsNot()
    {
        const int Limit = 1024;
        using var handler = new FakeGrpcHandler();
        await using var connection = await FakeConnection.OpenAsync(handler, FakeConnection.Options(handler, maxResponseSize: Limit));

        handler.Respond = (_, _) => Task.FromResult(FakeGrpcHandler.Message(SymbolResponseOfSize(Limit)));
        var read = await connection.GetSymbolAsync();
        Assert.Equal(Limit, read.CalculateSize());

        handler.Respond = (_, _) => Task.FromResult(FakeGrpcHandler.Message(SymbolResponseOfSize(Limit + 1)));
        var error = await Assert.ThrowsAsync<TransportException>(() => connection.GetSymbolAsync());
        Assert.Equal(StatusCode.ResourceExhausted, error.StatusCode);
    }

    [Fact]
    public async Task ARequestAtTheLimitIsSentAndOneByteMoreIsNot()
    {
        const int Limit = 512;
        using var handler = new FakeGrpcHandler { Respond = (_, _) => Task.FromResult(FakeGrpcHandler.Message(new Wire.SymbolResponse())) };
        await using var connection = await FakeConnection.OpenAsync(handler, FakeConnection.Options(handler, maxRequestSize: Limit));

        await connection.CallAsync<Wire.GetSymbolRequest, Wire.SymbolResponse>("GetSymbol", RequestOfSize(Limit), TestContext.Current.CancellationToken);
        Assert.Equal(Limit, handler.RequestsFor("GetSymbol").Single().Message(Wire.GetSymbolRequest.Parser).CalculateSize());

        var error = await Assert.ThrowsAsync<TransportException>(() =>
            connection.CallAsync<Wire.GetSymbolRequest, Wire.SymbolResponse>("GetSymbol", RequestOfSize(Limit + 1), TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.ResourceExhausted, error.StatusCode);
        Assert.Single(handler.RequestsFor("GetSymbol"));
    }

    [Fact]
    public async Task ACallIsAUnaryGrpcPostWithTheHeadersTheDeadlineAndIdentityEncodingOnly()
    {
        using var handler = new FakeGrpcHandler { Respond = (_, _) => Task.FromResult(FakeGrpcHandler.Message(new Wire.SymbolResponse())) };
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer abc", ["X-Tenant"] = "t1" };
        await using var connection = await FakeConnection.OpenAsync(handler, FakeConnection.Options(handler, headers: headers, requestTimeout: TimeSpan.FromSeconds(30)));

        await connection.GetSymbolAsync();

        var sent = handler.RequestsFor("GetSymbol").Single();
        Assert.Equal("/sysml.SysMLService/GetSymbol", sent.Uri.AbsolutePath);
        Assert.Equal("application/grpc", sent.ContentType);
        Assert.Equal("Bearer abc", sent.Headers["authorization"]);
        Assert.Equal("t1", sent.Headers["x-tenant"]);
        Assert.Equal(["identity"], sent.Headers["grpc-accept-encoding"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.False(sent.Headers.ContainsKey("grpc-encoding"));
        Assert.Matches("^[0-9]+[HMSmun]$", sent.Headers["grpc-timeout"]);
        Assert.Equal(new Wire.GetSymbolRequest { ModelHash = "m", SymbolId = "A" }, sent.Message(Wire.GetSymbolRequest.Parser));
        Assert.All(handler.Requests, request => Assert.Equal("Bearer abc", request.Headers["authorization"]));
    }

    [Fact]
    public void AnInternalFailureCausedByAnHttp2ProtocolErrorNamesAnHttp11Endpoint()
    {
        var cause = new HttpRequestException(
            HttpRequestError.HttpProtocolError,
            "The HTTP/2 server sent invalid data on the connection.",
            new HttpProtocolException(1, "PROTOCOL_ERROR", null));
        var error = new RpcException(new Status(Grpc.Core.StatusCode.Internal, "Error starting gRPC call.", cause));

        var mapped = Assert.IsType<TransportException>(GrpcTransport.Map(error, serverStatus: null, GetSymbol, Address, CancellationToken.None));

        Assert.Equal(StatusCode.Internal, mapped.StatusCode);
        Assert.Same(cause, mapped.InnerException);
        Assert.Contains("did not answer as an HTTP/2 gRPC endpoint (HTTP/2 protocol error 1)", mapped.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP/1.1", mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnavailableConnectionResetIsNotBlamedOnHttp11()
    {
        var cause = new HttpRequestException(HttpRequestError.Unknown, "reset", new SocketException((int)SocketError.ConnectionReset));
        var error = new RpcException(new Status(Grpc.Core.StatusCode.Unavailable, "Error starting gRPC call.", cause));

        var mapped = Assert.IsType<TransportException>(GrpcTransport.Map(error, serverStatus: null, GetSymbol, Address, CancellationToken.None));

        Assert.Equal(StatusCode.Unavailable, mapped.StatusCode);
        Assert.DoesNotContain("HTTP/1.1", mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStatusWithALocalCauseIsNeverTheServicesEvenWhenTheResponseCarriedIt()
    {
        var error = new RpcException(new Status(Grpc.Core.StatusCode.Internal, "decode", new InvalidOperationException("local")));

        Assert.IsType<TransportException>(GrpcTransport.Map(error, serverStatus: 13, GetSymbol, Address, CancellationToken.None));
        Assert.IsType<ServiceException>(GrpcTransport.Map(new RpcException(new Status(Grpc.Core.StatusCode.Internal, "x")), 13, GetSymbol, Address, CancellationToken.None));
        Assert.IsType<TransportException>(GrpcTransport.Map(new RpcException(new Status(Grpc.Core.StatusCode.Internal, "x")), 0, GetSymbol, Address, CancellationToken.None));
    }

    [Fact]
    public async Task DisposingTheTransportDisposesAnOwnedHandlerAndOnlyThat()
    {
        var owned = new FakeGrpcHandler();
        var borrowed = new FakeGrpcHandler();
        using (borrowed)
        {
            var options = new ConnectionOptions();

            await new GrpcTransport(Address, owned, ownsHandler: true, options).DisposeAsync();
            var transport = new GrpcTransport(Address, borrowed, ownsHandler: false, options);
            await transport.DisposeAsync();
            await transport.DisposeAsync();

            Assert.Equal(1, owned.Disposals);
            Assert.Equal(0, borrowed.Disposals);
        }
    }

    private static Wire.SymbolResponse SymbolResponseOfSize(int size)
    {
        var response = new Wire.SymbolResponse { Error = string.Empty };
        var overhead = new Wire.SymbolResponse { Error = "x" }.CalculateSize() - 1;
        var length = size - overhead;
        while (new Wire.SymbolResponse { Error = new string('x', length) }.CalculateSize() > size)
        {
            length--;
        }

        response.Error = new string('x', length);
        Assert.Equal(size, response.CalculateSize());
        return response;
    }

    private static Wire.GetSymbolRequest RequestOfSize(int size)
    {
        var length = size - 3;
        while (new Wire.GetSymbolRequest { SymbolId = new string('a', length) }.CalculateSize() > size)
        {
            length--;
        }

        var request = new Wire.GetSymbolRequest { SymbolId = new string('a', length) };
        Assert.Equal(size, request.CalculateSize());
        return request;
    }
}
