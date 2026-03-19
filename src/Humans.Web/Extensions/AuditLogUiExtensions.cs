using Humans.Domain.Enums;

namespace Humans.Web.Extensions;

public static class AuditLogUiExtensions
{
    private const string AnomalyActionName = nameof(AuditAction.AnomalousPermissionDetected);

    public static bool IsAuditFilterSelected(this string? currentFilter, string filter)
    {
        return string.Equals(currentFilter, filter, StringComparison.Ordinal);
    }

    public static string ToAuditFilterButtonClass(
        this string? currentFilter,
        string filter,
        string selectedClass,
        string defaultClass)
    {
        return currentFilter.IsAuditFilterSelected(filter) ? selectedClass : defaultClass;
    }

    public static bool IsAnomalousPermissionAction(this string? action)
    {
        return string.Equals(action, AnomalyActionName, StringComparison.Ordinal);
    }

    public static string ToAuditEntryRowClass(this string? action)
    {
        return action.IsAnomalousPermissionAction() ? "table-warning" : string.Empty;
    }

    public static string ToAuditBadgeClass(this string? action)
    {
        return action.IsAnomalousPermissionAction() ? "bg-warning text-dark" : "bg-secondary";
    }

    public static string ToAuditBadgeLabel(this string? action)
    {
        return action.IsAnomalousPermissionAction() ? "Anomaly" : action ?? string.Empty;
    }
}
