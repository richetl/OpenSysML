using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Google.Protobuf.Collections;

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

    /// <summary>One run of an action, with everything the API reports of it.</summary>
    internal static Wire.ExecuteActionResponse ActionRun(ActionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var rendered = new Wire.ExecuteActionResponse { FinalTime = run.FinalTime };
        Values(rendered.Outputs, run.Outputs);
        rendered.Diagnostics.AddRange(Diagnostics(run.Diagnostics));
        Values(rendered.PerformerAttributes, run.PerformerAttributes);
        rendered.OrderDependent.Add(run.OrderDependent.ToDictionary(StringComparer.Ordinal));
        return rendered;
    }

    /// <summary>One run of a state machine, with its trace and everything else the API reports of it.</summary>
    internal static Wire.ExecuteStateResponse StateRun(StateRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var rendered = new Wire.ExecuteStateResponse { StatesVisited = { run.StatesVisited }, FinalTime = run.FinalTime, TraceDropped = run.TraceDropped };
        Values(rendered.FinalContext, run.FinalContext);
        rendered.Diagnostics.AddRange(Diagnostics(run.Diagnostics));
        rendered.Trace.AddRange(Trace(run.Trace));
        rendered.OrderDependent.Add(run.OrderDependent.ToDictionary(StringComparer.Ordinal));
        return rendered;
    }

    /// <summary>An exploration's outcomes, in the order the API reports them.</summary>
    internal static IEnumerable<Wire.Outcome> Outcomes(Exploration exploration)
    {
        ArgumentNullException.ThrowIfNull(exploration);
        foreach (var outcome in exploration.Outcomes)
        {
            var rendered = new Wire.Outcome
            {
                FinalState = outcome.FinalState,
                StatesVisited = { outcome.StatesVisited },
                Error = outcome.Error,
                Linearizations = outcome.Linearizations,
                Witness = { outcome.Witness },
                Probability = outcome.Probability,
            };
            Values(rendered.Outputs, outcome.Outputs);
            rendered.Diagnostics.AddRange(Diagnostics(outcome.Diagnostics));
            if (outcome.ProbabilityRange is { } range)
            {
                rendered.ProbabilityRange = new Wire.ProbabilityRange { Min = range.Min, Max = range.Max };
            }

            yield return rendered;
        }
    }

    /// <summary>How an exploration ended.</summary>
    internal static Wire.ExplorationStatus ExplorationStatus(Exploration exploration)
    {
        ArgumentNullException.ThrowIfNull(exploration);
        return new Wire.ExplorationStatus
        {
            Complete = exploration.Complete,
            Runs = exploration.Runs,
            BudgetsHit = { exploration.BudgetsHit },
            RunsBudget = exploration.RunsBudget,
            DepthBudget = exploration.DepthBudget,
            ProbabilitiesLowerBound = exploration.ProbabilitiesLowerBound,
            FailedLinearizations = exploration.FailedLinearizations,
            Unenumerated = { exploration.Unenumerated },
        };
    }

    /// <summary>What a check found, with every witness the API reports.</summary>
    internal static Wire.CheckBehaviorResponse CheckResult(CheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var rendered = new Wire.CheckBehaviorResponse
        {
            Verdict = result.Verdict,
            Lines = { result.Lines },
            Claim = result.Claim,
            FreeInputs = { result.FreeInputs },
            Assumptions = { result.Assumptions },
            Witness = Witness(result.Witness),
            Contrast = Witness(result.Contrast),
            Engine = result.Standing.Engine,
            Strength = result.Standing.Strength,
            Reason = result.Reason,
        };
        if (result.Search is { } search)
        {
            rendered.Search = new Wire.CheckSearch
            {
                States = search.States,
                Moves = search.Moves,
                MaxDepth = search.MaxDepth,
                BoundsHit = { search.BoundsHit },
                Horizon = search.Horizon,
                HorizonSet = search.HorizonSet,
                MassBounded = search.MassBounded,
                Notes = { search.Notes },
                Scope = { search.Scope },
                Status = search.Status,
            };
        }

        foreach (var violation in result.Violations)
        {
            rendered.Violations.Add(new Wire.CheckViolation
            {
                Kind = violation.Kind,
                Property = violation.Property,
                Error = violation.Error,
                Depth = violation.Depth,
                Probability = violation.Probability,
                Witness = Witness(violation.Witness),
                Text = violation.Text,
            });
        }

        foreach (var divergence in result.Divergences)
        {
            var rendering = new Wire.CheckDivergence { Feature = divergence.Feature };
            foreach (var value in divergence.Values)
            {
                rendering.Values.Add(new Wire.CheckDivergentValue { Value = value.Value, Witness = Witness(value.Witness) });
            }

            rendered.Divergences.Add(rendering);
        }

        foreach (var final in result.Finals)
        {
            var rendering = new Wire.CheckFinal { Outcome = final.Outcome, Witness = Witness(final.Witness) };
            rendering.Values.Add(final.Values.ToDictionary(StringComparer.Ordinal));
            rendered.Finals.Add(rendering);
        }

        foreach (var bound in result.Standing.Bounds)
        {
            rendered.Bounds.Add(new Wire.Bound { Name = bound.Name, Limit = bound.Limit, Reached = bound.Reached });
        }

        return rendered;
    }

    /// <summary>A run's trace, in the order the API reports it.</summary>
    internal static IEnumerable<Wire.DocumentEvent> Trace(IEnumerable<DocumentEvent> trace)
    {
        foreach (var record in trace)
        {
            yield return Event(record);
        }
    }

    /// <summary>A document value, in the arm of its kind.</summary>
    /// <exception cref="InvalidOperationException">The value is of a kind this renderer does not know.</exception>
    internal static Wire.DocumentValue DocumentValue(DocumentValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            OpenSysML.DocumentValue.ElementValue element => Element(element),
            OpenSysML.DocumentValue.ObjectValue held => new() { Object = Object(held.Value) },
            OpenSysML.DocumentValue.TextValue text => new() { StringValue = text.Text },
            OpenSysML.DocumentValue.NumberValue number => number.Number switch
            {
                OpenSysML.Value.IntegerValue integer => new() { IntValue = integer.Value },
                OpenSysML.Value.BigIntegerValue big => new() { BigIntValue = Digits(big.Value) },
                OpenSysML.Value.RationalValue rational => new() { RationalValue = Terms(rational.Value) },
                OpenSysML.Value.RealValue real => new() { RealValue = real.Value },
                var other => throw new InvalidOperationException($"no rendering of a document number that is a {other.GetType().FullName}"),
            },
            OpenSysML.DocumentValue.BooleanValue boolean => new() { BoolValue = boolean.Value },
            OpenSysML.DocumentValue.InfinityValue => new() { Infinity = true },
            OpenSysML.DocumentValue.QuantityValue quantity => new() { Quantity = Quantity(quantity.Quantity) },
            OpenSysML.DocumentValue.VerdictValue verdict => new() { Verdict = Verdict(verdict.Verdict) },
            OpenSysML.DocumentValue.StateValue state => new() { State = State(state.State) },
            OpenSysML.DocumentValue.EventValue record => new() { Event = Event(record.Event) },
            _ => throw new InvalidOperationException($"no rendering of a {value.GetType().FullName}"),
        };
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

    private static void Values(MapField<string, Wire.Value> field, ValueDictionary<Value> values)
    {
        foreach (var (name, value) in values)
        {
            field.Add(name, Value(value));
        }
    }

    // An element named by no id is the value of no arm, which is how the service sends one.
    private static Wire.DocumentValue Element(DocumentValue.ElementValue element) =>
        element.Id is { } id ? new() { ElementId = id, ElementType = element.ElementType } : new() { ElementType = element.ElementType };

    private static Wire.DocumentValue? OptionalElement(DocumentValue.ElementValue? element) => element is null ? null : Element(element);

    private static Wire.DocumentObject Object(DocumentObject held) =>
        new() { InstanceId = held.InstanceId, Path = held.Path, Element = OptionalElement(held.Element) };

    private static Wire.DocumentVerdict Verdict(DocumentVerdict verdict) =>
        new()
        {
            Assertion = OptionalElement(verdict.Assertion),
            Kind = verdict.Kind,
            Text = verdict.Text,
            Path = verdict.Path,
            Verdict = verdict.Verdict,
            Condition = verdict.Condition,
            Reason = verdict.Reason,
            Verification = { verdict.Verification },
        };

    private static Wire.DocumentState State(DocumentState state) =>
        new()
        {
            Object = state.Subject is { } subject ? Object(subject) : null,
            Machine = state.Machine,
            Name = state.Name,
            StatePath = state.StatePath,
            State = OptionalElement(state.State),
            Region = state.Region,
            Enclosing = { state.Enclosing },
        };

    private static Wire.DocumentEvent Event(DocumentEvent record) =>
        new()
        {
            Kind = record.Kind,
            Time = record.Time is { } time ? DocumentValue(time) : null,
            Object = record.Subject is { } subject ? Object(subject) : null,
            Machine = record.Machine,
            State = record.State,
            From = record.From,
            To = record.To,
            Target = record.Target is { } target ? Object(target) : null,
            Event = record.Event,
            Payload = { record.Payload },
            Alternatives = { record.Alternatives },
            Taken = record.Taken,
            Text = record.Text,
        };

    private static Wire.CheckWitness? Witness(CheckWitness? witness) =>
        witness is null
            ? null
            : new()
            {
                Schedule = witness.Schedule,
                Draws = { witness.Draws },
                Choices = { witness.Choices },
                Inputs = { witness.Inputs },
                FileName = witness.FileName,
                FileContent = witness.FileContent,
            };
}
