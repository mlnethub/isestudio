using System.Text.Json;
using System.Text.Json.Serialization;

namespace ISEStudio.Sources;

public sealed record SourceConfigField(string Name, string Type, bool Required, bool Secret,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null);

public sealed record SourceKindDescriptor(
    string Kind,
    bool ActiveSync,
    IReadOnlyList<SourceConfigField> ConfigFields,
    [property: JsonIgnore] Func<JsonElement, string?>? ValidateConfig = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null);

public sealed class SourceAdapterRegistry
{
    private readonly IReadOnlyDictionary<string, SourceKindDescriptor> _kinds;

    public SourceAdapterRegistry(IEnumerable<SourceKindDescriptor> kinds)
    {
        _kinds = kinds.Where(kind => !IsReservedKind(kind.Kind))
            .ToDictionary(kind => kind.Kind, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsReservedKind(string kind)
        => string.Equals(kind, SourceKind.AzureBlob, StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, SourceKind.Memory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, SourceKind.Upload, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyCollection<SourceKindDescriptor> CreatableKinds => _kinds.Values.ToArray();

    public bool TryGet(string kind, out SourceKindDescriptor descriptor)
        => _kinds.TryGetValue(kind, out descriptor!);
}