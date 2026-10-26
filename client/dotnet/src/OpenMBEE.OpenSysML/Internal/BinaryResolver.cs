using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OpenMBEE.OpenSysML.Internal;

/// <summary>A <c>sysml-grpc</c> binary to start: its canonical path and the SHA-256 digest of its contents.</summary>
/// <param name="Path">The full path, every link in it followed.</param>
/// <param name="Sha256">The digest of its contents, in lower-case hexadecimal.</param>
internal sealed record ResolvedBinary(string Path, string Sha256);

/// <summary>A binary as found, before its links are followed.</summary>
/// <param name="Path">The path of an executable file.</param>
/// <param name="Cache">The shared cache, when that is where it was found.</param>
internal sealed record FoundBinary(string Path, CacheLayout? Cache);

/// <summary>
/// Finds the <c>sysml-grpc</c> binary a private service is started from: the one
/// <see cref="ConnectionOptions.BinaryPath"/> names, else the one <see cref="ConnectionOptions.BinaryVariable"/>
/// names, else the shared cache every client installs into, <c>~/.opensysml/bin</c>, else the first on
/// <c>PATH</c>. A binary named explicitly that is not there fails rather than another being used. The
/// binary is hashed whichever way it was found, and checked against
/// <see cref="ConnectionOptions.ExpectedBinarySha256"/> before anything executes it. The shared cache is
/// read with its lock held, and what is started is its executable linked under its own digest, which no
/// later install replaces. Releases are not downloaded.
/// </summary>
internal static partial class BinaryResolver
{
    private const int MaxLinkHops = 40;

    /// <summary>The binary's file name on this platform.</summary>
    internal static string ExecutableName => OperatingSystem.IsWindows() ? "sysml-grpc.exe" : "sysml-grpc";

    /// <summary>The variable holding the home directory on this platform.</summary>
    internal static string HomeVariable => OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";

    /// <summary>The platform this process runs on when it cannot start processes; null when it can.</summary>
    internal static string? ProcessLessPlatform =>
        OperatingSystem.IsBrowser() ? "browser"
        : OperatingSystem.IsWasi() ? "wasi"
        : OperatingSystem.IsIOS() ? "ios"
        : OperatingSystem.IsTvOS() ? "tvos"
        : null;

    /// <summary>Finds, hashes and checks the binary to start.</summary>
    /// <param name="options">The connection's options, already validated.</param>
    /// <param name="environment">Reads an environment variable; null when it is unset.</param>
    /// <param name="cancellationToken">Cancels the hashing, and waiting for the shared cache's lock.</param>
    /// <returns>The binary.</returns>
    /// <exception cref="UnsupportedPlatformException">This platform cannot start a process.</exception>
    /// <exception cref="BinaryNotFoundException">No executable binary was found where it was looked for.</exception>
    /// <exception cref="ChecksumMismatchException">The binary does not have the digest required of it.</exception>
    /// <exception cref="ServiceStartException">The binary could not be read, or the shared cache stayed locked.</exception>
    internal static async Task<ResolvedBinary> ResolveAsync(ConnectionOptions options, Func<string, string?> environment, CancellationToken cancellationToken)
    {
        RequireProcesses(ProcessLessPlatform);
        var found = Find(options, environment);
        string canonical;
        string digest;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                if (found.Cache is null)
                {
                    canonical = Canonical(found.Path);
                    digest = await Sha256Async(canonical, cancellationToken).ConfigureAwait(false);
                    break;
                }

                if (await SnapshotAsync(found.Cache, options.Logger, cancellationToken).ConfigureAwait(false) is { } snapshot)
                {
                    canonical = Canonical(snapshot.Path);
                    digest = snapshot.Sha256;
                    LogFromCache(options.Logger, found.Path, canonical, snapshot.Kind);
                    break;
                }

                // Removed while its lock was awaited: look again, as if it had never been there.
                if (attempt == 3)
                {
                    throw new ServiceStartException($"{found.Path} was removed each time its lock was taken, {attempt} times; nothing is left to start there.");
                }

                found = Find(options, environment);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ServiceStartException($"sysml-grpc at {found.Path} could not be read: {error.Message}", error);
        }

