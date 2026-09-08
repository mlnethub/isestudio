using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ISEStudio.Ontology;

public interface IOntologyRepository
{
    Task<string> ApplyEditAsync(
        string graphIri,
        string baseIri,
        string operation,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken = default);
}
