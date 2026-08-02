using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.GoogleIntegration;

/// <summary>
/// EF-backed implementation of <see cref="IGoogleDrivePermissionFailureRepository"/>.
/// The only non-test file that touches
/// <c>DbSet&lt;GoogleDrivePermissionFailure&gt;</c>. Uses
/// <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> remains Scoped.
/// </summary>
internal sealed class GoogleDrivePermissionFailureRepository(IDbContextFactory<HumansDbContext> factory)
    : IGoogleDrivePermissionFailureRepository
{
    public async Task<bool> ExistsAsync(string googleId, string permissionId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.GoogleDrivePermissionFailures
            .AsNoTracking()
            .AnyAsync(f => f.GoogleId == googleId && f.PermissionId == permissionId, ct);
    }

    public async Task RecordTerminalFailureAsync(
        string googleId,
        string permissionId,
        string email,
        string errorMessage,
        Instant detectedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var exists = await ctx.GoogleDrivePermissionFailures
            .AnyAsync(f => f.GoogleId == googleId && f.PermissionId == permissionId, ct);
        if (exists)
        {
            return;
        }

        ctx.GoogleDrivePermissionFailures.Add(new GoogleDrivePermissionFailure
        {
            Id = Guid.NewGuid(),
            GoogleId = googleId,
            PermissionId = permissionId,
            Email = email,
            ErrorMessage = errorMessage,
            DetectedAt = detectedAt
        });
        await ctx.SaveChangesAsync(ct);
    }
}
