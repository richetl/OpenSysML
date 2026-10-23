using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using OpenMBEE.OpenSysML.Conformance;
using Xunit;

namespace OpenMBEE.OpenSysML.Tests;

/// <summary>
/// The conformance adapters answer a scenario through the public typed API: the request the typed call
/// sends is the one the scenario decoded, the answer rendered is the complete answer the service gave,
/// a failure reported in band is rendered in band, a refusal keeps its status, a request the API cannot
/// express is skipped with its stable reason and sends nothing, and nothing reaches the service raw.
/// </summary>
[Trait(Category.Name, Category.Unit)]
public sealed class ConformanceAdapterTests
{
    private const string Hash = "h";

    public static TheoryData<ClientWire> Wires => ClientWires.All;

    /// <summary>For each model RPC, a request setting every field the API can send and an answer setting every field it reads.</summary>
    public static TheoryData<ClientWire, string> Exchanges()
    {
        var data = new TheoryData<ClientWire, string>();
        foreach (var wire in new[] { ClientWire.Grpc, ClientWire.ConnectProto, ClientWire.ConnectJson })
        {
            foreach (var name in Complete.Keys)
            {
                data.Add(wire, name);
            }
        }

        return data;
    }

    public static TheoryData<ClientWire, string> Failures()
    {
        var data = new TheoryData<ClientWire, string>();
        foreach (var wire in new[] { ClientWire.Grpc, ClientWire.ConnectProto, ClientWire.ConnectJson })
        {
            foreach (var name in InBand.Keys)
            {
                data.Add(wire, name);
            }
        }

        return data;
    }

    public static TheoryData<string, string, string> UnexpressibleRequests => new()
    {
        { "ParseFile", "{}", "the public API always names a source, so it cannot send a request naming none" },
        { "ParseFile", """{"file_path": "/m/a.sysml", "language": "kerml"}""", "the public API reads a file's notation from its extension, so it cannot name a language for one" },
        { "ParseFile", """{"file_path": ""}""", "the public API names a file by a path that is not empty" },
        { "ParseFile", """{"content": "package P;", "language": "fortran"}""", "the public API names a language as a SourceLanguage, which has none spelled \"fortran\"" },
        { "ParseFile", """{"content": "package P;", "content_hash": "abc"}""", "the public API has no way to send sysml.ParseFileRequest.content_hash" },
        { "ParseSources", """{"documents": [{}]}""", "the public API always names a document's source, so it cannot send a document naming none" },
        { "ParseSources", """{"documents": [{"file_path": "/m/a.sysml", "name": "a"}]}""", "the public API names a file document by its path alone, so it cannot give one a name or a language" },
        { "ParseSources", """{"documents": [{"file_path": ""}]}""", "the public API names a file document by a path that is not empty" },
        { "GetSymbol", """{"model_hash": " ", "symbol_id": "Demo::Vehicle"}""", "the public API names a model by a hash that is not blank" },
        { "Evaluate", """{"expression": "2 + 2"}""", "the public API names a model by a hash that is not blank" },
        { "SelfCheck", """{"model_hash": "h"}""", "the public API does not cover SelfCheck" },
        { "VerifyConstraint", """{"symbol_id": "C"}""", "the public API names a model by a hash that is not blank" },
        { "EvaluateCalc", """{"model_hash": "h", "arguments": [{"function": {"calc_id": "F::Scaler::scale", "self_id": 3}}]}""", "The value at arguments[0] is the function F::Scaler::scale closing over instance(3), which lives only within the response that held it." },
        { "EvaluateCalc", """{"model_hash": "h", "arguments": [{"measurement_ref": {"unit": "m"}}]}""", "the public API holds a measurement reference with its reduction, which arguments[0] has none of" },
        { "VerifyRequirement", """{"model_hash": "h", "named_arguments": {"x": {"undetermined": {}}}}""", "The value at named_arguments[\"x\"] is undetermined, which is a result the model leaves open, never an argument." },
        { "RunSweep", """{"model_hash": "h", "ranges": [{"parameter": "b", "end": {"int_value": 1}}]}""", "the public API holds a sweep range with both endpoints, which ranges[0] does not state" },
        { "RunAnalysis", """{"model_hash": "h", "schedule": "explore", "tool_dry_run": true}""", "AnalysisOptions.ToolDryRun previews one run of the case, which an exploration makes none of; leave it false." },
        { "ExecuteAction", """{"model_hash": "h", "inputs": {"x": {"unset": true}}}""", "The value at inputs[\"x\"] is unset, which is what a feature with no value reads as, never an argument; leave the argument out instead." },
        { "ExecuteAction", """{"model_hash": "h", "inputs": {"x": {"big_int_value": "+9223372036854775808"}}}""", "the public API writes an integer in canonical digits, which inputs[\"x\"]'s \"+9223372036854775808\" is not" },
        { "ExecuteAction", """{"model_hash": "h", "inputs": {"x": {"rational_value": {"numerator": "2", "denominator": "4"}}}}""", "the public API writes a rational in lowest terms over a positive denominator, which inputs[\"x\"]'s 2/4 is not" },
        { "ExecuteAction", """{"model_hash": "h", "inputs": {"x": {}}}""", "the public API holds no value as inputs[\"x\"] spells it (None)" },
        { "ExecuteAction", """{"model_hash": "h", "inputs": {"x": {"undetermined": {"reason": "open"}}}}""", "The value at inputs[\"x\"] is undetermined, which is a result the model leaves open, never an argument." },
        { "ExecuteAction", """{"model_hash": "h", "inputs": {"x": {"sequence": {"elements": [{"quantity": {"unit": "m"}}]}}}}""", "the public API holds a quantity with its magnitude, which inputs[\"x\"].sequence[0].quantity has none of" },
        { "CheckBehavior", """{"model_hash": "h", "action_symbol_id": "A", "inputs": {"x": {"vector": {"components": [{"bool_value": true}]}}}}""", "the public API holds a vector's components as numbers, which inputs[\"x\"].vector[0] is not" },
        { "ExecuteState", """{"model_hash": "h", "schedule": "explore", "trace": true}""", "RunOptions.Trace asks for one run's trace, which an exploration describes none of; leave it false." },
        { "CheckBehavior", """{"model_hash": "h", "action_symbol_id": "A", "state_machine_symbol_id": "S"}""", "the public API checks one behavior, so it cannot name both an action and a state machine" },
        { "CheckBehavior", """{"model_hash": "h", "state_machine_symbol_id": "S", "inputs": {"x": {"int_value": 1}}}""", "the public API binds inputs only to an action it checks, so it cannot send them with a state machine" },
    };

