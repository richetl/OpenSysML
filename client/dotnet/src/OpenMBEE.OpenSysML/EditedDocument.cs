using System;

namespace OpenMBEE.OpenSysML;

/// <summary>The notation of one document of a model as an operation left it, named as the model names the document.</summary>
public sealed record EditedDocument
{
    /// <summary>Creates a document.</summary>
    /// <param name="name">The document's name as the parse named it, or the name the service gave a document it made.</param>
    /// <param name="content">The document's notation, byte-identical to the source outside what the operation changed.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public EditedDocument(string name, string content)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(content);
        Name = name;
        Content = content;
    }

    /// <summary>
    /// The document's name as the parse named it: the file path of a file, the name of an inline document, or the
    /// position it was given when it was named neither; or the name the service gave a document it made.
    /// </summary>
    public string Name { get; }

    /// <summary>The document's notation, byte-identical to the source outside what the operation changed.</summary>
    public string Content { get; }
}
