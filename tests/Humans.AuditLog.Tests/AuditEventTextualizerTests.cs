using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.Services;

namespace Humans.AuditLog.Tests;

public class AuditEventTextualizerTests
{
    [HumansFact]
    public void GetActionVerb_MappedAction_ReturnsVerb()
    {
        var verb = AuditEventTextualizer.GetActionVerb(AuditAction.TeamMemberAdded);

        verb.Should().Be("added",
            because: "TeamMemberAdded is a mapped action with a known transitive verb");
    }

    [HumansFact]
    public void GetActionVerb_UnmappedAction_ReturnsNull()
    {
        var verb = AuditEventTextualizer.GetActionVerb(AuditAction.AnomalousPermissionDetected);

        verb.Should().BeNull(
            because: "actions without a structured verb mapping return null so callers can skip or fall back");
    }

    [HumansFact]
    public void GetActionSelfVerb_ActionWithSelfForm_ReturnsSelfVerb()
    {
        var selfVerb = AuditEventTextualizer.GetActionSelfVerb(AuditAction.ShiftSignupCreated);

        selfVerb.Should().NotBeNull(
            because: "ShiftSignupCreated has a distinct self-form verb for when the actor is the viewer");
        selfVerb.Should().Be("signed up for");
    }

    [HumansFact]
    public void GetActionSelfVerb_ActionWithNoSelfForm_ReturnsNull()
    {
        var selfVerb = AuditEventTextualizer.GetActionSelfVerb(AuditAction.TeamMemberAdded);

        selfVerb.Should().BeNull(
            because: "actions without a distinct self-form verb return null; RenderPlainText falls back to TrimDanglingPreposition");
    }

    [HumansFact]
    public void TrimDanglingPreposition_VerbEndingWithFor_RemovesFor()
    {
        var trimmed = AuditEventTextualizer.TrimDanglingPreposition("approved signup for");

        trimmed.Should().Be("approved signup",
            because: "a dangling preposition at the end of a verb phrase is dropped when there is no visible subject");
    }

    [HumansFact]
    public void TrimDanglingPreposition_VerbWithNoPreposition_ReturnsUnchanged()
    {
        var trimmed = AuditEventTextualizer.TrimDanglingPreposition("joined");

        trimmed.Should().Be("joined",
            because: "verbs without a trailing preposition are returned as-is");
    }

    [HumansFact]
    public void ShouldRenderDescriptionTail_TailStyleAction_ReturnsTrue()
    {
        var should = AuditEventTextualizer.ShouldRenderDescriptionTail(AuditAction.ShiftSignupBailed);

        should.Should().BeTrue(
            because: "tail-style actions append the description as a trailing phrase");
    }

    [HumansFact]
    public void ShouldRenderDescriptionTail_SentenceStyleAction_ReturnsFalse()
    {
        var should = AuditEventTextualizer.ShouldRenderDescriptionTail(AuditAction.TeamJoinedDirectly);

        should.Should().BeFalse(
            because: "sentence-style actions render as a complete sentence without a description tail");
    }
}
