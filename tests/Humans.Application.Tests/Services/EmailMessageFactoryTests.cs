using AwesomeAssertions;
using Humans.Application.Interfaces.Email;
using Humans.Application.Services.Email;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using NSubstitute;
using NSubstitute.Extensions;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Tests the per-type policy stamped by <see cref="EmailMessageFactory"/> around the
/// pure <see cref="IEmailRenderer"/> — template name, opt-out category, reply-to,
/// immediate-drain, recipient routing, and the campaign user/grant ids. The shared
/// transport (opt-out, unsubscribe, wrapping, enqueue) is covered by
/// <see cref="OutboxEmailServiceTests"/>.
/// </summary>
public sealed class EmailMessageFactoryTests
{
    private readonly IEmailRenderer _renderer = Substitute.For<IEmailRenderer>();
    private readonly EmailMessageFactory _factory;

    public EmailMessageFactoryTests()
    {
        // Any render returns known content so assertions focus on the stamped policy.
        _renderer.ReturnsForAll(new EmailContent("Subj", "<p>Body</p>"));
        _factory = new EmailMessageFactory(_renderer);
    }

    [HumansFact]
    public void ApplicationApproved_StampsGovernanceCategory()
    {
        var msg = _factory.ApplicationApproved("a@x.com", "Alice", MembershipTier.Colaborador, "en");

        msg.RecipientEmail.Should().Be("a@x.com");
        msg.RecipientName.Should().Be("Alice");
        msg.Subject.Should().Be("Subj");
        msg.HtmlBody.Should().Be("<p>Body</p>");
        msg.TemplateName.Should().Be("application_approved");
        msg.Category.Should().Be(MessageCategory.Governance);
        msg.TriggerImmediate.Should().BeFalse();
        msg.ReplyTo.Should().BeNull();
    }

    [HumansFact]
    public void ReConsentsRequired_IsAlwaysSend_NullCategory()
    {
        var msg = _factory.ReConsentsRequired("a@x.com", "Alice", ["Doc"], "en");

        msg.TemplateName.Should().Be("reconsents_required");
        msg.Category.Should().BeNull();
    }

    [HumansFact]
    public void EmailVerification_TriggersImmediate_NullCategory()
    {
        var msg = _factory.EmailVerification("a@x.com", "Alice", "https://verify", isConflict: true, "en");

        msg.TemplateName.Should().Be("email_verification");
        msg.Category.Should().BeNull();
        msg.TriggerImmediate.Should().BeTrue();
        _renderer.Received(1).RenderEmailVerification("Alice", "a@x.com", "https://verify", true, "en");
    }

    [HumansFact]
    public void AddedToTeam_StampsTeamUpdates()
    {
        var msg = _factory.AddedToTeam("a@x.com", "Alice", "Alpha", "alpha", [], "en");

        msg.TemplateName.Should().Be("added_to_team");
        msg.Category.Should().Be(MessageCategory.TeamUpdates);
    }

    [HumansFact]
    public void SignupRejected_StampsSystem()
    {
        var msg = _factory.SignupRejected("a@x.com", "Alice", "nope", "en");

        msg.TemplateName.Should().Be("signup_rejected");
        msg.Category.Should().Be(MessageCategory.System);
    }

    [HumansFact]
    public void FacilitatedMessage_WithContactInfo_SetsReplyToSender()
    {
        var msg = _factory.FacilitatedMessage(
            "rcpt@x.com", "Rcpt", "Sender", "Hi", includeContactInfo: true, senderEmail: "sender@x.com", "en");

        msg.Category.Should().Be(MessageCategory.FacilitatedMessages);
        msg.ReplyTo.Should().Be("sender@x.com");
    }

    [HumansFact]
    public void FacilitatedMessage_WithoutContactInfo_HasNoReplyTo()
    {
        var msg = _factory.FacilitatedMessage(
            "rcpt@x.com", "Rcpt", "Sender", "Hi", includeContactInfo: false, senderEmail: "sender@x.com", "en");

        msg.ReplyTo.Should().BeNull();
    }

