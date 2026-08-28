using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Application.Integration;
using ISEStudio.Integration;

namespace ISEStudio.Documents;

/// <summary>
/// DI helpers for the documents slice. Mirrors
/// <see cref="Knowledge.KnowledgeServiceCollectionExtensions"/>:
/// the service is Scoped (it depends on the scoped
/// <c>ISEStudioDbContext</c>) so the request-scoped entity tracker
/// flows through.
///
/// <para>The underlying storage / parser / chunker singletons
/// (<see cref="Storage.IBlobStore"/>, <see cref="Parsing.IDocumentParser"/>,
/// <see cref="Parsing.Chunker"/>) are registered by the host
/// (<c>Program.cs</c>) so a single instance spans the whole app —
/// <see cref="DocumentService"/> resolves them from constructor
/// injection.</para>
///
/// <para>The application-service layer <see cref="IDocumentApplicationService"/>
/// is registered here so the existing <c>Program.cs</c> call site
/// (<c>builder.Services.AddDocumentServices()</c>) picks it up
/// transparently; see
/// <c>docs/superpowers/specs/2026-08-28-documents-application-service.md</c>.</para>
/// </summary>
public static class DocumentServiceCollectionExtensions
{
    public static IServiceCollection AddDocumentServices(this IServiceCollection services)
    {
        services.AddScoped<DocumentService>();
        services.AddScoped<IDocumentApplicationService, DocumentApplicationService>();
        return services;
    }
}