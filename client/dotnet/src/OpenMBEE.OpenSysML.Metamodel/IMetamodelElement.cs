namespace OpenMBEE.OpenSysML.Metamodel;

/// <summary>
/// What every element of a metamodel document carries besides its metaclass's properties: its
/// identity and the metaclass it is read as. Every generated root metaclass interface, <see cref="IElement"/>
/// among them, extends it, so no generated property may take one of these names.
/// </summary>
public interface IMetamodelElement
{
    /// <summary>The element's <c>@id</c>.</summary>
    string Id { get; }

    /// <summary>The element's <c>@type</c>, as the document wrote it.</summary>
    string JsonType { get; }

    /// <summary>The metaclass the element is read as.</summary>
    MetaclassInfo MetaclassInfo { get; }
}
