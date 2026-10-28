using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace OpenMBEE.OpenSysML.Metamodel;

/// <summary>
/// One metaclass of the SysML metamodel: its place in the generalization hierarchy, the properties it
/// declares and inherits, and the C# types generated for it. Every instance is one of the
/// <see cref="Metaclasses"/>; none is created elsewhere.
/// </summary>
public sealed class MetaclassInfo
{
    private readonly Func<string, string, MetamodelElement>? factory;
    private IReadOnlyList<MetaclassInfo>? parents;
    private IReadOnlyList<MetaclassInfo>? ancestors;
    private IReadOnlyList<MetaProperty>? declared;
    private IReadOnlyList<MetaProperty>? effective;
    private FrozenSet<MetaclassInfo>? conforming;
    private FrozenDictionary<string, MetaProperty>? byJsonKey;
    private FrozenDictionary<string, MetaProperty>? byName;

    internal MetaclassInfo(string name, bool isAbstract, Type interfaceType, Type? elementType, Func<string, string, MetamodelElement>? factory)
    {
        if (isAbstract != (elementType is null) || (elementType is null) != (factory is null))
        {
            throw new ArgumentException($"{name}: a concrete metaclass has an element class and a factory, an abstract one neither");
        }

        Name = name;
        IsAbstract = isAbstract;
        InterfaceType = interfaceType;
        ElementType = elementType;
        this.factory = factory;
    }

    /// <summary>The metaclass name, for example <c>PartUsage</c>: the local name of its IRI and the <c>@type</c> a document writes.</summary>
    public string Name { get; }

    /// <summary>Whether no element is an instance of exactly this metaclass.</summary>
    public bool IsAbstract { get; }

    /// <summary>The generated interface, for example <c>IPartUsage</c>, that every element conforming to the metaclass implements.</summary>
    public Type InterfaceType { get; }

    /// <summary>The generated sealed class of elements of exactly this metaclass; null when the metaclass is abstract.</summary>
    public Type? ElementType { get; }

    /// <summary>The direct superclasses, in the order the metamodel declares them.</summary>
    public IReadOnlyList<MetaclassInfo> Parents => parents ?? throw Uninitialized();

    /// <summary>
    /// Every superclass, direct or not, each once: the metaclass's C3 linearization without itself,
    /// nearest first, so a superclass always precedes its own superclasses.
    /// </summary>
    public IReadOnlyList<MetaclassInfo> Ancestors => ancestors ?? throw Uninitialized();

    /// <summary>The properties this metaclass itself declares, in ordinal order of their JSON keys.</summary>
    public IReadOnlyList<MetaProperty> DeclaredProperties => declared ?? throw Uninitialized();

    /// <summary>
    /// Every property an element of this metaclass has, declared or inherited, one per JSON key, in
    /// ordinal order of the keys. Where a property redefines an inherited one of the same name, the
    /// redefining property is the one listed.
    /// </summary>
    public IReadOnlyList<MetaProperty> Properties => effective ?? throw Uninitialized();

    /// <summary>Whether this metaclass is <paramref name="other"/> or one of its subclasses.</summary>
    /// <param name="other">The metaclass to test against.</param>
    /// <returns>True when every element of this metaclass is an element of <paramref name="other"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public bool ConformsTo(MetaclassInfo other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return (conforming ?? throw Uninitialized()).Contains(other);
    }

    /// <summary>Finds a property of an element of this metaclass by its JSON key, for example <c>ownedFeature</c>.</summary>
    /// <param name="jsonKey">The key.</param>
    /// <param name="property">The property, when there is one.</param>
    /// <returns>Whether the metaclass has a property with that key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="jsonKey"/> is null.</exception>
    public bool TryGetProperty(string jsonKey, [NotNullWhen(true)] out MetaProperty? property)
    {
        ArgumentNullException.ThrowIfNull(jsonKey);
        return (byJsonKey ?? throw Uninitialized()).TryGetValue(jsonKey, out property);
    }

    /// <summary>Finds a property of an element of this metaclass by its C# property name, for example <c>OwnedFeature</c>.</summary>
    /// <param name="name">The C# name.</param>
    /// <param name="property">The property, when there is one.</param>
    /// <returns>Whether the metaclass has a property with that C# name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public bool TryGetPropertyByName(string name, [NotNullWhen(true)] out MetaProperty? property)
    {
        ArgumentNullException.ThrowIfNull(name);
        return (byName ?? throw Uninitialized()).TryGetValue(name, out property);
    }

    /// <summary>The metaclass name.</summary>
    /// <returns><see cref="Name"/>.</returns>
    public override string ToString() => Name;

    /// <summary>
    /// Completes the metaclass once every ancestor is complete: the generated registry calls it in
    /// an order that puts each metaclass after its superclasses.
    /// </summary>
    internal void Initialize(MetaclassInfo[] parentList, MetaclassInfo[] linearization, MetaProperty[] declaredList)
    {
        if (parents is not null)
        {
            throw new InvalidOperationException($"{Name} is initialized already");
        }

        if (linearization.Any(ancestor => ancestor.effective is null) || parentList.Any(parent => !linearization.Contains(parent)))
        {
            throw new InvalidOperationException($"{Name} is initialized before its superclasses");
        }

        if (declaredList.Any(property => property.DeclaringMetaclass != this))
        {
            throw new InvalidOperationException($"{Name} lists a property it does not declare");
        }

        var properties = new Dictionary<string, MetaProperty>(StringComparer.Ordinal);
        foreach (var owner in linearization.Prepend(this))
        {
            foreach (var property in owner == this ? declaredList : owner.DeclaredProperties)
            {
                properties.TryAdd(property.JsonKey, property);
            }
        }

        parents = Array.AsReadOnly<MetaclassInfo>([.. parentList]);
        ancestors = Array.AsReadOnly<MetaclassInfo>([.. linearization]);
        declared = Array.AsReadOnly<MetaProperty>([.. declaredList.OrderBy(property => property.JsonKey, StringComparer.Ordinal)]);
        effective = Array.AsReadOnly<MetaProperty>([.. properties.Values.OrderBy(property => property.JsonKey, StringComparer.Ordinal)]);
        conforming = linearization.Prepend(this).ToFrozenSet();
        byJsonKey = properties.ToFrozenDictionary(StringComparer.Ordinal);
        byName = properties.Values.ToFrozenDictionary(property => property.Name, StringComparer.Ordinal);
    }

    /// <summary>Creates an element of exactly this metaclass, with nothing supplied yet.</summary>
    /// <exception cref="InvalidOperationException">The metaclass is abstract.</exception>
    internal MetamodelElement CreateElement(string id, string jsonType) =>
        factory is null
            ? throw new InvalidOperationException($"{Name} is abstract; no element is exactly an instance of it")
            : factory(id, jsonType);

    private InvalidOperationException Uninitialized() => new($"{Name} is not initialized");
}
