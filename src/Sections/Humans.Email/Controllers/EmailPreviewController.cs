using Humans.Base.Controllers;
using Humans.Email.Services;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Humans.Email.Controllers;

/// <summary>
/// Open to any signed-in member, unlike the admin-only <see cref="EmailController"/>: every compose form uses its
/// Preview and Send to me buttons.
/// </summary>
[Authorize]
[Route("Email")]
internal sealed class EmailPreviewController(
    IUserService userService,
    IEmailPreviewManagement emailPreviews,
    ComposerSelfSendService selfSend) : ApiControllerBase(userService)
{
    public const string SelfSendRateLimitPolicy = "EmailComposerSelfSend";
    public const int SelfSendPermitsPerMinute = 5;

    [HttpPost("PreviewMarkdown", Name = "EmailPreviewMarkdown")]
    [ValidateAntiForgeryToken]
    public IActionResult PreviewMarkdown([FromForm] string? subject, [FromForm] string? body, [FromForm] string? category)
    {
        MessageCategory? parsedCategory = !string.IsNullOrWhiteSpace(category)
            && Enum.TryParse<MessageCategory>(category, out var value)
                ? value
                : null;

        var preview = emailPreviews.RenderMarkdown(subject ?? string.Empty, body, parsedCategory);
        return new JsonResult(new { subject = preview.Subject, html = preview.HtmlBody });
    }

    /// <summary>Only ever to the signed-in member's own address. Rate-limited per member.</summary>
    [HttpPost("SendMarkdownToSelf", Name = "EmailSendMarkdownToSelf")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(SelfSendRateLimitPolicy)]
    public async Task<IActionResult> SendMarkdownToSelf(
        [FromForm] string? subject, [FromForm] string? body, CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var sentTo = await selfSend.SendToSelfAsync(userId.Value, subject, body, ct);
        if (sentTo is null) return UnprocessableEntity();

        return new JsonResult(new { email = sentTo });
    }
}
