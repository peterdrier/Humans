using Humans.Backdoor.Domain;
using Microsoft.EntityFrameworkCore;
using Humans.Base.Data;

namespace Humans.Backdoor.Data;

internal sealed class BackdoorOAuthRepository(IDbContextFactory<BackdoorDbContext> factory) : SectionRepository<BackdoorDbContext>(factory), IBackdoorOAuthRepository
{
    public async Task<BackdoorOAuthConnection?> GetConnectionAsync(Guid id, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.OAuthConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct), ct);

    public async Task<IReadOnlyList<BackdoorOAuthConnection>> GetConnectionsAsync(CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.OAuthConnections.AsNoTracking().ToListAsync(ct), ct);

    public async Task AddConnectionAsync(BackdoorOAuthConnection connection, CancellationToken ct = default) =>
        await InsertAsync(connection, ct);

    public async Task UpdateConnectionAsync(BackdoorOAuthConnection connection, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        ctx.OAuthConnections.Update(connection);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteConnectionsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        await ctx.OAuthTokens.Where(t => t.ConnectionId != null && ids.Contains(t.ConnectionId.Value)).ExecuteDeleteAsync(ct);
        return await ctx.OAuthConnections.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task<int> SetConnectionStatusAsync(
        IReadOnlyCollection<Guid> ids, string status, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.OAuthConnections
            .Where(c => ids.Contains(c.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, status), ct), ct);

    public async Task<BackdoorOAuthToken?> GetTokenAsync(Guid id, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.OAuthTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct), ct);

    public async Task<BackdoorOAuthToken?> FindTokenByReferenceAsync(string referenceId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.OAuthTokens.AsNoTracking().FirstOrDefaultAsync(t => t.ReferenceId == referenceId, ct), ct);

    public async Task<IReadOnlyList<BackdoorOAuthToken>> GetTokensAsync(CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.OAuthTokens.AsNoTracking().ToListAsync(ct), ct);

    public async Task AddTokenAsync(BackdoorOAuthToken token, CancellationToken ct = default) =>
        await InsertAsync(token, ct);

    public async Task UpdateTokenAsync(BackdoorOAuthToken token, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        ctx.OAuthTokens.Update(token);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteTokensAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.OAuthTokens.Where(t => ids.Contains(t.Id)).ExecuteDeleteAsync(ct), ct);

    public async Task<int> SetTokenStatusAsync(IReadOnlyCollection<Guid> ids, string status, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.OAuthTokens
            .Where(t => ids.Contains(t.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, status), ct), ct);

    public async Task EraseForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        await ctx.OAuthTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
        await ctx.OAuthConnections.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
    }
}
