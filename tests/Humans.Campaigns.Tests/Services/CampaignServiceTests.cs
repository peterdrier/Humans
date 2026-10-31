using System.Globalization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using System.Text;
using Humans.Base.Constants;
using System.Security.Claims;
using Humans.AuditLog.Contracts;
using AwesomeAssertions;
using Humans.Campaigns.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Campaigns.Data;
using Humans.Campaigns.Domain;
using Humans.Campaigns.Services;
using Humans.Campaigns.Services.Dtos;
using Humans.Base.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using CampaignServiceImpl = Humans.Campaigns.Services.CampaignService;

namespace Humans.Campaigns.Tests.Services;

/// <summary>
/// Owns its fixture: inheriting the <c>UsersDbContext</c>-based harness would grant this project
/// <c>InternalsVisibleTo</c> on another section's context, so users come from an in-memory registry.
/// </summary>
public sealed class CampaignServiceTests
{
    private readonly FakeClock Clock = new(Instant.FromUtc(2026, 3, 31, 12, 0));

    private readonly TestDbContextFactory<CampaignsDbContext> CampaignsDbFactory =
        new(new DbContextOptionsBuilder<CampaignsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private readonly CampaignsDbContext CampaignsDb;

    private readonly Dictionary<Guid, UserInfo> _people = [];
    private readonly Dictionary<Guid, TeamInfo> _teams = [];

    private readonly Guid _actorId = Guid.NewGuid();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly CampaignServiceImpl _service;
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly INotificationEmitter _notifications = Substitute.For<INotificationEmitter>();
    private readonly CampaignsEmails _emailMessages = new();
    private readonly ITicketDiscountCodes _ticketDiscountCodes;

    public CampaignServiceTests()
    {
        CampaignsDb = CampaignsDbFactory.CreateDbContext();

        var repository = new CampaignRepository(CampaignsDbFactory);
        var teamService = Substitute.For<ITeamService>();
        var userEmailService = Substitute.For<IUserEmailService>();
        var userService = Substitute.For<IUserService>();
        _ticketDiscountCodes = Substitute.For<ITicketDiscountCodes>();

        // Stubs read the in-memory registries the Seed* helpers write to.
        teamService
            .GetTeamAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => _teams.GetValueOrDefault(call.ArgAt<Guid>(0)));

        teamService
            .GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new Dictionary<Guid, TeamInfo>(_teams));

        userService
            .GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<UserInfo?>(_people.GetValueOrDefault(call.ArgAt<Guid>(0))));

        userService
            .GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call
                .ArgAt<IReadOnlyCollection<Guid>>(0)
                .Where(_people.ContainsKey)
                .ToDictionary(id => id, id => _people[id]));

        userEmailService
            .GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call
                .ArgAt<IReadOnlyCollection<Guid>>(0)
                .Where(id => _people.TryGetValue(id, out var p) && !string.IsNullOrEmpty(p.Email))
                .ToDictionary(id => id, id => _people[id].Email!));

        _service = new CampaignServiceImpl(
            repository,
            teamService,
            userEmailService,
            userService,
            _notifications,
            _emailService,
            _emailMessages,
            _ticketDiscountCodes,
            Clock,
            _audit,
            NullLogger<CampaignServiceImpl>.Instance);
    }

    [HumansFact]
    public async Task AdminMutations_record_the_actor_and_committed_changes()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _audit.LogAsync(CampaignsAuditActions.CampaignCreated, "Campaign", Arg.Any<Guid>(),
            Arg.Any<string>(), _actorId).Returns(async call =>
            {
                var savedId = call.ArgAt<Guid>(2);
                (await CampaignsDb.Campaigns.AnyAsync(campaign => campaign.Id == savedId, ct)).Should().BeTrue();
            });
        var created = await _service.CreateAsync("Audited campaign", null, "Code", "{{Code}}", null, _actorId, ct);
        var campaignId = created.Campaign!.Id;
        await _service.UpdateAsync(_actorId, campaignId, "Updated campaign", null, "Code", "{{Code}}", null, ct);
        await _service.ImportCodesAsync(_actorId, campaignId, ["AUDIT-1"], ct);
        await _service.ActivateAsync(_actorId, campaignId, ct);
        var user = SeedUser();
        var team = SeedTeam("Audited team");
        SeedTeamMember(team.Id, user.Id);
        await _service.SendWaveAsync(_actorId, campaignId, team.Id, ct);
        var grant = await CampaignsDb.CampaignGrants.SingleAsync(ct);
        await _service.ResendToGrantAsync(_actorId, grant.Id, ct);
        await _service.CompleteAsync(_actorId, campaignId, ct);

        var entries = _audit.ReceivedCalls().Select(call => call.GetArguments()).ToList();
        entries.Select(args => (AuditActionDefinition)args[0]!).Should().Equal(
            CampaignsAuditActions.CampaignCreated, CampaignsAuditActions.CampaignUpdated,
            CampaignsAuditActions.CampaignCodesImported, CampaignsAuditActions.CampaignActivated,
            CampaignsAuditActions.CampaignWaveSent, CampaignsAuditActions.CampaignGrantResent,
            CampaignsAuditActions.CampaignCompleted);
        entries.Should().OnlyContain(args => (Guid)args[4]! == _actorId);
        entries.Single(args => (AuditActionDefinition)args[0]! == CampaignsAuditActions.CampaignWaveSent)[5].Should().Be(user.Id);
        ClearAllTrackers();
        (await CampaignsDb.Campaigns.SingleAsync(ct)).Status.Should().Be(CampaignStatus.Completed);
#pragma warning disable CS0618 // a retired column must stay unwritten until its drop is approved
        (await CampaignsDb.CampaignGrants.SingleAsync(ct)).LatestEmailStatus.Should().BeNull(
            "delivery state is no longer tracked on a grant (nobodies-collective/Humans#1243)");
