using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using OpenMBEE.OpenSysML.Internal;

namespace OpenMBEE.OpenSysML;

/// <summary>
/// A connection to a <c>sysml-grpc</c> service, and the entry point of this client. Opening one asks
/// the service what it is, once; <see cref="ServerInfo"/> keeps the answer. A connection is safe to
/// share between threads and owns one channel to the service, which disposing it closes. A service
/// this client did not start is never stopped by it.
/// </summary>
/// <remarks>
/// A connection speaks gRPC, or the Connect protocol with protobuf or JSON bodies, as
/// <see cref="ConnectionOptions.Protocol"/> and <see cref="ConnectionOptions.Encoding"/> say, and
/// never another. This release connects to a service that is already running, named by
/// <see cref="ConnectionOptions.Address"/> or <see cref="ConnectionOptions.ServiceVariable"/>; it
/// does not yet start a private one.
/// </remarks>
public sealed partial class Connection : IAsyncDisposable
{
    private const string ServerInfoMethod = "GetServerInfo";

    private readonly ITransport transport;
    private readonly ILogger logger;
    private readonly TimeSpan requestTimeout;
    private readonly CancellationTokenSource closing = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ServerInfo? serverInfo;
    private int active;
    private int disposing;

    private Connection(ITransport transport, Uri address, ConnectionOptions options)
    {
        this.transport = transport;
        Address = address;
        logger = options.Logger;
        requestTimeout = options.RequestTimeout;
    }

    /// <summary>The base URL of the service.</summary>
    public Uri Address { get; }

    /// <summary>The protocol the connection speaks.</summary>
    public Protocol Protocol => transport.Protocol;

    /// <summary>How the connection encodes messages.</summary>
    public MessageEncoding Encoding => transport.Encoding;

    /// <summary>What the service said of itself when the connection opened.</summary>
    public ServerInfo ServerInfo => serverInfo ?? throw new InvalidOperationException("The connection has not finished opening.");

    /// <summary>The capabilities the service advertised when the connection opened.</summary>
    public Capabilities Capabilities => ServerInfo.Capabilities;

    /// <summary>
    /// Opens a connection: asks the service for its <see cref="ServerInfo"/>, then checks
    /// <see cref="ConnectionOptions.Version"/> and <see cref="ConnectionOptions.RequireCapabilities"/>
    /// against the answer. A failure after the channel was made closes it again.
    /// </summary>
    /// <param name="options">How to reach the service; null for <see cref="ConnectionOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open connection, which the caller disposes.</returns>
    /// <exception cref="InvalidRequestException">An option is invalid.</exception>
    /// <exception cref="ServiceStartException">No service is named; this release does not start one.</exception>
    /// <exception cref="StaleServiceException">The service is not the <see cref="ConnectionOptions.Version"/> asked for.</exception>
    /// <exception cref="CapabilityException">The service lacks a required capability.</exception>
    /// <exception cref="TransportException">The service could not be reached.</exception>
    /// <exception cref="ServiceException">The service refused GetServerInfo.</exception>
    public static Task<Connection> OpenAsync(ConnectionOptions? options = null, CancellationToken cancellationToken = default) =>
        OpenAsync(options, Environment.GetEnvironmentVariable, cancellationToken);

