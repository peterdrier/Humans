using NodaTime;

namespace Humans.MailerLite.Domain;

/// <summary>
/// Current sync state for one <see cref="Services.IMailerLiteAudience"/> — or for the import
/// reconciliation run, under <see cref="MailerLiteSyncKeys.Reconciliation"/>. One row per key,
/// overwritten on every run: this is current state, not history
/// (nobodies-collective/Humans#1082 — it used to be serialized into an
/// <c>audit_log</c> description and read back out).
/// </summary>
/// <remarks>
/// <see cref="Id"/> is what gives the sync's audit row a real subject to point at, so it must
/// survive across runs — the repository upserts on <see cref="Key"/> and never replaces the row.
/// The count columns describe an audience push; the reconciliation row leaves them at zero and
/// carries its numbers in <see cref="Summary"/>, which is what the dashboard renders.
/// </remarks>
internal sealed class MailerLiteSyncState
{
    public Guid Id { get; init; }

    /// <summary>Audience key ("has-shift"), or <see cref="MailerLiteSyncKeys.Reconciliation"/>.</summary>
    public string Key { get; init; } = "";

    public Instant LastSyncAt { get; set; }

    /// <summary>Human prose, shown on the MailerLite admin dashboard.</summary>
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

/// <summary>Sync keys that are not an audience key.</summary>
internal static class MailerLiteSyncKeys
{
    /// <summary>
    /// The import reconciliation run's row. Not an audience key — no
    /// <see cref="Services.IMailerLiteAudience"/> may claim it, which
    /// <c>MailerLiteArchitectureTests</c> pins.
    /// </summary>
    public const string Reconciliation = "import-reconciliation";
}
