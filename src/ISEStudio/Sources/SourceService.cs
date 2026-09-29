using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Cronos;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Authorization;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Sources;

public sealed class SourceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ISEStudioDbContext _db;
    private readonly KnowledgeSystemAccessService _access;
    private readonly SourceAdapterRegistry _registry;
    private readonly ISourceSecretProtector _secrets;
    private readonly TimeProvider _clock;
    private readonly SourceSyncJobStore? _syncJobs;
    private readonly ISourceSyncQueueWakeup? _syncQueueWakeup;

    public SourceService(
        ISEStudioDbContext db,
        KnowledgeSystemAccessService access,
        SourceAdapterRegistry registry,
        ISourceSecretProtector secrets,
        TimeProvider clock,
        SourceSyncJobStore? syncJobs = null,
        ISourceSyncQueueWakeup? syncQueueWakeup = null)
    {
        _db = db;
        _access = access;
        _registry = registry;
        _secrets = secrets;
        _clock = clock;
        _syncJobs = syncJobs;
        _syncQueueWakeup = syncQueueWakeup;
    }

    public async Task<SourceMutationResult<IReadOnlyList<SourceOut>>> ListAsync(
        Guid ksId, UserEntity actor, CancellationToken ct)
    {
        var access = await CheckAccessAsync(ksId, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (access is not null) return SourceMutationResult<IReadOnlyList<SourceOut>>.Failure(access.Value.Status, access.Value.Error);

        var sources = await _db.Sources.AsNoTracking()
            .Where(source => source.KnowledgeSystemId == ksId)
            .ToListAsync(ct).ConfigureAwait(false);
        sources = sources.OrderBy(source => source.CreatedAt).ThenBy(source => source.Id).ToList();
        var output = new List<SourceOut>(sources.Count);
        foreach (var source in sources)
            output.Add(await ProjectAsync(source, ct).ConfigureAwait(false));
        return SourceMutationResult<IReadOnlyList<SourceOut>>.Success(output);
    }

    public async Task<SourceMutationResult<SourceDetailOut>> GetAsync(
        Guid ksId, Guid sourceId, UserEntity actor, CancellationToken ct)
    {
        var access = await CheckAccessAsync(ksId, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (access is not null) return SourceMutationResult<SourceDetailOut>.Failure(access.Value.Status, access.Value.Error);

        var source = await _db.Sources.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == sourceId && item.KnowledgeSystemId == ksId, ct)
            .ConfigureAwait(false);
        if (source is null) return SourceMutationResult<SourceDetailOut>.Failure(404, "Source not found");
        var summary = await ProjectAsync(source, ct).ConfigureAwait(false);
        return SourceMutationResult<SourceDetailOut>.Success(new SourceDetailOut(
            summary.Id, summary.Kind, summary.Name, summary.Icon, summary.SyncIntervalMinutes,
            summary.SyncCron, summary.LastSyncedAt, summary.LastSyncStatus, summary.LastSyncError,
            summary.LastSyncAdded, summary.CreatedAt, summary.DocumentCount, summary.MissingDocumentCount,
            ProjectConfig(source)));
    }

    public async Task<SourceMutationResult<SourceSyncJobOut>> SyncAsync(
        Guid ksId, Guid sourceId, UserEntity actor, CancellationToken ct)
    {
        var access = await CheckAccessAsync(ksId, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (access is not null)
            return SourceMutationResult<SourceSyncJobOut>.Failure(access.Value.Status, access.Value.Error);

        var source = await _db.Sources.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == sourceId && item.KnowledgeSystemId == ksId, ct)
            .ConfigureAwait(false);
        if (source is null)
            return SourceMutationResult<SourceSyncJobOut>.Failure(404, "Source not found");
        if (!_registry.TryGet(source.Kind, out var descriptor) || !descriptor.ActiveSync)
            return SourceMutationResult<SourceSyncJobOut>.Failure(400, "Source kind does not support sync");
        if (_syncJobs is null)
            return SourceMutationResult<SourceSyncJobOut>.Failure(503, "Source sync is not configured");

        Guid jobId;
        try
        {
            jobId = await _syncJobs.EnqueueAsync(sourceId, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return SourceMutationResult<SourceSyncJobOut>.Failure(404, "Source not found");
        }
        if (_syncQueueWakeup is not null)
            await _syncQueueWakeup.WakeAsync(ct).ConfigureAwait(false);

        var job = await _db.SourceSyncJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == jobId && item.SourceId == sourceId, ct)
            .ConfigureAwait(false);
        return job is null
            ? SourceMutationResult<SourceSyncJobOut>.Failure(404, "Source sync job not found")
            : SourceMutationResult<SourceSyncJobOut>.Success(new SourceSyncJobOut(job.Id, job.Status), 202);
    }

    public async Task<SourceMutationResult<IReadOnlyList<SourceSyncRunOut>>> ListRunsAsync(
        Guid ksId, Guid sourceId, UserEntity actor, CancellationToken ct)
    {
        var access = await CheckAccessAsync(ksId, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (access is not null)
            return SourceMutationResult<IReadOnlyList<SourceSyncRunOut>>.Failure(
                access.Value.Status, access.Value.Error);
        if (!await _db.Sources.AsNoTracking().AnyAsync(
                item => item.Id == sourceId && item.KnowledgeSystemId == ksId, ct).ConfigureAwait(false))
            return SourceMutationResult<IReadOnlyList<SourceSyncRunOut>>.Failure(404, "Source not found");

        var runQuery = _db.SourceSyncRuns.AsNoTracking().Where(run => run.SourceId == sourceId);
        List<SourceSyncRunEntity> runs;
        if (_db.Database.IsNpgsql())
        {
            runs = await runQuery.OrderByDescending(run => run.StartedAt)
                .ThenByDescending(run => run.Id)
                .Take(50)
                .ToListAsync(ct).ConfigureAwait(false);
        }
        else
        {
            runs = (await runQuery.ToListAsync(ct).ConfigureAwait(false))
                .OrderByDescending(run => run.StartedAt)
                .ThenByDescending(run => run.Id)
                .Take(50)
                .ToList();
        }

        return SourceMutationResult<IReadOnlyList<SourceSyncRunOut>>.Success(runs.Select(run =>
            new SourceSyncRunOut(run.Id, run.Status, run.StartedAt, run.FinishedAt,
                run.AddedCount, run.UpdatedCount, run.Error)).ToArray());
    }

    public async Task<SourceMutationResult<SourceSyncJobDetailOut>> GetJobAsync(
        Guid ksId, Guid sourceId, Guid jobId, UserEntity actor, CancellationToken ct)
    {
        var access = await CheckAccessAsync(ksId, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (access is not null)
            return SourceMutationResult<SourceSyncJobDetailOut>.Failure(access.Value.Status, access.Value.Error);
        if (!await _db.Sources.AsNoTracking().AnyAsync(
                item => item.Id == sourceId && item.KnowledgeSystemId == ksId, ct).ConfigureAwait(false))
            return SourceMutationResult<SourceSyncJobDetailOut>.Failure(404, "Source not found");

        var job = await _db.SourceSyncJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == jobId && item.SourceId == sourceId, ct)
            .ConfigureAwait(false);
        return job is null
            ? SourceMutationResult<SourceSyncJobDetailOut>.Failure(404, "Source sync job not found")
            : SourceMutationResult<SourceSyncJobDetailOut>.Success(new SourceSyncJobDetailOut(
                job.Id, job.Status, job.ActiveRunId, job.LeaseUntil, job.CreatedAt,
                job.StartedAt, job.FinishedAt, job.Error));
    }

    public async Task<SourceMutationResult<SourceOut>> CreateAsync(
        Guid ksId, SourceUpsertRequest request, UserEntity actor, CancellationToken ct)
    {
        var access = await CheckAccessAsync(ksId, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (access is not null) return SourceMutationResult<SourceOut>.Failure(access.Value.Status, access.Value.Error);
        var validation = Validate(request, out var kindDescriptor);
        if (validation is not null) return SourceMutationResult<SourceOut>.Failure(400, validation);

        var kind = kindDescriptor.Kind;
        var name = request.Name!.Trim();
        if (await _db.Sources.AnyAsync(source => source.KnowledgeSystemId == ksId && source.Name == name, ct)
                .ConfigureAwait(false))
            return SourceMutationResult<SourceOut>.Failure(409, "A source with this name already exists");

        var source = new SourceEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ksId,
            Kind = kind,
            Name = name,
            Icon = NullIfBlank(request.Icon),
            SyncIntervalMinutes = request.SyncIntervalMinutes,
            SyncCron = string.IsNullOrWhiteSpace(request.SyncCron) ? null : request.SyncCron.Trim(),
            LastSyncStatus = "never",
            CreatedAt = _clock.GetUtcNow(),
        };
        var configResult = BuildConfig(source, kindDescriptor, request.Config, creating: true);
        if (configResult.Error is not null)
            return SourceMutationResult<SourceOut>.Failure(configResult.StatusCode, configResult.Error);
        source.Config = configResult.Config!;
        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        _db.Sources.Add(source);
        var changedFields = new List<string> { "kind", "name" };
        changedFields.AddRange(configResult.ChangedFields.Select(field => $"config.{field}"));
        try
        {
            await WriteAuditAsync(ksId, actor, "source.create", source, changedFields, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            _db.Entry(source).State = EntityState.Detached;
            DetachPendingSourceAudit("source.create", ksId, source);
            if (await _db.Sources.AsNoTracking().AnyAsync(
                    item => item.KnowledgeSystemId == ksId && item.Name == name, ct)
                .ConfigureAwait(false))
                return SourceMutationResult<SourceOut>.Failure(409, "A source with this name already exists");
            throw;
        }
        return SourceMutationResult<SourceOut>.Success(await ProjectAsync(source, ct).ConfigureAwait(false), 201);
    }

    public async Task<SourceMutationResult<SourceOut>> UpdateAsync(
        Guid ksId, Guid sourceId, SourceUpsertRequest request, UserEntity actor, CancellationToken ct)
    {
        var access = await CheckAccessAsync(ksId, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (access is not null) return SourceMutationResult<SourceOut>.Failure(access.Value.Status, access.Value.Error);
        var validation = Validate(request, out var kindDescriptor);
        if (validation is not null) return SourceMutationResult<SourceOut>.Failure(400, validation);

        var source = await _db.Sources.SingleOrDefaultAsync(
            item => item.Id == sourceId && item.KnowledgeSystemId == ksId, ct).ConfigureAwait(false);
        if (source is null) return SourceMutationResult<SourceOut>.Failure(404, "Source not found");
        var kind = request.Kind!.Trim();
        if (!string.Equals(source.Kind, kind, StringComparison.OrdinalIgnoreCase))
            return SourceMutationResult<SourceOut>.Failure(400, "Source kind cannot be changed");
        var name = request.Name!.Trim();
        if (await _db.Sources.AnyAsync(
                item => item.KnowledgeSystemId == ksId && item.Id != sourceId && item.Name == name, ct)
                .ConfigureAwait(false))
            return SourceMutationResult<SourceOut>.Failure(409, "A source with this name already exists");

        (string? Config, int StatusCode, string? Error, IReadOnlyList<string> ChangedFields)? configResult = null;
        if (request.Config.HasValue)
        {
            configResult = BuildConfig(source, kindDescriptor, request.Config, creating: false);
            if (configResult.Value.Error is not null)
                return SourceMutationResult<SourceOut>.Failure(
                    configResult.Value.StatusCode, configResult.Value.Error);
        }

        var changedFields = new List<string>();
        if (!string.Equals(source.Name, name, StringComparison.Ordinal))
        {
            source.Name = name;
            changedFields.Add("name");
        }
        if (request.Icon is not null)
        {
            var icon = NullIfBlank(request.Icon);
            if (!string.Equals(source.Icon, icon, StringComparison.Ordinal))
            {
                source.Icon = icon;
                changedFields.Add("icon");
            }
        }
        if (configResult.HasValue)
        {
            source.Config = configResult.Value.Config!;
            changedFields.AddRange(configResult.Value.ChangedFields.Select(field => $"config.{field}"));
        }
        var syncCron = string.IsNullOrWhiteSpace(request.SyncCron) ? null : request.SyncCron.Trim();
        if (source.SyncIntervalMinutes != request.SyncIntervalMinutes)
        {
            source.SyncIntervalMinutes = request.SyncIntervalMinutes;
            changedFields.Add("sync_interval_minutes");
        }
        if (!string.Equals(source.SyncCron, syncCron, StringComparison.Ordinal))
        {
            source.SyncCron = syncCron;
            changedFields.Add("sync_cron");
        }
        try
        {
            await WriteAuditAsync(ksId, actor, "source.update", source, changedFields, ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            DetachPendingSourceAudit("source.update", ksId, source);
            await _db.Entry(source).ReloadAsync(ct).ConfigureAwait(false);
            if (await _db.Sources.AsNoTracking().AnyAsync(
                    item => item.KnowledgeSystemId == ksId && item.Id != sourceId && item.Name == name, ct)
                .ConfigureAwait(false))
                return SourceMutationResult<SourceOut>.Failure(409, "A source with this name already exists");
            throw;
        }
        return SourceMutationResult<SourceOut>.Success(await ProjectAsync(source, ct).ConfigureAwait(false));
    }

    public async Task<SourceMutationResult<bool>> DeleteAsync(
        Guid ksId, Guid sourceId, UserEntity actor, CancellationToken ct)
    {
        var access = await CheckAccessAsync(ksId, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (access is not null) return SourceMutationResult<bool>.Failure(access.Value.Status, access.Value.Error);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        SourceEntity? source;
        if (_db.Database.IsNpgsql())
        {
            source = await _db.Sources.FromSqlInterpolated(
                    $"SELECT * FROM source WHERE id = {sourceId} AND knowledge_system_id = {ksId} FOR UPDATE")
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE source SET id = id WHERE id = {sourceId} AND knowledge_system_id = {ksId}", ct)
                .ConfigureAwait(false);
            source = await _db.Sources.SingleOrDefaultAsync(
                item => item.Id == sourceId && item.KnowledgeSystemId == ksId, ct).ConfigureAwait(false);
        }

        if (source is null) return SourceMutationResult<bool>.Failure(404, "Source not found");
        if (await _db.SourceSyncJobs.AnyAsync(
                job => job.SourceId == sourceId && (job.Status == "queued" || job.Status == "running"), ct)
            .ConfigureAwait(false))
            return SourceMutationResult<bool>.Failure(409, "Source has an active sync job");

        var documentIds = await _db.Documents.AsNoTracking()
            .Where(document => document.SourceId == sourceId)
            .Select(document => document.Id)
            .Concat(_db.SourceDocumentBindings.AsNoTracking()
                .Where(binding => binding.SourceId == sourceId)
                .Select(binding => binding.DocumentId))
            .Distinct()
            .OrderBy(id => id)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);
        List<DocumentEntity> documents;
        if (_db.Database.IsNpgsql())
        {
            documents = await _db.Documents.FromSqlInterpolated(
                    $"SELECT * FROM document WHERE id = ANY({documentIds}) ORDER BY id FOR UPDATE")
                .ToListAsync(ct).ConfigureAwait(false);
        }
        else
        {
            foreach (var documentId in documentIds)
            {
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE document SET id = id WHERE id = {documentId}", ct).ConfigureAwait(false);
            }
            documents = await _db.Documents.Where(document => documentIds.Contains(document.Id))
                .OrderBy(document => document.Id).ToListAsync(ct).ConfigureAwait(false);
        }

        foreach (var document in documents)
        {
            if (document.SourceId == sourceId)
            {
                document.SourceId = null;
                document.ExternalKey = null;
            }
        }
        _db.Sources.Remove(source);
        await WriteAuditAsync(ksId, actor, "source.delete", source, ["sourceId", "kind"], ct)
            .ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        foreach (var document in documents)
        {
            var bindingStates = await _db.SourceDocumentBindings.AsNoTracking()
                .Where(binding => binding.DocumentId == document.Id)
                .Select(binding => binding.MissingSince)
                .ToListAsync(ct).ConfigureAwait(false);
            if (document.IsManualUpload || bindingStates.Count == 0 || bindingStates.Any(state => state is null))
                document.MissingSince = null;
            else
                document.MissingSince ??= now;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return SourceMutationResult<bool>.Success(true, 204);
    }

    public async Task<SourceMutationResult<SourceTokenOut>> RotateTokenAsync(
        Guid ksId, Guid sourceId, UserEntity actor, CancellationToken ct)
    {
        var access = await CheckAccessAsync(ksId, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (access is not null) return SourceMutationResult<SourceTokenOut>.Failure(access.Value.Status, access.Value.Error);
        if (!_secrets.IsConfigured)
            return SourceMutationResult<SourceTokenOut>.Failure(503, "Source token protection is not configured");

        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var source = await FindSourceForUpdateAsync(ksId, sourceId, ct).ConfigureAwait(false);
        if (source is null) return SourceMutationResult<SourceTokenOut>.Failure(404, "Source not found");
        if (!SupportsPushToken(source.Kind))
            return SourceMutationResult<SourceTokenOut>.Failure(409, "This source kind does not support push tokens");

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        CryptographicOperations.ZeroMemory(tokenBytes);
        source.IngestTokenCiphertext = _secrets.Seal(token, TokenAssociatedData(ksId, sourceId));
        await WriteAuditAsync(ksId, actor, "source.token.rotate", source, ["token"], ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return SourceMutationResult<SourceTokenOut>.Success(new SourceTokenOut(token));
    }

    public async Task<SourceMutationResult<SourceTokenOut>> RevealTokenAsync(
        Guid ksId, Guid sourceId, UserEntity actor, CancellationToken ct)
    {
        var access = await CheckAccessAsync(ksId, actor, KSRole.Editor, ct).ConfigureAwait(false);
        if (access is not null) return SourceMutationResult<SourceTokenOut>.Failure(access.Value.Status, access.Value.Error);
        if (!_secrets.IsConfigured)
            return SourceMutationResult<SourceTokenOut>.Failure(503, "Source token protection is not configured");

        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var source = await FindSourceForUpdateAsync(ksId, sourceId, ct).ConfigureAwait(false);
        if (source is null) return SourceMutationResult<SourceTokenOut>.Failure(404, "Source not found");
        if (!SupportsPushToken(source.Kind))
            return SourceMutationResult<SourceTokenOut>.Failure(409, "This source kind does not support push tokens");
        if (string.IsNullOrWhiteSpace(source.IngestTokenCiphertext))
            return SourceMutationResult<SourceTokenOut>.Failure(404, "Source token has not been created");

        string token;
        try
        {
            token = _secrets.Open(source.IngestTokenCiphertext, TokenAssociatedData(ksId, sourceId));
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return SourceMutationResult<SourceTokenOut>.Failure(503, "Source token is unavailable");
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        await WriteAuditAsync(ksId, actor, "source.token.reveal", source, ["token"], ct).ConfigureAwait(false);
        return SourceMutationResult<SourceTokenOut>.Success(new SourceTokenOut(token));
    }

    public async Task<bool> VerifyAsync(
        Guid ksId, Guid sourceId, string? presentedToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(presentedToken) || !_secrets.IsConfigured) return false;
        var source = await _db.Sources.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == sourceId && item.KnowledgeSystemId == ksId,
            ct).ConfigureAwait(false);
        if (source is null || !SupportsPushToken(source.Kind)
            || string.IsNullOrWhiteSpace(source.IngestTokenCiphertext))
            return false;

        try
        {
            var expected = _secrets.Open(source.IngestTokenCiphertext, TokenAssociatedData(ksId, sourceId));
            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            var presentedBytes = Encoding.UTF8.GetBytes(presentedToken);
            try
            {
                return CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedBytes);
                CryptographicOperations.ZeroMemory(presentedBytes);
            }
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return false;
        }
    }

    public async Task<SourceMutationResult<IReadOnlyList<SourceKindDescriptor>>> KindsAsync(
        Guid ksId, UserEntity actor, CancellationToken ct)
    {
        var access = await CheckAccessAsync(ksId, actor, KSRole.Viewer, ct).ConfigureAwait(false);
        if (access is not null)
            return SourceMutationResult<IReadOnlyList<SourceKindDescriptor>>.Failure(access.Value.Status, access.Value.Error);
        return SourceMutationResult<IReadOnlyList<SourceKindDescriptor>>.Success(_registry.CreatableKinds.ToArray());
    }

    private async Task<(int Status, string Error)?> CheckAccessAsync(
        Guid ksId, UserEntity actor, KSRole minimum, CancellationToken ct)
    {
        var ks = await _db.KnowledgeSystems.SingleOrDefaultAsync(item => item.Id == ksId, ct).ConfigureAwait(false);
        if (ks is null) return (404, "Knowledge system not found");
        var role = await _access.GetEffectiveRoleAsync(actor, ks, _db, ct).ConfigureAwait(false);
        return role >= minimum ? null : (403, "Insufficient permissions");
    }

    private async Task<SourceEntity?> FindSourceForUpdateAsync(Guid ksId, Guid sourceId, CancellationToken ct)
    {
        if (_db.Database.IsNpgsql())
        {
            return await _db.Sources.FromSqlInterpolated(
                    $"SELECT * FROM source WHERE id = {sourceId} AND knowledge_system_id = {ksId} FOR UPDATE")
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        }

        return await _db.Sources.SingleOrDefaultAsync(
            item => item.Id == sourceId && item.KnowledgeSystemId == ksId, ct).ConfigureAwait(false);
    }

    private string? Validate(SourceUpsertRequest request, out SourceKindDescriptor kind)
    {
        kind = null!;
        if (string.IsNullOrWhiteSpace(request.Kind) || !_registry.TryGet(request.Kind.Trim(), out kind))
            return "Source kind is not available";
        if (string.IsNullOrWhiteSpace(request.Name)) return "Source name is required";
        if (request.Name.Trim().Length > 255) return "Source name must be 255 characters or fewer";
        var syncCron = string.IsNullOrWhiteSpace(request.SyncCron) ? null : request.SyncCron.Trim();
        if (request.SyncIntervalMinutes is <= 0) return "sync_interval_minutes must be greater than zero";
        if (request.SyncIntervalMinutes.HasValue && syncCron is not null)
            return "sync interval and cron cannot both be set";
        if ((request.SyncIntervalMinutes.HasValue || syncCron is not null)
            && !kind.ActiveSync)
            return "This source kind does not support scheduled sync";
        if (syncCron is not null)
        {
            try
            {
                _ = CronExpression.Parse(syncCron, CronFormat.Standard);
            }
            catch (CronFormatException)
            {
                return "sync_cron must be a valid standard five-field cron expression";
            }
        }
        if (request.Config is { ValueKind: not JsonValueKind.Object }) return "config must be an object";
        return null;
    }

    private (string? Config, int StatusCode, string? Error, IReadOnlyList<string> ChangedFields) BuildConfig(
        SourceEntity source, SourceKindDescriptor kind, JsonElement? patch, bool creating)
    {
        var fields = kind.ConfigFields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!creating)
        {
            using var existing = JsonDocument.Parse(source.Config);
            foreach (var property in existing.RootElement.EnumerateObject())
            {
                if (fields.ContainsKey(property.Name)) values[property.Name] = property.Value.Clone();
            }
        }

        var changedFields = new List<string>();
        if (patch is { } supplied)
        {
            foreach (var property in supplied.EnumerateObject())
            {
                if (!fields.TryGetValue(property.Name, out var field))
                    return (null, 400, $"Unknown config field: {property.Name}", changedFields);

                changedFields.Add(field.Name);
                if (field.Secret)
                {
                    if (property.Value.ValueKind == JsonValueKind.Null
                        || (property.Value.ValueKind == JsonValueKind.String
                            && property.Value.GetString()!.Length == 0))
                    {
                        values.Remove(field.Name);
                        continue;
                    }
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return (null, 400, $"Config field '{field.Name}' must be a string", changedFields);
                    if (!_secrets.IsConfigured)
                        return (null, 503, "Source secret protection is not configured", changedFields);

                    var ciphertext = _secrets.Seal(
                        property.Value.GetString()!, ConfigAssociatedData(source.KnowledgeSystemId, source.Id, field.Name));
                    values[field.Name] = JsonSerializer.SerializeToElement(ciphertext, JsonOptions);
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    if (field.Required)
                        return (null, 400, $"Config field '{field.Name}' is required", changedFields);
                    values.Remove(field.Name);
                    continue;
                }
                if (!MatchesType(property.Value, field.Type))
                    return (null, 400, $"Config field '{field.Name}' must be a {field.Type}", changedFields);
                values[field.Name] = property.Value.Clone();
            }
        }

        foreach (var field in kind.ConfigFields)
        {
            if (!values.TryGetValue(field.Name, out var value))
            {
                if (field.Required)
                    return (null, 400, $"Config field '{field.Name}' is required", changedFields);
                continue;
            }

            if (field.Secret)
            {
                if (value.ValueKind != JsonValueKind.String)
                    return (null, 400, $"Config field '{field.Name}' must be a string", changedFields);
                var storedValue = value.GetString()!;
                if (!storedValue.StartsWith("v1:", StringComparison.Ordinal))
                {
                    if (!_secrets.IsConfigured)
                        return (null, 503, "Source secret protection is not configured", changedFields);
                    values[field.Name] = JsonSerializer.SerializeToElement(
                        _secrets.Seal(storedValue,
                            ConfigAssociatedData(source.KnowledgeSystemId, source.Id, field.Name)), JsonOptions);
                }
            }
            else if (!MatchesType(value, field.Type))
            {
                return (null, 400, $"Config field '{field.Name}' must be a {field.Type}", changedFields);
            }
        }

        return (JsonSerializer.Serialize(values, JsonOptions), 200, null, changedFields);
    }

    private static bool MatchesType(JsonElement value, string type)
        => type.ToLowerInvariant() switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            _ => false,
        };

    private async Task<SourceOut> ProjectAsync(SourceEntity source, CancellationToken ct)
    {
        int documentCount;
        int missingDocumentCount;
        if (string.Equals(source.Kind, "folder", StringComparison.OrdinalIgnoreCase))
        {
            var documents = _db.Documents.Where(document => document.SourceId == source.Id);
            documentCount = await documents.CountAsync(ct).ConfigureAwait(false);
            missingDocumentCount = await documents.CountAsync(
                document => document.MissingSince != null, ct).ConfigureAwait(false);
        }
        else
        {
            var bindings = _db.SourceDocumentBindings.Where(binding => binding.SourceId == source.Id);
            documentCount = await bindings.Select(binding => binding.DocumentId)
                .Distinct().CountAsync(ct).ConfigureAwait(false);
            missingDocumentCount = await bindings.CountAsync(
                binding => binding.MissingSince != null, ct).ConfigureAwait(false);
        }

        return new SourceOut(
            source.Id, source.Kind, source.Name, source.Icon, source.SyncIntervalMinutes, source.SyncCron,
            source.LastSyncedAt, source.LastSyncStatus, source.LastSyncError, source.LastSyncAdded,
            source.CreatedAt, documentCount, missingDocumentCount);
    }

    private JsonElement ProjectConfig(SourceEntity source)
    {
        using var parsed = JsonDocument.Parse(source.Config);
        var visible = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (_registry.TryGet(source.Kind, out var kind))
        {
            foreach (var field in kind.ConfigFields.Where(field => !field.Secret))
            {
                if (parsed.RootElement.TryGetProperty(field.Name, out var value))
                    visible[field.Name] = value.Clone();
            }
        }
        return JsonSerializer.SerializeToElement(visible, JsonOptions);
    }

    private async Task WriteAuditAsync(
        Guid ksId, UserEntity actor, string action, SourceEntity source,
        IReadOnlyCollection<string> changedFields, CancellationToken ct)
    {
        _db.AuditEvents.Add(new AuditEventEntity
        {
            KnowledgeSystemId = ksId,
            ActorId = actor.Id,
            ActorName = actor.DisplayName ?? actor.Username,
            Action = action,
            Summary = $"{action} {source.Kind} source '{source.Name}'",
            Detail = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                source_id = source.Id,
                kind = source.Kind,
                changed_fields = changedFields,
            })),
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private void DetachPendingSourceAudit(string action, Guid ksId, SourceEntity source)
    {
        var summary = $"{action} {source.Kind} source '{source.Name}'";
        foreach (var entry in _db.ChangeTracker.Entries<AuditEventEntity>()
                     .Where(entry => entry.State == EntityState.Added
                         && entry.Entity.KnowledgeSystemId == ksId
                         && entry.Entity.Action == action
                         && entry.Entity.Summary == summary)
                     .ToArray())
            entry.State = EntityState.Detached;
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool SupportsPushToken(string kind)
        => kind is SourceKind.Api or SourceKind.Statements;

    private static string TokenAssociatedData(Guid ksId, Guid sourceId)
        => $"{ksId:D}:{sourceId:D}:token";

    private static string ConfigAssociatedData(Guid ksId, Guid sourceId, string fieldName)
        => $"{ksId:D}:{sourceId:D}:{fieldName}";
}