namespace Humans.Application.Interfaces.Mailer;

/// <summary>
/// A code-defined mailing list. Each implementation computes a set of Humans
/// user-ids who belong in the audience and maps it to a single MailerLite group
/// whose <see cref="MailerLiteGroupName"/> must start with "Humans - "
/// (pinned by <c>MailerArchitectureTests.AllAudiences_UseHumansPrefix</c>).
/// </summary>
public interface IMailerAudience
{
    /// <summary>Stable URL-safe key (e.g. "ticket-no-shifts").</summary>
    string Key { get; }

    /// <summary>Display name shown on the dashboard card.</summary>
    string DisplayName { get; }

    /// <summary>Target MailerLite group name. Must start with "Humans - ".</summary>
    string MailerLiteGroupName { get; }

    /// <summary>
    /// Returns the current Humans user-ids who belong in this audience.
    /// Implementations read cross-section state via service interfaces only —
    /// never via <c>HumansDbContext</c> directly.
    /// </summary>
    Task<IReadOnlySet<Guid>> ComputeMemberUserIdsAsync(CancellationToken ct);
}
