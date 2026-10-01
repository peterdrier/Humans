using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.ViewComponents;

public class TicketStubViewTests
{
    [HumansTheory]
    [InlineData("Elsewhere 2027")]
    [InlineData(null)]
    public async Task EventLabel_UsesConfiguredNameOrLocalizedFallback(string? eventName)
    {
        var settings = Substitute.For<ISettingsService>();
        var activeEvent = eventName is null ? null : new EventSettingsInfo(
            Guid.NewGuid(), eventName, 2027, "Europe/Madrid", new LocalDate(2027, 7, 1),
            -14, 7, 10, -14, -7, -7, -2, new Dictionary<int, int>(), null, null);
        settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(activeEvent);
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<TicketsResource>>();
        var item = new RazorCompiledItemLoader().LoadItems(typeof(TicketStubViewComponent).Assembly)
            .Single(i => string.Equals(i.Identifier, "/Views/Shared/Components/TicketStub/Default.cshtml", StringComparison.Ordinal));
        var page = (RazorPage<TicketStubInfo>)Activator.CreateInstance(item.Type)!;
        item.Type.GetProperty("SettingsService")!.SetValue(page, settings);
        item.Type.GetProperty("Localizer")!.SetValue(page, localizer);
        page.HtmlEncoder = HtmlEncoder.Default;
        using var writer = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        page.ViewContext = new ViewContext
        {
            HttpContext = new DefaultHttpContext { RequestAborted = cancellation.Token },
            Writer = writer,
            ViewData = new ViewDataDictionary<TicketStubInfo>(new EmptyModelMetadataProvider(), new ModelStateDictionary())
            {
                Model = new TicketStubInfo("Ada", "ada@example.com", TicketAttendeeStatus.Valid, false, null, null),
            },
        };

        page.ViewData = (ViewDataDictionary<TicketStubInfo>)page.ViewContext.ViewData;

        await page.ExecuteAsync();

        var expectedName = eventName ?? localizer["Tickets_TicketHoldings_Title"].Value;
        writer.ToString().Should().Contain(HtmlEncoder.Default.Encode(
            localizer["Tickets_TicketStub_EventLabel", expectedName].Value));
        writer.ToString().Should().NotContain("Elsewhere 2026");
        await settings.Received(1).GetActiveEventSettingsAsync(cancellation.Token);
    }
}
