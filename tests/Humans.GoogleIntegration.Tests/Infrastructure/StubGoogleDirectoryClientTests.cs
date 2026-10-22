using AwesomeAssertions;
using Humans.GoogleIntegration.Services.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

/// <summary>
/// Pins the shape the sync service relies on: a non-empty primary email, and a non-null id and
/// email on each group row.
/// </summary>
public class StubGoogleDirectoryClientTests
{
    private readonly StubGoogleDirectoryClient _client =
        new(NullLogger<StubGoogleDirectoryClient>.Instance);

    [HumansFact]
    public async Task ListDomainUsersAsync_ReturnsDeterministicDomainWithPrimaryEmails()
    {
        var result = await _client.ListDomainUsersAsync(Xunit.TestContext.Current.CancellationToken);

        result.Error.Should().BeNull();
        result.Users.Should().NotBeNull().And.NotBeEmpty();
        result.Users!.Should().AllSatisfy(u =>
            u.PrimaryEmail.Should().NotBeNullOrEmpty());
    }

    [HumansFact]
    public async Task ListDomainGroupsAsync_ReturnsDeterministicGroupsWithIdsAndEmails()
    {
        var result = await _client.ListDomainGroupsAsync(Xunit.TestContext.Current.CancellationToken);

        result.Error.Should().BeNull();
        result.Groups.Should().NotBeNull().And.NotBeEmpty();
        result.Groups!.Should().AllSatisfy(g =>
        {
            g.Id.Should().NotBeNullOrEmpty();
            g.Email.Should().NotBeNullOrEmpty();
        });
    }
}
