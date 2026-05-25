using AwesomeAssertions;
using Humans.Application.Services.AuditLog;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Tests.AuditLog;

/// <summary>
/// Tests for <see cref="AuditEvent"/> — the resolved, display-ready view of
/// an audit log entry. Covers <see cref="AuditEvent.RenderPlainText"/> and
/// <see cref="AuditEvent.RenderStructured"/>.
///
/// Tests for the underlying verb tables live in
/// <see cref="AuditEventTextualizerTests"/>.
/// </summary>
public class AuditEventTests
{
    private static readonly Instant FixedAt = Instant.FromUtc(2026, 4, 30, 17, 0);
    private const string ExpectedDate = "2026-04-30";

    [HumansFact]
    public void ActorIsViewer_RendersYouAndSelfVerb()
    {
        var viewer = Guid.NewGuid();
        var ev = MakeEvent(
            action: AuditAction.ShiftSignupCreated,
            actorId: viewer,
            actorName: "Frank",
            entityType: "ShiftSignup",
            entityId: Guid.NewGuid(),
            description: "shift 'Cantina dinner @ 18:00'");

        var line = ev.RenderPlainText(viewer);

        line.Should().Be($"{ExpectedDate} — You signed up for — shift 'Cantina dinner @ 18:00'");
    }

    [HumansFact]
    public void SubjectIsViewer_RendersOtherActorAndYouSubject()
    {
        var viewer = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var ev = MakeEvent(
            action: AuditAction.ShiftSignupVoluntold,
            actorId: actor,
            actorName: "Frank",
            entityType: "ShiftSignup",
            entityId: Guid.NewGuid(),
            relatedEntityType: "User",
            relatedEntityId: viewer,
            subjectId: viewer,
            subjectName: "Peter",
            description: "shift 'Cantina dinner'");

        var line = ev.RenderPlainText(viewer);

        line.Should().Be($"{ExpectedDate} — Frank voluntold You — shift 'Cantina dinner'");
    }

    [HumansFact]
    public void TailStyleAction_AppendsDescriptionTail()
    {
        var ev = MakeEvent(
            action: AuditAction.ShiftSignupBailed,
            actorId: Guid.NewGuid(),
            actorName: "Peter",
            entityType: "ShiftSignup",
            entityId: Guid.NewGuid(),
            description: "shift 'Cleanup'");

        var line = ev.RenderPlainText();

        line.Should().Contain("Peter bailed");
        line.Should().EndWith("— shift 'Cleanup'");
    }

    [HumansFact]
    public void SentenceStyleAction_DoesNotAppendDescriptionTail()
    {
        var ev = MakeEvent(
            action: AuditAction.TeamJoinedDirectly,
            actorId: Guid.NewGuid(),
            actorName: "Peter",
            entityType: "Team",
            entityId: Guid.NewGuid(),
            targetTeamId: null,
            targetTeamName: "Build",
            targetTeamSlug: "build",
            description: "Joined Build directly");

        var line = ev.RenderPlainText();

        line.Should().NotContain("— Joined Build directly");
    }

    [HumansFact]
    public void MissingActor_RendersSystem()
    {
        var ev = MakeEvent(
            action: AuditAction.VolunteerApproved,
            actorId: null,
            actorName: null,
            entityType: "User",
            entityId: Guid.NewGuid(),
            subjectId: Guid.NewGuid(),
            subjectName: "Maria");

        var line = ev.RenderPlainText();

        line.Should().StartWith($"{ExpectedDate} — System approved Maria");
    }

    [HumansFact]
    public void UnmappedAction_ReturnsNull()
    {
        var ev = MakeEvent(
            action: AuditAction.AnomalousPermissionDetected,
            actorId: null,
            actorName: null,
            entityType: "GoogleResource",
            entityId: Guid.NewGuid());

        ev.RenderPlainText().Should().BeNull();
    }

