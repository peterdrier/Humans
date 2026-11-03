namespace Humans.AuditLog.Models;

internal static class AuditLogUiExtensions
{
    public static string ToAuditFilterButtonClass(
        this string? currentFilter,
        string filter,
        string selectedClass,
        string defaultClass)
    {
        return string.Equals(currentFilter, filter, StringComparison.Ordinal) ? selectedClass : defaultClass;
    }

}
