using AwesomeAssertions;
using AwesomeAssertions.Execution;
using System.Text.Json;
using Humans.Expenses.Contracts;
using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Budget.Contracts;
using Humans.Finance.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Holded.Contracts;
using Humans.Expenses.Data;
using Humans.Teams.Contracts;
using Humans.Expenses.Services;
using Humans.Expenses.Services.Dtos;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Users.Contracts;
using Humans.Email.Contracts;
using Humans.Expenses.Tests.Infrastructure;

namespace Humans.Expenses.Tests.Services;

public class ExpenseReportServiceGdprTests
{
    private readonly IStringLocalizer<ExpensesResource> _localizer =
        new StringLocalizer<ExpensesResource>(new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));

    private static readonly Instant FakeNow = Instant.FromUtc(2026, 5, 10, 12, 0);
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly IExpenseRepository _repo;
    private readonly IUserService _userService;
    private readonly IAuditLogService _auditLogService;
    private readonly IAuditViewerService _auditViewer = Substitute.For<IAuditViewerService>();
    private readonly IFinanceService _finance = Substitute.For<IFinanceService>();
    private readonly IUserDataContributor _sut;

    public ExpenseReportServiceGdprTests()
    {
        _repo = Substitute.For<IExpenseRepository>();
        _userService = Substitute.For<IUserService>();
        _auditLogService = Substitute.For<IAuditLogService>();

        // Nothing merged into anybody by default: GetUserInfoAsync returns null and the
        // export reads the single id.

        _auditViewer.GetFilteredAsync(
                entityType: Arg.Any<string?>(),
                entityId: Arg.Any<Guid?>(),
                userId: Arg.Any<Guid?>(),
                actions: Arg.Any<IReadOnlyList<string>?>(),
                limit: Arg.Any<int>(),
                ct: Arg.Any<CancellationToken>())
            .Returns([]);

        _sut = new ExpenseReportService(_repo, Substitute.For<IFileStorage>(), Substitute.For<IBudgetService>(), _userService, _auditLogService, _auditViewer, Substitute.For<IHoldedService>(), _finance, Substitute.For<IFinanceWrite>(), Substitute.For<Humans.Preapprovals.Contracts.IPreapprovalService>(), Substitute.For<Humans.Preapprovals.Contracts.IPreapprovalWrite>(), new FakeClock(FakeNow), NullLogger<ExpenseReportService>.Instance, Options.Create(new TravelReimbursementConfig()));
    }

    private static UserInfo WrapInUserInfo(Guid userId, ProfileInfo profile) => UserInfo.Create(
        user: new User
        {
            Id = userId,
            PreferredLanguage = "en",
            CreatedAt = FakeNow,
        },
        userEmails: [],
        externalLogins: [],
        profile: profile);

    private static ExpenseReportDto MakeReport(Guid submitterId) => new()
    {
        Id = Guid.NewGuid(),
        SubmitterUserId = submitterId,
        HoldedAccountNumber = 0,
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
                Description = "Flight ticket",
                Amount = 100m,
                LineType = ExpenseLineType.Receipt,
                SortOrder = 1,
                AttachmentId = null,
                Attachment = null,
            }
        }
    };

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task HappyPath_ExportsReportDatesAndMaskedIban(bool approved)
    {
        var report = MakeReport(UserId) with
        {
            SubmittedAt = approved ? Instant.FromUtc(2026, 4, 2, 11, 0) : null,
            ApprovedAt = approved ? Instant.FromUtc(2026, 4, 3, 12, 0) : null
        };
        _repo.GetForSubmitterAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([report]);

        _userService.GetUserInfoAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(UserId, UserFixtures.Profile()));

        var slices = await _sut.ContributeForUserAsync(UserId, Xunit.TestContext.Current.CancellationToken);

        slices.Should().HaveCount(2);

        var reportsSlice = slices.Single(s => string.Equals(s.SectionName, "ExpenseReports", StringComparison.Ordinal));
        reportsSlice.Data.Should().NotBeNull();

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(reportsSlice.Data));
        var row = json.RootElement[0];
        using var scope = new AssertionScope();
        row.GetProperty("CreatedAt").GetRawText().Should().Be("\"2026-04-01T10:00:00Z\"");
        row.GetProperty("SubmittedAt").GetRawText().Should().Be(approved ? "\"2026-04-02T11:00:00Z\"" : "null");
        row.GetProperty("ApprovedAt").GetRawText().Should().Be(approved ? "\"2026-04-03T12:00:00Z\"" : "null");
        row.GetProperty("PayeeIban").GetString().Should().Be("ES12****012");
    }

    [HumansFact]
    public async Task PayeeIban_InReports_IsMasked()
    {
        var report = MakeReport(UserId);
        _repo.GetForSubmitterAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([report]);
        _userService.GetUserInfoAsync(UserId, Arg.Any<CancellationToken>())
            .Returns((UserInfo?)null);

        var slices = await _sut.ContributeForUserAsync(UserId, Xunit.TestContext.Current.CancellationToken);

        // PayeeIban in the report should be masked
        var reportsSlice = slices.Single(s => string.Equals(s.SectionName, "ExpenseReports", StringComparison.Ordinal));
        var json = JsonSerializer.Serialize(reportsSlice.Data);
        json.Should().Contain("ES12****012");
        json.Should().NotContain("ES1234567890123456789012");
    }

    [HumansFact]
    public async Task NoReports_ReportsSliceIsNull()
    {
        _repo.GetForSubmitterAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([]);
        _userService.GetUserInfoAsync(UserId, Arg.Any<CancellationToken>())
            .Returns((UserInfo?)null);

        var slices = await _sut.ContributeForUserAsync(UserId, Xunit.TestContext.Current.CancellationToken);

        var reportsSlice = slices.Single(s => string.Equals(s.SectionName, "ExpenseReports", StringComparison.Ordinal));
        reportsSlice.Data.Should().BeNull("null data is dropped by the GDPR orchestrator");
    }

    [HumansFact]
    public async Task AuditEntries_AreIncluded()
    {
        _repo.GetForSubmitterAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([]);
        _userService.GetUserInfoAsync(UserId, Arg.Any<CancellationToken>())
            .Returns((UserInfo?)null);

        var entry = Event(
            ExpensesAuditActions.ExpenseSubmit,
            Guid.NewGuid(),
            "Submitted",
            actorUserId: UserId,
            relatedEntityId: UserId,
            relatedEntityType: AuditSubjectTypes.User);

        _auditViewer.GetFilteredAsync(
                entityType: Arg.Any<string?>(),
                entityId: Arg.Any<Guid?>(),
                userId: Arg.Any<Guid?>(),
                actions: Arg.Any<IReadOnlyList<string>?>(),
                limit: Arg.Any<int>(),
                ct: Arg.Any<CancellationToken>())
            .Returns([entry]);

        var slices = await _sut.ContributeForUserAsync(UserId, Xunit.TestContext.Current.CancellationToken);

        var auditSlice = slices.Single(s => string.Equals(s.SectionName, "ExpenseAuditLog", StringComparison.Ordinal));
        auditSlice.Data.Should().NotBeNull();
    }

    [HumansFact]
    public async Task MergedSourceIds_ReportsFromBothIds_AreIncluded()
    {
        var sourceId = Guid.NewGuid();
        _userService.GetUserInfoAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(WithMergedIds(UserId, sourceId)));

        var ownReport = MakeReport(UserId);
        var sourceReport = MakeReport(sourceId);

        _repo.GetForSubmitterAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns([sourceReport]);
        _repo.GetForSubmitterAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([ownReport]);

        var slices = await _sut.ContributeForUserAsync(UserId, Xunit.TestContext.Current.CancellationToken);

        var reportsSlice = slices.Single(s => string.Equals(s.SectionName, "ExpenseReports", StringComparison.Ordinal));
        var json = JsonSerializer.Serialize(reportsSlice.Data);
        // Both report IDs should appear
        json.Should().Contain(ownReport.Id.ToString());
        json.Should().Contain(sourceReport.Id.ToString());
    }

    [HumansFact]
    public async Task DecisionsOnOwnReports_AndRejectionReasonAndCap_AreExported()
    {
        var report = MakeReport(UserId) with
        {
            MaxAmount = 80m,
            LastRejectionReason = "Receipt unreadable",
            LastRejectedAt = FakeNow,
        };
        _repo.GetForSubmitterAsync(UserId, Arg.Any<CancellationToken>()).Returns([report]);

        var approval = Event(ExpensesAuditActions.ExpenseApprove, report.Id, "Finance approved expense report.",
            actorUserId: Guid.NewGuid());
        _auditViewer.GetFilteredAsync(
                entityType: "ExpenseReport",
                entityId: report.Id,
                userId: Arg.Any<Guid?>(),
                actions: Arg.Any<IReadOnlyList<string>?>(),
                limit: Arg.Any<int>(),
                ct: Arg.Any<CancellationToken>())
            .Returns([approval]);

        var slices = await _sut.ContributeForUserAsync(UserId, Xunit.TestContext.Current.CancellationToken);

        var reportsJson = JsonSerializer.Serialize(slices.Single(s => string.Equals(s.SectionName, "ExpenseReports", StringComparison.Ordinal)).Data);
        reportsJson.Should().Contain("Receipt unreadable").And.Contain("\"MaxAmount\":80");
        var auditJson = JsonSerializer.Serialize(slices.Single(s => string.Equals(s.SectionName, "ExpenseAuditLog", StringComparison.Ordinal)).Data);
        auditJson.Should().Contain("Finance approved expense report.");
    }

    [HumansFact]
    public async Task AdminActingOnOtherMembers_ReceivesNoCrossUserAuditEntriesOrRawIbans()
    {
        var adminId = UserId;
        var otherMemberId = Guid.NewGuid();
        var otherReportId = Guid.NewGuid();
        var ownReport = MakeReport(adminId);
        _repo.GetForSubmitterAsync(adminId, Arg.Any<CancellationToken>()).Returns([ownReport]);
        _userService.GetUserInfoAsync(adminId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(adminId, UserFixtures.Profile()));

        var ownSubmit = Event(
            ExpensesAuditActions.ExpenseSubmit,
            ownReport.Id,
            "Submitted expense report.",
            actorUserId: adminId,
            relatedEntityId: adminId,
            relatedEntityType: AuditSubjectTypes.User);

        var crossUserIbanSet = Event(
            ExpensesAuditActions.IbanSet,
            otherMemberId,
            "IBAN set for Dani Member to ES9121000418450200051332",
            actorUserId: adminId,
            entityType: AuditEntityTypes.Profile,
            relatedEntityId: otherMemberId,
            relatedEntityType: AuditSubjectTypes.User);
        var crossUserPayeeUpdated = Event(
            ExpensesAuditActions.ExpensePayeeIbanUpdated,
            otherReportId,
            "Payee IBAN updated for Dani Member to ES9121000418450200051332.",
            actorUserId: adminId,
            relatedEntityId: otherMemberId,
            relatedEntityType: AuditSubjectTypes.User);
        var crossUserCreated = Event(
            ExpensesAuditActions.ExpenseCreatedOnBehalf,
            otherReportId,
            "Created expense report on behalf of Dani Member.",
            actorUserId: adminId,
            relatedEntityId: otherMemberId,
            relatedEntityType: AuditSubjectTypes.User);
        var crossUserEdited = Event(
            ExpensesAuditActions.ExpenseEditedOnBehalf,
            otherReportId,
            "Added line \"Secret Hotel\" €250 on behalf of Dani Member.",
            actorUserId: adminId,
            relatedEntityId: otherMemberId,
            relatedEntityType: AuditSubjectTypes.User);
        var crossUserLineDeclined = Event(
            ExpensesAuditActions.ExpenseLineDeclined,
            otherReportId,
            "Coordinator declined line \"Other Receipt\" (50.00 EUR): private reason",
            actorUserId: adminId);
        var crossUserEndorse = Event(
            ExpensesAuditActions.ExpenseEndorse,
            otherReportId,
            "Pre-approval manager endorsed expense report.",
            actorUserId: adminId);
        var crossUserApprove = Event(
            ExpensesAuditActions.ExpenseApprove,
            otherReportId,
            "Finance approved expense report.",
            actorUserId: adminId);
        var crossUserIbanReveal = Event(
            FinanceAuditActions.IbanReveal,
            otherMemberId,
            $"Admin revealed IBAN for user {otherMemberId}",
            actorUserId: adminId,
            entityType: AuditSubjectTypes.User);

        _auditViewer.GetFilteredAsync(
                entityType: null,
                entityId: null,
                userId: adminId,
                actions: Arg.Any<IReadOnlyList<string>?>(),
                limit: Arg.Any<int>(),
                ct: Arg.Any<CancellationToken>())
            .Returns([
                ownSubmit,
                crossUserIbanSet,
                crossUserPayeeUpdated,
                crossUserCreated,
                crossUserEdited,
                crossUserLineDeclined,
                crossUserEndorse,
                crossUserApprove,
                crossUserIbanReveal,
            ]);
        _auditViewer.GetFilteredAsync(
                entityType: AuditEntityTypes.Report,
                entityId: ownReport.Id,
                userId: null,
                actions: Arg.Any<IReadOnlyList<string>?>(),
                limit: Arg.Any<int>(),
                ct: Arg.Any<CancellationToken>())
            .Returns([ownSubmit]);

        var slices = await _sut.ContributeForUserAsync(adminId, Xunit.TestContext.Current.CancellationToken);

        var auditSlice = slices.Single(s => string.Equals(s.SectionName, "ExpenseAuditLog", StringComparison.Ordinal));
        auditSlice.Data.Should().NotBeNull();
        var auditJson = JsonSerializer.Serialize(auditSlice.Data);
        auditJson.Should().Contain(ownReport.Id.ToString());
        auditJson.Should().Contain("Submitted expense report.");
        auditJson.Should().NotContain("ES9121000418450200051332");
        auditJson.Should().NotContain("ES91****332");
        auditJson.Should().NotContain("Dani Member");
        auditJson.Should().NotContain("Secret Hotel");
        auditJson.Should().NotContain("Other Receipt");
        auditJson.Should().NotContain(otherMemberId.ToString());
        auditJson.Should().NotContain(otherReportId.ToString());
    }

    [HumansFact]
    public async Task AdminWithNoOwnExpenseActivity_ActingOnlyOnOthers_GetsNullAuditSlice()
    {
        var adminId = UserId;
        var otherMemberId = Guid.NewGuid();
        var otherReportId = Guid.NewGuid();
        _repo.GetForSubmitterAsync(adminId, Arg.Any<CancellationToken>()).Returns([]);
        _userService.GetUserInfoAsync(adminId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(adminId, UserFixtures.Profile()));

        _auditViewer.GetFilteredAsync(
                entityType: null,
                entityId: null,
                userId: adminId,
                actions: Arg.Any<IReadOnlyList<string>?>(),
                limit: Arg.Any<int>(),
                ct: Arg.Any<CancellationToken>())
            .Returns([
                Event(
                    ExpensesAuditActions.IbanSet,
                    otherMemberId,
                    "IBAN set for Dani Member to ES9121000418450200051332",
                    actorUserId: adminId,
                    entityType: AuditEntityTypes.Profile,
                    relatedEntityId: otherMemberId,
                    relatedEntityType: AuditSubjectTypes.User),
                Event(
                    ExpensesAuditActions.ExpenseApprove,
                    otherReportId,
                    "Finance approved expense report.",
                    actorUserId: adminId),
            ]);

        var slices = await _sut.ContributeForUserAsync(adminId, Xunit.TestContext.Current.CancellationToken);

        var auditSlice = slices.Single(s => string.Equals(s.SectionName, "ExpenseAuditLog", StringComparison.Ordinal));
        auditSlice.Data.Should().BeNull();
    }

    [HumansFact]
    public async Task SubjectExport_IncludesOwnAndMergedAuditTrail_WithRawIbansMasked()
    {
        var survivorId = UserId;
        var sourceId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var otherMemberId = Guid.NewGuid();
        _userService.GetUserInfoAsync(survivorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(WithMergedIds(survivorId, sourceId)));

        var ownReport = MakeReport(survivorId);
        var sourceReport = MakeReport(sourceId);
        _repo.GetForSubmitterAsync(survivorId, Arg.Any<CancellationToken>()).Returns([ownReport]);
        _repo.GetForSubmitterAsync(sourceId, Arg.Any<CancellationToken>()).Returns([sourceReport]);

        var subjectIbanSet = Event(
            ExpensesAuditActions.IbanSet,
            survivorId,
            "IBAN set for Dani Member to ES9121000418450200051332",
            actorUserId: adminId,
            entityType: AuditEntityTypes.Profile,
            relatedEntityId: survivorId,
            relatedEntityType: AuditSubjectTypes.User);
        var subjectPayeeIbanUpdated = Event(
            ExpensesAuditActions.ExpensePayeeIbanUpdated,
            sourceReport.Id,
            "Payee IBAN updated for Dani Member to DE89370400440532013000.",
            actorUserId: adminId,
            relatedEntityId: sourceId,
            relatedEntityType: AuditSubjectTypes.User);
        var legacySelfIbanSet = Event(
            ExpensesAuditActions.IbanSet,
            sourceId,
            "IBAN set",
            actorUserId: sourceId,
            entityType: AuditEntityTypes.Profile);
        var subjectIbanReveal = Event(
            FinanceAuditActions.IbanReveal,
            survivorId,
            $"Admin revealed IBAN for user {survivorId}",
            actorUserId: adminId,
            entityType: AuditSubjectTypes.User);
        var reportApproved = Event(
            ExpensesAuditActions.ExpenseApprove,
            sourceReport.Id,
            "Finance approved expense report.",
            actorUserId: adminId);
        var crossUserIbanSet = Event(
            ExpensesAuditActions.IbanSet,
            otherMemberId,
            "IBAN set for Other Member to FR7630006000011234567890189",
            actorUserId: survivorId,
            entityType: AuditEntityTypes.Profile,
            relatedEntityId: otherMemberId,
            relatedEntityType: AuditSubjectTypes.User);

        _auditViewer.GetFilteredAsync(
                entityType: null,
                entityId: null,
                userId: survivorId,
                actions: Arg.Any<IReadOnlyList<string>?>(),
                limit: Arg.Any<int>(),
                ct: Arg.Any<CancellationToken>())
            .Returns([
                subjectIbanSet,
                subjectPayeeIbanUpdated,
                legacySelfIbanSet,
                subjectIbanReveal,
                crossUserIbanSet,
            ]);
        _auditViewer.GetFilteredAsync(
                entityType: AuditEntityTypes.Report,
                entityId: sourceReport.Id,
                userId: null,
                actions: Arg.Any<IReadOnlyList<string>?>(),
                limit: Arg.Any<int>(),
                ct: Arg.Any<CancellationToken>())
            .Returns([subjectPayeeIbanUpdated, reportApproved]);

        var slices = await _sut.ContributeForUserAsync(survivorId, Xunit.TestContext.Current.CancellationToken);

        var auditSlice = slices.Single(s => string.Equals(s.SectionName, "ExpenseAuditLog", StringComparison.Ordinal));
        auditSlice.Data.Should().NotBeNull();
        var auditJson = JsonSerializer.Serialize(auditSlice.Data);
        using var doc = JsonDocument.Parse(auditJson);
        doc.RootElement.GetProperty("Entries").GetArrayLength().Should().Be(5);

        auditJson.Should().Contain("IBAN set for Dani Member to ES91****332");
        auditJson.Should().Contain("Payee IBAN updated for Dani Member to DE89****000.");
        auditJson.Should().Contain("\"Action\":\"IbanSet\"");
        auditJson.Should().Contain($"Admin revealed IBAN for user {survivorId}");
        auditJson.Should().Contain("Finance approved expense report.");
        auditJson.Should().NotContain("ES9121000418450200051332");
        auditJson.Should().NotContain("DE89370400440532013000");
        auditJson.Should().NotContain("FR7630006000011234567890189");
        auditJson.Should().NotContain("FR76****189");
        auditJson.Should().NotContain(otherMemberId.ToString());
    }

    /// <summary>A record listing the given ids as accounts merged into <paramref name="userId"/>.</summary>
    private static UserInfo WithMergedIds(Guid userId, params Guid[] mergedUserIds)
    {
        var info = UserInfo.Create(new User { Id = userId, PreferredLanguage = "en" }, [], [], null);
        return info with { MergedUserIds = mergedUserIds };
    }

    private static AuditEvent Event(
        AuditActionDefinition action,
        Guid entityId,
        string description,
        Guid? actorUserId,
        string entityType = AuditEntityTypes.Report,
        Guid? relatedEntityId = null,
        string? relatedEntityType = null) =>
        new(Guid.NewGuid(), FakeNow, action, actorUserId, null, entityType, entityId,
            null, null, null, null, null, relatedEntityId, relatedEntityType, description);
}