    /// <summary>
    /// Calls a method of the service directly, with the connection's headers, timeout, size limits,
    /// error mapping and disposal checks. This is the escape hatch below the typed API: it checks
    /// no capability and interprets no result, so a failure the answer reports in band is returned
    /// as an ordinary response. The service still validates the request.
    /// </summary>
    /// <typeparam name="TRequest">The request message the method takes.</typeparam>
    /// <typeparam name="TResponse">The response message the method returns.</typeparam>
    /// <param name="method">The method, bare (<c>ParseFile</c>) or qualified (<c>sysml.SysMLService/ParseFile</c>).</param>
    /// <param name="request">The request; the call does not change it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The response.</returns>
    /// <exception cref="InvalidRequestException">The service has no such method, or not with these messages; nothing was sent.</exception>
    /// <exception cref="ServiceException">The service refused the call.</exception>
    /// <exception cref="TransportException">The call failed in transport.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The connection was disposed before or during the call.</exception>
    public Task<TResponse> CallAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposing) != 0, this);
        if (!RpcMethods.TryResolve<TRequest, TResponse>(method, out var resolved, out var problem))
        {
            throw new InvalidRequestException(problem);
        }

        return InvokeAsync(resolved, request, cancellationToken);
    }

    /// <summary>
    /// Closes the connection: refuses new calls, cancels the calls in flight, which fail with
    /// <see cref="ObjectDisposedException"/>, waits for them, and closes the channel. Disposing again,
    /// or concurrently, waits for the same close; it never throws. A failure to close is logged.
    /// </summary>
    /// <returns>When the connection is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposing, 1) != 0)
        {
            await closed.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            await closing.CancelAsync().ConfigureAwait(false);
        }
        catch (AggregateException error)
        {
            LogCloseFailed(logger, Address, error);
        }

        if (Volatile.Read(ref active) == 0)
        {
            drained.TrySetResult();
        }

        await drained.Task.ConfigureAwait(false);
        try
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Disposal never throws: a failure to close is logged, as the contract says.
        catch (Exception error)
