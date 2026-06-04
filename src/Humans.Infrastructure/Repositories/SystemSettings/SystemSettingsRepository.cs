using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Repositories.SystemSettings;

internal sealed class SystemSettingsRepository(IDbContextFactory<HumansDbContext> factory)
    : ISystemSettingsRepository
{
    public async Task<string?> GetValueAsync(string key, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.SystemSettings
            .AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct);
    }

    public async Task SetValueAsync(string key, string value, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var setting = await ctx.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == key, ct);

        if (setting is null)
        {
            ctx.SystemSettings.Add(new SystemSetting
            {
                Key = key,
                Value = value,
            });
        }
        else
        {
            setting.Value = value;
        }

        await ctx.SaveChangesAsync(ct);
    }
}
