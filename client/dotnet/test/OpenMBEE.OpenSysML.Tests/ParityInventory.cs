using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenMBEE.OpenSysML.Tests;

/// <summary>
/// The .NET client's parity inventory, client/dotnet/test/testdata/parity.json: every RPC of the
/// service with the public method that delivers it and the capability gates its request is
/// subject to; the Value and EditOperation arms; the response fields a capability governs; and a
/// classification of every request and response field. <see cref="ParityChecker"/> holds it to
/// the schema the client was generated from and to the conformance scenarios.
/// </summary>
/// <param name="Service">The service's full name.</param>
/// <param name="Rpcs">One row per RPC, in the schema's order.</param>
/// <param name="Values">The arms of sysml.Value.</param>
/// <param name="Edits">The arms of sysml.EditOperation.</param>
/// <param name="Population">Response fields, by descriptor path, mapped to the capability that governs whether the service populates them.</param>
/// <param name="Fields">Every field of every message an RPC reaches, by message and field name.</param>
internal sealed record ParityInventory(
    string Service,
    IReadOnlyList<RpcRow> Rpcs,
    ValueArms Values,
    EditArms Edits,
    IReadOnlyDictionary<string, string> Population,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, FieldClass>> Fields)
{
    /// <summary>The committed inventory's path.</summary>
    internal static string CommittedPath => Repository.PathOf("client", "dotnet", "test", "testdata", "parity.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        Converters = { new FieldClass.Converter() },
    };

    /// <summary>Reads the committed inventory.</summary>
    internal static ParityInventory LoadCommitted() => Parse(File.ReadAllText(CommittedPath));

    /// <summary>Reads an inventory, refusing an unknown or duplicated member and a missing required one.</summary>
    internal static ParityInventory Parse(string json) =>
        JsonSerializer.Deserialize<ParityInventory>(json, Options)
            ?? throw new JsonException("The parity inventory is null.");
}

/// <summary>One RPC: its wire types, the public surface that delivers it, and its request gates.</summary>
/// <param name="Rpc">The bare method name.</param>
/// <param name="Request">The request message's full name.</param>
/// <param name="Response">The response message's full name.</param>
/// <param name="Methods">The public methods that call it, as Owner.MethodAsync.</param>
/// <param name="Input">What those methods take.</param>
/// <param name="Result">What they return.</param>
/// <param name="Area">The area of the client that delivers it.</param>
/// <param name="Status">deferred, or implemented once a public method calls it.</param>
/// <param name="Gates">The capabilities the service requires of the request, in the order it checks them.</param>
/// <param name="ValueFields">The request fields, as dotted paths, whose Values are held to the Value arms' send gates.</param>
/// <param name="Scenarios">Every conformance scenario of the RPC, by ID.</param>
/// <param name="Tests">The tests that prove an implemented row, as Class.Method.</param>
internal sealed record RpcRow(
    string Rpc,
    string Request,
    string Response,
    IReadOnlyList<string> Methods,
    string Input,
    string Result,
    string Area,
    string Status,
    IReadOnlyList<Gate> Gates,
    IReadOnlyList<string> ValueFields,
    IReadOnlyList<string> Scenarios,
    IReadOnlyList<string>? Tests = null);

/// <summary>A capability the service requires, and the request content that makes it required.</summary>
/// <param name="Capability">The capability's name.</param>
/// <param name="When">The predicate, in words; always for an unconditional gate.</param>
/// <param name="Field">The field, as a dotted path from the gated message, the predicate reads.</param>
internal sealed record Gate(string Capability, string When, string? Field = null);

/// <summary>The arms of sysml.Value and how each crosses the wire.</summary>
/// <param name="Message">The message's full name.</param>
/// <param name="Oneof">The oneof holding the arms.</param>
/// <param name="Area">The area of the client that maps them.</param>
/// <param name="SendGateOrder">The send capabilities in the order the service checks them.</param>
/// <param name="Arms">Every arm, by field name.</param>
internal sealed record ValueArms(
    string Message,
    string Oneof,
    string Area,
    IReadOnlyList<string> SendGateOrder,
    IReadOnlyDictionary<string, ValueArm> Arms);

/// <summary>One arm of sysml.Value.</summary>
/// <param name="Kind">The client's name for the kind of value.</param>
/// <param name="Status">deferred, or implemented once it is encoded and decoded.</param>
/// <param name="Send">ungated, response-only, or the capability a request holding it needs.</param>
/// <param name="Receive">The capability under which the service sends it, if any.</param>
/// <param name="Note">Gates its nested content carries.</param>
/// <param name="Test">The test that proves an implemented arm, as Class.Method.</param>
internal sealed record ValueArm(string Kind, string Status, string Send, string? Receive = null, string? Note = null, string? Test = null);

/// <summary>The arms of sysml.EditOperation and the capabilities each needs.</summary>
/// <param name="Message">The message's full name.</param>
/// <param name="Oneof">The oneof holding the arms.</param>
/// <param name="Area">The area of the client that maps them.</param>
/// <param name="Checked">When the service checks the arms' gates.</param>
/// <param name="GateOrder">The arms' capabilities in the order the service checks them.</param>
/// <param name="Arms">Every arm, by field name.</param>
internal sealed record EditArms(
    string Message,
    string Oneof,
    string Area,
    string Checked,
    IReadOnlyList<string> GateOrder,
    IReadOnlyDictionary<string, EditArm> Arms);

/// <summary>One arm of sysml.EditOperation.</summary>
/// <param name="Edit">The client's name for the edit.</param>
/// <param name="Status">deferred, or implemented once it is encoded.</param>
/// <param name="Gates">The capabilities an operation of this arm needs; a field path is relative to the arm's message.</param>
/// <param name="Test">The test that proves an implemented arm, as Class.Method.</param>
internal sealed record EditArm(string Edit, string Status, IReadOnlyList<Gate> Gates, string? Test = null);

/// <summary>
/// How the client treats one field: deferred to the area that will map it, typed by a public API,
/// deliberately left to the raw call (raw-only), or not supported. Written as the bare string
/// "deferred", or as an object with a status, a reason and the test that proves it.
/// </summary>
/// <param name="Status">deferred, typed, raw-only or unsupported.</param>
/// <param name="Reason">Why the field is raw-only or unsupported.</param>
/// <param name="Test">The test that proves the classification, as Class.Method.</param>
internal sealed record FieldClass(string Status, string? Reason = null, string? Test = null)
{
    /// <summary>Reads a classification written as a string or an object.</summary>
    internal sealed class Converter : JsonConverter<FieldClass>
    {
        public override FieldClass Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return new FieldClass(reader.GetString()!);
            }

            string? status = null, reason = null, test = null;
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException($"A field classification is a string or an object, not {reader.TokenType}.");
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var name = reader.GetString();
                reader.Read();
                var value = reader.TokenType == JsonTokenType.String
                    ? reader.GetString()
                    : throw new JsonException($"A field classification's {name} is a string, not {reader.TokenType}.");
                switch (name)
                {
                    case "status" when status is null:
                        status = value;
                        break;
                    case "reason" when reason is null:
                        reason = value;
                        break;
                    case "test" when test is null:
                        test = value;
                        break;
                    default:
                        throw new JsonException($"A field classification has an unknown or repeated member {name}.");
                }
            }

            return new FieldClass(status ?? throw new JsonException("A field classification has no status."), reason, test);
        }

        public override void Write(Utf8JsonWriter writer, FieldClass value, JsonSerializerOptions options) =>
            throw new NotSupportedException("The inventory is written by hand.");
    }
}
