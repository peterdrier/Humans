using AwesomeAssertions;
using Humans.Google.Services.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace Humans.Google.Tests.Infrastructure;

/// <summary>
/// Pins the group-list shape the admin service relies on: a non-null id and email on each row.
/// </summary>
public class StubGoogleDirectoryClientTests
{
    private readonly StubGoogleDirectoryClient _client =
        new(NullLogger<StubGoogleDirectoryClient>.Instance);

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
