using Humans.Audit.Contracts;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Google.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using NodaTime;
using System.Text.RegularExpressions;

namespace Humans.Governance.Services;

/// <summary>Each body's Google group and files. Governance claims them in Google, so its stored copy is the source of truth.</summary>
internal sealed class GovernanceBodyLinksService(
    IGovernanceBodyRepository repository,
    ITeamResourceService teamResources,
    IAuditLogService audit,
    GovernanceBodySync bodySync,
    IClock clock) : IApplicationService
{
    public const string InvalidGroupName = "Governance_Manage_InvalidGroupName";
    public const string GroupNameTaken = "Governance_Manage_GroupNameTaken";
    public const string FileAlreadyLinked = "Governance_Manage_FileAlreadyLinked";
    public const string FileNotFound = "Governance_Manage_FileNotFound";

    /// <summary>
    /// Keys each body's audit entries. These were its old system team's ids, kept so the body's history stays in one place.
    /// Reserved GUID block 0001: see docs/guid-reservations.md.
    /// </summary>
    internal static readonly IReadOnlyDictionary<GovernanceBody, Guid> AuditIds = new Dictionary<GovernanceBody, Guid>
    {
        [GovernanceBody.Asociados] = Guid.Parse("00000000-0000-0000-0001-000000000004"),
        [GovernanceBody.Colaboradores] = Guid.Parse("00000000-0000-0000-0001-000000000005"),
        [GovernanceBody.Board] = Guid.Parse("00000000-0000-0000-0001-000000000003"),
    };

    public Task<IReadOnlyList<GovernanceBodyLinks>> GetAllAsync(CancellationToken ct = default) =>
        repository.GetAllAsync(ct);

    /// <summary>Sets or, with a blank value, clears the body's group. Takes the full address or the part before the @.</summary>
    public async Task<GovernanceBodyLinkResult> SetGroupAsync(
        GovernanceBody body, string? value, Guid actorUserId, CancellationToken ct = default)
    {
        var prefix = value?.Trim().ToLowerInvariant() is { Length: > 0 } typed ? typed : null;
        if (prefix is not null && prefix.EndsWith($"@{DomainConstants.GoogleGroupDomain}", StringComparison.Ordinal))
            prefix = prefix[..prefix.IndexOf('@', StringComparison.Ordinal)];
        if (prefix is not null && (prefix.Length > 64 || !GroupPrefixPattern.IsMatch(prefix)))
            return new(false, InvalidGroupName);

        var all = await repository.GetAllAsync(ct);
        // Two bodies on one address would fight over its membership.
        if (prefix is not null && all.Any(l => l.Body != body && string.Equals(l.GoogleGroupPrefix, prefix, StringComparison.Ordinal)))
            return new(false, GroupNameTaken);

        var links = all.SingleOrDefault(l => l.Body == body) ?? new GovernanceBodyLinks { Body = body };
        if (string.Equals(links.GoogleGroupPrefix, prefix, StringComparison.Ordinal))
            return new(true);

        links.GoogleGroupPrefix = prefix;
        await SaveAsync(links, links.GoogleGroupEmail is { } email ? $"{body} group set to {email}" : $"{body} group cleared",
            actorUserId, ct);
        return new(true);
    }

    /// <summary>Links the Drive folder or file behind <paramref name="url"/> once Google confirms it can reach it.</summary>
    public async Task<GovernanceBodyLinkResult> AddFileAsync(
        GovernanceBody body, string url, DrivePermissionLevel level, Guid actorUserId, CancellationToken ct = default)
    {
        var lookup = await teamResources.LookUpDriveLinkAsync(url, ct);
        if (lookup.Item is not { } item)
            return new(false, GoogleMessage: lookup.ErrorMessage);

        var links = (await repository.GetAllAsync(ct)).SingleOrDefault(l => l.Body == body) ?? new GovernanceBodyLinks { Body = body };
        if (links.Files.Any(f => string.Equals(f.GoogleId, item.GoogleId, StringComparison.Ordinal)))
            return new(false, FileAlreadyLinked);

        links.Files.Add(new GovernanceBodyFile
        {
            Id = Guid.NewGuid(),
            Body = body,
            GoogleId = item.GoogleId,
            ResourceType = item.ResourceType,
            Name = item.Name,
            Url = item.Url,
            PermissionLevel = level,
        });
        await SaveAsync(links, $"{body} gets '{item.Name}' ({item.GoogleId}) as {level}", actorUserId, ct);
        return new(true);
    }

    /// <summary>Stops claiming the file. Like unlinking from a team, Google then leaves its sharing as it is.</summary>
    public async Task<GovernanceBodyLinkResult> RemoveFileAsync(
        GovernanceBody body, Guid fileId, Guid actorUserId, CancellationToken ct = default)
    {
        var links = (await repository.GetAllAsync(ct)).SingleOrDefault(l => l.Body == body);
        if (links?.Files.SingleOrDefault(f => f.Id == fileId) is not { } file)
            return new(false, FileNotFound);

        links.Files.Remove(file);
        await SaveAsync(links, $"{body} no longer gets '{file.Name}' ({file.GoogleId})", actorUserId, ct);
        return new(true);
    }

    private async Task SaveAsync(GovernanceBodyLinks links, string description, Guid actorUserId, CancellationToken ct)
    {
        links.UpdatedAt = clock.GetCurrentInstant();
        await repository.ReplaceAsync(links, ct);
        ct = CancellationToken.None; // the change is stored: its audit and sync request must follow
        await audit.LogAsync(GovernanceAuditActions.GovernanceBodyLinksChanged, GovernanceAuditSubjectTypes.GovernanceBody,
            AuditIds[links.Body], description, actorUserId);
        await bodySync.RequestAsync(links.Body, ct);
    }

    /// <summary>Same rule as a team's or workgroup's group name.</summary>
    private static readonly Regex GroupPrefixPattern =
        new("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
}

/// <summary><see cref="ErrorKey"/> is a resource key; <see cref="GoogleMessage"/> is Google's own English reason.</summary>
internal sealed record GovernanceBodyLinkResult(bool Success, string? ErrorKey = null, string? GoogleMessage = null);
