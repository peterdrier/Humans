using System.Globalization;

namespace Humans.Application.Interfaces.Mailer.Dtos;

/// <summary>Post-sync counts for one audience. Mirrors the audit metadata.</summary>
public sealed record AudienceSyncResult(
    string Key,
    string GroupId,
    string GroupName,
    int Candidates,
    int ExcludedUnsubscribed,
    int Created,
    int Assigned,
    int AlreadyAssigned,
    int Unassigned,
    int Errors)
{
    public string FormatSummary() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Created} created, {Assigned} newly assigned, {Unassigned} unassigned, {Errors} errors.");
}