        if (options.ExpectedBinarySha256 is { } expected && !string.Equals(expected, digest, StringComparison.OrdinalIgnoreCase))
        {
            throw new ChecksumMismatchException(
                canonical,
                expected.ToLowerInvariant(),
                digest,
                "name the binary that digest was taken of, or set ConnectionOptions.ExpectedBinarySha256 to this one's after checking where it came from");
        }

        LogResolved(options.Logger, canonical, digest);
        return new ResolvedBinary(canonical, digest);
    }

    /// <summary>Refuses to go on where processes cannot be started.</summary>
    /// <param name="platform">The platform, when it cannot start processes; null when it can.</param>
    /// <exception cref="UnsupportedPlatformException"><paramref name="platform"/> is not null.</exception>
    internal static void RequireProcesses(string? platform)
    {
        if (platform is not null)
        {
            throw new UnsupportedPlatformException(
                platform,
                $"A private sysml-grpc cannot be started on {platform}, which cannot start processes: "
                + $"name a running service with ConnectionOptions.Address or ${ConnectionOptions.ServiceVariable}.");
        }
    }

    /// <summary>The binary to start, as found, before its links are followed.</summary>
    /// <param name="options">The connection's options.</param>
    /// <param name="environment">Reads an environment variable.</param>
    /// <returns>The path of an executable file, and the shared cache when it is there.</returns>
    /// <exception cref="BinaryNotFoundException">None was found.</exception>
    internal static FoundBinary Find(ConnectionOptions options, Func<string, string?> environment)
    {
        if (options.BinaryPath is { } option)
        {
            return new FoundBinary(Named(option, "ConnectionOptions.BinaryPath"), null);
        }

        var variable = environment(ConnectionOptions.BinaryVariable);
        if (!string.IsNullOrWhiteSpace(variable))
        {
            return new FoundBinary(Named(variable, "$" + ConnectionOptions.BinaryVariable), null);
        }

        var searched = new List<string> { $"${ConnectionOptions.BinaryVariable}: unset" };
        if (CacheLayout.InHome(environment) is not { } cache)
        {
            searched.Add($"~/.opensysml/bin/{ExecutableName}: no home directory, since ${HomeVariable} is unset");
        }
        else
        {
            if (IsExecutable(cache.Executable, out var why))
            {
                return new FoundBinary(cache.Executable, cache);
            }

            searched.Add($"{cache.Executable}: {why}");
        }

        var directories = (environment("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, ExecutableName);
            if (IsExecutable(candidate, out _))
            {
                return new FoundBinary(candidate, null);
            }
        }

        searched.Add(directories.Length == 0
            ? "$PATH: unset or empty"
            : $"$PATH: no executable {ExecutableName} in its {directories.Length} director{(directories.Length == 1 ? "y" : "ies")}");
        throw new BinaryNotFoundException(
            $"No sysml-grpc binary was found to start as a private service. Looked at: {string.Join("; ", searched)}.\n"
            + $"  fix: build one (make build-grpc) and set ConnectionOptions.BinaryPath or ${ConnectionOptions.BinaryVariable} to it, "
            + $"install one as ~/.opensysml/bin/{ExecutableName} or on $PATH, or name a running service with "
            + $"ConnectionOptions.Address or ${ConnectionOptions.ServiceVariable}. This release of the .NET client does not download one.",
            searched);
    }

    /// <summary>
    /// The full path of a file with every link in it followed, as the kernel would follow them, so
    /// that one file reached by different paths is one binary.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The canonical path.</returns>
    /// <exception cref="IOException">The links do not end within the kernel's own limit.</exception>
    /// <exception cref="DirectoryNotFoundException">A parent component is not an accessible directory.</exception>
    internal static string Canonical(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var current = Absolute(path, Environment.CurrentDirectory);
        for (var hops = 0; ; hops++)
        {
            var resolved = FollowFirstLink(current, out var canonical);
            if (resolved is null)
            {
                return canonical;
            }

            if (hops == MaxLinkHops)
            {
                throw new IOException($"{path} has more than {MaxLinkHops} links to follow.");
            }

            current = resolved;
        }
    }

    /// <summary>Whether a path is a file this process could execute, and why not when it is not.</summary>
    /// <param name="path">The path.</param>
    /// <param name="why">What it is instead, when it is not.</param>
    /// <returns>Whether it is.</returns>
    internal static bool IsExecutable(string path, out string why)
    {
        try
        {
            // File.Exists is true of a link to nothing, so what the links lead to is what is examined.
            string resolved;
            try
            {
                resolved = Canonical(path);
            }
            catch (DirectoryNotFoundException)
            {
                why = "absent";
                return false;
            }
            catch (IOException)
            {
                why = "a loop of links";
                return false;
            }

            if (Directory.Exists(resolved))
            {
                why = "a directory";
                return false;
            }

            if (!File.Exists(resolved))
            {
                why = new FileInfo(path).LinkTarget is null ? "absent" : "a link to nothing";
                return false;
            }

            if (!OperatingSystem.IsWindows()
                && (File.GetUnixFileMode(resolved) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            {
                why = "not executable";
                return false;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            why = $"unreadable ({error.Message})";
            return false;
        }

        why = string.Empty;
        return true;
    }

    private static string Named(string path, string source)
    {
        if (IsExecutable(path, out var why))
        {
            return path;
        }

        throw new BinaryNotFoundException(
            $"{source} names {path}, which is {why}, not an executable sysml-grpc; it is used as named or not at all.\n"
            + $"  fix: point {source} at a sysml-grpc build (make build-grpc writes bin/{ExecutableName}), or leave it unset to look for one.",
            [$"{source}: {path}: {why}"]);
    }

    private static string? FollowFirstLink(string full, out string canonical)
    {
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        canonical = root;
        for (var i = 0; i < parts.Length; i++)
        {
            if (!Directory.Exists(canonical))
            {
                throw new DirectoryNotFoundException($"{canonical} is not an accessible directory in {full}.");
            }

            if (parts[i] == ".")
            {
                continue;
            }

            if (parts[i] == "..")
            {
                canonical = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(canonical)) ?? root;
                continue;
            }

            var parent = canonical;
            canonical = Path.Combine(canonical, parts[i]);
            if (new FileInfo(canonical).LinkTarget is { } target)
            {
                return Path.Combine([Absolute(target, parent), .. parts[(i + 1)..]]);
            }
        }

        if (Path.EndsInDirectorySeparator(full) && !Directory.Exists(canonical))
        {
            throw new DirectoryNotFoundException($"{canonical} is not an accessible directory in {full}.");
        }

        return null;
    }

    // Keep parent components until their preceding links have been followed.
    private static string Absolute(string path, string parent) =>
        Path.IsPathFullyQualified(path) ? path
        : Path.IsPathRooted(path) ? Path.GetFullPath(path, parent)
        : Path.Combine(parent, path);

    /// <summary>The SHA-256 digest of a file's contents, in lower-case hexadecimal.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the reading.</param>
    /// <returns>The digest.</returns>
    internal static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (file.ConfigureAwait(false))
        {
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        }
    }

    // A cache that cannot be locked, being where this user may not write, is used as the other clients use it:
    // unlocked, with a warning, as itself.
    private static async Task<CacheSnapshot?> SnapshotAsync(CacheLayout cache, ILogger logger, CancellationToken cancellationToken)
    {
        CacheTransaction transaction;
        try
        {
            transaction = await CacheTransaction.BeginAsync(cache, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LogUnlocked(logger, cache.Lock, cache.Executable, error.Message);
            return File.Exists(cache.Executable)
                ? new CacheSnapshot(cache.Executable, await Sha256Async(cache.Executable, cancellationToken).ConfigureAwait(false), SnapshotKind.Unsnapshotted)
                : null;
        }

        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Resolved the private service's binary to {Path}, SHA-256 {Sha256}")]
    private static partial void LogResolved(ILogger logger, string path, string sha256);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Starting the shared cache's {Executable} as {Snapshot} ({Kind})")]
    private static partial void LogFromCache(ILogger logger, string executable, string snapshot, SnapshotKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not lock {Lock} ({Reason}); using {Executable} unlocked, so a release another client installs over it may be started instead")]
    private static partial void LogUnlocked(ILogger logger, string @lock, string executable, string reason);
}