    [HumansFact]
    public void GoogleSyncEvent_RendersStructuredFields()
    {
        var ev = MakeEvent(
            action: AuditAction.GoogleResourceAccessGranted,
            actorId: null,
            actorName: null,
            entityType: "GoogleResource",
            entityId: Guid.NewGuid(),
            description: "Granted reader",
            role: "reader",
            userEmail: "peter@nobodies.example",
            success: true,
            syncSource: GoogleSyncSource.ManualSync,
            resourceName: "Build Drive");

        var line = ev.RenderPlainText();

        line.Should().Contain("GoogleResourceAccessGranted reader");
        line.Should().Contain("for peter@nobodies.example");
        line.Should().Contain("on Build Drive");
        line.Should().Contain("(ManualSync)");
    }

    [HumansFact]
    public void GoogleSyncFailure_AppendsErrorMessage()
    {
        var ev = MakeEvent(
            action: AuditAction.GoogleResourceAccessGranted,
            actorId: null,
            actorName: null,
            entityType: "GoogleResource",
            entityId: Guid.NewGuid(),
            role: "writer",
            userEmail: "p@x",
            success: false,
            errorMessage: "API quota exceeded",
            syncSource: GoogleSyncSource.SystemTeamSync,
            resourceName: "Drive A");

        var line = ev.RenderPlainText();

        line.Should().Contain("failed: API quota exceeded");
    }

    [HumansFact]
    public void RenderStructured_ReturnsVerbAndTrimmedVerb()
    {
        var ev = MakeEvent(
            action: AuditAction.ShiftSignupCreated,
            actorId: Guid.NewGuid(),
            actorName: "Peter",
            entityType: "ShiftSignup",
            entityId: Guid.NewGuid());

        var render = ev.RenderStructured();

        render.Verb.Should().Be("created signup for");
        render.SelfVerb.Should().Be("signed up for");
        render.TrimmedVerb.Should().Be("created signup");
        render.ShouldRenderDescriptionTail.Should().BeFalse(); // Description is empty
    }

    [HumansFact]
    public void RenderPlainText_NeverContainsRawGuid()
    {
        var viewer = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        var ev = MakeEvent(
            action: AuditAction.TeamMemberAdded,
            actorId: actor,
            actorName: "Frank",
            entityType: "Team",
            entityId: teamId,
            relatedEntityType: "User",
            relatedEntityId: subject,
            subjectId: subject,
            subjectName: "Peter",
            targetTeamId: teamId,
            targetTeamName: "Build",
            targetTeamSlug: "build",
            description: "Build");

        var line = ev.RenderPlainText(viewer)!;

        line.Should().NotContain(viewer.ToString());
        line.Should().NotContain(actor.ToString());
        line.Should().NotContain(subject.ToString());
        line.Should().NotContain(entityId.ToString());
        line.Should().NotContain(teamId.ToString());
    }

    private static AuditEvent MakeEvent(
        AuditAction action,
        Guid? actorId,
        string? actorName,
        string entityType,
        Guid entityId,
        Guid? subjectId = null,
        string? subjectName = null,
        Guid? targetTeamId = null,
        string? targetTeamName = null,
        string? targetTeamSlug = null,
        Guid? relatedEntityId = null,
        string? relatedEntityType = null,
        string description = "",
        string? role = null,
        string? userEmail = null,
        bool? success = null,
        string? errorMessage = null,
        GoogleSyncSource? syncSource = null,
        Guid? resourceId = null,
        string? resourceName = null)
    {
        return new AuditEvent(
            Id: Guid.NewGuid(),
            OccurredAt: FixedAt,
            Action: action,
            ActorUserId: actorId,
            ActorDisplayName: actorName,
            EntityType: entityType,
            EntityId: entityId,
            SubjectUserId: subjectId,
            SubjectDisplayName: subjectName,
            TargetTeamId: targetTeamId,
            TargetTeamName: targetTeamName,
            TargetTeamSlug: targetTeamSlug,
            RelatedEntityId: relatedEntityId,
            RelatedEntityType: relatedEntityType,
            Description: description,
            Role: role,
            UserEmail: userEmail,
            Success: success,
            ErrorMessage: errorMessage,
            SyncSource: syncSource,
            ResourceId: resourceId,
            ResourceName: resourceName);
    }
}
