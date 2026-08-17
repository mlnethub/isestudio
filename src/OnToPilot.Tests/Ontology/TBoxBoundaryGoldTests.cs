using System.Text.Json;
using System.Text.Json.Serialization;
using OnToPilot.Ontology;

namespace OnToPilot.Tests.Ontology;

/// <summary>
/// Gold fixture mirror of <c>backend/tests/gold/tbox_abox_boundary.json</c>. The
/// fixture is loaded once and projected into <see cref="BoundaryCase"/>
/// instances. Each case asserts that the .NET <c>TBoxGuard.Sanitize</c>
/// produces the same accepted / rejected label set as the Python
/// <c>sanitize_ontology_delta</c> on the same source text.
/// </summary>
public sealed class TBoxBoundaryGoldTests
{
    public sealed record BoundaryCase(
        string Name,
        OntologyMutation Input,
        GuardContext Context,
        IReadOnlyList<string> ExpectedClasses,
        IReadOnlyList<string> ExpectedIndividuals);

    public sealed record FixtureEntry(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("classes")] List<string> Classes,
        [property: JsonPropertyName("expected_classes")] List<string> ExpectedClasses,
        [property: JsonPropertyName("expected_rejected")] List<string> ExpectedRejected);

    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "tbox_abox_boundary.json");

    public static IEnumerable<object[]> BoundaryCases()
    {
        var json = File.ReadAllText(FixturePath);
        var entries = JsonSerializer.Deserialize<List<FixtureEntry>>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to deserialize gold fixture.");

        foreach (var e in entries)
        {
            yield return new object[]
            {
                new BoundaryCase(
                    Name: e.Name,
                    Input: new OntologyMutation(
                        Classes: e.Classes
                            .Select(label => new ClassMutation(Label: label))
                            .ToList(),
                        ObjectProperties: Array.Empty<PropertyMutation>(),
                        DataProperties: Array.Empty<PropertyMutation>(),
                        Axioms: Array.Empty<AxiomMutation>()),
                    Context: new GuardContext(SourceText: e.Source),
                    ExpectedClasses: e.ExpectedClasses,
                    ExpectedIndividuals: e.ExpectedRejected),
            };
        }
    }

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public void Guard_matches_python_role_boundary_fixture(BoundaryCase fixture)
    {
        var result = Guard.Sanitize(fixture.Input, fixture.Context);

        Assert.Equal(fixture.ExpectedClasses, result.Classes.Select(c => c.Label).ToList());
        Assert.Equal(fixture.ExpectedIndividuals, result.Individuals.Select(i => i.Label).ToList());
    }
}