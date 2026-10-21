using Humans.Email.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services.Audiences;

/// <summary>
/// Base for all code-defined audiences. A subclass computes its raw member set via
/// <see cref="ComputeRawMemberUserIdsAsync"/>; this base then removes anyone who has
/// explicitly opted out of Marketing (an <see cref="ICommunicationPreferenceService"/> Marketing
/// preference with <c>OptedOut</c> true).
/// MailerLite rejects opted-out addresses, so they never belong in any group. Humans
/// with no Marketing preference (null) or who opted in (false) are kept.
/// </summary>
internal abstract class MailerLiteAudienceBase(IUserService users, ICommunicationPreferenceService prefs)
    : IMailerLiteAudience
{
    /// <summary>User reader, shared with subclasses that also enumerate users.</summary>
    protected IUserService Users { get; } = users;

    /// <summary>Communication-preference reader, shared with the Marketing audiences.</summary>
    protected ICommunicationPreferenceService Preferences { get; } = prefs;

    /// <summary>Every user's Marketing preference, keyed by user id; no entry = nothing recorded.</summary>
    protected Task<IReadOnlyDictionary<Guid, CommunicationPreferenceSnapshot>> MarketingPreferencesAsync(CancellationToken ct) =>
        Preferences.GetPreferencesForCategoryAsync(MessageCategory.Marketing, ct);

    public abstract string Key { get; }
    public abstract string DisplayName { get; }
    public abstract string MailerLiteGroupName { get; }

    public async Task<IReadOnlySet<Guid>> ComputeMemberUserIdsAsync(CancellationToken ct)
    {
        var raw = await ComputeRawMemberUserIdsAsync(ct);
        if (raw.Count == 0) return raw;

        var optedOut = (await MarketingPreferencesAsync(ct))
            .Where(kv => kv.Value.OptedOut)
            .Select(kv => kv.Key)
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
