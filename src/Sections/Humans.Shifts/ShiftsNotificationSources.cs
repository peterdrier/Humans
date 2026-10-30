using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.Shifts;

/// <summary>The notification sources this section emits. Names are stored on rows: never change one.</summary>
internal sealed class ShiftsNotificationSources : NotificationSourceContributor
{
    public static readonly NotificationSourceDefinition ShiftCoverageGap = new("ShiftCoverageGap", MessageCategory.VolunteerUpdates, NotificationInboxGroup.Shifts);

    /// <summary>Confirm, bail or cancel.</summary>
    public static readonly NotificationSourceDefinition ShiftSignupChange = new("ShiftSignupChange", MessageCategory.VolunteerUpdates, NotificationInboxGroup.Shifts);

    /// <summary>Enrolled (voluntold) by a coordinator.</summary>
    public static readonly NotificationSourceDefinition ShiftAssigned = new("ShiftAssigned", MessageCategory.VolunteerUpdates);
}
