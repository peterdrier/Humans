using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

public class EmailOutboxService : IEmailOutboxService
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;

    public EmailOutboxService(HumansDbContext dbContext, IClock clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    public async Task<string?> RetryMessageAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var message = await _dbContext.EmailOutboxMessages.FindAsync([id], cancellationToken);
        if (message is null) return null;

        message.Status = EmailOutboxStatus.Queued;
        message.RetryCount = 0;
        message.LastError = null;
        message.NextRetryAt = null;
        message.PickedUpAt = null;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return message.RecipientEmail;
    }

    public async Task<string?> DiscardMessageAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var message = await _dbContext.EmailOutboxMessages.FindAsync([id], cancellationToken);
        if (message is null) return null;

        var recipient = message.RecipientEmail;
        _dbContext.EmailOutboxMessages.Remove(message);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return recipient;
    }

    public async Task<EmailOutboxStats> GetOutboxStatsAsync(int recentMessageCount = 50, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var cutoff24H = now - Duration.FromHours(24);

        var totalCount = await _dbContext.EmailOutboxMessages.CountAsync(cancellationToken);
        var queuedCount = await _dbContext.EmailOutboxMessages
            .CountAsync(m => m.Status == EmailOutboxStatus.Queued, cancellationToken);
        var sentLast24H = await _dbContext.EmailOutboxMessages
            .CountAsync(m => m.Status == EmailOutboxStatus.Sent && m.SentAt > cutoff24H, cancellationToken);
        var failedCount = await _dbContext.EmailOutboxMessages
            .CountAsync(m => m.Status == EmailOutboxStatus.Failed, cancellationToken);

        var isPaused = await IsEmailPausedAsync(cancellationToken);

        var messages = await _dbContext.EmailOutboxMessages
            .Include(m => m.User)
            .OrderByDescending(m => m.CreatedAt)
            .Take(recentMessageCount)
            .ToListAsync(cancellationToken);

        return new EmailOutboxStats(totalCount, queuedCount, sentLast24H, failedCount, isPaused, messages);
    }

    public async Task<bool> IsEmailPausedAsync(CancellationToken cancellationToken = default)
    {
        var setting = await _dbContext.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == SystemSettingKeys.IsEmailSendingPaused, cancellationToken);
        return string.Equals(setting?.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SetEmailPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        var setting = await _dbContext.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == SystemSettingKeys.IsEmailSendingPaused, cancellationToken);
        if (setting is null)
        {
            setting = new SystemSetting { Key = SystemSettingKeys.IsEmailSendingPaused, Value = paused ? "true" : "false" };
            _dbContext.SystemSettings.Add(setting);
        }
        else
        {
            setting.Value = paused ? "true" : "false";
        }
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
