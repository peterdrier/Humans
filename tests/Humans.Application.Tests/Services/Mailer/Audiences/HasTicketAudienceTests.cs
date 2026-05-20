using AwesomeAssertions;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Mailer.Audiences;
using NSubstitute;

namespace Humans.Application.Tests.Services.Mailer.Audiences;

public class HasTicketAudienceTests
{
    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_ReturnsTicketHolders()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var audience = NewAudience(ticketHolders: [userA, userB]);

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEquivalentTo([userA, userB]);
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_NoTicketHolders_ReturnsEmpty()
    {
        var audience = NewAudience(ticketHolders: []);

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEmpty();
    }

    [HumansFact]
    public void Metadata_UsesHumansPrefix()
    {
        var audience = NewAudience([]);
        audience.Key.Should().Be("has-ticket");
        audience.MailerLiteGroupName.Should().Be("Humans - Has Ticket");
        audience.MailerLiteGroupName.Should().StartWith("Humans - ");
    }

    private static HasTicketAudience NewAudience(HashSet<Guid> ticketHolders)
    {
        var tickets = Substitute.For<ITicketQueryService>();
        tickets.GetUserIdsWithTicketsAsync().Returns(ticketHolders);

        var users = Substitute.For<IUserService>();
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(new List<UserInfo>());

        return new HasTicketAudience(tickets, users);
    }
}
