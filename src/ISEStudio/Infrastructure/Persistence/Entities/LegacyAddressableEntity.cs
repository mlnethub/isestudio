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
    /// The Python-era integer identifier. Persisted with a unique index per
    /// table so existing REST routes that still reference the legacy
    /// integer <c>id</c> resolve through it.
    /// </summary>
    public long LegacyId { get; set; }
}