namespace ISEStudio.Sources;

public sealed record SourceConfigField(string Name, string Type, bool Required, bool Secret);

public sealed record SourceKindDescriptor(
    string Kind,
    bool ActiveSync,
    IReadOnlyList<SourceConfigField> ConfigFields);

public sealed class SourceAdapterRegistry
{
    private readonly IReadOnlyDictionary<string, SourceKindDescriptor> _kinds;

    public SourceAdapterRegistry(IEnumerable<SourceKindDescriptor> kinds)
    {
        _kinds = kinds.ToDictionary(kind => kind.Kind, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<SourceKindDescriptor> CreatableKinds => _kinds.Values.ToArray();

    public bool TryGet(string kind, out SourceKindDescriptor descriptor)
        => _kinds.TryGetValue(kind, out descriptor!);
}