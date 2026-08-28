using ISEStudio.Api;
using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Application.Prompts;
using ISEStudio.Prompts;
using static ISEStudio.Integration.InternalRequestHelpers;

namespace ISEStudio.Integration;

/// <summary>
/// Application service for the four <c>prompts.*</c> dispatcher arms
/// (10/13 slice). The dispatcher still wraps each mutation arm in
/// <c>RunWithExtractionGuardAsync</c> at the switch arm layer, so a
/// live extraction job still turns 409 with the
/// <c>{detail:{job_id,...}}</c> envelope — the application service
/// throws no guard of its own.
/// </summary>
public sealed class PromptsApplicationService : IPromptsApplicationService
{
    private readonly PromptService _prompts;

    public PromptsApplicationService(PromptService prompts)
    {
        _prompts = prompts;
    }

    public Task<PromptListOut?> ListAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null)
        {
            return Task.FromResult<PromptListOut?>(null);
        }
        return _prompts.ListAsync(
            request.KnowledgeSystemGuid.Value, request.Actor, cancellationToken);
    }

    public Task<PromptOut?> UpdateAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || string.IsNullOrEmpty(request.ResourceId))
        {
            return Task.FromResult<PromptOut?>(null);
        }
        var body = DeserializeBody<PromptUpdateIn>(request);
        if (body is null || string.IsNullOrWhiteSpace(body.Content))
        {
            throw new ValidationException("content must not be empty");
        }
        return _prompts.UpdateAsync(
            request.KnowledgeSystemGuid.Value,
            request.ResourceId,
            body.Content,
            request.Actor,
            cancellationToken);
    }

    public Task<PromptOut?> RestoreAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null
            || string.IsNullOrEmpty(request.ResourceId))
        {
            return Task.FromResult<PromptOut?>(null);
        }
        return _prompts.RestoreAsync(
            request.KnowledgeSystemGuid.Value,
            request.ResourceId,
            request.Actor,
            cancellationToken);
    }

    public Task<int> RestoreAllAsync(
        InternalRequest request, CancellationToken cancellationToken)
    {
        if (request.KnowledgeSystemGuid is null)
        {
            return Task.FromResult(0);
        }
        return _prompts.RestoreAllAsync(
            request.KnowledgeSystemGuid.Value, request.Actor, cancellationToken);
    }
}