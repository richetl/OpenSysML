using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;

namespace OpenMBEE.OpenSysML.Tests;

/// <summary>
/// An HTTP handler standing in for a gRPC service: it records every request a channel sends and
/// answers GetServerInfo itself, and every other method through <see cref="Respond"/>. It frames
/// the responses it is told to by hand, so a test controls every byte, header and trailer a
/// client reads.
/// </summary>
internal sealed class FakeGrpcHandler : HttpMessageHandler
{
    /// <summary>The address a connection over this handler is opened at.</summary>
    internal const string Address = "http://fake.invalid:50051";

    private readonly ConcurrentQueue<SentRequest> requests = new();
    private int disposals;

    /// <summary>The version GetServerInfo reports.</summary>
    internal string Version { get; init; } = "v9.9.9";

    /// <summary>The capabilities GetServerInfo reports.</summary>
    internal IReadOnlyList<string> Capabilities { get; init; } = [OpenSysML.Capabilities.Query, OpenSysML.Capabilities.Convert];

    /// <summary>Answers GetServerInfo instead of the default answer.</summary>
    internal Func<SentRequest, CancellationToken, Task<HttpResponseMessage>>? ServerInfo { get; init; }

    /// <summary>Answers every method but GetServerInfo.</summary>
    internal Func<SentRequest, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(TrailersOnly(12, "not faked"));

    /// <summary>Every request sent, in order.</summary>
    internal IReadOnlyList<SentRequest> Requests => [.. requests];

    /// <summary>The requests sent for a method, by bare name.</summary>
    internal IReadOnlyList<SentRequest> RequestsFor(string method) => [.. requests.Where(request => request.Method == method)];

    /// <summary>How many times the handler was disposed.</summary>
    internal int Disposals => Volatile.Read(ref disposals);

    /// <summary>A response carrying one message and the status in its trailers.</summary>
    internal static HttpResponseMessage Message(IMessage message, int status = 0) => Framed(message.ToByteArray(), status);

    /// <summary>A response carrying one frame of these bytes, with the status in its trailers.</summary>
    internal static HttpResponseMessage Framed(byte[] payload, int? status = 0, bool compressed = false) =>
        Raw(Frame(payload, compressed), status);

    /// <summary>A response with this body, as is, and the status in its trailers when there is one.</summary>
    internal static HttpResponseMessage Raw(byte[] body, int? status = 0, string contentType = "application/grpc", HttpStatusCode code = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(code)
        {
            Version = HttpVersion.Version20,
            Content = new ByteArrayContent(body),
        };
        response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        if (status is { } value)
        {
            response.TrailingHeaders.Add("grpc-status", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return response;
    }

    /// <summary>A trailers-only response: the status, and no message, in its headers.</summary>
    internal static HttpResponseMessage TrailersOnly(int status, string message) => TrailersOnly(status.ToString(System.Globalization.CultureInfo.InvariantCulture), message);

    /// <summary>A trailers-only response with a grpc-status header of this text.</summary>
    internal static HttpResponseMessage TrailersOnly(string status, string message)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Version = HttpVersion.Version20,
            Content = new ByteArrayContent([]),
        };
        response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/grpc");
        response.Headers.Add("grpc-status", status);
        response.Headers.Add("grpc-message", Uri.EscapeDataString(message));
        return response;
    }

    /// <summary>One length-prefixed gRPC frame.</summary>
    internal static byte[] Frame(byte[] payload, bool compressed = false)
    {
        var frame = new byte[5 + payload.Length];
        frame[0] = compressed ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(frame, 5);
        return frame;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var sent = new SentRequest(
            request.RequestUri!.AbsolutePath.Split('/')[^1],
            request.RequestUri,
            request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase),
            request.Content?.Headers.ContentType?.MediaType,
            body);
        requests.Enqueue(sent);
        if (sent.Method == "GetServerInfo")
        {
            if (ServerInfo is not null)
            {
                return await ServerInfo(sent, cancellationToken);
            }

            var info = new Wire.ServerInfoResponse { Version = Version };
            info.Capabilities.AddRange(Capabilities);
            return Message(info);
        }

        return await Respond(sent, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        Interlocked.Increment(ref disposals);
        base.Dispose(disposing);
    }
}

/// <summary>One request a <see cref="FakeGrpcHandler"/> received.</summary>
/// <param name="Method">The bare method name.</param>
/// <param name="Uri">The request's URL.</param>
/// <param name="Headers">Its headers, by name.</param>
/// <param name="ContentType">Its content type.</param>
/// <param name="Body">Its body, framed.</param>
internal sealed record SentRequest(string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? ContentType, byte[] Body)
{
    /// <summary>The message the body frames.</summary>
    internal T Message<T>(MessageParser<T> parser)
        where T : IMessage<T> =>
        parser.ParseFrom(Body.AsSpan(5).ToArray());
}

/// <summary>Connections over a <see cref="FakeGrpcHandler"/>.</summary>
internal static class FakeConnection
{
    /// <summary>Options addressing the fake, through it, with any of these overridden.</summary>
    internal static ConnectionOptions Options(
        HttpMessageHandler handler,
        TimeSpan? requestTimeout = null,
        IReadOnlyDictionary<string, string>? headers = null,
        int? maxRequestSize = null,
        int? maxResponseSize = null,
        Microsoft.Extensions.Logging.ILogger? logger = null,
        string? version = null,
        IReadOnlyList<string>? requireCapabilities = null) => new()
        {
            Address = FakeGrpcHandler.Address,
            HttpMessageHandler = handler,
            RequestTimeout = requestTimeout ?? ConnectionOptions.DefaultRequestTimeout,
            Headers = headers ?? new Dictionary<string, string>(),
            MaxRequestSize = maxRequestSize ?? ConnectionOptions.DefaultMaxMessageSize,
            MaxResponseSize = maxResponseSize ?? ConnectionOptions.DefaultMaxMessageSize,
            Logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            Version = version,
            RequireCapabilities = requireCapabilities ?? [],
        };

    /// <summary>Opens a connection over the fake.</summary>
    internal static Task<Connection> OpenAsync(FakeGrpcHandler handler, ConnectionOptions? options = null) =>
        Connection.OpenAsync(options ?? Options(handler), Xunit.TestContext.Current.CancellationToken);

    /// <summary>A raw GetSymbol call, the method the tests make when the method does not matter.</summary>
    internal static Task<Wire.SymbolResponse> CallGetSymbolAsync(this Connection connection, CancellationToken cancellationToken) =>
        connection.CallAsync<Wire.GetSymbolRequest, Wire.SymbolResponse>(
            "GetSymbol",
            new Wire.GetSymbolRequest { ModelHash = "m", SymbolId = "A" },
            cancellationToken);

    /// <summary>A raw GetSymbol call under the test's token.</summary>
    internal static Task<Wire.SymbolResponse> GetSymbolAsync(this Connection connection) =>
        connection.CallGetSymbolAsync(Xunit.TestContext.Current.CancellationToken);
}
