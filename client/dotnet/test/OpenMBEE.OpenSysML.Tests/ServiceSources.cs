using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenMBEE.OpenSysML.Tests;

/// <summary>One conformance scenario, as conformance/scenarios states it.</summary>
/// <param name="Id">The scenario's ID.</param>
/// <param name="Rpc">The bare name of the method it calls.</param>
/// <param name="RequiredCapabilities">The capabilities it requires of the service.</param>
internal sealed record Scenario(string Id, string Rpc, IReadOnlyList<string> RequiredCapabilities)
{
    private const string Qualifier = "sysml.SysMLService/";

    /// <summary>Every scenario in conformance/scenarios, in file order.</summary>
    internal static IReadOnlyList<Scenario> All { get; } = Load();

    private static List<Scenario> Load()
    {
        var scenarios = new List<Scenario>();
        foreach (var path in Directory.GetFiles(Repository.PathOf("conformance", "scenarios"), "*.json").Order(StringComparer.Ordinal))
        {
            using var file = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var scenario in file.RootElement.GetProperty("scenarios").EnumerateArray())
            {
                var rpc = scenario.GetProperty("rpc").GetString()!;
                var required = scenario.TryGetProperty("requires_capabilities", out var capabilities)
                    ? capabilities.EnumerateArray().Select(capability => capability.GetString()!).ToList()
                    : [];
                scenarios.Add(new Scenario(
                    scenario.GetProperty("id").GetString()!,
                    rpc.StartsWith(Qualifier, StringComparison.Ordinal) ? rpc[Qualifier.Length..] : rpc,
                    required));
            }
        }

        return scenarios;
    }
}

/// <summary>The capability names the service declares, read from its Go source.</summary>
internal static partial class ServiceCapabilities
{
    /// <summary>Every capability name a <c>const CapabilityX = "name"</c> of the service declares.</summary>
    internal static IReadOnlySet<string> Names { get; } = Load();

    [GeneratedRegex("""^const Capability\w+ = "([a-z_]+)"$""", RegexOptions.Multiline)]
    private static partial Regex Declaration();

    private static HashSet<string> Load()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(Repository.PathOf("internal", "frontend", "grpc"), "*.go"))
        {
            if (path.EndsWith("_test.go", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match declaration in Declaration().Matches(File.ReadAllText(path)))
            {
                names.Add(declaration.Groups[1].Value);
            }
        }

        return names;
    }
}
