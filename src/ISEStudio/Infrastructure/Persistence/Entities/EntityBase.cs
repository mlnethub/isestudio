namespace ISEStudio.Infrastructure.Persistence.Entities;

/// <summary>
/// Default base class for ISEStudio persistence entities. Replaces
/// LegacyAddressableEntity (Phase 3 retired). New rows get a fresh Guid
/// when constructed; EF will replace it with a server-generated default
/// if the column is configured accordingly.
/// </summary>
public abstract class EntityBase : IHasId
{
    public Guid Id { get; set; } = Guid.NewGuid();
}