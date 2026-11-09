using Humans.Base.Interfaces;
using Humans.Consent.Contracts;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Consent.Services;

internal sealed class LegalDocumentReader(
    ILegalDocumentRepository repository,
    ITeamService teamService,
    IClock clock) : IApplicationService
{
    internal async Task<IReadOnlyList<AdminLegalDocumentListItem>> GetLegalDocumentsAsync(
        Guid? teamId,
        CancellationToken cancellationToken = default)
    {
        var documents = await repository.GetDocumentsAsync(teamId, cancellationToken);
        if (documents.Count == 0)
            return [];

        var now = clock.GetCurrentInstant();

        var teamIds = documents.Select(d => d.TeamId).Distinct().ToList();
        var teams = await teamService.GetTeamsWithParentsAsync(teamIds, cancellationToken);

        return documents
            .Select(d =>
            {
                var teamName = teams.TryGetValue(d.TeamId, out var team) ? team.Name : string.Empty;
                var currentVersion = CurrentVersionAt(d, now);

                Instant? lastSyncedAt = d.LastSyncedAt == default ? null : d.LastSyncedAt;

                return new AdminLegalDocumentListItem(
                    d.Id,
                    d.Name,
                    d.TeamId,
                    teamName,
                    d.IsRequired,
                    d.IsActive,
                    d.GracePeriodDays,
                    d.GitHubFolderPath,
                    currentVersion?.VersionNumber,
                    lastSyncedAt,
                    d.Versions.Count);
            })
            .ToList();
    }

    /// <summary>The version in force at <paramref name="now"/>: latest already-effective one.</summary>
    private static DocumentVersion? CurrentVersionAt(LegalDocument document, Instant now) =>
        document.Versions
            .Where(v => v.EffectiveFrom <= now)
            .MaxBy(v => v.EffectiveFrom);

    internal async Task<AdminLegalDocumentEditDetail?> GetLegalDocumentWithVersionsAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var document = await repository.GetByIdAsync(documentId, cancellationToken);
        if (document is null)
            return null;

        return new AdminLegalDocumentEditDetail(
            document.Id,
            document.Name,
            document.TeamId,
            document.IsRequired,
            document.IsActive,
            document.GracePeriodDays,
            document.GitHubFolderPath,
            document.LastSyncedAt,
            CurrentVersionAt(document, clock.GetCurrentInstant())?.VersionNumber,
            document.Versions
                .Select(v => new AdminLegalDocumentVersionDetail(
                    v.Id,
                    v.VersionNumber,
                    v.CommitSha,
                    v.EffectiveFrom,
                    v.CreatedAt,
                    v.ChangesSummary,
                    v.RequiresReConsent,
                    v.Content.Count,
                    v.Content.Keys.Order(StringComparer.Ordinal).ToList()))
                .ToList());
    }

    internal async Task<IReadOnlyList<LegalDocumentSnapshot>> GetActiveDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        var documents = await repository.GetActiveDocumentsAsync(cancellationToken);
        return documents.Select(ToDocumentSnapshot).ToList();
    }

    internal async Task<IReadOnlyList<RequiredDocumentVersionSnapshot>> GetRequiredVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        var requiredDocuments = await repository.GetActiveRequiredDocumentsAsync(cancellationToken);

        return requiredDocuments
            .Select(d => ToLatestRequiredVersionSnapshot(d, now))
            .OfType<RequiredDocumentVersionSnapshot>()
            .ToList();
    }

    internal async Task<LegalDocumentVersionSnapshot?> GetVersionByIdAsync(
        Guid versionId, CancellationToken cancellationToken = default)
    {
        var version = await repository.GetVersionByIdAsync(versionId, cancellationToken);
        return version is null ? null : ToVersionSnapshot(version);
    }

    internal async Task<IReadOnlyList<RequiredDocumentVersionSnapshot>> GetRequiredDocumentVersionsForTeamAsync(
        Guid teamId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        var requiredDocuments = await repository.GetActiveRequiredDocumentsForTeamAsync(teamId, cancellationToken);

        return requiredDocuments
            .Select(d => ToLatestRequiredVersionSnapshot(d, now))
            .OfType<RequiredDocumentVersionSnapshot>()
            .ToList();
    }

    private static RequiredDocumentVersionSnapshot? ToLatestRequiredVersionSnapshot(LegalDocument document, Instant now)
    {
        var version = CurrentVersionAt(document, now);
        return version is null ? null : new(
            version.Id,
            version.LegalDocumentId,
            document.Name,
            document.GracePeriodDays,
            version.VersionNumber,
            version.EffectiveFrom,
            version.RequiresReConsent,
            version.ChangesSummary);
    }

    private static LegalDocumentVersionSnapshot ToVersionSnapshot(DocumentVersion version) =>
        new(
            version.Id,
            version.LegalDocumentId,
            version.LegalDocument.Name,
            version.LegalDocument.GracePeriodDays,
            version.VersionNumber,
            new Dictionary<string, string>(version.Content, StringComparer.Ordinal),
            version.EffectiveFrom,
            version.RequiresReConsent,
            version.CreatedAt,
            version.ChangesSummary);

    internal async Task<int> GetActiveRequiredCountAsync(CancellationToken cancellationToken = default)
    {
        var documents = await repository.GetActiveRequiredDocumentsAsync(cancellationToken);
        return documents.Count;
    }

    internal async Task<IReadOnlyList<ActiveRequiredLegalDocumentSnapshot>> GetActiveRequiredDocumentsForTeamsAsync(
        IReadOnlyCollection<Guid>? teamIds, CancellationToken cancellationToken = default)
    {
        var documents = teamIds is null
            ? await repository.GetActiveRequiredDocumentsAsync(cancellationToken)
            : await repository.GetActiveRequiredDocumentsForTeamsAsync(teamIds, cancellationToken);
        if (documents.Count == 0) return [];

        var distinctTeamIds = documents.Select(d => d.TeamId).Distinct().ToList();
        var teams = await teamService.GetTeamsWithParentsAsync(distinctTeamIds, cancellationToken);

        return documents.Select(d =>
        {
            var teamName = teams.TryGetValue(d.TeamId, out var team) ? team.Name : string.Empty;
            return new ActiveRequiredLegalDocumentSnapshot(
                d.Id,
                d.Name,
                d.TeamId,
                teamName,
                d.LastSyncedAt,
                d.Versions.Select(ToVersionSnapshot).ToList());
        }).ToList();
    }

    private static LegalDocumentSnapshot ToDocumentSnapshot(LegalDocument document) =>
        new(
            document.Id,
            document.Name,
            document.TeamId,
            document.GracePeriodDays,
            document.GitHubFolderPath,
            document.CurrentCommitSha,
            document.IsRequired,
            document.IsActive,
            document.CreatedAt,
            document.LastSyncedAt,
            document.Versions.Select(ToVersionSnapshot).ToList());
}
