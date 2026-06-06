using System.Text;
using Humans.Application.Extensions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.AuditLog;

/// <summary>
/// Resolved view of an <see cref="AuditLogEntry"/> with pre-resolved actor/subject/team display names.
/// Constructed exclusively by <see cref="IAuditViewerService"/>. Privacy guard: no raw GUIDs in rendered output.
/// </summary>
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
    string Description,
    string? Role,
    string? UserEmail,
    bool? Success,
    string? ErrorMessage,
    GoogleSyncSource? SyncSource,
    Guid? ResourceId,
    string? ResourceName)
{
    /// <summary>
    /// Renders this event as a single-line sentence for the agent's tool output. Never emits raw GUIDs.
    /// When <paramref name="viewerUserId"/> matches actor/subject, substitutes "You"/"you" and uses the self-verb.
    /// Returns null when the action has no verb mapping (caller filters).
    /// </summary>
    public string? RenderPlainText(Guid? viewerUserId = null)
    {
        // Google sync entries use a separate structured schema.
        if (SyncSource.HasValue)
            return RenderGoogleSync(viewerUserId);

        var verb = AuditEventTextualizer.GetActionVerb(Action);
        if (verb is null)
            return null;

        var actorIsViewer = viewerUserId.HasValue && ActorUserId == viewerUserId.Value;
        var subjectIsViewer = viewerUserId.HasValue && SubjectUserId == viewerUserId.Value;
        var actorIsSubject = ActorUserId.HasValue && SubjectUserId.HasValue
            && ActorUserId.Value == SubjectUserId.Value;

        // "System" for jobs (no actor id).
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

    /// <summary>
    /// Structured render bundle for HTML composition. Shares verb tables with <see cref="RenderPlainText"/>.
    /// </summary>
    public AuditEventRender RenderStructured()
    {
        var verb = AuditEventTextualizer.GetActionVerb(Action);
        var selfVerb = verb is null ? null : AuditEventTextualizer.GetActionSelfVerb(Action);
        var renderTail = !string.IsNullOrWhiteSpace(Description)
            && AuditEventTextualizer.ShouldRenderDescriptionTail(Action);
        return new AuditEventRender(
            Verb: verb,
            SelfVerb: selfVerb,
            ShouldRenderDescriptionTail: renderTail,
            TrimmedVerb: verb is null ? null : AuditEventTextualizer.TrimDanglingPreposition(verb));
    }

    private string RenderGoogleSync(Guid? viewerUserId)
    {
        // Form: "<date> — <action> <role> for <email|You> on <resource> (<source>)". No GUIDs.
        var sb = new StringBuilder();
        sb.Append(FormatDate(OccurredAt));
        sb.Append(" — ");
        sb.Append(Action);

        if (!string.IsNullOrWhiteSpace(Role))
        {
            sb.Append(' ');
            sb.Append(Role);
        }

        if (!string.IsNullOrWhiteSpace(UserEmail))
        {
            sb.Append(" for ");
            var subjectIsViewer = viewerUserId.HasValue && SubjectUserId == viewerUserId.Value;
            sb.Append(subjectIsViewer ? "You" : UserEmail);
        }

        if (!string.IsNullOrWhiteSpace(ResourceName))
        {
            sb.Append(" on ");
            sb.Append(ResourceName);
        }

        if (SyncSource.HasValue)
        {
            sb.Append(" (");
            sb.Append(SyncSource.Value);
            sb.Append(')');
        }

        if (Success is false && !string.IsNullOrWhiteSpace(ErrorMessage))
        {
            sb.Append(" — failed: ");
            sb.Append(ErrorMessage.Trim());
        }

        return sb.ToString();
    }

    private static string FormatDate(Instant occurredAt) =>
        occurredAt.InUtc().Date.ToInvariantDate();
}

/// <summary>Structured render bundle from <see cref="AuditEvent.RenderStructured"/>.</summary>
public sealed record AuditEventRender(
    string? Verb,
    string? SelfVerb,
    bool ShouldRenderDescriptionTail,
    string? TrimmedVerb);
