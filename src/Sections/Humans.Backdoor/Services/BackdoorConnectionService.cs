using System.Security.Claims;
using Humans.AuditLog.Contracts;
using Humans.Backdoor.Data;
using Humans.Backdoor.OAuth;
using Humans.Base.Extensions;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.Localization;
using NodaTime;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Humans.Backdoor.Services;

internal sealed class BackdoorConnectionService(
    IBackdoorOAuthRepository repository,
    IOpenIddictAuthorizationManager authorizations,
    IOpenIddictTokenManager tokens,
    IBackdoorApiKeyService keys,
    INotificationEmitter notifications,
    IAuditLogService audit,
    IStringLocalizer<BackdoorResource> localizer,
    ILogger<BackdoorConnectionService> logger) : IBackdoorConnectionService, IUserDataContributor, IUserMerge
{
    internal const string BackdoorConnections = "BackdoorConnections";

    /// <summary>The scheme tokens are made under, so the identity names how the caller came in.</summary>
    internal const string AuthenticationType = "SignInWithHumans";

    public async Task<ClaimsIdentity?> ApproveAsync(
        Guid userId, BackdoorOAuthClient client, string resource, CancellationToken ct = default)
    {
        if (!await keys.IsEligibleAsync(userId, ct))
        {
            logger.LogWarning("Sign in with Humans refused for {UserId}: no Backdoor scope admits them", userId);
            return null;
        }

        var subject = userId.ToString();
        var identity = new ClaimsIdentity(AuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, subject);

        // The Shell's claims transformation adds roles and account state from this, as for a cookie or a key.
        identity.SetClaim(ClaimTypes.NameIdentifier, subject);

        // Refresh tokens come with offline_access; nothing else is granted, whatever the client asked for.
        identity.SetScopes(Scopes.OfflineAccess);
        identity.SetResources(resource);
        identity.SetDestinations(_ => [Destinations.AccessToken]);

        var connection = await authorizations.CreateAsync(
            identity, subject, client.ClientId, AuthorizationTypes.Permanent, identity.GetScopes(), ct);
        var connectionId = Guid.Parse(await authorizations.GetIdAsync(connection, ct)
            ?? throw new InvalidOperationException("A new connection has no id."));
        identity.SetAuthorizationId(connectionId.ToString());

        await audit.LogAsync(
            BackdoorAuditActions.BackdoorConnectionApproved,
            AuditEntityTypes.BackdoorConnection,
            connectionId,
            $"Sign in with Humans: {client.ClientName} connected",
            userId,
            relatedEntityId: userId,
            relatedEntityType: AuditSubjectTypes.User);

        await notifications.SendAsync(
            BackdoorNotificationSources.ConnectionApproved,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            localizer["Backdoor_Connection_Notification_Title", client.ClientName],
            [userId],
            body: localizer["Backdoor_Connection_Notification_Body"],
            actionUrl: "/Backdoor",
            actionLabel: localizer["Backdoor_Connection_Notification_Action"],
            sourceKey: connectionId.ToString(),
            cancellationToken: ct);

        return identity;
    }

    public async Task<IReadOnlyList<BackdoorConnectionRow>> ListAsync(CancellationToken ct = default) =>
        [.. (await repository.GetConnectionsAsync(ct))
            .Where(c => string.Equals(c.Status, Statuses.Valid, StringComparison.Ordinal))
            .Select(c => new BackdoorConnectionRow(
                c.Id, c.UserId, BackdoorOAuthClients.Find(c.ClientId)?.ClientName ?? c.ClientId, c.CreatedAt))];

    public async Task<bool> RevokeAsync(Guid connectionId, Guid actorUserId, CancellationToken ct = default)
    {
        var connection = await repository.GetConnectionAsync(connectionId, ct);
        if (connection is null)
        {
            logger.LogWarning("Connection {ConnectionId} not found", connectionId);
            return false;
        }

        if (!string.Equals(connection.Status, Statuses.Valid, StringComparison.Ordinal))
        {
            logger.LogWarning("Connection {ConnectionId} is already revoked", connectionId);
            return false;
        }

        await RevokeWithTokensAsync([connectionId], ct);
        await audit.LogAsync(
            BackdoorAuditActions.BackdoorConnectionRevoked,
            AuditEntityTypes.BackdoorConnection,
            connectionId,
            $"Sign in with Humans: {BackdoorOAuthClients.Find(connection.ClientId)?.ClientName ?? connection.ClientId} connection revoked",
            actorUserId,
            relatedEntityId: connection.UserId,
            relatedEntityType: AuditSubjectTypes.User);
        return true;
    }

    /// <summary>
    /// Through OpenIddict's managers, so status changes have one writer, the stores. The tokens go too, so nothing
    /// under the connection is left marked usable.
    /// </summary>
    private async Task RevokeWithTokensAsync(IReadOnlyCollection<Guid> connectionIds, CancellationToken ct)
    {
        foreach (var id in connectionIds)
        {
            var identifier = OAuthStoreValues.FormatId(id)!;
            if (await authorizations.FindByIdAsync(identifier, ct) is { } authorization)
                await authorizations.TryRevokeAsync(authorization, ct);
            await tokens.RevokeByAuthorizationIdAsync(identifier, ct);
        }
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var shaped = (await repository.GetConnectionsAsync(ct))
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new
            {
                Client = BackdoorOAuthClients.Find(c.ClientId)?.ClientName ?? c.ClientId,
                c.Status,
                CreatedAt = c.CreatedAt.ToIso8601(),
            })
            .ToList();

        return [new UserDataSlice(BackdoorConnections, shaped.Count == 0 ? null : shaped)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [BackdoorConnections] = null
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => repository.EraseForUserAsync(userId, ct);

    /// <summary>
    /// Revoked, not moved: every token carries the eliminated account's id, so the person reconnects as the survivor.
    /// </summary>
    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        var ids = (await repository.GetConnectionsAsync(ct))
            .Where(c => c.UserId == mergedFromUserId && string.Equals(c.Status, Statuses.Valid, StringComparison.Ordinal))
            .Select(c => c.Id)
            .ToList();
        if (ids.Count == 0) return;

        await RevokeWithTokensAsync(ids, ct);
        foreach (var id in ids)
        {
            await audit.LogAsync(
                BackdoorAuditActions.BackdoorConnectionRevoked,
                AuditEntityTypes.BackdoorConnection,
                id,
                "Sign in with Humans connection revoked: account merged",
                actorUserId,
                relatedEntityId: mergedFromUserId,
                relatedEntityType: AuditSubjectTypes.User);
        }
    }
}
