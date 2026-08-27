namespace ISEStudio.Infrastructure.Persistence.Entities;

/// <summary>
/// Marker interface for entities that carry a stable Guid primary key.
/// Phase 3 introduced this contract to replace the legacy long id inheritance.
/// </summary>
public interface IHasId
{
    Guid Id { get; set; }
}
