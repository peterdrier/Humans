using System.Globalization;
using System.Net;
using System.Resources;
using System.Text.RegularExpressions;
using Humans.Base;
using Humans.Users.Contracts;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Base.ViewComponents;
using Humans.Base.Extensions;
using Humans.Expenses.Contracts;
using Humans.Expenses.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;

namespace Humans.Expenses.Tests.Models;

public class ExpenseReportViewTests
{
    [HumansTheory]
    [Xunit.InlineData(false, 60, false)]
    [Xunit.InlineData(false, 60, true)]
    [Xunit.InlineData(true, 80, false)]
    [Xunit.InlineData(true, 80, true)]
    public async Task SubjectPreview_PreservesWholeSurrogatePairs(bool review, int limit, bool fits)
    {
        var prefix = new string('a', limit - (fits ? 2 : 1));
        var report = Report(ExpenseReportStatus.Draft, prefix + "😀tail");
        object model = review ? new ExpenseReviewViewModel
        {
            Reports = [report],
            BookingLabels = new Dictionary<Guid, string>(),
            FailedHoldedPushReportIds = new HashSet<Guid>(),
            Grants = new Dictionary<Guid, Humans.Preapprovals.Contracts.GrantOption>(),
        } : new ExpensesIndexViewModel { Reports = [report] };

        var html = await RenderAsync(review ? "Review" : "Index", model);

        html.Should().Contain(HtmlEncoder.Default.Encode(prefix + (fits ? "😀" : "") + "…"));
    }

