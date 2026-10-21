using System;
using System.Reflection;
using System.Runtime.Versioning;
using OpenMBEE.OpenSysML;

// A ProjectReference consumer of the client. It prints the referenced assembly's identity and,
// when OPENSYSML_SERVICE names a running sysml-grpc, connects to it and prints what it reports.
const string ClientAssembly = "OpenMBEE.OpenSysML";

var client = typeof(Connection).Assembly;
var version = client.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
var framework = client.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
if (version is null || framework is null)
{
    Console.Error.WriteLine($"{ClientAssembly} carries no informational version or target framework.");
    return 1;
}

Console.WriteLine($"{ClientAssembly} {version} ({framework})");
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionOptions.ServiceVariable)))
{
    return 0;
}

try
{
    await using var connection = await Connection.OpenAsync();
    Console.WriteLine(connection.ServerInfo);
    return 0;
}
catch (OpenSysMLException error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
