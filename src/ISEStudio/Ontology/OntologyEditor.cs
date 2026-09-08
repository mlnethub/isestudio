using System.Collections.Generic;

namespace ISEStudio.Ontology;

public sealed class OntologyEditException : Exception
{
    public OntologyEditException(string message) : base(message) { }
    public OntologyEditException(string message, Exception inner) : base(message, inner) { }
}

public sealed class OntologyEditor
{
    private readonly IOntologyRepository _repository;

    public OntologyEditor(IOntologyRepository repository)
    {
        _repository = repository;
    }

    public ValueTask<string> ApplyEditAsync(
        string graphIri,
        string baseIri,
        IReadOnlyDictionary<string, object?> op,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(graphIri);
        ArgumentException.ThrowIfNullOrEmpty(baseIri);
        ArgumentNullException.ThrowIfNull(op);
        if (!op.TryGetValue("op", out var operationValue) || operationValue is not string operation || string.IsNullOrWhiteSpace(operation))
            throw new OntologyEditException("Edit op requires a string 'op' field.");
        return new ValueTask<string>(_repository.ApplyEditAsync(graphIri, baseIri, operation, op, cancellationToken));
    }
}
