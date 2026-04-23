using Humans.Application.Interfaces.GoogleIntegration;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.GoogleWorkspace;

/// <summary>
/// Dev/test <see cref="IGoogleDirectoryClient"/> that returns a small,
/// deterministic fake domain so the email-mismatch and all-groups admin
/// flows can be exercised without Google credentials. Per the §15 connector
/// pattern, the Application-layer service runs against this stub — there
/// is no "stub service" variant.
/// </summary>
public sealed class StubGoogleDirectoryClient : IGoogleDirectoryClient
{
    private readonly ILogger<StubGoogleDirectoryClient> _logger;

    public StubGoogleDirectoryClient(ILogger<StubGoogleDirectoryClient> logger)
    {
        _logger = logger;
    }

    public Task<DirectoryUserListResult> ListDomainUsersAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[STUB] List domain users");

        IReadOnlyList<DirectoryUser> users =
        [
            new("alice@nobodies.team"),
            new("bob@nobodies.team"),
            new("carol@nobodies.team")
        ];

        return Task.FromResult(new DirectoryUserListResult(users, Error: null));
    }

    public Task<DirectoryGroupListResult> ListDomainGroupsAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[STUB] List domain groups");

        IReadOnlyList<DirectoryGroup> groups =
        [
            new("stubgroup-1", "board@nobodies.team", "Board", DirectMembersCount: 5),
            new("stubgroup-2", "all@nobodies.team", "All humans", DirectMembersCount: 42)
        ];

        return Task.FromResult(new DirectoryGroupListResult(groups, Error: null));
    }
}
