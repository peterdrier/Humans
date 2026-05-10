namespace Humans.Domain.Constants;

/// <summary>
/// Event type identifiers for Google sync outbox messages.
/// </summary>
public static class GoogleSyncOutboxEventTypes
{
    public const string AddUserToTeamResources = "AddUserToTeamResources";
    public const string RemoveUserFromTeamResources = "RemoveUserFromTeamResources";
}
