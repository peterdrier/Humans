using System.Globalization;

namespace Humans.MailerLite.Services.Dtos;

/// <summary>Mirrors the audit metadata.</summary>
internal sealed record AudienceSyncResult(
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
    public string FormatSummary() => FormatSummary(Created, Assigned, Unassigned, Errors);

    internal static string FormatSummary(IReadOnlyList<AudienceSyncResult> results) => FormatSummary(
        results.Sum(r => r.Created), results.Sum(r => r.Assigned),
        results.Sum(r => r.Unassigned), results.Sum(r => r.Errors));

    private static string FormatSummary(int created, int assigned, int unassigned, int errors) => string.Create(
        CultureInfo.InvariantCulture,
        $"{created} created, {assigned} newly assigned, {unassigned} unassigned, {errors} errors.");
}
