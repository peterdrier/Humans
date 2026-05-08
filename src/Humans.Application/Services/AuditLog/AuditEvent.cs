using System.Globalization;
using System.Text;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.AuditLog;

/// <summary>
/// A resolved view of an <see cref="AuditLogEntry"/> — the raw entry plus
/// pre-resolved actor / subject / related-subject display names so render
/// targets (agent tool, view components, controllers) never need to chase
/// down user or team name lookups themselves.
/// </summary>
/// <remarks>
/// Owned by the Audit Log section. Constructed exclusively by
/// <see cref="IAuditViewerService"/>; do not new this up at call sites.
/// Privacy guard: <see cref="RenderPlainText"/> never emits a raw GUID — the
/// viewer's own id is substituted with "You", other users render as their
/// resolved display name only, and entries with no verb mapping render
/// nothing (the caller filters them out).
/// </remarks>
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
    /// Renders this event as a single-line English sentence suitable for the
    /// in-app agent's tool output. No GUIDs ever appear in the output.
    /// </summary>
    /// <param name="viewerUserId">
    /// When supplied, occurrences of this user (as actor or subject) are
    /// substituted with "You" / "you" instead of the resolved display name,
    /// and the self-form verb is used when actor == viewer.
    /// </param>
    /// <returns>
    /// A non-empty single-line sentence on success, or <c>null</c> when the
    /// action has no structured verb mapping — callers should filter null
    /// results rather than dumping a raw <see cref="Description"/> blob into
    /// agent context.
    /// </returns>
    public string? RenderPlainText(Guid? viewerUserId = null)
    {
        // Google sync entries have their own structured fields and aren't
        // verb-mapped — render them on a deterministic schema so the agent
        // can still surface "your Drive role on resource X was changed".
        if (SyncSource.HasValue)
            return RenderGoogleSync(viewerUserId);

        var verb = AuditEventTextualizer.GetActionVerb(Action);
        if (verb is null)
            return null;

        var actorIsViewer = viewerUserId.HasValue && ActorUserId == viewerUserId.Value;
        var subjectIsViewer = viewerUserId.HasValue && SubjectUserId == viewerUserId.Value;
        var actorIsSubject = ActorUserId.HasValue && SubjectUserId.HasValue
            && ActorUserId.Value == SubjectUserId.Value;

        // Actor token — "You" if the viewer acted, the resolved display name
        // otherwise, "System" for jobs (no actor id).
        string actor = ActorUserId.HasValue
            ? actorIsViewer
                ? "You"
                : (ActorDisplayName ?? "Someone")
            : "System";

        // Subject token. Suppressed when actor == subject (keeps the sentence
        // tight — "You signed up" not "You created signup for You").
        string? subject = null;
        if (SubjectUserId.HasValue && !actorIsSubject)
        {
            subject = subjectIsViewer
                ? actorIsViewer ? "you" : "You"
                : (SubjectDisplayName ?? "someone");
        }

        // When no subject will render, prefer the self-verb if defined; else
        // strip the dangling preposition off the transitive verb.
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
    /// Returns a structured render bundle for HTML composition. The verb
    /// and self-verb come from the same tables that power
    /// <see cref="RenderPlainText"/>, so the in-app audit log and the agent
    /// tool stay in lock-step.
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
        // Google sync entries describe a permission/role change against a
        // resource. The user-facing form: "<date> — <action> <role> for
        // <email or You> on <resource> (<source>)". We deliberately keep
        // GUIDs out of the output; resource name comes from the resolved
        // entity (or a fallback "a Google resource" if name lookup missed).
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
            // The viewer's own email isn't a GUID, so we don't need to mask
            // it; but rephrase to "You" when it matches the viewer's
            // primary subject for friendliness.
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
        occurredAt.InUtc().Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Structured render bundle returned by <see cref="AuditEvent.RenderStructured"/>.
/// View components compose HTML from these fields so the verb tables don't
/// need to be re-imported at the view layer.
/// </summary>
public sealed record AuditEventRender(
    string? Verb,
    string? SelfVerb,
    bool ShouldRenderDescriptionTail,
    string? TrimmedVerb);
