namespace ISEStudio.Infrastructure.Persistence.Entities;

/// <summary>
/// Compatibility base class for every business entity mapped in
/// <see cref="ISEStudioDbContext"/>. Mirrors the ISEStudio Python backend's
/// integer <c>id</c> primary key while moving the actual primary key onto a
/// <see cref="Guid"/>.
///
/// <para>Mapping strategy: <b>Table-Per-Concrete-Type (TPC)</b>. Each concrete
/// type owns its own table with its own <c>Id</c> PK and its own unique
/// <c>LegacyId</c> index, so the migration cleanly translates the 24 Python
/// tables 1-to-1.</para>
/// </summary>
public abstract class LegacyAddressableEntity
{
    /// <summary>
    /// New primary key. Generated in C# at construction time
    /// (<see cref="Guid.NewGuid"/>) — not by the database. Every concrete
    /// entity maps <c>Id</c> as its <c>PRIMARY KEY</c>.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// The Python-era integer identifier. Persisted as a non-unique column
    /// default <c>0L</c> post-Phase 2 — the <c>LegacyIdAllocator</c> service
    /// and the <c>ux_*_legacy_id</c> UNIQUE indexes have been retired, so
    /// new rows share the same default value without conflict. REST routes
    /// resolve through <see cref="Id"/> (Guid) for new callers; legacy
    /// integer callers still see the column but must not depend on its
    /// uniqueness. The single intentional non-zero production write is
    /// <c>SettingsService.cs:114</c> (singleton SystemConfig seeds with
    /// <c>SystemConfigEntity.SingletonLegacyId</c>).
    /// </summary>
    public long LegacyId { get; set; }
}