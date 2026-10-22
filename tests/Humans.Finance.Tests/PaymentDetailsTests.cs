using System.Security.Claims;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Finance.Controllers;
using Humans.Finance.Data;
using Humans.Finance.Services;
using Humans.Finance.ViewComponents;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Finance.Tests;

public class PaymentDetailsTests
{
    private const string Iban = "ES9121000418450200051332";
    private const string OtherIban = "ES1000750000010000000000";
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 20, 12, 0);

    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IPaymentDetailsRepository _repo;
    private readonly PaymentDetailsService _sut;

    public PaymentDetailsTests()
    {
        var options = new DbContextOptionsBuilder<FinanceDbContext>()
            .UseInMemoryDatabase($"finance-pd-{Guid.NewGuid():N}")
            .Options;
        _repo = new PaymentDetailsRepository(new Factory(options));
        _sut = new PaymentDetailsService(_repo, _users, _audit, new FakeClock(Now));
    }

    private sealed class Factory(DbContextOptions<FinanceDbContext> options)
        : IDbContextFactory<FinanceDbContext>
    {
        public FinanceDbContext CreateDbContext() => new(options);
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private UserInfo SeedUser(Guid id, string? legacyIban = null, Guid? resolvesTo = null)
    {
        var info = UserInfo.Create(
            new User { Id = resolvesTo ?? id, BurnerName = "Ana", CreatedAt = Now },
            [], [], [], UserFixtures.Profile(iban: legacyIban), []);
        _users.GetUserInfoAsync(id, Arg.Any<CancellationToken>()).Returns(info);
        _users.GetRawUserInfoAsync(id, Arg.Any<CancellationToken>()).Returns(info);
        return info;
    }

    [HumansFact]
    public async Task GetIban_FallsBackToTheLegacyProfileValue_UntilARowExists()
    {
        var id = Guid.NewGuid();
        SeedUser(id, legacyIban: Iban);

        (await _sut.GetIbanAsync(id, Ct)).Should().Be(Iban);

        (await _sut.SetIbanAsync(id, OtherIban, Ct)).Should().BeTrue();
        (await _sut.GetIbanAsync(id, Ct)).Should().Be(OtherIban);
    }

    [HumansFact]
    public async Task SetIban_Clearing_RecordsTheRemoval_SoTheLegacyValueDoesNotComeBack()
    {
        var id = Guid.NewGuid();
        SeedUser(id, legacyIban: Iban);

        (await _sut.SetIbanAsync(id, "  ", Ct)).Should().BeTrue();

        (await _sut.GetIbanAsync(id, Ct)).Should().BeNull();
    }

    [HumansFact]
    public async Task SetIban_Normalizes_AndRefusesAnUnknownUser()
    {
        var id = Guid.NewGuid();
        SeedUser(id);

        await _sut.SetIbanAsync(id, "es91 2100 0418 4502 0005 1332", Ct);

        (await _sut.GetIbanAsync(id, Ct)).Should().Be(Iban);
        (await _sut.SetIbanAsync(Guid.NewGuid(), Iban, Ct)).Should().BeFalse();
    }

    [HumansFact]
    public async Task GetIban_WithAMergedAwayId_ReadsTheSurvivorsRow()
    {
        var survivor = Guid.NewGuid();
        var archived = Guid.NewGuid();
        SeedUser(survivor);
        SeedUser(archived, resolvesTo: survivor);
        await _sut.SetIbanAsync(survivor, Iban, Ct);

        (await _sut.GetIbanAsync(archived, Ct)).Should().Be(Iban);
    }

    [HumansFact]
    public async Task RevealIban_ReturnsTheIban_AndAuditsTheAdmin()
    {
        var target = Guid.NewGuid();
        var admin = Guid.NewGuid();
        SeedUser(target, legacyIban: Iban);

        (await _sut.RevealIbanAsync(target, admin, Ct)).Should().Be(Iban);

        await _audit.Received(1).LogAsync(
            AuditAction.IbanReveal, nameof(User), target,
            Arg.Is<string>(m => m.Contains(target.ToString(), StringComparison.Ordinal)),
            admin, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task RevealIban_WithNoIban_ReturnsNull_AndAuditsNothing()
    {
        var target = Guid.NewGuid();
        SeedUser(target);

        (await _sut.RevealIbanAsync(target, Guid.NewGuid(), Ct)).Should().BeNull();

        await _audit.DidNotReceiveWithAnyArgs().LogAsync(
            default, default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task Migration_ListsEveryUncopiedLegacyIbanMasked_CopiesThem_AndIsIdempotent()
    {
        var pending = Guid.NewGuid();
        var alreadyRow = Guid.NewGuid();
        var noIban = Guid.NewGuid();
        var users = new[]
        {
            SeedUser(pending, legacyIban: Iban),
            SeedUser(alreadyRow, legacyIban: OtherIban),
            SeedUser(noIban),
        };
        _users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(users);
        await _sut.SetIbanAsync(alreadyRow, null, Ct);

        var services = new ServiceCollection()
            .AddSingleton<IPaymentDetailsManagement>(_sut)
            .BuildServiceProvider();
        var migration = new SectionDataMigrations().Migrations()
            .Single(m => string.Equals(m.Key, "finance.iban-to-payment-details", StringComparison.Ordinal));

        var status = await migration.Status(services, Ct);
        status.Pending.Should().Be(1);
        status.PendingRows[0][0].Should().Be(pending.ToString());
        status.PendingRows[0][2].Should().Be(Humans.Base.Helpers.IbanFormatter.Mask(Iban)).And.NotBe(Iban);

        (await migration.Apply(services, Guid.NewGuid(), Ct)).Should().Be(1);
        (await migration.Apply(services, Guid.NewGuid(), Ct)).Should().Be(0);
        (await migration.Status(services, Ct)).Done.Should().BeTrue();
        (await _repo.GetAsync(pending, Ct))!.Iban.Should().Be(Iban);
        (await _sut.GetIbanAsync(alreadyRow, Ct)).Should().BeNull(); // the removal is kept
    }

    [HumansFact]
    public async Task Merge_MovesTheSourceRow_WhenTheSurvivorHasNone()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        SeedUser(source);
        SeedUser(target);
        await _sut.SetIbanAsync(source, Iban, Ct);

        await _sut.ReassignAsync(source, target, Guid.NewGuid(), Now, Ct);
        await _sut.ReassignAsync(source, target, Guid.NewGuid(), Now, Ct); // idempotent

        (await _repo.GetAsync(target, Ct))!.Iban.Should().Be(Iban);
        (await _repo.GetAsync(source, Ct)).Should().BeNull();
    }

    [HumansFact]
    public async Task Merge_KeepsTheSurvivorsIban_IncludingOneStillOnItsLegacyProfile()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        SeedUser(source);
        SeedUser(target, legacyIban: OtherIban);
        await _sut.SetIbanAsync(source, Iban, Ct);

        await _sut.ReassignAsync(source, target, Guid.NewGuid(), Now, Ct);

        (await _repo.GetAsync(target, Ct))!.Iban.Should().Be(OtherIban);
        (await _repo.GetAsync(source, Ct)).Should().BeNull();
    }

    [HumansFact]
    public async Task Export_MasksTheIban_AndErasureDeletesTheRow()
    {
        var id = Guid.NewGuid();
        SeedUser(id);
        await _sut.SetIbanAsync(id, Iban, Ct);

        var slice = (await _sut.ContributeForUserAsync(id, Ct)).Single();
        slice.SectionName.Should().Be(PaymentDetailsService.PaymentDetailsSlice);
        System.Text.Json.JsonSerializer.Serialize(slice.Data).Should().NotContain("21000418");
        _sut.ErasureDeclaration.Should().ContainKey(PaymentDetailsService.PaymentDetailsSlice)
            .WhoseValue.Should().BeNull();

        await _sut.EraseForUserAsync(id, Ct);

        (await _repo.GetAsync(id, Ct)).Should().BeNull();
        (await _sut.ContributeForUserAsync(id, Ct)).Single().Data.Should().BeNull();
    }

    [HumansFact]
    public void RevealIban_IsAdminOnly_PostAndAntiforgery()
    {
        typeof(PaymentDetailsAdminController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>().Single().Policy.Should().Be(PolicyNames.AdminOnly);
        var action = typeof(PaymentDetailsAdminController).GetMethod(nameof(PaymentDetailsAdminController.RevealIban))!;
        action.GetCustomAttributes(typeof(HttpPostAttribute), false).Should().ContainSingle();
        action.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), false).Should().ContainSingle();
    }

    [HumansFact]
    public async Task RevealIban_PutsTheIbanInTempData_AndReturnsToTheAdminDetailPage()
    {
        var target = Guid.NewGuid();
        var admin = Guid.NewGuid();
        SeedUser(target, legacyIban: Iban);
        var controller = BuildController(admin);

        var result = await controller.RevealIban(target, Ct);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Which;
        redirect.ControllerName.Should().Be("UsersAdmin");
        redirect.ActionName.Should().Be("AdminDetail");
        controller.TempData[PaymentDetailsViewComponent.RevealedIbanKey].Should().Be(Iban);
    }

    [HumansFact]
    public async Task RevealIban_WithNoIban_LeavesTempDataEmpty()
    {
        var target = Guid.NewGuid();
        SeedUser(target);
        var controller = BuildController(Guid.NewGuid());

        await controller.RevealIban(target, Ct);

        controller.TempData.ContainsKey(PaymentDetailsViewComponent.RevealedIbanKey).Should().BeFalse();
    }

    [HumansTheory]
    [Xunit.InlineData(true, ProfileCardViewMode.Public)]
    [Xunit.InlineData(false, ProfileCardViewMode.Admin)]
    public async Task Card_RendersNothing_OutsideTheAdminHost_OrForANonAdmin(bool isAdmin, ProfileCardViewMode mode)
    {
        var id = Guid.NewGuid();
        SeedUser(id, legacyIban: Iban);

        var result = await BuildCard(isAdmin).InvokeAsync(id, mode);

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Card_ShowsTheMaskedIbanToAnAdmin()
    {
        var id = Guid.NewGuid();
        SeedUser(id, legacyIban: Iban);

        var result = await BuildCard(isAdmin: true).InvokeAsync(id, ProfileCardViewMode.Admin);

        var model = result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model
            .Should().BeOfType<PaymentDetailsViewModel>().Which;
        model.MaskedIban.Should().NotBeNull().And.NotBe(Iban);
        model.RevealedIban.Should().BeNull();
    }

    [HumansFact]
    public void Parts_ContributeTheCardToTheAdminDetailSidebarOnly()
    {
        IUserPart contribution = new SectionUserParts();

        contribution.Parts().Should().ContainSingle()
            .Which.Should().Be(new UserPart(UserPartSlots.AdminDetailSidebar, typeof(PaymentDetailsViewComponent), 100));
    }

    private PaymentDetailsAdminController BuildController(Guid actorId)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "TestAuth")),
        };
        return new PaymentDetailsAdminController(_users, _sut)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
        };
    }

    private PaymentDetailsViewComponent BuildCard(bool isAdmin)
    {
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), PolicyNames.AdminOnly)
            .Returns(isAdmin ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var http = new DefaultHttpContext();
        return new PaymentDetailsViewComponent(_sut, authorization)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = http,
                    TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
                },
            },
        };
    }
}
