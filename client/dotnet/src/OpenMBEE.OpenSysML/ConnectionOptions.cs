using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenMBEE.OpenSysML.Internal;

namespace OpenMBEE.OpenSysML;

/// <summary>
/// How a <see cref="Connection"/> reaches a service and calls it. Collections are copied when they
/// are set, so changing the collection afterwards changes nothing here. Values are checked when a
/// connection opens, which refuses an invalid one with <see cref="InvalidRequestException"/>
/// before it does any work.
/// </summary>
public sealed class ConnectionOptions
{
    /// <summary>The environment variable naming the service to connect to when <see cref="Address"/> does not.</summary>
    public const string ServiceVariable = "OPENSYSML_SERVICE";

    /// <summary>The default largest request and response message, in bytes: 512 MiB.</summary>
    public const int DefaultMaxMessageSize = 512 * 1024 * 1024;

    private IReadOnlyDictionary<string, string> headers = FrozenDictionary<string, string>.Empty;
    private IReadOnlyList<string> requireCapabilities = [];
    private ILogger logger = NullLogger.Instance;

    /// <summary>The default options: the service <see cref="ServiceVariable"/> names, over gRPC.</summary>
    public static ConnectionOptions Default { get; } = new();

    /// <summary>The default time a call may take: 60 seconds.</summary>
    public static TimeSpan DefaultRequestTimeout => TimeSpan.FromSeconds(60);

    /// <summary>The longest <see cref="RequestTimeout"/> a connection accepts: <see cref="int.MaxValue"/> milliseconds.</summary>
    public static TimeSpan MaxRequestTimeout => TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// The service to connect to: <c>host:port</c>, which means <c>http://host:port</c>, or an
    /// <c>http</c> or <c>https</c> URL with no path, query, fragment or user information. A bracketed
    /// IPv6 host, as <c>[::1]:50051</c>, is accepted. When null, <see cref="ServiceVariable"/> names
    /// it. A named service is never started, stopped or downloaded by this client.
    /// </summary>
    public string? Address { get; init; }

    /// <summary>
    /// The protocol to speak; <see cref="Protocol.Grpc"/> by default. The connection speaks it alone:
    /// a call that fails is never retried over another.
    /// </summary>
    public Protocol Protocol { get; init; } = Protocol.Grpc;

    /// <summary>
    /// How messages are encoded; <see cref="MessageEncoding.Protobuf"/> by default.
    /// <see cref="MessageEncoding.Json"/> needs <see cref="Protocol.Connect"/>: gRPC carries protobuf
    /// alone, so asking for JSON over it fails the open before anything is sent.
    /// </summary>
    public MessageEncoding Encoding { get; init; } = MessageEncoding.Protobuf;

    /// <summary>
    /// Whether to start a private service when none is named; true by default. This release of the
    /// client does not yet start one, so a connection with no service named fails with
    /// <see cref="ServiceStartException"/> either way, saying which.
    /// </summary>
    public bool AutoStart { get; init; } = true;

    /// <summary>
    /// How long a call may take, from when it is made: <see cref="DefaultRequestTimeout"/> by default.
    /// Positive and at most <see cref="MaxRequestTimeout"/>.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = DefaultRequestTimeout;

    /// <summary>
    /// Headers sent with every call, as authorization or other custom metadata. Names are matched
    /// without regard to case and are sent in lower case. A name is ASCII letters, digits, <c>-</c>,
    /// <c>_</c> and <c>.</c>, and a value printable ASCII. A header the protocol controls (content
    /// type and encoding, timeouts, the host, and any <c>grpc-</c> or <c>connect-</c> header) and a
    /// binary <c>-bin</c> header are refused. Values are never logged.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers
    {
        get => headers;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Any(header => header.Key is null || header.Value is null))
            {
                throw new ArgumentException("A header name or value is null.", nameof(value));
            }

            headers = value.ToFrozenDictionary(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Capabilities the service must advertise, checked once the connection has asked it what it
    /// is; a missing one fails the open with <see cref="CapabilityException"/>. A repeated name counts once.
    /// </summary>
    public IReadOnlyList<string> RequireCapabilities
    {
        get => requireCapabilities;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Contains(null!))
            {
                throw new ArgumentException("A required capability is null.", nameof(value));
            }

            requireCapabilities = [.. value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// The exact build version the service must report, or null to accept any. A service that
    /// reports another, or does not answer, fails the open with <see cref="StaleServiceException"/>.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>Where the connection logs; <see cref="NullLogger.Instance"/> by default.</summary>
    public ILogger Logger
    {
        get => logger;
        init => logger = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The HTTP handler to send through, or null for one the connection creates and disposes. A
    /// handler given here is borrowed: the connection never disposes it, so one handler can serve
    /// several connections and outlive them.
    /// </summary>
    public HttpMessageHandler? HttpMessageHandler { get; init; }

    /// <summary>
    /// The largest request message to send, in bytes as <see cref="Encoding"/> encodes it; positive,
    /// <see cref="DefaultMaxMessageSize"/> by default. A larger request fails without being sent.
    /// </summary>
    public int MaxRequestSize { get; init; } = DefaultMaxMessageSize;

    /// <summary>
    /// The largest response message to accept, in bytes as <see cref="Encoding"/> encodes it and
    /// after any decompression; positive, <see cref="DefaultMaxMessageSize"/> by default.
    /// </summary>
    public int MaxResponseSize { get; init; } = DefaultMaxMessageSize;

    /// <summary>Refuses options no connection can use.</summary>
    /// <exception cref="InvalidRequestException">An option is invalid.</exception>
    internal void Validate()
    {
        if (!Enum.IsDefined(Protocol))
        {
            throw new InvalidRequestException($"ConnectionOptions.Protocol is {(int)Protocol}, which is no protocol of this client.");
        }

        if (!Enum.IsDefined(Encoding))
        {
            throw new InvalidRequestException($"ConnectionOptions.Encoding is {(int)Encoding}, which is no encoding of this client.");
        }

        if (Protocol == Protocol.Grpc && Encoding != MessageEncoding.Protobuf)
        {
            throw new InvalidRequestException(
                $"ConnectionOptions.Encoding is {Encoding}, which gRPC does not carry: use Protocol.Connect for {Encoding}, or MessageEncoding.Protobuf over gRPC.");
        }

        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > MaxRequestTimeout)
        {
            throw new InvalidRequestException(
                $"ConnectionOptions.RequestTimeout is {RequestTimeout}; it must be positive and at most {MaxRequestTimeout}.");
        }

        if (MaxRequestSize <= 0)
        {
            throw new InvalidRequestException($"ConnectionOptions.MaxRequestSize is {MaxRequestSize}; it must be positive.");
        }

        if (MaxResponseSize <= 0)
        {
            throw new InvalidRequestException($"ConnectionOptions.MaxResponseSize is {MaxResponseSize}; it must be positive.");
        }

        if (Version is not null && string.IsNullOrWhiteSpace(Version))
        {
            throw new InvalidRequestException("ConnectionOptions.Version is blank; name a version, or leave it null to accept any.");
        }

        if (RequireCapabilities.FirstOrDefault(string.IsNullOrWhiteSpace) is not null)
        {
            throw new InvalidRequestException("ConnectionOptions.RequireCapabilities holds a blank capability name.");
        }

        RequestHeaders.Validate(Headers);
    }
}
