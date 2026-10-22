namespace Humans.GoogleIntegration.Services.Workspace;

internal sealed class StubGoogleDirectoryClient(ILogger<StubGoogleDirectoryClient> logger) : IGoogleDirectoryClient
{
    public Task<DirectoryUserListResult> ListDomainUsersAsync(CancellationToken ct = default)
    {
        logger.LogDebug("[STUB] List domain users");

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
        logger.LogDebug("[STUB] List domain groups");

        IReadOnlyList<DirectoryGroup> groups =
        [
            new("stubgroup-1", "board@nobodies.team", "Board", DirectMembersCount: 5),
            new("stubgroup-2", "all@nobodies.team", "All humans", DirectMembersCount: 42)
        ];

        return Task.FromResult(new DirectoryGroupListResult(groups, Error: null));
    }
}
