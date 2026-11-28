using Microsoft.EntityFrameworkCore;

namespace Humans.Base.Data;

/// <summary>
/// Base for a section repository: one short-lived context per call, from the section's own factory.
/// </summary>
public abstract class SectionRepository<TContext>(IDbContextFactory<TContext> factory) where TContext : DbContext
{
    /// <summary>A new context the caller disposes.</summary>
    protected Task<TContext> CreateDbContextAsync(CancellationToken ct = default) =>
        factory.CreateDbContextAsync(ct);

    /// <summary>Runs <paramref name="work"/> on a new context and disposes it.</summary>
    protected async Task<T> RunAsync<T>(Func<TContext, Task<T>> work, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await work(db);
    }

    /// <summary>Adds <paramref name="entity"/> and saves.</summary>
    protected async Task InsertAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Add(entity);
        await db.SaveChangesAsync(ct);
    }
}
