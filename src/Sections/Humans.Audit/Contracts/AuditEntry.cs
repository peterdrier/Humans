using System.Text;
using Humans.Base.Extensions;
using NodaTime;

namespace Humans.Audit.Contracts;

/// <summary>
/// Built only by <see cref="IAuditViewerService"/>. Rendered output never contains a raw GUID. <c>Action</c> is the
/// stored name resolved to its definition; a name nobody declares any more is a bare definition with no verb.
/// </summary>
public sealed record AuditEntry(
    Guid Id,
    Instant OccurredAt,
    AuditActionDefinition Action,
    Guid? ActorUserId,
    string? ActorDisplayName,
    string SubjectType,
    Guid SubjectId,
    Guid? PersonUserId,
    string? PersonDisplayName,
    Guid? TeamId,
    string? TeamName,
    string? TeamSlug,
    Guid? TargetId,
    string? TargetType,
    string Description,
    string? JobName = null)
{
    /// <summary>
    /// Uses "You" and the self-verb when <paramref name="viewerUserId"/> is the actor or the person. Null when the
    /// action has no verb.
    /// </summary>
    public string? RenderPlainText(Guid? viewerUserId = null)
    {
        var verb = Action.Verb;
        if (verb is null)
            return null;

        var actorIsViewer = viewerUserId.HasValue && ActorUserId == viewerUserId.Value;
        var personIsViewer = viewerUserId.HasValue && PersonUserId == viewerUserId.Value;
        var actorIsPerson = ActorUserId.HasValue && PersonUserId.HasValue
            && ActorUserId.Value == PersonUserId.Value;

        string actor = ActorUserId.HasValue
            ? actorIsViewer
                ? "You"
                : (ActorDisplayName ?? "Someone")
            : JobName is null ? "System" : $"System ({JobName})";

        string? person = null;
        if (PersonUserId.HasValue && !actorIsPerson)
        {
            person = personIsViewer
                ? actorIsViewer ? "you" : "You"
                : (PersonDisplayName ?? "someone");
        }

        var noVisiblePerson = person is null;
        var displayVerb = noVisiblePerson
            ? Action.SelfVerb ?? TrimDanglingPreposition(verb)
            : verb;

        var sb = new StringBuilder();
        sb.Append(FormatDate(OccurredAt));
        sb.Append(" — ");
        sb.Append(actor);
        sb.Append(' ');
        sb.Append(displayVerb);
        if (person is not null)
        {
            sb.Append(' ');
            sb.Append(person);
        }

        if (TeamId.HasValue && !string.IsNullOrEmpty(TeamName))
        {
            sb.Append(" in ");
            sb.Append(TeamName);
        }

        if (!string.IsNullOrWhiteSpace(Description) && Action.DescriptionIsTail)
        {
            sb.Append(" — ");
            sb.Append(Description.Trim());
        }

        return sb.ToString();
    }

    internal AuditEntryRender RenderStructured() =>
        new(
            Verb: Action.Verb,
            ShouldRenderDescriptionTail: !string.IsNullOrWhiteSpace(Description) && Action.DescriptionIsTail);

    /// <summary>Trims a dangling " for"/" to" preposition when the verb has no person.</summary>
    internal static string TrimDanglingPreposition(string verb)
    {
        if (verb.EndsWith(" for", StringComparison.Ordinal))
            return verb[..^4];
        if (verb.EndsWith(" to", StringComparison.Ordinal))
            return verb[..^3];
        return verb;
    }

    private static string FormatDate(Instant occurredAt) =>
        occurredAt.InUtc().Date.ToInvariantDate();
}

internal sealed record AuditEntryRender(
    string? Verb,
    bool ShouldRenderDescriptionTail);
