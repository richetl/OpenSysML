using System;

namespace OpenMBEE.OpenSysML;

/// <summary>
/// A standard-library element an <c>api-json</c> conversion refers to, listed beside the element array rather than
/// written into it as a root.
/// </summary>
public sealed record LibraryReference
{
    /// <summary>Creates a reference.</summary>
    /// <param name="id">The element's id, as the element array's references name it.</param>
    /// <param name="qualifiedName">The element's qualified name in the standard library.</param>
    /// <param name="type">The element's metaclass, as the API's JSON <c>@type</c> spells it.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public LibraryReference(string id, string qualifiedName, string type)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(qualifiedName);
        ArgumentNullException.ThrowIfNull(type);
        Id = id;
        QualifiedName = qualifiedName;
        Type = type;
    }

    /// <summary>The element's id, as the element array's references name it.</summary>
    public string Id { get; }

    /// <summary>The element's qualified name in the standard library.</summary>
    public string QualifiedName { get; }

    /// <summary>The element's metaclass, as the API's JSON <c>@type</c> spells it.</summary>
    public string Type { get; }

    /// <summary>The element as a reader names it: its qualified name and metaclass.</summary>
    /// <returns>The qualified name, then the metaclass in parentheses.</returns>
    public override string ToString() => $"{QualifiedName} ({Type})";
}
