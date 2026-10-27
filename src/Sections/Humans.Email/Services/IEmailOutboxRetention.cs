namespace Humans.Email.Services;

internal interface IEmailOutboxRetention
{
    /// <summary>Deletes sent rows older than <c>Email:OutboxRetentionDays</c>. Returns rows deleted.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}
