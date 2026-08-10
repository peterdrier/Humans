using System.Collections.Concurrent;

using Humans.Domain.Enums;

namespace Humans.UI.Models.Tables;

/// <summary>
/// Central enum-value → Bootstrap badge class registry for <see cref="CellFormat.EnumBadge"/> columns.
/// Views stop owning color decisions: add new mappings here, never inline in a view.
/// Unmapped values render as bg-secondary.
/// </summary>
/// <remarks>
/// The literal rows below are the sections that have not yet moved into their own projects
/// (nobodies-collective/Humans#866, G5), whose enums all still sit in
/// <c>Humans.Domain.Enums</c>. A section that has moved owns its rows and pushes them in from
/// <c>Section.Register</c> via <see cref="Register"/> — Base cannot name a moved section's enum,
/// and referencing the section's contracts leaf to get it back would end with
/// <c>Humans.UI</c> holding a reference to every section (Peter, 2026-08-09;
/// <c>memory/architecture/base-ui-registries-are-section-populated.md</c>). Each G5 move
/// therefore deletes its rows from here and adds one call there, and the literal ends empty.
/// </remarks>
public static class EnumBadgeMap
{
    // ConcurrentDictionary, not Dictionary: parallel integration-test classes each compose
    // their own host (per-test isolation, nobodies-collective/Humans#983), so Section.Register
    // calls race against this shared static — and a host mid-composition writes while another
    // host's requests read.
    private static readonly ConcurrentDictionary<Enum, string> Map = new()
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

    /// <summary>
    /// Adds a moved section's badge rows. Called from <c>ISection.Register</c>, so every write
    /// happens during <c>AddSections()</c> at composition time and the map is read-only by the
    /// first request — which is why a static suffices where <see cref="TableColumn{TRow}"/>'s
    /// render path has no DI to inject a registry through.
    /// </summary>
    /// <remarks>
    /// Idempotent per row, deliberately. A process composes the service collection more than
    /// once — <c>WebApplicationFactory</c> builds a host per integration-test class, and section
    /// architecture tests call <c>Register</c> against a throwaway <c>ServiceCollection</c> —
    /// while this map is static and outlives all of them. Re-registering a row with the same
    /// class is therefore normal and must be a no-op.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The value is already mapped to a <em>different</em> class. That is two owners disagreeing
    /// about one badge colour — a section-boundary bug — and letting the last registration win
    /// silently would hide it.
    /// </exception>
    public static void Register(IReadOnlyDictionary<Enum, string> rows)
    {
        foreach (var (value, cssClass) in rows)
        {
            // GetOrAdd makes check-and-add atomic; two hosts registering the same row
            // concurrently both see the winning value and agree.
            var existing = Map.GetOrAdd(value, cssClass);
            if (!string.Equals(existing, cssClass, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"{value.GetType().Name}.{value} is already mapped to '{existing}'; "
                    + $"cannot re-map it to '{cssClass}'.",
                    nameof(rows));
            }
        }
    }

    public static string For(Enum value) => Map.GetValueOrDefault(value, "bg-secondary");
}
