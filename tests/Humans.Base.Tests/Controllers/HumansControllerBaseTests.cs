using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Base.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using NSubstitute;

namespace Humans.Base.Tests.Controllers;

public sealed class HumansControllerBaseTests
{
    [HumansFact]
    public void SetError_without_service_provider_sets_the_toast()
    {
        var controller = new TestController(Substitute.For<IUserServiceRead>());
        var http = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());

        controller.Error("Failed.");

        controller.TempData[TempDataKeys.ErrorMessage].Should().Be("Failed.");
    }

    private sealed class TestController(IUserServiceRead users) : HumansControllerBase(users)
    {
        public void Error(string message) => SetError(message);
    }
}
