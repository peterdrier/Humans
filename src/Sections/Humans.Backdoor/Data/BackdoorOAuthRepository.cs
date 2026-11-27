using Humans.Backdoor.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Backdoor.Data;

internal sealed class BackdoorOAuthRepository(IDbContextFactory<BackdoorDbContext> factory) : IBackdoorOAuthRepository
{
    public async Task<BackdoorOAuthConnection?> GetConnectionAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.OAuthConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<IReadOnlyList<BackdoorOAuthConnection>> GetConnectionsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.OAuthConnections.AsNoTracking().ToListAsync(ct);
    }

    public async Task AddConnectionAsync(BackdoorOAuthConnection connection, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.OAuthConnections.Add(connection);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateConnectionAsync(BackdoorOAuthConnection connection, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.OAuthConnections.Update(connection);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteConnectionsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        await ctx.OAuthTokens.Where(t => t.ConnectionId != null && ids.Contains(t.ConnectionId.Value)).ExecuteDeleteAsync(ct);
        return await ctx.OAuthConnections.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task<int> SetConnectionStatusAsync(
        IReadOnlyCollection<Guid> ids, string status, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.OAuthConnections
            .Where(c => ids.Contains(c.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, status), ct);
    }

    public async Task<BackdoorOAuthToken?> GetTokenAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.OAuthTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    public async Task<BackdoorOAuthToken?> FindTokenByReferenceAsync(string referenceId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.OAuthTokens.AsNoTracking().FirstOrDefaultAsync(t => t.ReferenceId == referenceId, ct);
    }

    public async Task<IReadOnlyList<BackdoorOAuthToken>> GetTokensAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.OAuthTokens.AsNoTracking().ToListAsync(ct);
    }

    public async Task AddTokenAsync(BackdoorOAuthToken token, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.OAuthTokens.Add(token);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateTokenAsync(BackdoorOAuthToken token, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.OAuthTokens.Update(token);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteTokensAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.OAuthTokens.Where(t => ids.Contains(t.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task<int> SetTokenStatusAsync(IReadOnlyCollection<Guid> ids, string status, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.OAuthTokens
            .Where(t => ids.Contains(t.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, status), ct);
    }

    public async Task EraseForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        await ctx.OAuthTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
        await ctx.OAuthConnections.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
    }
}
