using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Finance.Contracts;
using Humans.Notifications.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Data;
using Humans.Preapprovals.Domain;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Preapprovals.Services;

/// <summary>
/// Every state change is a person's action (or Expenses' on their behalf). Deadline reminders have a separate owner.
/// </summary>
/// <remarks>"Today" is the Europe/Madrid date, the zone the association books in.</remarks>
internal sealed partial class PreapprovalService(
    IPreapprovalRepository repository,
    IUserService users,
    IFinanceService finance,
    PreapprovalNotices notices,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<PreapprovalService> logger) : IPreapprovalManagement
{
    private readonly PreapprovalJournal journal = new(auditLog);
    private readonly PreapprovalReader reader = new(repository, users, clock, logger);

    private LocalDate Today => reader.Today;

    public Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken ct = default) =>
        reader.GetProgramsAsync(ct);

    public Task<ProgramInfo?> GetProgramAsync(Guid programId, CancellationToken ct = default) =>
        reader.GetProgramAsync(programId, ct);

    public Task<IReadOnlyList<GrantInfo>> GetGrantsForUserAsync(Guid userId, CancellationToken ct = default) =>
        reader.GetGrantsForUserAsync(userId, ct);

    public Task<IReadOnlyList<GrantOption>> ListClaimableForUserAsync(Guid userId, CancellationToken ct) =>
        reader.ListClaimableForUserAsync(userId, ct);

    public Task<GrantOption?> GetGrantAsync(Guid grantId, CancellationToken ct) =>
        reader.GetGrantAsync(grantId, ct);

    public Task<IReadOnlySet<Guid>> ListManagedGrantIdsAsync(Guid userId, CancellationToken ct) =>
        reader.ListManagedGrantIdsAsync(userId, ct);

    public Task<bool> HasAnythingForUserAsync(Guid userId, CancellationToken ct = default) =>
        reader.HasAnythingForUserAsync(userId, ct);

    public Task<bool> ManagesAnyProgramAsync(Guid userId, CancellationToken ct = default) =>
        reader.ManagesAnyProgramAsync(userId, ct);

    private PreapprovalClaims Claims => new(repository, notices, journal, reader, clock, logger);

    public Task<PreapprovalWriteResult> ClaimAsync(
        Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct,
        Action<Func<CancellationToken, Task>>? afterCommit = null) =>
        Claims.ClaimAsync(grantId, expenseReportId, actorUserId, ct, afterCommit);

    public Task<PreapprovalWriteResult> ReleaseAsync(
        Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct,
        Action<Func<CancellationToken, Task>>? afterCommit = null) =>
        Claims.ReleaseAsync(grantId, expenseReportId, actorUserId, ct, afterCommit);

    public Task<PreapprovalWriteResult> ConsumeAsync(
        Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct,
        Action<Func<CancellationToken, Task>>? afterCommit = null) =>
        Claims.ConsumeAsync(grantId, expenseReportId, actorUserId, ct, afterCommit);

    private Task<UserInfo?> ResolveAvailablePersonAsync(Guid userId, CancellationToken ct) =>
        reader.ResolveAvailablePersonAsync(userId, ct);

    private static PreapprovalRefusal? MoneyRefusal(decimal amount) => PreapprovalInput.MoneyRefusal(amount);

    private static string? Trimmed(string? text) => PreapprovalInput.Trimmed(text);

    internal static string Euros(decimal amount) => PreapprovalJournal.Euros(amount);

    private Task AuditGrantAsync(
        AuditActionDefinition action, Domain.PreapprovalGrant grant, string description, Guid actorUserId,
        Guid? expenseReportId = null) =>
        journal.AuditGrantAsync(action, grant, description, actorUserId, expenseReportId);


}
