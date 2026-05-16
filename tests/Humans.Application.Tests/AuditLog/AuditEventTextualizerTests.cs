using AwesomeAssertions;
using Humans.Application.Services.AuditLog;
using Humans.Domain.Enums;

namespace Humans.Application.Tests.AuditLog;

/// <summary>
/// Tests for the internal <c>AuditEventTextualizer</c> verb-table class.
/// These exercise the verb-lookup logic in isolation — independent of the
/// full <see cref="AuditEvent.RenderPlainText"/> rendering pipeline tested
/// in <see cref="AuditEventTests"/>.
/// </summary>
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
        // ShiftSignupCreated maps to verb "created signup for" / self-verb "signed up for"
        var selfVerb = AuditEventTextualizer.GetActionSelfVerb(AuditAction.ShiftSignupCreated);

        selfVerb.Should().NotBeNull(
            because: "ShiftSignupCreated has a distinct self-form verb for when the actor is the viewer");
        selfVerb.Should().Be("signed up for");
    }

    [HumansFact]
    public void GetActionSelfVerb_ActionWithNoSelfForm_ReturnsNull()
    {
        // TeamMemberAdded has only a transitive verb — no dedicated self-form.
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
        // ShiftSignupBailed is a "tail-style" action — description appended after the verb.
        var should = AuditEventTextualizer.ShouldRenderDescriptionTail(AuditAction.ShiftSignupBailed);

        should.Should().BeTrue(
            because: "tail-style actions append the description as a trailing phrase");
    }

    [HumansFact]
    public void ShouldRenderDescriptionTail_SentenceStyleAction_ReturnsFalse()
    {
        // TeamJoinedDirectly is a "sentence-style" action — description not appended.
        var should = AuditEventTextualizer.ShouldRenderDescriptionTail(AuditAction.TeamJoinedDirectly);

        should.Should().BeFalse(
            because: "sentence-style actions render as a complete sentence without a description tail");
    }
}
