using Humans.AuditLog.Contracts;
using Microsoft.Extensions.Localization;

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

    public static string ToAuditEntryRowClass(this AuditActionDefinition action)
    {
        return action.Alert ? "table-warning" : string.Empty;
    }

    public static string ToAuditBadgeClass(this AuditActionDefinition action)
    {
        return action.Alert ? "bg-warning text-dark" : "bg-secondary";
    }

    /// <summary>
    /// Admin pages (no localizer) show the stored name. Member pages take <c>Enum_AuditAction_{Name}</c> from the
    /// hub resx and fall back to the name split into words, so a section's new action renders before it is translated.
    /// </summary>
    public static string ToAuditBadgeLabel(
        this AuditActionDefinition action,
        IStringLocalizer<AuditLogResource>? localizer = null)
    {
        if (localizer is null)
            return action.Name;

        var localized = localizer[$"Enum_AuditAction_{action.Name}"];
        return localized.ResourceNotFound ? Humanize(action.Name) : localized.Value;
    }

    /// <summary>
    /// <c>AuditLog_{Verb|SelfVerb|NoSubjectVerb}_{Name}</c> from the hub resx, falling back to the definition's English
    /// verb. Null when the action has no verb.
    /// </summary>
    public static string? ToLocalizedAuditVerb(
        this AuditActionDefinition action,
        bool noVisibleSubject,
        IStringLocalizer<AuditLogResource> localizer)
    {
        if (action.Verb is null)
            return null;

        var form = noVisibleSubject
            ? action.SelfVerb is not null ? "SelfVerb" : "NoSubjectVerb"
            : "Verb";
        var localized = localizer[$"AuditLog_{form}_{action.Name}"];
        if (!localized.ResourceNotFound)
            return localized.Value;

        return noVisibleSubject
            ? action.SelfVerb ?? AuditEvent.TrimDanglingPreposition(action.Verb)
            : action.Verb;
    }

    private static string Humanize(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString();
    }
}
