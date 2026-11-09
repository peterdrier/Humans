namespace Humans.Grants.Services;

internal sealed partial class GrantService
{
    private GrantProgramAdministration Programs => new(repository, settingsService, reader, journal, clock);

    public Task<GrantMutationResult<Guid>> CreateProgramAsync(
        ProgramSave save, Guid actorUserId, CancellationToken ct = default) =>
        Programs.CreateProgramAsync(save, actorUserId, ct);

    public Task<GrantMutationResult> UpdateProgramAsync(
        Guid programId, ProgramSave save, Guid actorUserId, CancellationToken ct = default) =>
        Programs.UpdateProgramAsync(programId, save, actorUserId, ct);
}
