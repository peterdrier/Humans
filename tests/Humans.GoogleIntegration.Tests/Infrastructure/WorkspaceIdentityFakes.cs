using Humans.AuditLog.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Domain;
using Humans.GoogleIntegration.Services;
using Humans.Users.Contracts;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

/// <summary>
/// Google identity doubles for the sync tests. Their fixtures describe members through
/// <see cref="UserInfo"/> (the legacy <c>IsGoogle</c> / <c>GoogleEmailStatus</c> flags), so the reads
/// go through the real <see cref="WorkspaceIdentityService"/> resolution over an empty
/// <c>google_workspace_identities</c> (the legacy fallback), while the writes stay recordable on the
/// substitute.
/// </summary>
internal static class WorkspaceIdentityFakes
{
    public static IWorkspaceIdentityManagement ResolvingThrough(IUserService users)
    {
        var real = Create(users, new InMemoryWorkspaceIdentityRepository());
        var fake = Substitute.For<IWorkspaceIdentityManagement>();
        fake.GetWorkspaceIdentityAsync(default)
            .ReturnsForAnyArgs(ci => real.GetWorkspaceIdentityAsync(ci.Arg<Guid>(), ci.Arg<CancellationToken>()));
        fake.GetWorkspaceIdentitiesAsync(default!)
            .ReturnsForAnyArgs(ci => real.GetWorkspaceIdentitiesAsync(
                ci.Arg<IReadOnlyCollection<Guid>>(), ci.Arg<CancellationToken>()));
        fake.GetDriveEmailsAsync(default)
            .ReturnsForAnyArgs(ci => real.GetDriveEmailsAsync(ci.Arg<CancellationToken>()));
        return fake;
    }

    public static WorkspaceIdentityService Create(
        IUserService users,
        IWorkspaceIdentityRepository repo,
        IAuditLogService? audit = null,
        IClock? clock = null) =>
        new(repo, users, audit ?? Substitute.For<IAuditLogService>(),
            clock ?? new FakeClock(Instant.FromUtc(2026, 10, 21, 12, 0)));
}

/// <summary>Dictionary-backed <see cref="IWorkspaceIdentityRepository"/>.</summary>
internal sealed class InMemoryWorkspaceIdentityRepository : IWorkspaceIdentityRepository
{
    public Dictionary<Guid, WorkspaceIdentityRecord> Rows { get; } = [];

    public Task<WorkspaceIdentityRecord?> GetAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(Rows.TryGetValue(userId, out var row) ? Copy(row) : null);

    public Task<IReadOnlyList<WorkspaceIdentityRecord>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WorkspaceIdentityRecord>>(Rows.Values.Select(Copy).ToList());

    public Task UpsertAsync(Guid userId, string? email, GoogleEmailStatus status, Instant now, CancellationToken ct = default)
    {
        var driveEmail = Rows.TryGetValue(userId, out var row) && string.Equals(row.Email, email, StringComparison.OrdinalIgnoreCase)
            ? row.DriveEmail
            : null;
        Rows[userId] = new WorkspaceIdentityRecord
        {
            UserId = userId,
            Email = email,
            Status = status,
            DriveEmail = driveEmail,
            UpdatedAt = now
        };
        return Task.CompletedTask;
    }

    public Task SetDriveEmailAsync(
        Guid userId, string email, GoogleEmailStatus status, string driveEmail, Instant now, CancellationToken ct = default)
    {
        Rows[userId] = new WorkspaceIdentityRecord
        {
            UserId = userId,
            Email = email,
            Status = status,
            DriveEmail = driveEmail,
            UpdatedAt = now
        };
        return Task.CompletedTask;
    }

    public Task<bool> TryInsertAsync(Guid userId, string? email, GoogleEmailStatus status, Instant now, CancellationToken ct = default)
    {
        if (Rows.ContainsKey(userId)) return Task.FromResult(false);
        Rows[userId] = new WorkspaceIdentityRecord { UserId = userId, Email = email, Status = status, UpdatedAt = now };
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(Rows.Remove(userId));

    private static WorkspaceIdentityRecord Copy(WorkspaceIdentityRecord r) =>
        new() { UserId = r.UserId, Email = r.Email, Status = r.Status, DriveEmail = r.DriveEmail, UpdatedAt = r.UpdatedAt };
}
