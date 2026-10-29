using System.Security.Claims;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Controllers;
using Humans.Tickets.Data;
using Humans.Tickets.Services;
using Humans.Tickets.ViewComponents;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;
using LegacyRow = Humans.Users.Contracts.EventParticipation;
using TicketRow = Humans.Tickets.Domain.EventParticipation;

namespace Humans.Tickets.Tests.Services;

public sealed class EventParticipationServiceTests : IDisposable
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 21, 12, 0);
    private static readonly Instant Earlier = Instant.FromUtc(2026, 7, 8, 9, 0);

    private readonly FakeClock _clock = new(Now);
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly List<UserInfo> _all = [];
    private readonly TicketsDbContext _db;
    private readonly EventParticipationService _sut;

    public EventParticipationServiceTests()
    {
        var options = new DbContextOptionsBuilder<TicketsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new TicketsDbContext(options);
        _sut = new EventParticipationService(
            new EventParticipationRepository(new TestDbContextFactory<TicketsDbContext>(options)),
            _users, _audit, _clock, NullLogger<EventParticipationService>.Instance);
        _users.GetAllRawUserInfosAsync(Arg.Any<CancellationToken>()).Returns(_ => _all.ToList());
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A member whose frozen Users participation rows are <paramref name="legacy"/>.</summary>
    private UserInfo SeedUser(params (int Year, LegacyParticipationStatus Status, LegacyParticipationSource Source)[] legacy)
    {
        var userId = Guid.NewGuid();
        var info = UserInfo.Create(
            new User { Id = userId, BurnerName = "Ana", CreatedAt = Now },
            [],
            [.. legacy.Select(l => new LegacyRow
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Year = l.Year,
                Status = l.Status,
                Source = l.Source,
                DeclaredAt = l.Status == LegacyParticipationStatus.NotAttending ? Earlier : null,
                CheckedInAt = l.Status == LegacyParticipationStatus.Attended ? Earlier : null,
            })],
            [], null, []);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(info);
        _users.GetRawUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(info);
        _all.Add(info);
        return info;
    }

    private Task<List<TicketRow>> RowsAsync(Guid userId) =>
        _db.EventParticipations.AsNoTracking().Where(r => r.UserId == userId).ToListAsync(Ct);

    [HumansFact]
    public async Task Read_FallsBackToTheLegacyRow_UntilARowExistsForThatYear()
    {
        var user = SeedUser(
            (2025, LegacyParticipationStatus.Attended, LegacyParticipationSource.TicketSync),
            (2026, LegacyParticipationStatus.Ticketed, LegacyParticipationSource.TicketSync));

        (await _sut.GetParticipationsAsync(user.Id, Ct)).Should().BeEquivalentTo(
        [
            new { Year = 2025, Status = ParticipationStatus.Attended, OnsiteSince = (Instant?)Earlier },
            new { Year = 2026, Status = ParticipationStatus.Ticketed, OnsiteSince = (Instant?)null },
        ], o => o.ExcludingMissingMembers());

        await _sut.RecordParticipationAsync(user.Id, 2026, ParticipationStatus.Attended, Now, Ct);

        var read = await _sut.GetParticipationsAsync(user.Id, Ct);
        read.Single(p => p.Year == 2026).OnsiteSince.Should().Be(Now);
        read.Single(p => p.Year == 2025).Status.Should().Be(ParticipationStatus.Attended);
        (await _sut.GetAllParticipationsAsync(Ct)).Should().BeEquivalentTo(read);
    }

    [HumansFact]
    public async Task Read_AMergedAwayId_ResolvesToTheSurvivorsParticipations()
    {
        var survivor = SeedUser((2026, LegacyParticipationStatus.Ticketed, LegacyParticipationSource.TicketSync));
        var archivedId = Guid.NewGuid();
        _users.GetUserInfoAsync(archivedId, Arg.Any<CancellationToken>()).Returns(survivor);

        (await _sut.GetParticipationsAsync(archivedId, Ct)).Should().ContainSingle()
            .Which.UserId.Should().Be(survivor.Id);
    }

    [HumansFact]
    public async Task Undo_OfALegacyDeclaration_LeavesAClearedRow_SoTheLegacyValueNeverComesBack()
    {
        var user = SeedUser((2026, LegacyParticipationStatus.NotAttending, LegacyParticipationSource.UserDeclared));

        (await _sut.UndoNotAttendingAsync(user.Id, 2026, Ct)).Should().BeTrue();

        (await _sut.GetParticipationsAsync(user.Id, Ct)).Should().BeEmpty();
        (await _sut.GetAllParticipationsAsync(Ct)).Should().BeEmpty();
        (await RowsAsync(user.Id)).Should().ContainSingle().Which.Status.Should().BeNull();
        (await _sut.GetUncopiedLegacyParticipationsAsync(Ct)).Should().BeEmpty();
        (await _sut.UndoNotAttendingAsync(user.Id, 2026, Ct)).Should().BeFalse();
    }

    [HumansFact]
    public async Task Writes_StartFromTheLegacyRow_SoALegacyAttendanceIsNeverDowngraded()
    {
        var user = SeedUser((2026, LegacyParticipationStatus.Attended, LegacyParticipationSource.TicketSync));

        (await _sut.DeclareNotAttendingAsync(user.Id, 2026, Ct)).Should().BeFalse();
        await _sut.RemoveTicketSyncParticipationAsync(user.Id, 2026, Ct);
        await _sut.RecordParticipationAsync(user.Id, 2026, ParticipationStatus.Ticketed, null, Ct);

        (await _sut.GetParticipationsAsync(user.Id, Ct)).Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Status = ParticipationStatus.Attended, CheckedInAt = (Instant?)Earlier },
            o => o.ExcludingMissingMembers());
    }

    [HumansFact]
    public async Task Declare_ThenUndo_WithNoLegacyRow_LeavesNothing()
    {
        var user = SeedUser();

        (await _sut.DeclareNotAttendingAsync(user.Id, 2026, Ct)).Should().BeTrue();
        (await _sut.GetParticipationsAsync(user.Id, Ct)).Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Status = ParticipationStatus.NotAttending, Source = ParticipationSource.UserDeclared, DeclaredAt = (Instant?)Now },
            o => o.ExcludingMissingMembers());

        (await _sut.UndoNotAttendingAsync(user.Id, 2026, Ct)).Should().BeTrue();
        (await RowsAsync(user.Id)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Backfill_AuditsOnlyCommittedFinalChanges_AndNoOpsOnRepeat()
    {
        var user = SeedUser();
        var attended = SeedUser((2025, LegacyParticipationStatus.Attended, LegacyParticipationSource.TicketSync));
        var actorId = Guid.NewGuid();
        List<(Guid UserId, ParticipationStatus Status)> entries =
        [
            (user.Id, ParticipationStatus.Ticketed), (user.Id, ParticipationStatus.NoShow),
            (attended.Id, ParticipationStatus.NotAttending),
        ];

        (await _sut.BackfillParticipationsAsync(2025, entries, actorId, Ct)).Should().Be(1);

        await _audit.Received(1).LogAsync(AuditAction.ParticipationBackfilled, nameof(User), user.Id,
            Arg.Is<string>(d => d.Contains("NoShow", StringComparison.Ordinal) && d.Contains("2025", StringComparison.Ordinal)),
            actorId);
        _audit.ReceivedCalls().Should().HaveCount(1);
        (await _sut.GetParticipationsAsync(attended.Id, Ct)).Single().Status.Should().Be(ParticipationStatus.Attended);

        (await _sut.BackfillParticipationsAsync(2025, entries, actorId, Ct)).Should().Be(0);
        _audit.ReceivedCalls().Should().HaveCount(1);
    }

    [HumansFact]
    public async Task Copy_ListsEveryUncopiedLegacyRow_ThenIsIdempotent()
    {
        var a = SeedUser(
            (2025, LegacyParticipationStatus.Attended, LegacyParticipationSource.TicketSync),
            (2026, LegacyParticipationStatus.NotAttending, LegacyParticipationSource.UserDeclared));
        var b = SeedUser((2026, LegacyParticipationStatus.NoShow, LegacyParticipationSource.AdminBackfill));
        SeedUser();

        (await _sut.GetUncopiedLegacyParticipationsAsync(Ct)).Select(p => (p.UserId, p.Year))
            .Should().BeEquivalentTo([(a.Id, 2025), (a.Id, 2026), (b.Id, 2026)]);

        (await _sut.CopyLegacyParticipationsAsync(Ct)).Should().Be(3);
        (await _sut.CopyLegacyParticipationsAsync(Ct)).Should().Be(0);
        (await _sut.GetUncopiedLegacyParticipationsAsync(Ct)).Should().BeEmpty();
        (await RowsAsync(a.Id)).Should().BeEquivalentTo(
        [
            new { Year = 2025, Status = (ParticipationStatus?)ParticipationStatus.Attended, Source = ParticipationSource.TicketSync, CheckedInAt = (Instant?)Earlier },
            new { Year = 2026, Status = (ParticipationStatus?)ParticipationStatus.NotAttending, Source = ParticipationSource.UserDeclared, CheckedInAt = (Instant?)null },
        ], o => o.ExcludingMissingMembers());
        (await RowsAsync(b.Id)).Single().Source.Should().Be(ParticipationSource.AdminBackfill);
    }

    [HumansFact]
    public async Task Copy_NeverOverwritesARowWrittenSinceTheDeploy()
    {
        var user = SeedUser((2026, LegacyParticipationStatus.Ticketed, LegacyParticipationSource.TicketSync));
        await _sut.RecordParticipationAsync(user.Id, 2026, ParticipationStatus.Attended, Now, Ct);

        (await _sut.CopyLegacyParticipationsAsync(Ct)).Should().Be(0);
        (await RowsAsync(user.Id)).Single().Status.Should().Be(ParticipationStatus.Attended);
    }

    [HumansFact]
    public async Task DataMigration_IsRegistered_AndRunsTheCopy()
    {
        SeedUser((2026, LegacyParticipationStatus.Ticketed, LegacyParticipationSource.TicketSync));
        var services = new ServiceCollection().AddSingleton<IEventParticipationManagement>(_sut).BuildServiceProvider();
        var migration = ((ISectionDataMigrations)new Section()).Migrations().Should().ContainSingle().Subject;

        migration.Key.Should().Be("tickets.event-participations");
        (await migration.Status(services, Ct)).Pending.Should().Be(1);
        (await migration.Apply(services, Guid.NewGuid(), Ct)).Should().Be(1);
        (await migration.Status(services, Ct)).Pending.Should().Be(0);
    }

    [HumansFact]
    public async Task Merge_FoldsBothUsersLegacyRows_ByPrecedence_AndClearsTheSource()
    {
        var source = SeedUser(
            (2024, LegacyParticipationStatus.Attended, LegacyParticipationSource.TicketSync),
            (2025, LegacyParticipationStatus.NotAttending, LegacyParticipationSource.UserDeclared),
            (2023, LegacyParticipationStatus.Attended, LegacyParticipationSource.TicketSync));
        var target = SeedUser(
            (2024, LegacyParticipationStatus.NotAttending, LegacyParticipationSource.UserDeclared),
            (2025, LegacyParticipationStatus.Ticketed, LegacyParticipationSource.TicketSync));

        await _sut.ReassignAsync(source.Id, target.Id, Guid.NewGuid(), Now, Ct);

        (await _sut.GetParticipationsAsync(target.Id, Ct)).Select(p => (p.Year, p.Status)).Should().BeEquivalentTo(
        [
            (2023, ParticipationStatus.Attended),
            (2024, ParticipationStatus.Attended),
            (2025, ParticipationStatus.Ticketed),
        ]);
        (await _sut.GetParticipationsAsync(source.Id, Ct)).Should().BeEmpty();
        (await RowsAsync(source.Id)).Should().OnlyContain(r => r.Status == null);
    }

    [HumansFact]
    public async Task Export_CarriesTheEffectiveRows_AsAList()
    {
        var user = SeedUser((2025, LegacyParticipationStatus.Attended, LegacyParticipationSource.TicketSync));
        var none = SeedUser();

        var slice = (await _sut.ContributeForUserAsync(user.Id, Ct)).Should().ContainSingle().Subject;
        slice.SectionName.Should().Be(EventParticipationService.EventParticipationsSlice);
        slice.Data.Should().BeEquivalentTo(new[] { new { Year = 2025, Status = "Attended", Source = "TicketSync" } },
            o => o.ExcludingMissingMembers());
        (await _sut.ContributeForUserAsync(none.Id, Ct)).Single().Data
            .Should().BeAssignableTo<System.Collections.IEnumerable>().Which.Cast<object>().Should().BeEmpty();
    }

    [HumansFact]
    public async Task Erase_RetainsTheRows_AsUsersDidForTheLegacyTable()
    {
        var user = SeedUser();
        await _sut.DeclareNotAttendingAsync(user.Id, 2026, Ct);

        await _sut.EraseForUserAsync(user.Id, Ct);

        _sut.ErasureDeclaration[EventParticipationService.EventParticipationsSlice].Should().StartWith("Retained");
        (await RowsAsync(user.Id)).Should().ContainSingle();
    }

    [HumansFact]
    public void UserParts_IncludeTheOnsiteChipOnTheProfile()
    {
        ((IUserPart)new Section()).Parts().Should().Contain(
            new UserPart(UserPartSlots.Profile, typeof(OnsiteChipViewComponent), Weight: 10));
    }

    [HumansTheory]
    [InlineData(ProfileCardViewMode.Self, false, true)]
    [InlineData(ProfileCardViewMode.Public, false, false)]
    [InlineData(ProfileCardViewMode.Public, true, true)]
    [InlineData(ProfileCardViewMode.Admin, false, false)]
    public async Task OnsiteChip_RendersForSelf_OrTheOnsiteRosterPolicy_Only(
        ProfileCardViewMode mode, bool viewerPassesPolicy, bool renders)
    {
        var userId = Guid.NewGuid();
        var tickets = Substitute.For<ITicketService>();
        tickets.GetParticipationsAsync(userId, Arg.Any<CancellationToken>()).Returns(
        [
            new EventParticipationInfo(userId, 2025, ParticipationStatus.Attended, ParticipationSource.TicketSync, null, Earlier),
            new EventParticipationInfo(userId, 2026, ParticipationStatus.Attended, ParticipationSource.TicketSync, null, Now),
        ]);
        var settings = Substitute.For<ISettingsService>();
        settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(BurnFixtures.Burn(year: 2026));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), TicketsPolicies.Read)
            .Returns(viewerPassesPolicy ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var component = WithContext(new OnsiteChipViewComponent(tickets, settings, authorization));

        var result = await component.InvokeAsync(userId, mode);

        if (renders)
            result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model.Should().Be(Now);
        else
            result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
    }

    [HumansFact]
    public async Task OnsiteChip_NothingWithoutACheckInForTheActiveYear()
    {
        var userId = Guid.NewGuid();
        var tickets = Substitute.For<ITicketService>();
        tickets.GetParticipationsAsync(userId, Arg.Any<CancellationToken>()).Returns(
            [new EventParticipationInfo(userId, 2026, ParticipationStatus.Ticketed, ParticipationSource.TicketSync, null, null)]);
        var settings = Substitute.For<ISettingsService>();
        settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(BurnFixtures.Burn(year: 2026));
        var component = WithContext(new OnsiteChipViewComponent(tickets, settings, Substitute.For<IAuthorizationService>()));

        (await component.InvokeAsync(userId, ProfileCardViewMode.Self))
            .Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(nameof(TicketParticipationController.DeclareNotAttending), "DeclareNotAttending")]
    [InlineData(nameof(TicketParticipationController.UndoNotAttending), "UndoNotAttending")]
    public void Declaration_IsAnAuthorizedAntiforgeryPost_AtTheTicketsUrl(string action, string template)
    {
        var type = typeof(TicketParticipationController);
        type.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false).Should().ContainSingle();
        ((RouteAttribute)type.GetCustomAttributes(typeof(RouteAttribute), true).Single()).Template.Should().Be("Tickets");
        var method = type.GetMethod(action)!;
        method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true).Should().ContainSingle();
        ((HttpPostAttribute)method.GetCustomAttributes(typeof(HttpPostAttribute), true).Single()).Template
            .Should().Be(template);
    }

    [HumansFact]
    public async Task Declaration_ActsOnTheSignedInMember_ForTheActiveYear()
    {
        var user = SeedUser();
        var participations = Substitute.For<IEventParticipationManagement>();
        participations.UndoNotAttendingAsync(user.Id, 2026, Arg.Any<CancellationToken>()).Returns(false);
        var controller = NewController(user.Id, participations, activeYear: 2026);

        (await controller.DeclareNotAttending()).Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/");
        await participations.Received(1).DeclareNotAttendingAsync(user.Id, 2026, Arg.Any<CancellationToken>());
        controller.TempData[TempDataKeys.SuccessMessage].Should().Be("Tickets_NotAttending_Declared");

        await controller.UndoNotAttending();
        controller.TempData[TempDataKeys.ErrorMessage].Should().Be("Tickets_NotAttending_UndoFailed");
    }

    [HumansFact]
    public async Task Declaration_WithoutAnActiveEvent_WritesNothing()
    {
        var user = SeedUser();
        var participations = Substitute.For<IEventParticipationManagement>();
        var controller = NewController(user.Id, participations, activeYear: null);

        await controller.DeclareNotAttending();

        await participations.DidNotReceiveWithAnyArgs().DeclareNotAttendingAsync(default, default);
        controller.TempData[TempDataKeys.ErrorMessage].Should().Be("Tickets_NotAttending_NoActiveEvent");
    }

    private TicketParticipationController NewController(
        Guid userId, IEventParticipationManagement participations, int? activeYear)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(activeYear is { } year ? BurnFixtures.Burn(year: year) : null);
        var localizer = Substitute.For<IStringLocalizer<TicketsResource>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString((string)call[0], (string)call[0]));
        var controller = new TicketParticipationController(
            participations, settings, _users, localizer, NullLogger<TicketParticipationController>.Instance);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return controller;
    }

    private static T WithContext<T>(T component) where T : ViewComponent
    {
        component.ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext
            {
                HttpContext = new DefaultHttpContext(),
                ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()),
            },
        };
        return component;
    }
}