#pragma warning restore CS0618
    }

    [HumansFact]
    public async Task RefusedMutations_do_not_record_successful_audit_actions()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        (await _service.CreateAsync("", null, "Code", "{{Code}}", null, _actorId, ct)).Success.Should().BeFalse();
        (await _service.ActivateAsync(_actorId, Guid.NewGuid(), ct)).Success.Should().BeFalse();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task AdminMutationActions_without_a_current_actor_do_not_change_data()
    {
        var controller = new CampaignController(_service, Substitute.For<IUserService>(), NullLogger<CampaignController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var id = Guid.NewGuid();
        Func<Task<IActionResult>>[] actions =
        [
            () => controller.Create("Campaign", null, "Code", "{{Code}}", null),
            () => controller.Edit(id, "Campaign", null, "Code", "{{Code}}", null),
            () => controller.ImportCodes(id, null),
            () => controller.GenerateCodes(id, 1, "Percentage", 10),
            () => controller.Activate(id),
            () => controller.Complete(id),
            () => controller.SendWave(id, id),
            () => controller.Resend(id)
        ];

        foreach (var action in actions)
            (await action()).Should().BeOfType<UnauthorizedResult>();
        (await CampaignsDb.Campaigns.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
        _audit.ReceivedCalls().Should().BeEmpty();
        _emailService.ReceivedCalls().Should().BeEmpty();
        _ticketDiscountCodes.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData("csv")]
    [Xunit.InlineData("txt")]
    [Xunit.InlineData("multiline")]
    [Xunit.InlineData("plain")]
    public async Task ImportCodes_ReadsUploadedCodeValues(string format)
    {
        var campaign = await SeedCampaignAsync();
        var controller = CreateImportController();
        var content = format switch
        {
            "csv" => "\"A,One\"\r\n\"B\"\"Two\"\r\n",
            "multiline" => "\"A,\nOne\"\r\n\"B\"\"Two\"\r\n",
            "plain" => "AOne\r\nBTwo\r\n",
            _ => "A,One\r\nB\"Two\r\n",
        };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var file = new FormFile(stream, 0, stream.Length, "file", string.Equals(format, "txt", StringComparison.Ordinal) ? "codes.txt" : "codes.CSV");

        var result = await controller.ImportCodes(campaign.Id, file);

        result.Should().BeOfType<RedirectToActionResult>();
        var codes = await CampaignsDb.CampaignCodes.AsNoTracking().Where(c => c.CampaignId == campaign.Id)
            .OrderBy(c => c.ImportOrder).Select(c => c.Code).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        codes.Should().Equal(format switch { "multiline" => "A,\nOne", "plain" => "AOne", _ => "A,One" },
            format is "plain" ? "BTwo" : "B\"Two");
        controller.TempData.ContainsKey(TempDataKeys.ErrorMessage).Should().BeFalse();
    }

    [HumansTheory]
    [Xunit.InlineData("VALID\r\n\"UNCLOSED")]
    [Xunit.InlineData("VALID\r\nFIRST,SECOND\r\n")]
    public async Task ImportCodes_InvalidCsv_DoesNotImportAPartialPrefix(string content)
    {
        var campaign = await SeedCampaignAsync();
        var controller = CreateImportController();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var file = new FormFile(stream, 0, stream.Length, "file", "codes.csv");
        _audit.ClearReceivedCalls();

        var result = await controller.ImportCodes(campaign.Id, file);

        result.Should().BeOfType<RedirectToActionResult>();
        (await CampaignsDb.CampaignCodes.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
        controller.TempData[TempDataKeys.ErrorMessage].Should().BeOfType<string>();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(false, 200)]
    [Xunit.InlineData(false, 201)]
    [Xunit.InlineData(true, 200)]
    [Xunit.InlineData(true, 201)]
    public async Task ImportCodes_EnforcesDatabaseCharacterLimitWithoutPartialWrites(bool supplementary, int length)
    {
        var campaign = await SeedCampaignAsync();
        var controller = CreateImportController();
        var code = string.Concat(Enumerable.Repeat(supplementary ? "😀" : "A", length));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("VALID\n" + code));
        var file = new FormFile(stream, 0, stream.Length, "file", "codes.csv");
        _audit.ClearReceivedCalls();
        var result = await controller.ImportCodes(campaign.Id, file);
        result.Should().BeOfType<RedirectToActionResult>();
        var stored = await CampaignsDb.CampaignCodes.AsNoTracking().ToListAsync(Xunit.TestContext.Current.CancellationToken);
        if (length <= 200)
        {
            stored.Select(c => c.Code).Should().BeEquivalentTo("VALID", code);
            controller.TempData.ContainsKey(TempDataKeys.ErrorMessage).Should().BeFalse();
        }
        else
        {
            stored.Should().BeEmpty();
            controller.TempData[TempDataKeys.ErrorMessage].Should().BeOfType<string>().Which.Should().Contain("200");
            controller.TempData.ContainsKey(TempDataKeys.SuccessMessage).Should().BeFalse();
            _audit.ReceivedCalls().Should().BeEmpty();
        }
    }

    [HumansTheory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("invalid")]
    [Xunit.InlineData("0")]
    public async Task GenerateCodes_requires_submitted_discount_value_before_vendor_write(string? submittedValue)
    {
        var campaign = await SeedCampaignAsync();
        var controller = CreateImportController();
        _ticketDiscountCodes.GenerateAsync(Arg.Any<TicketDiscountCodeRequest>(), Arg.Any<CancellationToken>())
            .Returns(["GENERATED"]);
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        controller.Url = Substitute.For<IUrlHelper>();
        var parameter = typeof(CampaignController).GetMethod(nameof(CampaignController.GenerateCodes))!
            .GetParameters().Single(p => string.Equals(p.Name, "discountValue", StringComparison.Ordinal));
        var metadata = ((ModelMetadataProvider)services.GetRequiredService<IModelMetadataProvider>()).GetMetadataForParameter(parameter);
        var descriptor = new ControllerParameterDescriptor
        {
            Name = parameter.Name!,
            ParameterType = parameter.ParameterType,
            ParameterInfo = parameter
        };
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = parameter
        });
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal);
        if (submittedValue is not null) fields["discountValue"] = submittedValue;
        await services.GetRequiredService<ParameterBinder>().BindModelAsync(controller.ControllerContext, binder,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            descriptor, metadata, value: null);

        var result = await controller.GenerateCodes(campaign.Id, 1, "Fixed", 0m);

        if (!string.Equals(submittedValue, "0", StringComparison.Ordinal))
        {
            result.Should().BeOfType<BadRequestObjectResult>();
            controller.ModelState["discountValue"]!.Errors.Should().HaveCount(submittedValue is null ? 1 : 2);
            _ticketDiscountCodes.ReceivedCalls().Should().BeEmpty();
            (await CampaignsDb.CampaignCodes.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
            _audit.ReceivedCalls().Should().BeEmpty();
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            await _ticketDiscountCodes.Received(1).GenerateAsync(
                Arg.Is<TicketDiscountCodeRequest>(request => request.Value == 0m), CancellationToken.None);
            (await CampaignsDb.CampaignCodes.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(1);
        }
    }

    [HumansFact]
    public async Task GenerateCodes_ImportRefusal_IsNotReportedAsSuccess()
    {
        var campaign = await SeedCampaignAsync();
        var controller = CreateImportController();
        _ticketDiscountCodes.GenerateAsync(Arg.Any<TicketDiscountCodeRequest>(), Arg.Any<CancellationToken>())
            .Returns([new string('A', 201)]);
        var result = await controller.GenerateCodes(campaign.Id, 1, "Fixed", 10m);
        result.Should().BeOfType<RedirectToActionResult>();
        controller.TempData[TempDataKeys.ErrorMessage].Should().BeOfType<string>();
        controller.TempData.ContainsKey(TempDataKeys.SuccessMessage).Should().BeFalse();
        (await CampaignsDb.CampaignCodes.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
    }

    private CampaignController CreateImportController()
    {
        var actor = SeedUser(_actorId);
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(_actorId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(actor));
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _actorId.ToString())], "test")),
        };
        return new CampaignController(_service, users, NullLogger<CampaignController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
        };
    }

    [HumansFact]
    public async Task AbandonedAdminPages_CancelRepositoryReads()
    {
        var campaign = await SeedCampaignAsync(CampaignStatus.Active);
        using var request = new CancellationTokenSource();
        var controller = new CampaignController(_service, Substitute.For<IUserService>(), NullLogger<CampaignController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestAborted = request.Token }
            }
        };
        Func<Task<IActionResult>>[] pages =
        [
            () => controller.Index(),
            () => controller.Edit(campaign.Id),
            () => controller.Detail(campaign.Id),
            () => controller.SendWave(campaign.Id, null)
        ];
        foreach (var page in pages)
            (await page()).Should().BeOfType<ViewResult>();

        await request.CancelAsync();
        foreach (var page in pages)
        {
            Func<Task> read = async () => await page();
            await read.Should().ThrowAsync<OperationCanceledException>();
        }
    }

    [HumansFact]
    public async Task CreateAsync_CreatesCampaignInDraftStatus()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.CreateAsync(
            "Test Campaign", "A description",
            "Your code: {{Code}}", "<p>Hi {{Name}}, your code is {{Code}}</p>",
            null, userId, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.Campaign.Should().NotBeNull();
        result.Campaign!.Title.Should().Be("Test Campaign");
        result.Campaign.Description.Should().Be("A description");
        result.Campaign.Status.Should().Be(CampaignStatus.Draft);
        result.Campaign.CreatedAt.Should().Be(Clock.GetCurrentInstant());
        result.Campaign.CreatedByUserId.Should().Be(userId);

        var inDb = await CampaignsDb.Campaigns.FindAsync(result.Campaign.Id, Xunit.TestContext.Current.CancellationToken);
        inDb.Should().NotBeNull();
        inDb.Status.Should().Be(CampaignStatus.Draft);
    }

    [HumansTheory]
    [Xunit.InlineData("TitleTooLong", "title")]
    [Xunit.InlineData("DescriptionTooLong", "description")]
    [Xunit.InlineData("EmailSubjectTooLong", "emailSubject")]
    [Xunit.InlineData("ReplyToAddressTooLong", "replyToAddress")]
    [Xunit.InlineData("ReplyToAddressInvalid", "replyToAddress")]
    public async Task CampaignForms_RejectInvalidTextWithoutCreatingOrChangingStoredCampaign(string errorKey, string field)
    {
        var viewer = SeedUser();
        var existing = await SeedCampaignAsync();
        var title = string.Equals(errorKey, "TitleTooLong", StringComparison.Ordinal) ? new string('x', 201) : "Changed title";
        var description = string.Equals(errorKey, "DescriptionTooLong", StringComparison.Ordinal) ? new string('x', 2001) : "Changed description";
        var subject = string.Equals(errorKey, "EmailSubjectTooLong", StringComparison.Ordinal) ? new string('x', 1001) : "Changed subject";
        var replyTo = errorKey switch
        {
            "ReplyToAddressTooLong" => new string('x', 310) + "@example.com",
            "ReplyToAddressInvalid" => "not-an-email",
            _ => "reply@example.com",
        };
        var userRead = Substitute.For<IUserService>();
        userRead.GetUserInfoAsync(viewer.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(viewer));
        CampaignController Controller()
        {
            var http = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, viewer.Id.ToString())], "test")),
            };
            return new(_service, userRead, NullLogger<CampaignController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
                TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
                Url = Substitute.For<IUrlHelper>(),
            };
        }

        var create = Controller();
        (await create.Create(title, description, subject, "Changed body", replyTo)).Should().BeOfType<ViewResult>();
        create.ModelState.ContainsKey(field).Should().BeTrue();
        create.ModelState[field]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        ((string)create.ViewBag.Title2).Should().Be(title);
        ((string)create.ViewBag.ReplyToAddress).Should().Be(replyTo);
        (await CampaignsDb.Campaigns.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(1);

        var edit = Controller();
        (await edit.Edit(existing.Id, title, description, subject, "Changed body", replyTo)).Should().BeOfType<ViewResult>();
        edit.ModelState.ContainsKey(field).Should().BeTrue();
        edit.ModelState[field]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        var stored = await _service.GetByIdAsync(existing.Id, Xunit.TestContext.Current.CancellationToken);
        stored!.Title.Should().Be(existing.Title);
        stored.Description.Should().Be(existing.Description);
        stored.EmailSubject.Should().Be(existing.EmailSubject);
        stored.EmailBodyTemplate.Should().Be(existing.EmailBodyTemplate);
        stored.ReplyToAddress.Should().Be(existing.ReplyToAddress);
    }

    [HumansFact]
    public async Task CampaignForms_AllowStoredLimitsAndTrimOptionalFields()
    {
        var title = new string('x', 200);
        var description = new string('x', 2000);
        var subject = new string('x', 1000);
        var replyTo = new string('x', 308) + "@example.com";
        var created = await _service.CreateAsync(" " + title + " ", " " + description + " ", " " + subject + " ",
            "Body", " " + replyTo + " ", Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        created.Success.Should().BeTrue();
        created.Campaign!.ReplyToAddress.Should().Be(replyTo);
        var updated = await _service.UpdateAsync(_actorId, created.Campaign.Id, title, description, subject, "Body", "   ",
            Xunit.TestContext.Current.CancellationToken);
        updated.Success.Should().BeTrue();
        (await _service.GetByIdAsync(created.Campaign.Id, Xunit.TestContext.Current.CancellationToken))!.ReplyToAddress.Should().BeNull();
    }

    [HumansFact]
    public async Task CreateAsync_BlankTitle_ReturnsValidationError()
    {
        var result = await _service.CreateAsync(
            "  ", "A description",
            "Subject", "Body",
            null, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("TitleRequired");
        (await CampaignsDb.Campaigns.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansFact]
    public async Task ImportCodesAsync_IntoActiveCampaign_AppendsInOrderAndSkipsDuplicates()
    {
        var campaign = await SeedCampaignAsync(CampaignStatus.Active);

        var first = await _service.ImportCodesAsync(_actorId, campaign.Id, ["CODE1", "CODE2", "CODE1"], Xunit.TestContext.Current.CancellationToken);
        var second = await _service.ImportCodesAsync(_actorId, campaign.Id, ["code2", "CODE3"], Xunit.TestContext.Current.CancellationToken);

        first.Should().Be(new CampaignImportResult(true, Imported: 2, Skipped: 1));
        second.Should().Be(new CampaignImportResult(true, Imported: 1, Skipped: 1));
        var codes = await CampaignsDb.CampaignCodes
            .Where(c => c.CampaignId == campaign.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        codes.OrderBy(c => c.ImportOrder).Select(c => c.Code).Should().Equal("CODE1", "CODE2", "CODE3");
        codes.Select(c => c.ImportOrder).Should().OnlyHaveUniqueItems();
    }

    [HumansFact]
    public async Task GenerateAndImportDiscountCodesAsync_DraftCampaign_GeneratesAndImportsCodes()
    {
        var campaign = await SeedCampaignAsync();
        _ticketDiscountCodes
            .GenerateAsync(Arg.Any<TicketDiscountCodeRequest>(), Arg.Any<CancellationToken>())
            .Returns(["CODE-A", "CODE-B"]);

        var result = await _service.GenerateAndImportDiscountCodesAsync(
            _actorId,
            campaign.Id, 2, "Fixed", 10m, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.GeneratedCount.Should().Be(2);
        await _ticketDiscountCodes.Received(1).GenerateAsync(
            Arg.Is<TicketDiscountCodeRequest>(r =>
                r.Count == 2 &&
                r.Kind == TicketDiscountKind.Fixed &&
                r.Value == 10m),
            Arg.Any<CancellationToken>());
        var codes = await CampaignsDb.CampaignCodes
            .Where(c => c.CampaignId == campaign.Id)
            .Select(c => c.Code)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        codes.Should().BeEquivalentTo("CODE-A", "CODE-B");
    }

    [HumansFact]
    public async Task GenerateAndImportDiscountCodesAsync_CodeAlreadyInCampaign_IsSkippedAndNotCounted()
    {
        var campaign = await SeedCampaignAsync();
        await _service.ImportCodesAsync(_actorId, campaign.Id, ["CODE-A"], Xunit.TestContext.Current.CancellationToken);
        _ticketDiscountCodes
            .GenerateAsync(Arg.Any<TicketDiscountCodeRequest>(), Arg.Any<CancellationToken>())
            .Returns(["CODE-A", "CODE-B"]);

        var result = await _service.GenerateAndImportDiscountCodesAsync(
            _actorId,
            campaign.Id, 2, "Fixed", 10m, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.GeneratedCount.Should().Be(1);
        var codes = await CampaignsDb.CampaignCodes
            .Where(c => c.CampaignId == campaign.Id)
            .Select(c => c.Code)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        codes.Should().BeEquivalentTo("CODE-A", "CODE-B");
    }

    [HumansFact]
    public async Task GenerateAndImportDiscountCodesAsync_NonDraftCampaign_ReturnsNotDraft()
    {
        var campaign = await SeedCampaignAsync(CampaignStatus.Active);

        var result = await _service.GenerateAndImportDiscountCodesAsync(
            _actorId,
            campaign.Id, 2, "Fixed", 10m, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotDraft");
        await _ticketDiscountCodes.DidNotReceive().GenerateAsync(
            Arg.Any<TicketDiscountCodeRequest>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData("99")]
    [Xunit.InlineData("-1")]
    [Xunit.InlineData("unknown")]
    public async Task GenerateAndImportDiscountCodesAsync_InvalidKind_DoesNotGenerateOrImport(string discountType)
    {
        var campaign = await SeedCampaignAsync();

        var result = await _service.GenerateAndImportDiscountCodesAsync(
            _actorId,
            campaign.Id, 2, discountType, 10m, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("InvalidDiscountType");
        await _ticketDiscountCodes.DidNotReceive().GenerateAsync(
            Arg.Any<TicketDiscountCodeRequest>(), Arg.Any<CancellationToken>());
        (await CampaignsDb.CampaignCodes.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansFact]
    public async Task ActivateAsync_DraftWithCodes_TransitionsToActive()
    {
        var campaign = await SeedCampaignAsync();
        await _service.ImportCodesAsync(_actorId, campaign.Id, ["CODE1"], Xunit.TestContext.Current.CancellationToken);

        await _service.ActivateAsync(_actorId, campaign.Id, Xunit.TestContext.Current.CancellationToken);

        var updated = await CampaignsDb.Campaigns.FindAsync(campaign.Id, Xunit.TestContext.Current.CancellationToken);
        updated!.Status.Should().Be(CampaignStatus.Active);
    }

    [HumansFact(Timeout = 10000)]
    public async Task ActivateAsync_NoCodes_ReturnsNoCodes()
    {
        var campaign = await SeedCampaignAsync();

        var result = await _service.ActivateAsync(_actorId, campaign.Id, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NoCodes");
        var updated = await CampaignsDb.Campaigns.FindAsync(campaign.Id, Xunit.TestContext.Current.CancellationToken);
        updated!.Status.Should().Be(CampaignStatus.Draft);
    }

    [HumansFact]
    public async Task ActivateAsync_NotDraft_ReturnsNotDraft()
    {
        var campaign = await SeedCampaignAsync(CampaignStatus.Active);

        var result = await _service.ActivateAsync(_actorId, campaign.Id, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotDraft");
    }

    [HumansFact]
    public async Task UpdateAsync_ExistingCampaign_UpdatesFields()
    {
        var campaign = await SeedCampaignAsync();

        var updated = await _service.UpdateAsync(
            _actorId,
            campaign.Id,
            "  Updated Campaign  ",
            "  Updated description  ",
            "  Updated subject  ",
            "  Updated body  ",
            "  reply@example.com  ", Xunit.TestContext.Current.CancellationToken);

        updated.Success.Should().BeTrue();

        var refreshed = await CampaignsDb.Campaigns.FindAsync(campaign.Id, Xunit.TestContext.Current.CancellationToken);
        refreshed.Should().NotBeNull();
        refreshed.Title.Should().Be("Updated Campaign");
        refreshed.Description.Should().Be("Updated description");
        refreshed.EmailSubject.Should().Be("Updated subject");
        refreshed.EmailBodyTemplate.Should().Be("Updated body");
        refreshed.ReplyToAddress.Should().Be("reply@example.com");
    }

    [HumansFact]
    public async Task UpdateAsync_BlankEmailSubject_ReturnsValidationError()
    {
        var campaign = await SeedCampaignAsync();

        var updated = await _service.UpdateAsync(
            _actorId,
            campaign.Id,
            "Title",
            null,
            " ",
            "Body",
            null, Xunit.TestContext.Current.CancellationToken);

        updated.Success.Should().BeFalse();
        updated.ErrorKey.Should().Be("EmailSubjectRequired");
    }

    [HumansFact]
    public async Task GetDetailPageAsync_ReturnsComputedStats()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["A", "B", "C"]);
        var user = SeedUser(displayName: "Stats User");
        var grantedCode = await CampaignsDb.CampaignCodes
            .FirstAsync(c => c.CampaignId == campaign.Id && c.Code == "A", Xunit.TestContext.Current.CancellationToken);

        CampaignsDb.CampaignGrants.Add(new CampaignGrant
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            CampaignCodeId = grantedCode.Id,
            UserId = user.Id,
            AssignedAt = Clock.GetCurrentInstant(),
            RedeemedAt = Clock.GetCurrentInstant()
        });
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var page = await _service.GetDetailPageAsync(campaign.Id, Xunit.TestContext.Current.CancellationToken);

        page.Should().NotBeNull();
        page.Campaign.Id.Should().Be(campaign.Id);
        page.Stats.TotalCodes.Should().Be(3);
        page.Stats.AvailableCodes.Should().Be(2);
        page.Stats.CodesRedeemed.Should().Be(1);
        page.Stats.TotalGrants.Should().Be(1);
    }

    [HumansFact]
    public async Task GetSendWavePageAsync_ReturnsTeamsAndSelectedPreview()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["A1", "A2"]);
        var user = SeedUser(displayName: "Wave User");
        SeedTeam("Beta Team");
        var alpha = SeedTeam("Alpha Team");
        SeedTeamMember(alpha.Id, user.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var page = await _service.GetSendWavePageAsync(campaign.Id, alpha.Id, Xunit.TestContext.Current.CancellationToken);

        page.Should().NotBeNull();
        page.Campaign.Id.Should().Be(campaign.Id);
        page.SelectedTeamId.Should().Be(alpha.Id);
        page.Preview.Should().NotBeNull();
        page.Preview!.EligibleCount.Should().Be(1);
        page.Teams.Select(t => t.Name).Should().BeEquivalentTo("Alpha Team", "Beta Team");
        page.CanSendWave.Should().BeTrue();
    }

    [HumansTheory]
    [Xunit.InlineData("Draft")]
    [Xunit.InlineData("Completed")]
    public async Task GetSendWavePageAsync_InactiveCampaign_CannotSendWave(string status)
    {
        var campaign = await SeedCampaignAsync(Enum.Parse<CampaignStatus>(status));

        var page = await _service.GetSendWavePageAsync(campaign.Id, null, Xunit.TestContext.Current.CancellationToken);

        page.Should().NotBeNull();
        page.CanSendWave.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetCampaignIdForGrantAsync_ReturnsCampaignId()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["RESEND-CODE"]);
        var user = SeedUser(displayName: "Grant User");
        var team = SeedTeam("Grant Team");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        await _service.SendWaveAsync(_actorId, campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);
        var grant = await CampaignsDb.CampaignGrants.SingleAsync(Xunit.TestContext.Current.CancellationToken);

        var campaignId = await _service.GetCampaignIdForGrantAsync(grant.Id, Xunit.TestContext.Current.CancellationToken);

        campaignId.Should().Be(campaign.Id);
    }

    [HumansFact]
    public async Task CompleteAsync_ActiveCampaign_TransitionsToCompleted()
    {
        var campaign = await SeedCampaignAsync(CampaignStatus.Active);

        await _service.CompleteAsync(_actorId, campaign.Id, Xunit.TestContext.Current.CancellationToken);

        var updated = await CampaignsDb.Campaigns.FindAsync(campaign.Id, Xunit.TestContext.Current.CancellationToken);
        updated!.Status.Should().Be(CampaignStatus.Completed);
    }

    [HumansFact]
    public async Task CompleteAsync_NotActive_ReturnsNotActive()
    {
        var campaign = await SeedCampaignAsync();

        var result = await _service.CompleteAsync(_actorId, campaign.Id, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotActive");
        var updated = await CampaignsDb.Campaigns.FindAsync(campaign.Id, Xunit.TestContext.Current.CancellationToken);
        updated!.Status.Should().Be(CampaignStatus.Draft);
    }

    [HumansFact]
    public async Task SendWaveAsync_AssignsCodeToTeamMember_CreatesGrantAndEnqueuesEmail()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["CODE-A", "CODE-B"],
            emailSubject: "Hi {{Name}}, here is your code",
            emailBodyTemplate: "<p>Hi {{Name}}, your code is {{Code}}</p>");

        var user = SeedUser(displayName: "Alice");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.SendWaveAsync(_actorId, campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);

        result.SentCount.Should().Be(1);

        var grants = await CampaignsDb.CampaignGrants
            .Where(g => g.CampaignId == campaign.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        grants.Should().ContainSingle();
        grants[0].UserId.Should().Be(user.Id);
#pragma warning disable CS0618 // a retired column must stay unwritten until its drop is approved
        grants[0].LatestEmailStatus.Should().BeNull();
        grants[0].LatestEmailAt.Should().BeNull();
#pragma warning restore CS0618

        // CampaignService hands the built message to IEmailService — verify the
        // campaign, recipient and code reached it. Allocation orders by ImportOrder, so the
        // first code goes out first.
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m =>
                m.CampaignId == campaign.Id
                && m.UserId == user.Id
                && m.RecipientEmail == user.Email
                && m.HtmlBody.Contains("CODE-A")),
            Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData("x")]
    [Xunit.InlineData("🚀")]
    public async Task SendWaveAsync_LongCampaignTitle_DispatchesFullNoticeCopy(string character)
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["CODE-A"]);
        campaign.Title = string.Concat(Enumerable.Repeat(character, 200));
        CampaignsDb.Campaigns.Update(campaign);
        var user = SeedUser();
        var team = SeedTeam("Notice Team");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.SendWaveAsync(_actorId, campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);

        result.SentCount.Should().Be(1);
        var call = _notifications.ReceivedCalls().Should().ContainSingle().Subject;
        var arguments = call.GetArguments();
        var fullTitle = $"You received a code from campaign: {campaign.Title}";
        arguments[3].Should().Be(fullTitle);
        arguments[5].Should().Be("Your campaign code is available under My Codes in your profile.");
        ((IReadOnlyList<Guid>)arguments[4]!).Should().Equal(user.Id);
        arguments[0].Should().Be(CampaignsNotificationSources.CampaignReceived);
    }

    [HumansFact]
    public async Task SendWaveAsync_GroupsRecipientsByLanguageWithEnglishFallbackAndIsolatesDispatchFailures()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var copy = new[]
        {
            ("en", "You received a code from campaign: Test Campaign", "Your campaign code is available under My Codes in your profile.", "My Codes"),
            ("es", "Has recibido un código de la campaña: Test Campaign", "Tu código de campaña está disponible en Mis códigos en tu perfil.", "Mis códigos"),
            ("de", "Du hast einen Code aus der Kampagne erhalten: Test Campaign", "Dein Kampagnencode ist unter Meine Codes in deinem Profil verfügbar.", "Meine Codes"),
            ("it", "Hai ricevuto un codice dalla campagna: Test Campaign", "Il tuo codice della campagna è disponibile in I miei codici nel tuo profilo.", "I miei codici"),
            ("fr", "Tu as reçu un code de la campagne : Test Campaign", "Ton code de campagne est disponible dans Mes codes sur ton profil.", "Mes codes"),
            ("ca", "Has rebut un codi de la campanya: Test Campaign", "El teu codi de campanya està disponible a Els meus codis del teu perfil.", "Els meus codis"),
        };
        var campaign = await SeedActiveCampaignWithCodesAsync(Enumerable.Range(0, 7).Select(i => $"CODE-{i}").ToArray());
        var team = SeedTeam("Translated Notices");
        var recipients = copy.Select(x => SeedUser(preferredLanguage: x.Item1)).ToArray();
        var fallback = SeedUser(preferredLanguage: "unsupported");
        foreach (var user in recipients.Append(fallback))
            SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(ct);
        var attempts = 0;
        _notifications.SendAsync(Arg.Any<NotificationSourceDefinition>(), Arg.Any<NotificationClass>(), Arg.Any<NotificationPriority>(),
            Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++attempts == 1 ? Task.FromException(new InvalidOperationException("notification failure")) : Task.CompletedTask);

        var result = await _service.SendWaveAsync(_actorId, campaign.Id, team.Id, ct);

        result.SentCount.Should().Be(7);
        var calls = _notifications.ReceivedCalls().ToList();
        calls.Should().HaveCount(6, "one failed language must not prevent the others from being attempted");
        for (var index = 0; index < recipients.Length; index++)
        {
            var user = recipients[index];
            var args = calls.Single(c => ((IReadOnlyList<Guid>)c.GetArguments()[4]!).Contains(user.Id)).GetArguments();
            args[3].Should().Be(copy[index].Item2);
            args[5].Should().Be(copy[index].Item3);
            args[6].Should().Be("/Profile/Me");
            args[7].Should().Be(copy[index].Item4);
            ((IReadOnlyList<Guid>)args[4]!).Should().BeEquivalentTo(index == 0 ? [user.Id, fallback.Id] : new[] { user.Id });
        }
    }

    [HumansFact]
    public async Task SendWaveAsync_SendsMessageBuiltFromRawTemplateValues()
    {
        // CampaignService forwards the raw subject/body/code/name to CampaignsEmails,
        // which does the substitution and HTML-encoding before SendAsync sees it.
        var campaign = await SeedActiveCampaignWithCodesAsync(["A<B>C"],
            emailSubject: "Hi {{Name}}, your code",
            emailBodyTemplate: "<p>Hi {{Name}}, your code is {{Code}}</p>");

        var user = SeedUser(displayName: "O'Brien & Co");
        var team = SeedTeam("Gamma");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        await _service.SendWaveAsync(_actorId, campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m =>
                m.TemplateName == "campaign_code"
                && m.Subject == "Hi O'Brien & Co, your code"
                && m.RecipientName == "O'Brien & Co"
                && m.HtmlBody.Contains("A&lt;B&gt;C")
                && m.HtmlBody.Contains("O'Brien &amp; Co")),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendWaveAsync_NotActive_ReturnsNotActive()
    {
        var campaign = await SeedCampaignAsync();
        await _service.ImportCodesAsync(_actorId, campaign.Id, ["CODE-1"], Xunit.TestContext.Current.CancellationToken);
        var user = SeedUser(displayName: "Draft User");
        var team = SeedTeam("DraftTeam");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.SendWaveAsync(_actorId, campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotActive");
        (await CampaignsDb.CampaignGrants.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansFact]
    public async Task SendWaveAsync_EnqueueThrowsForOneGrant_KeepsEveryGrantAndContinues()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["ISO-1", "ISO-2"]);

        var user1 = SeedUser(displayName: "First");
        var user2 = SeedUser(displayName: "Second");
        var team = SeedTeam("Isolation");
        SeedTeamMember(team.Id, user1.Id);
        SeedTeamMember(team.Id, user2.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var sendCalls = 0;
        _emailService.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++sendCalls == 1
                ? Task.FromException(new InvalidOperationException("enqueue boom"))
                : Task.CompletedTask);

        var result = await _service.SendWaveAsync(_actorId, campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);

        // The wave survives the throw: both grants exist and the second send still ran.
        result.SentCount.Should().Be(2);
        sendCalls.Should().Be(2);
        var grants = await CampaignsDb.CampaignGrants
            .Where(g => g.CampaignId == campaign.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        grants.Should().HaveCount(2);
        var notice = _notifications.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        ((IReadOnlyList<Guid>)notice[4]!).Should().BeEquivalentTo(new[] { user1.Id, user2.Id });
        notice[5].Should().Be("Your campaign code is available under My Codes in your profile.");
        notice[6].Should().Be("/Profile/Me");
        notice[7].Should().Be("My Codes");
    }

    [HumansFact]
    public async Task SendWaveAsync_CanceledEnqueue_PropagatesAndKeepsTheAuditedGrant()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["CANCEL-1"]);
        var user = SeedUser(displayName: "Canceled");
        var team = SeedTeam("Cancellation");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        _emailService.SendAsync(Arg.Any<EmailMessage>(), cancellation.Token)
            .Returns(async _ =>
            {
                await cancellation.CancelAsync();
                throw new OperationCanceledException(cancellation.Token);
            });

        var act = () => _service.SendWaveAsync(_actorId, campaign.Id, team.Id, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var grant = await CampaignsDb.CampaignGrants.SingleAsync(Xunit.TestContext.Current.CancellationToken);
        await _audit.Received(1).LogAsync(CampaignsAuditActions.CampaignWaveSent, "Campaign", campaign.Id,
            Arg.Is<string>(description => description.Contains(grant.Id.ToString(), StringComparison.Ordinal)),
            _actorId, user.Id, "User");
    }

    [HumansFact]
    public async Task SendWaveAsync_DuplicatePrevention_ExcludesAlreadyGranted()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["CODE-1", "CODE-2", "CODE-3"]);

        var user1 = SeedUser(displayName: "Alice");
        var user2 = SeedUser(displayName: "Bob");
        var team = SeedTeam("Delta");
        SeedTeamMember(team.Id, user1.Id);
        SeedTeamMember(team.Id, user2.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        // First wave sends to both
        var wave1 = await _service.SendWaveAsync(_actorId, campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);
        wave1.SentCount.Should().Be(2);

        // Second wave should send to nobody (both already granted)
        var wave2 = await _service.SendWaveAsync(_actorId, campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);
        wave2.SentCount.Should().Be(0);
    }

    [HumansFact]
    public async Task SendWaveAsync_InsufficientCodes_ReturnsNotEnoughCodes()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["ONLY-ONE"]);

        var user1 = SeedUser(displayName: "Alice");
        var user2 = SeedUser(displayName: "Bob");
        var team = SeedTeam("Zeta");
        SeedTeamMember(team.Id, user1.Id);
        SeedTeamMember(team.Id, user2.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.SendWaveAsync(_actorId, campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotEnoughCodes");
        result.CodesNeeded.Should().Be(2);
        result.CodesAvailable.Should().Be(1);
        (await CampaignsDb.CampaignGrants.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
    }


    [HumansFact]
    public async Task ResendToGrantAsync_EnqueuesNewEmailAndAudits()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["RESEND-CODE"]);

        var user = SeedUser(displayName: "Dave");
        var team = SeedTeam("Theta");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        await _service.SendWaveAsync(_actorId, campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);
        _emailService.ClearReceivedCalls();

        var grant = await CampaignsDb.CampaignGrants.SingleAsync(Xunit.TestContext.Current.CancellationToken);

        await _service.ResendToGrantAsync(_actorId, grant.Id, Xunit.TestContext.Current.CancellationToken);

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.UserId == user.Id && m.CampaignId == campaign.Id),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(CampaignsAuditActions.CampaignGrantResent, "CampaignGrant", grant.Id,
            Arg.Any<string>(), _actorId, user.Id, "User");
    }

    [HumansTheory]
    [Xunit.InlineData("user")]
    [Xunit.InlineData("address")]
    [Xunit.InlineData("enqueue")]
    public async Task ResendToGrantAsync_FailurePropagatesAndKeepsTheGrant(string failure)
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["RESEND-FAILED"]);
        var user = SeedUser();
        var team = SeedTeam("Resend");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        await _service.SendWaveAsync(_actorId, campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);
        var grant = await CampaignsDb.CampaignGrants.SingleAsync(Xunit.TestContext.Current.CancellationToken);
        _emailService.ClearReceivedCalls();
        if (string.Equals(failure, "user", StringComparison.Ordinal))
            _people.Remove(user.Id);
        else if (string.Equals(failure, "address", StringComparison.Ordinal))
            _people[user.Id] = user with { IdentityEmailColumn = null };
        else
            _emailService.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new InvalidOperationException("enqueue unavailable")));

        var act = () => _service.ResendToGrantAsync(_actorId, grant.Id, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();

        ClearAllTrackers();
        (await CampaignsDb.CampaignGrants.FindAsync(grant.Id, Xunit.TestContext.Current.CancellationToken))
            .Should().NotBeNull();
    }

    [HumansFact]
    public async Task PreviewWaveSendAsync_ReturnsCorrectCounts()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["P1", "P2", "P3", "P4", "P5"]);

        var eligible = SeedUser(displayName: "Eligible");
        var alreadyGranted = SeedUser(displayName: "Granted");
        var otherUser = SeedUser(displayName: "Other");
        var team = SeedTeam("Preview");
        SeedTeamMember(team.Id, eligible.Id);
        SeedTeamMember(team.Id, alreadyGranted.Id);
        SeedTeamMember(team.Id, otherUser.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        // Grant a code to alreadyGranted user manually
        var code = await CampaignsDb.CampaignCodes.FirstAsync(c => c.CampaignId == campaign.Id, Xunit.TestContext.Current.CancellationToken);
        CampaignsDb.CampaignGrants.Add(new CampaignGrant
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            CampaignCodeId = code.Id,
            UserId = alreadyGranted.Id,
            AssignedAt = Clock.GetCurrentInstant()
        });
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var preview = await _service.PreviewWaveSendAsync(campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);

        preview.EligibleCount.Should().Be(2); // "Eligible" + "Other"
        preview.AlreadyGrantedExcluded.Should().Be(1);
        preview.CodesAvailable.Should().Be(4); // 5 total - 1 granted
        preview.CodesRemainingAfterSend.Should().Be(2); // 4 available - 2 eligible
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ContributeForUserAsync_ExportsGrantRows(bool requestArchivedId)
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["EXPORT-CODE", "OTHER-CODE"]);
        var user = SeedUser(displayName: "Export User");
        var archivedId = Guid.NewGuid();
        _people[user.Id] = user with { MergedUserIds = [archivedId] };
        _people[archivedId] = _people[user.Id];
        var codes = await CampaignsDb.CampaignCodes.ToListAsync(Xunit.TestContext.Current.CancellationToken);
        var code = codes.Single(c => string.Equals(c.Code, "EXPORT-CODE", StringComparison.Ordinal));
        CampaignsDb.CampaignGrants.Add(new CampaignGrant
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            CampaignCodeId = code.Id,
            UserId = user.Id,
            AssignedAt = Clock.GetCurrentInstant(),
#pragma warning disable CS0618 // old grants still hand over their stored status
            // A grant from before delivery tracking was dropped still hands over what it holds.
            LatestEmailStatus = EmailOutboxStatus.Sent
#pragma warning restore CS0618
        });
        CampaignsDb.CampaignGrants.Add(new CampaignGrant
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            CampaignCodeId = codes.Single(c => string.Equals(c.Code, "OTHER-CODE", StringComparison.Ordinal)).Id,
            UserId = SeedUser(displayName: "Unrelated user").Id,
            AssignedAt = Clock.GetCurrentInstant()
        });
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var slices = await _service.ContributeForUserAsync(requestArchivedId ? archivedId : user.Id, Xunit.TestContext.Current.CancellationToken);

        slices.Should().ContainSingle();
        slices[0].SectionName.Should().Be(CampaignService.CampaignGrants);
        var rows = (System.Collections.IEnumerable)slices[0].Data!;
        rows.Cast<object>().Should().ContainSingle();
        var exported = System.Text.Json.JsonSerializer.SerializeToElement(slices[0].Data).EnumerateArray().Single();
        exported.GetProperty("Code").GetString().Should().Be("EXPORT-CODE");
        exported.GetProperty("EmailStatus").GetString().Should().Be("Sent");
    }

    [HumansFact]
    public async Task GetCodeTrackingAsync_combines_grant_redemption_with_recipient_display_data()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["TRACK-CODE"]);
        var recipient = SeedUser(displayName: "Tracker");
        var code = await CampaignsDb.CampaignCodes.SingleAsync(Xunit.TestContext.Current.CancellationToken);
        var redeemedAt = Clock.GetCurrentInstant();
        await CampaignsDb.CampaignGrants.AddAsync(new CampaignGrant
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            CampaignCodeId = code.Id,
            UserId = recipient.Id,
            AssignedAt = redeemedAt,
            RedeemedAt = redeemedAt
        }, Xunit.TestContext.Current.CancellationToken);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var tracking = await _service.GetCodeTrackingAsync(Xunit.TestContext.Current.CancellationToken);

        tracking.Campaigns.Should().ContainSingle(summary =>
            summary.CampaignId == campaign.Id && summary.TotalGrants == 1 && summary.Redeemed == 1);
        tracking.Grants.Should().ContainSingle(grant =>
            grant.CampaignId == campaign.Id && grant.RecipientName == "Tracker" &&
            grant.Code == "TRACK-CODE" && grant.RedeemedAt == redeemedAt);
    }

    [HumansFact]
    public async Task EraseForUserAsync_DeletesOnlyThatUsersGrants()
    {
        var campaign = await SeedActiveCampaignWithCodesAsync(["ERASE-1", "ERASE-2"]);
        var erased = SeedUser(displayName: "Erased");
        var kept = SeedUser(displayName: "Kept");
        var team = SeedTeam("Erasure");
        SeedTeamMember(team.Id, erased.Id);
        SeedTeamMember(team.Id, kept.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        await _service.SendWaveAsync(_actorId, campaign.Id, team.Id, Xunit.TestContext.Current.CancellationToken);

        await _service.EraseForUserAsync(erased.Id, Xunit.TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var remaining = await CampaignsDb.CampaignGrants.ToListAsync(Xunit.TestContext.Current.CancellationToken);
        remaining.Should().ContainSingle();
        remaining[0].UserId.Should().Be(kept.Id);
    }

    [HumansFact]
    public async Task ReassignAsync_MovesGrantsToTarget_TargetWinsOnCollision()
    {
        // Campaign A: both source and target hold a grant -> target's survives.
        // Campaign B: only source holds a grant -> it moves to target.
        var campaignA = await SeedActiveCampaignWithCodesAsync(["A-SRC", "A-TGT"]);
        var campaignB = await SeedActiveCampaignWithCodesAsync(["B-SRC"]);
        var source = SeedUser(displayName: "Source");
        var target = SeedUser(displayName: "Target");
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var codes = await CampaignsDb.CampaignCodes.ToListAsync(Xunit.TestContext.Current.CancellationToken);
        Guid GrantOn(string code, Guid userId)
        {
            var c = codes.Single(x => string.Equals(x.Code, code, StringComparison.Ordinal));
            var id = Guid.NewGuid();
            CampaignsDb.CampaignGrants.Add(new CampaignGrant
            {
                Id = id,
                CampaignId = c.CampaignId,
                CampaignCodeId = c.Id,
                UserId = userId,
                AssignedAt = Clock.GetCurrentInstant()
            });
            return id;
        }
        GrantOn("A-SRC", source.Id);
        var targetGrantA = GrantOn("A-TGT", target.Id);
        var sourceGrantB = GrantOn("B-SRC", source.Id);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        ClearAllTrackers();

        await _service.ReassignAsync(
            source.Id, target.Id, Guid.NewGuid(), Clock.GetCurrentInstant(),
            Xunit.TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var grants = await CampaignsDb.CampaignGrants.ToListAsync(Xunit.TestContext.Current.CancellationToken);
        grants.Should().OnlyContain(g => g.UserId == target.Id);
        grants.Select(g => g.Id).Should().BeEquivalentTo([targetGrantA, sourceGrantB]);
        grants.Single(g => g.CampaignId == campaignA.Id).Id.Should().Be(targetGrantA);
    }

    private async Task<Campaign> SeedCampaignAsync(
        CampaignStatus status = CampaignStatus.Draft,
        string emailSubject = "Your code: {{Code}}",
        string emailBodyTemplate = "<p>Hi {{Name}}, your code is {{Code}}</p>")
    {
        var creatorId = Guid.NewGuid();
        SeedUser(creatorId, "Creator");

        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            Title = "Test Campaign",
            EmailSubject = emailSubject,
            EmailBodyTemplate = emailBodyTemplate,
            Status = status,
            CreatedAt = Clock.GetCurrentInstant(),
            CreatedByUserId = creatorId
        };
        CampaignsDb.Campaigns.Add(campaign);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        ClearAllTrackers();
        return campaign;
    }

    private async Task<Campaign> SeedActiveCampaignWithCodesAsync(
        string[] codes,
        string emailSubject = "Your code: {{Code}}",
        string emailBodyTemplate = "<p>Hi {{Name}}, your code is {{Code}}</p>")
    {
        var campaign = await SeedCampaignAsync(
            CampaignStatus.Active,
            emailSubject,
            emailBodyTemplate);

        var now = Clock.GetCurrentInstant();
        for (var i = 0; i < codes.Length; i++)
        {
            CampaignsDb.CampaignCodes.Add(new CampaignCode
            {
                Id = Guid.NewGuid(),
                CampaignId = campaign.Id,
                Code = codes[i],
                ImportOrder = i + 1,
                ImportedAt = now
            });
        }
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        ClearAllTrackers();
        return campaign;
    }

    private UserInfo SeedUser(Guid? id = null, string displayName = "Test User", string preferredLanguage = "en")
    {
        var userId = id ?? Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = $"test-{userId}@test.com",
            Email = $"test-{userId}@test.com",
            DisplayName = displayName,
            // UserInfo.BurnerName reads User.BurnerName only, so the fixture sets it directly.
            BurnerName = displayName,
            PreferredLanguage = preferredLanguage,
            CreatedAt = Clock.GetCurrentInstant()
        };
        var info = UserInfo.Create(
            user: user,
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: null,
            communicationPreferences: []);
        _people[userId] = info;
        return info;
    }

    private TeamInfo SeedTeam(string name)
    {
        var team = new TeamInfo(
            Guid.NewGuid(), name, null, name.ToLowerInvariant().Replace(" ", "-"),
            IsActive: true, IsSystemTeam: false, SystemTeamType.None, RequiresApproval: false,
            IsPublicPage: false, IsHidden: false, IsPromotedToDirectory: false,
            Clock.GetCurrentInstant(), []);
        _teams[team.Id] = team;
        return team;
    }

    private void SeedTeamMember(Guid teamId, Guid userId)
    {
        _people.TryGetValue(userId, out var person);
        _teams[teamId].Members.Add(new TeamMemberInfo(
            Guid.NewGuid(), userId, person?.BurnerName ?? string.Empty,
            person?.Email, null, TeamMemberRole.Member, Clock.GetCurrentInstant()));
    }

    private Task SaveAllAsync(CancellationToken ct = default) => CampaignsDb.SaveChangesAsync(ct);

    private void ClearAllTrackers() => CampaignsDb.ChangeTracker.Clear();
}
