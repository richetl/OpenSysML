using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenMBEE.OpenSysML.Tests;

/// <summary>
/// A sysml-grpc the tests start and stop themselves, so a connection reaches it at an explicit
/// address as it would any external service. This is a test harness, not the client's own
/// lifecycle: it reads the address the service reports, drains its output, and on disposal closes
/// its stdin, which -exit-with-parent makes it exit on, killing it only if it does not.
/// </summary>
internal sealed class ServiceProcess : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(10);

    private readonly Process process;
    private readonly Task draining;
    private readonly ConcurrentQueue<string> output = new();

    private ServiceProcess(Process process, string address, string transport)
    {
        this.process = process;
        Address = address;
        Transport = transport;
        draining = Task.WhenAll(DrainAsync(process.StandardOutput), DrainAsync(process.StandardError));
    }

    /// <summary>The address the service reported, as host:port.</summary>
    internal string Address { get; }

    /// <summary>The transport it serves: connect or grpc.</summary>
    internal string Transport { get; }

    /// <summary>Whether the process has exited.</summary>
    internal bool HasExited => process.HasExited;

    /// <summary>Starts a service serving one transport on a port the kernel chooses.</summary>
    /// <param name="binary">The sysml-grpc binary.</param>
    /// <param name="transport">connect or grpc.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    internal static async Task<ServiceProcess> StartAsync(string binary, string transport, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(binary)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-port", "0", "-health-port", "0", "-report-address", "-exit-with-parent", "-transport", transport, "-log-level", "warn" })
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException($"{binary} did not start.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StartupTimeout);
            var address = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(address))
            {
                var errors = await process.StandardError.ReadToEndAsync(timeout.Token);
                throw new InvalidOperationException($"{binary} reported no address: {errors}");
            }

            return new ServiceProcess(process, address.Trim(), transport);
        }
        catch
        {
            await StopAsync(process);
            process.Dispose();
            throw;
        }
    }

    /// <summary>Closes the service's stdin and waits for it to exit, killing it if it does not.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(process);
        await draining;
        process.Dispose();
    }

    /// <summary>What the service wrote after its address.</summary>
    public override string ToString() => $"sysml-grpc -transport {Transport} at {Address}: {string.Join('\n', output)}";

    private static async Task StopAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        using var grace = new CancellationTokenSource(ShutdownGrace);
        try
        {
            await process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    private async Task DrainAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
        {
            output.Enqueue(line);
        }
    }
}
