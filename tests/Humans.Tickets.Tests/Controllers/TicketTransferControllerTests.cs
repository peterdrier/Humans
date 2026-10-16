using System.Globalization;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.EarlyEntry.Contracts;
using Humans.Tickets.Controllers;
using Humans.Tickets.Models;
using Humans.Tickets.Services;
using Humans.Tickets.Services.Dtos;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.Controllers;

public class TicketTransferControllerTests
{
    [HumansFact]
    public async Task Index_CancelsAtViewerResolution()
    {
        using var request = new CancellationTokenSource();
        var id = Guid.NewGuid();
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(id, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = id }, [], [], [], null, []));
        });
        var transfers = Substitute.For<ITicketTransferService>();
        transfers.GetMyAttendeesAsync(id, Arg.Any<CancellationToken>()).Returns([]);
        transfers.GetBySenderAsync(id, Arg.Any<CancellationToken>()).Returns([]);
        var controller = new TicketTransferController(transfers, Substitute.For<IEarlyEntryService>(),
            users, NullLogger<TicketTransferController>.Instance, Substitute.For<IStringLocalizer<TicketsResource>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestAborted = request.Token,
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "Test")),
                }
            },
        };
        (await controller.Index(request.Token)).Should().BeOfType<ViewResult>();
        await request.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.Index(request.Token));
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MemberRefusalsAreLocalized_DependencyFailuresPropagate(bool cancel, bool unknown)
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es");
        try
        {
            using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
            var localizer = services.GetRequiredService<IStringLocalizer<TicketsResource>>();
            var userId = Guid.NewGuid();
            var user = new User { Id = userId };
            var users = Substitute.For<IUserServiceRead>();
            users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
                .Returns(UserInfo.Create(user, [], [], [], null, []));
            var transfers = Substitute.For<ITicketTransferService>();
            transfers.GetMyAttendeesAsync(userId, Arg.Any<CancellationToken>()).Returns([]);
            transfers.GetBySenderAsync(userId, Arg.Any<CancellationToken>()).Returns([]);
            var key = unknown ? "untranslated provider failure" : cancel
                ? "Tickets_TicketTransfer_NotFound" : "TicketTransfer_AlreadyPending";
            transfers.CreateRequestAsync(Arg.Any<TicketTransferRequestDto>(), userId, Arg.Any<CancellationToken>())
                .Returns(_ => unknown
                    ? Task.FromException<TicketTransferMutationResult>(new InvalidOperationException(key))
                    : Task.FromResult(TicketTransferMutationResult.Refused(key)));
            transfers.CancelAsync(Arg.Any<Guid>(), userId, Arg.Any<CancellationToken>())
                .Returns(_ => unknown
                    ? Task.FromException<TicketTransferMutationResult>(new InvalidOperationException(key))
                    : Task.FromResult(TicketTransferMutationResult.Refused(key)));
            var http = new DefaultHttpContext
            {
                RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
            };
            var controller = new TicketTransferController(transfers, Substitute.For<IEarlyEntryService>(),
                users, NullLogger<TicketTransferController>.Instance, localizer)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
                TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
                Url = Substitute.For<IUrlHelper>()
            };

            if (unknown)
            {
                Func<Task> action = cancel
                    ? () => controller.Cancel(Guid.NewGuid(), TestContext.Current.CancellationToken)
                    : () => controller.Submit(Guid.NewGuid(), Guid.NewGuid(), "Reason", TestContext.Current.CancellationToken);
                await action.Should().ThrowAsync<InvalidOperationException>().WithMessage(key);
                controller.TempData.ContainsKey(TempDataKeys.ErrorMessage).Should().BeFalse();
                return;
            }

            if (cancel)
            {
                await controller.Cancel(Guid.NewGuid(), TestContext.Current.CancellationToken);
                controller.TempData[TempDataKeys.ErrorMessage].Should().Be("Transferencia no encontrada.");
            }
            else
            {
                var result = await controller.Submit(Guid.NewGuid(), Guid.NewGuid(), "Reason", TestContext.Current.CancellationToken);
                var view = result.Should().BeOfType<ViewResult>().Subject;
                var model = view.Model.Should().BeOfType<TicketTransferWizardViewModel>().Subject;
                model.Error.Should().Be(localizer["TicketTransfer_AlreadyPending"].Value);
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }
}