#pragma warning restore CA1031
        {
            LogCloseFailed(logger, Address, error);
        }

        closing.Dispose();
        LogClosed(logger, Address);
        closed.TrySetResult();
    }

    /// <summary>Opens a connection, reading the environment through <paramref name="environment"/>.</summary>
    /// <param name="options">How to reach the service; null for <see cref="ConnectionOptions.Default"/>.</param>
    /// <param name="environment">Reads an environment variable; null when it is unset.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open connection.</returns>
    internal static async Task<Connection> OpenAsync(
        ConnectionOptions? options,
        Func<string, string?> environment,
        CancellationToken cancellationToken)
    {
        options ??= ConnectionOptions.Default;
        options.Validate();
        var address = ResolveAddress(options, environment);
        cancellationToken.ThrowIfCancellationRequested();
        ITransport transport = options.Protocol == Protocol.Connect ? new ConnectTransport(address, options) : new GrpcTransport(address, options);
        return await OpenAsync(transport, address, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens a connection over a transport, which the connection then owns, even when the open fails.</summary>
    /// <param name="transport">The transport.</param>
    /// <param name="address">The service's base URL.</param>
    /// <param name="options">The connection's options, already validated.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open connection.</returns>
    internal static async Task<Connection> OpenAsync(
        ITransport transport,
        Uri address,
        ConnectionOptions options,
        CancellationToken cancellationToken)
    {
        var connection = new Connection(transport, address, options);
        try
        {
            connection.serverInfo = await connection.AskServerInfoAsync(cancellationToken).ConfigureAwait(false);
            connection.RequireOptions(options);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        LogOpened(connection.logger, address, connection.Protocol, connection.Encoding, connection.serverInfo);
        return connection;
    }

    /// <summary>The address options name, or the refusal of a connection that names none.</summary>
    /// <param name="options">The connection's options.</param>
    /// <param name="environment">Reads an environment variable.</param>
    /// <returns>The service's base URL.</returns>
    internal static Uri ResolveAddress(ConnectionOptions options, Func<string, string?> environment)
    {
        if (options.Address is not null)
        {
            return ServiceAddress.Parse(options.Address, "ConnectionOptions.Address");
        }

        var named = environment(ConnectionOptions.ServiceVariable);
        if (!string.IsNullOrWhiteSpace(named))
        {
            return ServiceAddress.Parse(named, "$" + ConnectionOptions.ServiceVariable);
        }

        throw new ServiceStartException(options.AutoStart
            ? "No service was named, and this release of the .NET client cannot start a private one yet: "
                + $"name a running sysml-grpc with ConnectionOptions.Address or ${ConnectionOptions.ServiceVariable}."
            : "No service was named and ConnectionOptions.AutoStart is false: "
                + $"name a running sysml-grpc with ConnectionOptions.Address or ${ConnectionOptions.ServiceVariable}.");
    }

    private async Task<ServerInfo> AskServerInfoAsync(CancellationToken cancellationToken)
    {
        if (!RpcMethods.TryResolve<Wire.ServerInfoRequest, Wire.ServerInfoResponse>(ServerInfoMethod, out var method, out var problem))
        {
            throw new InvalidOperationException(problem);
        }

        try
        {
            var response = await InvokeAsync(method, new Wire.ServerInfoRequest(), cancellationToken).ConfigureAwait(false);
            return new ServerInfo(response.Version, new Capabilities(response.Capabilities, response.Version), answered: true);
        }
        catch (ServiceException error) when (error.StatusCode == StatusCode.Unimplemented)
        {
            LogServerInfoUnanswered(logger, Address);
            return new ServerInfo(string.Empty, Capabilities.None, answered: false);
        }
    }

    private void RequireOptions(ConnectionOptions options)
    {
        var info = ServerInfo;
        if (options.Version is { } version)
        {
            string? reason = null;
            if (!info.Answered)
            {
                reason = $"it did not answer GetServerInfo, so it cannot be shown to be the {version} asked for";
            }
            else if (!string.Equals(info.Version, version, StringComparison.Ordinal))
            {
                reason = $"it reports version {(info.Version.Length == 0 ? "(unknown)" : info.Version)}, but {version} was asked for";
            }

            if (reason is not null)
            {
                throw new StaleServiceException(
                    Address.ToString(),
                    reason,
                    $"connect to a service of version {version}, or accept the one at {Address} by leaving ConnectionOptions.Version null",
                    info.Version);
            }
        }

        foreach (var capability in options.RequireCapabilities)
        {
            info.Capabilities.Require(capability);
        }
    }

    private async Task<TResponse> InvokeAsync<TRequest, TResponse>(RpcMethod<TRequest, TResponse> method, TRequest request, CancellationToken cancellationToken)
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        Enter();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closing.Token);
            var deadline = DateTime.UtcNow + requestTimeout;
            try
            {
                return await transport.UnaryAsync(method, request, deadline, call.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or TransportException { StatusCode: StatusCode.Cancelled or StatusCode.DeadlineExceeded })
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException($"{method.Untyped.QualifiedName} was cancelled.", error, cancellationToken);
                }

                if (closing.IsCancellationRequested)
                {
                    throw new ObjectDisposedException($"{method.Untyped.QualifiedName} was cancelled because its connection was disposed.", error);
                }

                throw;
            }
        }
        finally
        {
            Exit();
        }
    }

    private void Enter()
    {
        Interlocked.Increment(ref active);
        if (Volatile.Read(ref disposing) != 0)
        {
            Exit();
            ObjectDisposedException.ThrowIf(true, this);
        }
    }

    private void Exit()
    {
        if (Interlocked.Decrement(ref active) == 0 && Volatile.Read(ref disposing) != 0)
        {
            drained.TrySetResult();
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Connected to {Address} over {Protocol} with {Encoding}: {ServerInfo}")]
    private static partial void LogOpened(ILogger logger, Uri address, Protocol protocol, MessageEncoding encoding, ServerInfo serverInfo);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The service at {Address} did not answer GetServerInfo; it is assumed to advertise no capability")]
    private static partial void LogServerInfoUnanswered(ILogger logger, Uri address);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Closed the connection to {Address}")]
    private static partial void LogClosed(ILogger logger, Uri address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closing the connection to {Address} failed")]
    private static partial void LogCloseFailed(ILogger logger, Uri address, Exception error);
}
