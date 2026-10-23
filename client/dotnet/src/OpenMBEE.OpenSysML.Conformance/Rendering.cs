using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace OpenMBEE.OpenSysML.Conformance;

/// <summary>
/// Writes what the client's public API returned back as the protocol messages a scenario is stated
/// against, so the comparison judges what a caller was given. It reads only public domain types:
/// a fact the API does not expose cannot be rendered, and a scenario asserting it fails.
/// </summary>
internal static class Rendering
{
    /// <summary>Diagnostics, in the order the API reports them.</summary>
    internal static IEnumerable<Wire.Diagnostic> Diagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            yield return Diagnostic(diagnostic);
        }
    }

    /// <summary>One diagnostic, its severity as the service spelled it.</summary>
    internal static Wire.Diagnostic Diagnostic(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var rendered = new Wire.Diagnostic
        {
            Severity = diagnostic.SeverityName,
            Message = diagnostic.Message,
            Code = diagnostic.Code,
            OrderDependent = { diagnostic.OrderDependent },
            Independent = diagnostic.Independent,
        };
        if (diagnostic.Span is { } span)
        {
            rendered.Span = new Wire.Span
            {
                File = span.File,
                StartLine = span.StartLine,
                StartCol = span.StartColumn,
                EndLine = span.EndLine,
                EndCol = span.EndColumn,
            };
        }

        return rendered;
    }

    /// <summary>A symbol with every fact the API reports of it.</summary>
    internal static Wire.SymbolInfo Symbol(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var rendered = new Wire.SymbolInfo
        {
            Id = symbol.Id,
            Name = symbol.Name,
            Kind = symbol.Kind,
            ChildIds = { symbol.ChildIds },
            WithheldLibraryAttributes = symbol.WithheldLibraryAttributes,
        };
        foreach (var (name, fact) in symbol.Metadata)
        {
            rendered.Metadata.Add(name, fact);
        }

        foreach (var attribute in symbol.Attributes)
        {
            var info = new Wire.AttributeInfo { Name = attribute.Name, Type = attribute.Type, Unit = attribute.Unit };
            if (attribute.Value is { } value)
            {
                info.Value = Value(value);
            }

            rendered.Attributes.Add(info);
        }

        if (symbol.TypeFacts is { } type)
        {
            rendered.TypeInfo = new Wire.TypeInfo
            {
                Declared = type.Declared,
                ResolvedId = type.ResolvedId,
                ResolvedKind = type.ResolvedKind,
                Primitive = type.Primitive,
                PrimitiveSource = type.PrimitiveSource,
                Quantity = type.IsQuantity,
                Unit = type.Unit,
            };
        }

        if (symbol.Multiplicity is { } multiplicity)
        {
            rendered.Multiplicity = new Wire.MultiplicityInfo { Lower = multiplicity.Lower, Upper = multiplicity.Upper };
        }

        foreach (var edge in symbol.Specializations)
        {
            rendered.Specializations.Add(new Wire.Specialization
            {
                Kind = edge.Kind,
                Declared = edge.Declared,
                TargetId = edge.TargetId,
                TargetKind = edge.TargetKind,
            });
        }

        return rendered;
    }

    /// <summary>An object the service built, with what each of its features holds.</summary>
    internal static Wire.Instance Instance(Instance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var rendered = new Wire.Instance { Id = instance.Id, TypeSymbolId = instance.TypeSymbolId };
        foreach (var (name, feature) in instance.FeatureValues)
        {
            var held = new Wire.FeatureValue
            {
                FeatureName = feature.FeatureName,
                Materialized = feature.IsMaterialized,
                Error = feature.Error,
                OrderDependent = feature.OrderDependent,
            };
            if (feature.Value is { } value)
            {
                held.Value = Value(value);
            }

            foreach (var each in feature.Values)
            {
                held.Values.Add(Value(each));
            }

            rendered.FeatureValues.Add(name, held);
        }

        return rendered;
    }

    /// <summary>A value, in the arm of its kind.</summary>
    /// <exception cref="InvalidOperationException">The value is of a kind this renderer does not know.</exception>
    internal static Wire.Value Value(Value value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            Value.NumberValue number => Number(number),
            Value.ComplexValue complex => new() { Complex = new Wire.Complex { Real = complex.Real, Imaginary = complex.Imaginary } },
            Value.BooleanValue boolean => new() { BoolValue = boolean.Value },
            Value.TextValue text => new() { StringValue = text.Value },
            Value.InstanceRefValue reference => new() { InstanceId = reference.InstanceId },
            Value.EnumLiteralValue literal => new() { EnumLiteral = EnumLiteral(literal) },
            Value.SequenceValue sequence => new() { Sequence = new Wire.ValueSequence { Elements = { Values(sequence.Elements) } } },
            Value.SetValue set => new() { Set = new Wire.ValueSet { Elements = { Values(set.Elements) } } },
            Value.QuantityValue quantity => new() { Quantity = Quantity(quantity.Quantity) },
            Value.ArrayValue array => new() { Array = new Wire.Array { Dimensions = { array.Dimensions }, Elements = { Values(array.Elements) } } },
            Value.VectorValue vector => new() { Vector = new Wire.Vector { Components = { Values(vector.Components) } } },
            Value.VectorQuantityValue vector => new() { VectorQuantity = new Wire.VectorQuantity { Components = { Quantities(vector.Components) } } },
            Value.TensorQuantityValue tensor => new()
            {
                TensorQuantity = new Wire.TensorQuantity { Dimensions = { tensor.Dimensions }, Components = { Quantities(tensor.Components) } },
            },
            Value.MeasurementRefValue reference => new()
            {
                MeasurementRef = new Wire.MeasurementRef { Unit = reference.Unit, UnitTerm = Term(reference.Reduction), UnitId = reference.UnitId ?? string.Empty },
            },
            Value.FunctionValue function => new() { Function = new Wire.Function { CalcId = function.CalcId, SelfId = function.SelfId ?? 0 } },
            Value.MetaobjectValue metaobject => new() { Metaobject = new Wire.Metaobject { ElementId = metaobject.ElementId, MetaclassId = metaobject.MetaclassId } },
            Value.NullValue nothing => new() { Null = nothing.Unsupported },
            OpenSysML.Value.UnsetValue => new() { Unset = true },
            Value.UndeterminedValue undetermined => new() { Undetermined = Undetermined(undetermined) },
            OpenSysML.Value.InfinityValue => new() { Infinity = true },
            _ => throw new InvalidOperationException($"no rendering of a {value.GetType().FullName}, a value of kind {value.Kind}"),
        };
    }

    private static IEnumerable<Wire.Value> Values<T>(IEnumerable<T> values)
        where T : OpenSysML.Value
    {
        foreach (var value in values)
        {
            yield return Value(value);
        }
    }

    private static IEnumerable<Wire.Quantity> Quantities(IEnumerable<Quantity> quantities)
    {
        foreach (var quantity in quantities)
        {
            yield return Quantity(quantity);
        }
    }

    private static Wire.Value Number(OpenSysML.Value.NumberValue number) => number switch
    {
        OpenSysML.Value.IntegerValue integer => new() { IntValue = integer.Value },
        OpenSysML.Value.BigIntegerValue big => new() { BigIntValue = Digits(big.Value) },
        OpenSysML.Value.RationalValue rational => new() { RationalValue = Terms(rational.Value) },
        OpenSysML.Value.RealValue real => new() { RealValue = real.Value },
        _ => throw new InvalidOperationException($"no rendering of a {number.GetType().FullName}, a number of kind {number.Kind}"),
    };

    private static Wire.EnumLiteral EnumLiteral(Value.EnumLiteralValue literal)
    {
        var rendered = new Wire.EnumLiteral { LiteralId = literal.LiteralId, EnumerationId = literal.EnumerationId, Name = literal.Name };
        if (literal.Scalar is { } scalar)
        {
            rendered.Value = Value(scalar);
        }

        return rendered;
    }

    private static Wire.Quantity Quantity(Quantity quantity)
    {
        var rendered = new Wire.Quantity { Unit = quantity.Unit };
        switch (quantity.Magnitude)
        {
            case Value.IntegerValue integer:
                rendered.IntMagnitude = integer.Value;
                break;
            case Value.BigIntegerValue big:
                rendered.BigIntMagnitude = Digits(big.Value);
                break;
            case Value.RationalValue rational:
                rendered.RationalMagnitude = Terms(rational.Value);
                break;
            case Value.RealValue real:
                rendered.RealMagnitude = real.Value;
                break;
            default:
                throw new InvalidOperationException($"no rendering of a quantity whose magnitude is a {quantity.Magnitude.GetType().FullName}");
        }

        if (quantity.Reduction is { } reduction)
        {
            rendered.UnitTerm = Term(reduction);
        }

        return rendered;
    }

    private static Wire.UnitTerm Term(UnitTerm term)
    {
        var rendered = new Wire.UnitTerm { ScaleNum = term.ScaleNumerator, ScaleDen = term.ScaleDenominator };
        foreach (var factor in term.Factors)
        {
            rendered.Factors.Add(new Wire.UnitFactor { UnitId = factor.UnitId, Exponent = factor.Exponent });
        }

        return rendered;
    }

    // The client reads an absent count as empty bounds, so only a bound it reports brings the count back.
    private static Wire.Undetermined Undetermined(Value.UndeterminedValue undetermined)
    {
        var rendered = new Wire.Undetermined { Reason = undetermined.Reason };
        if (undetermined.CountLower.Length > 0 || undetermined.CountUpper.Length > 0)
        {
            rendered.Count = new Wire.MultiplicityInfo { Lower = undetermined.CountLower, Upper = undetermined.CountUpper };
        }

        return rendered;
    }

    private static string Digits(BigInteger integer) => integer.ToString(CultureInfo.InvariantCulture);

    private static Wire.Rational Terms(Rational rational) =>
        new() { Numerator = Digits(rational.Numerator), Denominator = Digits(rational.Denominator) };
}
