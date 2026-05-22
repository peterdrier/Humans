using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Mailer.Audiences;

/// <summary>
/// Base for all code-defined audiences. A subclass computes its raw member set via
/// <see cref="ComputeRawMemberUserIdsAsync"/>; this base then removes anyone who has
/// explicitly opted out of Marketing (<see cref="UserInfo.MarketingOptedOut"/> == true).
/// MailerLite rejects opted-out addresses, so they never belong in any group. Humans
/// with no Marketing preference (null) or who opted in (false) are kept.
/// </summary>
public abstract class MailerAudienceBase(IUserServiceRead users) : IMailerAudience
{
    /// <summary>User reader, shared with subclasses that also enumerate users.</summary>
    protected IUserServiceRead Users { get; } = users;

    public abstract string Key { get; }
    public abstract string DisplayName { get; }
    public abstract string MailerLiteGroupName { get; }

    public async Task<IReadOnlySet<Guid>> ComputeMemberUserIdsAsync(CancellationToken ct)
    {
        var raw = await ComputeRawMemberUserIdsAsync(ct);
        if (raw.Count == 0) return raw;

        var optedOut = (await Users.GetAllUserInfosAsync(ct))
            .Where(u => u.MarketingOptedOut == true)
            .Select(u => u.Id)
            .ToHashSet();

        return optedOut.Count == 0
            ? raw
            : raw.Where(id => !optedOut.Contains(id)).ToHashSet();
    }

    /// <summary>
    /// The audience-specific member set, before the universal Marketing opt-out
    /// exclusion applied by <see cref="ComputeMemberUserIdsAsync"/>.
    /// </summary>
    protected abstract Task<IReadOnlySet<Guid>> ComputeRawMemberUserIdsAsync(CancellationToken ct);
}
