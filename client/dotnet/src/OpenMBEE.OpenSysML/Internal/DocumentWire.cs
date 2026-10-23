using System.Linq;
using Google.Protobuf;
using DocumentKind = OpenMBEE.OpenSysML.Wire.DocumentValue.KindOneofCase;

namespace OpenMBEE.OpenSysML.Internal;

/// <summary>
/// Reads the service's document messages, <c>DocumentValue</c>, <c>DocumentObject</c>,
/// <c>DocumentVerdict</c>, <c>DocumentState</c> and <c>DocumentEvent</c>, into the client's immutable
/// records. A number or a quantity is read by <see cref="ValueWire"/>'s rules; a malformed answer is a
/// <see cref="TransportException"/> naming where it is, and an arm this client does not know is an
/// <see cref="UnsupportedValueException"/>, never an anonymous element.
/// </summary>
internal static class DocumentWire
{
    /// <summary>Reads a document value.</summary>
    /// <param name="value">The message.</param>
    /// <param name="method">The method answered, for an error.</param>
    /// <param name="path">Where the value is in the answer, as <c>trace[0].time</c>.</param>
    internal static DocumentValue Value(Wire.DocumentValue value, string method, string path) => Read(value, method, path, 0);

    /// <summary>Reads a trace record.</summary>
    /// <param name="value">The message.</param>
    /// <param name="method">The method answered, for an error.</param>
    /// <param name="path">Where the record is in the answer, as <c>trace[0]</c>.</param>
    internal static DocumentEvent Event(Wire.DocumentEvent value, string method, string path) => ReadEvent(value, method, path, 0);

    private static DocumentValue Read(Wire.DocumentValue value, string method, string path, int depth)
    {
        if (depth > ValueWire.MaxDepth)
        {
            throw ValueWire.MalformedAnswer(method, path, $"it nests deeper than {ValueWire.MaxDepth} values");
        }

        if (value.KindCase is not (DocumentKind.ElementId or DocumentKind.None) && value.ElementType.Length > 0)
        {
            throw ValueWire.MalformedAnswer(method, path, $"element_type {ValueText.Quoted(value.ElementType)} is set on a {value.KindCase} value, which names no element");
        }

        return value.KindCase switch
        {
            DocumentKind.ElementId => new DocumentValue.ElementValue(value.ElementId, value.ElementType),
            DocumentKind.None => Anonymous(value, method, path),
            DocumentKind.StringValue => new DocumentValue.TextValue(value.StringValue),
            DocumentKind.IntValue => new DocumentValue.NumberValue(new Value.IntegerValue(value.IntValue)),
            DocumentKind.BigIntValue => new DocumentValue.NumberValue(new Value.BigIntegerValue(ValueWire.BigIntegerFromWire(value.BigIntValue, method, path))),
            DocumentKind.RationalValue => new DocumentValue.NumberValue(new Value.RationalValue(ValueWire.RationalFromWire(value.RationalValue, method, path))),
            DocumentKind.RealValue => new DocumentValue.NumberValue(new Value.RealValue(value.RealValue)),
            DocumentKind.BoolValue => new DocumentValue.BooleanValue(value.BoolValue),
            DocumentKind.Infinity => value.Infinity
                ? new DocumentValue.InfinityValue()
                : throw ValueWire.MalformedAnswer(method, path, "the infinity arm states no value unless it is true"),
            DocumentKind.Quantity => new DocumentValue.QuantityValue(ValueWire.FromWire(value.Quantity, method, path + ".quantity")),
            DocumentKind.Object => new DocumentValue.ObjectValue(ReadObject(value.Object, method, path + ".object", depth)),
            DocumentKind.Verdict => new DocumentValue.VerdictValue(ReadVerdict(value.Verdict, method, path + ".verdict", depth)),
            DocumentKind.State => new DocumentValue.StateValue(ReadState(value.State, method, path + ".state", depth)),
            DocumentKind.Event => new DocumentValue.EventValue(ReadEvent(value.Event, method, path + ".event", depth)),
            _ => throw Unsupported(value, method, path),
        };
    }

    // A value of no known arm: an anonymous element named by its type alone, unless it carries
    // fields this client does not know, which are a newer arm rather than no element.
    private static DocumentValue.ElementValue Anonymous(Wire.DocumentValue value, string method, string path) =>
        new Wire.DocumentValue { ElementType = value.ElementType }.CalculateSize() == value.CalculateSize()
            ? new DocumentValue.ElementValue(null, value.ElementType)
            : throw Unsupported(value, method, path);

    // A field the schema says holds an element value: one naming an element, or none.
    private static DocumentValue.ElementValue? Element(Wire.DocumentValue? value, string method, string path, int depth) => value is null
        ? null
        : Read(value, method, path, depth + 1) as DocumentValue.ElementValue
            ?? throw ValueWire.MalformedAnswer(method, path, $"it is a {value.KindCase} value where the schema holds an element");

    private static DocumentObject ReadObject(Wire.DocumentObject value, string method, string path, int depth) =>
        new(value.InstanceId, value.Path, Element(value.Element, method, path + ".element", depth));

    private static DocumentVerdict ReadVerdict(Wire.DocumentVerdict value, string method, string path, int depth) =>
        new(
            Element(value.Assertion, method, path + ".assertion", depth),
            value.Kind,
            value.Text,
            value.Path,
            value.Verdict,
            value.Condition,
            value.Reason,
            value.Verification);

    private static DocumentState ReadState(Wire.DocumentState value, string method, string path, int depth) =>
        new(
            value.Object is { } held ? ReadObject(held, method, path + ".object", depth + 1) : null,
            value.Machine,
            value.Name,
            value.StatePath,
            Element(value.State, method, path + ".state", depth),
            value.Region,
            value.Enclosing);

    private static DocumentEvent ReadEvent(Wire.DocumentEvent value, string method, string path, int depth) =>
        new(
            value.Kind,
            value.Time is { } time ? Read(time, method, path + ".time", depth + 1) : null,
            value.Object is { } held ? ReadObject(held, method, path + ".object", depth + 1) : null,
            value.Machine,
            value.State,
            value.From,
            value.To,
            value.Target is { } target ? ReadObject(target, method, path + ".target", depth + 1) : null,
            value.Event,
            value.Payload.ToList(),
            value.Alternatives.ToList(),
            value.Taken,
            value.Text);

    private static UnsupportedValueException Unsupported(Wire.DocumentValue value, string method, string path) =>
        new($"The service answered a document value at {path} of a kind this client does not know; a newer client may read it.", method, path, value.ToByteArray());
}
