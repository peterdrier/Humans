using System.Text;
using Humans.Base.Extensions;
using NodaTime;

namespace Humans.AuditLog.Contracts;

/// <summary>
/// Built only by <see cref="IAuditViewerService"/>. Rendered output never contains a raw GUID. <c>Action</c> is the
/// stored name resolved to its definition; a name nobody declares any more is a bare definition with no verb.
/// </summary>
public sealed record AuditEvent(
    Guid Id,
    Instant OccurredAt,
    AuditActionDefinition Action,
    Guid? ActorUserId,
    string? ActorDisplayName,
    string EntityType,
    Guid EntityId,
    Guid? SubjectUserId,
    string? SubjectDisplayName,
    Guid? TargetTeamId,
    string? TargetTeamName,
    string? TargetTeamSlug,
    Guid? RelatedEntityId,
    string? RelatedEntityType,
    string Description)
{
    /// <summary>
    /// Uses "You" and the self-verb when <paramref name="viewerUserId"/> is the actor or subject. Null when the
    /// action has no verb.
    /// </summary>
    public string? RenderPlainText(Guid? viewerUserId = null)
    {
        var verb = Action.Verb;
        if (verb is null)
            return null;

        var actorIsViewer = viewerUserId.HasValue && ActorUserId == viewerUserId.Value;
        var subjectIsViewer = viewerUserId.HasValue && SubjectUserId == viewerUserId.Value;
        var actorIsSubject = ActorUserId.HasValue && SubjectUserId.HasValue
            && ActorUserId.Value == SubjectUserId.Value;

        string actor = ActorUserId.HasValue
            ? actorIsViewer
                ? "You"
                : (ActorDisplayName ?? "Someone")
            : "System";

        // Suppress subject when actor == subject ("You signed up", not "You created signup for You").
        string? subject = null;
        if (SubjectUserId.HasValue && !actorIsSubject)
        {
            subject = subjectIsViewer
                ? actorIsViewer ? "you" : "You"
                : (SubjectDisplayName ?? "someone");
        }

        var noVisibleSubject = subject is null;
        var displayVerb = noVisibleSubject
            ? Action.SelfVerb ?? TrimDanglingPreposition(verb)
            : verb;

        var sb = new StringBuilder();
        sb.Append(FormatDate(OccurredAt));
        sb.Append(" — ");
        sb.Append(actor);
        sb.Append(' ');
        sb.Append(displayVerb);
        if (subject is not null)
        {
            sb.Append(' ');
            sb.Append(subject);
        }

        if (TargetTeamId.HasValue && !string.IsNullOrEmpty(TargetTeamName))
        {
            sb.Append(" in ");
            sb.Append(TargetTeamName);
        }

        if (!string.IsNullOrWhiteSpace(Description) && Action.DescriptionIsTail)
        {
            sb.Append(" — ");
            sb.Append(Description.Trim());
        }

        return sb.ToString();
    }

    public AuditEventRender RenderStructured() =>
        new(
            Verb: Action.Verb,
            ShouldRenderDescriptionTail: !string.IsNullOrWhiteSpace(Description) && Action.DescriptionIsTail);

    /// <summary>Trims a dangling " for"/" to" preposition when the verb has no subject.</summary>
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

public sealed record AuditEventRender(
    string? Verb,
    bool ShouldRenderDescriptionTail);