    private static Dictionary<string, (IMessage Request, IMessage Response)> Complete => new(StringComparer.Ordinal)
    {
        ["ParseFile"] = (
            new Wire.ParseFileRequest { Content = "package P;", Language = "kerml", StrictConformance = true },
            new Wire.ParseFileResponse { ModelHash = Hash, Root = CompleteWire.Symbol, Diagnostics = { CompleteWire.Diagnostic, CompleteWire.BareDiagnostic } }),
        ["ParseFile from a file"] = (
            new Wire.ParseFileRequest { FilePath = "/models/a.sysml", StrictConformance = true },
            new Wire.ParseFileResponse { ModelHash = Hash, Root = CompleteWire.Symbol }),
        ["ParseSources"] = (
            new Wire.ParseSourcesRequest
            {
                Documents =
                {
                    new Wire.SourceDocument { Content = "package A;", Name = "a.sysml", Language = "sysml" },
                    new Wire.SourceDocument { Content = "package B;" },
                    new Wire.SourceDocument { FilePath = "/models/c.sysml" },
                },
                StrictConformance = true,
                BaseModelHash = "base",
            },
            new Wire.ParseSourcesResponse
            {
                ModelHash = Hash,
                Roots = { CompleteWire.Symbol, new Wire.SymbolInfo { Kind = "RootNamespace" } },
                Diagnostics = { CompleteWire.Diagnostic },
                Affected = { "a.sysml", "/models/c.sysml" },
            }),
        ["GetSymbol"] = (
            new Wire.GetSymbolRequest { ModelHash = Hash, SymbolId = "Demo::Vehicle" },
            new Wire.SymbolResponse { Symbol = CompleteWire.Symbol }),
        ["GetDiagnostics"] = (
            new Wire.DiagnosticsRequest { ModelHash = Hash, DisableLint = { "undeclared-signal" }, EnableLint = { "rounded-real-literal" } },
            new Wire.DiagnosticsResponse { Diagnostics = { CompleteWire.Diagnostic, CompleteWire.BareDiagnostic } }),
        ["Evaluate"] = (
            new Wire.EvaluateRequest { ModelHash = Hash, Expression = "mass", ContextSymbolId = "Demo::Vehicle", SubjectSymbolId = "Demo::sedan" },
            new Wire.EvaluateResponse
            {
                Result = new Wire.Value { Sequence = new Wire.ValueSequence { Elements = { CompleteWire.Values } } },
                Diagnostics = { CompleteWire.Diagnostic },
            }),
        ["Instantiate"] = (
            new Wire.InstantiateRequest { ModelHash = Hash, SymbolId = "Demo::Vehicle" },
            new Wire.InstantiateResponse
            {
                Instance = CompleteWire.Instance,
                Instances = { CompleteWire.Instance, new Wire.Instance { Id = 10, TypeSymbolId = "Demo::Engine" } },
                Diagnostics = { CompleteWire.Diagnostic },
            }),
        ["ExecuteAction"] = (
            new Wire.ExecuteActionRequest
            {
                ModelHash = Hash,
                ActionSymbolId = "Test::addFive",
                Inputs = { SendableValues },
                Schedule = "declared",
                PerformerSymbolId = "Plant::tank",
                Advance = 3,
                Seed = 42,
                Draws = "min",
                ClockStep = 0.25,
            },
            CompleteExecutionWire.ActionRun),
        ["ExecuteAction exploring"] = (
            new Wire.ExecuteActionRequest { ModelHash = Hash, ActionSymbolId = "Test::race", Schedule = "explore:runs=8,depth=4", Advance = 0 },
            CompleteExecutionWire.ActionExploration),
        ["ExecuteState"] = (
            new Wire.ExecuteStateRequest
            {
                ModelHash = Hash,
                StateMachineSymbolId = "Test::Machine",
                Events = { "go", "stop" },
                Schedule = "seed:3",
                PerformerSymbolId = "Test::box",
                Trace = true,
                Advance = 1,
                Seed = 7,
                Draws = "random",
                ClockStep = 2,
            },
            CompleteExecutionWire.StateRun),
        ["ExecuteState exploring"] = (
            new Wire.ExecuteStateRequest { ModelHash = Hash, StateMachineSymbolId = "Test::Machine", Events = { "go" }, Schedule = "explore" },
            CompleteExecutionWire.StateExploration),
        ["CheckBehavior"] = (
            new Wire.CheckBehaviorRequest
            {
                ModelHash = Hash,
                ActionSymbolId = "Plant::Tank::fill",
                Inputs = { ["x"] = new Wire.Value { IntValue = 3 } },
                PerformerSymbolId = "Plant::tank",
                CheckProperty = { "Plant::Tank::low" },
                CheckDiverge = { "this.level" },
                CheckInput = { "x" },
                CheckAssume = { "Plant::Tank::capped" },
                CheckWitness = true,
                CheckDepth = -1,
                CheckStates = 10,
                CheckUnroll = 2,
                CheckTimeout = 0.5,
                Engine = "explore",
            },
            CompleteExecutionWire.Check),
        ["CheckBehavior of a state machine"] = (
            new Wire.CheckBehaviorRequest { ModelHash = Hash, StateMachineSymbolId = "Test::Machine", CheckProperty = { "Test::ok" } },
            new Wire.CheckBehaviorResponse { Verdict = "holds", Engine = "check" }),
        ["VerifyConstraint"] = (
            new Wire.VerifyConstraintRequest
            {
                ModelHash = Hash,
                SymbolId = "Demo::Vehicle::massLight",
                SubjectSymbolId = "Demo::sedan",
                Engine = "explore",
                Question = "holds",
                Arguments = { SendableValues.Values },
                NamedArguments = { SendableValues },
            },
            CompleteVerificationWire.Constraint),
        ["VerifyRequirement"] = (
            new Wire.VerifyRequirementRequest
            {
                ModelHash = Hash,
                SymbolId = "Demo::bounded",
                SubjectSymbolId = "Demo::sedan",
                Engine = "all",
                Question = "bogus",
                Arguments = { SendableValues.Values },
                NamedArguments = { SendableValues },
            },
            CompleteVerificationWire.Requirement),
        ["VerifySatisfaction"] = (
            new Wire.VerifySatisfactionRequest { ModelHash = Hash, SymbolId = "Demo::checks", Engine = "auto", Question = "satisfiable" },
            CompleteVerificationWire.Satisfaction),
        ["VerifySatisfaction of every assertion"] = (
            new Wire.VerifySatisfactionRequest { ModelHash = Hash },
            new Wire.VerifySatisfactionResponse()),
        ["ValidateInstance"] = (
            new Wire.ValidateInstanceRequest { ModelHash = Hash, SymbolId = "Demo::sedan", Engine = "run" },
            CompleteVerificationWire.Validation),
        ["EvaluateCalc"] = (
            new Wire.EvaluateCalcRequest { ModelHash = Hash, SymbolId = "Demo::add", Arguments = { SendableValues.Values }, Engine = "run" },
            CompleteVerificationWire.Calculation),
        ["RunAnalysis"] = (
            new Wire.RunAnalysisRequest
            {
                ModelHash = Hash,
                SymbolId = "An::CostAnalysis",
                SubjectSymbolId = "An::barge",
                Arguments = { SendableValues.Values },
                NamedArguments = { SendableValues },
                Schedule = "declared",
                Engine = "run",
                ToolDryRun = true,
            },
            CompleteVerificationWire.Analysis),
        ["RunAnalysis exploring"] = (
            new Wire.RunAnalysisRequest { ModelHash = Hash, SymbolId = "An::race", Engine = " explore " },
            CompleteVerificationWire.Exploration),
        ["RunSweep"] = (
            new Wire.RunSweepRequest
            {
                ModelHash = Hash,
                SymbolId = "Sw::CostAnalysis",
                SubjectSymbolId = "Sw::barge",
                Arguments = { SendableValues.Values },
                NamedArguments = { SendableValues },
                Ranges =
                {
                    new Wire.SweepRange { Parameter = "limit", Start = new Wire.Value { RealValue = 0 }, End = new Wire.Value { RealValue = 40 }, Step = new Wire.Value { RealValue = 20 } },
                    new Wire.SweepRange { Start = new Wire.Value { IntValue = -1 }, End = new Wire.Value { Quantity = new Wire.Quantity { IntMagnitude = 1 } } },
                },
                Samples = -2,
                Seed = ulong.MaxValue,
                Engine = "sweep",
            },
            CompleteVerificationWire.Sweep),
        ["ListEngines"] = (
            new Wire.ListEnginesRequest { Probe = true },
            CompleteVerificationWire.Engines),
        ["CheckBehavior of no behavior"] = (
            new Wire.CheckBehaviorRequest { ModelHash = Hash },
            new Wire.CheckBehaviorResponse { Verdict = "undecided" }),
    };

