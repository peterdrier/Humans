using Humans.Events.Contracts;

namespace Humans.Events.Services;

/// <summary>
/// <see cref="NewStatus"/> picks the template. Rejected and ResubmitRequested also need <see cref="Reason"/> and
/// <see cref="ActionUrl"/> (the edit link).
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
