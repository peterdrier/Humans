using Humans.Users.Contracts;

namespace Humans.Gdpr.Models;

internal sealed class AccountStatusViewModel
{
    public required UserState State { get; init; }
    public required Guid UserId { get; init; }
    public required string ContactEmail { get; init; }
}

internal sealed class PendingDeletionViewModel
{
    public DateTime? ScheduledFor { get; init; }
}
