using System.Text;
using Humans.Base.Extensions;
using Humans.AuditLog.Domain;
using Humans.AuditLog.Services;
using NodaTime;

namespace Humans.AuditLog.Contracts;

/// <summary>Built only by <see cref="IAuditViewerService"/>. Rendered output never contains a raw GUID.</summary>
public sealed record AuditEvent(
    Guid Id,
    Instant OccurredAt,
    AuditAction Action,
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
    /// action has no verb mapping.
    /// </summary>
    public string? RenderPlainText(Guid? viewerUserId = null)
    {
        var verb = AuditEventTextualizer.GetActionVerb(Action);
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
            ? AuditEventTextualizer.GetActionSelfVerb(Action) ?? AuditEventTextualizer.TrimDanglingPreposition(verb)
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

        if (!string.IsNullOrWhiteSpace(Description)
            && AuditEventTextualizer.ShouldRenderDescriptionTail(Action))
        {
            sb.Append(" — ");
            sb.Append(Description.Trim());
        }

        return sb.ToString();
    }

    public AuditEventRender RenderStructured()
    {
        var verb = AuditEventTextualizer.GetActionVerb(Action);
        var renderTail = !string.IsNullOrWhiteSpace(Description)
            && AuditEventTextualizer.ShouldRenderDescriptionTail(Action);
        return new AuditEventRender(
            Verb: verb,
            ShouldRenderDescriptionTail: renderTail);
    }

    private static string FormatDate(Instant occurredAt) =>
        occurredAt.InUtc().Date.ToInvariantDate();
}

public sealed record AuditEventRender(
    string? Verb,
    bool ShouldRenderDescriptionTail);
