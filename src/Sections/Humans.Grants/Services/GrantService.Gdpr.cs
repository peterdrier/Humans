using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Grants.Services;

internal sealed partial class GrantService : IUserDataContributor, IUserMerge
{
    private GrantPersonalData PersonalData => new(repository, reader, settingsService, auditLog);

    internal const string Grants = GrantPersonalData.Grants;
    internal const string GrantDecisions = GrantPersonalData.GrantDecisions;

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => PersonalData.ErasureDeclaration;

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        PersonalData.ContributeForUserAsync(userId, ct);

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        PersonalData.EraseForUserAsync(userId, ct);

    public Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        PersonalData.ReassignAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct);
}
