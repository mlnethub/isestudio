using ISEStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ISEStudio.Ontology;

public sealed class RdfWriteScope : IDisposable, IAsyncDisposable
{
    private readonly IDbContextTransaction? _owned;
    private RdfWriteScope(IDbContextTransaction? owned) => _owned = owned;
    public static RdfWriteScope Unmanaged() => new(null);

    public static async Task<RdfWriteScope> BeginAsync(ISEStudioDbContext db, Guid ksId, CancellationToken ct = default)
    {
        var owned = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        try
        {
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM knowledgesystem WHERE id = {ksId} FOR NO KEY UPDATE", ct).ConfigureAwait(false);
            else
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE knowledgesystem SET id = id WHERE id = {ksId}", ct).ConfigureAwait(false);
            return new(owned);
        }
        catch
        {
            if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task CommitAsync(CancellationToken ct = default) => _owned?.CommitAsync(ct) ?? Task.CompletedTask;
    public void Commit() => _owned?.Commit();
    public void Dispose() => _owned?.Dispose();
    public ValueTask DisposeAsync() => _owned?.DisposeAsync() ?? ValueTask.CompletedTask;
}