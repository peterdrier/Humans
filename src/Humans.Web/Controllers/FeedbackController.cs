using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Feedback")]
public class FeedbackController : HumansControllerBase
{
    private readonly IFeedbackService _feedbackService;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<FeedbackController> _logger;

    public FeedbackController(
        IFeedbackService feedbackService,
        UserManager<User> userManager,
        IStringLocalizer<SharedResource> localizer,
        ILogger<FeedbackController> logger)
        : base(userManager)
    {
        _feedbackService = feedbackService;
        _localizer = localizer;
        _logger = logger;
    }

    [HttpPost("Submit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(SubmitFeedbackViewModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing != null)
        {
            return userMissing;
        }

        if (!ModelState.IsValid)
        {
            SetError(_localizer["Feedback_Error"].Value);
            return LocalRedirect(Url.IsLocalUrl(model.PageUrl) ? model.PageUrl : "/");
        }

        try
        {
            await _feedbackService.SubmitFeedbackAsync(
                user.Id, model.Category, model.Description,
                model.PageUrl, model.UserAgent, model.Screenshot);

            SetSuccess(_localizer["Feedback_Submitted"].Value);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Feedback submission failed for user {UserId}", user.Id);
            SetError(_localizer["Feedback_Error"].Value);
        }

        return LocalRedirect(Url.IsLocalUrl(model.PageUrl) ? model.PageUrl : "/");
    }
}
