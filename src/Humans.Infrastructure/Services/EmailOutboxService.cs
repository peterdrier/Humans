using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

public class EmailOutboxService : IEmailOutboxService
{
    private readonly HumansDbContext _dbContext;

    public EmailOutboxService(HumansDbContext dbContext)
    {
        _dbContext = dbContext;
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
}