    [HumansTheory]
    [Xunit.InlineData("en", ExpenseReportStatus.Draft, "Draft")]
    [Xunit.InlineData("es", ExpenseReportStatus.Draft, "Borrador")]
    [Xunit.InlineData("de", ExpenseReportStatus.Draft, "Entwurf")]
    [Xunit.InlineData("it", ExpenseReportStatus.Draft, "Bozza")]
    [Xunit.InlineData("fr", ExpenseReportStatus.Draft, "Brouillon")]
    [Xunit.InlineData("ca", ExpenseReportStatus.Draft, "Esborrany")]
    [Xunit.InlineData("nl", ExpenseReportStatus.Draft, "Concept")]
    [Xunit.InlineData("en", ExpenseReportStatus.CoordinatorEndorsed, "Coordinator endorsed")]
    [Xunit.InlineData("es", ExpenseReportStatus.Approved, "Aprobado")]
    [Xunit.InlineData("fr", ExpenseReportStatus.Withdrawn, "Retirée")]
    [Xunit.InlineData("ca", ExpenseReportStatus.Submitted, "Presentat")]
    public async Task MemberList_StatusBadgeUsesExistingLocalizedLabel(
        string culture, ExpenseReportStatus status, string expected)
    {
        using var language = new CultureScope(culture);
        var html = await RenderAsync("Index", new ExpensesIndexViewModel { Reports = [Report(status)] });

        html.Should().Contain($">{HtmlEncoder.Default.Encode(expected)}</span>");
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)] // category coordinator: views the report, neither submitter nor finance admin
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, true)]
    public async Task Detail_ShowsReportLocalPaymentStatus_ToEveryViewer_AndAccountWideRows_OnlyToSubmitterAndFinance(
        bool isSubmitter, bool isFinanceAdmin)
    {
        var html = await RenderAsync("Detail", new ExpenseDetailViewModel
        {
            Report = Report(ExpenseReportStatus.Approved),
            BookingLabel = "2026",
            IsSubmitter = isSubmitter,
            CanBindCreditor = isFinanceAdmin,
            HoldedTimeline = new ExpenseHoldedTimeline
            {
                RegisteredInHolded = true,
                OwedToMember = 0m,
                MemberRegisteredTotal = 0m,
                // Account-wide: other advances and the whole creditor account's payment history.
                OtherAmount = 17m,
                Paid = true,
                PaidOn = new NodaTime.LocalDate(2026, 3, 9),
                TotalPaid = 913m,
                SyncState = ExpenseHoldedSyncState.Pushed,
            },
        });

        var privileged = isSubmitter || isFinanceAdmin;
        html.Should().Contain("Payment status").And.Contain("Registered in Holded");
        html.Contains("Includes", StringComparison.Ordinal).Should().Be(privileged);
        html.Contains("913", StringComparison.Ordinal).Should().Be(privileged);
        html.Contains("2026-03-09", StringComparison.Ordinal).Should().Be(privileged);
        html.Contains("Holded sync", StringComparison.Ordinal).Should().Be(isFinanceAdmin);
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, true)]
    public async Task Detail_ShowsOwedToYou_AndAwaitingPayment_OnlyToTheirAudience(
        bool isSubmitter, bool isFinanceAdmin)
    {
        var html = await RenderAsync("Detail", new ExpenseDetailViewModel
        {
            Report = Report(ExpenseReportStatus.Approved),
            BookingLabel = "2026",
            IsSubmitter = isSubmitter,
            CanBindCreditor = isFinanceAdmin,
            HoldedTimeline = new ExpenseHoldedTimeline
            {
                RegisteredInHolded = true,
                OwedToMember = 40m,
                MemberRegisteredTotal = 40m,
                OtherAmount = 0m,
                Paid = false,
                TotalPaid = 0m,
                SyncState = ExpenseHoldedSyncState.Pushed,
            },
        });

        html.Contains("owed to you", StringComparison.Ordinal).Should().Be(isSubmitter);
        html.Contains("Awaiting payment", StringComparison.Ordinal).Should().Be(isSubmitter || isFinanceAdmin);
    }

    [HumansTheory]
    [Xunit.InlineData("en", false)]
    [Xunit.InlineData("es", false)]
    [Xunit.InlineData("de", false)]
    [Xunit.InlineData("it", false)]
    [Xunit.InlineData("fr", false)]
    [Xunit.InlineData("ca", false)]
    [Xunit.InlineData("nl", false)]
    [Xunit.InlineData("en", true)]
    [Xunit.InlineData("es", true)]
    [Xunit.InlineData("de", true)]
    [Xunit.InlineData("it", true)]
    [Xunit.InlineData("fr", true)]
    [Xunit.InlineData("ca", true)]
    [Xunit.InlineData("nl", true)]
    public async Task Iban_OnBehalfGuidanceUsesTheHumanEvenWhenTheMemberIsMissing(string language, bool missingMember)
    {
        using var culture = new CultureScope(language);
        var memberId = Guid.NewGuid();
        const string memberName = "Member <script>&";
        var users = Substitute.For<IUserService>();
        if (!missingMember)
            users.GetUserInfoAsync(memberId, Arg.Any<CancellationToken>()).Returns(
                UserInfo.Create(new User { Id = memberId, BurnerName = memberName }, [], [], null));
        var model = new ExpenseIbanViewModel
        {
            ReportId = Guid.NewGuid(),
            OnBehalfOfUserId = memberId,
            ReportStatus = missingMember ? ExpenseReportStatus.Submitted : ExpenseReportStatus.Draft,
        };

        var html = await RenderAsync("Iban", model, users);
        var name = missingMember ? new ResourceManager(typeof(SharedResource))
            .GetString("Common_Unknown", CultureInfo.CurrentUICulture)! : memberName;
        var resources = new ResourceManager(typeof(ExpensesResource));
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", string.Empty, RegexOptions.NonBacktracking));
        foreach (var key in new[] { "Expenses_IbanOnBehalf", "Expenses_IbanStoredForMember",
                     missingMember ? "Expenses_IbanEnterForMemberNoRemove" : "Expenses_IbanEnterForMember" })
            text.Should().Contain(string.Format(CultureInfo.CurrentCulture,
                resources.GetString(key, CultureInfo.CurrentUICulture)!, name));
        text.Should().NotContain(resources.GetString("Expenses_IbanPrivacy", CultureInfo.CurrentUICulture)!);
        html.Should().Contain(HtmlEncoder.Default.Encode(name)).And.NotContain("<script>");
    }

    [HumansFact]
    public async Task Review_RendersTheSubmitterInBothGridAndFailedBannerWithoutAddingSpaceBeforeTheComma()
    {
        var report = Report(ExpenseReportStatus.Approved);
        const string name = "Member <script>&";
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(report.SubmitterUserId, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = report.SubmitterUserId, BurnerName = name }, [], [], null));
        var html = await RenderAsync("Review", new ExpenseReviewViewModel
        {
            Reports = [report],
            BookingLabels = new Dictionary<Guid, string>(),
            FailedHoldedPushReportIds = new HashSet<Guid> { report.Id },
            Grants = new Dictionary<Guid, Humans.Preapprovals.Contracts.GrantOption>(),
            IsFinanceAdmin = true,
        }, users);
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", string.Empty, RegexOptions.NonBacktracking));

        Regex.Replace(text, @"\s+", " ", RegexOptions.NonBacktracking).Should().Contain(name + ",");
        html.Should().Contain(HtmlEncoder.Default.Encode(name), Exactly.Twice()).And.NotContain("<script>");
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Detail_SubmitterIdentityRespectsTheExistingVisibilityGate(bool canSeeNames)
    {
        var report = Report(ExpenseReportStatus.Submitted);
        const string name = "Submitter <b>&";
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(report.SubmitterUserId, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = report.SubmitterUserId, BurnerName = name }, [], [], null));
        var html = await RenderAsync("Detail", new ExpenseDetailViewModel
        {
            Report = report,
            BookingLabel = "Ops",
            CanSeeNames = canSeeNames,
            SubmitterLegalName = "Frozen legal name",
        }, users);

        html.Contains(HtmlEncoder.Default.Encode(name), StringComparison.Ordinal).Should().Be(canSeeNames);
        html.Contains("Frozen legal name", StringComparison.Ordinal).Should().Be(canSeeNames);
        if (!canSeeNames)
            await users.DidNotReceive().GetUserInfoAsync(report.SubmitterUserId, Arg.Any<CancellationToken>());
    }

    private static ExpenseReportDto Report(ExpenseReportStatus status, string? note = null) => new()
    {
        Id = Guid.NewGuid(),
        SubmitterUserId = Guid.NewGuid(),
        HoldedAccountNumber = 0,
        BudgetCategoryId = Guid.NewGuid(),
        BudgetYearId = Guid.NewGuid(),
        Status = status,
        PayeeName = "",
        PayeeIban = "",
        Total = 0,
        Lines = [],
        CreatedAt = Instant.FromUtc(2026, 7, 1, 0, 0),
        UpdatedAt = Instant.FromUtc(2026, 7, 1, 0, 0),
        Note = note,
    };

    private static async Task<string> RenderAsync(string page, object model, IUserService? users = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ExpenseReportViewTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(ExpensesResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly)
            .AddApplicationPart(typeof(Humans.AuditLog.ViewComponents.AuditLogViewComponent).Assembly)
            .AddViewLocalization();
        builder.Services.AddSingleton(users ?? Substitute.For<IUserService>());
        builder.Services.AddSingleton(Substitute.For<Humans.AuditLog.Contracts.IAuditViewerService>());
        builder.Services.AddHttpContextAccessor();
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, $"/Views/Expenses/{page}.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "Expenses";
        route.Values["action"] = page;
        var action = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary()) { Model = model };
        using var writer = new StringWriter();
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());

        await result.View!.RenderAsync(context);

        return writer.ToString();
    }
}
