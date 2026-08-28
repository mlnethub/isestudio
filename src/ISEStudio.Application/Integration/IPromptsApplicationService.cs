using ISEStudio.Application.Foundation;
using ISEStudio.Application.Prompts;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Application-service seam for the four <c>prompts.*</c> dispatcher
/// arms (10/13 slice). Replaces the inline <c>InvokePrompts*</c>
/// helpers that previously unpacked the <see cref="InternalRequest"/>
/// envelope (body deserialization + resource-id routing) and called
/// <c>PromptService</c> directly.
/// </summary>
public interface IPromptsApplicationService
{
    /// <summary>
    /// <c>prompts.list</c>: merge the static <see cref="Prompts.PromptCatalog"/>
    /// with this KS's override rows into the wire-shape list. Returns
    /// <c>null</c> when the KS is missing or invisible to the actor
    /// (mapped to 404 by the dispatcher).
    /// </summary>
    Task<PromptListOut?> ListAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>prompts.update</c>: upsert an override row. Throws
    /// <see cref="System.Collections.Generic.KeyNotFoundException"/>
    /// when the key isn't in the catalog and
    /// <see cref="ISEStudio.Api.ValidationException"/> for empty content
    /// or insufficient role.
    /// </summary>
    Task<PromptOut?> UpdateAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>prompts.restore</c>: remove an override row (no-op when
    /// missing). The returned <see cref="PromptOut"/> reflects the
    /// default state (<c>is_overridden=false</c>) whether or not a row
    /// existed.
    /// </summary>
    Task<PromptOut?> RestoreAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>prompts.restore_all</c>: remove every override row for this
    /// KS in one transaction. Returns the number of rows removed;
    /// <c>0</c> when no overrides exist.
    /// </summary>
    Task<int> RestoreAllAsync(InternalRequest request, CancellationToken cancellationToken);
}