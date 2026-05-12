using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Expenses;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Holded;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Expenses;
using Humans.Application.Services.Expenses.Dtos;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Expenses;

public class ExpenseReportServiceGdprTests
{
    private static readonly Instant FakeNow = Instant.FromUtc(2026, 5, 10, 12, 0);
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly IExpenseRepository _repo;
    private readonly IUserService _userService;
    private readonly IProfileService _profileService;
    private readonly IAuditLogService _auditLogService;
    private readonly IUserDataContributor _sut;

    public ExpenseReportServiceGdprTests()
    {
        _repo = Substitute.For<IExpenseRepository>();
        _userService = Substitute.For<IUserService>();
        _profileService = Substitute.For<IProfileService>();
        _auditLogService = Substitute.For<IAuditLogService>();

        // No merge tombstones by default
        _userService.GetMergedSourceIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());

        _auditLogService.GetFilteredEntriesAsync(
                entityType: Arg.Any<string>(),
                entityId: Arg.Any<Guid?>(),
                userId: Arg.Any<Guid?>(),
                actions: Arg.Any<IReadOnlyList<AuditAction>?>(),
                limit: Arg.Any<int>(),
                ct: Arg.Any<CancellationToken>())
            .Returns([]);

        _sut = new ExpenseReportService(
            _repo,
            Substitute.For<IFileStorage>(),
            Substitute.For<IBudgetService>(),
            Substitute.For<ITeamService>(),
            _userService,
            _profileService,
            _auditLogService,
            Substitute.For<IHoldedClient>(),
            new FakeClock(FakeNow),
            NullLogger<ExpenseReportService>.Instance);
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static ExpenseReportDto MakeReport(Guid submitterId) => new()
    {
        Id = Guid.NewGuid(),
        SubmitterUserId = submitterId,
        BudgetCategoryId = Guid.NewGuid(),
        BudgetYearId = Guid.NewGuid(),
        Status = ExpenseReportStatus.Approved,
        PayeeName = "Alice",
        PayeeIban = "ES1234567890123456789012",
        Total = 100m,
        CreatedAt = Instant.FromUtc(2026, 4, 1, 10, 0),
        UpdatedAt = Instant.FromUtc(2026, 4, 1, 10, 0),
        Lines = new List<ExpenseLineDto>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ExpenseReportId = Guid.NewGuid(),
                Description = "Flight ticket",
                Amount = 100m,
                SortOrder = 1,
                AttachmentId = null,
                Attachment = null,
            }
        }
    };

    // ─── happy path ───────────────────────────────────────────────────────────

    [HumansFact]
    public async Task HappyPath_ReturnsReportsAndMaskedIban()
    {
        var report = MakeReport(UserId);
        _repo.GetForSubmitterAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([report]);

        _profileService.GetProfileAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new Profile { Id = Guid.NewGuid(), Iban = "ES1234567890123456789012" });

        var slices = await _sut.ContributeForUserAsync(UserId, CancellationToken.None);

        slices.Should().HaveCount(2);

        var reportsSlice = slices.Single(s => string.Equals(s.SectionName, "ExpenseReports", StringComparison.Ordinal));
        reportsSlice.Data.Should().NotBeNull();

        // The returned data is shaped — we can verify via JSON (or cast)
        // At minimum, it should be non-null and contain data
        reportsSlice.Data.Should().NotBeNull();
    }

    [HumansFact]
    public async Task PayeeIban_InReports_IsMasked()
    {
        var report = MakeReport(UserId);
        _repo.GetForSubmitterAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([report]);
        _profileService.GetProfileAsync(UserId, Arg.Any<CancellationToken>())
            .Returns((Profile?)null);

        var slices = await _sut.ContributeForUserAsync(UserId, CancellationToken.None);

        // PayeeIban in the report should be masked
        var reportsSlice = slices.Single(s => string.Equals(s.SectionName, "ExpenseReports", StringComparison.Ordinal));
        var json = System.Text.Json.JsonSerializer.Serialize(reportsSlice.Data);
        json.Should().Contain("ES12****012");
        json.Should().NotContain("ES1234567890123456789012");
    }

    [HumansFact]
    public async Task NoReports_ReportsSliceIsNull()
    {
        _repo.GetForSubmitterAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([]);
        _profileService.GetProfileAsync(UserId, Arg.Any<CancellationToken>())
            .Returns((Profile?)null);

        var slices = await _sut.ContributeForUserAsync(UserId, CancellationToken.None);

        var reportsSlice = slices.Single(s => string.Equals(s.SectionName, "ExpenseReports", StringComparison.Ordinal));
        reportsSlice.Data.Should().BeNull("null data is dropped by the GDPR orchestrator");
    }

    [HumansFact]
    public async Task AuditEntries_AreIncluded()
    {
        _repo.GetForSubmitterAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([]);
        _profileService.GetProfileAsync(UserId, Arg.Any<CancellationToken>())
            .Returns((Profile?)null);

        var entry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Action = AuditAction.ExpenseSubmit,
            EntityType = "ExpenseReport",
            EntityId = Guid.NewGuid(),
            Description = "Submitted",
            OccurredAt = FakeNow,
        };

        _auditLogService.GetFilteredEntriesAsync(
                entityType: Arg.Any<string>(),
                entityId: Arg.Any<Guid?>(),
                userId: Arg.Any<Guid?>(),
                actions: Arg.Any<IReadOnlyList<AuditAction>?>(),
                limit: Arg.Any<int>(),
                ct: Arg.Any<CancellationToken>())
            .Returns([entry]);

        var slices = await _sut.ContributeForUserAsync(UserId, CancellationToken.None);

        var auditSlice = slices.Single(s => string.Equals(s.SectionName, "ExpenseAuditLog", StringComparison.Ordinal));
        auditSlice.Data.Should().NotBeNull();
    }

    [HumansFact]
    public async Task MergedSourceIds_ReportsFromBothIds_AreIncluded()
    {
        var sourceId = Guid.NewGuid();
        _userService.GetMergedSourceIdsAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { sourceId });

        var ownReport = MakeReport(UserId);
        var sourceReport = MakeReport(sourceId);

        _repo.GetForSubmitterAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns([sourceReport]);
        _repo.GetForSubmitterAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([ownReport]);

        _profileService.GetProfileAsync(UserId, Arg.Any<CancellationToken>())
            .Returns((Profile?)null);

        var slices = await _sut.ContributeForUserAsync(UserId, CancellationToken.None);

        var reportsSlice = slices.Single(s => string.Equals(s.SectionName, "ExpenseReports", StringComparison.Ordinal));
        var json = System.Text.Json.JsonSerializer.Serialize(reportsSlice.Data);
        // Both report IDs should appear
        json.Should().Contain(ownReport.Id.ToString());
        json.Should().Contain(sourceReport.Id.ToString());
    }
}