    // Every value a request can send, one of each arm the API writes as given, by name.
    private static Dictionary<string, Wire.Value> SendableValues => CompleteWire.Values
        .Where(value => value.KindCase is not (Wire.Value.KindOneofCase.Unset or Wire.Value.KindOneofCase.Undetermined or Wire.Value.KindOneofCase.Null)
            && value.Function?.SelfId is null or 0)
        .Append(new Wire.Value { Null = string.Empty })
        .Append(new Wire.Value { Function = new Wire.Function { CalcId = "F::Sq" } })
        .Select((value, i) => (Name: $"v{i:D2}", Value: value))
        .ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.Ordinal);

    private static Dictionary<string, (IMessage Request, IMessage Response)> InBand => new(StringComparer.Ordinal)
    {
        ["ParseFile"] = (
            new Wire.ParseFileRequest { Content = "package P;" },
            new Wire.ParseFileResponse { Error = "the source could not be read", Diagnostics = { CompleteWire.Diagnostic } }),
        ["ParseSources"] = (
            new Wire.ParseSourcesRequest { Documents = { new Wire.SourceDocument { Content = "package A;", Name = "a.sysml" } } },
            new Wire.ParseSourcesResponse { Error = "the sources could not be read", Diagnostics = { CompleteWire.Diagnostic } }),
        ["GetSymbol"] = (
            new Wire.GetSymbolRequest { ModelHash = Hash, SymbolId = "Demo::Nope" },
            new Wire.SymbolResponse { Error = "symbol not found: Demo::Nope" }),
        ["GetDiagnostics"] = (
            new Wire.DiagnosticsRequest { ModelHash = Hash },
            new Wire.DiagnosticsResponse { Error = "the lints did not run", Diagnostics = { CompleteWire.Diagnostic } }),
        ["Evaluate"] = (
            new Wire.EvaluateRequest { ModelHash = Hash, Expression = "invalid syntax (((" },
            new Wire.EvaluateResponse { Error = "unexpected trailing text", Diagnostics = { CompleteWire.Diagnostic } }),
        ["Instantiate"] = (
            new Wire.InstantiateRequest { ModelHash = Hash, SymbolId = "Test::DoesNotExist" },
            new Wire.InstantiateResponse { Error = "symbol not found: Test::DoesNotExist", Diagnostics = { CompleteWire.Diagnostic } }),
        ["ExecuteAction"] = (
            new Wire.ExecuteActionRequest { ModelHash = Hash, ActionSymbolId = "Timed::delayed", Advance = 2 },
            CompleteExecutionWire.FailedAction),
        ["ExecuteState"] = (
            new Wire.ExecuteStateRequest { ModelHash = Hash, StateMachineSymbolId = "Test::Machine", Trace = true },
            CompleteExecutionWire.FailedState),
        ["CheckBehavior"] = (
            new Wire.CheckBehaviorRequest { ModelHash = Hash, ActionSymbolId = "Plant::Tank::drain" },
            CompleteExecutionWire.FailedCheck),
        ["VerifyConstraint"] = (
            new Wire.VerifyConstraintRequest { ModelHash = Hash, SymbolId = "Demo::nope" },
            new Wire.VerifyConstraintResponse { Error = "symbol not found: Demo::nope", Instances = { CompleteWire.Instance }, Diagnostics = { CompleteWire.Diagnostic } }),
        ["VerifyRequirement"] = (
            new Wire.VerifyRequirementRequest { ModelHash = Hash, SymbolId = "Demo::nope" },
            new Wire.VerifyRequirementResponse
            {
                Error = "symbol not found: Demo::nope",
                Instances = { CompleteWire.Instance },
                Diagnostics = { CompleteWire.Diagnostic },
                VerificationVerdicts = { CompleteVerificationWire.Verification },
            }),
        ["VerifySatisfaction"] = (
            new Wire.VerifySatisfactionRequest { ModelHash = Hash, SymbolId = "Demo" },
            new Wire.VerifySatisfactionResponse
            {
                Error = "Demo states no satisfaction assertion",
                FailureReason = Wire.FailureReason.WrongKind,
                Instances = { CompleteWire.Instance },
                Diagnostics = { CompleteWire.Diagnostic },
                VerificationVerdicts = { CompleteVerificationWire.Verification },
            }),
        ["ValidateInstance"] = (
            new Wire.ValidateInstanceRequest { ModelHash = Hash, SymbolId = "Demo" },
            new Wire.ValidateInstanceResponse
            {
                Error = "Demo declares no object",
                FailureReason = Wire.FailureReason.WrongKind,
                Instances = { CompleteWire.Instance },
                Diagnostics = { CompleteWire.Diagnostic },
                VerificationVerdicts = { CompleteVerificationWire.Verification },
            }),
        ["EvaluateCalc"] = (
            new Wire.EvaluateCalcRequest { ModelHash = Hash, SymbolId = "Demo::sedan" },
            new Wire.EvaluateCalcResponse
            {
                Error = "Demo::sedan declares no calc",
                FailureReason = Wire.FailureReason.WrongKind,
                Diagnostics = { CompleteWire.Diagnostic },
                Engine = "run",
                Strength = "not covered",
                Bounds = { CompleteVerificationWire.Bounds },
            }),
        ["RunAnalysis"] = (
            new Wire.RunAnalysisRequest { ModelHash = Hash, SymbolId = "An::CostAnalysis" },
            CompleteVerificationWire.FailedAnalysis),
        ["RunSweep"] = (
            new Wire.RunSweepRequest { ModelHash = Hash, SymbolId = "Sw::barge" },
            new Wire.RunSweepResponse
            {
                Error = "Sw::barge declares neither an analysis case nor a calc",
                FailureReason = Wire.FailureReason.Evaluation,
                Instances = { CompleteWire.Instance },
                Diagnostics = { CompleteWire.Diagnostic },
                Engine = "sweep",
                Strength = "observed",
                Bounds = { CompleteVerificationWire.Bounds },
            }),
    };

    [Theory]
    [MemberData(nameof(Exchanges))]
    public async Task EachRpcSendsTheScenarioRequestThroughItsTypedCallAndRendersTheWholeAnswer(ClientWire wire, string name)
    {
        var (request, response) = Complete[name];
        var method = ScenarioRequests.Method(name.Split(' ')[0]);
        using var handler = Serving(wire, method.Name, response);
        await using var connection = await FakeConnection.OpenAsync(handler);

        var answer = await Client(connection).CallAsync(method, request, TestContext.Current.CancellationToken);

        Assert.Equal(new ScenarioAnswer.Answered(response), answer);
        var sent = Assert.Single(handler.RequestsFor(method.Name));
        Assert.Equal(request, Decoded(sent, method.InputType));
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailureTheAnswerReportsIsRenderedInBand(ClientWire wire, string name)
    {
        var (request, response) = InBand[name];
        var method = ScenarioRequests.Method(name);
        using var handler = Serving(wire, method.Name, response);
        await using var connection = await FakeConnection.OpenAsync(handler);

        var answer = await Client(connection).CallAsync(method, request, TestContext.Current.CancellationToken);

        Assert.Equal(new ScenarioAnswer.Answered(response), answer);
    }

    [Theory]
    [MemberData(nameof(Wires))]
    public async Task ARefusalKeepsTheStatusAndMessageTheServiceGave(ClientWire wire)
    {
        using var handler = new FakeServiceHandler(wire) { Capabilities = FakeModels.ModelCapabilities }
            .Refusing("Evaluate", StatusCode.NotFound, "model not found: h")
            .Refusing("GetDiagnostics", StatusCode.InvalidArgument, "unknown lint code no-such-lint")
            .Refusing("ParseFile", StatusCode.NotFound, "open /nonexistent: no such file");
        await using var connection = await FakeConnection.OpenAsync(handler);
        var client = Client(connection);
        var token = TestContext.Current.CancellationToken;

        var evaluate = await client.CallAsync(ScenarioRequests.Method("Evaluate"), new Wire.EvaluateRequest { ModelHash = Hash, Expression = "1" }, token);
        var lint = await client.CallAsync(ScenarioRequests.Method("GetDiagnostics"), new Wire.DiagnosticsRequest { ModelHash = Hash, DisableLint = { "no-such-lint" } }, token);
        var file = await client.CallAsync(ScenarioRequests.Method("ParseFile"), new Wire.ParseFileRequest { FilePath = "/nonexistent" }, token);

        Assert.Equal(new ScenarioAnswer.Refused(StatusCode.NotFound, "model not found: h"), evaluate);
        Assert.Equal(new ScenarioAnswer.Refused(StatusCode.InvalidArgument, "unknown lint code no-such-lint"), lint);
        Assert.Equal(new ScenarioAnswer.Refused(StatusCode.NotFound, "open /nonexistent: no such file"), file);
    }

    [Theory]
    [MemberData(nameof(Wires))]
    public async Task ACapabilityTheServiceLacksIsRefusedAsUnimplementedBeforeAnythingIsSent(ClientWire wire)
    {
        using var handler = new FakeServiceHandler(wire) { Capabilities = [] };
        await using var connection = await FakeConnection.OpenAsync(handler);
        var client = Client(connection);
        var token = TestContext.Current.CancellationToken;

        var strict = await client.CallAsync(ScenarioRequests.Method("ParseFile"), new Wire.ParseFileRequest { Content = "package P;", StrictConformance = true }, token);
        var subject = await client.CallAsync(
            ScenarioRequests.Method("Evaluate"), new Wire.EvaluateRequest { ModelHash = Hash, Expression = "mass", SubjectSymbolId = "Demo::sedan" }, token);
        var sources = await client.CallAsync(
            ScenarioRequests.Method("ParseSources"), new Wire.ParseSourcesRequest { Documents = { new Wire.SourceDocument { Content = "package A;" } } }, token);

        Assert.All([strict, subject, sources], answer => Assert.Equal(StatusCode.Unimplemented, Assert.IsType<ScenarioAnswer.Refused>(answer).Status));
        Assert.Contains(Capabilities.StrictConformance, ((ScenarioAnswer.Refused)strict).Message, StringComparison.Ordinal);
        Assert.Equal(["GetServerInfo"], handler.Requests.Select(request => request.Method));
    }

    [Theory]
    [MemberData(nameof(Wires))]
    public async Task AnAnswerOfNeitherResultNorFailureIsTheTransportsInternalStatus(ClientWire wire)
    {
        using var handler = Serving(wire, "Evaluate", new Wire.EvaluateResponse());
        await using var connection = await FakeConnection.OpenAsync(handler);

        var answer = await Client(connection).CallAsync(
            ScenarioRequests.Method("Evaluate"), new Wire.EvaluateRequest { ModelHash = Hash, Expression = "1" }, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCode.Internal, Assert.IsType<ScenarioAnswer.Refused>(answer).Status);
    }

    [Theory]
    [MemberData(nameof(UnexpressibleRequests))]
    public async Task ARequestTheApiCannotExpressIsSkippedForItsReasonAndSendsNothing(string rpc, string json, string reason)
    {
        var method = ScenarioRequests.Method(rpc);
        using var handler = new FakeServiceHandler { Capabilities = FakeModels.ModelCapabilities };
        await using var connection = await FakeConnection.OpenAsync(handler);

        var answer = await Client(connection).CallAsync(method, JsonParser.Default.Parse(json, method.InputType), TestContext.Current.CancellationToken);

        Assert.Equal(new ScenarioAnswer.Unexpressible(reason), answer);
        Assert.Equal(["GetServerInfo"], handler.Requests.Select(request => request.Method));
    }

    [Fact]
    public async Task AValueThePublicTypesRefuseAsSpelledIsUnexpressibleForTheirReason()
    {
        var method = ScenarioRequests.Method("ExecuteAction");
        using var handler = new FakeServiceHandler { Capabilities = FakeModels.ModelCapabilities };
        await using var connection = await FakeConnection.OpenAsync(handler);
        var request = JsonParser.Default.Parse("""{"model_hash": "h", "inputs": {"x": {"array": {"dimensions": [3], "elements": [{"int_value": 1}]}}}}""", method.InputType);

        var answer = await Client(connection).CallAsync(method, request, TestContext.Current.CancellationToken);

        var reason = Assert.IsType<ScenarioAnswer.Unexpressible>(answer).Reason;
        Assert.StartsWith("the public API cannot hold inputs[\"x\"] as spelled: An array", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("(Parameter", reason, StringComparison.Ordinal);
        Assert.Equal(["GetServerInfo"], handler.Requests.Select(sent => sent.Method));
    }

    [Theory]
    [MemberData(nameof(Wires))]
    public async Task ServerInfoIsWhatTheConnectionKeptWhenItOpened(ClientWire wire)
    {
        using var handler = new FakeServiceHandler(wire) { Version = "v1.2.3", Capabilities = ["query", "convert", "apply_edits"] };
        using var silent = new FakeServiceHandler(wire)
        {
            ServerInfo = (_, _) => Task.FromResult(FakeServiceHandler.RefuseOn(wire, StatusCode.Unimplemented, "unknown method")),
        };
        await using var connection = await FakeConnection.OpenAsync(handler);
        await using var old = await FakeConnection.OpenAsync(silent);
        var method = ScenarioRequests.Method("GetServerInfo");
        var token = TestContext.Current.CancellationToken;

        var answer = await Client(connection).CallAsync(method, new Wire.ServerInfoRequest(), token);
        var refused = await Client(old).CallAsync(method, new Wire.ServerInfoRequest(), token);

        Assert.Equal(
            new ScenarioAnswer.Answered(new Wire.ServerInfoResponse { Version = "v1.2.3", Capabilities = { "apply_edits", "convert", "query" } }),
            answer);
        Assert.Equal(StatusCode.Unimplemented, Assert.IsType<ScenarioAnswer.Refused>(refused).Status);
        Assert.Single(handler.RequestsFor("GetServerInfo"));
    }

    [Theory]
    [MemberData(nameof(Wires))]
    public async Task AScenarioModelIsParsedAsOneFileOrAsNamedDocuments(ClientWire wire)
    {
        using var handler = new FakeServiceHandler(wire) { Capabilities = FakeModels.ModelCapabilities }
            .On("ParseFile", Wire.ParseFileRequest.Parser, _ => new Wire.ParseFileResponse { ModelHash = "one", Diagnostics = { CompleteWire.Diagnostic } })
            .On("ParseSources", Wire.ParseSourcesRequest.Parser, _ => new Wire.ParseSourcesResponse { Error = "cannot", Diagnostics = { CompleteWire.BareDiagnostic } });
        await using var connection = await FakeConnection.OpenAsync(handler);
        var client = Client(connection);
        var token = TestContext.Current.CancellationToken;

        var single = await client.ParseAsync(new ScenarioModel("a.sysml", [], "kerml", true), [new FixtureSource("a.sysml", "package A;")], token);
        var several = await client.ParseAsync(
            new ScenarioModel("", ["a.sysml", "b.sysml"], "", false), [new FixtureSource("a.sysml", "package A;"), new FixtureSource("b.sysml", "package B;")], token);

        Assert.Equal(("one", ""), (single.Hash, single.Error));
        Assert.Equal([new ModelParseDiagnostic("warning", "rounded")], single.Diagnostics);
        Assert.Equal(("", "cannot"), (several.Hash, several.Error));
        Assert.Equal([new ModelParseDiagnostic("hint", "consider")], several.Diagnostics);
        Assert.Equal(
            new Wire.ParseFileRequest { Content = "package A;", Language = "kerml", StrictConformance = true },
            Assert.Single(handler.RequestsFor("ParseFile")).Message(Wire.ParseFileRequest.Parser));
        Assert.Equal(
            new Wire.ParseSourcesRequest
            {
                Documents = { new Wire.SourceDocument { Content = "package A;", Name = "a.sysml" }, new Wire.SourceDocument { Content = "package B;", Name = "b.sysml" } },
            },
            Assert.Single(handler.RequestsFor("ParseSources")).Message(Wire.ParseSourcesRequest.Parser));
    }

    [Fact]
    public void EveryAdapterReadsItsRpcsRequestAndSendsOnlyFieldsTheInventoryTypes()
    {
        var inventory = ParityInventory.LoadCommitted();
        foreach (var (name, adapter) in ApiAdapters.All)
        {
            var method = ScenarioRequests.Method(name);
            var fields = inventory.Fields[method.InputType.FullName];

            Assert.Equal(method.InputType.ClrType, adapter.RequestType);
            Assert.Equal(
                fields.Where(field => field.Value.Status == "typed").Select(field => field.Key).Order(StringComparer.Ordinal),
                adapter.Fields.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void TheAdaptersCoverEveryImplementedRpcAndTheExpectedSkipsListEveryDeferredOne()
    {
        var rows = ParityInventory.LoadCommitted().Rpcs;
        var expected = ExpectedSkips.Load(Repository.PathOf("client", "dotnet", "test", "testdata", "expected-skips.json"));

        Assert.Equal(
            rows.Where(row => row.Status == "implemented").Select(row => row.Rpc).Order(StringComparer.Ordinal),
            ApiAdapters.All.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            rows.Where(row => row.Status == "deferred").Select(row => row.Rpc).Order(StringComparer.Ordinal),
            expected.UncoveredRpcs.Order(StringComparer.Ordinal));
        Assert.Empty(expected.Problems(ScenarioSuite.Load(Repository.PathOf("conformance", "scenarios")), ScenarioRequests.Service, ApiAdapters.All.Keys));
    }

    [Fact]
    public void NothingInTheRunnerReachesTheServiceRaw()
    {
        var raw = new HashSet<MethodBase>(typeof(Connection).GetMethods().Where(method => method.Name == nameof(Connection.CallAsync)));
        var stub = typeof(Wire.SysMLService.SysMLServiceClient);
        var calls = new List<string>();
        foreach (var type in typeof(ApiScenarioClient).Assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)))
            {
                foreach (var called in Called(method))
                {
                    var definition = called is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericMethodDefinition() : called;
                    if (raw.Contains(definition) || called.DeclaringType == stub)
                    {
                        calls.Add($"{type.FullName}.{method.Name} calls {called.DeclaringType}.{called.Name}");
                    }
                }
            }
        }

        Assert.Empty(calls);
        // The scan is not blind: it finds the typed call an adapter does make.
        var evaluate = typeof(ModelArea).GetMethod("EvaluateAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()!.StateMachineType
            .GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Contains(Called(evaluate), called => called.DeclaringType == typeof(Model) && called.Name == nameof(Model.EvaluateAsync));
    }

    private static ApiScenarioClient Client(Connection connection) => new("test", "fake", connection);

    private static FakeServiceHandler Serving(ClientWire wire, string method, IMessage response)
    {
        var handler = new FakeServiceHandler(wire) { Capabilities = FakeModels.ModelCapabilities };
        handler.Respond = (sent, _) => Task.FromResult(sent.Method == method ? handler.Answer(response) : handler.Refuse(StatusCode.Unimplemented, "not faked"));
        return handler;
    }

    private static IMessage Decoded(SentRequest sent, MessageDescriptor type) =>
        sent.Wire == ClientWire.ConnectJson
            ? JsonParser.Default.Parse(Encoding.UTF8.GetString(sent.Body), type)
            : type.Parser.ParseFrom(sent.MessageBytes);

    // The methods a method's IL calls, constructs or loads a pointer to.
    private static IEnumerable<MethodBase> Called(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        for (var at = 0; at < il.Length;)
        {
            var code = il[at] == 0xFE ? Codes[0x100 | il[at + 1]] : Codes[il[at]];
            at += code.Size;
            var operand = Operand(code.OperandType, il, at);
            if (code.OperandType == OperandType.InlineMethod)
            {
                MethodBase? called = null;
                try
                {
                    called = method.Module.ResolveMethod(
                        BitConverter.ToInt32(il, at),
                        method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null,
                        method.IsGenericMethod ? method.GetGenericArguments() : null);
                }
                catch (ArgumentException)
                {
                }

                if (called is not null)
                {
                    yield return called;
                }
            }

            at += operand;
        }
    }

    private static int Operand(OperandType type, byte[] il, int at) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, at)),
        _ => 4,
    };

    private static readonly Dictionary<int, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Size == 1 ? code.Value & 0xFF : 0x100 | (code.Value & 0xFF));
}
