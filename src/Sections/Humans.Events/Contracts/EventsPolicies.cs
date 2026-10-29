namespace Humans.Events.Contracts;

/// <summary>Events' authorization policies, registered by its <c>Section</c>. Read admits Board; Manage never does.</summary>
public static class EventsPolicies
{
    /// <summary>Events Admin, Board or Admin: the event guide admin, moderation, dashboard and export pages.</summary>
    public const string Read = "EventsRead";

    /// <summary>Events Admin or Admin: moderation decisions, categories, venues and guide settings.</summary>
    public const string Manage = "EventsManage";
}
