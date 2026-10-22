namespace Humans.MailerLite.Services;

/// <summary>Maps to one MailerLite group, whose name must start with "Humans - ".</summary>
internal interface IMailerLiteAudience
{
    /// <summary>Stable URL-safe key (e.g. "ticket-no-shifts").</summary>
    string Key { get; }

    string DisplayName { get; }

    string MailerLiteGroupName { get; }

    Task<IReadOnlySet<Guid>> ComputeMemberUserIdsAsync(CancellationToken ct);
}
