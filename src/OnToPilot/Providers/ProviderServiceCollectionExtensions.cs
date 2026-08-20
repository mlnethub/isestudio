namespace OnToPilot.Providers;

/// <summary>
/// DI helpers for the <see cref="ProviderService"/> registration. Kept
/// thin so the <c>Program.cs</c> wiring matches the pattern used by the
/// other recovery/seed services (see <c>Program.cs</c> lines 343-345).
/// </summary>
public static class ProviderServiceCollectionExtensions
{
    /// <summary>
    /// Register the <see cref="ProviderService"/> as a Scoped service so
    /// the dispatcher (Singleton) can resolve it per-request via
    /// <c>IServiceProvider.GetService</c> and share the request's scoped
    /// <c>OnToPilotDbContext</c>.
    /// </summary>
    public static IServiceCollection AddProviderServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<ProviderService>();
        return services;
    }
}