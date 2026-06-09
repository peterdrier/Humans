using Humans.Domain.Enums;

namespace Humans.Web.Models.Tables;

/// <summary>
/// Central enum-value → Bootstrap badge class registry for <see cref="CellFormat.EnumBadge"/> columns.
/// Views stop owning color decisions: add new mappings here, never inline in a view.
/// Unmapped values render as bg-secondary.
/// </summary>
public static class EnumBadgeMap
{
    private static readonly Dictionary<Enum, string> Map = new()
    {
        [TicketAttendeeStatus.Valid] = "bg-success",
        [TicketAttendeeStatus.CheckedIn] = "bg-info",
        [TicketAttendeeStatus.Void] = "bg-danger",
    };

    public static string For(Enum value) => Map.GetValueOrDefault(value, "bg-secondary");
}