    [HumansFact]
    public void CoordinatorRotaMessage_RoutesRepliesToCoordinator_VolunteerUpdates()
    {
        var request = new CoordinatorRotaMessageRequest(
            RecipientEmail: "rcpt@x.com",
            RecipientName: "Rcpt",
            SenderName: "Coord",
            SenderEmail: "coord@x.com",
            RotaName: "Gate",
            MessageText: "Hello",
            ShiftLines: ["Mon"],
            Culture: "en");

        var msg = _factory.CoordinatorRotaMessage(request);

        msg.RecipientEmail.Should().Be("rcpt@x.com");
        msg.TemplateName.Should().Be("coordinator_rota_message");
        msg.Category.Should().Be(MessageCategory.VolunteerUpdates);
        msg.ReplyTo.Should().Be("coord@x.com");
    }

    [HumansFact]
    public void MagicLinkSignup_UsesAddressAsNameAndTriggersImmediate()
    {
        var msg = _factory.MagicLinkSignup("new@x.com", "https://link", "en");

        msg.RecipientEmail.Should().Be("new@x.com");
        msg.RecipientName.Should().Be("new@x.com");
        msg.TemplateName.Should().Be("magic_link_signup");
        msg.Category.Should().BeNull();
        msg.TriggerImmediate.Should().BeTrue();
    }

    [HumansFact]
    public void CampaignCode_CarriesUserGrantReplyToAndCampaignCategory()
    {
        var userId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var request = new CampaignCodeEmailRequest(
            UserId: userId,
            CampaignGrantId: grantId,
            RecipientEmail: "zoe@x.com",
            RecipientName: "Zoe",
            Subject: "S {{Name}}",
            MarkdownBody: "Hi {{Name}} {{Code}}",
            Code: "ABC",
            ReplyTo: "reply@x.com");

        var msg = _factory.CampaignCode(request);

        msg.RecipientEmail.Should().Be("zoe@x.com");
        msg.TemplateName.Should().Be("campaign_code");
        msg.Category.Should().Be(MessageCategory.CampaignCodes);
        msg.ReplyTo.Should().Be("reply@x.com");
        msg.UserId.Should().Be(userId);
        msg.CampaignGrantId.Should().Be(grantId);
        _renderer.Received(1).RenderCampaignCode("S {{Name}}", "Hi {{Name}} {{Code}}", "ABC", "Zoe");
    }

    [HumansFact]
    public void EventLifecycle_PicksTemplateFromStatus_TriggersImmediate()
    {
        var request = new EventLifecycleNotification(EventStatus.Approved, "Bob", "My Event");

        var msg = _factory.EventLifecycle(request, "bob@x.com");

        msg.RecipientEmail.Should().Be("bob@x.com");
        msg.RecipientName.Should().Be("Bob");
        msg.TemplateName.Should().Be("event_approved");
        msg.Category.Should().BeNull();
        msg.TriggerImmediate.Should().BeTrue();
    }

    [HumansFact]
    public void TicketTransferTeamNotification_RoutesToTicketsInbox_System()
    {
        var msg = _factory.TicketTransferTeamNotification(
            "Sender", "Receiver", "rx@x.com", "Ticket #1", "reason", "https://review");

        msg.RecipientEmail.Should().Be(TicketConstants.TicketsTeamEmail);
        msg.RecipientName.Should().Be("Ticket team");
        msg.TemplateName.Should().Be("ticket_transfer_team");
        msg.Category.Should().Be(MessageCategory.System);
    }

    [HumansFact]
    public void TicketTransferDecision_TemplateReflectsOutcome()
    {
        _factory.TicketTransferDecision("a@x.com", "A", successful: true, "T", "Rx", null, "en")
            .TemplateName.Should().Be("ticket_transfer_completed");

        _factory.TicketTransferDecision("a@x.com", "A", successful: false, "T", "Rx", "why", "en")
            .TemplateName.Should().Be("ticket_transfer_cancelled");
    }

    [HumansFact]
    public void GoogleGroupRemoval_StampsSystem()
    {
        var msg = _factory.GoogleGroupRemovalLossOfAccess("a@x.com", "Alice", "Group", "g@x.com", "en");

        msg.TemplateName.Should().Be("google_group_removal_loss_of_access");
        msg.Category.Should().Be(MessageCategory.System);
    }
}
