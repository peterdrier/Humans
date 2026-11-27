using AwesomeAssertions;
using Humans.Audit.Contracts;
using NodaTime;

namespace Humans.Audit.Tests;

public class AuditEntryTests
{
    private static readonly Instant FixedAt = Instant.FromUtc(2026, 4, 30, 17, 0);
    private const string ExpectedDate = "2026-04-30";

    [HumansFact]
    public void RenderPlainText_ActorIsViewer_RendersYou()
    {
        var viewer = Guid.NewGuid();
        var entry = MakeEntry(
            action: TestActions.ShiftSignupCreated,
            actorId: viewer,
            actorName: "Frank",
            subjectType: "ShiftSignup",
            subjectId: Guid.NewGuid(),
            description: "shift 'Cantina dinner @ 18:00'");

        var line = entry.RenderPlainText(viewer);

        line.Should().Be($"{ExpectedDate} — You signed up for — shift 'Cantina dinner @ 18:00'");
    }

    [HumansFact]
    public void RenderPlainText_PersonIsViewer_RendersYouAsPerson()
    {
        var viewer = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var entry = MakeEntry(
            action: TestActions.ShiftSignupVoluntold,
            actorId: actor,
            actorName: "Frank",
            subjectType: "ShiftSignup",
            subjectId: Guid.NewGuid(),
            targetType: "User",
            targetId: viewer,
            personId: viewer,
            personName: "Peter",
            description: "shift 'Cantina dinner'");

        var line = entry.RenderPlainText(viewer);

        line.Should().Be($"{ExpectedDate} — Frank voluntold You — shift 'Cantina dinner'");
    }

    [HumansFact]
    public void TailStyleAction_AppendsDescriptionTail()
    {
        var entry = MakeEntry(
            action: TestActions.ShiftSignupBailed,
            actorId: Guid.NewGuid(),
            actorName: "Peter",
            subjectType: "ShiftSignup",
            subjectId: Guid.NewGuid(),
            description: "shift 'Cleanup'");

        var line = entry.RenderPlainText();

        line.Should().Contain("Peter bailed");
        line.Should().EndWith("— shift 'Cleanup'");
    }

    [HumansFact]
    public void SentenceStyleAction_DoesNotAppendDescriptionTail()
    {
        var entry = MakeEntry(
            action: TestActions.TeamJoinedDirectly,
            actorId: Guid.NewGuid(),
            actorName: "Peter",
            subjectType: "Team",
            subjectId: Guid.NewGuid(),
            teamId: null,
            teamName: "Build",
            teamSlug: "build",
            description: "Joined Build directly");

        var line = entry.RenderPlainText();

        line.Should().NotContain("— Joined Build directly");
    }

    [HumansFact]
    public void MissingActor_RendersSystem()
    {
        var entry = MakeEntry(
            action: TestActions.VolunteerApproved,
            actorId: null,
            actorName: null,
            subjectType: "User",
            subjectId: Guid.NewGuid(),
            personId: Guid.NewGuid(),
            personName: "Maria");

        var line = entry.RenderPlainText();

        line.Should().StartWith($"{ExpectedDate} — System approved Maria");
    }

    [HumansFact]
    public void MissingActor_WithJob_RendersSystemWithJobName()
    {
        var entry = MakeEntry(
            action: TestActions.VolunteerApproved,
            actorId: null,
            actorName: null,
            subjectType: "User",
            subjectId: Guid.NewGuid(),
            personId: Guid.NewGuid(),
            personName: "Maria",
            jobName: "SystemTeamSyncJob");

        var line = entry.RenderPlainText();

        line.Should().StartWith($"{ExpectedDate} — System (SystemTeamSyncJob) approved Maria");
    }

    [HumansFact]
    public void UnmappedAction_ReturnsNull()
    {
        var entry = MakeEntry(
            action: TestActions.AnomalousPermissionDetected,
            actorId: null,
            actorName: null,
            subjectType: "GoogleResource",
            subjectId: Guid.NewGuid());

        entry.RenderPlainText().Should().BeNull();
    }

    [HumansFact]
    public void RenderStructured_ReturnsVerb()
    {
        var entry = MakeEntry(
            action: TestActions.ShiftSignupCreated,
            actorId: Guid.NewGuid(),
            actorName: "Peter",
            subjectType: "ShiftSignup",
            subjectId: Guid.NewGuid());

        var render = entry.RenderStructured();

        render.Verb.Should().Be("created signup for");
        render.ShouldRenderDescriptionTail.Should().BeFalse();
    }

    [HumansFact]
    public void RenderPlainText_NeverContainsRawGuid()
    {
        var viewer = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        var entry = MakeEntry(
            action: TestActions.TeamMemberAdded,
            actorId: actor,
            actorName: "Frank",
            subjectType: "Team",
            subjectId: teamId,
            targetType: "User",
            targetId: subject,
            personId: subject,
            personName: "Peter",
            teamId: teamId,
            teamName: "Build",
            teamSlug: "build",
            description: "Build");

        var line = entry.RenderPlainText(viewer)!;

        line.Should().NotContain(viewer.ToString());
        line.Should().NotContain(actor.ToString());
        line.Should().NotContain(subject.ToString());
        line.Should().NotContain(subjectId.ToString());
        line.Should().NotContain(teamId.ToString());
    }

    [HumansFact]
    public void TrimDanglingPreposition_TrailingForOrTo_IsDropped()
    {
        AuditEntry.TrimDanglingPreposition("approved signup for").Should().Be("approved signup");
        AuditEntry.TrimDanglingPreposition("assigned role to").Should().Be("assigned role");
        AuditEntry.TrimDanglingPreposition("joined").Should().Be("joined");
    }

    [HumansFact]
    public void UndeclaredAction_RendersNullLikeAnyVerblessAction()
    {
        var entry = MakeEntry(
            action: new AuditActionDefinition("SomethingNobodyDeclaresAnyMore", AuditKept.Forever),
            actorId: Guid.NewGuid(),
            actorName: "Peter",
            subjectType: "User",
            subjectId: Guid.NewGuid());

        entry.RenderPlainText().Should().BeNull();
        entry.RenderStructured().Verb.Should().BeNull();
    }

    private static AuditEntry MakeEntry(
        AuditActionDefinition action,
        Guid? actorId,
        string? actorName,
        string subjectType,
        Guid subjectId,
        Guid? personId = null,
        string? personName = null,
        Guid? teamId = null,
        string? teamName = null,
        string? teamSlug = null,
        Guid? targetId = null,
        string? targetType = null,
        string description = "",
        string? jobName = null)
    {
        return new AuditEntry(
            Id: Guid.NewGuid(),
            OccurredAt: FixedAt,
            Action: action,
            ActorUserId: actorId,
            ActorDisplayName: actorName,
            SubjectType: subjectType,
            SubjectId: subjectId,
            PersonUserId: personId,
            PersonDisplayName: personName,
            TeamId: teamId,
            TeamName: teamName,
            TeamSlug: teamSlug,
            TargetId: targetId,
            TargetType: targetType,
            Description: description,
            JobName: jobName);
    }
}
