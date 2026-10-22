using System;
using System.Collections.Generic;

namespace OpenMBEE.OpenSysML;

/// <summary>
/// A failure the service reported inside an answer it did give: an expression that would not
/// evaluate, a symbol that is not there, an object that could not be built. The call itself
/// succeeded, so this is never a <see cref="ServiceException"/>: a refused call is a service
/// problem, a rejected model the caller's.
/// </summary>
public class ModelException : OpenSysMLException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The failure, as the service worded it.</param>
    /// <param name="method">The method whose answer reported it, as <c>sysml.SysMLService/Evaluate</c>.</param>
    /// <param name="diagnostics">The diagnostics the answer carried.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A diagnostic is null.</exception>
    public ModelException(string message, string method, IEnumerable<Diagnostic> diagnostics)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(method);
        Method = method;
        Diagnostics = new ValueList<Diagnostic>(diagnostics);
    }

    /// <summary>The method whose answer reported the failure, as <c>sysml.SysMLService/Evaluate</c>.</summary>
    public string Method { get; }

    /// <summary>The diagnostics the answer carried; empty when it carried none.</summary>
    public ValueList<Diagnostic> Diagnostics { get; }
}
