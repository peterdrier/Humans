using AwesomeAssertions;
using Humans.Users.Contracts;
using Humans.Users.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Users.Tests.Controllers;

public class UnsubscribeControllerTests
{
    [HumansFact]
    public async Task Index_RequestAborted_CancelsTokenValidation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var service = Substitute.For<IUnsubscribeService>();
        service.ValidateTokenAsync("token", Arg.Any<CancellationToken>()).Returns(UnsubscribeTokenResult.Invalid());
        service.ValidateTokenAsync("token", aborted.Token)
            .Returns(Task.FromException<UnsubscribeTokenResult>(new OperationCanceledException(aborted.Token)));
        var controller = new UnsubscribeController(service, NullLogger<UnsubscribeController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token },
            },
        };

        var read = () => controller.Index("token");

        await read.Should().ThrowAsync<OperationCanceledException>();
    }
}
