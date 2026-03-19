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
public class FeedbackController : Controller
{
    private readonly IFeedbackService _feedbackService;
    private readonly UserManager<User> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<FeedbackController> _logger;

    public FeedbackController(
        IFeedbackService feedbackService,
        UserManager<User> userManager,
        IStringLocalizer<SharedResource> localizer,
        ILogger<FeedbackController> logger)
    {
        _feedbackService = feedbackService;
        _userManager = userManager;
        _localizer = localizer;
        _logger = logger;
    }

    [HttpPost("Submit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(SubmitFeedbackViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return NotFound();

        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = _localizer["Feedback_Error"].Value;
            return LocalRedirect(Url.IsLocalUrl(model.PageUrl) ? model.PageUrl : "/");
        }

        try
        {
            await _feedbackService.SubmitFeedbackAsync(
                user.Id, model.Category, model.Description,
                model.PageUrl, model.UserAgent, model.Screenshot);

            TempData["SuccessMessage"] = _localizer["Feedback_Submitted"].Value;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Feedback submission failed for user {UserId}", user.Id);
            TempData["ErrorMessage"] = _localizer["Feedback_Error"].Value;
        }

        return LocalRedirect(Url.IsLocalUrl(model.PageUrl) ? model.PageUrl : "/");
    }
}
