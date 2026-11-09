using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Expenses.Contracts;
using Humans.Users.Contracts;

namespace Humans.Expenses.Services;

internal sealed class ExpenseReportAudit(
    IUserService userService,
    IAuditLogService auditLogService,
    ILogger<ExpenseReportService> logger) : IApplicationService
{
    /// <summary>Best-effort display name for an audit of an already committed edit.</summary>
    internal async Task<string> DescribeMemberAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            return (await userService.GetUserInfoAsync(userId, ct))?.BurnerName ?? userId.ToString();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning("Audit display-name lookup cancelled for member {UserId}; using member ID", userId);
        }
        catch (Exception ex)
        {
            // Name enrichment must not skip the audit or fail an edit that already committed.
            logger.LogError(ex, "Could not resolve audit display name for member {UserId}; using member ID", userId);
        }
        return userId.ToString();
    }

    /// <summary>
    /// An edit to somebody else's report owes them a trail naming both; a member editing their own leaves none.
    /// <paramref name="whatChanged"/> is the sentence opener, e.g. "Added line 'Fuel'".
    /// </summary>
    internal async Task AuditOnBehalfEditAsync(
        ExpenseReportDto report, Guid actorUserId, string whatChanged, CancellationToken ct)
    {
        if (actorUserId == report.SubmitterUserId) return;

        await auditLogService.LogAsync(
            ExpensesAuditActions.ExpenseEditedOnBehalf,
            AuditEntityTypes.Report, report.Id,
            $"{whatChanged} on behalf of {await DescribeMemberAsync(report.SubmitterUserId, ct)}.",
            actorUserId,
            relatedEntityId: report.SubmitterUserId,
            relatedEntityType: AuditSubjectTypes.User);
    }

    internal static string MaxAmountDetail(decimal? maxAmount) =>
        maxAmount is { } cap
            ? $" Authorized maximum {cap.ToString("0.00", CultureInfo.InvariantCulture)} EUR."
            : "";

    internal static string DescribeGrant(Guid? grantId) =>
        grantId is { } id ? $" Filed against pre-approval {id}." : "";

    internal static string DescribeNote(string? note) =>
        string.IsNullOrWhiteSpace(note) ? "cleared" : $"\"{note}\"";

}
