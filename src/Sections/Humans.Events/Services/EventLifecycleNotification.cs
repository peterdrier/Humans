using Humans.Events.Contracts;

namespace Humans.Events.Services;

/// <summary>
/// Payload for an event lifecycle notification. <see cref="NewStatus"/> picks
/// the template: <see cref="EventStatus.Pending"/> = submission received,
/// <see cref="EventStatus.Approved"/> = approved, <see cref="EventStatus.Rejected"/>
/// = rejected (requires <see cref="Reason"/> and <see cref="ActionUrl"/> for the
/// edit link), <see cref="EventStatus.ResubmitRequested"/> = changes requested
/// (also requires <see cref="Reason"/> and <see cref="ActionUrl"/>).
/// Internal to Events: only <see cref="EventService"/> builds one and only
/// <see cref="EventsEmails"/> reads it (peterdrier/Humans#1651).
/// </summary>
internal sealed record EventLifecycleNotification(
    EventStatus NewStatus,
    string UserName,
    string EventTitle,
    string? Reason = null,
    string? ActionUrl = null,
    string? Culture = null)
{
    public string TemplateName() => NewStatus switch
    {
        EventStatus.Pending => "event_submitted",
        EventStatus.Approved => "event_approved",
        EventStatus.Rejected => "event_rejected",
        EventStatus.ResubmitRequested => "event_resubmit_requested",
        _ => throw new InvalidOperationException(
            $"EventLifecycleNotification does not support status {NewStatus}")
    };
}
