using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OpenMBEE.OpenSysML.Tests;

/// <summary>
/// What a connection reports when the endpoint is not a gRPC service: real sockets on the loopback
/// interface, through the handler a connection makes for itself.
/// </summary>
[Trait(Category.Name, Category.Unit)]
public sealed class NetworkFailureTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ARefusedPortIsATransportUnavailableCausedByTheRefusal()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var error = await Assert.ThrowsAsync<TransportException>(() => OpenAsync($"127.0.0.1:{port}"));

        Assert.Equal(StatusCode.Unavailable, error.StatusCode);
        Assert.Equal("sysml.SysMLService/GetServerInfo", error.Method);
        Assert.Contains(Chain(error), cause => cause is SocketException { SocketErrorCode: SocketError.ConnectionRefused });
    }

    [Fact]
    public async Task AnUnresolvableHostIsATransportUnavailableCausedByTheLookup()
    {
        var error = await Assert.ThrowsAsync<TransportException>(() => OpenAsync("no-such-host.invalid:50051"));

        Assert.Equal(StatusCode.Unavailable, error.StatusCode);
        Assert.Contains(Chain(error), cause => cause is SocketException);
    }

    [Fact]
    public async Task AnEndpointThatSpeaksOnlyHttp11IsATransportInternalThatSaysSo()
    {
        await using var server = RawTcpServer.Start("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());

        var error = await Assert.ThrowsAsync<TransportException>(() => OpenAsync($"127.0.0.1:{server.Port}"));

        Assert.Equal(StatusCode.Internal, error.StatusCode);
        Assert.Contains(Chain(error), cause => cause is HttpProtocolException);
        Assert.Contains("did not answer as an HTTP/2 gRPC endpoint", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEndpointThatDoesNotSpeakTlsFailsAnHttpsConnection()
    {
        await using var server = RawTcpServer.Start("this is not TLS\r\n"u8.ToArray());

        var error = await Assert.ThrowsAsync<TransportException>(() => OpenAsync($"https://127.0.0.1:{server.Port}"));

        Assert.Equal(StatusCode.Unavailable, error.StatusCode);
        Assert.Contains(Chain(error), cause => cause is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError });
    }

    private static async Task<Connection> OpenAsync(string address)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Timeout);
        return await Connection.OpenAsync(new ConnectionOptions { Address = address, RequestTimeout = Timeout }, timeout.Token);
    }

    private static List<Exception> Chain(Exception error)
    {
        var chain = new List<Exception>();
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
        {
            chain.Add(cause);
        }

        return chain;
    }
}

/// <summary>A TCP server on the loopback interface that answers every connection with fixed bytes and closes it.</summary>
internal sealed class RawTcpServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task accepting;

    private RawTcpServer(TcpListener listener, byte[] answer)
    {
        this.listener = listener;
        accepting = AcceptAsync(answer);
    }

    /// <summary>The port it listens on.</summary>
    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>How many connections it accepted.</summary>
    internal int Connections { get; private set; }

    /// <summary>Starts a server answering with these bytes.</summary>
    internal static RawTcpServer Start(byte[] answer)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new RawTcpServer(listener, answer);
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        listener.Stop();
        try
        {
            await accepting;
        }
        catch (OperationCanceledException)
        {
        }

        stopping.Dispose();
    }

    private async Task AcceptAsync(byte[] answer)
    {
        while (!stopping.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptSocketAsync(stopping.Token);
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            Connections++;
            using (client)
            {
                try
                {
                    var buffer = new byte[4096];
                    using var read = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    read.CancelAfter(TimeSpan.FromSeconds(2));
                    await client.ReceiveAsync(buffer, SocketFlags.None, read.Token);
                    await client.SendAsync(answer, SocketFlags.None, stopping.Token);
                    client.Shutdown(SocketShutdown.Both);
                }
                catch (Exception error) when (error is OperationCanceledException or SocketException)
                {
                }
            }
        }
    }

    /// <summary>The bytes as text, for a test's message.</summary>
    public override string ToString() => $"{nameof(RawTcpServer)} on {Port}";
}
