namespace ISEStudio.Application.Foundation;

/// <summary>
/// One structured TBox edit in an ontology change-set. Concrete verb
/// vocabulary (AddClass, RemoveProperty, …) is owned by task 2; this stub
/// keeps the facade signature compiling so the test skeleton can lock the
/// parameter ordering before any real implementations land.
/// </summary>
public sealed record EditOperation(string Verb, string Target, IReadOnlyDictionary<string, string>? Fields = null);