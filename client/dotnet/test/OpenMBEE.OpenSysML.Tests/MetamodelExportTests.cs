using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenMBEE.OpenSysML.Metamodel;
using Xunit;

namespace OpenMBEE.OpenSysML.Tests;

/// <summary>
/// The standalone metamodel reader over what a real sysml-grpc exports as the API's JSON element form, on every
/// client wire: the owned properties and some derived ones read, the derived properties the export omits are not
/// supplied, a reference to a standard-library element the export does not include fails only its property, and the
/// export's own annotations are kept on request.
/// </summary>
[Trait(Category.Name, Category.Integration)]
public sealed class MetamodelExportTests(ServiceFixture services) : IClassFixture<ServiceFixture>
{
    private const string Source = """
        package ReaderDemo {
            private import ScalarValues::*;
            part def Engine {
                attribute power : Real = 300.0;
            }
            part engine : Engine;
        }
        """;

    public static TheoryData<ClientWire> Wires => ClientWires.All;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Wires))]
    public async Task TheReaderReadsTheServicesApiJson(ClientWire wire)
    {
        var service = await services.ServiceAsync("connect");
        await using var connection = await Connection.OpenAsync(wire.To(service.Address), Token);
        var model = (await connection.LoadContentAsync(Source, cancellationToken: Token)).RequireNoErrors();
        var exported = await model.ToApiJsonAsync(cancellationToken: Token);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(exported.Content));
        var graph = MetamodelGraph.ReadJson(stream);

        var usage = graph.All<IPartUsage>().Single(part => part.DeclaredName == "engine");
        foreach (var derived in new Func<object>[] { () => usage.Feature, () => usage.InheritedFeature, () => usage.Definition })
        {
            Assert.True(Assert.Throws<NotSuppliedException>(derived).IsDerived);
        }

        var definition = graph.All<IPartDefinition>().Single(item => item.DeclaredName == "Engine");
        Assert.Same(definition, Assert.Single(usage.Type));
        Assert.Equal("ReaderDemo::engine", usage.QualifiedName);
        Assert.NotEmpty(definition.OwnedFeature);
        Assert.All(definition.OwnedFeature, feature => Assert.Same(feature, graph[feature.Id]));
        var power = Assert.IsAssignableFrom<IAttributeUsage>(Assert.Single(definition.OwnedFeature));
        Assert.Equal("power", power.DeclaredName);
        var real = Assert.Throws<UnresolvedReferenceException>(() => power.Type);
        Assert.False(graph.Contains(real.TargetId!));
        Assert.Equal("ReaderDemo", Assert.Single(graph.Roots.SelectMany(root => root.OwnedMember)).DeclaredName);

        using var document = System.Text.Json.JsonDocument.Parse(exported.Content);
        var kept = MetamodelGraph.ReadJson(document, new ReadOptions { KeepExtensions = true });
        var source = kept[usage.Id].Extensions["sysx:sourceText"].GetString();
        Assert.Contains("part engine : Engine", source, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => usage.Extensions);
    }
}
