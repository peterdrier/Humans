using Humans.Users.Contracts;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Email.Services;
using NSubstitute;
using NSubstitute.Extensions;

namespace Humans.Email.Tests.Services;

/// <summary>
/// Tests the per-type policy stamped by <see cref="EmailMessageFactory"/> around the
/// pure <see cref="IEmailRenderer"/> — template name, opt-out category, reply-to,
/// recipient routing, and the campaign user/grant ids. The shared
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
}
