namespace OnToPilot.Application.Foundation;

/// <summary>
/// Protocol-agnostic DTO returned by
/// <see cref="Integration.IIntegrationApiFacade.QueryAsync"/>. Bounded
/// read-only SPARQL results — only SELECT/ASK are admitted by the facade.
/// Concrete result binding is owned by task 3.
/// </summary>
public sealed record QueryResponse(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);