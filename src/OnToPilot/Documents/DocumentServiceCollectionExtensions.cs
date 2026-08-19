using Microsoft.Extensions.DependencyInjection;

namespace OnToPilot.Documents;

/// <summary>
/// DI helpers for the documents slice. Mirrors
/// <see cref="Knowledge.KnowledgeServiceCollectionExtensions"/>:
/// the service is Scoped (it depends on the scoped
/// <c>OnToPilotDbContext</c>) so the request-scoped entity tracker
/// flows through.
///
/// <para>The underlying storage / parser / chunker singletons
/// (<see cref="Storage.IBlobStore"/>, <see cref="Parsing.IDocumentParser"/>,
/// <see cref="Parsing.Chunker"/>) are registered by the host
/// (<c>Program.cs</c>) so a single instance spans the whole app —
/// <see cref="DocumentService"/> resolves them from constructor
/// injection.</para>
/// </summary>
public static class DocumentServiceCollectionExtensions
{
    public static IServiceCollection AddDocumentServices(this IServiceCollection services)
    {
        services.AddScoped<DocumentService>();
        return services;
    }
}