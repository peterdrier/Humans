using Humans.Email.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services.Audiences;

/// <summary>
/// Removes anyone who explicitly opted out of Marketing: MailerLite rejects them. No preference, or opted in,
/// is kept.
/// </summary>
internal abstract class MailerLiteAudienceBase(IUserService users, ICommunicationPreferenceService prefs)
    : IMailerLiteAudience
{
    protected IUserService Users { get; } = users;

    protected ICommunicationPreferenceService Preferences { get; } = prefs;

    /// <summary>No entry means nothing recorded.</summary>
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

    /// <summary>Before the Marketing opt-out exclusion.</summary>
    protected abstract Task<IReadOnlySet<Guid>> ComputeRawMemberUserIdsAsync(CancellationToken ct);
}
