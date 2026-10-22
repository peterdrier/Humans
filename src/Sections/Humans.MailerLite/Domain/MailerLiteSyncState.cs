using NodaTime;

namespace Humans.MailerLite.Domain;

/// <summary>
/// Current state, not history: one row per key, overwritten each run. <see cref="Id"/> must survive runs, since
/// the audit row points at it. The reconciliation row leaves the counts at zero and puts its numbers in
/// <see cref="Summary"/>.
/// </summary>
internal sealed class MailerLiteSyncState
{
    public Guid Id { get; init; }

    /// <summary>Audience key ("has-shift"), or <see cref="MailerLiteSyncKeys.Reconciliation"/>.</summary>
    public string Key { get; init; } = "";

    public Instant LastSyncAt { get; set; }

    public string Summary { get; set; } = "";

    public string? GroupId { get; set; }
    public string? GroupName { get; set; }

    public int Candidates { get; set; }
    public int ExcludedUnsubscribed { get; set; }
    public int Created { get; set; }
    public int Assigned { get; set; }
    public int AlreadyAssigned { get; set; }
    public int Unassigned { get; set; }
    public int Errors { get; set; }
}

internal static class MailerLiteSyncKeys
{
    /// <summary>No audience may claim this key.</summary>
    public const string Reconciliation = "import-reconciliation";
}
