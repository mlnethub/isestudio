using Microsoft.Extensions.DependencyInjection;
using ISEStudio.Application.Integration;
using ISEStudio.Integration;
using ISEStudio.Llm;
using ISEStudio.Ontology;

namespace ISEStudio.Conflicts;

/// <summary>
/// DI helpers for the conflicts slice. Mirrors <see cref="Providers.ProviderServiceCollectionExtensions"/>:
/// the service is Scoped (it depends on the scoped <c>ISEStudioDbContext</c>);
/// the optional <c>StoreWrapper</c> + <c>ExtractionJobStore</c> dependencies
/// are resolved per-request through <see cref="IServiceProvider"/> rather
/// than constructor-injected so the SQLite-backed contract-test factory can
/// run the SQL paths without an Oxigraph store.
/// <para>
/// The <see cref="ISEStudio.Conflicts.ConflictDetectionOrchestrator"/> +
/// <see cref="IConflictApplicationService"/> pair is the application-layer
/// surface the dispatcher delegates to (see
/// <c>docs/superpowers/specs/2026-08-28-abox-application-service-pilot.md</c>
/// §6 for the cross-slice decisions and
/// <c>docs/superpowers/specs/2026-08-28-conflicts-application-service.md</c>
/// for this slice's design).
/// </para>
/// </summary>
public static class ConflictServiceCollectionExtensions
{
    public static IServiceCollection AddConflictServices(this IServiceCollection services)
    {
        services.AddScoped<EmbeddingGeneratorFactory>();
        services.AddScoped<DuplicateJudge>();
        services.AddScoped<ConflictService>();
        services.AddScoped<ConflictAgent>();
        // Slice 3 spec §5 D6: the Dovetail agent-chain steps and the
        // orchestrator fallback resolve via IConflictAgent; forward to the
        // scoped concrete so both keys share one instance.
        services.AddScoped<IConflictAgent>(sp => sp.GetRequiredService<ConflictAgent>());
        services.AddScoped<ConflictDetectionOrchestrator>();
        services.AddScoped<IConflictApplicationService, ConflictApplicationService>();
        return services;
    }
}