using AwesomeAssertions;
using Humans.Infrastructure.Services.GoogleWorkspace;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Humans.Application.Tests.Infrastructure.GoogleWorkspace;

/// <summary>
/// Contract tests for <see cref="StubGoogleDirectoryClient"/>. The stub
/// returns a small deterministic domain so the email-mismatch and
/// all-domain-groups flows can be exercised without Google credentials.
/// These tests pin the shape the sync service relies on (non-empty primary
/// email, non-null id + email on each group row).
/// </summary>
public class StubGoogleDirectoryClientTests
{
    private readonly StubGoogleDirectoryClient _client =
        new(NullLogger<StubGoogleDirectoryClient>.Instance);

    [Fact]
    public async Task ListDomainUsersAsync_ReturnsDeterministicDomainWithPrimaryEmails()
    {
        var result = await _client.ListDomainUsersAsync();

        result.Error.Should().BeNull();
        result.Users.Should().NotBeNull().And.NotBeEmpty();
        result.Users!.Should().AllSatisfy(u =>
            u.PrimaryEmail.Should().NotBeNullOrEmpty());
    }

    [Fact]
    public async Task ListDomainGroupsAsync_ReturnsDeterministicGroupsWithIdsAndEmails()
    {
        var result = await _client.ListDomainGroupsAsync();

        result.Error.Should().BeNull();
        result.Groups.Should().NotBeNull().And.NotBeEmpty();
        result.Groups!.Should().AllSatisfy(g =>
        {
            g.Id.Should().NotBeNullOrEmpty();
            g.Email.Should().NotBeNullOrEmpty();
        });
    }
}
