using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Extensions;
using Humans.Application.Architecture;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories.GoogleIntegration;

/// <summary>
/// EF-backed implementation of <see cref="IDriveActivityMonitorRepository"/>.
/// The only non-test file that touches the <c>DriveActivityMonitor:LastRunAt</c>
/// row in <c>DbContext.SystemSettings</c> after the Google Integration §15
/// migration lands. Anomaly audit entries are written by the AuditLog section
/// via <c>IAuditLogService</c> (the service layer owns that cross-section call);
/// this repository only persists the monitor's own state.
/// Uses <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> remains Scoped.
/// </summary>
[Grandfathered("HUM0025", justification: "Per-key SystemSettings access shared with EmailOutboxRepository (disjoint keys); split the table or route through an owning service.", since: "2026-05-25", issueRef: "docs/superpowers/specs/2026-05-25-analyzer-consolidation.md", scope: "SystemSettings")]
[Grandfathered("HUM0025", justification: "Cross-section read of Identity UserLogins; migrate to IUserService.", since: "2026-05-25", issueRef: "docs/superpowers/specs/2026-05-25-analyzer-consolidation.md", scope: "UserLogins")]
[Grandfathered("HUM0025", justification: "Cross-section read of the Users table; migrate to IUserService.", since: "2026-05-25", issueRef: "docs/superpowers/specs/2026-05-25-analyzer-consolidation.md", scope: "Users")]
internal sealed class DriveActivityMonitorRepository(
    IDbContextFactory<HumansDbContext> factory,
    ILogger<DriveActivityMonitorRepository> logger) : IDriveActivityMonitorRepository
{
    /// <summary>
    /// <c>system_settings</c> key under which the monitor stores the instant
    /// of its last fully-successful run. Shared with no other consumer.
    /// </summary>
    internal const string LastRunSettingKey = "DriveActivityMonitor:LastRunAt";

    public async Task<Instant?> GetLastRunTimestampAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var setting = await ctx.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == LastRunSettingKey, ct);

        if (setting is null || string.IsNullOrEmpty(setting.Value))
        {
            return null;
        }

        var pattern = NodaTime.Text.InstantPattern.General;
        var result = pattern.Parse(setting.Value);
        if (result.Success)
        {
            return result.Value;
        }

        logger.LogWarning(
            "Could not parse stored Drive activity monitor timestamp '{Value}', falling back to default lookback",
            setting.Value);
        return null;
    }

    public async Task AdvanceLastRunMarkerAsync(
        Instant? newLastRunAt,
        CancellationToken ct = default)
    {
        if (newLastRunAt is null)
        {
            // Nothing to persist — avoid opening a context.
            return;
        }

        await using var ctx = await factory.CreateDbContextAsync(ct);

        var value = newLastRunAt.Value.ToInvariantInstantString();
        var setting = await ctx.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == LastRunSettingKey, ct);

        if (setting is not null)
        {
            setting.Value = value;
        }
        else
        {
            ctx.SystemSettings.Add(new SystemSetting
            {
                Key = LastRunSettingKey,
                Value = value,
            });
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<string?> TryResolveEmailByGoogleUserIdAsync(
        string googleUserId, CancellationToken ct = default)
    {
        try
        {
            await using var ctx = await factory.CreateDbContextAsync(ct);

            // ASP.NET Identity user logins for Google provider key → user id.
            var login = await ctx.Set<IdentityUserLogin<Guid>>()
                .AsNoTracking()
                .FirstOrDefaultAsync(l =>
                    l.ProviderKey == googleUserId &&
                    l.LoginProvider == "Google",
                    ct);

            if (login is null)
            {
                return null;
            }

            var email = await ctx.Users
                .AsNoTracking()
                .Where(u => u.Id == login.UserId)
                .Select(u => u.Email)
                .FirstOrDefaultAsync(ct);

            return email;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Error resolving Google user id {GoogleUserId} via local DB", googleUserId);
            return null;
        }
    }
}
