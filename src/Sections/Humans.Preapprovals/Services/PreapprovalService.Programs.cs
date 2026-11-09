namespace Humans.Preapprovals.Services;

internal sealed partial class PreapprovalService
{
    private PreapprovalProgramAdministration Programs => new(repository, finance, notices, reader, journal, clock);

    public Task<PreapprovalMutationResult<Guid>> CreateProgramAsync(
        ProgramSave save, Guid actorUserId, CancellationToken ct = default) =>
        Programs.CreateProgramAsync(save, actorUserId, ct);

    public Task<PreapprovalMutationResult> UpdateProgramAsync(
        Guid programId, ProgramSave save, Guid actorUserId, CancellationToken ct = default) =>
        Programs.UpdateProgramAsync(programId, save, actorUserId, ct);
}
