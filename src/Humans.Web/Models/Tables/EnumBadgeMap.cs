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

        [CampaignStatus.Draft] = "bg-secondary",
        [CampaignStatus.Active] = "bg-success",
        [CampaignStatus.Completed] = "bg-info",

        [EmailOutboxStatus.Queued] = "bg-warning text-dark",
        [EmailOutboxStatus.Sent] = "bg-success",
        [EmailOutboxStatus.Failed] = "bg-danger",

        [ExpenseReportStatus.Draft] = "bg-secondary",
        [ExpenseReportStatus.Submitted] = "bg-primary",
        [ExpenseReportStatus.CoordinatorEndorsed] = "bg-info text-dark",
        [ExpenseReportStatus.Approved] = "bg-success",
        [ExpenseReportStatus.SepaSent] = "bg-warning text-dark",
        [ExpenseReportStatus.Paid] = "bg-success",
        [ExpenseReportStatus.Withdrawn] = "bg-secondary",

        [ShiftPeriod.Build] = "bg-info",
        [ShiftPeriod.Event] = "bg-success",
        [ShiftPeriod.Strike] = "bg-secondary",

        [SignupStatus.Pending] = "bg-warning text-dark",
        [SignupStatus.Confirmed] = "bg-success",
        [SignupStatus.Refused] = "bg-danger",
        [SignupStatus.Bailed] = "bg-secondary",
        [SignupStatus.Cancelled] = "bg-dark",
        [SignupStatus.NoShow] = "bg-danger",

        [TicketPaymentStatus.Paid] = "bg-success",
        [TicketPaymentStatus.Pending] = "bg-warning text-dark",
        [TicketPaymentStatus.Refunded] = "bg-danger",
        [TicketPaymentStatus.Cancelled] = "bg-secondary",

        [VoteChoice.Yay] = "bg-success",
        [VoteChoice.Maybe] = "bg-warning text-dark",
        [VoteChoice.No] = "bg-danger",
        [VoteChoice.Abstain] = "bg-secondary",
    };

    public static string For(Enum value) => Map.GetValueOrDefault(value, "bg-secondary");
}
