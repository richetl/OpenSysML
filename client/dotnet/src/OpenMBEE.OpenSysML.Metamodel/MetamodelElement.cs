using System;
using System.Collections;
using System.Collections.Generic;

namespace OpenMBEE.OpenSysML.Metamodel;

/// <summary>
/// The base of every generated element class in <c>OpenMBEE.OpenSysML.Metamodel.Elements</c>: one sealed class per concrete
/// metaclass, implementing that metaclass's interface and so the interface of every ancestor. A property
/// the document supplies reads as its value; one it does not supply throws <see cref="NotSuppliedException"/>
/// rather than reading as an invented default.
/// </summary>
/// <remarks>
/// Elements are created and supplied only while a document is read, and are immutable afterwards.
/// </remarks>
public abstract class MetamodelElement : IMetamodelElement
{
    private readonly Dictionary<MetaProperty, object?> values = [];

    private protected MetamodelElement(MetaclassInfo metaclass, string id, string jsonType)
    {
        ArgumentNullException.ThrowIfNull(metaclass);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(jsonType);
        MetaclassInfo = metaclass;
        Id = id;
        JsonType = jsonType;
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public string JsonType { get; }

    /// <inheritdoc/>
    public MetaclassInfo MetaclassInfo { get; }

    /// <summary>The metaclass name and <c>@id</c>, for diagnostics.</summary>
    /// <returns>For example <c>PartUsage 'a1b2'</c>.</returns>
    public override string ToString() => $"{MetaclassInfo.Name} '{Id}'";

    /// <summary>
    /// Records the value a document supplies for one of this element's properties: null, a value of the
    /// property's <see cref="MetaProperty.ValueType"/>, or for a many-valued property an immutable
    /// <see cref="IReadOnlyList{T}"/> of them. Null supplied to a many-valued property reads as empty.
    /// </summary>
    /// <exception cref="ArgumentException">The property is not one of this element's metaclass, or the value does not fit it.</exception>
    /// <exception cref="InvalidOperationException">The property was supplied already.</exception>
    internal void Supply(MetaProperty property, object? value)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (!MetaclassInfo.TryGetProperty(property.JsonKey, out var effective) || !ReferenceEquals(effective, property))
        {
            throw new ArgumentException($"{property.QualifiedName} is not a property of {MetaclassInfo.Name}", nameof(property));
        }

        if (values.ContainsKey(property))
        {
            throw new InvalidOperationException($"{property.QualifiedName} of {this} is supplied already");
        }

        if (value is not null && !Fits(property, value))
        {
            throw new ArgumentException(
                $"{property.QualifiedName} of {this} holds {property.ValueType.Name}{(property.IsMany ? " lists" : string.Empty)}, not {value.GetType().Name}",
                nameof(value));
        }

        values.Add(property, value);
    }

    /// <summary>Whether the document supplied the property, even as null.</summary>
    internal bool IsSupplied(MetaProperty property) => values.ContainsKey(property);

    /// <summary>A single string-valued property: null when the document supplies null.</summary>
    /// <exception cref="NotSuppliedException">The document does not supply the property.</exception>
    private protected string? ReadText(MetaProperty property) => (string?)Read(property);

    /// <summary>A single Boolean, integer, real or enumeration property: null when the document supplies null.</summary>
    /// <exception cref="NotSuppliedException">The document does not supply the property.</exception>
    private protected T? ReadValue<T>(MetaProperty property)
        where T : struct => Read(property) is { } value ? (T)value : null;

    /// <summary>A single reference to another element: null when the document supplies null.</summary>
    /// <exception cref="NotSuppliedException">The document does not supply the property.</exception>
    private protected T? ReadReference<T>(MetaProperty property)
        where T : class => (T?)Read(property);

    /// <summary>A many-valued property: empty when the document supplies null.</summary>
    /// <exception cref="NotSuppliedException">The document does not supply the property.</exception>
    private protected IReadOnlyList<T> ReadList<T>(MetaProperty property) =>
        Read(property) is { } value ? (IReadOnlyList<T>)value : [];

    private object? Read(MetaProperty property) =>
        values.TryGetValue(property, out var value) ? value : throw new NotSuppliedException(property, Id);

    private static bool Fits(MetaProperty property, object value)
    {
        if (!property.IsMany)
        {
            return property.ValueType.IsInstanceOfType(value);
        }

        if (!typeof(IReadOnlyList<>).MakeGenericType(property.ValueType).IsInstanceOfType(value))
        {
            return false;
        }

        foreach (var item in (IEnumerable)value)
        {
            if (item is null)
            {
                return false;
            }
        }

        return true;
    }
}
