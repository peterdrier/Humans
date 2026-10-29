using Humans.Email.Contracts;

namespace Humans.Notifications.Contracts;

/// <summary>
/// One kind of notification a section emits. The section declares it once, as a static field on its
/// <see cref="NotificationSourceContributor"/>, and passes it to every emit and resolve call.
/// </summary>
/// <param name="Name">Stored on every row, so never change it once rows exist. Unique across sections, at most <see cref="NameMaxLength"/> characters.</param>
/// <param name="Category">The preference category whose inbox toggle suppresses informational rows.</param>
/// <param name="InboxGroup">The inbox filter that lists this source besides All.</param>
/// <param name="Retired">Nothing emits it and nothing can resolve it any more: the nightly purge deletes its unresolved rows.</param>
public sealed record NotificationSourceDefinition(
    string Name,
    MessageCategory Category,
    NotificationInboxGroup InboxGroup = NotificationInboxGroup.None,
    bool Retired = false)
{
    public const int NameMaxLength = 50;

    public override string ToString() => Name;
}

/// <summary>The inbox filters a source can belong to, besides All.</summary>
public enum NotificationInboxGroup
{
    None = 0,
    Shifts = 1,
    Approvals = 2,
}
