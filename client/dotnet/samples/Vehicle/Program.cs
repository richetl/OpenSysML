using System;
using System.Reflection;
using System.Runtime.Versioning;

// A ProjectReference consumer of the client: it reads the referenced assembly's
// identity from its metadata, which is all the client offers so far.
const string ClientAssembly = "OpenMBEE.OpenSysML";

var client = Assembly.Load(new AssemblyName(ClientAssembly));
var version = client.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
var framework = client.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
if (version is null || framework is null)
{
    Console.Error.WriteLine($"{ClientAssembly} carries no informational version or target framework.");
    return 1;
}

Console.WriteLine($"{ClientAssembly} {version} ({framework})");
return 0;
