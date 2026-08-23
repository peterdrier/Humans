using Humans.Settings.Contracts;
using Humans.Settings.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Settings.Data;

internal sealed class Repository(IDbContextFactory<SettingsDbContext> factory)
    : ISettingsRepository
{
    public async Task<string?> GetValueAsync(string key, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Settings
            .AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct);
    }

    public async Task SetValueAsync(string key, string value, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var setting = await ctx.Settings
            .FirstOrDefaultAsync(s => s.Key == key, ct);

        if (setting is null)
        {
            ctx.Settings.Add(new Setting
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

    public async Task<EventSettings?> GetActiveEventSettingsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Status == EventSettingsStatus.Active, ct);
    }

    public async Task<EventSettings?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<bool> AnyOtherActiveEventSettingsAsync(Guid excludingId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventSettings
            .AsNoTracking()
            .AnyAsync(e => e.Status == EventSettingsStatus.Active && e.Id != excludingId, ct);
    }

    public async Task UpsertEventSettingsAsync(
        EventSettings settings, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.EventSettings.FirstOrDefaultAsync(e => e.Id == settings.Id, ct);

        if (existing is null)
        {
            settings.CreatedAt = now;
            settings.UpdatedAt = now;
            ctx.EventSettings.Add(settings);
        }
        else
        {
            existing.EventName = settings.EventName;
            existing.Year = settings.Year;
            existing.TimeZoneId = settings.TimeZoneId;
            existing.GateOpeningDate = settings.GateOpeningDate;
            existing.BuildStartOffset = settings.BuildStartOffset;
            existing.EventEndOffset = settings.EventEndOffset;
            existing.StrikeEndOffset = settings.StrikeEndOffset;
            existing.FirstCrewStartOffset = settings.FirstCrewStartOffset;
            existing.SetupWeekStartOffset = settings.SetupWeekStartOffset;
            existing.PreEventWeekStartOffset = settings.PreEventWeekStartOffset;
            existing.FinishingWeekendStartOffset = settings.FinishingWeekendStartOffset;
            existing.EarlyEntryCapacity = settings.EarlyEntryCapacity;
            existing.BarriosEarlyEntryAllocation = settings.BarriosEarlyEntryAllocation;
            existing.EarlyEntryClose = settings.EarlyEntryClose;
            existing.Status = settings.Status;
            existing.UpdatedAt = now;
        }

        await ctx.SaveChangesAsync(ct);
    }
}
