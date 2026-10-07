using Humans.AuditLog.Contracts;
using Humans.AuditLog.Services;
using Humans.Base.Extensions;
using Microsoft.Extensions.Localization;

namespace Humans.AuditLog.Models;

internal static class AuditLogUiExtensions
{
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

    public static bool IsAnomalousPermissionAction(this AuditAction action)
    {
        return action == AuditAction.AnomalousPermissionDetected;
    }

    public static string ToAuditEntryRowClass(this AuditAction action)
    {
        return action.IsAnomalousPermissionAction() ? "table-warning" : string.Empty;
    }

    public static string ToAuditBadgeClass(this AuditAction action)
    {
        return action.IsAnomalousPermissionAction() ? "bg-warning text-dark" : "bg-secondary";
    }

    public static string ToAuditBadgeLabel(
        this AuditAction action,
        IStringLocalizer<AuditLogResource>? localizer = null)
    {
        if (localizer is null)
            return action.IsAnomalousPermissionAction() ? "Anomaly" : action.ToString();

        return action.IsAnomalousPermissionAction()
            ? localizer["AuditLog_Anomaly"]
            : localizer.EnumDisplay(action);
    }

    public static string? ToLocalizedAuditVerb(
        this AuditAction action,
        bool noVisibleSubject,
        IStringLocalizer<AuditLogResource> localizer)
    {
        if (AuditEventTextualizer.GetActionVerb(action) is null)
            return null;

        var form = noVisibleSubject
            ? AuditEventTextualizer.GetActionSelfVerb(action) is not null ? "SelfVerb" : "NoSubjectVerb"
            : "Verb";
        return localizer[$"AuditLog_{form}_{action}"];
    }
}
