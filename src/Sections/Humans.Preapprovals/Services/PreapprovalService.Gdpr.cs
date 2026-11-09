using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Preapprovals.Services;

internal sealed partial class PreapprovalService : IUserDataContributor, IUserMerge
{
    private PreapprovalPersonalData PersonalData => new(repository, reader, notices, auditLog);

    internal const string Preapprovals = PreapprovalPersonalData.Preapprovals;
    internal const string PreapprovalDecisions = PreapprovalPersonalData.PreapprovalDecisions;

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => PersonalData.ErasureDeclaration;

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        PersonalData.ContributeForUserAsync(userId, ct);

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        PersonalData.EraseForUserAsync(userId, ct);

    public Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        PersonalData.ReassignAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct);
}
